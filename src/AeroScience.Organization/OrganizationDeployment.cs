using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroScience.Organization;

// Configuration/ownership metadata only. Received evidence remains in P17's PostgreSQL tables.
public sealed class DeploymentStateRecord
{
    public int Id {get;set;}=1;
    public string CertificateSha256 {get;set;}="";
    public string BootstrapSha256 {get;set;}="";
    public DateTimeOffset BootstrapIssuedUtc {get;set;}
    public DateTimeOffset BootstrapExpiresUtc {get;set;}
    public DateTimeOffset? BootstrapConsumedUtc {get;set;}
}
public static class OrganizationDeployment
{
    public static string ApplicationVersion=>typeof(OrganizationProgram).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"0.7.0";
    public static string Digest(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static void PrepareCertificate(OrganizationSettings settings)
    {
        if(!settings.ManagedDeployment)return;
        if(OperatingSystem.IsWindows())throw new InvalidOperationException("The managed container profile requires Linux. Use the existing operator-protected certificate configuration on Windows.");
        var directory=new DirectoryInfo(settings.StateDirectory!);
        if(!directory.Exists)throw new InvalidOperationException("Mount the persistent private configuration volume before deployment.");
        for(var entry=directory;entry is not null;entry=entry.Parent)
            if(entry.LinkTarget is not null)throw new InvalidOperationException("The persistent configuration directory cannot use symbolic links.");
        if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(directory.FullName)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0)
            throw new InvalidOperationException("The persistent configuration directory must be private to the service account.");
        var lockPath=Path.Combine(directory.FullName,".initialization-lock");
        if(new FileInfo(lockPath).LinkTarget is not null)throw new InvalidOperationException("Unsafe configuration lock.");
        using var gate=new FileStream(lockPath,PrivateOptions(FileMode.OpenOrCreate));
        if(File.Exists(settings.KeyCertificateFile)){using var existing=settings.LoadKeyCertificate();return;}
        // Never replace a missing key for an initialized database: that would orphan cookies and grants.
        using(var connection=new NpgsqlConnection(settings.ConnectionString)){
            connection.Open();using var command=new NpgsqlCommand("SELECT to_regclass('organization.\"DataProtectionKeys\"') IS NOT NULL OR to_regclass('organization.\"Organizations\"') IS NOT NULL",connection);
            if((bool)command.ExecuteScalar()!)throw new InvalidOperationException("Persistent key material is missing for an existing database. Restore protected configuration; do not create replacement keys.");
        }
        using var key=RSA.Create(3072);
        var request=new CertificateRequest("CN=AeroScience Organization Data Protection",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
        var bytes=certificate.Export(X509ContentType.Pfx,settings.KeyCertificatePassword);
        try{
            using var file=new FileStream(settings.KeyCertificateFile!,PrivateOptions(FileMode.CreateNew));
            file.Write(bytes);file.Flush(true);
        }finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    static FileStreamOptions PrivateOptions(FileMode mode){var options=new FileStreamOptions{Mode=mode,Access=FileAccess.ReadWrite,Share=FileShare.None};if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;return options;}
    public static async Task Initialize(OrganizationDatabase db,OrganizationSettings settings,BootstrapCredential credential,CancellationToken token)
    {
        if(!settings.ManagedDeployment)return;
        using var cert=settings.LoadKeyCertificate()??throw new InvalidOperationException("Protected configuration is unavailable.");
        var fingerprint=Digest(cert.RawData);
        await using var tx=await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7013001)",token);
        var state=await db.DeploymentState.SingleOrDefaultAsync(token);
        if(state is null){state=new(){CertificateSha256=fingerprint};db.DeploymentState.Add(state);}
        else if(state.CertificateSha256!=fingerprint)throw new InvalidOperationException("Persistent key identity changed. Restore the original protected configuration before continuing.");
        if(!await db.Organizations.AnyAsync(token)){
            var bytes=Encoding.UTF8.GetBytes(settings.BootstrapSecret!);var digest=Digest(bytes);
            if(state.BootstrapSha256!=digest){state.BootstrapSha256=digest;state.BootstrapIssuedUtc=DateTimeOffset.UtcNow;state.BootstrapExpiresUtc=state.BootstrapIssuedUtc.AddMinutes(settings.BootstrapLifetimeMinutes);state.BootstrapConsumedUtc=null;}
            credential.Initialize(bytes,state.BootstrapConsumedUtc is null?state.BootstrapExpiresUtc:DateTimeOffset.MinValue);
        }
        await db.SaveChangesAsync(token);await tx.CommitAsync(token);
    }
    public static async Task ConsumeBootstrap(OrganizationDatabase db,OrganizationSettings settings,string secret,CancellationToken token)
    {
        if(!settings.ManagedDeployment)return;
        var state=await db.DeploymentState.SingleAsync(token);
        var digest=Digest(Encoding.UTF8.GetBytes(secret));
        if(state.BootstrapConsumedUtc is not null||state.BootstrapExpiresUtc<=DateTimeOffset.UtcNow||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state.BootstrapSha256),Encoding.ASCII.GetBytes(digest)))throw new OrganizationForbidden();
        state.BootstrapConsumedUtc=DateTimeOffset.UtcNow;
    }
}

