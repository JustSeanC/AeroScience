using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using AeroScience.Missions;

namespace AeroScience.Engine;

public sealed record LocalOwnerPairing(Guid Id,string Name,string PublicKey,DateTimeOffset CreatedUtc,string? BoundOrigin=null,string? BoundProject=null,string? BoundEnvironment=null,string? BoundService=null);
public sealed record OwnerPairingCreate(string Name);
public sealed record OwnerPairingInspect(Guid IntentId,string Address,Guid RequestId);
public sealed record OwnerPairingConfirm(Guid ReviewId,bool Confirm);
public sealed partial class OrganizationConnections
{
    static bool CanonicalDeploymentId(string value)=>Guid.TryParseExact(value,"D",out var id)&&id!=Guid.Empty&&value==id.ToString("D");
    string OwnerFile=>Path.Combine(Path.GetDirectoryName(file)!,"organization-owner-pairings.json");
    readonly Dictionary<Guid,OrganizationOwnerChallenge> ownerReviews=[];
    LocalOwnerPairing[] ReadOwners()=>File.Exists(OwnerFile)?JsonSerializer.Deserialize<LocalOwnerPairing[]>(File.ReadAllBytes(OwnerFile),WireJson.Options)??[]:[];
    public async Task<LocalOwnerPairing[]> OwnerPairings(CancellationToken ct){await gate.WaitAsync(ct);try{return ReadOwners();}finally{gate.Release();}}
    public async Task<LocalOwnerPairing> CreateOwnerPairing(OwnerPairingCreate request,CancellationToken ct)
    {
        if(!vault.OwnerKeyAvailable)throw new IOException("Create the ownership key on your companion PC with protected credential storage.");
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>120||request.Name.Any(char.IsControl))throw new ArgumentException("Enter a short name for the server you want to create.");
        await gate.WaitAsync(ct);try{
            using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);var row=new LocalOwnerPairing(Guid.NewGuid(),request.Name.Trim(),Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),DateTimeOffset.UtcNow);
            var privateBytes=key.ExportPkcs8PrivateKey();try{vault.SaveOwnerKey(row.Id,Convert.ToBase64String(privateBytes));PreferenceStore.WriteAtomic(OwnerFile,ReadOwners().Append(row).ToArray());}finally{CryptographicOperations.ZeroMemory(privateBytes);}return row;
        }finally{gate.Release();}
    }
    public async Task<object> InspectOwnerPairing(OwnerPairingInspect request,CancellationToken ct)
    {
        var origin=Origin(request.Address,localDevelopment).GetLeftPart(UriPartial.Authority);
        var c=await Request<OrganizationOwnerChallenge>(origin,"/api/owner/setup/"+request.RequestId,null,ct);
        await gate.WaitAsync(ct);try{
            var pairing=ReadOwners().SingleOrDefault(p=>p.Id==request.IntentId)??throw new ArgumentException("Choose the ownership key created for this server.");
            if(c.Id!=request.RequestId||c.IntentId!=pairing.Id||c.Origin!=origin||c.PublicKeySha256!=OrganizationOwnerProtocol.Fingerprint(pairing.PublicKey)||c.State!="pending"||c.ExpiresUtc<=DateTimeOffset.UtcNow||c.ExpiresUtc>DateTimeOffset.UtcNow.AddMinutes(6)||!CanonicalDeploymentId(c.ProjectId)||!CanonicalDeploymentId(c.EnvironmentId)||!CanonicalDeploymentId(c.ServiceId)||c.Nonce is not{Length:64}||c.Nonce.Any(v=>!char.IsAsciiHexDigitLower(v))||c.PayloadSha256 is not{Length:64}||c.PayloadSha256.Any(v=>!char.IsAsciiHexDigitLower(v))||c.OrganizationName is not{Length:>=1 and <=120}||c.OrganizationName.Any(char.IsControl)||c.UserName is not{Length:>=1 and <=64}||c.UserName.Any(v=>!char.IsAsciiLetterOrDigit(v)&&v is not('.' or '_' or '-' or '@')))throw new IOException("This setup request does not match this server's exact ownership key and deployment. Nothing was approved.");
            if(pairing.BoundOrigin is not null&&(pairing.BoundOrigin!=origin||pairing.BoundProject!=c.ProjectId||pairing.BoundEnvironment!=c.EnvironmentId||pairing.BoundService!=c.ServiceId))throw new IOException("This ownership key is already bound to a different deployment. Create a new key for a different server.");
            foreach(var expired in ownerReviews.Where(v=>v.Value.ExpiresUtc<=DateTimeOffset.UtcNow).Select(v=>v.Key).ToArray())ownerReviews.Remove(expired);
            if(ownerReviews.Count>=8)throw new IOException("Finish an earlier ownership review first.");var id=Guid.NewGuid();ownerReviews[id]=c;return new{reviewId=id,challenge=c};
        }finally{gate.Release();}
    }
    public async Task<object> ApproveOwnerPairing(OwnerPairingConfirm request,CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{
            if(!request.Confirm||!ownerReviews.TryGetValue(request.ReviewId,out var c)||c.ExpiresUtc<=DateTimeOffset.UtcNow)throw new ArgumentException("Review the exact organization and matching setup request before approving ownership.");
            var rows=ReadOwners();var pairing=rows.Single(p=>p.Id==c.IntentId);
            var bytes=Convert.FromBase64String(vault.ReadOwnerKey(pairing.Id)??throw new IOException("The protected ownership key is unavailable. No administrator was created."));
            try{
                using var key=ECDsa.Create();key.ImportPkcs8PrivateKey(bytes,out _);
                if(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())!=pairing.PublicKey)throw new IOException("The protected ownership key does not match its public identity.");
                // Persist the deployment binding before sending, including a lost acknowledgement.
                if(pairing.BoundOrigin is not null&&(pairing.BoundOrigin!=c.Origin||pairing.BoundProject!=c.ProjectId||pairing.BoundEnvironment!=c.EnvironmentId||pairing.BoundService!=c.ServiceId))throw new IOException("This key is bound to another deployment.");
                PreferenceStore.WriteAtomic(OwnerFile,rows.Select(p=>p.Id==pairing.Id?p with{BoundOrigin=c.Origin,BoundProject=c.ProjectId,BoundEnvironment=c.EnvironmentId,BoundService=c.ServiceId}:p).ToArray());
                var signature=Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(OrganizationOwnerProtocol.SignedText(c)),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
                var answer=await Request<JsonElement>(c.Origin,"/api/owner/approve",new OrganizationOwnerApproval(c.Id,signature),ct);ownerReviews.Remove(request.ReviewId);return answer;
            }finally{CryptographicOperations.ZeroMemory(bytes);}
        }finally{gate.Release();}
    }
}
