using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
namespace AeroScience.Organization;

// Narrow platform adapter. It configures the same application; it cannot provision cloud accounts or make purchases.
public static class RailwayRuntime
{
    const string State="/configuration";
    [DllImport("libc",SetLastError=true)]static extern uint geteuid();
    [DllImport("libc",SetLastError=true)]static extern int chown(string path,uint owner,uint group);
    [DllImport("libc",SetLastError=true)]static extern int setgroups(nuint size,IntPtr list);
    [DllImport("libc",SetLastError=true)]static extern int setresgid(uint real,uint effective,uint saved);
    [DllImport("libc",SetLastError=true)]static extern int setresuid(uint real,uint effective,uint saved);
    [DllImport("libc",SetLastError=true)]static extern int prctl(int option,ulong arg2,ulong arg3,ulong arg4,ulong arg5);
    public static void RefuseRootApplication(){if(OperatingSystem.IsLinux()&&geteuid()==0)throw new InvalidOperationException("The organization application must never serve requests as root.");}
    internal static void InitializeVolumeAndDropPrivileges()
    {
        if(!OperatingSystem.IsLinux())throw new InvalidOperationException("The Railway runtime requires its supported Linux container.");
        if(geteuid()==0){
            // Railway mounts volumes as root. Only this fixed volume root is initialized. Never recursively chown a supplied path.
            var dir=new DirectoryInfo(State);if(!dir.Exists||dir.LinkTarget is not null)throw new InvalidOperationException("The protected Railway configuration volume is missing or unsafe.");
            if(chown(State,1654,1654)!=0)throw new InvalidOperationException("The protected volume could not be initialized.");File.SetUnixFileMode(State,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            if(setgroups(0,IntPtr.Zero)!=0||setresgid(1654,1654,1654)!=0||setresuid(1654,1654,1654)!=0)throw new InvalidOperationException("The Railway initializer could not permanently discard root privileges.");
        }
        if(geteuid()!=1654||prctl(38,1,0,0,0)!=0)throw new InvalidOperationException("The organization application must run as its restricted service identity with no new privileges.");
    }
    public static string PublicOrigin(string domain)
    {
        if(domain.Length>253||!Regex.IsMatch(domain,@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.up\.railway\.app$",RegexOptions.CultureInvariant))throw new InvalidOperationException("Use the exact native Railway-generated public HTTPS hostname.");return "https://"+domain;
    }
    public static string DatabaseHost(string domain)
    {
        if(domain.Length>253||!Regex.IsMatch(domain,@"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.railway\.internal$",RegexOptions.CultureInvariant))throw new InvalidOperationException("Use the exact private Railway PostgreSQL service hostname.");return domain;
    }
    public static byte[] VerifyTrust(string host,string nonce,string certificateBase64,string authentication,string secret)
    {
        _=DatabaseHost(host);if(nonce.Length!=64||nonce.Any(c=>!char.IsAsciiHexDigit(c))||secret.Length is <32 or >256||certificateBase64.Length>24576)throw new InvalidOperationException("Unsupported database trust response.");
        var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes("aeroscience-railway-database-trust-v1\n"+host+"\n"+nonce+"\n"+certificateBase64));
        byte[] supplied;try{supplied=Convert.FromHexString(authentication);}catch(FormatException){throw new InvalidOperationException("Database trust authentication failed.");}
        if(!CryptographicOperations.FixedTimeEquals(expected,supplied))throw new InvalidOperationException("Database trust authentication failed. No certificate was installed.");
        var pem=Convert.FromBase64String(certificateBase64);using var certificate=X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(pem));
        var constraint=certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();if(constraint?.CertificateAuthority!=true||certificate.NotAfter.ToUniversalTime()<=DateTime.UtcNow||certificate.NotBefore.ToUniversalTime()>DateTime.UtcNow)throw new InvalidOperationException("The authenticated database CA is invalid or expired.");return pem;
    }
    public static async Task Configure(IConfigurationManager configuration,CancellationToken ct)
    {
        // Only --railway-start enters this adapter. Ordinary self-hosted or local servers never adopt provider variables.
        var host=DatabaseHost(configuration["AEROSCIENCE_DATABASE_HOST"]??"");var secret=configuration["AEROSCIENCE_DATABASE_TRUST_SECRET"]??"";
        var origin=PublicOrigin(configuration["RAILWAY_PUBLIC_DOMAIN"]??"");
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(5)};byte[]? certificate=null;
        var nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        for(var attempt=0;attempt<30;attempt++){
            try{using var request=new HttpRequestMessage(HttpMethod.Post,"http://"+host+":8081/trust"){Content=new StringContent(JsonSerializer.Serialize(new{host,nonce}),Encoding.UTF8,"application/json")};using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();await response.Content.LoadIntoBufferAsync(32768,ct);using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));certificate=VerifyTrust(host,nonce,json.RootElement.GetProperty("certificate").GetString()!,json.RootElement.GetProperty("authentication").GetString()!,secret);break;}
            catch(HttpRequestException) when(attempt<29){await Task.Delay(TimeSpan.FromSeconds(1),ct);}
        }
        if(certificate is null)throw new InvalidOperationException("The protected database trust service is unavailable.");
        var trust=Path.Combine(State,"database-ca.crt");if(new FileInfo(trust).LinkTarget is not null)throw new InvalidOperationException("Unsafe database trust destination.");
        var fileOptions=new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write};if(!OperatingSystem.IsWindows())fileOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        await using(var file=new FileStream(trust,fileOptions)){await file.WriteAsync(certificate,ct);file.Flush(true);}
        var pg=new NpgsqlConnectionStringBuilder{Host=host,Port=5432,Database=configuration["AEROSCIENCE_DATABASE_NAME"],Username=configuration["AEROSCIENCE_DATABASE_USER"],Password=configuration["AEROSCIENCE_DATABASE_PASSWORD"],SslMode=SslMode.VerifyFull,RootCertificate=trust,GssEncryptionMode=GssEncryptionMode.Disable};
        if(configuration["AEROSCIENCE_RECOVERY_DATABASE_USER"] is {} recoveryUser&&configuration["AEROSCIENCE_RECOVERY_DATABASE_PASSWORD"] is {} recoveryPassword){var recovery=new NpgsqlConnectionStringBuilder(pg.ConnectionString){Database="postgres",Username=recoveryUser,Password=recoveryPassword};configuration["Organization:Operations:RecoveryConnectionString"]=recovery.ConnectionString;}
        if(configuration["AEROSCIENCE_RECOVERY_REPOSITORY"] is {} repository&&configuration["AEROSCIENCE_RECOVERY_REPOSITORY_PASSWORD"] is {} repositoryPassword){
            ServerOperationsOptions.ValidateRepository(repository,false);if(repositoryPassword.Length!=64||repositoryPassword.Any(c=>!char.IsAsciiHexDigit(c)))throw new InvalidOperationException("Use the independent native-generated recovery encryption key.");
            var passwordFile=Path.Combine(State,"recovery-repository.key");if(new FileInfo(passwordFile).LinkTarget is not null)throw new InvalidOperationException("Unsafe recovery key destination.");
            if(File.Exists(passwordFile)){var retained=File.ReadAllText(OrganizationSettings.PrivateFile(passwordFile,64,64).FullName);if(!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(retained),Encoding.ASCII.GetBytes(repositoryPassword)))throw new InvalidOperationException("Recovery key changed. Restore the original key; do not replace it silently.");}
            else{var keyOptions=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write};if(!OperatingSystem.IsWindows())keyOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;using var file=new FileStream(passwordFile,keyOptions);file.Write(Encoding.ASCII.GetBytes(repositoryPassword));file.Flush(true);}
            configuration["Organization:Operations:S3BucketLookup"]=configuration["AEROSCIENCE_RECOVERY_BUCKET_LOOKUP"]??"dns";configuration["Organization:Operations:Repository"]=repository;configuration["Organization:Operations:PasswordFile"]=passwordFile;configuration["Organization:Operations:Enabled"]="true";
        }
        configuration["Organization:PublicOrigin"]=origin;configuration["Organization:ConnectionString"]=pg.ConnectionString;configuration["Organization:ManagedDeployment"]="true";configuration["Organization:HostingProvider"]="railway";configuration["Organization:StateDirectory"]=State;configuration["Organization:DatabaseExposure"]="private-network";configuration["Organization:PersistentStorageConfirmed"]="true";
        // An informational X-Railway-Edge header is not authentication. Exact trusted proxy configuration is still required.
        if(!(configuration.GetSection("Organization:TrustedProxies").Get<string[]>()??[]).Any())throw new InvalidOperationException("Railway ingress trust has not been verified. The deployment candidate cannot accept public requests until its strict proxy contract is established.");
    }
}
