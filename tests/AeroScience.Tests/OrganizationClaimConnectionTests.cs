using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
public sealed partial class OrganizationConnectionIntegrationTests
{
    static async Task<OrganizationOpportunitySnapshot> RealOpportunity(Server server)
    {
        var p=await server.Post("/api/organization/programs",new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Collaborative research",""));var program=p.GetProperty("id").GetGuid();
        var c=await server.Post($"/api/organization/programs/{program}/campaigns",new OrganizationCampaignRequest(1,Guid.NewGuid(),0,"Shared campaign","","Investigate altitude and temperature",OrganizationFocus.ScientificResearch,new("vertical-atmospheric-profile",1)));var campaign=c.GetProperty("id").GetGuid();await server.Post($"/api/organization/campaigns/{campaign}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1));
        var card=await server.Post($"/api/organization/opportunities/campaigns/{campaign}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,2,"Collaborative test card","","Investigate retained measurements","Do not upload data in P15",new(OrganizationParticipation.Exclusive,1,1,true,[OrganizationRoles.Administrator],"Future review required")));
        var published=await server.Post($"/api/organization/opportunities/{card.GetProperty("id").GetGuid()}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1));return published.Deserialize<OrganizationOpportunitySnapshot>(WireJson.Options)!;
    }
    [PostgresFact]public async Task RealDesktopRequiresNewClaimAuthorizationAndKeepsServerIdentityOfflineAndPersonalStorageIndependent()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-claims-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var database="aeroscience_p13_"+Guid.NewGuid().ToString("N");Server? server=null;
        try{
            server=await Start(database,root,"Real Collaborative Lab");var card=await RealOpportunity(server);var vault=new Vault();var options=new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true};
            using var desktop=new OrganizationConnections(options,new Environment(),vault);var read=await Connect(desktop,server);var command=new OrganizationClaimCommand(1,Guid.NewGuid(),card.OrganizationId,card.Id,card.Revision,OrganizationClaimAction.Claim);
            await Assert.ThrowsAsync<IOException>(()=>desktop.ClaimCommand(read,command,default));
            var review=await desktop.Review(server.Origin,default);var pending=await desktop.Begin(new(review.ReviewId,review.Organization.Id,true,OrganizationDesktopPermission.ManageOwnClaims),default);await server.Approve(pending);await desktop.Finish(pending.PendingId,default);
            var claim=await desktop.ClaimCommand(pending.PendingId,command,default);Assert.Equal(OrganizationClaimState.Claimed,claim.State);Assert.Equal(claim.Id,(await desktop.ClaimCommand(pending.PendingId,command,default)).Id);Assert.Equal(2,(await desktop.List(default)).Length);
            var cross=command with{RequestId=Guid.NewGuid(),OrganizationId=Guid.NewGuid()};await Assert.ThrowsAsync<ArgumentException>(()=>desktop.ClaimCommand(pending.PendingId,cross,default));
            await server.App.StopAsync();var offline=JsonSerializer.SerializeToElement(await desktop.Research(pending.PendingId,true,0,default),WireJson.Options);Assert.Contains("Offline",offline.GetProperty("status").GetString());Assert.Equal(JsonValueKind.Null,offline.GetProperty("page").ValueKind);
            await Assert.ThrowsAsync<IOException>(()=>desktop.ClaimCommand(pending.PendingId,command with{Action=OrganizationClaimAction.Release,RequestId=Guid.NewGuid(),ClaimId=claim.Id,ExpectedClaimRevision=claim.Revision},default));
            Assert.False(File.Exists(Path.Combine(root,"missions.db")));Assert.False(Directory.Exists(Path.Combine(root,"ScienceLibrary")));
            var metadata=await File.ReadAllTextAsync(Path.Combine(root,"organization-connections.json"));Assert.DoesNotContain(vault.Read(pending.PendingId)!,metadata);
        }finally{
            if(server is not null)await server.DisposeAsync();Npgsql.NpgsqlConnection.ClearAllPools();var admin=new Npgsql.NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));await using var c=new Npgsql.NpgsqlConnection(admin.ConnectionString);await c.OpenAsync();await using var cmd=new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)",c);await cmd.ExecuteNonQueryAsync();Directory.Delete(root,true);
        }
    }
}
