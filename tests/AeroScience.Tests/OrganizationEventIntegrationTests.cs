using System.Net;
using System.Net.Http.Json;
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
    [PostgresFact]public async Task AuthoritativeOrganizationEventsKeepReceiptReviewAndApprovedCardPublicationDistinctAndRestartSafe()
    {
        await using var f=await SubmissionFixture.Create();var card=await f.Card(ObservationResearch.Template);var result=await f.Record(card.Prepared,2,notes:true);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);
        var row=await f.ReviewApprove(f.Draft(result));using var delivery=f.Delivery();var received=await delivery.Send(row.Id,default);var root=received.Receipt!.Id;
        var events=await ReviewGet<OrganizationEventPage>(f.Server,"/api/membership/events");Assert.Single(events.Items,e=>e.Type==OrganizationEventType.EvidenceReceived);Assert.DoesNotContain(events.Items,e=>e.Type==OrganizationEventType.ApprovedCardResult);
        var review=await ReviewGet<ResearchReviewSnapshot>(f.Server,ReviewPath(root));var requirement=Assert.Single(review.Requirements).Requirement;var target=new ReviewTarget(requirement.CardId,requirement.Key);
        var evidence=received.Manifest.Items.Where(i=>i.CardId==requirement.CardId).Select(i=>new ReviewEvidenceReference(root,i.Key)).ToArray();
        var command=new ResearchReviewCommand(1,Guid.NewGuid(),review.Summary.Revision,ResearchReviewAction.Approve,target,"PRIVATE REVIEW FINDING",evidence,ConfirmApproval:true);
        var accepted=await f.Server.Post(ReviewPath(root)+"/commands",command);await f.Server.Post(ReviewPath(root)+"/commands",command);
        events=await ReviewGet<OrganizationEventPage>(f.Server,"/api/membership/events");Assert.Single(events.Items,e=>e.Type==OrganizationEventType.RequirementApproved);Assert.Single(events.Items,e=>e.Type==OrganizationEventType.ApprovedCardResult);var ids=events.Items.Select(e=>e.Id).Order().ToArray();
        Assert.DoesNotContain("PRIVATE REVIEW FINDING",JsonSerializer.Serialize(events));Assert.DoesNotContain("Selected original pilot note",JsonSerializer.Serialize(events));Assert.DoesNotContain("latitude",JsonSerializer.Serialize(events),StringComparison.OrdinalIgnoreCase);
        await f.Restart();using(var scope=f.Server.App.Services.CreateScope()){await scope.ServiceProvider.GetRequiredService<CampaignProgressProjection>().Sync(root,default,true);}
        Assert.Equal(ids,(await ReviewGet<OrganizationEventPage>(f.Server,"/api/membership/events")).Items.Select(e=>e.Id).Order().ToArray());
        var invitation=(await f.Server.Post("/api/organization/members/invitations",new OrganizationInvitationRequest(1,Guid.NewGuid()))).Deserialize<OrganizationInvitationView>(WireJson.Options)!;
        var identity=await ReviewGet<OrganizationPublicIdentity>(f.Server,"/api/public/organization");using var member=new HttpClient(new HttpClientHandler{UseCookies=true}){BaseAddress=new(f.Server.Origin)};
        using(var enrolled=await ReviewSend(f.Server,"/api/enrollment",new OrganizationJoinRequest(1,identity.Id,"event-member","ResearchOnly!2026Flight",new Uri(invitation.Link!).Fragment[6..],"Read approved metadata",true),member))Assert.Equal(HttpStatusCode.OK,enrolled.StatusCode);
        using(var login=await ReviewSend(f.Server,"/api/sign-in",new OrganizationLoginRequest("event-member","ResearchOnly!2026Flight"),member))Assert.Equal(HttpStatusCode.OK,login.StatusCode);
        var publicEvents=(await member.GetFromJsonAsync<OrganizationEventPage>("/api/membership/events",WireJson.Options))!;var published=Assert.Single(publicEvents.Items);Assert.Equal(OrganizationEventType.ApprovedCardResult,published.Type);Assert.Equal(Guid.Empty,published.ActorId);
        Assert.Equal(HttpStatusCode.Forbidden,(await member.GetAsync(ReviewPath(root))).StatusCode);
        await ReviewCommand(f,received,ResearchReviewAction.RequestEvidence,"PRIVATE missing method explanation",target);
        var changed=(await member.GetFromJsonAsync<OrganizationEventPage>("/api/membership/events",WireJson.Options))!;Assert.Single(changed.Items,e=>e.Type==OrganizationEventType.ApprovedResultSuperseded);Assert.DoesNotContain(changed.Items,e=>e.Type==OrganizationEventType.MoreEvidenceRequested);
        Assert.Equal(result.Outcome,f.Store.ResearchAttempts(flightId:result.FlightId).Single().Outcome);
    }
    [PostgresFact]public async Task RejectedOriginalQuantitativeEvidenceCreatesOnlyPrivateReviewEventsAndCannotBecomeApprovedScience()
    {
        await using var f=await SubmissionFixture.Create();var card=await f.Card(incomplete:true);var result=await f.Record(card.Prepared,31,fail:true);Assert.Equal(ResearchAttemptOutcome.Fail,result.Outcome);
        await f.ReviewApprove(f.Draft(result));using var delivery=f.Delivery();var received=await delivery.Send(f.Store.NextApprovedSubmission()!.Id,default);var root=received.Receipt!.Id;var review=await ReviewGet<ResearchReviewSnapshot>(f.Server,ReviewPath(root));var requirement=Assert.Single(review.Requirements).Requirement;
        await ReviewCommand(f,received,ResearchReviewAction.Reject,"Original quantitative evidence does not meet the hold requirement.",new(requirement.CardId,requirement.Key));
        var events=await ReviewGet<OrganizationEventPage>(f.Server,"/api/membership/events");Assert.Single(events.Items,e=>e.Type==OrganizationEventType.RequirementRejected);Assert.DoesNotContain(events.Items,e=>e.Type==OrganizationEventType.ApprovedCardResult||e.Type==OrganizationEventType.RequirementApproved);
        Assert.Equal(ResearchAttemptOutcome.Fail,f.Store.ResearchAttempts(flightId:result.FlightId).Single().Outcome);
        using var scope=f.Server.App.Services.CreateScope();Assert.All(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().CampaignContributions.ToArrayAsync(),c=>Assert.NotEqual(ResearchReviewState.Approved,c.State));
    }
}
