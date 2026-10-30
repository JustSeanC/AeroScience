using System.Security.Cryptography;
using System.Text;

namespace AeroScience.Shared;

// Public, data-only pairing contract. No provider account token or server private key.
public sealed record OrganizationOwnerRequest(string OrganizationName,string Description,string UserName,string Password);
public sealed record OrganizationOwnerChallenge(Guid Id,Guid IntentId,string Origin,string ProjectId,string EnvironmentId,string ServiceId,string PublicKeySha256,string OrganizationName,string UserName,string PayloadSha256,string Nonce,DateTimeOffset ExpiresUtc,string State);
public sealed record OrganizationOwnerApproval(Guid Id,string Signature);
public static class OrganizationOwnerProtocol
{
    public static string SignedText(OrganizationOwnerChallenge c)=>string.Join('\n',"AeroScience.owner-pairing.v1",c.Id.ToString("D"),c.IntentId.ToString("D"),c.Origin,c.ProjectId,c.EnvironmentId,c.ServiceId,c.PublicKeySha256,c.OrganizationName,c.UserName,c.PayloadSha256,c.Nonce,c.ExpiresUtc.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string Fingerprint(string publicKey)=>Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKey)));
    public static bool ValidPublicKey(string? value)
    {
        if(value is null||value.Length is <100 or >200)return false;
        try{using var key=ECDsa.Create();var bytes=Convert.FromBase64String(value);key.ImportSubjectPublicKeyInfo(bytes,out var read);return read==bytes.Length&&key.KeySize==256&&key.ExportParameters(false).Curve.Oid.Value==ECCurve.NamedCurves.nistP256.Oid.Value;}catch(Exception e) when(e is FormatException or CryptographicException){return false;}
    }
    public static bool Verify(string publicKey,OrganizationOwnerChallenge challenge,string? signature)
    {
        if(!ValidPublicKey(publicKey)||signature?.Length!=88)return false;
        try{using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey),out _);return key.VerifyData(Encoding.UTF8.GetBytes(SignedText(challenge)),Convert.FromBase64String(signature),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation);}catch(Exception e) when(e is FormatException or CryptographicException){return false;}
    }
}
