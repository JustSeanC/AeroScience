using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

// Synthetic publications and recorded inputs, using real mission/science archives. No NASA or installed-MSFS claim.
public sealed class SubmissionStoreTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"submission-store-"+Guid.NewGuid());
    readonly MissionStore store;readonly ScienceStore science;readonly DateTimeOffset at=DateTimeOffset.UtcNow.AddMinutes(-5);
    readonly Guid organization=Guid.NewGuid(),campaign=Guid.NewGuid(),pilot=Guid.NewGuid();
    const string aircraft="PRIVATE-RECORDED-AIRCRAFT-7da8ff6a";
    public SubmissionStoreTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;}
    OrganizationAssignmentIdentity Source(string template="custom-flight-test",Guid? org=null)
    {
        var definition=OrganizationTemplates.Resolve(new(template,1));if(definition.FlightRegime is {} flight)definition=definition with{FlightRegime=flight with{HoldSeconds=3}};
        var snapshot=new OrganizationOpportunitySnapshot(1,Guid.NewGuid(),org??organization,Guid.NewGuid(),campaign,2,2,OrganizationPublication.Published,"Submission fixture card","Synthetic fixture","Review selected original evidence","No scientific approval implied",new(OrganizationParticipation.Exclusive,1,1,true,[OrganizationRoles.Pilot],"Awaiting future review"),definition,at.AddMinutes(-3),at.AddMinutes(-2),Guid.NewGuid(),"Fixture author");
        var json=JsonSerializer.Serialize(snapshot,WireJson.Options);var claim=new OrganizationClaimView(Guid.NewGuid(),pilot,null,snapshot.OrganizationId,snapshot.Id,1,2,PreflightReadiness.Hash(snapshot),OrganizationClaimState.Claimed,at.AddMinutes(-1),at.AddMinutes(-1),null,null,snapshot,false,2,"Claimed");
        return MissionStore.VerifiedOrganizationAssignment(Guid.NewGuid(),"https://research.example.org",snapshot.OrganizationId,new(claim,json,"Submission Lab","Evidence Campaign","Fixture pilot",true,at));
    }
    PreparedOrganizationMission Prepare(OrganizationAssignmentIdentity source,Guid? plan=null)
    {var p=store.PreviewOrganizationMission(source,plan);return store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,new(1,Guid.NewGuid(),plan,p.PlanSha256,source.IssuedSha256,source.VerifiedClaimRevision,[],plan is null?"copy":"update"));}
    TelemetryFrame Frame(int second,double speed=120)=>new(at.AddSeconds(second),TelemetrySource.Simulated,aircraft,40,-75,3000,null,speed,speed,speed,0,0,0,0,0);
    (OrganizationAssignmentIdentity Source,PreparedOrganizationMission Prepared,PersonalResearchAttempt Result) Quantitative(bool failing=false)
    {
        var source=Source();var p=Prepare(source);var flight=store.Create("Private source flight",Frame(0),at,plan:p.Plan);for(var i=0;i<=5;i++)store.Append(flight,Frame(i,failing?140:120));store.Finish(flight,MissionStatus.Completed,at.AddSeconds(5),"end");var result=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(5),CardId:p.Card.Id));return(source,p,result);
    }
    SubmissionDraftRequest Request(PersonalResearchAttempt result,Guid? id=null,string[]? keys=null)=>new(1,id??Guid.NewGuid(),0,result.FlightId,[result.Id],keys,"Selected research evidence");
    void Policy(OrganizationAssignmentIdentity s,SubmissionPolicyMode mode=SubmissionPolicyMode.RequireMinimumEvidence,int revision=1)=>store.CacheSubmissionPolicy(s.Origin,SubmissionEvidenceProtocol.DefaultPolicy(s.OpportunityId,at) with{Mode=mode,Revision=revision});
    SubmissionDraft Approve(SubmissionDraft row)
    {var p=store.PreviewSavedSubmission(row.Id,science);return store.ApproveSubmission(row.Id,new(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,Guid.NewGuid(),true),science,row.Manifest.PilotId,row.Manifest.OrganizationId);}
    string QueueJson(){using var db=new SqliteConnection("Data Source="+store.DatabasePath);db.Open();using var c=db.CreateCommand();c.CommandText="SELECT group_concat(payload_json) FROM submission_drafts";return c.ExecuteScalar() as string??"";}

    [Theory][InlineData(false)][InlineData(true)]
    public void QuantitativePassAndMeasuredFailureUseOriginalEvidenceAndReferenceOnlyDrafts(bool failing)
    {
        var f=Quantitative(failing);Policy(f.Source);var request=Request(f.Result);var preview=store.PreviewSubmission(request,science);Assert.True(preview.CanApprove);Assert.True(preview.Assessment.MinimumMet);Assert.Equal(failing?ResearchAttemptOutcome.Fail:ResearchAttemptOutcome.Pass,f.Result.Outcome);
        var row=store.SaveSubmissionDraft(request,science);Assert.Equal(SubmissionDeliveryState.Draft,row.State);Assert.Null(row.Consent);Assert.Null(store.NextApprovedSubmission());Assert.DoesNotContain(aircraft,QueueJson());Assert.DoesNotContain("indicatedAirspeedKnots",QueueJson());
        var approved=Approve(row);Assert.Equal(SubmissionDeliveryState.ApprovedQueued,approved.State);var d=store.ApprovedSubmissionDocument(row.Id,science);Assert.Contains(aircraft,JsonSerializer.Serialize(d,WireJson.Options));Assert.DoesNotContain("issuedJson",JsonSerializer.Serialize(d,WireJson.Options));Assert.DoesNotContain(f.Source.ConnectionId.ToString(),JsonSerializer.Serialize(d,WireJson.Options));Assert.True(SubmissionEvidenceProtocol.Assess(d).MinimumMet);Assert.Equal(row.Id,store.NextApprovedSubmission()!.Id);Assert.Empty(Directory.GetFiles(root,"*.zip",SearchOption.AllDirectories));
    }
    [Fact]public void UnknownPolicyAllowsOfflineDraftButCannotAuthorizeUpload()
    {
        var f=Quantitative();var row=store.SaveSubmissionDraft(Request(f.Result),science);var p=store.PreviewSavedSubmission(row.Id,science);Assert.False(p.PolicyVerified);Assert.False(p.CanApprove);Assert.Throws<InvalidOperationException>(()=>Approve(row));Assert.Equal(SubmissionDeliveryState.Draft,store.SubmissionDraft(row.Id).State);Assert.Null(store.NextApprovedSubmission());
    }
    [Fact]public void RemovingSupportingEvidenceHonorsBothPublishedPoliciesWithoutChangingLocalOutcome()
    {
        var f=Quantitative();Policy(f.Source);var request=Request(f.Result);var all=store.PreviewSubmission(request,science);var keys=all.Manifest.Items.Where(i=>i.Kind!=SubmissionEvidenceKind.Telemetry).Select(i=>i.Key).ToArray();var row=store.SaveSubmissionDraft(request with{SelectedKeys=keys},science);Assert.False(store.PreviewSavedSubmission(row.Id,science).CanApprove);Assert.Throws<InvalidOperationException>(()=>Approve(row));
        Policy(f.Source,SubmissionPolicyMode.AllowIncompleteSubmission,2);var changed=store.SaveSubmissionDraft(MissionStore.SubmissionRequest(row),science);Assert.True(store.PreviewSavedSubmission(row.Id,science).CanApprove);var approved=Approve(changed);Assert.Equal(SubmissionDeliveryState.ApprovedQueued,approved.State);Assert.False(SubmissionEvidenceProtocol.Assess(store.ApprovedSubmissionDocument(row.Id,science)).MinimumMet);Assert.Equal(ResearchAttemptOutcome.Pass,store.ResearchAttempt(f.Result.Id).Outcome);
    }
    [Fact]public void ApprovalBindsExactSelectionDestinationPolicyBytesAndRevision()
    {
        var f=Quantitative();Policy(f.Source);var row=store.SaveSubmissionDraft(Request(f.Result),science);var p=store.PreviewSavedSubmission(row.Id,science);var approval=new SubmissionApproval(1,row.Revision,p.ManifestSha256,p.PayloadSha256,p.PayloadBytes,Guid.NewGuid(),true);
        foreach(var bad in new[]{approval with{ExplicitApproval=false},approval with{PayloadBytes=p.PayloadBytes+1},approval with{PayloadSha256=new('0',64)},approval with{ManifestSha256=new('f',64)},approval with{ExpectedRevision=row.Revision+1}})Assert.ThrowsAny<Exception>(()=>store.ApproveSubmission(row.Id,bad,science,pilot,organization));
        Assert.Throws<InvalidOperationException>(()=>store.ApproveSubmission(row.Id,approval,science,Guid.NewGuid(),organization));var accepted=store.ApproveSubmission(row.Id,approval,science,pilot,organization);Policy(f.Source,SubmissionPolicyMode.AllowIncompleteSubmission,2);Assert.Throws<IOException>(()=>store.ApprovedSubmissionDocument(row.Id,science));Assert.Equal(p.ManifestSha256,accepted.Consent!.ManifestSha256);
    }
    [Fact]public void AlteredOriginalTelemetryCannotBeSubstitutedUnderPriorApproval()
    {
        var f=Quantitative();Policy(f.Source);var row=Approve(store.SaveSubmissionDraft(Request(f.Result),science));
        using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE telemetry SET payload_json=json_set(payload_json,'$.indicatedAirspeedKnots',160) WHERE mission_id=$id";c.Parameters.AddWithValue("$id",f.Result.FlightId.ToString());c.ExecuteNonQuery();}
        Assert.Throws<IOException>(()=>store.ApprovedSubmissionDocument(row.Id,science));Assert.Equal(SubmissionDeliveryState.ApprovedQueued,store.SubmissionDraft(row.Id).State);Assert.DoesNotContain(aircraft,QueueJson());
    }
    [Fact]public void AtmosphericOriginalsCanMeetCollectionMinimumAndDeletionCannotRecreateThem()
    {
        var source=Source(AtmosphericResearch.Template);Policy(source);var p=Prepare(source);var flight=store.Create("Atmosphere",Frame(0),at,plan:p.Plan);for(var i=0;i<=15;i++)store.Append(flight,Frame(i));store.Finish(flight,MissionStatus.Completed,at.AddSeconds(15),"end");var dataset=science.EnsureDataset(store.Detail(flight)!.Mission,new("ambient-temperature",true),store.Research(flight));var session=Guid.NewGuid();
        for(var band=0;band<3;band++)for(var n=0;n<3;n++){var time=at.AddSeconds(1+band*4+n);science.Append([new(0,dataset.Id,flight,"ambient-temperature",time,"temperature",17.1234567-band,"degC",ScienceProvenance.SimulatorReported,"Synthetic retained original atmosphere",40,-75,1100+band*500,ObservationQuality.Valid,time,SessionId:session)]);}
        var result=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(15),CardId:p.Card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);var row=Approve(store.SaveSubmissionDraft(Request(result),science));Assert.DoesNotContain("17.1234567",QueueJson());var doc=store.ApprovedSubmissionDocument(row.Id,science);Assert.Equal(9,doc.Evidence.Sum(i=>i.Data.Observations?.Length??0));Assert.True(SubmissionEvidenceProtocol.Assess(doc).MinimumMet);
        var review=science.PreviewCollectedDeletion(new([flight]),store);science.DeleteCollectedScience(new(review.Id,review.SelectionHash),store,new MissionCoordinator(store,1000).EnterMaintenance);Assert.ThrowsAny<Exception>(()=>store.ApprovedSubmissionDocument(row.Id,science));Assert.Equal(16,store.StreamSamples(flight).Count());Assert.DoesNotContain("17.1234567",QueueJson());Assert.Null(store.SubmissionDraft(row.Id).Receipt);
    }
    [Fact]public void PilotDocumentationIsSelectedAsOriginalNotesAndNotASensorMeasurement()
    {
        var source=Source(ObservationResearch.Template);Policy(source);var p=Prepare(source);var flight=store.Create("Pilot documentation",Frame(0),at,plan:p.Plan);for(var i=0;i<=5;i++)store.Append(flight,Frame(i));const string text="PRIVATE-SCIENTIFIC-NOTE-647dcc81";
        for(var i=1;i<=3;i++)store.AddMarker(flight,new("observation",text+i),at.AddSeconds(i),Frame(i),"PRIVATE-UNRELATED-SNAPSHOT");store.Finish(flight,MissionStatus.Completed,at.AddSeconds(5),"end");var result=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(5),CardId:p.Card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);
        var row=Approve(store.SaveSubmissionDraft(Request(result),science));Assert.DoesNotContain(text,QueueJson());var doc=store.ApprovedSubmissionDocument(row.Id,science);Assert.True(SubmissionEvidenceProtocol.Assess(doc).MinimumMet);var json=JsonSerializer.Serialize(doc,WireJson.Options);Assert.Contains(text,json);Assert.DoesNotContain("PRIVATE-UNRELATED-SNAPSHOT",json);Assert.All(doc.Evidence.SelectMany(i=>i.Data.Documentation??[]),d=>{Assert.StartsWith("Pilot Observation",d.Kind);Assert.Empty(d.SupportingObservationIds);});
    }
    [Fact]public void TwoEntrypointsResumeOneAuthoritativeDraftAndStaleEditsAreRejected()
    {
        var f=Quantitative();Policy(f.Source);var request=Request(f.Result);var first=store.SaveSubmissionDraft(request,science);var second=store.SaveSubmissionDraft(request with{Id=Guid.NewGuid()},science);Assert.Equal(first.Id,second.Id);var updated=store.SaveSubmissionDraft(MissionStore.SubmissionRequest(first) with{Name="Reviewed selection"},science);Assert.Equal(first.Revision+1,updated.Revision);Assert.Throws<InvalidOperationException>(()=>store.SaveSubmissionDraft(MissionStore.SubmissionRequest(first) with{Name="Stale edit"},science));Assert.Equal("Reviewed selection",store.SubmissionDraft(first.Id).Manifest.Name);
    }
    [Fact]public void CancellationAndRevisedSelectionHaveIndependentImmutablePackageIdentities()
    {
        var f=Quantitative();Policy(f.Source);var row=Approve(store.SaveSubmissionDraft(Request(f.Result),science));var cancelled=store.CancelSubmission(row.Id,row.Revision);Assert.Null(store.NextApprovedSubmission());Assert.Throws<InvalidOperationException>(()=>store.ApprovedSubmissionDocument(row.Id,science));var revised=store.ReopenSubmission(row.Id,cancelled.Revision);Assert.NotEqual(row.Id,revised.Id);Assert.Equal(SubmissionDeliveryState.Draft,revised.State);Assert.Null(revised.Consent);Assert.Equal(SubmissionDeliveryState.Cancelled,store.SubmissionDraft(row.Id).State);Assert.Equal(2,store.SubmissionDrafts().Length);
    }
    [Fact]public void InterruptedSendingRecoversOnlyPreviouslyApprovedIdentity()
    {
        var f=Quantitative();Policy(f.Source);var row=Approve(store.SaveSubmissionDraft(Request(f.Result),science));store.ChangeSubmission(row.Id,row.Revision,r=>r with{State=SubmissionDeliveryState.Sending,NextAttemptUtc=null});var restarted=new MissionStore(store.DatabasePath);restarted.Initialize();restarted.RecoverSubmissionDeliveries();var recovered=restarted.SubmissionDraft(row.Id);Assert.Equal(SubmissionDeliveryState.RetryAvailable,recovered.State);Assert.Equal(row.Consent,recovered.Consent);Assert.Equal(PreflightReadiness.Hash(row.Manifest),PreflightReadiness.Hash(recovered.Manifest));Assert.Equal(row.Id,restarted.NextApprovedSubmission()!.Id);
    }
    [Fact]public void SchemaElevenMigrationRetainsOriginalEvidenceAndRollsBackOnInterruption()
    {
        var f=Quantitative();using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var c=db.CreateCommand();c.CommandText="DROP TABLE submission_grants; DROP TABLE submission_policies; DROP TABLE submission_drafts; PRAGMA user_version=11";c.ExecuteNonQuery();}var before=StorageManager.DatabaseFingerprint(store.DatabasePath);store.EvidenceFault=p=>{if(p=="submission-migration-before-commit")throw new IOException("Controlled migration interruption");};Assert.Throws<IOException>(store.Initialize);Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));store.EvidenceFault=null;store.Initialize();Assert.Equal(PreflightReadiness.Hash(f.Result),PreflightReadiness.Hash(store.ResearchAttempt(f.Result.Id)));Assert.Empty(store.SubmissionDrafts());Assert.Contains(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(store.DatabasePath)!,"Backups"),"missions-schema11-*.db"),p=>StorageManager.DatabaseFingerprint(p)==before);
    }

    [Fact]public void PublishedPolicyCacheRejectsRollbackAndSameRevisionSubstitution()
    {
        var f=Quantitative();Policy(f.Source,SubmissionPolicyMode.AllowIncompleteSubmission,2);Assert.Throws<InvalidOperationException>(()=>Policy(f.Source,SubmissionPolicyMode.RequireMinimumEvidence,1));Assert.Throws<InvalidOperationException>(()=>Policy(f.Source,SubmissionPolicyMode.RequireMinimumEvidence,2));Assert.Equal(2,store.CachedSubmissionPolicy(f.Source.Origin,f.Source.OpportunityId)!.Revision);
    }
    [Fact]public void MalformedTypedDocumentsAndMisstatedRecordCountsFailClosed()
    {
        var f=Quantitative();Policy(f.Source);var row=Approve(store.SaveSubmissionDraft(Request(f.Result),science));var doc=store.ApprovedSubmissionDocument(row.Id,science);var m=doc.Manifest;
        foreach(var bad in new[]{doc with{Manifest=m with{Cards=[null!]}},doc with{Manifest=m with{Policies=[null!]}},doc with{Manifest=m with{Items=[null!]}},doc with{Evidence=[null!]},doc with{Manifest=m with{Policies=[m.Policies[0],m.Policies[0]]}}})Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(bad));
        var first=doc.Evidence[0];var changed=first with{Reference=first.Reference with{Records=first.Reference.Records+1}};var packet=doc with{Evidence=doc.Evidence.Select(i=>i==first?changed:i).ToArray(),Manifest=m with{Items=m.Items.Select(i=>i.Key==first.Reference.Key?changed.Reference:i).ToArray()}};Assert.Throws<ArgumentException>(()=>SubmissionEvidenceProtocol.Validate(packet));
    }
    [Fact]public void DeletedOriginalCannotHideBehindResultOnlySelection()
    {
        var f=Quantitative();Policy(f.Source,SubmissionPolicyMode.AllowIncompleteSubmission);var preview=store.PreviewSubmission(Request(f.Result),science);var keys=preview.Manifest.Items.Where(i=>i.Kind!=SubmissionEvidenceKind.Telemetry).Select(i=>i.Key).ToArray();var row=Approve(store.SaveSubmissionDraft(Request(f.Result,keys:keys),science));using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var c=db.CreateCommand();c.CommandText="DELETE FROM telemetry WHERE mission_id=$id";c.Parameters.AddWithValue("$id",f.Result.FlightId.ToString());c.ExecuteNonQuery();}Assert.Throws<IOException>(()=>store.ApprovedSubmissionDocument(row.Id,science));Assert.DoesNotContain(aircraft,QueueJson());
    }

    [Fact]public void ContextStatusDistinguishesDraftQueuedAndMatchingReceiptWithoutSharingOtherCards()
    {
        var f=Quantitative();Policy(f.Source);var row=store.SaveSubmissionDraft(Request(f.Result),science);Assert.Equal(new SubmissionStatusSummary(0,0,1,0,0),store.SubmissionStatusForCard(f.Prepared.Card.Id,f.Result.FlightId));row=Approve(row);Assert.Equal(new SubmissionStatusSummary(0,1,0,0,0),store.SubmissionStatusForCard(f.Prepared.Card.Id,f.Result.FlightId));var c=row.Consent!;var receipt=new SubmissionReceipt(1,Guid.NewGuid(),row.Id,row.Manifest.OrganizationId,row.Manifest.PilotId,row.Manifest.CampaignId,c.ManifestSha256,c.PayloadSha256,c.PayloadBytes,DateTimeOffset.UtcNow,"Received / Awaiting Review",SubmissionEvidenceProtocol.RetentionDisclosure);Assert.Throws<IOException>(()=>store.RecordSubmissionReceipt(row.Id,receipt with{OrganizationId=Guid.NewGuid()}));Assert.Equal(0,store.SubmissionStatusForCard(f.Prepared.Card.Id).ReceivedPackages);store.RecordSubmissionReceipt(row.Id,receipt);Assert.Equal(new SubmissionStatusSummary(1,0,0,0,0),store.SubmissionStatusForCard(f.Prepared.Card.Id,f.Result.FlightId));Assert.Equal(new SubmissionStatusSummary(0,0,0,0,0),store.SubmissionStatusForCard(Guid.NewGuid(),f.Result.FlightId));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
