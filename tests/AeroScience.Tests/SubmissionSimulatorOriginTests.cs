using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class SubmissionStoreTests
{
    private (OrganizationAssignmentIdentity Source,PersonalResearchAttempt Result) SourceQualifiedResult(string simulator)
    {
        var source=Source();var prepared=Prepare(source);
        var origin=new SimulatorOrigin(1,simulator,null,simulator=="xplane12"?"aeroscience.xplane.web":"aeroscience.simconnect",
            "fixture",simulator=="xplane12"?"v1":null,Guid.NewGuid(),"fixture-map")
            {TimestampBasis=SimulatorProvenance.HostReceiptBasis,Limitations=["Synthetic fixture, not genuine simulator acceptance"]};
        var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();var context=fixture.Assembler.Snapshot()!.SampleContext!;
        TelemetryFrame Sample(int second)
        {
            var utc=at.AddSeconds(second);var delta=utc-context.HostReceivedUtc;
            var timing=context with{ConnectionEpoch=origin.ConnectionEpoch,HostReceivedUtc=utc,
                ConfirmationStartedUtc=context.ConfirmationStartedUtc+delta,ConfirmationCompletedUtc=context.ConfirmationCompletedUtc+delta,
                Clock=context.Clock! with{RunningSeconds=100+second,FlightSeconds=10+second},
                ReportedValues=context.ReportedValues!.Select(v=>v with{ConfirmedUtc=v.ConfirmedUtc+delta}).ToArray(),
                Fields=context.Fields.Select(f=>f with{LastChangedUtc=f.LastChangedUtc+delta,LastReceivedUtc=f.LastReceivedUtc+delta,LastConfirmedUtc=f.LastConfirmedUtc+delta}).ToArray()};
            return Frame(second) with{Source=TelemetrySource.Simulator,SimulatorOrigin=origin,SimulatorContext=simulator=="xplane12"?timing:null};
        }
        var flight=store.Create("Source-qualified original",Sample(0),at,plan:prepared.Plan,simulatorOrigin:origin);
        for(var n=0;n<=5;n++)store.Append(flight,Sample(n));store.Finish(flight,MissionStatus.Completed,at.AddSeconds(5),"Synthetic end");
        var result=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(5),CardId:prepared.Card.Id));
        Assert.Equal(ResearchAttemptOutcome.Pass,result.Outcome);Policy(source);return(source,result);
    }

    [Theory] [InlineData("msfs2024")] [InlineData("xplane12")]
    public void NewSourceAwareEvidenceRequiresExactReceiverCapabilityBeforeExplicitApproval(string simulator)
    {
        var original=SourceQualifiedResult(simulator);var request=Request(original.Result);var preview=store.PreviewSubmission(request,science);
        Assert.Equal(3,preview.Manifest.Version);Assert.Equal(simulator,preview.Manifest.SimulatorOrigin!.SimulatorId);Assert.False(preview.CanApprove);
        var row=store.SaveSubmissionDraft(request,science);Assert.Equal(SubmissionDeliveryState.Draft,row.State);Assert.Null(row.Consent);Assert.Null(store.NextApprovedSubmission());
        var connection=Guid.NewGuid();var capabilities=new SubmissionCapabilities(organization,pilot,store.CachedSubmissionPolicy(original.Source.Origin,original.Source.OpportunityId)!,OrganizationDesktopPermission.SubmitEvidence);
        foreach(var unsupported in new[]{capabilities,capabilities with{SimulatorOriginsVersion=2},capabilities with{SimulatorOriginsVersion=1,Permission=OrganizationDesktopPermission.ReadPublished},capabilities with{SimulatorOriginsVersion=1,OrganizationId=Guid.NewGuid()}})
        {
            store.CacheSubmissionCapabilities(connection,original.Source.Origin,unsupported);var p=store.PreviewSubmission(request with{ConnectionId=connection},science);
            Assert.False(p.CanApprove);Assert.Throws<InvalidOperationException>(()=>store.ApproveSubmission(row.Id,new(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,connection,true),science,pilot,organization));
        }
        store.CacheSubmissionCapabilities(connection,original.Source.Origin,capabilities with{SimulatorOriginsVersion=1});
        row=store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row) with{ConnectionId=connection},science);
        preview=store.PreviewSavedSubmission(row.Id,science);Assert.True(preview.CanApprove);
        var approved=store.ApproveSubmission(row.Id,new(1,row.Revision,preview.ManifestSha256,preview.PayloadSha256,preview.PayloadBytes,connection,true),science,pilot,organization);
        Assert.Equal(SubmissionDeliveryState.ApprovedQueued,approved.State);var document=store.ApprovedSubmissionDocument(row.Id,science);SubmissionEvidenceProtocol.Validate(document);
        Assert.Equal(simulator,document.Manifest.SimulatorOrigin!.SimulatorId);Assert.True(SubmissionEvidenceProtocol.Assess(document).MinimumMet);
        Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(document with{Manifest=document.Manifest with{Version=2}}));
        Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(document with{Manifest=document.Manifest with{Version=2,SimulatorOrigin=null}}));
        Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(document with{Manifest=document.Manifest with{SimulatorOrigin=document.Manifest.SimulatorOrigin with{ConnectionEpoch=Guid.NewGuid()}}}));
        Assert.DoesNotContain("simulatorContext",QueueJson());Assert.DoesNotContain(aircraft,QueueJson());
    }

    [Fact] public void LegacyPackagesRemainVersionOneAndVerifyWithoutInventingAnOrigin()
    {
        var original=Quantitative();Policy(original.Source);var row=Approve(store.SaveSubmissionDraft(Request(original.Result),science));
        var document=store.ApprovedSubmissionDocument(row.Id,science);var bytes=JsonSerializer.SerializeToUtf8Bytes(document,WireJson.Options);
        Assert.Equal(1,document.Manifest.Version);Assert.Null(document.Manifest.SimulatorOrigin);Assert.DoesNotContain("simulatorOrigin",System.Text.Encoding.UTF8.GetString(bytes));
        SubmissionEvidenceProtocol.Validate(document);Assert.Equal(bytes,JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<SubmissionDocument>(bytes,WireJson.Options),WireJson.Options));
        Assert.Null(store.Detail(original.Result.FlightId)!.Mission.SimulatorOrigin);
    }
}
