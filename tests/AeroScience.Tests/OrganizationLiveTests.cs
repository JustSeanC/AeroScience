using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;

public sealed class OrganizationLiveContractTests
{
    internal static LiveFlightUpdate Update(Guid session,DateTimeOffset now,long sequence=1)=>new(1,session,1,sequence,now,TelemetrySource.Simulated,LiveSourceState.Connected,"Synthetic aircraft",41.25,-71.75,180,3000,120,[],new(null,CollectionPhase.Armed,[new("ambient-temperature",SensorOperatingState.CannotAssess,SensorScientificDataState.Unknown)]));
    [Fact]public void LiveContractAcceptsOnlyBoundedSupportedCurrentStateWithoutInventedSignals()
    {
        var original=Update(Guid.NewGuid(),DateTimeOffset.UtcNow);OrganizationLiveFlights.ValidateUpdate(original);
        foreach(var invalid in new[]{original with{Latitude=91},original with{IndicatedAirspeedKnots=-1},original with{Latitude=null},original with{Sequence=0},original with{State=LiveSourceState.Unavailable},original with{ConsentRevision=2},original with{Aircraft=new string('x',121)},original with{Science=new(null,null,[new("unsupported-mach-sensor",SensorOperatingState.CannotAssess,SensorScientificDataState.Unknown)])},original with{Cards=null!},original with{Source=(TelemetrySource)999}})Assert.Throws<ArgumentException>(()=>OrganizationLiveFlights.ValidateUpdate(invalid));
        OrganizationLiveFlights.ValidateUpdate(original with{State=LiveSourceState.Unavailable,Latitude=null,Longitude=null,HeadingDegrees=null,AltitudeMslFeet=null,IndicatedAirspeedKnots=null,Source=null,SourceUtc=null});
    }
    [Fact]public void LiveMetadataModelMatchesItsVersionedPostgreSqlMigration(){using var db=new OrganizationDesignTime().CreateDbContext([]);Assert.False(db.Database.HasPendingModelChanges());}
    [Fact]public void ConsentModelHasNoCoordinateArchiveAndScopeValuesRemainBackwardCompatible()
    {
        Assert.Equal(0,(int)OrganizationDesktopPermission.ReadPublished);Assert.Equal(1,(int)OrganizationDesktopPermission.ManageOwnClaims);Assert.Equal(2,(int)OrganizationDesktopPermission.SubmitEvidence);
        var names=typeof(LiveConsentRecord).GetProperties().Select(p=>p.Name).ToArray();Assert.DoesNotContain("Latitude",names);Assert.DoesNotContain("PayloadJson",names);Assert.DoesNotContain("Aircraft",names);
        Assert.Equal(64,OrganizationLiveProtocol.MaximumFlights);Assert.Equal(30,OrganizationLiveProtocol.PositionLifetimeSeconds);Assert.Equal(5,OrganizationLiveProtocol.UpdateSeconds);
    }
}

