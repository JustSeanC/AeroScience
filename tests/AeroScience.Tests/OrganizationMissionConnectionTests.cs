using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    static OrganizationMissionSave MissionSave(OrganizationMissionPreview p)=>new(1,Guid.NewGuid(),p.ExistingPlan?p.Before.Id:null,p.PlanSha256,p.Assignment.IssuedSha256,p.Assignment.VerifiedClaimRevision,p.Changes.Select(c=>c.Id).ToArray(),"copy");
    static async Task<OrganizationClaimView> BrowserClaim(Server server,OrganizationOpportunitySnapshot card)=>
        (await server.Post("/api/membership/research/claim-command",new OrganizationClaimCommand(1,Guid.NewGuid(),card.OrganizationId,card.Id,card.Revision,OrganizationClaimAction.Claim))).Deserialize<OrganizationClaimView>(WireJson.Options)!;
    static async Task DropMissionFixture(string database,string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Npgsql.NpgsqlConnection.ClearAllPools();await using var c=new Npgsql.NpgsqlConnection(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));await c.OpenAsync();await using var cmd=new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)",c);await cmd.ExecuteNonQueryAsync();Directory.Delete(root,true);
    }
    [PostgresFact]public async Task AllEightActualPublishedClaimsPrepareRealDesktopPlansWithoutGivingReadOnlyCredentialsClaimWritePowers()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-mission-http-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var database="aeroscience_p16_"+Guid.NewGuid().ToString("N");Server? server=null;
        try{
            server=await Start(database,root,"Real Research Preparation Lab");var vault=new Vault();using var desktop=new OrganizationConnections(new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true},new Environment(),vault);var connection=await Connect(desktop,server);
            var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
            foreach(var template in AeroScience.Organization.OrganizationTemplates.All()){
                var card=await RealOpportunity(server,template.Reference.Id);var claim=await BrowserClaim(server,card);var preview=await desktop.PreviewMission(connection,claim.Id,new(),store,default);
                var save=MissionSave(preview);var prepared=await desktop.PrepareMission(connection,claim.Id,save,store,default);
                Assert.Equal(template.Reference.Id,prepared.Card.TemplateId);Assert.Equal(claim.Id,prepared.Card.OrganizationAssignment!.ClaimId);Assert.Equal(claim.IssuedSha256,prepared.Card.OrganizationAssignment.IssuedSha256);Assert.Equal(server.Origin,prepared.Card.OrganizationAssignment.Origin);Assert.False(prepared.Reused);
                Assert.True((await desktop.PrepareMission(connection,claim.Id,save,store,default)).Reused);
                Assert.DoesNotContain(vault.Read(connection)!,JsonSerializer.Serialize(prepared,WireJson.Options));
                await Assert.ThrowsAsync<IOException>(()=>desktop.ClaimCommand(connection,new(1,Guid.NewGuid(),card.OrganizationId,card.Id,card.Revision,OrganizationClaimAction.Release,claim.Id,claim.Revision),default));
            }
            Assert.Equal(8,store.OrganizationResearchCards().Length);Assert.Empty(store.List());Assert.False(Directory.Exists(Path.Combine(root,"ScienceLibrary")));
        }finally{if(server is not null)await server.DisposeAsync();await DropMissionFixture(database,root);}
    }
    [PostgresFact]public async Task ActualServerRevisionReleaseAndOfflineStateNeverReplacePreparedDefinitionsOrInventNewAuthorization()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-mission-boundary-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var database="aeroscience_p16_"+Guid.NewGuid().ToString("N");Server? server=null;
        try{
            server=await Start(database,root,"Frozen Research Lab");var card=await RealOpportunity(server);var claim=await BrowserClaim(server,card);var vault=new Vault();using var desktop=new OrganizationConnections(new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true},new Environment(),vault);var connection=await Connect(desktop,server);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
            var preview=await desktop.PreviewMission(connection,claim.Id,new(),store,default);var command=MissionSave(preview);var prepared=await desktop.PrepareMission(connection,claim.Id,command,store,default);var hash=PreflightReadiness.Hash(prepared.Card);
            var draft=await server.Post($"/api/organization/opportunities/{card.Id}/campaigns/{card.CampaignId}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),card.Revision,card.CampaignRevision,"New publication name",card.Description,"New published question",card.Instructions,card.Policy),HttpMethod.Put);
            await server.Post($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.GetProperty("revision").GetInt32()));
            var updated=await desktop.ReconcileMission(prepared.Card.Id,new(),store,default);Assert.True(updated.Original!.Claim.UpdateAvailable);Assert.Equal(claim.IssuedRevision,updated.Original.Claim.IssuedRevision);Assert.Equal(hash,PreflightReadiness.Hash(store.ResearchCard(prepared.Card.Id)));
            await server.Post("/api/membership/research/claim-command",new OrganizationClaimCommand(1,Guid.NewGuid(),card.OrganizationId,card.Id,claim.IssuedRevision,OrganizationClaimAction.Release,claim.Id,claim.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>desktop.PreviewMission(connection,claim.Id,new(),store,default));var check=await desktop.ReconcileMission(prepared.Card.Id,new(),store,default);Assert.Equal(OrganizationClaimState.Released,check.Original!.Claim.State);Assert.NotNull(check.Original.Claim.EndedUtc);Assert.Equal(hash,PreflightReadiness.Hash(store.ResearchCard(prepared.Card.Id)));
            await server.App.StopAsync();var offline=await desktop.ReconcileMission(prepared.Card.Id,new(),store,default);Assert.Null(offline.Original);Assert.Contains("Pending",offline.Status);Assert.True((await desktop.PrepareMission(connection,claim.Id,command,store,default)).Reused);await Assert.ThrowsAsync<IOException>(()=>desktop.PrepareMission(connection,claim.Id,command with{RequestId=Guid.NewGuid()},store,default));
            var restarted=new MissionStore(store.DatabasePath);restarted.Initialize();Assert.Equal(hash,PreflightReadiness.Hash(restarted.ResearchCard(prepared.Card.Id)));Assert.Equal("Organization Eligibility Pending Verification",restarted.LatestOrganizationClaimCheck(prepared.Card.Id)!.Status);Assert.Single(restarted.Plans());
        }finally{if(server is not null)await server.DisposeAsync();await DropMissionFixture(database,root);}
    }
    [PostgresFact]public async Task RealIndependentOrganizationConnectionsCannotPrepareOrReconcileEachOthersClaims()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-mission-isolation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var roots=new[]{Path.Combine(root,"one"),Path.Combine(root,"two")};foreach(var r in roots)Directory.CreateDirectory(r);var databases=new[]{"aeroscience_p16_"+Guid.NewGuid().ToString("N"),"aeroscience_p16_"+Guid.NewGuid().ToString("N")};var servers=new List<Server>();
        try{
            servers.Add(await Start(databases[0],roots[0],"Independent Lab One"));servers.Add(await Start(databases[1],roots[1],"Independent Lab Two"));var card=await RealOpportunity(servers[0]);var claim=await BrowserClaim(servers[0],card);var vault=new Vault();using var desktop=new OrganizationConnections(new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true},new Environment(),vault);var one=await Connect(desktop,servers[0]);var two=await Connect(desktop,servers[1]);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
            await Assert.ThrowsAsync<IOException>(()=>desktop.PreviewMission(two,claim.Id,new(),store,default));Assert.Empty(store.OrganizationResearchCards());
            var preview=await desktop.PreviewMission(one,claim.Id,new(),store,default);var prepared=await desktop.PrepareMission(one,claim.Id,MissionSave(preview),store,default);var hash=PreflightReadiness.Hash(prepared.Card);var before=servers[1].ClaimReads;await Assert.ThrowsAsync<ArgumentException>(()=>desktop.ReconcileMission(prepared.Card.Id,new(two),store,default));Assert.Equal(before,servers[1].ClaimReads);Assert.Equal(hash,PreflightReadiness.Hash(store.ResearchCard(prepared.Card.Id)));Assert.Equal(servers[0].Origin,prepared.Card.OrganizationAssignment!.Origin);Assert.Single(store.Plans());
        }finally{foreach(var s in servers)await s.DisposeAsync();for(var i=0;i<2;i++)await DropMissionFixture(databases[i],roots[i]);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
