using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Organization;
public sealed record ServerOperationsOptions(bool Enabled,string? Repository,string? PasswordFile,string Destination,string WorkDirectory,string ResticExecutable,string PgDumpExecutable,string PgRestoreExecutable,string? RecoveryConnectionString,string? RepositoryCaFile,long MaximumBundleBytes,string? DistributionFile,string? DistributionPublicKeyFile)
{
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
        return new(enabled,repository,password,destination,directory,c["ResticExecutable"]??"/operations/bin/restic",c["PgDumpExecutable"]??"/operations/bin/pg_dump",c["PgRestoreExecutable"]??"/operations/bin/pg_restore",c["RecoveryConnectionString"],c["RepositoryCaFile"],limit,c["DistributionFile"],c["DistributionPublicKeyFile"]);
    }
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
        var d=signed.Distribution;if(d.Version!=1||d.SourceRevision.Length!=40||d.SourceRevision.Any(c=>!char.IsAsciiHexDigit(c))||!System.Text.RegularExpressions.Regex.IsMatch(d.Image,@"^ghcr\.io/justseanc/aeroscience-organization@sha256:[a-f0-9]{64}$")||d.SchemaSha256.Length!=64||d.SchemaSha256.Any(c=>!char.IsAsciiHexDigit(c))||!d.PairedRestoreRequired)throw new InvalidOperationException("Only a pinned official source/image with paired-restore policy can be prepared.");
        _=ReleaseVersionComparison.Compare(d.ApplicationVersion,d.MinimumVersion);return signed;
    }
}
