using System.Net;
using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;
public sealed partial class OrganizationConnectionIntegrationTests
{
    const string Notifications="/api/organization/notifications";
    static async Task NotificationMfa(SubmissionFixture f)
    {
        var begin=await f.Server.Post("/api/account-security/enroll",new AccountEnrollment("ResearchOnly!2026Flight"));var key=begin.GetProperty("setupKey").GetString()!;
        var enabled=await f.Server.Post("/api/account-security/enable",new AccountCode(OrganizationServerTests.AuthenticatorCode(key,DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1)));
        await f.Server.Post("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(enabled.GetProperty("recoveryCodes")[0].GetString()!));
        // Enabling MFA correctly revokes prior grants; explicitly approve new independently scoped connections.
        f.Read=await Connect(f.Desktop,f.Server);f.Upload=await Connect(f.Desktop,f.Server,OrganizationDesktopPermission.SubmitEvidence);
    }
    static async Task NotificationRun(SubmissionFixture f){using var scope=f.Server.App.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<OrganizationNotifications>().Tick(default);}
    [PostgresFact]public async Task DiscordRealReceivedScienceApprovalRequiresExactExternalDisclosureAndSupersedingReviewCancelsIt()
    {
        await using var receiver=await DiscordHttpsFixture.Create();await using var f=await SubmissionFixture.Create(notifications:receiver);await NotificationMfa(f);
        await f.Server.Post(Notifications,new DiscordSettingsCommand(Guid.NewGuid(),0,"External research channel",true,6,"https://discord.com/api/webhooks/123456789012345678/"+new string('a',60)));
        var card=await f.Card(ObservationResearch.Template);var result=await f.Record(card.Prepared,2,notes:true);var row=await f.ReviewApprove(f.Draft(result));using var delivery=f.Delivery();var received=await delivery.Send(row.Id,default);
        Assert.Empty((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);await NotificationRun(f);Assert.Empty(receiver.Bodies);
        var root=received.Receipt!.Id;var review=await ReviewGet<ResearchReviewSnapshot>(f.Server,ReviewPath(root));var requirement=Assert.Single(review.Requirements).Requirement;var target=new ReviewTarget(requirement.CardId,requirement.Key);
        await ReviewCommand(f,received,ResearchReviewAction.Approve,"PRIVATE REVIEW FINDING",target,[new(root,"*")]);await NotificationRun(f);Assert.Empty(receiver.Bodies);
        var candidate=Assert.Single((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);Assert.DoesNotContain("PRIVATE",candidate.Message);Assert.DoesNotContain("Selected original pilot note",candidate.Message);
        using(var changedPreview=await ReviewSend(f.Server,Notifications+"/disclose",new DiscordAction(Guid.NewGuid(),1,candidate.EventId,true,new string('0',64))))Assert.Equal(HttpStatusCode.Conflict,changedPreview.StatusCode);
        var command=new DiscordAction(Guid.NewGuid(),1,candidate.EventId,true,candidate.MessageSha256);await f.Server.Post(Notifications+"/disclose",command);await f.Server.Post(Notifications+"/disclose",command);await NotificationRun(f);Assert.Single(receiver.Bodies);
        var text=Encoding.UTF8.GetString(receiver.Bodies[0]);Assert.Contains("reviewer approval",text);Assert.DoesNotContain("PRIVATE REVIEW",text);Assert.DoesNotContain("latitude",text);Assert.DoesNotContain("pilot note",text);Assert.DoesNotContain("credentials",text);
        using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Single(await db.DiscordDeliveries.ToArrayAsync());Assert.Single(await db.DiscordDisclosures.ToArrayAsync());}
        // A second independent received contribution is disclosed, then loses approval before dispatch.
        var second=await f.Record(card.Prepared,2,notes:true);var secondRow=await f.ReviewApprove(f.Draft(second));secondRow=await delivery.Send(secondRow.Id,default);await ReviewCommand(f,secondRow,ResearchReviewAction.Approve,"Supported original documentation",target,[new(secondRow.Receipt!.Id,"*")]);
        var other=((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items).Single(x=>x.EventId!=candidate.EventId);await f.Server.Post(Notifications+"/disclose",new DiscordAction(Guid.NewGuid(),1,other.EventId,true,other.MessageSha256));
        await ReviewCommand(f,secondRow,ResearchReviewAction.RequestEvidence,"PRIVATE additional explanation",target);
        using(var scope=f.Server.App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var destination=await db.DiscordDestinations.SingleAsync();destination.NextSendUtc=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}
        await NotificationRun(f);Assert.Single(receiver.Bodies);Assert.DoesNotContain((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items,x=>x.EventId==other.EventId);
        Assert.Equal(ResearchAttemptOutcome.CollectionComplete,f.Store.ResearchAttempt(result.Id).Outcome);
    }
    [PostgresFact]public async Task DiscordAcceptedExceptionIsNotScientificPassAndOtherOrganizationCannotDiscloseIt()
    {
        await using var receiver=await DiscordHttpsFixture.Create();await using var f=await SubmissionFixture.Create(notifications:receiver);await using var other=await SubmissionFixture.Create();await NotificationMfa(f);await NotificationMfa(other);
        await f.Server.Post(Notifications,new DiscordSettingsCommand(Guid.NewGuid(),0,"Exceptions channel",true,4,"https://discord.com/api/webhooks/123456789012345678/"+new string('a',60)));
        await other.Server.Post(Notifications,new DiscordSettingsCommand(Guid.NewGuid(),0,"Other destination",true,4,"https://discord.com/api/webhooks/223456789012345678/"+new string('b',60)));
        var c=await f.Card();var policy=await WorkflowPolicy(f,c.Published.ProgramId,new(PhaseGatesEnabled:true));var phase=await WorkflowPhase(f,c.Published,"Bounded procedure",1,[]);await WorkflowAuthorize(f,c.Published.ProgramId,Waiver(c.Published,policy,phase));
        var candidate=Assert.Single((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);Assert.Contains("does not turn missing or failed",candidate.Message);Assert.Empty((await ReviewGet<DiscordAnnouncementPage>(other.Server,Notifications+"/announcements")).Items);
        using(var denied=await ReviewSend(other.Server,Notifications+"/disclose",new DiscordAction(Guid.NewGuid(),1,candidate.EventId,true,candidate.MessageSha256)))Assert.Equal(HttpStatusCode.NotFound,denied.StatusCode);
        await f.Server.Post(Notifications+"/disclose",new DiscordAction(Guid.NewGuid(),1,candidate.EventId,true,candidate.MessageSha256));await NotificationRun(f);Assert.Single(receiver.Bodies);Assert.Contains("administrative exception",Encoding.UTF8.GetString(receiver.Bodies[0]));
    }
}
