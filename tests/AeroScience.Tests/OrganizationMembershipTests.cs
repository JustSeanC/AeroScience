using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    async Task<OrganizationPublicIdentity> Identity()=>await Value<OrganizationPublicIdentity>(await browser.Client.GetAsync("/api/public/organization"));
    async Task<OrganizationInvitationView> Invite()=>await Value<OrganizationInvitationView>(await browser.Send("/api/organization/members/invitations",new OrganizationInvitationRequest(1,Guid.NewGuid())));
    async Task<Browser> JoinPilot(string name="pilot",string? invitation=null)
    {
        var identity=await Identity();var target=new Browser();target.Connect(app);await target.Session();
        await Value<JsonElement>(await target.Send("/api/enrollment",new OrganizationJoinRequest(1,identity.Id,name,Password,invitation,"Atmospheric investigations",true)));
        await Login(target,name);return target;
    }
    static string InvitationSecret(OrganizationInvitationView invite)=>new Uri(invite.Link!).Fragment[6..];
    async Task<OrganizationMemberView> Member(Browser target)
    {
        var self=await Value<JsonElement>(await target.Client.GetAsync("/api/membership"));var id=self.GetProperty("membership").GetProperty("userId").GetGuid();
        return (await Value<OrganizationPage<OrganizationMemberView>>(await browser.Client.GetAsync("/api/organization/members"))).Items.Single(m=>m.UserId==id);
    }
    async Task<OrganizationMemberView> Change(OrganizationMemberView m,string? role=null,OrganizationMembershipState? state=null)=>await Value<OrganizationMemberView>(await browser.Send("/api/organization/members/"+m.UserId,new OrganizationMemberRequest(1,Guid.NewGuid(),m.Revision,role??m.Role,state??m.State),"PUT"));
    async Task Policy(bool approval=true)
    {
        var p=(await Identity()).Policy;await Value<OrganizationEnrollmentPolicy>(await browser.Send("/api/organization/members/policy",new OrganizationPolicyRequest(1,Guid.NewGuid(),p.Revision,OrganizationEnrollmentMode.ApplicationsAllowed,approval),"PUT"));
    }
    async Task<(OrganizationDevicePending Device,string Verifier)> Device()
    {
        var identity=await Identity();var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);
        var device=await Value<OrganizationDevicePending>(await native.PostAsJsonAsync("/api/device/start",new OrganizationDeviceStart(identity.Id,"Actual desktop fixture",challenge),WireJson.Options));return(device,verifier);
    }
    async Task<OrganizationDeviceToken> Poll(OrganizationDevicePending device,string verifier)
    {
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);return await Value<OrganizationDeviceToken>(await native.PostAsJsonAsync("/api/device/poll",new OrganizationDevicePoll(device.Id,(await Identity()).Id,device.DeviceSecret,verifier),WireJson.Options));
    }
    async Task ReadyPoll(Guid id){using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.DeviceGrants.Where(d=>d.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(d=>d.NextPollUtc,DateTimeOffset.UtcNow.AddMinutes(-1)));}
    async Task<OrganizationDeviceToken> Approved(Browser pilot)
    {
        var (device,verifier)=await Device();await Value<JsonElement>(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,(await Identity()).Id,true,true)));return await Poll(device,verifier);
    }
    async Task<HttpResponseMessage> Bearer(string path,string credential,bool post=false)
    {
        using var native=app.GetTestClient();native.BaseAddress=new(Origin);var req=new HttpRequestMessage(post?HttpMethod.Post:HttpMethod.Get,path);if(post)req.Content=JsonContent.Create(new{});req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);return await native.SendAsync(req);
    }
    [PostgresFact]public async Task InvitationsAreHashedSingleUseBoundedAndIdempotentWithoutRetainingTheirSecrets()
    {
        await Bootstrap();await Login();var request=new OrganizationInvitationRequest(1,Guid.NewGuid(),1);var invitation=await Value<OrganizationInvitationView>(await browser.Send("/api/organization/members/invitations",request));
        Assert.NotNull(invitation.Link);var repeat=await Value<OrganizationInvitationView>(await browser.Send("/api/organization/members/invitations",request));Assert.Equal(invitation.Id,repeat.Id);Assert.Null(repeat.Link);
        Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send("/api/organization/members/invitations",request with{RequestId=Guid.NewGuid(),ExpiresHours=169})).StatusCode);
        using var pilot=await JoinPilot(invitation:InvitationSecret(invitation));var m=await Member(pilot);Assert.Equal(OrganizationRoles.Pilot,m.Role);Assert.Equal(OrganizationMembershipState.Active,m.State);
        using var other=new Browser();other.Connect(app);await other.Session();Assert.Equal(HttpStatusCode.Conflict,(await other.Send("/api/enrollment",new OrganizationJoinRequest(1,(await Identity()).Id,"other",Password,InvitationSecret(invitation),"",true))).StatusCode);
        var list=await Value<OrganizationPage<OrganizationInvitationView>>(await browser.Client.GetAsync("/api/organization/members/invitations"));Assert.Equal("Used",Assert.Single(list.Items).Status);Assert.All(list.Items,i=>Assert.Null(i.Link));
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var original=await db.Invitations.SingleAsync();Assert.Equal(64,original.SecretHash.Length);Assert.DoesNotContain(InvitationSecret(invitation).Split('.')[1],JsonSerializer.Serialize(await db.Receipts.ToArrayAsync()));
    }
    [PostgresFact]public async Task ExpiredRevokedTamperedAndWrongOrganizationInvitationsCannotEnroll()
    {
        await Bootstrap();await Login();var invitation=await Invite();using var applicant=new Browser();applicant.Connect(app);await applicant.Session();var req=new OrganizationJoinRequest(1,(await Identity()).Id,"pilot",Password,InvitationSecret(invitation),"",true);
        Assert.Equal(HttpStatusCode.Conflict,(await applicant.Send("/api/enrollment",req with{OrganizationId=Guid.NewGuid()})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await applicant.Send("/api/enrollment",req with{Invitation=invitation.Id.ToString("N")+"."+new string('a',64)})).StatusCode);
        await Value<JsonElement>(await browser.Send($"/api/organization/members/invitations/{invitation.Id}/revoke",new OrganizationPublishRequest(1,Guid.NewGuid(),0)));Assert.Equal(HttpStatusCode.Conflict,(await applicant.Send("/api/enrollment",req)).StatusCode);
        var expired=await Invite();using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.Invitations.Where(i=>i.Id==expired.Id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.ExpiresUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));}
        Assert.Equal(HttpStatusCode.Conflict,(await applicant.Send("/api/enrollment",req with{Invitation=InvitationSecret(expired)})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await applicant.Send("/api/enrollment",req with{Invitation=null})).StatusCode);
        using var check=app.Services.CreateScope();Assert.Equal(1,await check.ServiceProvider.GetRequiredService<OrganizationDatabase>().Users.CountAsync());
    }
    [PostgresFact]public async Task ConcurrentInvitationRedemptionCreatesExactlyOnePilotMembership()
    {
        await Bootstrap();await Login();var invite=await Invite();using var a=new Browser();a.Connect(app);await a.Session();using var b=new Browser();b.Connect(app);await b.Session();var req=new OrganizationJoinRequest(1,(await Identity()).Id,"first",Password,InvitationSecret(invite),"",true);
        var responses=await Task.WhenAll(a.Send("/api/enrollment",req),b.Send("/api/enrollment",req with{UserName="second"}));Assert.Single(responses,r=>r.IsSuccessStatusCode);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Conflict);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(2,await db.Memberships.CountAsync());Assert.Equal(2,await db.Users.CountAsync());
    }
    [PostgresFact]public async Task ApplicationsRemainPendingAcrossPolicyChangesAndNeedExplicitApproval()
    {
        await Bootstrap();await Login();await Policy();using var pilot=await JoinPilot();var member=await Member(pilot);Assert.Equal(OrganizationMembershipState.PendingApproval,member.State);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/membership/published")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/organization/members")).StatusCode);
        await Policy(false);Assert.Equal(OrganizationMembershipState.PendingApproval,(await Member(pilot)).State);
        member=await Change(member,state:OrganizationMembershipState.Active);Assert.Equal(HttpStatusCode.Unauthorized,(await pilot.Client.GetAsync("/api/membership")).StatusCode);await Login(pilot,"pilot");Assert.Equal(HttpStatusCode.OK,(await pilot.Client.GetAsync("/api/membership/published")).StatusCode);
        using var second=await JoinPilot("second");Assert.Equal(OrganizationMembershipState.Active,(await Member(second)).State);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(3,await db.Revisions.CountAsync(r=>r.Kind=="enrollment-policy"));Assert.NotEmpty(await db.Audit.Where(a=>a.Action.Contains("membership-active")).ToArrayAsync());
    }
    [PostgresFact]public async Task ManagersCannotPromoteThemselvesOrOtherPrivilegedMembersAndFinalAdministratorIsProtected()
    {
        await Bootstrap();await Login();var admin=Assert.Single((await Value<OrganizationPage<OrganizationMemberView>>(await browser.Client.GetAsync("/api/organization/members"))).Items);
        Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/organization/members/"+admin.UserId,new OrganizationMemberRequest(1,Guid.NewGuid(),admin.Revision,OrganizationRoles.Pilot,OrganizationMembershipState.Active),"PUT")).StatusCode);
        using var manager=await JoinPilot("manager",InvitationSecret(await Invite()));var row=await Change(await Member(manager),OrganizationRoles.Manager);await Login(manager,"manager");
        Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send("/api/organization/members/"+row.UserId,new OrganizationMemberRequest(1,Guid.NewGuid(),row.Revision,OrganizationRoles.Administrator,row.State),"PUT")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send("/api/organization/members/"+admin.UserId,new OrganizationMemberRequest(1,Guid.NewGuid(),admin.Revision,OrganizationRoles.Pilot,admin.State),"PUT")).StatusCode);
        var policy=(await Identity()).Policy;Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send("/api/organization/members/policy",new OrganizationPolicyRequest(1,Guid.NewGuid(),policy.Revision,OrganizationEnrollmentMode.ApplicationsAllowed,false),"PUT")).StatusCode);
        using var pilot=await JoinPilot("pilot",InvitationSecret(await Invite()));var target=await Member(pilot);Assert.Equal(HttpStatusCode.OK,(await manager.Send("/api/organization/members/"+target.UserId,new OrganizationMemberRequest(1,Guid.NewGuid(),target.Revision,OrganizationRoles.Pilot,OrganizationMembershipState.Suspended),"PUT")).StatusCode);
    }
    [PostgresFact]public async Task DeviceApprovalRequiresMatchingIdentityActiveMembershipCsrfAndExplicitConsent()
    {
        await Bootstrap();await Login();await Policy();using var pilot=await JoinPilot();var (device,verifier)=await Device();var org=(await Identity()).Id;
        Assert.Equal("pending",(await Poll(device,verifier)).Status);Assert.Equal("slowDown",(await Poll(device,verifier)).Status);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,org,true,true))).StatusCode);
        await Change(await Member(pilot),state:OrganizationMembershipState.Active);await Login(pilot,"pilot");
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,org,true,true),csrf:false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,Guid.NewGuid(),true,true))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,org,true,false))).StatusCode);
        await Value<JsonElement>(await pilot.Send("/api/connection/approval",new OrganizationDeviceApproval(device.Id,org,true,true)));await ReadyPoll(device.Id);var token=await Poll(device,verifier);Assert.Equal("approved",token.Status);Assert.NotNull(token.Credential);Assert.True(token.ExpiresUtc>DateTimeOffset.UtcNow);
        await ReadyPoll(device.Id);Assert.Equal(token,await Poll(device,verifier));Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/organization/members",token.Credential!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await Bearer("/api/member/published",token.Credential!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",new string('e',64))).StatusCode);
    }
    [PostgresFact]public async Task DeviceGrantsRejectAlteredVerifierExpiryRevocationAndBrowserOriginRequests()
    {
        await Bootstrap();await Login();var (device,verifier)=await Device();using var native=app.GetTestClient();native.BaseAddress=new(Origin);var poll=new OrganizationDevicePoll(device.Id,(await Identity()).Id,device.DeviceSecret,verifier);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/device/poll",poll with{Verifier=new string('a',64)},WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/device/poll",poll with{OrganizationId=Guid.NewGuid()},WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/device/poll",poll)).StatusCode);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.DeviceGrants.Where(g=>g.Id==device.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.ExpiresUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));}
        Assert.Equal("expiredOrRevoked",(await Poll(device,verifier)).Status);
    }
    [PostgresFact]public async Task RoleSuspensionRemovalLogoutAndDisconnectRevokeDesktopAccessPromptly()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var token=await Approved(pilot);
        var m=await Change(await Member(pilot),OrganizationRoles.Reviewer);Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);await Login(pilot,"pilot");token=await Approved(pilot);
        m=await Change(m,state:OrganizationMembershipState.Suspended);Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);m=await Change(m,state:OrganizationMembershipState.Active);await Login(pilot,"pilot");token=await Approved(pilot);
    }
    [PostgresFact]public async Task DisconnectLogoutAndRemovalRevokeIndependentDesktopCredentials()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var m=await Member(pilot);var token=await Approved(pilot);
        await Value<JsonElement>(await Bearer("/api/device/disconnect",token.Credential!,true));Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);
        token=await Approved(pilot);await Value<JsonElement>(await pilot.Send("/api/sign-out",new{}));Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);await Login(pilot,"pilot");token=await Approved(pilot);await Change(m,state:OrganizationMembershipState.Removed);Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);
    }
    [PostgresFact]public async Task IssuedCredentialsExpireWithoutAnImplicitRefreshOrBrowserPrivilege()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var token=await Approved(pilot);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.DeviceGrants.Where(g=>g.Id==token.ConnectionId).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.CredentialExpiresUtc,DateTimeOffset.UtcNow.AddSeconds(-1)));}
        Assert.Equal(HttpStatusCode.Unauthorized,(await Bearer("/api/member/published",token.Credential!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await pilot.Client.GetAsync("/api/member/published")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/organization/overview")).StatusCode);
    }
    [PostgresFact]public async Task MemberReadModelExposesOnlyPublishedRevisionsAndSurvivesRestart()
    {
        await Bootstrap();await Login();var p=await Program();var draft=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign()));var published=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1)));
        await Value<OrganizationProgramView>(await browser.Send("/api/organization/programs/"+p.Id,new OrganizationProgramRequest(1,Guid.NewGuid(),1,"PRIVATE PROGRAM EDIT",""),"PUT"));await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns/{draft.Id}",Campaign() with{ExpectedRevision=2,Name="PRIVATE CAMPAIGN DRAFT"},"PUT"));
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organization.\"Revisions\" SET \"TimestampUtc\"={DateTimeOffset.UtcNow.AddDays(-1)} WHERE \"ResourceId\"={p.Id} AND \"Revision\"=2");}
        using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var token=await Approved(pilot);var data=await Value<OrganizationMemberInformation>(await Bearer("/api/member/published",token.Credential!));Assert.Equal(1,published.ProgramRevision);Assert.Equal(p.Name,Assert.Single(data.Campaigns.Items).ProgramName);Assert.Equal(published.Name,data.Campaigns.Items[0].Name);Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(data));
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);pilot.Connect(app);Assert.Equal(data.Organization.Id,(await Value<OrganizationMemberInformation>(await Bearer("/api/member/published",token.Credential!))).Organization.Id);Assert.Equal(HttpStatusCode.OK,(await pilot.Client.GetAsync("/api/membership")).StatusCode);
    }
    [PostgresFact]public async Task HistoricalPublicationsWithoutPinnedProgramMetadataRemainReadableWithoutGuessingPrivateDrafts()
    {
        await Bootstrap();await Login();var p=await Program();var draft=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign()));
        await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1)));
        // Synthetic legacy-format fixture in this disposable database only; no genuine history is edited.
        using(var scope=app.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var row=await db.Revisions.AsNoTracking().SingleAsync(r=>r.ResourceId==draft.Id&&r.Revision==2);
            var json=System.Text.Json.Nodes.JsonNode.Parse(row.SnapshotJson)!;json.AsObject().Remove("programRevision");var original=json.ToJsonString();var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organization.\"Revisions\" SET \"SnapshotJson\"={original},\"Sha256\"={hash} WHERE \"ResourceId\"={draft.Id} AND \"Revision\"=2");
        }
        await Value<OrganizationProgramView>(await browser.Send("/api/organization/programs/"+p.Id,new OrganizationProgramRequest(1,Guid.NewGuid(),1,"PRIVATE NEW PROGRAM NAME",""),"PUT"));
        using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var token=await Approved(pilot);var data=await Value<OrganizationMemberInformation>(await Bearer("/api/member/published",token.Credential!));
        Assert.Equal("Program metadata unavailable for this historical publication",Assert.Single(data.Campaigns.Items).ProgramName);Assert.Equal(draft.Name,data.Campaigns.Items[0].Name);Assert.DoesNotContain("PRIVATE",JsonSerializer.Serialize(data));
    }
    [PostgresFact]public async Task P13DatabaseMigrationRetainsAdministratorRolesAndNeverReplacesItsIdentity()
    {
        using(var scope=app.Services.CreateScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.GetService<IMigrator>().MigrateAsync("20261027120617_InitialOrganization");
            var users=scope.ServiceProvider.GetRequiredService<UserManager<OrganizationUser>>();var roles=scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();foreach(var role in OrganizationRoles.All)Assert.True((await roles.CreateAsync(new(role){Id=Guid.NewGuid()})).Succeeded);
            var user=new OrganizationUser{Id=Guid.NewGuid(),UserName="administrator"};Assert.True((await users.CreateAsync(user,Password)).Succeeded);Assert.True((await users.AddToRoleAsync(user,OrganizationRoles.Administrator)).Succeeded);db.Organizations.Add(new(){PublicId=Guid.NewGuid(),Name="P13 preserved organization",AdministratorId=user.Id,CreatedUtc=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
        }
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await Login();var identity=await Identity();Assert.Equal("P13 preserved organization",identity.Name);Assert.Equal(OrganizationEnrollmentMode.InvitationOnly,identity.Policy.Mode);Assert.True(identity.Policy.RequireApproval);var members=await Value<OrganizationPage<OrganizationMemberView>>(await browser.Client.GetAsync("/api/organization/members"));Assert.Equal(OrganizationRoles.Administrator,Assert.Single(members.Items).Role);
    }
}
