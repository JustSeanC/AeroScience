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
    [Fact] public void AuthoritativeActionsAndBoundedLiveYieldTrackOriginalSessionEvidence()
    {
        var flight=Start();var armed=coordinator.ScienceCollection(flight.Id);
        Assert.True(armed.Actions.Single(a=>a.Action==CollectionAction.StartData).Available);
        Assert.False(armed.Actions.Single(a=>a.Action==CollectionAction.EndData).Available);
        var a=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);Observe(1);
        var projection=new MissionLiveProjection(store,flight.Id,science).Update();
        var metric=Assert.Single(projection.Input.Yield).Metrics.Single(m=>m.Id=="good-data-sets");
        Assert.Equal(2,metric.Numerator);Assert.Equal(2,metric.Denominator);
        Assert.Equal(a.Id,Assert.Single(projection.Input.Sessions).Session.Id);
        Do(flight.Id,CollectionAction.PauseData);var paused=coordinator.ScienceCollection(flight.Id);
        Assert.True(paused.Actions.Single(a=>a.Action==CollectionAction.ResumeData).Available);
        Assert.False(paused.Actions.Single(a=>a.Action==CollectionAction.StartData).Available);
        Do(flight.Id,CollectionAction.EndData);var b=Do(flight.Id,CollectionAction.StartData).Session!;Observe(1);
        projection=new MissionLiveProjection(store,flight.Id,science).Update();
        Assert.Equal(2,projection.Input.Sessions.Length);Assert.Equal(3,Assert.Single(projection.Input.Yield).Metrics.Single(m=>m.Id=="good-data-sets").Numerator);
        Assert.Equal(b.Id,coordinator.ScienceCollection(flight.Id).CurrentSession!.Id);
        coordinator.End(flight.Id);Assert.All(coordinator.ScienceCollection(flight.Id).Actions,a=>Assert.False(a.Available));
    }
    CollectionControlRequest Request(Guid id,CollectionAction action){var s=store.CollectionCheckpoint(id)!;return new(1,id,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId);}
    CollectionCommitResult Do(Guid id,CollectionAction action)=>coordinator.ControlScience(Request(id,action));
    [Fact] public void BoundedLiveReconciliationDoesNotStarveLaterOriginalsBehindMissingReceipts()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);for(var i=0;i<65;i++)Observe(1);
        // Deliberately missing synthetic scalar rows model unresolved earlier publications.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=science.DatabasePath,Pooling=false}.ToString()))
        {db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM observations WHERE mission_id=$id AND timestamp_utc < $last";cmd.Parameters.AddWithValue("$id",flight.Id.ToString());cmd.Parameters.AddWithValue("$last",clock.ToString("O"));cmd.ExecuteNonQuery();}
        var projection=new MissionLiveProjection(store,flight.Id,science);projection.Update();var second=projection.Update();
        var metric=Assert.Single(second.Input.Yield).Metrics.Single(m=>m.Id=="good-data-sets");Assert.Equal(1,metric.Numerator);Assert.Equal(1,metric.Denominator);Assert.Equal(64,metric.Unassessed);
        coordinator.End(flight.Id);
    }
    [Fact] public void NativeSessionCommandsShareDurableReceiptsAcrossHandlerRestartsAndCannotRetarget()
    {
        var flight=Start();var toolbar=new ToolbarCommandHandler(coordinator);
        ToolbarReply Send(CollectionAction action)
        {
            var state=store.CollectionCheckpoint(flight.Id)!;var id=Guid.NewGuid();
            var request=new CollectionControlRequest(1,flight.Id,id,state.Revision,action,state.CurrentSessionId,ExpectedControlRevision:state.ControlRevision);
            return JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,id,"science-control",Control:request),WireJson.Options)),WireJson.Options)!;
        }
        Assert.True(Send(CollectionAction.StartData).Ok);var a=store.CollectionCheckpoint(flight.Id)!;
        var id=Guid.NewGuid();var end=new CollectionControlRequest(1,flight.Id,id,a.Revision,CollectionAction.EndData,a.CurrentSessionId,ExpectedControlRevision:a.ControlRevision);
        var json=JsonSerializer.Serialize(new ToolbarCommand(1,id,"science-control",Control:end),WireJson.Options);Assert.True(json.Length<=1024);
        Assert.True(JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(json),WireJson.Options)!.Ok);
        Do(flight.Id,CollectionAction.StartData);var b=store.CollectionCheckpoint(flight.Id)!.CurrentSessionId;
        toolbar=new(coordinator);var duplicate=JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(json),WireJson.Options)!;
        Assert.True(duplicate.Ok);Assert.Equal(b,duplicate.Intelligence!.SessionId);Assert.Equal(a.CurrentSessionId,duplicate.Receipt!.SessionId);
        var stale=end with{RequestId=Guid.NewGuid()};var rejected=JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,stale.RequestId,"science-control",Control:stale),WireJson.Options)),WireJson.Options)!;
        Assert.False(rejected.Ok);Assert.Equal(b,rejected.Intelligence!.SessionId);Assert.Contains("control-revision-1",rejected.Capabilities!);
        var unsupported=end with{Version=99,RequestId=Guid.NewGuid()};
        Assert.False(JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,unsupported.RequestId,"science-control",Control:unsupported),WireJson.Options)),WireJson.Options)!.Ok);
        Assert.Equal(b,store.CollectionCheckpoint(flight.Id)!.CurrentSessionId);
        var legacy=JsonSerializer.Deserialize<ToolbarReply>(toolbar.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"science-pause",MissionId:flight.Id),WireJson.Options)),WireJson.Options)!;
        Assert.True(legacy.Ok);Assert.True(legacy.Intelligence!.ManualHold);Assert.Equal(CollectionPhase.Paused,legacy.Intelligence.Phase);
        coordinator.End(flight.Id);
    }
    [Fact] public void NativeBudgetReductionPreservesStableIdentitiesAndOriginalUnicodeEvidence()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);Observe(1);
        var original=coordinator.Snapshot().Telemetry!;var unicode=string.Concat(Enumerable.Repeat("研究😀",200));
        clock=clock.AddSeconds(1);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,unicode,clock),original with{Aircraft=unicode,TimestampUtc=clock}));
        var expected=store.CollectionCheckpoint(flight.Id)!;
        var toolbar=new ToolbarCommandHandler(coordinator,intelligence:(snapshot,collection)=>ToolbarIntelligenceProjection.Create(snapshot,collection) with{Warnings=[unicode,unicode,unicode],TriggerStatus=unicode});
        var encoded=toolbar.Handle(JsonSerializer.Serialize(new ToolbarCommand(1,Guid.NewGuid(),"state"),WireJson.Options));
        var bytes=System.Text.Encoding.UTF8.GetByteCount(encoded);Console.WriteLine($"Native multilingual reduced reply: {bytes} UTF-8 bytes; limit 8000.");Assert.True(bytes<=8000);var reply=JsonSerializer.Deserialize<ToolbarReply>(encoded,WireJson.Options)!;
        Assert.Equal(1,reply.Protocol);Assert.Equal(expected.CurrentSessionId,reply.Intelligence!.SessionId);Assert.Equal(expected.Revision,reply.Intelligence.Revision);Assert.True(reply.Intelligence.DetailsOmitted);
        Assert.Equal(original.Latitude,reply.State.Telemetry!.Latitude);Assert.Equal(unicode,coordinator.Snapshot().Telemetry!.Aircraft);Assert.DoesNotContain("\ufffd",encoded);
        coordinator.End(flight.Id);
    }
    [Fact] public void ControlRevisionToleratesOnlyIngressAndRejectsConfigurationAndAuthorityRoundTrips()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);
        var captured=store.CollectionCheckpoint(flight.Id)!;
        var pause=new CollectionControlRequest(1,flight.Id,Guid.NewGuid(),captured.Revision,CollectionAction.PauseData,captured.CurrentSessionId,ExpectedControlRevision:captured.ControlRevision);
        Observe(1);Observe(1);Assert.True(coordinator.ControlScience(pause).Applied);
        Assert.False(coordinator.ControlScience(pause).Applied);Do(flight.Id,CollectionAction.ResumeData);
        captured=store.CollectionCheckpoint(flight.Id)!;
        var end=new CollectionControlRequest(1,flight.Id,Guid.NewGuid(),captured.Revision,CollectionAction.EndData,captured.CurrentSessionId,ExpectedControlRevision:captured.ControlRevision);
        Do(flight.Id,CollectionAction.PauseData);Do(flight.Id,CollectionAction.ResumeData);
        Assert.Equal(CollectionEventOutcome.Conflict,coordinator.ControlScience(end).Event.Outcome);
        captured=store.CollectionCheckpoint(flight.Id)!;
        end=end with{RequestId=Guid.NewGuid(),ExpectedRevision=captured.Revision,ExpectedControlRevision=captured.ControlRevision};
        coordinator.ConfigureInstrument("ambient-temperature",null,2000);
        Assert.Equal(CollectionEventOutcome.Conflict,coordinator.ControlScience(end).Event.Outcome);
        Assert.NotNull(store.CollectionCheckpoint(flight.Id)!.CurrentSessionId);coordinator.End(flight.Id);
    }
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
    [Fact] public void EvidenceFailureHoldsScienceButKeepsWritableFlightRecordingUntilExplicitRearm()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);
        store.EvidenceFault=point=>{if(point=="before-commit")throw new IOException("Synthetic evidence write interruption");};
        Observe(1);Assert.Single(store.StreamSamples(flight.Id));Assert.Empty(science.StreamObservations(flight.Id));Assert.Empty(store.ScienceIntents(flight.Id));
        store.EvidenceFault=null;Observe(1);Assert.Equal(2,store.StreamSamples(flight.Id).Count());Assert.Empty(science.StreamObservations(flight.Id));
        Do(flight.Id,CollectionAction.RearmAutomation);Observe(1);Assert.Single(science.StreamObservations(flight.Id));
    }
    [Fact] public void InitializationFailureNeverEnablesUnmanagedAcquisition()
    {
        store.EvidenceFault=point=>{if(point=="initialize-before-commit")throw new IOException("Synthetic initialization interruption");};
        Assert.Throws<IOException>(()=>coordinator.Start(new("Failed start",Instruments:[new("ambient-temperature",true)])));
        store.EvidenceFault=null;Observe(1);Assert.Null(coordinator.Snapshot().ActiveMission);Assert.Empty(science.Datasets());
        Assert.Equal(MissionStatus.Interrupted,store.List(10,0).Single().Status);
    }
    [Fact] public void IndependentAtmosphereCannotCreateAnExposureWhileSimulatorPositionIsPaused()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);clock=clock.AddSeconds(1);
        var frame=SimulatedTelemetryProvider.Generate(clock,0) with{IsPaused=true};
        var connection=new ConnectionInfo(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic paused packet",clock);
        coordinator.Apply(new(connection,frame));clock=clock.AddSeconds(1);
        coordinator.Apply(new(connection,null,new(clock,20,29,5,90,.002)));
        Assert.Empty(store.ScienceIntents(flight.Id));Assert.Empty(science.StreamObservations(flight.Id));
    }
    [Fact] public void PreStartPacketCannotPhaseShiftTheCommittedAtmosphericCadence()
    {
        var flight=Start();Do(flight.Id,CollectionAction.StartData);var anchor=clock;
        void Packet(DateTimeOffset utc){var frame=SimulatedTelemetryProvider.Generate(utc,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic queued-start packet",utc),frame,new(utc,17,29,5,90,.002)));}
        Packet(anchor.AddMilliseconds(-100));
        for(var i=1;i<=10;i++)Packet(anchor.AddMilliseconds(i*250));
        var rows=science.StreamObservations(flight.Id).ToArray();Assert.Equal(3,rows.Length);
        Assert.Equal(new[]{250d,1250d,2250d},rows.Select(r=>(r.TimestampUtc-anchor).TotalMilliseconds));
        Assert.All(store.ScienceIntents(flight.Id),i=>Assert.Equal("Published",i.State));
    }
    public void Dispose(){coordinator.Interrupt();research.Dispose();science.Dispose();Directory.Delete(root,true);}
}
