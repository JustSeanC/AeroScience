using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Organization;
public sealed record ServerOperationsOptions(bool Enabled,string? Repository,string? PasswordFile,string Destination,string WorkDirectory,string ResticExecutable,string PgDumpExecutable,string PgRestoreExecutable,string? RecoveryConnectionString,string? RepositoryCaFile,long MaximumBundleBytes,string? DistributionFile,string? DistributionPublicKeyFile)
{
    public string S3BucketLookup {get;init;}="auto";
    public string? RepositoryCredentialsFile {get;init;}
    public RecoveryRepositoryTarget? Independent {get;init;}
    public bool InheritStorageCredentials{get;init;}=true;
    public TimeProvider Clock{get;init;}=TimeProvider.System;
    public static ServerOperationsOptions Read(IConfiguration config,OrganizationSettings settings)
    {
        var c=config.GetSection("Organization:Operations");var enabled=c.GetValue<bool>("Enabled");var repository=c["Repository"];var password=c["PasswordFile"];var directory=c["WorkDirectory"]??"/tmp/aeroscience-operations";
        if(enabled){
            if(!Path.IsPathFullyQualified(directory)||string.IsNullOrWhiteSpace(repository)||string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("Configure protected recovery storage, its independent encryption key and a private bounded work directory.");
            if(settings.KeyCertificateFile is null||settings.KeyCertificatePassword is null)throw new InvalidOperationException("Paired recovery requires the original protected identity certificate and its credential.");
            var temporary=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(directory).StartsWith(temporary,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))throw new InvalidOperationException("Recovery work must use a private ephemeral system-temporary directory, not persistent evidence/configuration storage.");
            ValidateRepository(repository,settings.LocalDevelopment);
            _=OrganizationSettings.PrivateFile(password,32,256);
        }
        var limit=c.GetValue<long?>("MaximumBundleBytes")??2L*1024*1024*1024;if(limit<1048576||limit>1024L*1024*1024*1024)throw new InvalidOperationException("Choose a bounded recovery package size between 1 MiB and 1 TiB.");
        var destination=c["DestinationLabel"]??"Operator-configured recovery storage";if(destination.Length>120||destination.Any(char.IsControl))throw new InvalidOperationException("Choose a readable recovery destination label.");
        var lookup=c["S3BucketLookup"]??"auto";if(lookup is not("auto" or "dns" or "path"))throw new InvalidOperationException("Choose a supported S3 bucket addressing style.");
        RecoveryRepositoryTarget? independent=null;
        if(c["Independent:Repository"] is {} separate){
            if(!enabled)throw new InvalidOperationException("Enable and protect the primary recovery configuration before configuring its independent copy.");
            ValidateRepository(separate,settings.LocalDevelopment);var key=c["Independent:PasswordFile"]??throw new InvalidOperationException("An independent recovery copy needs its own protected encryption key.");
            _=OrganizationSettings.PrivateFile(key,32,256);
            if(separate==repository||File.ReadAllText(key).Trim()==File.ReadAllText(password!).Trim())throw new InvalidOperationException("Independent recovery requires a different repository and encryption key.");
            var independentLookup=c["Independent:S3BucketLookup"]??"auto";if(independentLookup is not("auto" or "dns" or "path"))throw new InvalidOperationException("Unsupported independent recovery bucket addressing style.");
            independent=new(separate,key,c["Independent:CredentialsFile"],c["Independent:RepositoryCaFile"],independentLookup);
            var credentials=independent.Credentials();
            if(!settings.LocalDevelopment&&(independent.CredentialsFile is null||(separate.StartsWith("s3:",StringComparison.Ordinal)?!credentials.ContainsKey("AWS_ACCESS_KEY_ID")||!credentials.ContainsKey("AWS_SECRET_ACCESS_KEY"):!credentials.ContainsKey("RESTIC_REST_USERNAME")||!credentials.ContainsKey("RESTIC_REST_PASSWORD"))))throw new InvalidOperationException("Use independently scoped protected credentials for the separate recovery destination; primary account credentials are never inherited.");
        }
        var result=new ServerOperationsOptions(enabled,repository,password,destination,directory,c["ResticExecutable"]??"/operations/bin/restic",c["PgDumpExecutable"]??"/operations/bin/pg_dump",c["PgRestoreExecutable"]??"/operations/bin/pg_restore",c["RecoveryConnectionString"],c["RepositoryCaFile"],limit,c["DistributionFile"],c["DistributionPublicKeyFile"]){S3BucketLookup=lookup,RepositoryCredentialsFile=c["RepositoryCredentialsFile"],Independent=independent};
        _=result.StorageCredentials();return result;
    }
    internal Dictionary<string,string> StorageCredentials()=>new RecoveryRepositoryTarget(Repository??"",PasswordFile??"",RepositoryCredentialsFile,RepositoryCaFile,S3BucketLookup).Credentials();
    public static void ValidateRepository(string repository,bool local)
    {
        if(repository.Length>2048||repository.Any(char.IsControl))throw new InvalidOperationException("Use a bounded protected recovery repository.");
        if(local&&Path.IsPathFullyQualified(repository))return;
        var prefix=repository.StartsWith("s3:",StringComparison.Ordinal)?"s3:":repository.StartsWith("rest:",StringComparison.Ordinal)?"rest:":null;
        if(prefix is null||!Uri.TryCreate(repository[prefix.Length..],UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||uri.AbsolutePath is "/" or ""||uri.AbsolutePath.Split('/').Any(p=>p is ".." or "."))throw new InvalidOperationException("Use authenticated HTTPS S3 or append-only REST recovery storage; credentials must remain in protected variables, not URLs.");
    }
    public SignedServerDistribution? Distribution()
    {
        if(DistributionFile is null||DistributionPublicKeyFile is null)return null;
        var data=OrganizationSettings.PrivateFile(DistributionFile,1,16384);var json=File.ReadAllBytes(data.FullName);
        var signed=JsonSerializer.Deserialize<SignedServerDistribution>(json,new JsonSerializerOptions(WireJson.Options){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new InvalidOperationException("Unsupported distribution manifest.");
        using var key=ECDsa.Create();key.ImportFromPem(File.ReadAllText(OrganizationSettings.PrivateFile(DistributionPublicKeyFile,1,16384).FullName));
        var signature=Convert.FromBase64String(signed.Signature);if(!key.VerifyData(JsonSerializer.SerializeToUtf8Bytes(signed.Distribution,WireJson.Options),signature,HashAlgorithmName.SHA256))throw new InvalidOperationException("The server distribution signature was not accepted.");
        var d=signed.Distribution;if(d.Version!=1||d.SourceRevision.Length!=40||d.SourceRevision.Any(c=>!char.IsAsciiHexDigit(c))||!System.Text.RegularExpressions.Regex.IsMatch(d.Image,@"^ghcr\.io/justseanc/aeroscience-organization(?:-railway)?@sha256:[a-f0-9]{64}$")||d.SchemaSha256.Length!=64||d.SchemaSha256.Any(c=>!char.IsAsciiHexDigit(c))||!d.PairedRestoreRequired)throw new InvalidOperationException("Only a pinned official source/image with paired-restore policy can be prepared.");
        _=ReleaseVersionComparison.Compare(d.ApplicationVersion,d.MinimumVersion);return signed;
    }
}
public sealed record RecoveryRepositoryTarget(string Repository,string PasswordFile,string? CredentialsFile,string? RepositoryCaFile,string S3BucketLookup)
{
    internal static readonly string[] CredentialNames=["AWS_ACCESS_KEY_ID","AWS_SECRET_ACCESS_KEY","AWS_SESSION_TOKEN","AWS_DEFAULT_REGION","RESTIC_REST_USERNAME","RESTIC_REST_PASSWORD"];
    internal Dictionary<string,string> Credentials()
    {
        // Explicit files isolate destinations. Legacy source-only deployments may use protected process variables.
        if(CredentialsFile is null)return [];
        var data=File.ReadAllBytes(OrganizationSettings.PrivateFile(CredentialsFile,2,16384).FullName);
        var values=JsonSerializer.Deserialize<Dictionary<string,string>>(data)??throw new InvalidOperationException("Recovery credentials must be a protected data-only object.");
        if(values.Any(v=>!CredentialNames.Contains(v.Key,StringComparer.Ordinal)||string.IsNullOrEmpty(v.Value)||v.Value.Length>4096||v.Value.Any(char.IsControl)))throw new InvalidOperationException("Unsupported recovery credential configuration.");return values;
    }
}
