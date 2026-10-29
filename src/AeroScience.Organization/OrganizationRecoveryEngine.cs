using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Npgsql;
namespace AeroScience.Organization;

// Infrastructure orchestration of PostgreSQL's native consistent dump and restic's authenticated encrypted repository.
// Only expressly received organization data is present in this database. No desktop/archive access exists here.
public sealed partial class OrganizationRecoveryEngine(OrganizationDatabase db,OrganizationSettings settings,ServerOperationsOptions options)
{
    internal static string Hash(ReadOnlySpan<byte> value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    static JsonSerializerOptions Json=>WireJson.Options;
    string[] ResticArguments(params string[] args)=>["--no-cache","--repo",options.Repository!,"--password-file",options.PasswordFile!,..options.RepositoryCaFile is {} ca?new[]{"--cacert",ca}:Array.Empty<string>(),..(options.Repository?.StartsWith("s3:",StringComparison.Ordinal)==true?new[]{"-o","s3.bucket-lookup="+options.S3BucketLookup}:Array.Empty<string>()),..args];
    Dictionary<string,string> StorageEnvironment()
    {
        var result=options.StorageCredentials();
        if(options.InheritStorageCredentials&&options.RepositoryCredentialsFile is null)foreach(var name in RecoveryRepositoryTarget.CredentialNames)if(Environment.GetEnvironmentVariable(name) is {} value)result[name]=value;
        return result;
    }
    Task<string> Restic(CancellationToken ct,params string[] args)=>Run(options.ResticExecutable,ResticArguments(args),ct,StorageEnvironment(),true);
    static FileStream CreatePrivate(string path){var o=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,Share=FileShare.None};if(!OperatingSystem.IsWindows())o.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;return new(path,o);}
    static void MakePrivate(string path){Directory.CreateDirectory(path);for(var d=new DirectoryInfo(path);d is not null;d=d.Parent)if(d.LinkTarget is not null)throw new InvalidOperationException("Private recovery work cannot follow symbolic links.");if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}
    static Process Start(string executable,IEnumerable<string> args,IReadOnlyDictionary<string,string>? environment=null,bool isolateStorage=false)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,CreateNoWindow=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        if(isolateStorage)foreach(var key in start.Environment.Keys.Where(k=>k.StartsWith("AWS_",StringComparison.Ordinal)||k.StartsWith("RESTIC_",StringComparison.Ordinal)).ToArray())start.Environment.Remove(key);
        if(environment is not null)foreach(var entry in environment)start.Environment[entry.Key]=entry.Value;
        // The pinned client libraries are isolated to PostgreSQL subprocesses, not the application runtime.
        var clientLibraries=Directory.Exists("/operations/lib")?"/operations/lib":Environment.GetEnvironmentVariable("AEROSCIENCE_RECOVERY_PG_LIBRARY_PATH");if(executable.Contains("pg_",StringComparison.Ordinal)&&clientLibraries is not null)start.Environment["LD_LIBRARY_PATH"]=clientLibraries;
        var process=Process.Start(start)??throw new InvalidOperationException("The protected recovery tool could not start.");process.StandardInput.Close();return process;
    }
    static async Task<string> BoundedText(StreamReader reader,int maximum,CancellationToken ct)
    {
        var b=new StringBuilder();var buffer=new char[4096];int n;while((n=await reader.ReadAsync(buffer,ct))>0){if(b.Length+n>maximum)throw new InvalidOperationException("Recovery tool output exceeded its bounded protocol.");b.Append(buffer,0,n);}return b.ToString();
    }
    static async Task<string> Run(string executable,string[] args,CancellationToken ct,IReadOnlyDictionary<string,string>? environment=null,bool isolateStorage=false)
    {
        using var process=Start(executable,args,environment,isolateStorage);try{
            var stdout=BoundedText(process.StandardOutput,2097152,ct);var stderr=BoundedText(process.StandardError,262144,ct);await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(ct));
            if(process.ExitCode!=0){var failure=new InvalidOperationException("The protected recovery tool refused this operation. No raw command, credential or evidence content is disclosed.");failure.Data["RecoveryToolExitCode"]=process.ExitCode;throw failure;}return await stdout;
        }finally{if(!process.HasExited)process.Kill(true);}
    }
    internal static Dictionary<string,string> PgEnvironment(string connection,string? database=null)
    {
        var pg=new NpgsqlConnectionStringBuilder(connection);return new(){["PGHOST"]=pg.Host??"",["PGPORT"]=pg.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),["PGDATABASE"]=database??pg.Database??"",["PGUSER"]=pg.Username??"",["PGPASSWORD"]=pg.Password??"",["PGSSLMODE"]=pg.SslMode==SslMode.VerifyFull?"verify-full":"disable",["PGSSLROOTCERT"]=pg.RootCertificate??"",["PGGSSENCMODE"]="disable",["PGCONNECT_TIMEOUT"]="10",["PGAPPNAME"]="AeroScience.Organization.Recovery"};
    }
    async Task InitializeRepository(CancellationToken ct)
    {
        try{_=await Restic(ct,"cat","config");}catch(InvalidOperationException ex) when(ex.Data["RecoveryToolExitCode"] is 10){_=await Restic(ct,"init");}
    }
    internal static async Task<long> CopyBounded(Stream source,Stream target,long maximum,IncrementalHash? hash,CancellationToken ct)
    {
        var buffer=new byte[65536];long count=0;int n;while((n=await source.ReadAsync(buffer,ct))>0){count=checked(count+n);if(count>maximum)throw new InvalidOperationException("The approved recovery package exceeded its configured bound; no partial restore is accepted.");hash?.AppendData(buffer,0,n);await target.WriteAsync(buffer.AsMemory(0,n),ct);}return count;
    }
    internal static async Task<(string Evidence,long Packages,string Audit,long Events)> Digests(NpgsqlConnection connection,NpgsqlTransaction? tx,CancellationToken ct)
    {
        using var evidence=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long packages=0;
        await using(var command=new NpgsqlCommand("SELECT \"Id\",\"ReceiptId\",\"PayloadSha256\",\"PayloadJson\" FROM organization.\"Submissions\" ORDER BY \"Id\"",connection,tx)){
            await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){
                var expected=reader.GetString(2);if(Hash(Encoding.UTF8.GetBytes(reader.GetString(3)))!=expected)throw new InvalidOperationException("Original received evidence failed its retained payload hash. A backup cannot certify altered evidence.");
                evidence.AppendData(Encoding.UTF8.GetBytes(reader.GetGuid(0)+"\0"+reader.GetGuid(1)+"\0"+expected+"\n"));packages++;
            }
        }
        using var audit=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long events=0;
        await using(var command=new NpgsqlCommand("SELECT \"Id\",\"TimestampUtc\",\"ActorId\",\"ActorName\",\"Action\",\"ResourceId\",\"Revision\" FROM organization.\"Audit\" ORDER BY \"Id\"",connection,tx)){
            await using var reader=await command.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct)){var row=new object[7];reader.GetValues(row);audit.AppendData(JsonSerializer.SerializeToUtf8Bytes(row,Json));events++;}
        }
        return(Convert.ToHexStringLower(evidence.GetHashAndReset()),packages,Convert.ToHexStringLower(audit.GetHashAndReset()),events);
    }
    public async Task<ServerBackupRecord> Backup(Guid id,CancellationToken ct)
    {
        if(!options.Enabled)throw new InvalidOperationException("Protected recovery is not enabled.");
        MakePrivate(options.WorkDirectory);var directory=Path.Combine(options.WorkDirectory,"backup-"+id.ToString("N"));if(Directory.Exists(directory))throw new InvalidOperationException("A previous private work directory requires operator recovery; it is never silently reused.");MakePrivate(directory);
        try{
            var bundle=Path.Combine(directory,"organization.aeroscience");ServerBackupManifest manifest;
            await using var connection=new NpgsqlConnection(settings.ConnectionString);await connection.OpenAsync(ct);await using var tx=await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
            await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,tx))await readOnly.ExecuteNonQueryAsync(ct);
            string snapshot;await using(var export=new NpgsqlCommand("SELECT pg_export_snapshot()",connection,tx))snapshot=(string)(await export.ExecuteScalarAsync(ct))!;
            var org=await db.Organizations.AsNoTracking().SingleAsync(ct);var digests=await Digests(connection,tx,ct);using var certificate=settings.LoadKeyCertificate()??throw new InvalidOperationException("Original protected identity is missing.");
            await using(var file=CreatePrivate(bundle)){
                using var archive=new ZipArchive(file,ZipArchiveMode.Create,true);var entry=archive.CreateEntry("database.dump",CompressionLevel.NoCompression);
                using var process=Start(options.PgDumpExecutable,["--format=custom","--no-owner","--no-acl","--snapshot="+snapshot],PgEnvironment(settings.ConnectionString));
                var errors=BoundedText(process.StandardError,262144,ct);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long size;
                try{await using(var target=entry.Open())size=await CopyBounded(process.StandardOutput.BaseStream,target,options.MaximumBundleBytes,hash,ct);await process.WaitForExitAsync(ct);await errors;if(process.ExitCode!=0)throw new InvalidOperationException("The consistent database snapshot could not complete.");}
                finally{if(!process.HasExited)process.Kill(true);}
                var source=OrganizationDeployment.ApplicationVersion.Split('+').ElementAtOrDefault(1)??"unpublished-development";
                manifest=new(1,id,org.PublicId,settings.Origin.GetLeftPart(UriPartial.Authority),OrganizationDeployment.ApplicationVersion,source,db.Database.GetMigrations().ToArray(),Hash(certificate.RawData),Convert.ToHexStringLower(hash.GetHashAndReset()),size,digests.Evidence,digests.Packages,digests.Audit,digests.Events,DateTimeOffset.UtcNow);
                async Task Add(string name,byte[] data){var e=archive.CreateEntry(name,CompressionLevel.NoCompression);await using var output=e.Open();await output.WriteAsync(data,ct);}
                await Add("identity.pfx",await File.ReadAllBytesAsync(OrganizationSettings.PrivateFile(settings.KeyCertificateFile!,1,1048576).FullName,ct));
                await Add("identity-password",Encoding.UTF8.GetBytes(settings.KeyCertificatePassword!));await Add("manifest.json",JsonSerializer.SerializeToUtf8Bytes(manifest,Json));
            }
            await tx.CommitAsync(ct);var bytes=new FileInfo(bundle).Length;if(bytes>options.MaximumBundleBytes+2097152)throw new InvalidOperationException("Recovery package exceeds the configured processing limit.");
            string bundleHash;await using(var input=File.OpenRead(bundle))bundleHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct));
            await InitializeRepository(ct);
            // Fixed host and filename keep repository records independent of source filesystem paths.
            var outputJson=await Restic(ct,"backup",bundle,"--json","--host","aeroscience-"+org.PublicId,"--tag","operation-"+id);
            string? snapshotId=null;foreach(var line in outputJson.Split('\n',StringSplitOptions.RemoveEmptyEntries)){using var doc=JsonDocument.Parse(line);if(doc.RootElement.TryGetProperty("snapshot_id",out var value))snapshotId=value.GetString();}
            if(snapshotId is not {Length:64}||snapshotId.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidOperationException("The encrypted repository did not return a complete authoritative snapshot identity.");
            // restic records a private work path internally; only the exact signed-in owner can request recovery.
            return new(){Id=id,OrganizationId=org.PublicId,RepositorySha256=Hash(Encoding.UTF8.GetBytes(options.Repository!)),SnapshotId=snapshotId,BundleSha256=bundleHash,Bytes=bytes,ManifestJson=JsonSerializer.Serialize(manifest,Json),CreatedUtc=manifest.CreatedUtc};
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    async Task Download(ServerBackupRecord backup,string target,CancellationToken ct)
    {
        // The source path is part of restic's immutable snapshot tree, never a caller-supplied filesystem path.
        var snapshotJson=await Restic(ct,"snapshots",backup.SnapshotId,"--json");using var doc=JsonDocument.Parse(snapshotJson);var snapshots=doc.RootElement;
        if(snapshots.ValueKind!=JsonValueKind.Array||snapshots.GetArrayLength()!=1||snapshots[0].GetProperty("id").GetString()!=backup.SnapshotId)throw new InvalidOperationException("The protected snapshot identity is unavailable.");
        var paths=snapshots[0].GetProperty("paths");if(paths.GetArrayLength()!=1)throw new InvalidOperationException("Unsupported recovery snapshot layout.");var path=paths[0].GetString()!;
        using var process=Start(options.ResticExecutable,ResticArguments("dump",backup.SnapshotId,path),StorageEnvironment(),true);var errors=BoundedText(process.StandardError,262144,ct);
        try{await using var file=CreatePrivate(target);using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);await CopyBounded(process.StandardOutput.BaseStream,file,options.MaximumBundleBytes+2097152,hash,ct);await process.WaitForExitAsync(ct);await errors;if(process.ExitCode!=0||(backup.BundleSha256.Length>0&&Convert.ToHexStringLower(hash.GetHashAndReset())!=backup.BundleSha256))throw new InvalidOperationException("Protected snapshot authentication or exact package hash failed. No database was restored.");}
        finally{if(!process.HasExited)process.Kill(true);}
    }
    public async Task<ServerBackupRecord> Discover(string snapshotId,CancellationToken ct)
    {
        if(snapshotId.Length!=64||snapshotId.Any(c=>!char.IsAsciiHexDigit(c)))throw new ArgumentException("Choose an exact protected repository snapshot identity.");
        MakePrivate(options.WorkDirectory);var directory=Path.Combine(options.WorkDirectory,"restore-"+Guid.NewGuid().ToString("N"));MakePrivate(directory);
        try{
            var path=Path.Combine(directory,"organization.aeroscience");var backup=new ServerBackupRecord{SnapshotId=snapshotId};await Download(backup,path,ct);
            await using var file=File.OpenRead(path);using var archive=new ZipArchive(file,ZipArchiveMode.Read);var entry=archive.GetEntry("manifest.json")??throw new InvalidOperationException("Recovery manifest unavailable.");if(entry.Length is <1 or >65536)throw new InvalidOperationException("Recovery manifest is outside its safe bound.");
            await using var stream=entry.Open();using var buffer=new MemoryStream();await CopyBounded(stream,buffer,65536,null,ct);var manifest=JsonSerializer.Deserialize<ServerBackupManifest>(buffer.ToArray(),Json)??throw new InvalidOperationException("Recovery manifest unavailable.");
            if(manifest.Version!=1||manifest.BackupId==Guid.Empty||manifest.OrganizationId==Guid.Empty)throw new InvalidOperationException("Unsupported recovery identity.");
            file.Position=0;backup.Id=manifest.BackupId;backup.OrganizationId=manifest.OrganizationId;backup.ManifestJson=JsonSerializer.Serialize(manifest,Json);backup.BundleSha256=Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct));backup.Bytes=file.Length;backup.CreatedUtc=manifest.CreatedUtc;return backup;
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    public async Task<ServerRestoreVerification> VerifyRestore(ServerBackupRecord backup,CancellationToken ct,bool allowCompatibleSchemaPrefix=false)
    {
        var recovery=options.RecoveryConnectionString??throw new InvalidOperationException("An isolated recovery database provisioner is not configured. No primary database was changed.");
        var pg=new NpgsqlConnectionStringBuilder(recovery);if(!settings.LocalDevelopment&&pg.SslMode!=SslMode.VerifyFull)throw new InvalidOperationException("Recovery provisioning also requires hostname-verified database TLS.");
        var directory=Path.Combine(options.WorkDirectory,"restore-"+Guid.NewGuid().ToString("N"));MakePrivate(directory);var temporaryDatabase="aeroscience_recovery_"+Guid.NewGuid().ToString("N");
        try{
            var package=Path.Combine(directory,"organization.aeroscience");await Download(backup,package,ct);
            await using(var input=File.OpenRead(package)){
                using var archive=new ZipArchive(input,ZipArchiveMode.Read);var expected=new[]{"database.dump","identity.pfx","identity-password","manifest.json"};if(archive.Entries.Count!=4||archive.Entries.Select(e=>e.FullName).Distinct().Count()!=4||archive.Entries.Any(e=>!expected.Contains(e.FullName,StringComparer.Ordinal)||e.Length<1||e.Length>options.MaximumBundleBytes+2097152))throw new InvalidOperationException("Unsupported or oversized recovery package; no path extraction is permitted.");
                foreach(var name in expected){var entry=archive.GetEntry(name)!;await using var source=entry.Open();await using var target=CreatePrivate(Path.Combine(directory,name));await CopyBounded(source,target,name=="database.dump"?options.MaximumBundleBytes:name=="identity.pfx"?1048576:65536,null,ct);}
            }
            var manifest=JsonSerializer.Deserialize<ServerBackupManifest>(await File.ReadAllBytesAsync(Path.Combine(directory,"manifest.json"),ct),Json)??throw new InvalidOperationException("Recovery manifest unavailable.");
            var known=db.Database.GetMigrations().ToArray();var compatible=manifest.Migrations.Length>0&&manifest.Migrations.SequenceEqual(allowCompatibleSchemaPrefix?known.Take(manifest.Migrations.Length):known);
            if(manifest.Version!=1||manifest.BackupId!=backup.Id||manifest.OrganizationId!=backup.OrganizationId||manifest.PublicOrigin!=settings.Origin.GetLeftPart(UriPartial.Authority)||JsonSerializer.Serialize(manifest,Json)!=backup.ManifestJson||!compatible)throw new InvalidOperationException("Recovery source/schema identity does not match. Use its exact compatible pinned distribution; no automatic downgrade is attempted.");
            var password=await File.ReadAllTextAsync(Path.Combine(directory,"identity-password"),ct);using var certificate=X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(directory,"identity.pfx"),password,X509KeyStorageFlags.EphemeralKeySet);
            if(!certificate.HasPrivateKey||Hash(certificate.RawData)!=manifest.CertificateSha256)throw new InvalidOperationException("Paired recovery identity material is missing or incompatible. No replacement identity is created.");
            string dumpHash;await using(var stream=File.OpenRead(Path.Combine(directory,"database.dump")))dumpHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct));if(dumpHash!=manifest.DatabaseSha256)throw new InvalidOperationException("The original database snapshot hash failed.");
            await using var provisioner=new NpgsqlConnection(recovery);await provisioner.OpenAsync(ct);
            await using(var create=new NpgsqlCommand("CREATE DATABASE \""+temporaryDatabase+"\" ENCODING 'UTF8' TEMPLATE template0",provisioner))await create.ExecuteNonQueryAsync(ct);
            _=await Run(options.PgRestoreExecutable,["--no-owner","--no-acl","--exit-on-error","--dbname="+temporaryDatabase,Path.Combine(directory,"database.dump")],ct,PgEnvironment(recovery,temporaryDatabase));
            pg.Database=temporaryDatabase;await using var restored=new NpgsqlConnection(pg.ConnectionString);await restored.OpenAsync(ct);var digests=await Digests(restored,null,ct);
            Guid restoredOrganization;await using(var original=new NpgsqlCommand("SELECT \"PublicId\" FROM organization.\"Organizations\"",restored))restoredOrganization=(Guid)(await original.ExecuteScalarAsync(ct))!;
            if(restoredOrganization!=manifest.OrganizationId||digests.Evidence!=manifest.EvidenceDigest||digests.Packages!=manifest.ReceivedPackages||digests.Audit!=manifest.AuditDigest||digests.Events!=manifest.AuditEvents)throw new InvalidOperationException("Restored organization/evidence/audit identity did not match the consistent snapshot.");
            // Verify encrypted Identity secrets using the original key ring and certificate, not merely SQL counts.
            var services=new ServiceCollection();services.AddLogging();services.AddDbContext<OrganizationDatabase>(o=>o.UseNpgsql(pg.ConnectionString));
            services.AddDataProtection().SetApplicationName("AeroScience.Organization.0.7").PersistKeysToDbContext<OrganizationDatabase>().ProtectKeysWithCertificate(certificate);
            services.AddIdentityCore<OrganizationUser>().AddRoles<Microsoft.AspNetCore.Identity.IdentityRole<Guid>>().AddEntityFrameworkStores<OrganizationDatabase>().AddDefaultTokenProviders();
            services.AddScoped<Microsoft.AspNetCore.Identity.IUserStore<OrganizationUser>,ProtectedOrganizationUserStore>();
            await using(var validation=services.BuildServiceProvider()){
                using var scope=validation.CreateScope();var context=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var manager=scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<OrganizationUser>>();
                if(!(await context.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(manifest.Migrations))throw new InvalidOperationException("The restored schema does not match its original manifest.");
                for(var offset=0;;offset+=100){
                    var administrators=await context.Users.Where(u=>u.TwoFactorEnabled).OrderBy(u=>u.Id).Skip(offset).Take(100).ToArrayAsync(ct);
                    foreach(var admin in administrators)if(await manager.GetAuthenticatorKeyAsync(admin) is null)throw new InvalidOperationException("Restored MFA could not be decrypted with the paired identity.");
                    if(administrators.Length<100)break;
                }
            }
            return new(1,backup.Id,manifest.OrganizationId,dumpHash,Hash(certificate.RawData),digests.Evidence,digests.Packages,digests.Audit,digests.Events,manifest.Migrations,DateTimeOffset.UtcNow,"Actual isolated PostgreSQL restore and original identity-secret decryption. Primary data were not modified. Provider/account loss and novice recovery still require external acceptance.");
        }finally{
            // Only generated disposable databases may be removed, never the original organization database.
            try{await using var admin=new NpgsqlConnection(recovery);await admin.OpenAsync(CancellationToken.None);await using var drop=new NpgsqlCommand("DROP DATABASE IF EXISTS \""+temporaryDatabase+"\" WITH (FORCE)",admin);await drop.ExecuteNonQueryAsync(CancellationToken.None);}catch{ }
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }
    internal static void CleanInterruptedWork(ServerOperationsOptions options)
    {
        if(!Directory.Exists(options.WorkDirectory))return;
        for(var d=new DirectoryInfo(options.WorkDirectory);d is not null;d=d.Parent)if(d.LinkTarget is not null)throw new InvalidOperationException("Unsafe interrupted recovery work directory.");
        foreach(var directory in Directory.EnumerateDirectories(options.WorkDirectory).Take(100)){
            var info=new DirectoryInfo(directory);if(info.LinkTarget is not null)throw new InvalidOperationException("Unsafe interrupted recovery directory.");
            if(System.Text.RegularExpressions.Regex.IsMatch(info.Name,@"^(?:backup|restore)-[a-f0-9]{32}$")&&info.LastWriteTimeUtc<DateTime.UtcNow.AddMinutes(-20))Directory.Delete(directory,true);
        }
    }
    public static async Task<bool> ProcessOne(IServiceProvider services,CancellationToken ct)
    {
        using var scope=services.CreateScope();var operations=scope.ServiceProvider.GetRequiredService<OrganizationOperations>();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var engine=scope.ServiceProvider.GetRequiredService<OrganizationRecoveryEngine>();var options=scope.ServiceProvider.GetRequiredService<ServerOperationsOptions>();
        if(!options.Enabled)return false;CleanInterruptedWork(options);await operations.Schedule(ct);var job=await operations.Take(ct);if(job is null)return false;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(15));var token=deadline.Token;
        try{
            ServerBackupRecord backup;
            if(job.Kind is ServerOperationKind.Backup or ServerOperationKind.PrepareUpdate){backup=await engine.Backup(job.Id,token);db.ServerBackups.Add(backup);job.BackupId=backup.Id;await db.SaveChangesAsync(token);}else backup=await db.ServerBackups.SingleAsync(b=>b.Id==job.BackupId,token);
            if(job.Kind is ServerOperationKind.VerifyRestore or ServerOperationKind.PrepareUpdate||options.Independent is not null){var verified=await engine.VerifyRestore(backup,token);backup.VerifiedRestoreUtc=verified.VerifiedUtc;backup.VerificationJson=JsonSerializer.Serialize(verified,Json);}
            if(options.Independent is not null){await engine.CopyIndependent(backup,token);await db.SaveChangesAsync(token);var policy=await db.ServerOperationsPolicies.AsNoTracking().SingleAsync(token);await engine.EnforceRetention(policy,token);}
            job.State=ServerOperationState.Completed;job.Message=job.Kind switch{ServerOperationKind.Backup=>options.Independent is null?"Encrypted backup received by the configured repository. Verify a restore before relying on it.":"Encrypted backup and separately keyed copy both restored successfully. Approved retention was checked. Independent account control still requires operator verification.",ServerOperationKind.VerifyRestore=>"Paired restore verified in an isolated database; original evidence and audit digests match. Primary records were not changed.",_=>"Pinned update preflight has a verified paired recovery checkpoint. Provider rollout is not implemented/authorized by this receipt; no migration or image was applied."};
        }catch(Exception ex) when(ex is not OperationCanceledException||!ct.IsCancellationRequested){job.State=ServerOperationState.Failed;job.Message="Recovery did not complete. Existing primary records and earlier checkpoints remain unchanged. Check the protected destination, compatible keys/version and storage before retrying.";}
        job.Revision++;job.FinishedUtc=DateTimeOffset.UtcNow;scope.ServiceProvider.GetRequiredService<OrganizationManagement>().Audit(job.ActorId,job.ActorName,"server-operation-"+job.State.ToString().ToLowerInvariant(),job.Id,job.Revision);await db.SaveChangesAsync(ct);return true;
    }
}
public sealed class OrganizationOperationsWorker(IServiceProvider services,ServerOperationsOptions options):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Enabled)return;
        while(!stoppingToken.IsCancellationRequested){try{await OrganizationRecoveryEngine.ProcessOne(services,stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}catch{ /* State remains visible/recoverable; no credential or raw tool output is logged. */ }await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);}
    }
}
