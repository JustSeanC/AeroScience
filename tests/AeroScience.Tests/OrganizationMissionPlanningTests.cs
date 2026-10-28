using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

// Synthetic publications/flight inputs with the real local stores/evaluators, not installed-MSFS acceptance.
public sealed class OrganizationMissionPlanningTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"organization-mission-"+Guid.NewGuid());
    readonly MissionStore store;readonly ScienceStore science;
    readonly DateTimeOffset at=DateTimeOffset.Parse("2026-10-27T00:00:00Z");
    public OrganizationMissionPlanningTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;}
    public static IEnumerable<object[]> Templates()=>OrganizationTemplates.All().Select(t=>new object[]{t.Reference.Id});
    OrganizationAssignmentIdentity Source(string template="flight-regime",double? speed=null)
    {
        var definition=OrganizationTemplates.Resolve(new(template,1));if(speed is {} value)definition=definition with{FlightRegime=definition.FlightRegime! with{AirspeedKnots=value,HoldSeconds=3}};
        var snapshot=new OrganizationOpportunitySnapshot(1,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),2,2,OrganizationPublication.Published,"Issued scientific card","Fixture description","Investigate original evidence","Keep original measurements",new(OrganizationParticipation.Exclusive,1,1,true,[OrganizationRoles.Pilot],"Pending future review"),definition,at.AddMinutes(-3),at.AddMinutes(-2),Guid.NewGuid(),"Fixture manager");
        var json=JsonSerializer.Serialize(snapshot,WireJson.Options);var hash=PreflightReadiness.Hash(snapshot);
        var claim=new OrganizationClaimView(Guid.NewGuid(),Guid.NewGuid(),null,snapshot.OrganizationId,snapshot.Id,1,2,hash,OrganizationClaimState.Claimed,at.AddMinutes(-1),at.AddMinutes(-1),null,null,snapshot,false,2,"Current claimed fixture");
        return MissionStore.VerifiedOrganizationAssignment(Guid.NewGuid(),"https://research.example.org",snapshot.OrganizationId,new(claim,json,"Fixture Lab","Fixture Campaign","Fixture Pilot",true,at));
    }
    static OrganizationMissionSave Save(OrganizationMissionPreview p,string mode="copy",string[]? changes=null,Guid? request=null)=>new(1,request??Guid.NewGuid(),p.ExistingPlan?p.Before.Id:null,p.PlanSha256,p.Assignment.IssuedSha256,p.Assignment.VerifiedClaimRevision,changes??[],mode);
    PreparedOrganizationMission Prepare(OrganizationAssignmentIdentity source,Guid? plan=null,string mode="copy",string[]? changes=null)
    {var preview=store.PreviewOrganizationMission(source,plan);return store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,Save(preview,mode,changes));}
    TelemetryFrame Frame(DateTimeOffset time,double speed=120,double altitude=3000)=>new(time,TelemetrySource.Simulated,"Synthetic organizational flight",40,-75,altitude,null,speed,speed,speed,0,0,0,0,0);
    [Theory][MemberData(nameof(Templates))]public void AllEightIssuedDefinitionsPrepareFreezeAndReopenWithoutScientificCoercion(string template)
    {
        var source=Source(template);var preview=store.PreviewOrganizationMission(source,null);var prepared=Prepare(source);
        Assert.Empty(store.List());Assert.Equal(source.IssuedSha256,prepared.Card.OrganizationAssignment!.IssuedSha256);Assert.Equal(template,prepared.Card.TemplateId);
        Assert.Empty(prepared.Plan.Waypoints??[]);Assert.Empty(prepared.Plan.SurveyAreas??[]);Assert.Null(prepared.Plan.IntendedAircraft);Assert.Equal(new(prepared.Card.Id,1),prepared.Plan.ResearchCard);
        Assert.True(MissionStore.CompatibleResearchCards(prepared.Card,prepared.Card));var flight=store.Create("Frozen organization flight",Frame(at),at,plan:prepared.Plan);Assert.Equal(source,Assert.Single(store.ResearchOperations(flight).Cards).Card.OrganizationAssignment);
        store.Finish(flight,MissionStatus.Completed,at.AddSeconds(2),"fixture end");var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(source,reopened.ResearchCard(prepared.Card.Id).OrganizationAssignment);Assert.Single(reopened.OrganizationPreparationHistory(prepared.Card.Id));Assert.Single(reopened.OrganizationResearchCards());
        Assert.DoesNotContain("credential",JsonSerializer.Serialize(prepared.Plan,WireJson.Options),StringComparison.OrdinalIgnoreCase);Assert.Null(prepared.Plan.ResearchStarter);
    }
    [Fact]public void ExistingPlanReviewPreservesRejectedChoicesAndCopiesOrUpdatesOnlyWithExplicitConsent()
    {
        var source=Source(AtmosphericResearch.Template);var original=store.SavePlan(new(Guid.NewGuid(),"Existing aircraft plan",IntendedAircraft:"My aircraft",Waypoints:[new("A",40,-75),new("B",40.1,-75.1)],MinimumAltitudeFeet:1200,MaximumAltitudeFeet:2400,Instruments:[new("ambient-temperature",false,3000),new("thermal-imager",true,4000)],PreflightNotes:"Pilot's original notes"));
        var flight=store.Create("Prior frozen plan",Frame(at),at,plan:original);store.Finish(flight,MissionStatus.Completed,at.AddSeconds(1),"end");var frozen=PreflightReadiness.Hash(store.Research(flight).PlanSnapshot);
        var preview=store.PreviewOrganizationMission(source,original.Id);Assert.Contains(preview.Changes,c=>c.Id=="instrument:ambient-temperature");
        var rejected=store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,Save(preview));Assert.False(rejected.Plan.Instruments!.Single(i=>i.InstrumentId=="ambient-temperature").Enabled);Assert.Equal(PreflightReadiness.Hash(original),PreflightReadiness.Hash(store.Plan(original.Id)));Assert.NotEqual(original.Id,rejected.Plan.Id);Assert.Contains(rejected.Limitations,l=>l.StartsWith("Recommendation rejected"));
        var accepted=store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,Save(preview,"update",["instrument:ambient-temperature"]));Assert.Equal(original.Id,accepted.Plan.Id);Assert.True(accepted.Plan.Instruments!.Single(i=>i.InstrumentId=="ambient-temperature").Enabled);Assert.Equal(3000,accepted.Plan.Instruments!.Single(i=>i.InstrumentId=="ambient-temperature").IntervalMs);Assert.Equal(original.Instruments!.Length,accepted.Plan.Instruments!.Length);Assert.Equal(original.Template,accepted.Plan.Template);Assert.Equal(original.Waypoints,accepted.Plan.Waypoints);Assert.Equal(original.MinimumAltitudeFeet,accepted.Plan.MinimumAltitudeFeet);Assert.Equal(original.MaximumAltitudeFeet,accepted.Plan.MaximumAltitudeFeet);Assert.Equal(original.PreflightNotes,accepted.Plan.PreflightNotes);Assert.Equal(frozen,PreflightReadiness.Hash(store.Research(flight).PlanSnapshot));
        Assert.Equal(2,store.OrganizationPreparationHistory(accepted.Card.Id).Length);Assert.Contains(store.OrganizationPreparationHistory(accepted.Card.Id),a=>a.RejectedChanges.Contains("template"));
        Assert.All(store.OrganizationPreparationHistory(accepted.Card.Id),a=>Assert.Contains(a.ReviewedChanges!,c=>c.Id=="instrument:ambient-temperature"&&c.Before=="Disabled"));
    }
    [Fact]public void ChangedPlansUnsupportedRecommendationsAndInterruptedSavesFailAtomically()
    {
        var source=Source();var original=store.SavePlan(new(Guid.NewGuid(),"Guarded plan"));var preview=store.PreviewOrganizationMission(source,original.Id);var command=Save(preview,"update");
        var changed=store.SavePlan(original with{Name="Another editor saved"});Assert.Throws<InvalidOperationException>(()=>store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,command));Assert.Empty(store.OrganizationResearchCards());Assert.Equal(changed.Name,store.Plan(original.Id)!.Name);
        preview=store.PreviewOrganizationMission(source,original.Id);Assert.Throws<InvalidOperationException>(()=>store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,Save(preview,"update",["unreviewed-change"])));
        command=Save(preview,"update");store.EvidenceFault=p=>{if(p=="organization-preparation-before-commit")throw new IOException("Controlled transaction interruption");};Assert.Throws<IOException>(()=>store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,command));Assert.Empty(store.OrganizationResearchCards());Assert.Equal(PreflightReadiness.Hash(changed),PreflightReadiness.Hash(store.Plan(original.Id)));store.EvidenceFault=null;
        var result=store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,command);Assert.Single(store.OrganizationResearchCards());Assert.Single(store.OrganizationPreparationHistory(result.Card.Id));
    }
    [Fact]public async Task ConcurrentRetriesRestartAndRemovedPlansDoNotDuplicateOrResurrectWork()
    {
        var source=Source();var preview=store.PreviewOrganizationMission(source,null);var command=Save(preview);
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>store.SaveOrganizationMission(source.ConnectionId,source.ClaimId,source,command))));Assert.Single(results,r=>!r.Reused);Assert.Single(store.OrganizationResearchCards());Assert.Single(store.Plans());
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.True(reopened.RetainedOrganizationPreparation(source.ConnectionId,source.ClaimId,command)!.Reused);
        Assert.Throws<InvalidOperationException>(()=>reopened.RetainedOrganizationPreparation(Guid.NewGuid(),source.ClaimId,command));Assert.Throws<InvalidOperationException>(()=>reopened.RetainedOrganizationPreparation(source.ConnectionId,source.ClaimId,command with{SaveMode="update"}));
        Assert.True(reopened.DeletePlan(command.RequestId));Assert.Throws<InvalidOperationException>(()=>reopened.RetainedOrganizationPreparation(source.ConnectionId,source.ClaimId,command));Assert.Empty(reopened.Plans());
    }
    [Fact]public void ModifiedOriginalsClaimSubstitutionAndPersonalClonesCannotAuthorizeOrganizationWork()
    {
        var source=Source();foreach(var bad in new[]{source with{IssuedJson=source.IssuedJson.Replace("Issued scientific card","Another card")},source with{OrganizationId=Guid.NewGuid()},source with{IssuedRevision=3},source with{Origin="https://user:secret@example.org"},source with{Version=2}})Assert.Throws<ArgumentException>(()=>store.PreviewOrganizationMission(bad,null));
        var prepared=Prepare(source);Assert.Throws<ArgumentException>(()=>store.SaveResearchCard(new("Edited issued card",prepared.Card.FlightRegime! with{AirspeedKnots=150},prepared.Card.Id,1)));
        var personal=store.SaveResearchCard(new(prepared.Card.Name,prepared.Card.FlightRegime));Assert.Null(personal.OrganizationAssignment);Assert.NotEqual(prepared.Card.Id,personal.Id);
        Assert.Throws<ArgumentException>(()=>MissionStore.CompatibleResearchCards(prepared.Card with{FlightRegime=prepared.Card.FlightRegime! with{AirspeedKnots=150}},personal));
        Assert.Throws<ArgumentException>(()=>store.CreateResearchPlan(prepared.Card.Id,1));Assert.Single(store.OrganizationResearchCards());
        Assert.Throws<ArgumentException>(()=>store.PrepareResearchStarter(new(1,Guid.NewGuid(),"flight-regime",ResearchEntryIntent.FlyNow,new(prepared.Card.Id,1))));
        var clone=store.PrepareResearchStarter(new(1,Guid.NewGuid(),"flight-regime",ResearchEntryIntent.Customize,new(prepared.Card.Id,1)));Assert.Null(clone.Card.OrganizationAssignment);Assert.NotEqual(prepared.Card.Id,clone.Card.Id);
    }
    [Fact]public void ActualAtmosphericEvidenceAndMissingScienceUseExistingEvaluationAndPermanentDeletion()
    {
        var prepared=Prepare(Source(AtmosphericResearch.Template));var flight=store.Create("Organizational atmosphere",Frame(at),at,plan:prepared.Plan);for(var i=0;i<=20;i++)store.Append(flight,Frame(at.AddSeconds(i),altitude:1000+i*50));store.Finish(flight,MissionStatus.Completed,at.AddSeconds(20),"end");
        var missing=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:prepared.Card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,missing.Outcome);
        var mission=store.Detail(flight)!.Mission;var data=science.EnsureDataset(mission,new("ambient-temperature",true),store.Research(flight));var rows=new List<ScienceObservation>();for(var band=0;band<3;band++)for(var n=0;n<3;n++){var utc=at.AddSeconds(1+band*4+n);rows.Add(new(0,data.Id,flight,"ambient-temperature",utc,"temperature",20-band*3,"degC",ScienceProvenance.SimulatorReported,"Synthetic retained simulator variable fixture",40,-75,1100+band*500,ObservationQuality.Valid,utc,SessionId:Guid.Parse("11111111-1111-4111-8111-111111111111")));}science.Append(rows);
        var result=store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:prepared.Card.Id),science:science);Assert.Equal(ResearchAttemptOutcome.CollectionComplete,result.Outcome);Assert.Equal(prepared.Card.OrganizationAssignment,result.Card.OrganizationAssignment);Assert.True(store.ReviewAtmosphericEvidence(result,science).MatchesSavedEvidence);
        var raw=store.StreamSamples(flight).Count();var deletion=science.PreviewCollectedDeletion(new([flight]),store);Assert.Equal("Completed",science.DeleteCollectedScience(new(deletion.Id,deletion.SelectionHash),store,new MissionCoordinator(store,1000).EnterMaintenance).State);Assert.Equal(raw,store.StreamSamples(flight).Count());Assert.Throws<ArgumentException>(()=>store.ResearchAttempt(result.Id));Assert.Throws<InvalidOperationException>(()=>store.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(20),CardId:prepared.Card.Id),science:science));Assert.Single(store.OrganizationResearchCards());
    }
    [Fact]public void MultipleClaimsManualSwitchingAndNativeProjectionKeepOneFlightAndIndependentScience()
    {
        var first=Prepare(Source("flight-regime",120));var second=Prepare(Source("flight-regime",150),first.Plan.Id,"update");var scientific=Prepare(Source(AtmosphericResearch.Template),second.Plan.Id,"update");
        store.SavePlan(scientific.Plan with{Instruments=[new("ambient-temperature",true)],StartScienceArmed=false});using var research=new ResearchOperations(store,science);var time=at;var coordinator=new MissionCoordinator(store,1000,research:research,utcNow:()=>time);
        void Observe(int n,double speed=120){time=at.AddSeconds(n);var frame=Frame(time,speed);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",time),frame,new(time,12,29.9,5,270,.002)));}
        Observe(0);var flight=coordinator.Start(new("One organizational sortie",PlanId:second.Plan.Id));
        ResearchCardResult Control(PersonalResearchCard c,ResearchCardAction action){var state=coordinator.PersonalResearch(flight.Id);return coordinator.ControlPersonalResearch(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,c.Id,state.Cards.Single(i=>i.Card.Id==c.Id).OpenAttempt?.Id));}
        var session=coordinator.ScienceCollection(flight.Id)!.State!.CurrentSessionId;Assert.NotNull(session);Control(first.Card,ResearchCardAction.Activate);Control(scientific.Card,ResearchCardAction.Activate);Assert.Throws<InvalidOperationException>(()=>Control(second.Card,ResearchCardAction.Activate));var one=Control(first.Card,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;for(var n=1;n<=4;n++)Observe(n);Control(first.Card,ResearchCardAction.EndAttempt);Control(first.Card,ResearchCardAction.Deactivate);Control(second.Card,ResearchCardAction.Activate);var two=Control(second.Card,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;for(var n=5;n<=9;n++)Observe(n,150);Control(second.Card,ResearchCardAction.EndAttempt);
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.Equal(session,coordinator.ScienceCollection(flight.Id)!.State!.CurrentSessionId);Assert.Equal(3,coordinator.PersonalResearch(flight.Id).Cards.Length);Assert.True(coordinator.PersonalResearch(flight.Id).Cards.Single(c=>c.Card.Id==scientific.Card.Id).Active);
        var toolbar=new ToolbarCommandHandler(coordinator);var response=toolbar.Handle(JsonSerializer.Serialize(new{protocol=1,requestId=Guid.NewGuid(),op="research-state",missionId=flight.Id,offset=0},WireJson.Options));Assert.True(System.Text.Encoding.UTF8.GetByteCount(response)<=8000);Assert.DoesNotContain("issuedJson",response);Assert.Contains("Not Submitted",response);Assert.Contains("Fixture Lab",response);
        coordinator.End(flight.Id);Assert.Equal(ResearchAttemptOutcome.Pass,store.EvaluateRecordedResearchAttempt(one,Guid.NewGuid()).Outcome);Assert.Equal(ResearchAttemptOutcome.Pass,store.EvaluateRecordedResearchAttempt(two,Guid.NewGuid()).Outcome);Assert.NotEqual(store.EvaluateRecordedResearchAttempt(one,Guid.NewGuid()).Card.OrganizationAssignment!.ClaimId,store.EvaluateRecordedResearchAttempt(two,Guid.NewGuid()).Card.OrganizationAssignment!.ClaimId);
    }
    [Fact]public void ReconciliationKeepsHistoricalEvidenceAndRejectsUnrelatedConnections()
    {
        var source=Source();var prepared=Prepare(source);var before=PreflightReadiness.Hash(prepared.Card);var snapshot=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(source.IssuedJson,WireJson.Options)!;
        var released=new OrganizationClaimView(source.ClaimId,source.PilotId,null,source.OrganizationId,source.OpportunityId,2,source.IssuedRevision,source.IssuedSha256,OrganizationClaimState.Released,source.AcceptedUtc,source.AcceptedUtc,at.AddMinutes(2),"pilot-released",snapshot,true,4,"Released");var original=new OrganizationClaimOriginal(released,source.IssuedJson,source.OrganizationName,source.CampaignName,source.PilotName,false,DateTimeOffset.UtcNow);
        store.RecordOrganizationClaimCheck(prepared.Card.Id,new(DateTimeOffset.UtcNow,"Unavailable For New Preparation",original,"Claim ended; historical evidence remains pending server interval verification."));Assert.Equal(OrganizationClaimState.Released,store.LatestOrganizationClaimCheck(prepared.Card.Id)!.Original!.Claim.State);Assert.Equal(before,PreflightReadiness.Hash(store.ResearchCard(prepared.Card.Id)));
        Assert.Throws<ArgumentException>(()=>store.RecordOrganizationClaimCheck(prepared.Card.Id,new(DateTimeOffset.UtcNow,"Forged",original with{Claim=released with{PilotId=Guid.NewGuid()}},"Rejected")));Assert.Throws<InvalidOperationException>(()=>MissionStore.VerifiedOrganizationAssignment(source.ConnectionId,source.Origin,source.OrganizationId,original));
    }
    [Fact]public void AutomaticOrganizationalAttemptsPreserveFailuresAndIdempotentOfflineRecovery()
    {
        var prepared=Prepare(Source(FlightRegimeEvaluation.CustomTemplate,120));var time=at;var coordinator=new MissionCoordinator(store,1000,utcNow:()=>time);Mission? flight=null;
        void Observe(int n,double speed){time=at.AddSeconds(n);var frame=Frame(time,speed);coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",time),frame));if(flight is not null)store.ProcessAutomaticAttempts(flight.Id);}
        Observe(0,160);flight=coordinator.Start(new("Automatic organization test",PlanId:prepared.Plan.Id));
        void Control(ResearchCardAction action){var state=coordinator.PersonalResearch(flight.Id);coordinator.ControlPersonalResearch(new(1,flight.Id,Guid.NewGuid(),state.Revision,action,prepared.Card.Id,state.Cards.Single().OpenAttempt?.Id));}
        Control(ResearchCardAction.Activate);Control(ResearchCardAction.EnableAutomatic);for(var n=1;n<=5;n++)Observe(n,120);for(var n=6;n<=8;n++)Observe(n,160);for(var n=9;n<=15;n++)Observe(n,128);coordinator.End(flight.Id);
        for(var n=0;n<12;n++)store.ProcessAutomaticAttempts(flight.Id);store.EvaluateAutomaticResults(flight.Id);var attempts=store.RecordedResearchAttempts(flight.Id);Assert.Equal(2,attempts.Length);Assert.Equal(new[]{ResearchAttemptOutcome.Pass,ResearchAttemptOutcome.Fail},attempts.Select(a=>a.PointEvidence!.Outcome));Assert.All(store.ResearchAttempts(prepared.Card.Id),a=>{Assert.Equal(prepared.Card.OrganizationAssignment,a.Card.OrganizationAssignment);Assert.True(store.ReviewFlightTestEvidence(a).MatchesSavedEvidence);});
        var original=PreflightReadiness.Hash(attempts);store.RecoverAutomaticAttempts(flight.Id);for(var n=0;n<12;n++)store.ProcessAutomaticAttempts(flight.Id);store.EvaluateAutomaticResults(flight.Id);Assert.Equal(original,PreflightReadiness.Hash(store.RecordedResearchAttempts(flight.Id)));
    }
    [Fact]public void SchemaTenUpgradePreservesOriginalCardsAndRollsBackInterruptedOrganizationMetadataMigration()
    {
        var personal=store.SaveResearchCard(new("Unchanged original personal card",new(120,5,3000,100,3)));var saved=PreflightReadiness.Hash(personal);
        using(var db=new SqliteConnection("Data Source="+store.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE IF EXISTS organization_review_cache; DROP TABLE submission_grants; DROP TABLE submission_policies; DROP TABLE submission_drafts; DROP TABLE organization_claim_checks; DROP TABLE organization_mission_preparations; PRAGMA user_version=10";cmd.ExecuteNonQuery();}
        var fingerprint=StorageManager.DatabaseFingerprint(store.DatabasePath);store.EvidenceFault=p=>{if(p=="organization-planning-migration-before-commit")throw new IOException("Controlled schema-11 migration interruption");};Assert.Throws<IOException>(store.Initialize);Assert.Equal(fingerprint,StorageManager.DatabaseFingerprint(store.DatabasePath));
        store.EvidenceFault=null;store.Initialize();Assert.Equal(saved,PreflightReadiness.Hash(store.ResearchCard(personal.Id)));Assert.Empty(store.OrganizationResearchCards());
        foreach(var backup in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(store.DatabasePath)!,"Backups"),"missions-schema10-*.db"))Assert.Equal(fingerprint,StorageManager.DatabaseFingerprint(backup));
        var prepared=Prepare(Source());Assert.Single(store.OrganizationPreparationHistory(prepared.Card.Id));
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
