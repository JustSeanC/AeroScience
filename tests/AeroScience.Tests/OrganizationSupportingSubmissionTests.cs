using System.Net;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    static async Task<(PersonalResearchAttempt Result,ScientificArtifact Geometry,ScientificArtifact Terrain,ScientificSource Source)> SupportingSurvey(SubmissionFixture f,PreparedOrganizationMission prepared)
    {
        var utc=DateTimeOffset.UtcNow;InstrumentConfiguration[] settings=[new("terrain-scanner",true,1000,new(InstrumentProfiles.Generic("terrain-scanner")))];
        var flight=f.Store.Create("SYNTHETIC P06 reference workflow",null,utc,instruments:settings,plan:prepared.Plan);f.Store.InitializeCollectionEvidence(new(1,flight,0,CollectionPhase.Armed,CollectionAuthority.Manual,ControlRevision:0));
        var controller=new ScienceCollectionController(f.Store,flight,()=>settings);
        void Control(CollectionAction action,int second){var state=controller.State;controller.Control(new(1,flight,Guid.NewGuid(),state.Revision,action,state.CurrentSessionId),utc.AddSeconds(second));}
        Control(CollectionAction.StartData,0);
        for(var i=0;i<2;i++){
            var p=Geodesy.Direct(new(40,-75),0,i*100);var at=utc.AddSeconds(i);var frame=new TelemetryFrame(at,TelemetrySource.Simulated,"SYNTHETIC P06 archived pose",p.Latitude,p.Longitude,5000,1000,120,120,400,0,0,0,0,0);
            var packet=new CollectionInputReceipt("synthetic-p06-"+i,new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic fixture",at),frame));var state=controller.State;var next=state with{Revision=state.Revision+1};
            var e=new CollectionEvent(1,flight,next.Revision,at,at,state.CurrentSessionId,CollectionAction.Unknown,CollectionEventOrigin.Engine,CollectionEventOutcome.Applied,state.Phase,state.Phase,state.Authority,MissionStore.IngressReason,[MissionStore.DescribeCollectionInput(packet)],ExpectedSessionId:state.CurrentSessionId);
            f.Store.CommitCollectionEvidence(new(next,e,Inputs:[packet],FlightSample:frame,EngineDecisionId:Guid.NewGuid()));
        }
        Control(CollectionAction.EndData,2);f.Store.Finish(flight,MissionStatus.Completed,utc.AddSeconds(2),"Synthetic reference fixture complete");
        var wait=utc.AddSeconds(2)-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait); // Never backdate server claim-time authorization.
        var result=f.Store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,utc,utc.AddSeconds(2),CardId:prepared.Card.Id),science:f.Science);
        var source=await f.Science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(f.Root,"synthetic-p06-dem"),RemoteKind.Terrain));var geometry=ContinuousTerrainSurveys.Build(f.Science,f.Store,flight);
        var job=TerrainElevations.Create(f.Science,f.Store,flight,new(geometry.Id,[new(source.Id,source.ManifestSha256,Surface:"dem",LocalDefinitionAcknowledged:true)],new(TileSize:4,MaximumScratchBytes:4194304)));f.Science.ControlTerrainJob(job.Id,new(Guid.NewGuid(),job.Revision,"start"));
        return(result,geometry,TerrainElevations.Process(f.Science,f.Store,job.Id),source);
    }
    [PostgresFact] public async Task P06SupportingReferencesRequireExplicitApprovalReceiveExactMetadataAndCannotApproveUnsupportedScience()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card("terrain-temperature-survey",incomplete:true);var source=await SupportingSurvey(f,c.Prepared);var before=PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Store,f.Science,source.Result.FlightId));
        var request=new SubmissionDraftRequest(2,Guid.NewGuid(),0,source.Result.FlightId,[source.Result.Id],null,"Explicit P06 reference package",f.Upload,SupportingProductIds:[source.Geometry.Id,source.Terrain.Id]);
        var row=f.Store.SaveSubmissionDraft(request,f.Science);Assert.Equal(2,row.Manifest.Version);Assert.Null(row.Consent);Assert.Null(f.Store.NextApprovedSubmission());Assert.False(f.Store.PreviewSavedSubmission(row.Id,f.Science).CanApprove);Assert.Equal(0,await f.Received());
        row=await f.ReviewApprove(row);var document=f.Store.ApprovedSubmissionDocument(row.Id,f.Science);var references=document.Evidence.Where(i=>i.Data.SupportingProduct is not null).ToArray();Assert.Equal(2,references.Length);Assert.All(references,i=>Assert.Equal(0,i.Reference.Records));
        Assert.False(SubmissionEvidenceProtocol.Assess(document).MinimumMet);using var delivery=f.Delivery();var received=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.ServerReceived,received.State);Assert.Equal(1,await f.Received());
        var retained=await ReviewGet<SubmissionDocument>(f.Server,$"/api/organization/submissions/{document.Manifest.PilotId}/{row.Id}");Assert.Equal(PreflightReadiness.Hash(document),PreflightReadiness.Hash(retained));
        var reference=retained.Evidence.Single(i=>i.Data.SupportingProduct?.Kind==TerrainElevations.IndexKind).Data.SupportingProduct!;Assert.Equal(source.Source.ManifestSha256,Assert.Single(reference.Sources).ManifestSha256);
        var snapshot=await ReviewGet<ResearchReviewSnapshot>(f.Server,ReviewPath(received.Receipt!.Id));Assert.All(snapshot.Requirements,r=>Assert.Equal(ResearchReviewState.AwaitingReview,r.State));Assert.All(snapshot.Requirements,r=>Assert.False(r.Requirement.ApprovalSupported));
        foreach(var r in snapshot.Requirements)Assert.False(SubmissionReviewEvidence.Assess(retained,[retained],r.Requirement.CardId,r.Requirement.Key).CanApprove);
        Assert.Equal(before,PreflightReadiness.Hash(MissionEvaluationArchive.Build(f.Store,f.Science,source.Result.FlightId)));Assert.Equal(source.Result.Outcome,f.Store.ResearchAttempt(source.Result.Id).Outcome);
        Assert.DoesNotContain("supportingProduct\"",JsonSerializer.Serialize(f.Store.SubmissionDraft(row.Id),WireJson.Options)); // Local queue stores references, never a product-data copy.
        using var duplicate=await f.Packet(document,row.Consent);Assert.Equal(HttpStatusCode.OK,duplicate.StatusCode);Assert.Equal(1,await f.Received());
    }
    [PostgresFact] public async Task P06LegacyCapabilityCannotApproveOrReceiveADowngradedSupportingPackage()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card("terrain-temperature-survey",incomplete:true);var source=await SupportingSurvey(f,c.Prepared);
        var row=f.Store.SaveSubmissionDraft(new(2,Guid.NewGuid(),0,source.Result.FlightId,[source.Result.Id],null,"Version negotiation fixture",f.Upload,SupportingProductIds:[source.Terrain.Id]),f.Science);
        f.Server.SupportingFormatOverride=0;await Assert.ThrowsAsync<OrganizationSubmissionReviewRequired>(()=>f.Desktop.RefreshSubmissionPolicies(f.Upload,row,f.Store,default));Assert.False(f.Store.PreviewSavedSubmission(row.Id,f.Science).CanApprove);Assert.Equal(0,f.Server.SubmissionWrites);
        f.Server.SupportingFormatOverride=null;row=await f.ReviewApprove(row);f.Server.SupportingFormatOverride=0;using var delivery=f.Delivery();var held=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.HeldEvidenceMissing,held.State);Assert.Equal(0,f.Server.SubmissionWrites);Assert.Equal(0,await f.Received());
    }
    [PostgresFact] public async Task P06DeletedSupportingSourceHoldsApprovedDeliveryWithoutChangingOriginalEvidenceOrRecreatingIt()
    {
        await using var f=await SubmissionFixture.Create();var c=await f.Card("terrain-temperature-survey",incomplete:true);var source=await SupportingSurvey(f,c.Prepared);
        var row=f.Store.SaveSubmissionDraft(new(2,Guid.NewGuid(),0,source.Result.FlightId,[source.Result.Id],null,"Deleted source fixture",f.Upload,SupportingProductIds:[source.Terrain.Id]),f.Science);row=await f.ReviewApprove(row);
        File.Delete(Path.Combine(f.Science.DirectoryPath,source.Source.RelativeDirectory,source.Source.Files[0].RelativePath));
        using var delivery=f.Delivery();var held=await delivery.Send(row.Id,default);Assert.Equal(SubmissionDeliveryState.HeldEvidenceMissing,held.State);Assert.Equal(0,f.Server.SubmissionWrites);Assert.Equal(0,await f.Received());Assert.Equal(source.Result.Outcome,f.Store.ResearchAttempt(source.Result.Id).Outcome);
    }
}
