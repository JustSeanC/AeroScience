using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using Npgsql;

namespace AeroScience.Organization;

public sealed record OrganizationSettings(string ConnectionString,Uri Origin,bool LocalDevelopment,string? BootstrapSecretFile,
    string? KeyCertificateFile,string? KeyCertificatePassword,IPAddress[] TrustedProxies)
{
    public bool RequireAdministratorMfa {get;init;}
    public bool ManagedDeployment {get;init;}
    public string? StateDirectory {get;init;}
    public string? BootstrapSecret {get;init;}
    public int BootstrapLifetimeMinutes {get;init;}=30;
    public string HostingProvider {get;init;}="self-hosted";
    public string DatabaseExposure {get;init;}="unverified";
    public bool AuthenticatedLocalIngress {get;init;}
    public int IngressPublicPort {get;init;}=8080;
    public int IngressApplicationPort {get;init;}=9001;
    public string? OwnerPublicKey {get;init;}
    public Guid OwnerIntentId {get;init;}
    public string OwnerProjectId {get;init;}="";
    public string OwnerEnvironmentId {get;init;}="";
    public string OwnerServiceId {get;init;}="";
    public static OrganizationSettings Read(IConfiguration configuration,IHostEnvironment environment)
    {
        var c=configuration.GetSection("Organization");
        var local=c.GetValue<bool>("LocalDevelopment");
        if(local&&!environment.IsDevelopment()&&!environment.IsEnvironment("Testing"))
            throw new InvalidOperationException("Loopback organization development requires the Development or Testing environment.");
        if(!Uri.TryCreate(c["PublicOrigin"],UriKind.Absolute,out var origin)||origin.AbsolutePath!="/"||origin.Query!=""||origin.Fragment!=""||origin.UserInfo!="")
            throw new InvalidOperationException("Configure the exact Organization:PublicOrigin (scheme and host only).");
        if(local?(origin.Scheme!="http"||!IPAddress.TryParse(origin.Host,out var ip)||!IPAddress.IsLoopback(ip)):origin.Scheme!="https")
            throw new InvalidOperationException("Organization hosting requires HTTPS; isolated local development requires an explicit loopback HTTP origin.");
        var connection=c["ConnectionString"];
        if(string.IsNullOrWhiteSpace(connection))throw new InvalidOperationException("Configure durable organization PostgreSQL storage; no desktop or ephemeral fallback is available.");
        NpgsqlConnectionStringBuilder pg;
        try{pg=new(connection);}catch(ArgumentException){throw new InvalidOperationException("Organization PostgreSQL settings are invalid.");}
        if(string.IsNullOrWhiteSpace(pg.Host)||string.IsNullOrWhiteSpace(pg.Database)||string.IsNullOrWhiteSpace(pg.Username))throw new InvalidOperationException("Organization PostgreSQL host, database and user are required.");
        if(!local&&pg.SslMode!=SslMode.VerifyFull)throw new InvalidOperationException("Production PostgreSQL requires SslMode=VerifyFull.");
        if(pg.IncludeErrorDetail||pg.LogParameters)throw new InvalidOperationException("Organization database parameter/error-detail logging must remain disabled.");
        var managed=c.GetValue<bool>("ManagedDeployment");
        var state=c["StateDirectory"];
        var certificate=c["KeyCertificateFile"];
        var password=c["KeyCertificatePassword"];
        var bootstrap=c["BootstrapSecret"];
        var ownerPublicKey=c["OwnerPublicKey"];var ownerIntent=Guid.Empty;
        var ownerProject=c["OwnerProjectId"]??"";var ownerEnvironment=c["OwnerEnvironmentId"]??"";var ownerService=c["OwnerServiceId"]??"";
        if(ownerPublicKey is not null&&(!AeroScience.Shared.OrganizationOwnerProtocol.ValidPublicKey(ownerPublicKey)||!Guid.TryParse(c["OwnerIntentId"],out ownerIntent)||ownerIntent==Guid.Empty||new[]{ownerProject,ownerEnvironment,ownerService}.Any(v=>!Guid.TryParse(v,out var id)||id==Guid.Empty)))throw new InvalidOperationException("Owner pairing requires a supported public key, intent and exact deployment project/environment/service identities.");
        var lifetime=c.GetValue<int?>("BootstrapLifetimeMinutes")??30;
        var provider=c["HostingProvider"]??"self-hosted";
        var exposure=c["DatabaseExposure"]??"unverified";
        if(provider is not("self-hosted" or "render" or "railway" or "digitalocean" or "local-validation"))throw new InvalidOperationException("Choose a supported deployment provider label.");
        if(managed){
            if(local)throw new InvalidOperationException("Managed deployment cannot use loopback development security.");
            if(!c.GetValue<bool>("PersistentStorageConfirmed")||string.IsNullOrWhiteSpace(state)||!Path.IsPathFullyQualified(state))throw new InvalidOperationException("Managed deployment requires an explicitly confirmed persistent private configuration volume.");
            if(password is null||password.Length is <32 or >256||ownerPublicKey is null&&(bootstrap is null||bootstrap.Length is <32 or >256||bootstrap.Any(char.IsWhiteSpace))||lifetime is <5 or >60)throw new InvalidOperationException("Use protected key credentials and owner pairing or a separate expiring operator setup credential.");
            if(string.Equals(password,bootstrap,StringComparison.Ordinal))throw new InvalidOperationException("Setup and key protection require independent credentials.");
            if(exposure is not("private-network" or "restricted-tls"))throw new InvalidOperationException("Confirm a private or explicitly restricted, hostname-verified database connection.");
            certificate=Path.Combine(state,"data-protection.pfx");
        }
        if(configuration.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED"))throw new InvalidOperationException("Automatic unrestricted forwarding is not supported; configure explicit trusted proxy addresses.");
        if(!local&&string.IsNullOrWhiteSpace(certificate))throw new InvalidOperationException("Configure a persistent organization data-protection certificate before production startup.");
        var proxies=c.GetSection("TrustedProxies").Get<string[]>()??[];
        if(proxies.Length>4||proxies.Any(p=>!IPAddress.TryParse(p,out _)))throw new InvalidOperationException("Configure at most four explicit trusted proxy IPs.");
        var ingress=c.GetValue<bool>("AuthenticatedLocalIngress");
        var publicPort=c.GetValue<int?>("IngressPublicPort")??8080;var applicationPort=c.GetValue<int?>("IngressApplicationPort")??9001;
        if(ingress&&(local||provider!="railway"||proxies.Length!=0||publicPort is <1024 or >65535||applicationPort is <1024 or >65535||publicPort==applicationPort))throw new InvalidOperationException("The Railway local gateway requires separate unprivileged ports and no forwarded-header proxy trust.");
        if(!local)pg.GssEncryptionMode=GssEncryptionMode.Disable; // Password/TLS deployment; no optional Kerberos runtime dependency.
        return OrganizationRecoveryActivation.SelectActive(new(pg.ConnectionString,origin,local,c["BootstrapSecretFile"],certificate,password,proxies.Select(IPAddress.Parse).ToArray()){
            RequireAdministratorMfa=!local||c.GetValue<bool>("RequireAdministratorMfa"),ManagedDeployment=managed,StateDirectory=state,BootstrapSecret=bootstrap,BootstrapLifetimeMinutes=lifetime,HostingProvider=provider,DatabaseExposure=exposure,AuthenticatedLocalIngress=ingress,IngressPublicPort=publicPort,IngressApplicationPort=applicationPort,OwnerPublicKey=ownerPublicKey,OwnerIntentId=ownerIntent,OwnerProjectId=ownerProject,OwnerEnvironmentId=ownerEnvironment,OwnerServiceId=ownerService
        });
    }
    public byte[] ReadBootstrapSecret()
    {
        if(string.IsNullOrWhiteSpace(BootstrapSecretFile))throw new InvalidOperationException("First administrator setup requires an operator-provided protected bootstrap secret file.");
        try{
            var info=PrivateFile(BootstrapSecretFile,32,256);
            var secret=System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(info.FullName).Trim());
            if(secret.Length is <32 or >256)throw new InvalidOperationException("Bootstrap secret is outside supported bounds.");
            return secret;
        }catch(IOException){throw new InvalidOperationException("The protected bootstrap secret file is unavailable.");}
        catch(UnauthorizedAccessException){throw new InvalidOperationException("The protected bootstrap secret file is unavailable.");}
    }
    internal static FileInfo PrivateFile(string path,long minimum,long maximum)
    {
        var info=new FileInfo(path);
        if(!info.Exists||info.LinkTarget is not null||info.Length<minimum||info.Length>maximum)throw new InvalidOperationException("The operator credential must be a bounded, protected regular file.");
        if(OperatingSystem.IsWindows()){
            var broad=new[]{new SecurityIdentifier(WellKnownSidType.WorldSid,null),new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null)};
            foreach(FileSystemAccessRule rule in info.GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if(rule.AccessControlType==AccessControlType.Allow&&broad.Contains(rule.IdentityReference)&&(rule.FileSystemRights&(FileSystemRights.ReadData|FileSystemRights.WriteData|FileSystemRights.ChangePermissions))!=0)
                    throw new InvalidOperationException("Operator credential files must not grant broad user access.");
        }else if((File.GetUnixFileMode(info.FullName)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite))!=0)
            throw new InvalidOperationException("Operator credential files must be readable only by their operator/service account.");
        return info;
    }
    public X509Certificate2? LoadKeyCertificate()
    {
        if(KeyCertificateFile is null)return null;
        X509Certificate2 cert;
        try{_=PrivateFile(KeyCertificateFile,1,1048576);cert=X509CertificateLoader.LoadPkcs12FromFile(KeyCertificateFile,KeyCertificatePassword,X509KeyStorageFlags.EphemeralKeySet);}
        catch(Exception ex) when(ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException){throw new InvalidOperationException("The organization data-protection certificate is unavailable or invalid.");}
        if(!cert.HasPrivateKey||cert.NotAfter.ToUniversalTime()<=DateTime.UtcNow||cert.NotBefore.ToUniversalTime()>DateTime.UtcNow){cert.Dispose();throw new InvalidOperationException("A current private data-protection certificate is required.");}
        return cert;
    }
}
