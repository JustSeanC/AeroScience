using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroScience.Organization;
public sealed class OrganizationMaintenance
{
    int active;public bool Active=>Volatile.Read(ref active)!=0;
    public bool Enter()=>Interlocked.CompareExchange(ref active,1,0)==0;
    public void Exit()=>Volatile.Write(ref active,0);
}

// A restore selects a NEW database/certificate pair. It never overwrites a live database.
// Only the protected configuration volume contains the selection; no secrets enter API views.
public sealed record OrganizationRecoveryPair(int Version,Guid Id,Guid BackupId,Guid OrganizationId,string Database,string CertificateSha256,string CertificateFile,string Origin,string SchemaSha256,string ConfigurationBinding,string EvidenceDigest,string AuditDigest,DateTimeOffset CreatedUtc){public string OrganizationName{get;init;}="";public string OriginalOrigin{get;init;}="";public DateTimeOffset CheckpointUtc{get;init;}}
public sealed record OrganizationRecoveryPairView(Guid Id,Guid BackupId,Guid OrganizationId,DateTimeOffset CreatedUtc,string State,string Notice){public string OrganizationName{get;init;}="";public string OriginalOrigin{get;init;}="";public DateTimeOffset CheckpointUtc{get;init;}}
public static class OrganizationRecoveryActivation
{
    internal static string Binding(OrganizationSettings s)=>OrganizationDeployment.Digest(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',new NpgsqlConnectionStringBuilder(s.ConnectionString).Host,new NpgsqlConnectionStringBuilder(s.ConnectionString).Username,s.Origin.GetLeftPart(UriPartial.Authority),s.OwnerIntentId,s.OwnerProjectId,s.OwnerEnvironmentId,s.OwnerServiceId)));
    internal static string PathFor(OrganizationSettings s,bool active)=>Path.Combine(s.StateDirectory??throw new InvalidOperationException("A protected persistent configuration volume is required."),active?"active-pair.json":"recovery-candidate.json");
    internal static void WritePrivate(string path,byte[] value)
    {
        if(new FileInfo(path).LinkTarget is not null)throw new InvalidOperationException("Unsafe protected configuration target.");
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{var o=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};if(!OperatingSystem.IsWindows())o.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;using(var f=new FileStream(temporary,o)){f.Write(value);f.Flush(true);}File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);CryptographicOperations.ZeroMemory(value);}
    }
    internal static OrganizationRecoveryPair? Read(OrganizationSettings settings,bool active)
    {
        var path=PathFor(settings,active);if(!File.Exists(path))return null;var info=OrganizationSettings.PrivateFile(path,1,8192);var pair=JsonSerializer.Deserialize<OrganizationRecoveryPair>(File.ReadAllBytes(info.FullName),WireJson.Options)??throw new InvalidOperationException("Invalid protected recovery pair.");
        if(pair.Version!=1||pair.Id==Guid.Empty||pair.OrganizationId==Guid.Empty||!Regex.IsMatch(pair.Database,"^aeroscience_recovery_[a-f0-9]{32}$",RegexOptions.CultureInvariant)||pair.CertificateFile!="recovered-"+pair.Id.ToString("N")+".pfx"||pair.CertificateSha256.Length!=64||pair.Origin!=settings.Origin.GetLeftPart(UriPartial.Authority)||pair.ConfigurationBinding!=Binding(settings))throw new InvalidOperationException("The restored database/key pair does not match this exact deployment configuration.");return pair;
    }
    public static OrganizationSettings SelectActive(OrganizationSettings settings)
    {
        if(settings.StateDirectory is null)return settings;var pair=Read(settings,true);if(pair is null)return settings;
        var certificateFile=Path.Combine(settings.StateDirectory,pair.CertificateFile);
        var selected=settings with{ConnectionString=new NpgsqlConnectionStringBuilder(settings.ConnectionString){Database=pair.Database}.ConnectionString,KeyCertificateFile=certificateFile};
        using var certificate=selected.LoadKeyCertificate();if(certificate is null||OrganizationDeployment.Digest(certificate.RawData)!=pair.CertificateSha256)throw new InvalidOperationException("The selected restored identity is missing or changed. No fallback database or replacement key is allowed.");return selected;
    }
    internal static async Task<OrganizationRecoveryPair> Stage(NpgsqlConnection connection,OrganizationSettings settings,ServerBackupRecord backup,X509Certificate2 certificate,CancellationToken ct,string? recoveryAdministrator=null)
    {
        if(settings.StateDirectory is null||settings.KeyCertificatePassword is null||Read(settings,false) is not null)throw new InvalidOperationException("A protected volume and an empty replacement slot are required. Review the existing recovery candidate first.");
        var org=await new NpgsqlCommand("SELECT \"PublicId\" FROM organization.\"Organizations\"",connection).ExecuteScalarAsync(ct);if(org is not Guid id||id!=backup.OrganizationId)throw new InvalidOperationException("The restored organization identity is incorrect.");
        await using(var transaction=await connection.BeginTransactionAsync(ct)){
            using var candidate=new OrganizationDatabase(new DbContextOptionsBuilder<OrganizationDatabase>().UseNpgsql(connection).Options);await candidate.Database.UseTransactionAsync(transaction,ct);
            var organization=await candidate.Organizations.AsNoTracking().SingleAsync(ct);
            var recoveryName=recoveryAdministrator?.ToUpperInvariant();var owner=await candidate.Users.AsNoTracking().Where(u=>recoveryName==null?u.Id==organization.AdministratorId:u.NormalizedUserName==recoveryName).Where(u=>candidate.Memberships.Any(m=>m.UserId==u.Id&&m.Role=="Administrator"&&m.State==OrganizationMembershipState.Active)).Select(u=>new{u.Id,u.UserName}).SingleOrDefaultAsync(ct)??throw new InvalidOperationException("The selected recovery administrator is not active in this checkpoint. Choose a compatible recovery file naming an original active administrator; no account was elevated.");
            // No restored external action is silently retried. An acknowledgement may have
            // been delivered AFTER the checkpoint, even if this snapshot says Pending.
            const string fence="""
UPDATE organization."DiscordDestinations" SET "Enabled"=FALSE,"Revision"="Revision"+1,"Deleted"=TRUE,"ProtectedCredential"='',"MaskedIdentifier"='',"AuthorizerStamp"='';
UPDATE organization."DiscordDeliveries" SET "State"=5,"Reason"='Restored checkpoint: prior delivery cannot be proven. No automatic retry.',"FinishedUtc"=now() WHERE "State" IN (0,1);
UPDATE organization."DiscordConsents" SET "Allowed"=FALSE,"Revision"="Revision"+1;
UPDATE organization."LiveConsents" SET "EndedUtc"=COALESCE("EndedUtc",now()),"EndReason"='recovery',"Revision"="Revision"+1;
UPDATE organization."LivePolicies" SET "Enabled"=FALSE,"Revision"="Revision"+1;
UPDATE organization."DeviceGrants" SET "Revoked"=TRUE;
UPDATE organization."Invitations" SET "RevokedUtc"=COALESCE("RevokedUtc",now());
WITH held AS (UPDATE organization."Memberships" SET "State"=2,"Revision"="Revision"+1,"UpdatedUtc"=now() WHERE "State"=1 AND "UserId"<>@owner RETURNING "UserId","Revision","Role")
INSERT INTO organization."Audit" ("TimestampUtc","ActorId","ActorName","Action","ResourceId","Revision") SELECT now(),@system,'Protected recovery','membership-suspended-'||lower("Role"),"UserId","Revision" FROM held;
UPDATE organization."AspNetUsers" SET "SecurityStamp"=gen_random_uuid()::text;
UPDATE organization."ServerOperationsPolicies" SET "Scheduled"=FALSE,"AutomaticRetention"=FALSE,"Revision"="Revision"+1;
UPDATE organization."ServerOperations" SET "State"=4,"Message"='Stopped during explicit paired recovery.',"Revision"="Revision"+1,"FinishedUtc"=now() WHERE "State" IN (0,1);
DELETE FROM organization."OwnerSetups" WHERE "ConsumedUtc" IS NULL;
""";
            await using(var command=new NpgsqlCommand(fence,connection,transaction)){command.Parameters.AddWithValue("owner",owner.Id);command.Parameters.AddWithValue("system",Guid.Empty);await command.ExecuteNonQueryAsync(ct);}
            var enrollment=await candidate.EnrollmentPolicies.SingleAsync(ct);enrollment.Revision++;enrollment.Mode=OrganizationEnrollmentMode.InvitationOnly;enrollment.RequireApproval=true;var history=new OrganizationManagement(candidate);history.Revision(organization.PublicId,enrollment.Revision,"enrollment-policy",OrganizationMembership.View(enrollment),Guid.Empty);history.Audit(Guid.Empty,"Protected recovery","recovery-access-quarantined",organization.PublicId,enrollment.Revision);await candidate.SaveChangesAsync(ct);
            // The new deployment binding changes only configuration metadata, not original
            // claim origins, recorded evidence, receipts, review events or publications.
            if(settings.OwnerPublicKey is not null){var ownerBinding=OrganizationDeployment.Digest(System.Text.Encoding.UTF8.GetBytes(string.Join('\n',settings.OwnerPublicKey,settings.OwnerIntentId,settings.OwnerProjectId,settings.OwnerEnvironmentId,settings.OwnerServiceId,settings.Origin.GetLeftPart(UriPartial.Authority))));await using var bind=new NpgsqlCommand("UPDATE organization.\"DeploymentState\" SET \"OwnerBindingSha256\"=@owner",connection,transaction);bind.Parameters.AddWithValue("owner",ownerBinding);await bind.ExecuteNonQueryAsync(ct);}
            await transaction.CommitAsync(ct);
        }
        var pg=new NpgsqlConnectionStringBuilder(settings.ConnectionString);var user=pg.Username??throw new InvalidOperationException("Application database role is required.");var quoted="\""+user.Replace("\"","\"\"")+"\"";
        await using(var grant=new NpgsqlCommand("REVOKE ALL ON DATABASE \""+connection.Database+"\" FROM PUBLIC; GRANT CONNECT ON DATABASE \""+connection.Database+"\" TO "+quoted+"; GRANT USAGE, CREATE ON SCHEMA organization, public TO "+quoted+"; GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA organization, public TO "+quoted+"; GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA organization, public TO "+quoted,connection))await grant.ExecuteNonQueryAsync(ct);
        await using(var audit=new NpgsqlCommand("INSERT INTO organization.\"Audit\" (\"TimestampUtc\",\"ActorId\",\"ActorName\",\"Action\",\"ResourceId\") VALUES (now(),@actor,'Protected recovery','server-recovery-staged',@backup)",connection)){audit.Parameters.AddWithValue("actor",Guid.Empty);audit.Parameters.AddWithValue("backup",backup.Id);await audit.ExecuteNonQueryAsync(ct);}
        var digests=await OrganizationRecoveryEngine.Digests(connection,null,ct);
        using var model=new OrganizationDatabase(new DbContextOptionsBuilder<OrganizationDatabase>().UseNpgsql().Options);
        var manifest=JsonSerializer.Deserialize<ServerBackupManifest>(backup.ManifestJson,WireJson.Options)!;await using var nameQuery=new NpgsqlCommand("SELECT \"Name\" FROM organization.\"Organizations\"",connection);var name=(string)(await nameQuery.ExecuteScalarAsync(ct))!;
        var pairId=Guid.NewGuid();var pair=new OrganizationRecoveryPair(1,pairId,backup.Id,backup.OrganizationId,connection.Database,OrganizationDeployment.Digest(certificate.RawData),"recovered-"+pairId.ToString("N")+".pfx",settings.Origin.GetLeftPart(UriPartial.Authority),OrganizationSchemaUpgrade.SchemaHash(model.Database.GetMigrations()),Binding(settings),digests.Evidence,digests.Audit,DateTimeOffset.UtcNow){OrganizationName=name,OriginalOrigin=manifest.PublicOrigin,CheckpointUtc=manifest.CreatedUtc};
        var certPath=Path.Combine(settings.StateDirectory,pair.CertificateFile);
        try{WritePrivate(certPath,certificate.Export(X509ContentType.Pfx,settings.KeyCertificatePassword));WritePrivate(PathFor(settings,false),JsonSerializer.SerializeToUtf8Bytes(pair,WireJson.Options));}
        catch{if(File.Exists(certPath))File.Delete(certPath);throw;}return pair;
    }
    internal static async Task<OrganizationRecoveryPair> Activate(OrganizationSettings settings,Guid candidateId,CancellationToken ct)
    {
        var candidate=Read(settings,false)??throw new OrganizationConflict("No verified replacement is ready.");if(candidate.Id!=candidateId)throw new OrganizationConflict("The replacement changed. Review it again.");
        using(var certificate=(settings with{KeyCertificateFile=Path.Combine(settings.StateDirectory!,candidate.CertificateFile)}).LoadKeyCertificate())if(certificate is null||OrganizationDeployment.Digest(certificate.RawData)!=candidate.CertificateSha256)throw new OrganizationConflict("The replacement's protected identity is unavailable.");
        var candidateConnection=new NpgsqlConnectionStringBuilder(settings.ConnectionString){Database=candidate.Database}.ConnectionString;
        await using var connection=new NpgsqlConnection(candidateConnection);await connection.OpenAsync(ct);
        var digests=await OrganizationRecoveryEngine.Digests(connection,null,ct);if(digests.Evidence!=candidate.EvidenceDigest||digests.Audit!=candidate.AuditDigest)throw new OrganizationConflict("The replacement changed after verification. No database switch was made.");
        using(var model=new OrganizationDatabase(new DbContextOptionsBuilder<OrganizationDatabase>().UseNpgsql(candidateConnection).Options))if(OrganizationSchemaUpgrade.SchemaHash(await model.Database.GetAppliedMigrationsAsync(ct))!=candidate.SchemaSha256)throw new OrganizationConflict("The replacement schema is incompatible. No database switch was made.");
        WritePrivate(PathFor(settings,true),JsonSerializer.SerializeToUtf8Bytes(candidate,WireJson.Options));File.Delete(PathFor(settings,false));return candidate;
    }
    internal static OrganizationRecoveryPairView? View(OrganizationSettings settings){if(settings.StateDirectory is null)return null;var p=Read(settings,false);return p is null?null:new(p.Id,p.BackupId,p.OrganizationId,p.CreatedUtc,"Awaiting Explicit Activation","Verified replacement. Activating signs everyone out. Only the recovered administrator retains active access; review other memberships and send new invitations. Sharing, notifications and automatic deletion stay off. Restored webhook secrets are removed; configure a new webhook explicitly. Original evidence remains local to this organization server."){OrganizationName=p.OrganizationName,OriginalOrigin=p.OriginalOrigin,CheckpointUtc=p.CheckpointUtc};}
}
