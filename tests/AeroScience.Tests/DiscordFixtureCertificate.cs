using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace AeroScience.Tests;

// Synthetic certificates trusted only by an individual fixture client. No machine trust-store
// installation, provider credential or product TLS-validation override exists here.
internal sealed class DiscordFixtureCertificate(X509Certificate2 root,X509Certificate2 server):IDisposable
{
    public X509Certificate2 Root {get;}=root;public X509Certificate2 Server {get;}=server;
    public static DiscordFixtureCertificate Create(string hostname="discord.com")
    {
        using var rootKey=RSA.Create(2048);var issuer=new CertificateRequest("CN=AeroScience isolated HTTPS fixture CA",rootKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        issuer.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));issuer.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign|X509KeyUsageFlags.CrlSign,true));issuer.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuer.PublicKey,false));
        using var authority=issuer.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10),DateTimeOffset.UtcNow.AddDays(2));
        using var serverKey=RSA.Create(2048);var request=new CertificateRequest("CN="+hostname,serverKey,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var names=new SubjectAlternativeNameBuilder();names.AddDnsName(hostname);request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        using var signed=request.Create(authority,DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1),RandomNumberGenerator.GetBytes(16));using var paired=signed.CopyWithPrivateKey(serverKey);
        var bytes=paired.Export(X509ContentType.Pfx);X509Certificate2 leaf;
        try{
            // Windows Schannel needs a temporary user key container; a bare ephemeral RSA handle
            // can be refused before HTTP. No PersistKeySet: disposal removes that temporary key.
            leaf=X509CertificateLoader.LoadPkcs12(bytes,null,(OperatingSystem.IsWindows()?X509KeyStorageFlags.UserKeySet:X509KeyStorageFlags.EphemeralKeySet)|X509KeyStorageFlags.Exportable);
        }finally{CryptographicOperations.ZeroMemory(bytes);}
        var root=X509CertificateLoader.LoadCertificate(authority.RawData);var result=new DiscordFixtureCertificate(root,leaf);
        using var chain=new X509Chain();chain.ChainPolicy=result.Policy();if(!chain.Build(leaf)){var reason=string.Join(", ",chain.ChainStatus.Select(s=>s.Status));result.Dispose();throw new InvalidOperationException("Controlled HTTPS fixture certificate chain refused: "+reason);}
        return result;
    }
    X509ChainPolicy Policy()
    {
        var policy=new X509ChainPolicy{TrustMode=X509ChainTrustMode.CustomRootTrust,RevocationMode=X509RevocationMode.NoCheck,VerificationFlags=X509VerificationFlags.NoFlag};policy.CustomTrustStore.Add(Root);policy.ExtraStore.Add(Root);policy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));return policy;
    }
    public SslClientAuthenticationOptions ClientOptions(Action<string>? diagnostic=null)
    {
        var options=new SslClientAuthenticationOptions{CertificateChainPolicy=Policy()};
        if(diagnostic is not null)options.RemoteCertificateValidationCallback=(_,_,chain,errors)=>{diagnostic(errors+": "+string.Join(", ",chain?.ChainStatus.Select(s=>s.Status)??[]));return errors==SslPolicyErrors.None;};
        return options;
    }
    public void Dispose(){Server.Dispose();Root.Dispose();}
}
