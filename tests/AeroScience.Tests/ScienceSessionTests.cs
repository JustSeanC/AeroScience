using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

public sealed class ScienceSessionTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-session-"+Guid.NewGuid());
    readonly MissionStore store;readonly ScienceStore science;readonly ResearchOperations research;readonly MissionCoordinator coordinator;
    DateTimeOffset clock=DateTimeOffset.UtcNow;
    public ScienceSessionTests(){var settings=new StorageManager(new PreferenceStore(root)).Initialize();store=new(Path.Combine(settings.MissionDirectory,"missions.db"));store.Initialize();science=new(settings.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;research=new(store,science);coordinator=new(store,1000,research:research,utcNow:()=>clock);}
    Mission Start()=>coordinator.Start(new("Continuous flight",Instruments:[new("ambient-temperature",true)],ArmScience:true));
    CollectionControlRequest Request(Guid id,CollectionAction action){var s=store.CollectionCheckpoint(id)!;return new(1,id,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId);}
    CollectionCommitResult Do(Guid id,CollectionAction action)=>coordinator.ControlScience(Request(id,action));
    void Observe(int second){clock=clock.AddSeconds(second);var f=SimulatedTelemetryProvider.Generate(clock,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic session test",clock),f,new(clock,17,29,8,270,.002)));}
    [Fact] public async Task ArmedFlightCollectsTwoIndependentlyIdentifiedSessionsAndExportsOffline()
    {
        var flight=Start();Observe(1);Assert.Empty(science.StreamObservations(flight.Id));Assert.Single(store.StreamSamples(flight.Id));
        var a=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);
        Do(flight.Id,CollectionAction.PauseData);Observe(1);Do(flight.Id,CollectionAction.ResumeData);Observe(1);
        var endA=Request(flight.Id,CollectionAction.EndData);coordinator.ControlScience(endA);Observe(1);
        var b=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);
        Assert.NotEqual(a.Id,b.Id);Assert.Equal(2,b.Sequence);var duplicate=coordinator.ControlScience(endA);
        Assert.False(duplicate.Applied);Assert.Equal(b.Id,duplicate.CurrentState.CurrentSessionId);
        var stale=coordinator.ControlScience(endA with{RequestId=Guid.NewGuid()});Assert.Equal(CollectionEventOutcome.Conflict,stale.Event.Outcome);Assert.Equal(b.Id,stale.CurrentState.CurrentSessionId);
        Do(flight.Id,CollectionAction.EndData);Observe(1);Assert.True(store.CollectionCheckpoint(flight.Id)!.ManualAutomationHold);
        Assert.Equal(7,store.StreamSamples(flight.Id).Count());var rows=science.StreamObservations(flight.Id).ToArray();Assert.Equal(3,rows.Length);
        Assert.Equal(2,rows.Count(r=>r.SessionId==a.Id));Assert.Single(rows,r=>r.SessionId==b.Id);Assert.All(rows,r=>Assert.NotNull(r.IntentId));
        Assert.All(store.ScienceIntents(flight.Id),i=>Assert.Equal("Published",i.State));coordinator.End(flight.Id);
        Assert.All(store.CollectionSessions(flight.Id),s=>Assert.Equal(CollectionSessionStatus.Ended,s.Status));
        using var output=new MemoryStream();await store.ExportJsonAsync(flight.Id,output);using var json=JsonDocument.Parse(output.ToArray());
        Assert.Equal(3,json.RootElement.GetProperty("science").GetProperty("observations").GetArrayLength());Assert.Contains(a.Id.ToString(),System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }
    [Fact] public void OldPacketIsNotCreditedToANewSessionAndClosedSessionsNeverResume()
    {
        var flight=Start();var a=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);Do(flight.Id,CollectionAction.EndData);
        var b=Do(flight.Id,CollectionAction.StartData).Session!;Observe(0);Assert.Single(science.StreamObservations(flight.Id));
        Assert.Equal(CollectionEventOutcome.NoOp,Do(flight.Id,CollectionAction.ResumeData).Event.Outcome);Assert.Equal(b.Id,store.CollectionCheckpoint(flight.Id)!.CurrentSessionId);
        Assert.Equal(CollectionSessionStatus.Ended,store.CollectionSession(flight.Id,a.Id)!.Status);
    }
    [Fact] public void RecoveryClosesOnlyOpenSessionAndNeverAutoResumes()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);Observe(1);Do(flight.Id,CollectionAction.EndData);var b=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);
        coordinator.Interrupt();var reopened=new MissionStore(store.DatabasePath);reopened.Initialize();Assert.Equal(CollectionPhase.Stopped,reopened.CollectionCheckpoint(flight.Id)!.Phase);
        Assert.Equal(CollectionSessionStatus.Interrupted,reopened.CollectionSession(flight.Id,b.Id)!.Status);Assert.Equal(2,science.StreamObservations(flight.Id).Count());
        Assert.Null(new MissionCoordinator(reopened,1000).Snapshot().ActiveMission);
    }
    [Fact] public void ExposureBeforeStartAndDuplicateAuxiliaryPacketsDoNotInventEvidence()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);Observe(-1);Assert.Empty(science.StreamObservations(flight.Id));Observe(2);Observe(0);
        Assert.Single(science.StreamObservations(flight.Id));Assert.Single(store.ScienceIntents(flight.Id));
    }
    [Fact] public void ConfigurationHistoryIsFrozenForEachSession()
    {
        var flight=Start();var a=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);coordinator.ConfigureInstruments([new("ambient-temperature",true,2000)]);Observe(1);
        var saved=store.CollectionSession(flight.Id,a.Id)!;Assert.Equal(2,saved.Configurations.Count(c=>c.Settings.InstrumentId=="ambient-temperature"));
        Assert.Equal(1000,saved.Configurations.First(c=>c.Settings.InstrumentId=="ambient-temperature").Settings.IntervalMs);
        Do(flight.Id,CollectionAction.EndData);var b=Do(flight.Id,CollectionAction.StartData).Session!;
        Assert.Equal(2000,b.Configurations.Single(c=>c.Settings.InstrumentId=="ambient-temperature").Settings.IntervalMs);Assert.All(b.Configurations,c=>Assert.Equal(1,c.Revision));
    }
    [Fact] public void CommittedObservationReconcilesAfterAckCrashWithoutCreatingOrReattributingRows()
    {
        var flight=Start();var a=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);var original=science.StreamObservations(flight.Id).Single();
        // Simulate interruption after Library commit and before mission outbox acknowledgement.
        using(var db=new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False")){db.Open();using var command=db.CreateCommand();command.CommandText="UPDATE science_intents SET state='Pending',product_id=NULL,product_sha256=NULL";command.ExecuteNonQuery();}
        Do(flight.Id,CollectionAction.EndData);Do(flight.Id,CollectionAction.StartData);
        Assert.Equal("Cancelled",store.ScienceIntents(flight.Id).Single().State);store.ReconcileSessionPublications(science);
        var intent=store.ScienceIntents(flight.Id).Single();Assert.Equal("Published",intent.State);Assert.Equal(a.Id,intent.Intent.SessionId);
        Assert.Equal(original,science.StreamObservations(flight.Id).Single());store.ReconcileSessionPublications(science);Assert.Single(science.StreamObservations(flight.Id));
    }
    public void Dispose(){coordinator.Interrupt();research.Dispose();science.Dispose();Directory.Delete(root,true);}
}
