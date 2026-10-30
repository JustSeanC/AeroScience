using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Organization;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public sealed partial class OrganizationConnectionIntegrationTests
{
    [PostgresFact]public async Task RealDesktopOwnerPairingKeepsPrivateKeyInVaultRequiresReviewAndResumesAfterRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"organization-owner-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var database="aeroscience_owner_"+Guid.NewGuid().ToString("N");var vault=new Vault();Server? server=null;
        try{
            var options=new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true};LocalOwnerPairing pairing;OrganizationOwnerChallenge challenge;
            using(var desktop=new OrganizationConnections(options,new Environment(),vault)){
                pairing=await desktop.CreateOwnerPairing(new("Independent Research Lab"),default);Assert.NotNull(vault.ReadOwnerKey(pairing.Id));Assert.DoesNotContain(vault.ReadOwnerKey(pairing.Id)!,File.ReadAllText(Path.Combine(root,"organization-owner-pairings.json")));
                var values=new Dictionary<string,string?>{["Organization:OwnerPublicKey"]=pairing.PublicKey,["Organization:OwnerIntentId"]=pairing.Id.ToString(),["Organization:OwnerProjectId"]=Guid.NewGuid().ToString(),["Organization:OwnerEnvironmentId"]=Guid.NewGuid().ToString(),["Organization:OwnerServiceId"]=Guid.NewGuid().ToString()};server=await Start(database,root,"Pending",settingsOverride:values,initializeOwner:false);
                var pending=await server.Post("/api/owner/setup",new OrganizationOwnerRequest("Independent Research Lab","Public-key ownership fixture","paired-admin","ResearchOnly!2026Flight"));challenge=pending.Deserialize<OrganizationOwnerChallenge>(WireJson.Options)!;
                await Assert.ThrowsAsync<ArgumentException>(()=>desktop.ApproveOwnerPairing(new(Guid.NewGuid(),true),default));
                await Assert.ThrowsAsync<ArgumentException>(()=>desktop.InspectOwnerPairing(new(Guid.NewGuid(),server.Origin,challenge.Id),default));
                var reviewed=JsonSerializer.SerializeToElement(await desktop.InspectOwnerPairing(new(pairing.Id,server.Origin,challenge.Id),default),WireJson.Options);var reviewId=reviewed.GetProperty("reviewId").GetGuid();await Assert.ThrowsAsync<ArgumentException>(()=>desktop.ApproveOwnerPairing(new(reviewId,false),default));
            }
            // Only public metadata and the OS-vault key survive; an abandoned view cannot approve.
            using(var restarted=new OrganizationConnections(options,new Environment(),vault)){
                var review=JsonSerializer.SerializeToElement(await restarted.InspectOwnerPairing(new(pairing.Id,server.Origin,challenge.Id),default),WireJson.Options);var result=JsonSerializer.SerializeToElement(await restarted.ApproveOwnerPairing(new(review.GetProperty("reviewId").GetGuid(),true),default),WireJson.Options);Assert.Equal("completed",result.GetProperty("state").GetString());Assert.Equal(server.Origin,Assert.Single(await restarted.OwnerPairings(default)).BoundOrigin);
                var signIn=await server.Post("/api/sign-in",new OrganizationLoginRequest("paired-admin","ResearchOnly!2026Flight"));var session=await server.Browser.GetFromJsonAsync<JsonElement>("/api/session");Assert.Equal("paired-admin",session.GetProperty("user").GetProperty("name").GetString());
            }
            Assert.DoesNotContain("ResearchOnly!2026Flight",await File.ReadAllTextAsync(Path.Combine(root,"organization-owner-pairings.json")));
        }finally{if(server is not null)await server.DisposeAsync();await DropMissionFixture(database,root);}
    }
}
