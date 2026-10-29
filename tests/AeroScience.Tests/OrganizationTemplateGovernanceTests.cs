using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    static string TemplateJson(string type="custom-flight-test",string name="Shared stable flight")
    {var d=OrganizationTemplates.All().Single(t=>t.Reference.Id==type).Definition;return JsonSerializer.Serialize(PortableResearchTemplates.Create(name,"Definition only",d,"Self-declared pilot attribution"),WireJson.Options);}
    async Task<Browser> PublishingMember(string name,string role)
    {var pilot=await JoinPilot(name,InvitationSecret(await Invite()));await Change(await Member(pilot),role);await Login(pilot,name);return pilot;}
    async Task<ResearchPublishingPolicy> PublishingPolicy(ResearchPublishingMode mode)
    {var current=await Value<ResearchPublishingPolicy>(await browser.Client.GetAsync("/api/research-templates/policy"));return await Value<ResearchPublishingPolicy>(await browser.Send("/api/research-templates/policy",new ResearchPublishingPolicyCommand(1,Guid.NewGuid(),current.Revision,mode)));}
    async Task<TemplateDocument> NewDraft(Browser actor,string? contents=null)=>await Value<TemplateDocument>(await actor.Send("/api/research-templates/drafts",new ResearchDraftCommand(1,Guid.NewGuid(),0,contents??TemplateJson())));
    async Task<JsonElement> PubContext(Browser actor,string kind,Guid id)=>await Value<JsonElement>(await actor.Client.GetAsync($"/api/research-templates/publication-context?kind={kind}&id={id}"));
    async Task<PublicationReviewView> PubReview(Browser actor,string kind,Guid id)
    {var c=await PubContext(actor,kind,id);return await Value<PublicationReviewView>(await actor.Send("/api/research-templates/publication-reviews",new PublicationReviewCommand(1,Guid.NewGuid(),kind,id,c.GetProperty("draftRevision").GetInt32(),c.GetProperty("draftSha256").GetString()!,c.GetProperty("policy").GetProperty("revision").GetInt32())));}
    async Task<PublicationReviewView> ApprovePub(Browser actor,PublicationReviewView review)=>await Value<PublicationReviewView>(await actor.Send($"/api/research-templates/publication-reviews/{review.Id}/decision",new PublicationReviewDecision(1,Guid.NewGuid(),review.Revision,PublicationReviewStatus.Approved,"Exact supported definition reviewed")));
    static OrganizationPublishRequest ApprovedPublication(TemplateDocument draft,PublicationReviewView approval)=>new(1,Guid.NewGuid(),draft.Revision,approval.DraftSha256,approval.Id);

    [PostgresFact]public async Task TemplateProposalsNeedExplicitBrowserConsentAndArePrivateDefinitionOnlyRecords()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));using var other=await JoinPilot("other",InvitationSecret(await Invite()));
        var request=new TemplateProposalCommand(1,Guid.NewGuid(),0,(await Identity()).Id,TemplateJson(),"Investigate supported stable flight","No flight evidence",true);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send("/api/research-templates/proposals",request with{ConfirmDefinitionOnly=false})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/research-templates/proposals",request,csrf:false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/research-templates/proposals",request,origin:"https://other.example")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/research-templates/proposals",request with{OrganizationId=Guid.NewGuid()})).StatusCode);
        var value=await Value<TemplateDocument>(await pilot.Send("/api/research-templates/proposals",request));Assert.Equal(value.Id,(await Value<TemplateDocument>(await pilot.Send("/api/research-templates/proposals",request))).Id);
        Assert.Empty((await Value<OrganizationPage<TemplateDocument>>(await other.Client.GetAsync("/api/research-templates/proposals"))).Items);
        Assert.Equal(HttpStatusCode.Forbidden,(await other.Client.GetAsync($"/api/research-templates/{value.Id}/revisions/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/research-templates/proposals?manage=true")).StatusCode);
        var token=await Approved(pilot);using var native=app.GetTestClient();native.BaseAddress=new(Origin);native.DefaultRequestHeaders.Authorization=new("Bearer",token.Credential);
        Assert.Equal(HttpStatusCode.Unauthorized,(await native.PostAsJsonAsync("/api/research-templates/proposals",request,WireJson.Options)).StatusCode);
        var accepted=await Value<TemplateDocument>(await browser.Send($"/api/research-templates/proposals/{value.Id}/decision",new TemplateProposalDecision(1,Guid.NewGuid(),value.Revision,TemplateProposalStatus.AcceptedForDrafting,"Ready for an independent draft")));
        var draft=Assert.Single((await Value<OrganizationPage<TemplateDocument>>(await browser.Client.GetAsync("/api/research-templates/drafts"))).Items);Assert.Equal(accepted.DraftId,draft.Id);Assert.Equal(value.Template.Sha256,draft.Template.Sha256);Assert.NotEqual(value.Id,draft.Id);
        Assert.Empty((await Value<OrganizationPage<TemplateDocument>>(await pilot.Client.GetAsync("/api/research-templates/published"))).Items);
        var edited=await Value<TemplateDocument>(await browser.Send($"/api/research-templates/drafts/{draft.Id}",new ResearchDraftCommand(1,Guid.NewGuid(),draft.Revision,TemplateJson(name:"Organization copy")),"PUT"));Assert.Equal("Organization copy",edited.Template.Name);
        var original=await Value<DefinitionRevision>(await pilot.Client.GetAsync($"/api/research-templates/{value.Id}/revisions/1"));Assert.Contains("Shared stable flight",original.SnapshotJson);Assert.DoesNotContain("Organization copy",original.SnapshotJson);
        await Value<TemplateDocument>(await browser.Send($"/api/research-templates/drafts/{edited.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),edited.Revision)));
        var visible=Assert.Single((await Value<OrganizationPage<TemplateDocument>>(await other.Client.GetAsync("/api/research-templates/published"))).Items);Assert.Null(visible.ProposalId);Assert.Null(visible.OriginalTemplateSha256);Assert.Empty(visible.Purpose);Assert.Empty(visible.Message);Assert.Null(visible.ProgramId);Assert.Null(visible.CampaignId);
        await Change(await Member(pilot),state:OrganizationMembershipState.Suspended);Assert.True((await pilot.Send("/api/research-templates/proposals",request)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }
    [PostgresFact]public async Task TemplateChangesRequestsRejectsHistoryAndPostgresRestartPreserveOriginalProposals()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var request=new TemplateProposalCommand(1,Guid.NewGuid(),0,(await Identity()).Id,TemplateJson(),"Question","Original message",true);var p=await Value<TemplateDocument>(await pilot.Send("/api/research-templates/proposals",request));
        Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send($"/api/research-templates/proposals/{p.Id}/decision",new TemplateProposalDecision(1,Guid.NewGuid(),1,TemplateProposalStatus.Rejected,""))).StatusCode);
        var change=await Value<TemplateDocument>(await browser.Send($"/api/research-templates/proposals/{p.Id}/decision",new TemplateProposalDecision(1,Guid.NewGuid(),1,TemplateProposalStatus.ChangesRequested,"Clarify the purpose")));
        var revised=await Value<TemplateDocument>(await pilot.Send($"/api/research-templates/proposals/{p.Id}",request with{RequestId=Guid.NewGuid(),ExpectedRevision=change.Revision,Message="Revised explanation"},"PUT"));
        await Value<TemplateDocument>(await browser.Send($"/api/research-templates/proposals/{p.Id}/decision",new TemplateProposalDecision(1,Guid.NewGuid(),revised.Revision,TemplateProposalStatus.Rejected,"Not suitable for this campaign")));
        await app.DisposeAsync();await Start();browser.Connect(app);pilot.Connect(app);await browser.Session();await pilot.Session();
        var retained=Assert.Single((await Value<OrganizationPage<TemplateDocument>>(await pilot.Client.GetAsync("/api/research-templates/proposals"))).Items);Assert.Equal(TemplateProposalStatus.Rejected,retained.Status);Assert.Equal(4,retained.Revision);
        var first=await Value<DefinitionRevision>(await pilot.Client.GetAsync($"/api/research-templates/{p.Id}/revisions/1"));Assert.Contains("Original message",first.SnapshotJson);Assert.DoesNotContain("Revised explanation",first.SnapshotJson);
    }
    [PostgresFact]public async Task LegacyDefaultPreservesPermissionsAndExplicitDirectManagerPolicyEnablesPublication()
    {
        await Bootstrap();await Login();using var manager=await PublishingMember("manager",OrganizationRoles.Manager);using var reviewer=await PublishingMember("reviewer",OrganizationRoles.Reviewer);
        var initial=await Value<ResearchPublishingPolicy>(await manager.Client.GetAsync("/api/research-templates/policy"));Assert.Equal(ResearchPublishingMode.Legacy,initial.Mode);Assert.Equal(0,initial.Revision);
        var draft=await NewDraft(manager);Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await reviewer.Send("/api/research-templates/drafts",new ResearchDraftCommand(1,Guid.NewGuid(),0,TemplateJson()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send("/api/research-templates/policy",new ResearchPublishingPolicyCommand(1,Guid.NewGuid(),0,ResearchPublishingMode.DirectManager))).StatusCode);
        await PublishingPolicy(ResearchPublishingMode.DirectManager);var published=await Value<TemplateDocument>(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision)));Assert.Equal(TemplateProposalStatus.Published,published.Status);Assert.Null(published.Publication!.ApproverId);Assert.Equal(draft.AuthorId,published.Publication.PublisherId);
        var program=await Program();var campaign=await Value<OrganizationCampaignView>(await manager.Send($"/api/organization/programs/{program.Id}/campaigns",Campaign() with{Template=new("published:"+published.Id,published.Revision)}));
        var accepted=await Value<OrganizationCampaignView>(await manager.Send($"/api/organization/campaigns/{campaign.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),campaign.Revision)));Assert.Equal(published.Template.Definition.FlightRegime,accepted.Definition.FlightRegime);Assert.NotNull(accepted.SourceTemplate);
        var card=await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/campaigns/{accepted.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,accepted.Revision,"Custom organization card","Definition","Demonstrate stable conditions","Use original telemetry",Exclusive())));
        var opportunity=await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision)));
        using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var claim=await PilotCommand(pilot,Claim(opportunity));Assert.Equal(opportunity.Revision,claim.IssuedRevision);Assert.Equal(published.Template.Definition.FlightRegime,claim.IssuedCard.Definition.FlightRegime);
        await Value<TemplateDocument>(await manager.Send($"/api/research-templates/drafts/{published.Id}",new ResearchDraftCommand(1,Guid.NewGuid(),published.Revision,TemplateJson(name:"Next immutable version")),"PUT"));
        var held=Assert.Single((await MyClaims(pilot)).Items);Assert.Equal(claim.IssuedSha256,held.IssuedSha256);Assert.Equal(JsonSerializer.Serialize(claim.IssuedCard,WireJson.Options),JsonSerializer.Serialize(held.IssuedCard,WireJson.Options));
    }
    [PostgresFact]public async Task IndependentAdministratorApprovalBindsExactDraftAndCannotBeBypassedByCampaignOrOpportunityEndpoints()
    {
        await Bootstrap();await Login();using var manager=await PublishingMember("author",OrganizationRoles.Manager);using var second=await PublishingMember("independent",OrganizationRoles.Administrator);await PublishingPolicy(ResearchPublishingMode.AdministratorApproval);
        var draft=await NewDraft(manager);var review=await PubReview(manager,"research-template",draft.Id);
        var own=await NewDraft(browser);var ownReview=await PubReview(browser,"research-template",own.Id);Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send($"/api/research-templates/publication-reviews/{ownReview.Id}/decision",new PublicationReviewDecision(1,Guid.NewGuid(),1,PublicationReviewStatus.Approved,"Self approval"))).StatusCode);
        var approval=await ApprovePub(second,review);var published=await Value<TemplateDocument>(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",ApprovedPublication(draft,approval)));Assert.Equal(approval.ApproverId,published.Publication!.ApproverId);Assert.NotEqual(published.Publication.AuthorId,published.Publication.ApproverId);
        var p=await Program();var c=await Value<OrganizationCampaignView>(await manager.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign()));Assert.Equal(HttpStatusCode.Conflict,(await browser.Send($"/api/organization/campaigns/{c.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),c.Revision))).StatusCode);
        var cr=await PubReview(manager,"campaign",c.Id);cr=await ApprovePub(second,cr);c=await Value<OrganizationCampaignView>(await manager.Send($"/api/organization/campaigns/{c.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),c.Revision,cr.DraftSha256,cr.Id)));
        var card=await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/campaigns/{c.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,c.Revision,"Reviewed card","Details","Objective","Instructions",Exclusive())));
        Assert.Equal(HttpStatusCode.Conflict,(await manager.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision))).StatusCode);
        var r=await PubReview(manager,"opportunity",card.Id);r=await ApprovePub(second,r);var command=new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision,r.DraftSha256,r.Id);var result=await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/{card.Id}/publish",command));Assert.Equal(result.Revision,(await Value<OrganizationOpportunitySnapshot>(await manager.Send($"/api/organization/opportunities/{card.Id}/publish",command))).Revision);
    }
    [PostgresFact]public async Task DraftEditsPolicyChangesRevokedApproversAndStaleConcurrentDecisionsInvalidateApproval()
    {
        await Bootstrap();await Login();using var manager=await PublishingMember("manager",OrganizationRoles.Manager);using var second=await PublishingMember("second",OrganizationRoles.Administrator);await PublishingPolicy(ResearchPublishingMode.AdministratorApproval);
        var draft=await NewDraft(manager);var review=await PubReview(manager,"research-template",draft.Id);var command=new PublicationReviewDecision(1,Guid.NewGuid(),1,PublicationReviewStatus.Approved,"Supported exact definition");
        var results=await Task.WhenAll(browser.Send($"/api/research-templates/publication-reviews/{review.Id}/decision",command),second.Send($"/api/research-templates/publication-reviews/{review.Id}/decision",command with{RequestId=Guid.NewGuid()}));Assert.Single(results,x=>x.StatusCode==HttpStatusCode.OK);Assert.Single(results,x=>x.StatusCode==HttpStatusCode.Conflict);
        var approved=await Value<PublicationReviewView>(results.Single(x=>x.StatusCode==HttpStatusCode.OK));var updated=await Value<TemplateDocument>(await manager.Send($"/api/research-templates/drafts/{draft.Id}",new ResearchDraftCommand(1,Guid.NewGuid(),draft.Revision,TemplateJson(name:"Changed criteria title")),"PUT"));
        Assert.Equal(HttpStatusCode.Conflict,(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",ApprovedPublication(updated,approved))).StatusCode);
        var fresh=await ApprovePub(second,await PubReview(manager,"research-template",updated.Id));await PublishingPolicy(ResearchPublishingMode.AdministratorApproval);Assert.Equal(HttpStatusCode.Conflict,(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",ApprovedPublication(updated,fresh))).StatusCode);
        fresh=await ApprovePub(second,await PubReview(manager,"research-template",updated.Id));await Change(await Member(second),state:OrganizationMembershipState.Suspended);Assert.Equal(HttpStatusCode.Forbidden,(await manager.Send($"/api/research-templates/drafts/{draft.Id}/publish",ApprovedPublication(updated,fresh))).StatusCode);
        await Change(await Member(manager),state:OrganizationMembershipState.Removed);Assert.True((await manager.Client.GetAsync("/api/research-templates/drafts")).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }
    [PostgresFact]public async Task AllPublishedTypedTemplatesUseExistingClaimsPreparationFrozenFlightsAndPersonalClonesWithoutAuthority()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var program=await Program();var organization=(await Identity()).Id;var token=await Approved(pilot);using var native=app.GetTestClient();native.BaseAddress=new(Origin);native.DefaultRequestHeaders.Authorization=new("Bearer",token.Credential);
        var root=Path.Combine(directory,"local-research");Directory.CreateDirectory(root);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
        foreach(var t in OrganizationTemplates.All())
        {
            var draft=await NewDraft(browser,TemplateJson(t.Reference.Id));var published=await Value<TemplateDocument>(await browser.Send($"/api/research-templates/drafts/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),draft.Revision)));
            var c=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{program.Id}/campaigns",Campaign() with{Template=new("published:"+published.Id,published.Revision)}));c=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{c.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),c.Revision)));
            var card=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/campaigns/{c.Id}",new OrganizationOpportunityRequest(1,Guid.NewGuid(),0,c.Revision,"Issued "+t.Reference.Id,"Shared supported definition","Investigate original evidence","Keep exact criteria",Exclusive())));card=await Value<OrganizationOpportunitySnapshot>(await browser.Send($"/api/organization/opportunities/{card.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),card.Revision)));
            var claim=await PilotCommand(pilot,Claim(card));var original=await Value<OrganizationClaimOriginal>(await native.GetAsync($"/api/member/claims/{claim.Id}/original"));
            var source=MissionStore.VerifiedOrganizationAssignment(Guid.NewGuid(),Origin,organization,original);var preview=store.PreviewOrganizationMission(source,null);
            var prepared=store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,new(1,Guid.NewGuid(),null,preview.PlanSha256,source.IssuedSha256,source.VerifiedClaimRevision,preview.Changes.Select(x=>x.Id).ToArray(),"copy"));
            Assert.Equal(JsonSerializer.Serialize(t.Definition,WireJson.Options),JsonSerializer.Serialize(PortableResearchTemplates.Definition(prepared.Card),WireJson.Options));
            var at=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(at,TelemetrySource.Simulated,"Synthetic P24 integration",40,-75,3000,null,120,120,120,0,0,0,0,0);var flight=store.Create("Original flight",frame,at,plan:prepared.Plan);store.Finish(flight,MissionStatus.Completed,at.AddSeconds(2),"Fixture end");
            var frozen=Assert.Single(store.ResearchOperations(flight).Cards).Card;Assert.Equal(source.IssuedSha256,frozen.OrganizationAssignment!.IssuedSha256);
            var clone=store.CloneResearchTemplate(prepared.Card.Id,new(prepared.Card.Revision,"Independent personal "+t.Reference.Id));Assert.Null(clone.OrganizationAssignment);Assert.Empty(store.ResearchFlights(clone.Id));Assert.Empty(store.ResearchAttempts(clone.Id));
            var exported=JsonSerializer.Serialize(store.ExportResearchTemplate(prepared.Card.Id,new(prepared.Card.Revision)),WireJson.Options);Assert.DoesNotContain(source.ClaimId.ToString(),exported);Assert.DoesNotContain(source.ConnectionId.ToString(),exported);Assert.DoesNotContain(flight.ToString(),exported);
        }
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(8,reopened.OrganizationResearchCards().Length);
    }
    [PostgresFact]public async Task TemplateProposalPagingAndDifferentOrganizationIdentityIsolationUseAuthoritativeRecords()
    {
        await Bootstrap();await Login();using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));var organization=(await Identity()).Id;
        for(var i=0;i<23;i++)await Value<TemplateDocument>(await pilot.Send("/api/research-templates/proposals",new TemplateProposalCommand(1,Guid.NewGuid(),0,organization,TemplateJson(),"Question "+i,"Definition only",true)));
        var first=await Value<OrganizationPage<TemplateDocument>>(await pilot.Client.GetAsync("/api/research-templates/proposals"));Assert.Equal(20,first.Items.Length);Assert.Equal(20,first.NextOffset);var next=await Value<OrganizationPage<TemplateDocument>>(await pilot.Client.GetAsync("/api/research-templates/proposals?offset=20"));Assert.Equal(3,next.Items.Length);Assert.Null(next.NextOffset);Assert.Empty(first.Items.Select(x=>x.Id).Intersect(next.Items.Select(x=>x.Id)));
        var other=new OrganizationServerTests();await other.InitializeAsync();try{await other.Bootstrap();await other.Login();Assert.Equal(HttpStatusCode.Forbidden,(await other.browser.Send("/api/research-templates/proposals",new TemplateProposalCommand(1,Guid.NewGuid(),0,organization,TemplateJson(),"Question","Wrong destination",true))).StatusCode);Assert.Equal(HttpStatusCode.NotFound,(await other.browser.Client.GetAsync($"/api/research-templates/{first.Items[0].Id}/revisions/1")).StatusCode);Assert.Empty((await Value<OrganizationPage<TemplateDocument>>(await other.browser.Client.GetAsync("/api/research-templates/proposals?manage=true"))).Items);}finally{await other.DisposeAsync();}
    }
}