public sealed record ServerCheck(string Name,string State,string Message);
public sealed record OrganizationServerHealth(string Version,string PublicOrigin,DateTimeOffset CheckedUtc,string DeploymentState,bool ProductionReady,ServerCheck[] Checks);
public sealed class OrganizationServerDiagnostics(OrganizationDatabase db,OrganizationSettings settings,ServerOperationsOptions operations)
{
    public async Task<OrganizationServerHealth> Check(CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(5));var ct=timeout.Token;
        var checks=new List<ServerCheck>();var healthy=true;
        checks.Add(new("Secure address",settings.LocalDevelopment?"development":"healthy",settings.LocalDevelopment?"Isolated loopback development; not public hosting.":"Exact public HTTPS origin enforced. Untrusted forwarded headers cannot change this address."));
        try{
            await db.Database.OpenConnectionAsync(ct);
            var pending=(await db.Database.GetPendingMigrationsAsync(ct)).Any();
            var applied=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            var known=db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);var unknown=applied.Any(m=>!known.Contains(m));
            healthy=!pending&&!unknown;
            checks.Add(new("Database & schema",healthy?"healthy":"blocked",healthy?"PostgreSQL is connected; the schema matches this application.":"The database schema requires a compatible application or migration. No production readiness is established."));
            await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()";
            var tls=(await command.ExecuteScalarAsync(ct)) is true;
            if(!settings.LocalDevelopment&&!tls)healthy=false;
            checks.Add(new("Database transport",tls&&!settings.LocalDevelopment?"healthy":"development",tls&&!settings.LocalDevelopment?"TLS is active and configured to verify the database hostname.":"Local fixture database transport; not verified production TLS."));
            _=await db.Submissions.AsNoTracking().Select(s=>s.Id).Take(1).ToArrayAsync(ct);
            checks.Add(new("Received evidence", "healthy","Authorized P17 evidence, manifests and receipts use the same PostgreSQL transaction storage. No disposable attachment directory is used."));
            var keys=await db.DataProtectionKeys.AnyAsync(ct);var protectedIdentity=false;
            if(settings.KeyCertificateFile is not null){
                try{using var certificate=settings.LoadKeyCertificate();protectedIdentity=certificate is not null&&keys;
                    if(settings.ManagedDeployment){var pinned=await db.DeploymentState.AsNoTracking().SingleAsync(ct);protectedIdentity=protectedIdentity&&certificate is not null&&pinned.CertificateSha256==OrganizationDeployment.Digest(certificate.RawData);}
                }catch(InvalidOperationException){protectedIdentity=false;}
            }
            if(!settings.LocalDevelopment&&!protectedIdentity)healthy=false;
            checks.Add(new("Persistent identity",protectedIdentity?"healthy":settings.LocalDevelopment?"development":"blocked",protectedIdentity?"Encrypted identity keys are retained in PostgreSQL with a separate protected certificate. Preserve both during replacement.":"Protected production identity persistence is not yet established."));
            var initialized=await db.Organizations.AnyAsync(ct);
            checks.Add(new("Organization setup",initialized?"healthy":"setup-needed",initialized?"Organization and first administrator are saved.":"Owner-controlled, single-use setup is still required."));
            if(!settings.LocalDevelopment&&settings.DatabaseExposure=="unverified")checks.Add(new("Database access rules","unverified","Database network access restrictions have not been declared by the operator."));
        }catch(Exception ex) when(ex is not OperationCanceledException||!token.IsCancellationRequested){healthy=false;checks.Add(new("Storage check","blocked","Storage checks could not complete. No research or secret details are exposed; retry or contact the hosting-account owner."));}
        finally{await db.Database.CloseConnectionAsync();}
        checks.Add(new("Configuration volume",settings.ManagedDeployment?"operator-confirmed":"unverified",settings.ManagedDeployment?"A private persistent configuration directory was declared and its certificate identity checked. Provider durability and off-server recovery still require acceptance.":"The operator must preserve the data-protection certificate and its separate protected credential."));
        checks.Add(new("Recovery configuration",operations.Enabled?"configured":"setup-needed",operations.Enabled?"Protected recovery tools and destination are configured. A successful restore and independent destination are still required.":"Recovery storage is not connected. This is not a production-ready deployment."));
        var checkpoint=await db.ServerBackups.AsNoTracking().Where(b=>b.DeletedUtc==null&&b.RetentionStartedUtc==null).OrderByDescending(b=>b.CreatedUtc).FirstOrDefaultAsync(ct);
        checks.Add(new("Backups & recovery",checkpoint?.IndependentVerifiedUtc is not null?"verified-independent-local-restore":checkpoint?.VerifiedRestoreUtc is not null?"verified-local-restore":"release-gate",checkpoint is null?"No protected recovery point is recorded. Open Server Protection. Independent destination and cloud-loss recovery remain acceptance gates.":$"Latest backup: {checkpoint.CreatedUtc:u}. Paired restore: {(checkpoint.VerifiedRestoreUtc is {} verified?verified.ToString("u"):"not verified")}. Separately keyed restore: {(checkpoint.IndependentVerifiedUtc is {} independent?independent.ToString("u"):"not verified")}. Independent account control and actual cloud recovery still require acceptance."));
        var administrators=db.Memberships.AsNoTracking().Where(m=>m.Role=="Administrator"&&m.State==AeroScience.Shared.OrganizationMembershipState.Active);
        var present=await administrators.AnyAsync(ct);var insecure=await administrators.Join(db.Users,m=>m.UserId,u=>u.Id,(m,u)=>u).AnyAsync(u=>!u.TwoFactorEnabled||u.RecoveryAcknowledgedUtc==null,ct);
        checks.Add(new("Administrator security",present&&!insecure?"healthy":"setup-needed",present&&!insecure?"Active administrators have Identity authenticators and verified saved recovery codes. Password-only production administration is blocked.":"Complete Account Security for each active administrator. Authenticator and tested recovery are required before production administration."));
        checks.Add(new("Pinned updates","release-gate","Server Protection verifies signed pinned update manifests and paired restore checkpoints. The protected offline runner applies only the exact signed binary's compiled transactional schema changes. Provider maintenance/rollout, image rollback and novice disaster recovery remain unaccepted."));
        checks.Add(new("Live provider acceptance","release-gate","No provider deployment or billing authorization is implied. Production readiness remains blocked."));
        return new(OrganizationDeployment.ApplicationVersion,settings.Origin.GetLeftPart(UriPartial.Authority),DateTimeOffset.UtcNow,healthy?"Deployment Healthy":"Deployment Blocked",false,checks.ToArray());
    }
}
