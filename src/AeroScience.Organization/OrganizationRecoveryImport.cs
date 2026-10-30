using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using AeroScience.Shared;

namespace AeroScience.Organization;
internal sealed record ProtectedRecoveryImport(OrganizationRecoveryKit Kit,bool UseLatestRecoveryPoint);

public static class OrganizationRecoveryImport
{
    internal static IDataProtector Protector(IDataProtectionProvider protection,Guid operation,Guid actor)=>protection.CreateProtector("AeroScience.Organization.RecoveryImport.v1",operation.ToString("D"),actor.ToString("D"));
    public static void Validate(OrganizationRecoveryKit kit,OrganizationSettings settings,bool confirmAddressChange)
    {
        if(kit is null)throw new ArgumentException("Choose a private organization recovery file.");
        if(kit.RecoveryAdministrator is {} account&&(account.Length is <1 or >64||account.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '_' or '-' or '@'))))throw new ArgumentException("The private recovery file must name an original administrator account.");
        var point=kit.RecoveryPoint;var target=kit.Independent;
        if(kit.Version!=1||kit.OrganizationId==Guid.Empty||point is null||point.OrganizationId!=kit.OrganizationId||point.BackupId==Guid.Empty||point.IndependentSnapshotId is not{Length:64}||point.IndependentSnapshotId.Any(c=>!char.IsAsciiHexDigitLower(c))||point.BundleSha256 is not{Length:64}||point.BundleSha256.Any(c=>!char.IsAsciiHexDigitLower(c))||target is null)throw new ArgumentException("Choose a current private recovery file with a verified independent recovery point. A same-account bucket or key alone is not enough.");
        if(!Uri.TryCreate(kit.Origin,UriKind.Absolute,out var origin)||origin.GetLeftPart(UriPartial.Authority)!=kit.Origin||origin.Query!=""||origin.Fragment!=""||origin.UserInfo!=""||origin.Scheme!=(settings.LocalDevelopment?"http":"https")||kit.Origin!=settings.Origin.GetLeftPart(UriPartial.Authority)&&!confirmAddressChange)throw new ArgumentException("Review the original organization address and explicitly approve recovery at this server's new address.");
        if(target.Repository is not{Length:>=1 and <=2048}||target.Repository.Any(char.IsControl)||target.RepositoryPassword is not{Length:>=32 and <=256}||target.RepositoryPassword.Any(char.IsWhiteSpace)||target.S3BucketLookup is not("auto" or "path" or "dns")||target.Credentials is null||target.Credentials.Count>6||target.Credentials.Any(v=>!RecoveryRepositoryTarget.CredentialNames.Contains(v.Key,StringComparer.Ordinal)||v.Value is not{Length:>=1 and <=4096}||v.Value.Any(char.IsControl)))throw new ArgumentException("The independent recovery key has an unsupported protected-storage configuration.");
        // UI input cannot turn the native tool into a generic HTTP/SSRF client. Production
        // guided import supports standard AWS S3 endpoints only; other operator-configured
        // repositories remain supported by the existing advanced recovery engine.
        if(settings.LocalDevelopment&&Path.IsPathFullyQualified(target.Repository))return;
        if(!target.Repository.StartsWith("s3:https://",StringComparison.Ordinal)||target.Repository.Contains('%')||target.Repository.Split('/').Any(p=>p is "." or "..")||!Uri.TryCreate(target.Repository[3..],UriKind.Absolute,out var uri)||uri.Port!=443||!uri.IsDefaultPort||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath is "" or "/"||!Regex.IsMatch(uri.Host,@"^s3\.(?:us|eu|ap|sa|ca|me|af|il|mx)-(?:east|west|south|north|central|northeast|southeast|southwest)-[1-4]\.amazonaws\.com$",RegexOptions.CultureInvariant)||!target.Credentials.ContainsKey("AWS_ACCESS_KEY_ID")||!target.Credentials.ContainsKey("AWS_SECRET_ACCESS_KEY")||target.Credentials.Keys.Any(k=>k.StartsWith("RESTIC_",StringComparison.Ordinal)))throw new ArgumentException("Guided recovery requires an independently controlled standard AWS S3 repository. Custom endpoints, redirects, local addresses and REST destinations are not accepted here. No provider account or charges are created.");
    }
    internal static async Task<ServerBackupRecord> Stage(OrganizationDatabase db,OrganizationSettings settings,ServerOperationsOptions options,ServerOperationRecord job,IDataProtectionProvider protection,CancellationToken ct)
    {
        var approved=JsonSerializer.Deserialize<ProtectedRecoveryImport>(Protector(protection,job.Id,job.ActorId).Unprotect(job.ProtectedImportJson??throw new InvalidOperationException("Approved recovery material is unavailable.")),WireJson.Options)??throw new InvalidOperationException("Approved recovery material is invalid.");var kit=approved.Kit;Validate(kit,settings,true);
        var target=kit.Independent!;var point=kit.RecoveryPoint!;OrganizationRecoveryEngine.MakePrivate(options.WorkDirectory);var directory=Path.Combine(options.WorkDirectory,"restore-"+Guid.NewGuid().ToString("N"));OrganizationRecoveryEngine.MakePrivate(directory);
        try{
            var password=Path.Combine(directory,"repository.key");var credentials=Path.Combine(directory,"credentials.json");OrganizationRecoveryActivation.WritePrivate(password,Encoding.UTF8.GetBytes(target.RepositoryPassword));OrganizationRecoveryActivation.WritePrivate(credentials,JsonSerializer.SerializeToUtf8Bytes(target.Credentials));
            var imported=options with{Repository=target.Repository,PasswordFile=password,RepositoryCredentialsFile=credentials,RepositoryCaFile=null,S3BucketLookup=target.S3BucketLookup,Independent=null,InheritStorageCredentials=false};var engine=new OrganizationRecoveryEngine(db,settings,imported);
            if(approved.UseLatestRecoveryPoint)point=await engine.LatestIndependentPoint(kit.OrganizationId,ct);
            var backup=await engine.Discover(point.IndependentSnapshotId,ct);
            if(backup.Id!=point.BackupId||backup.OrganizationId!=kit.OrganizationId||backup.BundleSha256!=point.BundleSha256)throw new InvalidOperationException("The independent recovery point does not match its exact approved organization and integrity reference.");
            await engine.VerifyRestore(backup,ct,stageReplacement:true,renewIdentity:true,approvedOriginalOrigin:kit.Origin,recoveryAdministrator:kit.RecoveryAdministrator);
            backup.RepositorySha256=OrganizationDeployment.Digest(Encoding.UTF8.GetBytes(target.Repository));return backup;
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
}
