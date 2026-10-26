using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AeroScience.Tests;

public sealed class PersonalResearchOperationsTests : IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-personal-ops-"+Guid.NewGuid());
    readonly MissionStore store; readonly ScienceStore science; readonly MissionCoordinator coordinator; readonly ResearchOperations research;
    DateTimeOffset time=DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    readonly DateTimeOffset start=DateTimeOffset.Parse("2026-10-26T00:00:00Z");
    readonly PersonalResearchCard a,b,c; readonly Mission flight;
    public PersonalResearchOperationsTests()
    {
        var settings=new StorageManager(new PreferenceStore(root)).Initialize();
        store=new(Path.Combine(settings.MissionDirectory,"missions.db"));store.Initialize();science=new(settings.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;
        research=new ResearchOperations(store,science);coordinator=new(store,1000,research:research,utcNow:()=>time);
        a=store.SaveResearchCard(new("Card A",new(120,5,3000,100,2)));
        b=store.SaveResearchCard(new("Card B",new(122,4,3050,75,2)));
        c=store.SaveResearchCard(new("Card C",new(150,5,3000,100,2)));
        var plan=store.SavePlan(new(Guid.NewGuid(),"Continuous research sortie",Instruments:[new("ambient-temperature",true)],ResearchCards:[new(a.Id,1),new(b.Id,1),new(c.Id,1)]));
        Observe(0);flight=coordinator.Start(new("One continuous flight",PlanId:plan.Id));
    }
    void Observe(int second,bool paused=false,double speed=120)
    {
        time=start.AddSeconds(second);var frame=new TelemetryFrame(time,TelemetrySource.Simulated,"Synthetic P02 fixture",40,-75,3000,null,speed,speed,speed,0,0,0,0,0,paused);
        coordinator.Apply(new(new(ConnectionState.Connected,frame.Source,"Synthetic fixture",time),frame,new(time,12,29.9,5,270,.002)));
    }
    ResearchCardControl Command(PersonalResearchCard card,ResearchCardAction action)
    { var state=coordinator.PersonalResearch(flight.Id);return new(1,flight.Id,Guid.NewGuid(),state.Revision,action,card.Id,state.Cards.Single(c=>c.Card.Id==card.Id).OpenAttempt?.Id); }
    ResearchCardResult Control(PersonalResearchCard card,ResearchCardAction action)=>coordinator.ControlPersonalResearch(Command(card,action));

    [Fact]public void CardsFreezeBeforeFlightAndIndependentControlsKeepScienceAndFlightContinuous()
    {
        var scienceState=coordinator.ScienceCollection(flight.Id)!.State!;
        Assert.All(coordinator.PersonalResearch(flight.Id).Cards,c=>Assert.False(c.Active));
        Control(a,ResearchCardAction.Activate);Control(b,ResearchCardAction.Activate);
        var first=Control(a,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;
        Control(b,ResearchCardAction.BeginAttempt);Observe(1);Observe(2);Observe(3);
        Control(a,ResearchCardAction.EndAttempt);Control(b,ResearchCardAction.EndAttempt);
        Control(a,ResearchCardAction.BeginAttempt);Observe(4);
        Control(a,ResearchCardAction.Deactivate);Control(b,ResearchCardAction.Deactivate);
        Control(c,ResearchCardAction.Activate);var other=Control(c,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;
        Observe(5);Observe(6);Observe(7);Control(c,ResearchCardAction.EndAttempt);
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);
        Assert.Equal(scienceState.CurrentSessionId,coordinator.ScienceCollection(flight.Id)!.State!.CurrentSessionId);
        Assert.Equal(CollectionPhase.Collecting,coordinator.ScienceCollection(flight.Id)!.State!.Phase);
        Assert.Single(store.List());coordinator.End(flight.Id);
        Assert.Equal(ResearchAttemptOutcome.Pass,store.EvaluateRecordedResearchAttempt(first,Guid.NewGuid()).Outcome);
        Assert.Equal(ResearchAttemptOutcome.Fail,store.EvaluateRecordedResearchAttempt(other,Guid.NewGuid()).Outcome);
        var attempts=store.RecordedResearchAttempts(flight.Id);Assert.Equal(4,attempts.Length);
        var abandoned=attempts.Single(x=>x.Status==ResearchAttemptStatus.Interrupted);
        Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,store.EvaluateRecordedResearchAttempt(abandoned.Id,Guid.NewGuid()).Outcome);
        Assert.True(science.StreamObservations(flight.Id).Any());
    }
    [Fact]public void OnlyIntersectingInclusiveTargetRangesCanBeActiveTogether()
    {
        Assert.True(MissionStore.CompatibleResearchCards(a,b));Assert.False(MissionStore.CompatibleResearchCards(a,c));
        Assert.True(MissionStore.CompatibleResearchCards(a,c with{FlightRegime=c.FlightRegime! with{AirspeedKnots=130}}));
        Assert.False(MissionStore.CompatibleResearchCards(a,b with{FlightRegime=b.FlightRegime! with{AltitudeMslFeet=4000}}));
        Control(a,ResearchCardAction.Activate);Assert.Throws<InvalidOperationException>(()=>Control(c,ResearchCardAction.Activate));
        Assert.Contains("Deactivate Card A",coordinator.PersonalResearch(flight.Id).Cards.Single(x=>x.Card.Id==c.Id).ActivationBlocked);
        Control(a,ResearchCardAction.Deactivate);Control(c,ResearchCardAction.Activate);
    }
    [Fact]public void RevisionAttemptIdentityAndDurableReceiptsPreventDuplicateOrStaleCommands()
    {
        Control(a,ResearchCardAction.Activate);var begin=Command(a,ResearchCardAction.BeginAttempt);
        var receipt=coordinator.ControlPersonalResearch(begin).Receipt;
        Assert.Equal(receipt,coordinator.ControlPersonalResearch(begin).Receipt);Assert.Single(store.RecordedResearchAttempts(flight.Id));
        Assert.Throws<InvalidOperationException>(()=>coordinator.ControlPersonalResearch(begin with{Action=ResearchCardAction.Deactivate}));
        Assert.Throws<InvalidOperationException>(()=>coordinator.ControlPersonalResearch(Command(a,ResearchCardAction.EndAttempt) with{ExpectedAttemptId=Guid.NewGuid()}));
        var old=Command(b,ResearchCardAction.Activate);Control(a,ResearchCardAction.EndAttempt);
        Assert.Throws<InvalidOperationException>(()=>coordinator.ControlPersonalResearch(old));
        coordinator.End(flight.Id);Assert.Equal(receipt,coordinator.ControlPersonalResearch(begin).Receipt);
        Assert.Throws<InvalidOperationException>(()=>Control(b,ResearchCardAction.Activate));
    }
    [Theory][InlineData("paused")][InlineData("gap")][InlineData("no-data")]
    public void MarkedIntervalsNeverBridgePausedMissingOrUnobservedTelemetry(string scenario)
    {
        Control(a,ResearchCardAction.Activate);var id=Control(a,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;
        Observe(1);if(scenario=="paused")Observe(2,true);else if(scenario!="no-data")Observe(4);
        if(scenario=="paused")Observe(3);Control(a,ResearchCardAction.EndAttempt);coordinator.End(flight.Id);
        var result=store.EvaluateRecordedResearchAttempt(id,Guid.NewGuid());Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,result.Outcome);
        Assert.Equal(id,result.RecordedAttemptId);
    }
    [Fact]public void EditingCardDoesNotRewriteFrozenDefinitionOrOriginalSamples()
    {
        store.SaveResearchCard(new(a.Name,a.FlightRegime! with{AirspeedKnots=200},a.Id,1));
        Control(a,ResearchCardAction.Activate);var id=Control(a,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;
        Observe(1);Observe(2);Observe(3);Control(a,ResearchCardAction.EndAttempt);coordinator.End(flight.Id);
        var samples=store.StreamSamples(flight.Id).ToArray();var result=store.EvaluateRecordedResearchAttempt(id,Guid.NewGuid());
        Assert.Equal(1,result.Card.Revision);Assert.Equal(120,result.Card.FlightRegime!.AirspeedKnots);Assert.Equal(ResearchAttemptOutcome.Pass,result.Outcome);
        Assert.Equal(samples,store.StreamSamples(flight.Id));Assert.Equal(3,result.Evidence.SampleCount);
        Assert.Equal(id,store.EvaluateRecordedResearchAttempt(id,result.Id).RecordedAttemptId);
    }
    [Fact]public void InterruptedRecordingReopensInactiveWithNoFabricatedEndEvidenceOrAutoPass()
    {
        Control(a,ResearchCardAction.Activate);time=start.AddSeconds(1);
        var id=Control(a,ResearchCardAction.BeginAttempt).Receipt.AttemptId!.Value;
        // No committed sample after the marker; recovery must not backdate or fabricate one.
        var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();reopened.RecoverInterrupted();
        var state=reopened.ResearchOperations(flight.Id);Assert.All(state.Cards,x=>{Assert.False(x.Active);Assert.Null(x.OpenAttempt);});
        var attempt=reopened.RecordedResearchAttempt(id);Assert.Equal(ResearchAttemptStatus.Interrupted,attempt.Status);
        var result=reopened.EvaluateRecordedResearchAttempt(id,Guid.NewGuid());Assert.Equal(ResearchAttemptOutcome.InsufficientEvidence,result.Outcome);Assert.Equal(0,result.Evidence.SampleCount);
        Assert.Empty(store.StreamSamples(flight.Id));
    }
    [Fact]public void FaultBeforeCommitRollsBackMarkerStateAttemptAndReceiptTogether()
    {
        var command=Command(a,ResearchCardAction.Activate);var revision=store.ResearchOperations(flight.Id).Revision;var events=store.ReplayEvents(flight.Id);
        store.EvidenceFault=point=>{if(point=="research-control-before-commit")throw new IOException("Synthetic interruption");};
        Assert.Throws<IOException>(()=>coordinator.ControlPersonalResearch(command));
        Assert.Equal(revision,store.ResearchOperations(flight.Id).Revision);Assert.Equal(events,store.ReplayEvents(flight.Id));Assert.Empty(store.RecordedResearchAttempts(flight.Id));
        store.EvidenceFault=null;Assert.True(coordinator.ControlPersonalResearch(command).State.Cards.Single(x=>x.Card.Id==a.Id).Active);
    }
    [Fact]public void BeginNeedsFreshUnpausedTelemetryButEndAndDeactivationNeverEndScience()
    {
        Control(a,ResearchCardAction.Activate);Observe(1,true);Assert.Throws<InvalidOperationException>(()=>Control(a,ResearchCardAction.BeginAttempt));
        Observe(2);Control(a,ResearchCardAction.BeginAttempt);Observe(3,true);Control(a,ResearchCardAction.EndAttempt);
        Assert.NotNull(coordinator.Snapshot().ActiveMission);Assert.True(coordinator.PersonalResearch(flight.Id).Cards.Single(x=>x.Card.Id==a.Id).Active);
    }
    [Fact]public void NativeCommandsUseSameDurableAuthorityAndBoundedRepliesWithoutChangingScience()
    {
        var native=new ToolbarCommandHandler(coordinator);var request=Command(a,ResearchCardAction.Activate);
        var packet=new ToolbarCommand(1,request.RequestId,"research-control",MissionId:flight.Id,ResearchControl:request);
        foreach(var invalid in new[]{packet with{MissionId=Guid.NewGuid()},packet with{Offset=1}}){
            using var rejected=JsonDocument.Parse(native.Handle(JsonSerializer.Serialize(invalid,WireJson.Options)));
            Assert.False(rejected.RootElement.GetProperty("ok").GetBoolean());Assert.Equal(0,coordinator.PersonalResearch(flight.Id).Revision);
        }
        var encoded=JsonSerializer.Serialize(packet,WireJson.Options);var reply=native.Handle(encoded);
        Assert.True(Encoding.UTF8.GetByteCount(reply)<8000);using var doc=JsonDocument.Parse(reply);Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        var receipt=coordinator.ControlPersonalResearch(request).Receipt;
        using var repeated=JsonDocument.Parse(native.Handle(encoded));Assert.Equal(receipt.EventId,repeated.RootElement.GetProperty("receipt").GetProperty("eventId").GetInt64());
        using var mismatch=JsonDocument.Parse(native.Handle(JsonSerializer.Serialize(packet with{RequestId=Guid.NewGuid()},WireJson.Options)));Assert.False(mismatch.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(flight.Id,coordinator.Snapshot().ActiveMission!.Id);Assert.Equal(CollectionPhase.Collecting,coordinator.ScienceCollection(flight.Id).State!.Phase);
    }
    [Fact]public void SchemaNineUpgradeLeavesOriginalRowsAndDoesNotInventHistoricActivations()
    {
        Observe(1);var sample=Assert.Single(store.StreamSamples(flight.Id));
        using(var db=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False")){
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE personal_card_attempts; DROP TABLE personal_research_state; DROP TABLE personal_flight_cards; PRAGMA user_version=9";cmd.ExecuteNonQuery();
        }
        var original=StorageManager.DatabaseFingerprint(store.DatabasePath);
        store.EvidenceFault=point=>{if(point=="research-operations-before-commit")throw new IOException("Synthetic migration interruption");};
        Assert.Throws<IOException>(store.Initialize);Assert.Equal(original,StorageManager.DatabaseFingerprint(store.DatabasePath));
        store.EvidenceFault=null;store.Initialize();Assert.Equal(sample,Assert.Single(store.StreamSamples(flight.Id)));Assert.Empty(store.ResearchOperations(flight.Id).Cards);
    }
    public void Dispose(){if(store.Detail(flight.Id)?.Mission.Status==MissionStatus.Recording)coordinator.Interrupt();else research.End();science.Dispose();SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
