using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AeroScience.Organization;
using Xunit;
namespace AeroScience.Tests;
public sealed class RailwayRuntimeTests
{
    [Theory][InlineData("test.up.railway.app")]public void ExactNativePublicOrigin(string domain)=>Assert.Equal("https://"+domain,RailwayRuntime.PublicOrigin(domain));
    [Theory][InlineData("https://x.up.railway.app")][InlineData("x.up.railway.app.evil.test")][InlineData("x.up.railway.app/path")][InlineData("x.up.railway.app\nHost:evil")][InlineData("127.0.0.1")]
    public void RejectOriginInjectionOrUnverifiedHosts(string value)=>Assert.Throws<InvalidOperationException>(()=>RailwayRuntime.PublicOrigin(value));
    [Fact]public void PrivateDatabaseCaRequiresFreshAuthenticatedExactHostChallenge()
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=Independent Synthetic Railway CA",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));using var ca=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));var pem=Encoding.UTF8.GetBytes(ca.ExportCertificatePem());var encoded=Convert.ToBase64String(pem);var secret=new string('a',64);var nonce=new string('b',64);const string host="postgres.railway.internal";
        string Sign(string h,string n,string c)=>Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes("aeroscience-railway-database-trust-v1\n"+h+"\n"+n+"\n"+c)));
        var signature=Sign(host,nonce,encoded);Assert.Equal(pem,RailwayRuntime.VerifyTrust(host,nonce,encoded,signature,secret));
        Assert.Throws<InvalidOperationException>(()=>RailwayRuntime.VerifyTrust(host,new string('c',64),encoded,signature,secret));Assert.Throws<InvalidOperationException>(()=>RailwayRuntime.VerifyTrust("different.railway.internal",nonce,encoded,signature,secret));Assert.Throws<InvalidOperationException>(()=>RailwayRuntime.VerifyTrust(host,nonce,encoded,signature,new string('z',64)));Assert.Throws<InvalidOperationException>(()=>RailwayRuntime.VerifyTrust("127.0.0.1",nonce,encoded,signature,secret));
    }
}