public sealed partial class OrganizationServerTests
{
    sealed class LiveClock:TimeProvider
    {
        DateTimeOffset now=DateTimeOffset.UtcNow;public override DateTimeOffset GetUtcNow()=>now;
        public void Advance(double seconds)=>now=now.AddSeconds(seconds);
    }
    LiveClock? liveClock;
    async Task ClockForLive()
    {
        await app.StopAsync();await app.DisposeAsync();liveClock=new();await Start();browser.Connect(app);await browser.Session();
    }
    DateTimeOffset LiveNow=>app.Services.GetRequiredService<OrganizationLiveRuntime>().UtcNow;
    async Task<OrganizationDeviceToken> LiveGrant(Browser pilot,OrganizationDesktopPermission permission=OrganizationDesktopPermission.ShareLiveFlight)
    {
        var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);var identity=await Identity();
        var pending=await Value<OrganizationDevicePending>(await native.PostAsJsonAsync("/api/device/start",new OrganizationDeviceStart(identity.Id,"Live consent fixture",challenge,permission),WireJson.Options));
        if(permission==OrganizationDesktopPermission.ShareLiveFlight)Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(pending.Id,identity.Id,true,true))).StatusCode);
        await Value<JsonElement>(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(pending.Id,identity.Id,true,true,permission==OrganizationDesktopPermission.ManageOwnClaims,permission==OrganizationDesktopPermission.SubmitEvidence,permission==OrganizationDesktopPermission.ShareLiveFlight)));
        return await Poll(pending,verifier);
    }
    async Task<HttpResponseMessage> LiveRequest(string path,OrganizationDeviceToken grant,object? body=null)
    {
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);native.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",grant.Credential);
        return body is null?await native.GetAsync(path):await native.PostAsJsonAsync(path,body,WireJson.Options);
    }
    async Task<LiveSharingStart> LiveStartRequest(string name="Pilot public label",Guid[]? claims=null,int revision=0)=>new(1,Guid.NewGuid(),(await Identity()).Id,Guid.NewGuid(),LiveNow.AddSeconds(-5),revision,name,claims??[],true);
    async Task<LiveSharingSession> StartLive(OrganizationDeviceToken grant,string name="Public research flight",Guid[]? claims=null)=>await Value<LiveSharingSession>(await LiveRequest("/api/member/live/start",grant,await LiveStartRequest(name,claims)));
    async Task<LiveDashboard> Dashboard(Browser? viewer=null)=>await Value<LiveDashboard>(await (viewer??browser).Client.GetAsync("/api/membership/live"));

    [PostgresFact]public async Task LiveSharingIsOffByDefaultAndRequiresSeparateScopedGrantAndExactExplicitConsent()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));Assert.Empty((await Dashboard(pilot)).Flights);
        var read=await Approved(pilot);Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live",read)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",read,await LiveStartRequest())).StatusCode);
        foreach(var scope in new[]{OrganizationDesktopPermission.ManageOwnClaims,OrganizationDesktopPermission.SubmitEvidence}){var token=await LiveGrant(pilot,scope);Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",token,await LiveStartRequest())).StatusCode);}
        var grant=await LiveGrant(pilot);var request=await LiveStartRequest();
        Assert.Equal(HttpStatusCode.BadRequest,(await LiveRequest("/api/member/live/start",grant,request with{ConfirmAllActiveMembers=false})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",grant,request with{OrganizationId=Guid.NewGuid()})).StatusCode);
        var session=await Value<LiveSharingSession>(await LiveRequest("/api/member/live/start",grant,request));Assert.Equal(session,await Value<LiveSharingSession>(await LiveRequest("/api/member/live/start",grant,request)));
        Assert.Equal(HttpStatusCode.Conflict,(await LiveRequest("/api/member/live/start",grant,request with{DisplayLabel="Changed consent"})).StatusCode);
        Assert.Null(Assert.Single((await Dashboard()).Flights).Current);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/member/live/update",OrganizationLiveContractTests.Update(session.Id,LiveNow))).StatusCode);
        await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow)));
        Assert.Equal(41.25,Assert.Single((await Dashboard()).Flights).Current!.Latitude);
    }
    [PostgresFact]public async Task LiveDashboardSharesTwoIndependentAircraftWithAllActiveRolesAndNoUnapprovedEvidence()
    {
        await Bootstrap();await Login();using var a=await JoinPilot("pilot-a",InvitationSecret(await Invite()));using var b=await JoinPilot("pilot-b",InvitationSecret(await Invite()));using var viewer=await JoinPilot("watcher",InvitationSecret(await Invite()));
        var ga=await LiveGrant(a);var gb=await LiveGrant(b);var sa=await StartLive(ga,"Aircraft A");var sb=await StartLive(gb,"Aircraft B");
        await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",ga,OrganizationLiveContractTests.Update(sa.Id,LiveNow)));
        await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",gb,OrganizationLiveContractTests.Update(sb.Id,LiveNow) with{Latitude=42.5,Longitude=-70.5,AltitudeMslFeet=5500}));
        Assert.Equal(2,(await Dashboard(viewer)).Flights.Length);Assert.Equal(2,(await Dashboard()).Flights.Length);
        foreach(var role in new[]{OrganizationRoles.Reviewer,OrganizationRoles.Manager}){await Change(await Member(viewer),role);await Login(viewer,"watcher");var v=await Dashboard(viewer);Assert.Equal(new[]{41.25,42.5},v.Flights.Select(x=>x.Current!.Latitude!.Value).Order().ToArray());Assert.All(v.Flights,f=>Assert.Empty(f.Cards));}
        using var anonymous=app.GetTestClient();anonymous.BaseAddress=new(Origin);Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/membership/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await a.Client.GetAsync("/api/organization/submissions")).StatusCode);
    }
    [PostgresFact]public async Task LiveUpdateRetriesCoalesceAndOldOrInterruptedSignalsNeverBecomeCurrentPositions()
    {
        await ClockForLive();await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var session=await StartLive(grant);var update=OrganizationLiveContractTests.Update(session.Id,LiveNow);
        var receipt=await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update));Assert.Equal(receipt,await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update)));
        Assert.Equal(HttpStatusCode.Conflict,(await LiveRequest("/api/member/live/update",grant,update with{Latitude=40})).StatusCode);
        Assert.False((await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update with{Sequence=2}))).Applied);
        liveClock!.Advance(5);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update with{Sequence=3,SourceUtc=LiveNow,State=LiveSourceState.Paused,Latitude=null,Longitude=null,AltitudeMslFeet=null,IndicatedAirspeedKnots=null,HeadingDegrees=null}));Assert.Null(Assert.Single((await Dashboard()).Flights).Current!.Latitude);
        liveClock.Advance(5);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update with{Sequence=4,SourceUtc=LiveNow.AddSeconds(-40)}));Assert.Equal(LiveSourceState.Stale,Assert.Single((await Dashboard()).Flights).Current!.State);Assert.Null(Assert.Single((await Dashboard()).Flights).Current!.AltitudeMslFeet);
        liveClock.Advance(5);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update with{Sequence=5,SourceUtc=LiveNow.AddMinutes(1)}));Assert.Null(Assert.Single((await Dashboard()).Flights).Current!.Latitude);
        var unknown=JsonSerializer.SerializeToNode(update,WireJson.Options)!;unknown["privateNotes"]="NEVER SHARE";Assert.Equal(HttpStatusCode.BadRequest,(await LiveRequest("/api/member/live/update",grant,unknown)).StatusCode);
    }
    [PostgresFact]public async Task LivePositionsExpireWithoutPersistenceAndConsentsDoNotReviveAfterServerRestart()
    {
        await ClockForLive();await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var session=await StartLive(grant);
        await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow)));
        liveClock!.Advance(16);Assert.True(Assert.Single((await Dashboard()).Flights).Stale);liveClock.Advance(15);Assert.Null(Assert.Single((await Dashboard()).Flights).Current);
        Assert.Equal(HttpStatusCode.Conflict,(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow))).StatusCode); // Expiring a position does not reset ordering.
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var json=JsonSerializer.Serialize(await db.LiveConsents.ToArrayAsync());Assert.DoesNotContain("41.25",json);Assert.DoesNotContain("Synthetic aircraft",json);Assert.DoesNotContain("latitude",json,StringComparison.OrdinalIgnoreCase);}
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();Assert.Empty((await Dashboard()).Flights);
        Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow,2))).StatusCode);
        using(var scope=app.Services.CreateScope()){await scope.ServiceProvider.GetRequiredService<OrganizationLiveFlights>().Sweep(default);Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Events.ToArrayAsync());}
    }
    [PostgresFact]public async Task LiveStopIsImmediateOnSubsequentReadsIdempotentAndCannotStopAnotherPilotsSession()
    {
        await Bootstrap();await Login();using var a=await JoinPilot("first",InvitationSecret(await Invite()));using var b=await JoinPilot("second",InvitationSecret(await Invite()));var ga=await LiveGrant(a);var gb=await LiveGrant(b);var session=await StartLive(ga);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",ga,OrganizationLiveContractTests.Update(session.Id,LiveNow)));
        var stop=new LiveSharingStop(1,Guid.NewGuid(),session.Id,1);Assert.Equal(HttpStatusCode.NotFound,(await LiveRequest("/api/member/live/stop",gb,stop)).StatusCode);
        await Value<LiveSharingSession>(await LiveRequest("/api/member/live/stop",ga,stop));await Value<LiveSharingSession>(await LiveRequest("/api/member/live/stop",ga,stop));Assert.Empty((await Dashboard(b)).Flights);
        Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",ga,OrganizationLiveContractTests.Update(session.Id,LiveNow,2))).StatusCode);
        using var scope=app.Services.CreateScope();Assert.Single(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Events.Where(e=>e.Type==OrganizationEventType.LiveSharingStopped).ToArrayAsync());
    }
    [PostgresFact]public async Task LiveMembershipAndGrantRevocationRemovePositionsFromExistingReaders()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var session=await StartLive(grant);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow)));Assert.Single((await Dashboard()).Flights);
        await Change(await Member(pilot),state:OrganizationMembershipState.Suspended);Assert.Empty((await Dashboard()).Flights);Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow,2))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await pilot.Client.GetAsync("/api/membership/live")).StatusCode);
    }
    [PostgresFact]public async Task LivePolicyRevokesAllExistingConsentsAndRestrictsActualClaimedCampaigns()
    {
        await Bootstrap();await Login();var (campaign,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var claim=await PilotCommand(pilot,Claim(card));var grant=await LiveGrant(pilot);var session=await StartLive(grant,claims:[claim.Id]);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/membership/live/policy",new LivePolicyCommand(1,Guid.NewGuid(),0,false,[]))).StatusCode);
        var command=new LivePolicyCommand(1,Guid.NewGuid(),0,true,[campaign.Id]);var policy=await Value<OrganizationLivePolicy>(await browser.Send("/api/membership/live/policy",command));Assert.Equal(1,policy.Revision);Assert.Empty((await Dashboard()).Flights);Assert.Equal(JsonSerializer.Serialize(policy),JsonSerializer.Serialize(await Value<OrganizationLivePolicy>(await browser.Send("/api/membership/live/policy",command))));
        Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",grant,(await LiveStartRequest(claims:[claim.Id],revision:1)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",grant,await LiveStartRequest(revision:1))).StatusCode); // Omitting a sensitive claim cannot bypass server policy.
        Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/membership/live/policy",command with{RequestId=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow))).StatusCode);
        await Value<OrganizationLivePolicy>(await browser.Send("/api/membership/live/policy",new LivePolicyCommand(1,Guid.NewGuid(),1,false,[])));
        Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/start",grant,await LiveStartRequest(revision:2))).StatusCode);
    }
    [PostgresFact]public async Task LiveCardIdentityUsesExactIssuedClaimAndLocalAttemptEventsAreNotScientificApprovals()
    {
        await ClockForLive();await Bootstrap();await Login();var (_,card)=await Opportunity();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var claim=await PilotCommand(pilot,Claim(card));var grant=await LiveGrant(pilot);var session=await StartLive(grant,claims:[claim.Id]);
        var local=MissionStore.OrganizationLocalCardId(Origin,claim.OrganizationId,claim.PilotId,claim.Id,claim.IssuedSha256);var status=new LiveCardStatus(claim.Id,local,1,null,Guid.NewGuid(),LiveResearchProgress.AttemptRecorded,true,ResearchAttemptOutcome.Fail);var update=OrganizationLiveContractTests.Update(session.Id,LiveNow) with{Cards=[status]};
        Assert.Equal(HttpStatusCode.Forbidden,(await LiveRequest("/api/member/live/update",grant,update with{Cards=[status with{CardId=Guid.NewGuid()}]})).StatusCode);
        await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update));liveClock!.Advance(5);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,update with{Sequence=2,SourceUtc=LiveNow}));
        var shown=Assert.Single(Assert.Single((await Dashboard()).Flights).Cards);Assert.Equal(card.Name,shown.Name);Assert.Equal(ResearchAttemptOutcome.Fail,shown.Outcome);Assert.Equal(claim.IssuedRevision,shown.IssuedRevision);
        var events=await Value<OrganizationEventPage>(await pilot.Client.GetAsync("/api/membership/events"));var attempt=Assert.Single(events.Items,e=>e.Type==OrganizationEventType.AttemptRecorded);Assert.Contains("not",attempt.Meaning,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain(events.Items,e=>e.Type==OrganizationEventType.RequirementApproved);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Empty(await db.Submissions.ToArrayAsync());var original=await db.Events.SingleAsync(e=>e.Id==attempt.Id);original.Meaning="FORGED";await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());}
        await PilotCommand(pilot,Claim(card,OrganizationClaimAction.Release,claim));Assert.Empty((await Dashboard()).Flights);
    }
    [PostgresFact]public async Task LiveConcurrentRetriesCoalesceWithoutAHistoryQueueAndStopWinsAllLaterReads()
    {
        await ClockForLive();await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var session=await StartLive(grant);var update=OrganizationLiveContractTests.Update(session.Id,LiveNow);
        var retries=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>LiveRequest("/api/member/live/update",grant,update)));foreach(var r in retries)Assert.True((await Value<LiveUpdateReceipt>(r)).Applied);
        var fast=await Task.WhenAll(Enumerable.Range(2,8).Select(i=>LiveRequest("/api/member/live/update",grant,update with{Sequence=i})));foreach(var r in fast)Assert.False((await Value<LiveUpdateReceipt>(r)).Applied);
        var stop=LiveRequest("/api/member/live/stop",grant,new LiveSharingStop(1,Guid.NewGuid(),session.Id,1));var race=LiveRequest("/api/member/live/update",grant,update with{Sequence=20});await Task.WhenAll(stop,race);Assert.Equal(HttpStatusCode.OK,stop.Result.StatusCode);Assert.Empty((await Dashboard()).Flights);
        using var scope=app.Services.CreateScope();var row=await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().LiveConsents.SingleAsync();Assert.Equal(1,row.LastSequence);Assert.Equal("",row.LastUpdateSha256);Assert.NotNull(row.EndedUtc);
    }
    [PostgresFact]public async Task LiveDisconnectedAndExpiredDesktopGrantsCannotReadOrWriteExistingPositions()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var session=await StartLive(grant);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow)));
        await Value<JsonElement>(await LiveRequest("/api/device/disconnect",grant,new{}));Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live",grant)).StatusCode);Assert.Empty((await Dashboard()).Flights);
        var newer=await LiveGrant(pilot);var second=await StartLive(newer);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",newer,OrganizationLiveContractTests.Update(second.Id,LiveNow)));
        using(var scope=app.Services.CreateScope()){await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().DeviceGrants.Where(g=>g.Id==newer.ConnectionId).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.CredentialExpiresUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));}
        Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",newer,OrganizationLiveContractTests.Update(second.Id,LiveNow,2))).StatusCode);Assert.Empty((await Dashboard()).Flights);
    }
    [PostgresFact]public async Task LivePendingAndRemovedMembersCannotViewPositionsOrInheritAnOldConsent()
    {
        await Bootstrap();await Login();await Policy();using var pending=await JoinPilot("pending-live");Assert.Equal(OrganizationMembershipState.PendingApproval,(await Member(pending)).State);Assert.Equal(HttpStatusCode.Forbidden,(await pending.Client.GetAsync("/api/membership/live")).StatusCode);
        await Change(await Member(pending),state:OrganizationMembershipState.Active);await Login(pending,"pending-live");var grant=await LiveGrant(pending);var session=await StartLive(grant);await Value<LiveUpdateReceipt>(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow)));Assert.Single((await Dashboard()).Flights);
        await Change(await Member(pending),state:OrganizationMembershipState.Removed);Assert.Equal(HttpStatusCode.Unauthorized,(await pending.Client.GetAsync("/api/membership/live")).StatusCode);Assert.Empty((await Dashboard()).Flights);Assert.Equal(HttpStatusCode.Unauthorized,(await LiveRequest("/api/member/live/update",grant,OrganizationLiveContractTests.Update(session.Id,LiveNow,2))).StatusCode);
    }
}
