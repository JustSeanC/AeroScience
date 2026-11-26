using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

public sealed class SimulatorSafetyTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-p03-safety",Guid.NewGuid().ToString("N"));
    private MissionStore Store(){var store=new MissionStore(Path.Combine(directory,"missions.db"));store.Initialize();return store;}
    private static TelemetryUpdate XPlane(DateTimeOffset now,bool approved=false)
    {
        var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();var update=fixture.Assembler.Snapshot()!;
        return update with {Frame=update.Frame! with{TimestampUtc=now}, SampleContext=update.SampleContext! with{Capabilities=update.SampleContext.Capabilities with{ProductionRecordingReady=approved,ScienceIntegrationReady=approved}}};
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void XPlaneCannotGrantItsOwnProductionPermissionsOrWriteMissionScience(bool forgedReady)
    {
        var store=Store();var library=Path.Combine(directory,"science");Directory.CreateDirectory(library);
        PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{libraryId=Guid.NewGuid(),formatVersion=1});
        using var science=new ScienceStore(library);science.Initialize();
        var policy=new SimulatorAccessPolicy("xplane12");using var research=new ResearchOperations(store,science,simulatorAccess:policy);
        research.Configure([new("ambient-temperature",true)]);var service=new MissionCoordinator(store,1000,research:research,simulatorAccess:policy);
        var now=DateTimeOffset.UtcNow;var update=XPlane(now,forgedReady);service.Apply(update);
        var snapshot=service.Snapshot();Assert.Equal(ConnectionState.Connected,snapshot.Connection.State);Assert.NotNull(snapshot.Telemetry);
        Assert.Null(snapshot.Telemetry.Environment);Assert.Null(snapshot.Telemetry.Atmosphere);Assert.Null(snapshot.Research!.Atmosphere);
        Assert.False(snapshot.Simulator!.ProductionRecordingReady);Assert.False(snapshot.Simulator.ScienceIntegrationReady);
        Assert.All(snapshot.Research.Instruments,i=>Assert.Empty(i.Measurements));
        Assert.Throws<InvalidOperationException>(()=>service.Start("Legacy start"));
        Assert.Throws<InvalidOperationException>(()=>service.Start(new("Armed science",ArmScience:true)));
        var flight=Guid.NewGuid();Assert.Throws<InvalidOperationException>(()=>service.SetSciencePaused(flight,false));
        foreach(var action in Enum.GetValues<CollectionAction>().Where(a=>a!=CollectionAction.Unknown))
            Assert.Throws<InvalidOperationException>(()=>service.ControlScience(new(1,flight,Guid.NewGuid(),0,action,null)));
        foreach(var action in Enum.GetValues<ResearchCardAction>().Where(a=>a!=ResearchCardAction.Unknown))
            Assert.Throws<InvalidOperationException>(()=>service.ControlPersonalResearch(new(1,flight,Guid.NewGuid(),0,action,Guid.NewGuid(),null)));
        Assert.Throws<InvalidOperationException>(()=>research.SetPaused(false));
        for(var i=0;i<5;i++){service.Apply(update);research.Observe(update,update.Frame);}
        Assert.Empty(store.List(10,0));Assert.Null(service.Snapshot().ActiveMission);Assert.Empty(science.Datasets());
    }
    [Fact] public void LegacyDefaultCoordinatorAlsoRejectsP02FramesBeforeMsfsEnrichmentOrRecording()
    {
        var service=new MissionCoordinator(Store(),1000);service.Apply(XPlane(DateTimeOffset.UtcNow));
        Assert.Null(service.Snapshot().Telemetry);Assert.False(service.LiveOperationsAllowed);Assert.Throws<InvalidOperationException>(()=>service.Start("Wrong source"));
        var now=DateTimeOffset.UtcNow;service.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic MSFS regression fixture",now),SimulatedTelemetryProvider.Generate(now,0)));
        Assert.True(service.LiveOperationsAllowed);Assert.Null(service.Snapshot().Simulator);Assert.NotNull(service.Start("Existing MSFS path"));
    }
    [Fact] public void StaleMissingAndForeignReadOnlyTelemetryClearsInsteadOfReusingCachedPosition()
    {
        var now=DateTimeOffset.UtcNow;var service=new MissionCoordinator(Store(),1000,utcNow:()=>now,simulatorAccess:new("xplane12"));var update=XPlane(now);
        service.Apply(update);Assert.NotNull(service.Snapshot().Telemetry);
        now=now.AddSeconds(3);Assert.Null(service.Snapshot().Telemetry);service.Apply(update);Assert.Null(service.Snapshot().Telemetry);Assert.Equal(ConnectionState.Initializing,service.Snapshot().Connection.State);
        service.Apply(update with{Frame=null});Assert.Null(service.Snapshot().Telemetry);
        service.Apply(update with{Frame=update.Frame! with{TimestampUtc=now},Connection=update.Connection with{Origin=update.Connection.Origin! with{SimulatorId="msfs2024"}}});
        Assert.Null(service.Snapshot().Telemetry);Assert.Equal("SimulatorSourceMismatch",service.Snapshot().Connection.Message);
        service.Apply(update with{Frame=update.Frame! with{TimestampUtc=now},Connection=update.Connection with{State=ConnectionState.Disconnected}});Assert.Null(service.Snapshot().Telemetry);
    }
    [Fact] public void SwitchingSourceDoesNotMutateHistoricalEvidenceOrPreventOfflinePlanning()
    {
        var store=Store();var legacy=new MissionCoordinator(store,1000);var start=DateTimeOffset.UtcNow.AddMinutes(-5);
        var flight=legacy.Start("Historical flight");legacy.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Fixture",start),SimulatedTelemetryProvider.Generate(start,0)));legacy.End(flight.Id);
        var before=JsonSerializer.Serialize(store.Detail(flight.Id),WireJson.Options);var samples=JsonSerializer.Serialize(store.Samples(flight.Id,10,0),WireJson.Options);
        var plan=store.SavePlan(new(Guid.NewGuid(),"Offline plan"));var selected=new MissionCoordinator(store,1000,simulatorAccess:new("xplane12"));selected.SelectPlan(plan.Id);selected.Apply(XPlane(DateTimeOffset.UtcNow));
        Assert.Equal(plan.Id,selected.Snapshot().SelectedPlanId);Assert.Equal(before,JsonSerializer.Serialize(store.Detail(flight.Id),WireJson.Options));Assert.Equal(samples,JsonSerializer.Serialize(store.Samples(flight.Id,10,0),WireJson.Options));
        Assert.Null(selected.Snapshot().ActiveMission);Assert.Equal(TelemetrySource.Simulated,store.Detail(flight.Id)!.Mission.Source);
    }
    [Fact] public void ForeignXPlanePacketCannotEnterAnAlreadyRecordingMsfsFlight()
    {
        var store=Store();var now=DateTimeOffset.UtcNow;var service=new MissionCoordinator(store,1000,utcNow:()=>now);
        var initial=SimulatedTelemetryProvider.Generate(now,0) with{Source=TelemetrySource.Simulator,Aircraft="Explicit MSFS host fixture"};
        service.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"Synthetic MSFS fixture",now),initial));
        var flight=service.Start("Preserve original MSFS evidence");now=now.AddSeconds(1);
        service.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"Synthetic MSFS fixture",now),initial with{TimestampUtc=now}));
        // Compare durable rows, including original payloads/evidence, rather than
        // the live DurationSeconds display which advances without any DB write.
        var before=StorageManager.DatabaseFingerprint(store.DatabasePath);var original=store.Samples(flight.Id,10,0).ToArray();Assert.Single(original);
        service.Apply(XPlane(now,approved:true));
        Assert.Equal(flight.Id,service.Snapshot().ActiveMission!.Id);Assert.False(service.LiveOperationsAllowed);Assert.Null(service.Snapshot().Telemetry);
        Assert.Equal(before,StorageManager.DatabaseFingerprint(store.DatabasePath));Assert.Equal(original,store.Samples(flight.Id,10,0));
        Assert.Throws<InvalidOperationException>(()=>service.ControlScience(new(1,flight.Id,Guid.NewGuid(),0,CollectionAction.ResumeData,null)));
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
