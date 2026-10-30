using System.Text;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    // One joined workflow across the actual Engine stores, Kestrel, PostgreSQL,
    // scientific collector/evaluator, reviews, coverage and verified local HTTPS transport.
    // These are explicitly synthetic simulator inputs, never installed-MSFS acceptance.
    [PostgresFact]public async Task IntegratedAtmosphericReceiptApprovalCoverageAndDiscordPreserveConsentAndOriginalDeletionBoundaries()
    {
        await using var receiver=await DiscordHttpsFixture.Create();
        await using var f=await SubmissionFixture.Create(notifications:receiver);await NotificationMfa(f);
        await f.Server.Post(Notifications,new DiscordSettingsCommand(Guid.NewGuid(),0,"Explicitly approved research",true,2,"https://discord.com/api/webhooks/123456789012345678/"+new string('a',60)));
        var card=await f.Card(AtmosphericResearch.Template);var attempt=await AtmosphericRecording(f,card.Prepared);
        Assert.Equal(ResearchAttemptOutcome.CollectionComplete,attempt.Outcome);
        var draft=f.Draft(attempt);Assert.Null(draft.Consent);Assert.Equal(0,await f.Received());Assert.Empty(receiver.Bodies);
        var approved=await f.ReviewApprove(draft);var original=f.Store.ApprovedSubmissionDocument(approved.Id,f.Science);
        var observations=original.Evidence.SelectMany(e=>e.Data.Observations??[]).ToArray();Assert.NotEmpty(observations);
        using var delivery=f.Delivery();var received=await delivery.Send(approved.Id,default);Assert.Equal(SubmissionDeliveryState.ServerReceived,received.State);
        var receipt=received.Receipt!;var campaign="/campaigns/"+card.Published.CampaignId;
        Assert.Equal(0,(await ProgressGet<CampaignProgressView>(f,campaign)).Totals.CompletedCards);
        Assert.Empty((await ProgressGet<OrganizationPage<CoverageGroup>>(f,campaign+"/coverage/groups")).Items);
        Assert.Empty((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);
        await NotificationRun(f);Assert.Empty(receiver.Bodies);

        var review=await ReviewGet<ResearchReviewSnapshot>(f.Server,ReviewPath(receipt.Id));
        foreach(var point in review.Requirements)
            await ReviewCommand(f,received,ResearchReviewAction.Approve,"PRIVATE scientific review finding, not public notification content.",new(point.Requirement.CardId,point.Requirement.Key),[new(receipt.Id,"*")]);
        Assert.Equal(1,(await ProgressGet<CampaignProgressView>(f,campaign)).Totals.CompletedCards);
        var measurements=(await ProgressGet<ApprovedResearchDetail>(f,$"/approved/{receipt.Id}/{card.Prepared.Card.Id}")).Measurements.Items;
        var measured=Assert.Single(measurements);Assert.Equal(observations.Length,measured.Count);
        Assert.Equal(observations.Min(o=>o.Value),measured.Minimum);Assert.Equal(observations.Max(o=>o.Value),measured.Maximum);
        Assert.Equal(observations[0].Unit,measured.Unit);
        var coverage=Assert.Single((await ProgressGet<OrganizationPage<CoverageGroup>>(f,campaign+"/coverage/groups")).Items);
        Assert.Equal(CoverageEvidenceCategory.SyntheticFixture,coverage.Category);Assert.Equal(observations.Length,coverage.Observations);Assert.Equal(1,coverage.Pilots);Assert.Equal(1,coverage.Flights);
        await NotificationRun(f);Assert.Empty(receiver.Bodies); // Member approval is still not external-disclosure consent.
        var announcement=Assert.Single((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);
        await f.Server.Post(Notifications+"/disclose",new DiscordAction(Guid.NewGuid(),1,announcement.EventId,true,announcement.MessageSha256));
        await NotificationRun(f);Assert.Single(receiver.Bodies);
        var publicText=Encoding.UTF8.GetString(receiver.Bodies[0]);Assert.Contains("reviewer approval",publicText);
        foreach(var privateText in new[]{"PRIVATE scientific",f.Vault.Read(f.Upload)!,"latitude","observations","17.1234567"})Assert.DoesNotContain(privateText,publicText);
        await NotificationRun(f);Assert.Single(receiver.Bodies);

        // Receiving selected evidence authorizes a separate server copy. It never authorizes
        // restoring deleted local science or sending another queued package after deletion.
        var queued=await f.ReviewApprove(f.Draft(attempt));var deletion=f.Science.PreviewCollectedDeletion(new([attempt.FlightId]),f.Store);
        f.Science.DeleteCollectedScience(new(deletion.Id,deletion.SelectionHash),f.Store,new MissionCoordinator(f.Store,1000).EnterMaintenance);
        Assert.Equal(SubmissionDeliveryState.HeldEvidenceMissing,(await delivery.Send(queued.Id,default)).State);
        Assert.Equal(1,await f.Received());Assert.Equal(receipt,f.Store.SubmissionDraft(received.Id).Receipt);
        Assert.NotEmpty(f.Store.StreamSamples(attempt.FlightId));
        var retained=(await ProgressGet<ApprovedResearchDetail>(f,$"/approved/{receipt.Id}/{card.Prepared.Card.Id}")).Measurements.Items;
        Assert.Equal(measured,Assert.Single(retained));

        foreach(var point in review.Requirements)
            await ReviewCommand(f,received,ResearchReviewAction.RequestEvidence,"A later finding needs more evidence; the original approval and received values remain in history.",new(point.Requirement.CardId,point.Requirement.Key));
        Assert.Equal(0,(await ProgressGet<CampaignProgressView>(f,campaign)).Totals.CompletedCards);
        Assert.Empty((await ProgressGet<OrganizationPage<CoverageGroup>>(f,campaign+"/coverage/groups")).Items);
        Assert.Empty((await ReviewGet<DiscordAnnouncementPage>(f.Server,Notifications+"/announcements")).Items);
        await NotificationRun(f);Assert.Single(receiver.Bodies); // A previously acknowledged public post cannot be silently undone.
        var history=await ReviewGet<ResearchReviewHistory>(f.Server,ReviewPath(receipt.Id)+"/history");
        Assert.Contains(history.Items,e=>e.Action==ResearchReviewAction.Approve);Assert.Contains(history.Items,e=>e.Action==ResearchReviewAction.RequestEvidence);
    }
}
