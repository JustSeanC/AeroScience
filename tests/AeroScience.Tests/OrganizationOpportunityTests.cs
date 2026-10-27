using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    static OrganizationClaimPolicy Exclusive(bool volunteers=true)=>new(OrganizationParticipation.Exclusive,1,1,volunteers,[OrganizationRoles.Pilot],"Future authorized evidence review required.");
    async Task<(OrganizationCampaignView Campaign,OrganizationOpportunitySnapshot Card)> Opportunity(OrganizationClaimPolicy? policy=null,bool publish=true,string template="vertical-atmospheric-profile")
    {
        var p=await Program();var c=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign() with{Template=new(template,1)}));c=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{c.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),c.Revision)));
        var card=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/campaigns/{c.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,c.Revision,"Pilot Research Opportunity","Published opportunity","Investigate the retained original measurements","Follow the issued scientific requirements",policy??Exclusive())));
        if(publish)card=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision)));return(c,card);
    }
    static OrganizationClaimCommand Claim(OrganizationOpportunitySnapshot card,OrganizationClaimAction action=OrganizationClaimAction.Claim,OrganizationClaimView? prior=null,Guid? pilot=null)=>new(1,Guid.NewGuid(),card.OrganizationId,card.Id,prior?.IssuedRevision??card.Revision,action,prior?.Id,prior?.Revision,pilot);
    async Task<OrganizationClaimView> PilotCommand(Browser pilot,OrganizationClaimCommand command)=>await Value<OrganizationClaimView>(await pilot.Send("/api/membership/research/claim-command",command));
    async Task<OrganizationPage<OrganizationOpportunityView>> Opportunities(Browser pilot)=>await Value<OrganizationPage<OrganizationOpportunityView>>(await pilot.Client.GetAsync("/api/membership/research/opportunities"));
    async Task<OrganizationPage<OrganizationClaimView>> MyClaims(Browser pilot)=>await Value<OrganizationPage<OrganizationClaimView>>(await pilot.Client.GetAsync("/api/membership/research/claims"));
    [PostgresFact]public async Task DraftsArePrivateAndAllEightCampaignDefinitionsPublishWithoutSubstitution()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));
        foreach(var template in OrganizationTemplates.All()){
            var (c,draft)=await Opportunity(publish:false,template:template.Reference.Id);Assert.DoesNotContain((await Opportunities(pilot)).Items,i=>i.Card.Id==draft.Id);
            Assert.Equal(HttpStatusCode.NotFound,(await pilot.Send("/api/membership/research/claim-command",Claim(draft))).StatusCode);
            var pub=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision)));Assert.Equal(JsonSerializer.Serialize(c.Definition,WireJson.Options),JsonSerializer.Serialize(pub.Definition,WireJson.Options));
            Assert.Equal(OrganizationPublication.Published,pub.Status);Assert.Equal(c.Revision,pub.CampaignRevision);
        }
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/organization/opportunities")).StatusCode);
    }
    [PostgresFact]public async Task ConcurrentExclusiveClaimsHaveOneWinnerAndDoNotLeakAnotherPilotsHistory()
    {
        await Bootstrap();await Login();var (_,card)=await Opportunity();using var a=await JoinPilot("first",InvitationSecret(await Invite()));using var b=await JoinPilot("second",InvitationSecret(await Invite()));
        var responses=await Task.WhenAll(a.Send("/api/membership/research/claim-command",Claim(card)),b.Send("/api/membership/research/claim-command",Claim(card)));Assert.Single(responses,r=>r.IsSuccessStatusCode);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Conflict);
        var ac=await MyClaims(a);var bc=await MyClaims(b);Assert.Equal(1,ac.Items.Length+bc.Items.Length);Assert.All(ac.Items.Concat(bc.Items),c=>Assert.Null(c.PilotName));
        var publicList=await Opportunities(a);Assert.Equal(0,Assert.Single(publicList.Items).AvailableSlots);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(1,await db.Claims.CountAsync());Assert.Equal(1,await db.ClaimEvents.CountAsync());
    }
    [PostgresFact]public async Task ReservationsRequireAcceptanceAndReleaseReassignmentNeverReusesOriginalIdentity()
    {
        await Bootstrap();await Login();var (_,card)=await Opportunity(Exclusive(false));using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));using var other=await JoinPilot("other",InvitationSecret(await Invite()));var target=await Member(pilot);
        var reserved=await Value<OrganizationClaimView>(await browser.Send("/api/organization/opportunities/claim-command",Claim(card,OrganizationClaimAction.Assign,pilot:target.UserId)));Assert.Equal(OrganizationClaimState.Reserved,reserved.State);Assert.Null(reserved.AcceptedUtc);
        Assert.Equal(HttpStatusCode.Forbidden,(await other.Send("/api/membership/research/claim-command",Claim(card))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await other.Send("/api/membership/research/claim-command",Claim(card,OrganizationClaimAction.Accept,reserved))).StatusCode);
        var accepted=await PilotCommand(pilot,Claim(card,OrganizationClaimAction.Accept,reserved));Assert.Equal(OrganizationClaimState.Claimed,accepted.State);Assert.NotNull(accepted.AcceptedUtc);
        var released=await PilotCommand(pilot,Claim(card,OrganizationClaimAction.Release,accepted));Assert.Equal(OrganizationClaimState.Released,released.State);Assert.NotNull(released.EndedUtc);
        var second=await Value<OrganizationClaimView>(await browser.Send("/api/organization/opportunities/claim-command",Claim(card,OrganizationClaimAction.Assign,pilot:(await Member(other)).UserId)));Assert.NotEqual(reserved.Id,second.Id);Assert.NotEqual(reserved.PilotId,second.PilotId);Assert.Single((await MyClaims(pilot)).Items);
        var events=await Value<OrganizationClaimEventPage>(await browser.Client.GetAsync($"/api/organization/opportunities/claims/{reserved.Id}/events"));Assert.Equal(new[]{"Assign","Accept","Release"},events.Items.Select(e=>e.Action));
    }
    [PostgresFact]public async Task MultiPilotClaimsEnforceIndependentCapacityIdempotencyAndStaleCommandRules()
    {
        await Bootstrap();await Login();var policy=new OrganizationClaimPolicy(OrganizationParticipation.MultiPilot,2,2,true,[OrganizationRoles.Pilot],"Pending future review");var (_,card)=await Opportunity(policy);using var a=await JoinPilot("first",InvitationSecret(await Invite()));using var b=await JoinPilot("second",InvitationSecret(await Invite()));using var third=await JoinPilot("third",InvitationSecret(await Invite()));
        var request=Claim(card);var responses=await Task.WhenAll(a.Send("/api/membership/research/claim-command",request),a.Send("/api/membership/research/claim-command",request));var first=await Value<OrganizationClaimView>(responses[0]);Assert.Equal(first.Id,(await Value<OrganizationClaimView>(responses[1])).Id);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Send("/api/membership/research/claim-command",Claim(card))).StatusCode);var second=await PilotCommand(b,Claim(card));Assert.NotEqual(first.PilotId,second.PilotId);
        Assert.Equal(HttpStatusCode.Conflict,(await third.Send("/api/membership/research/claim-command",Claim(card))).StatusCode);
        var release=Claim(card,OrganizationClaimAction.Release,first);var released=await PilotCommand(a,release);Assert.Equal(released.Id,(await PilotCommand(a,release)).Id);Assert.Equal(HttpStatusCode.Conflict,(await a.Send("/api/membership/research/claim-command",release with{RequestId=Guid.NewGuid()})).StatusCode);
        await PilotCommand(third,Claim(card));Assert.Equal(0,Assert.Single((await Opportunities(b)).Items).AvailableSlots);Assert.Contains("Pending / Unverified",first.EvidenceStatus);
    }
    [PostgresFact]public async Task PublishedUpdatesFreezeIssuedRequirementsAndSuspensionNeverSilentlyFreesSlots()
    {
        await Bootstrap();await Login();var (campaign,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var claim=await PilotCommand(pilot,Claim(card));
        var draft=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/{card.Id}/campaigns/{campaign.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),card.Revision,campaign.Revision,"Updated card","New description","A new question","New instructions",Exclusive()),"PUT"));var pub=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision)));
        var retained=Assert.Single((await MyClaims(pilot)).Items);Assert.True(retained.UpdateAvailable);Assert.Equal(claim.IssuedRevision,retained.IssuedRevision);Assert.Equal(card.Objective,retained.IssuedCard.Objective);Assert.Equal(pub.Revision,retained.LatestPublishedRevision);
        var page=await Value<OrganizationPage<OrganizationOpportunityView>>(await browser.Client.GetAsync("/api/organization/opportunities"));var view=Assert.Single(page.Items);await Value<JsonElement>(await browser.Send($"/api/organization/opportunities/{card.Id}/availability",new OrganizationOpportunityAvailability(1,Guid.NewGuid(),view.AvailabilityRevision,true)));
        await Change(await Member(pilot),state:OrganizationMembershipState.Suspended);using var other=await JoinPilot("other",InvitationSecret(await Invite()));Assert.Equal(HttpStatusCode.Conflict,(await other.Send("/api/membership/research/claim-command",Claim(pub))).StatusCode);
        var manager=await Value<OrganizationPage<OrganizationClaimView>>(await browser.Client.GetAsync("/api/organization/opportunities/claims"));Assert.Contains("Unavailable",Assert.Single(manager.Items).Availability);
        var released=await Value<OrganizationClaimView>(await browser.Send("/api/organization/opportunities/claim-command",Claim(card,OrganizationClaimAction.ManagerRelease,claim)));Assert.Equal(OrganizationClaimState.Released,released.State);
    }
    [PostgresFact]public async Task ReadOnlyDevicesCannotAcquireClaimScopeWithoutNewExplicitBrowserConsent()
    {
        await Bootstrap();await Login();var (_,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var read=await Approved(pilot);
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);native.DefaultRequestHeaders.Authorization=new("Bearer",read.Credential);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/member/claim-command",Claim(card),WireJson.Options)).StatusCode);
        var identity=await Identity();var verifier=new string('b',64);var challenge=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        var device=await Value<OrganizationDevicePending>(await native.PostAsJsonAsync("/api/device/start",new OrganizationDeviceStart(identity.Id,"Scoped claim desktop",challenge,OrganizationDesktopPermission.ManageOwnClaims),WireJson.Options));
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,identity.Id,true,true))).StatusCode);
        await Value<JsonElement>(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,identity.Id,true,true,true)));var token=await Poll(device,verifier);Assert.Equal(OrganizationDesktopPermission.ManageOwnClaims,token.Permission);
        native.DefaultRequestHeaders.Authorization=new("Bearer",token.Credential);var claimed=await Value<OrganizationClaimView>(await native.PostAsJsonAsync("/api/member/claim-command",Claim(card),WireJson.Options));Assert.Equal(OrganizationClaimState.Claimed,claimed.State);
        Assert.Equal(HttpStatusCode.Unauthorized,(await native.GetAsync("/api/organization/opportunities")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/member/claim-command",Claim(card) with{PilotId=Guid.NewGuid()},WireJson.Options)).StatusCode);
        await Change(await Member(pilot),state:OrganizationMembershipState.Removed);Assert.Equal(HttpStatusCode.Unauthorized,(await native.PostAsJsonAsync("/api/member/claim-command",Claim(card,OrganizationClaimAction.Release,claimed),WireJson.Options)).StatusCode);
    }
    [PostgresFact]public async Task ClaimHistorySurvivesRestartAndInvalidPoliciesAndCrossOrganizationCommandsFailClosed()
    {
        await Bootstrap();await Login();var (campaign,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var c=await PilotCommand(pilot,Claim(card));
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/membership/research/claim-command",Claim(card) with{OrganizationId=Guid.NewGuid()})).StatusCode);
        var bad=new OrganizationOpportunityRequest(1,Guid.NewGuid(),card.Revision,campaign.Revision,"Bad policy","","Question","",new(OrganizationParticipation.MultiPilot,3,2,true,[OrganizationRoles.Pilot],""));Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send($"/api/organization/opportunities/{card.Id}/campaigns/{campaign.Id}",bad,"PUT")).StatusCode);
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);pilot.Connect(app);var retained=Assert.Single((await MyClaims(pilot)).Items);Assert.Equal(c.Id,retained.Id);Assert.Equal(c.IssuedSha256,retained.IssuedSha256);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var original=await db.Claims.FirstAsync();original.IssuedRevision++;await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());db.Entry(original).State=EntityState.Detached;var e=await db.ClaimEvents.FirstAsync();e.Action="Rewrite history";await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());
    }
    [PostgresFact]public async Task ManagersPublishWithinTheirRoleAndReleasedHistoryUsesCompletePagedInventory()
    {
        await Bootstrap();await Login();var (campaign,card)=await Opportunity();using var manager=await JoinPilot("manager",InvitationSecret(await Invite()));await Change(await Member(manager),OrganizationRoles.Manager);await Login(manager,"manager");
        var draft=await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/campaigns/{campaign.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,campaign.Revision,"Manager opportunity","","A scientific question","",Exclusive())));
        await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision)));
        using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision))).StatusCode);
        for(var i=0;i<12;i++){var held=await PilotCommand(pilot,Claim(card));await PilotCommand(pilot,Claim(card,OrganizationClaimAction.Release,held));}
        var first=await MyClaims(pilot);var second=await Value<OrganizationPage<OrganizationClaimView>>(await pilot.Client.GetAsync("/api/membership/research/claims?offset="+first.NextOffset));Assert.Equal(10,first.Items.Length);Assert.Equal(2,second.Items.Length);Assert.Null(second.NextOffset);Assert.Empty(first.Items.Select(x=>x.Id).Intersect(second.Items.Select(x=>x.Id)));Assert.All(first.Items.Concat(second.Items),c=>Assert.Equal(OrganizationClaimState.Released,c.State));
        var claims=await Value<OrganizationPage<OrganizationClaimView>>(await manager.Client.GetAsync("/api/organization/opportunities/claims"));Assert.All(claims.Items,c=>Assert.Equal("pilot",c.PilotName));
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/membership/research/claim-command",Claim(card,OrganizationClaimAction.Assign,pilot:(await Member(manager)).UserId))).StatusCode);
    }

    [PostgresFact]public Task ClaimRevalidatesCommittedRevocationAfterWaitingForItsTransactionLock()=>RevocationAfterWaiting(false);
    [PostgresFact]public Task ClaimReceiptReplayRevalidatesCommittedRevocationAfterWaitingForItsRequestLock()=>RevocationAfterWaiting(true);
    async Task RevocationAfterWaiting(bool replay)
    {
        await Bootstrap();await Login();var (_,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));
        var verifier=new string('c',64);var challenge=Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);
        var device=await Value<OrganizationDevicePending>(await native.PostAsJsonAsync("/api/device/start",new OrganizationDeviceStart(card.OrganizationId,"Revocation race fixture",challenge,OrganizationDesktopPermission.ManageOwnClaims),WireJson.Options));await Value<JsonElement>(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,card.OrganizationId,true,true,true)));var granted=await Poll(device,verifier);native.DefaultRequestHeaders.Authorization=new("Bearer",granted.Credential);
        var command=Claim(card);long key=7014001; // Existing server membership/revocation lock.
        if(replay){await Value<OrganizationClaimView>(await native.PostAsJsonAsync("/api/member/claim-command",command,WireJson.Options));key=System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((await Member(pilot)).UserId.ToString()+command.RequestId)));}
        await using var holder=new Npgsql.NpgsqlConnection(connection);await holder.OpenAsync();await using var held=await holder.BeginTransactionAsync();await using(var cmd=new Npgsql.NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)",holder,held)){cmd.Parameters.AddWithValue("key",key);await cmd.ExecuteNonQueryAsync();}
        var attempted=native.PostAsJsonAsync("/api/member/claim-command",command,WireJson.Options);
        // Observe the real PostgreSQL wait, proving the first authorization has completed.
        await using var observer=new Npgsql.NpgsqlConnection(connection);await observer.OpenAsync();var waiting=false;var deadline=DateTime.UtcNow.AddSeconds(10);
        while(DateTime.UtcNow<deadline){await using var check=new Npgsql.NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory' AND query LIKE '%pg_advisory_xact_lock%')",observer);waiting=(bool)(await check.ExecuteScalarAsync())!;if(waiting)break;await Task.Delay(20);}
        Assert.True(waiting,"The controlled command must reach the real membership transaction lock.");
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.DeviceGrants.Where(g=>g.Id==device.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true));}
        await held.CommitAsync();Assert.Equal(HttpStatusCode.Unauthorized,(await attempted).StatusCode);
        using var final=app.Services.CreateScope();Assert.Equal(replay?1:0,await final.ServiceProvider.GetRequiredService<OrganizationDatabase>().Claims.CountAsync());
    }

}
