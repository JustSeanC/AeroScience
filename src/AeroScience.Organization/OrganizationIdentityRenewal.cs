using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroScience.Organization;

// Run only on a verified isolated recovery candidate. The live key ring is never edited.
public static class OrganizationIdentityRenewal
{
    internal static async Task<X509Certificate2> Renew(OrganizationDatabase candidate,X509Certificate2 original,CancellationToken ct)
    {
        using var rsa=RSA.Create(3072);var request=new CertificateRequest("CN=AeroScience Organization Data Protection",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var renewed=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
        try{
            var services=new ServiceCollection();services.AddLogging();services.AddDataProtection().UnprotectKeysWithAnyCertificate(original);await using var provider=services.BuildServiceProvider();var decryptor=new EncryptedXmlDecryptor(provider);var encryptor=new CertificateXmlEncryptor(renewed,NullLoggerFactory.Instance);
            var last=0;var count=0;
            while(true){
                var rows=await candidate.DataProtectionKeys.Where(k=>k.Id>last).OrderBy(k=>k.Id).Take(100).ToArrayAsync(ct);if(rows.Length==0)break;
                foreach(var row in rows){
                    if(row.Xml is null||row.Xml.Length>65536)throw new InvalidOperationException("The protected identity key has an unsupported format.");
                    using var reader=XmlReader.Create(new StringReader(row.Xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});var xml=XElement.Load(reader);
                    var encrypted=xml.Descendants(XName.Get("encryptedSecret","http://schemas.asp.net/2015/03/dataProtection")).ToArray();
                    if(encrypted.Length!=1||encrypted[0].Elements().Count()!=1||(string?)encrypted[0].Attribute("decryptorType")!=typeof(EncryptedXmlDecryptor).AssemblyQualifiedName)throw new InvalidOperationException("Only the established protected certificate key format can be renewed.");
                    var plaintext=decryptor.Decrypt(encrypted[0].Elements().Single());var protectedXml=encryptor.Encrypt(plaintext);encrypted[0].ReplaceNodes(protectedXml.EncryptedElement);encrypted[0].SetAttributeValue("decryptorType",protectedXml.DecryptorType.AssemblyQualifiedName);row.Xml=xml.ToString(SaveOptions.DisableFormatting);last=row.Id;count++;
                }
                await candidate.SaveChangesAsync(ct);candidate.ChangeTracker.Clear();
            }
            if(count==0)throw new InvalidOperationException("No original protected identity keys were available to renew.");
            await candidate.DeploymentState.ExecuteUpdateAsync(s=>s.SetProperty(d=>d.CertificateSha256,OrganizationDeployment.Digest(renewed.RawData)),ct);
            return renewed;
        }catch{renewed.Dispose();throw;}
    }
}
