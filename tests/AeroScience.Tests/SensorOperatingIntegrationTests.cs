using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class SensorOperatingIntegrationTests
{
    [Fact]public void LiveProjectionFollowsActualControllerAndKeepsArchivedEvidenceByteForByte()
    {
        var root=Path.Combine(Path.GetTempPath(),"sensor-status-"+Guid.NewGuid().ToString("N"));
        ResearchOperations? research=null;ScienceStore? science=null;MissionCoordinator? coordinator=null;Guid? id=null;
        try
        {
            var paths=new StorageManager(new PreferenceStore(root)).Initialize();var store=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();
            science=new(paths.ScienceLibraryDirectory);science.Initialize();store.ScienceLibrary=science;research=new(store,science);
            var clock=DateTimeOffset.UtcNow.AddSeconds(-3);coordinator=new(store,1000,research:research,utcNow:()=>clock);
            var config=new InstrumentConfiguration("thermal-imager",true);
            SensorOperatingCard Card()=>coordinator.OperatingIntelligence().Sensors.Instruments.Single(c=>c.InstrumentId==config.InstrumentId);
            void Observe(){clock=clock.AddSeconds(1);var frame=SimulatedTelemetryProvider.Generate(clock,0) with{AltitudeAglFeet=1000,PitchDegrees=0,BankDegrees=0};coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Synthetic sensor fixture",clock),frame));}
            void Control(CollectionAction action){var s=coordinator.ScienceCollection(id!.Value).State!;coordinator.ControlScience(new(1,id.Value,Guid.NewGuid(),s.Revision,action,s.CurrentSessionId));}
            coordinator.ConfigureInstruments([config]);Assert.Equal(SensorOperatingState.NotCollecting,Card().OperatingStatus);
            id=coordinator.Start(new("Synthetic sensor lifecycle",ScienceMode.Realistic,Instruments:[config],ArmScience:true)).Id;
            Assert.Equal(SensorOperatingState.NotCollecting,Card().OperatingStatus);Control(CollectionAction.StartData);var first=Card().SessionId;Observe();
            Assert.Equal(SensorOperatingState.WithinCheckedLimits,Card().OperatingStatus);
            var retained=store.OperatingReports(id.Value).Single();var before=JsonSerializer.Serialize(retained,WireJson.Options);
            Control(CollectionAction.PauseData);Assert.Equal(SensorOperatingState.NotCollecting,Card().OperatingStatus);
            Control(CollectionAction.ResumeData);Assert.Equal(SensorOperatingState.CannotAssess,Card().OperatingStatus);Observe();Assert.Equal(SensorOperatingState.WithinCheckedLimits,Card().OperatingStatus);
            coordinator.ConfigureInstruments([config with{IntervalMs=2000}]);Assert.Null(Card().Report);Assert.Equal(SensorOperatingState.CannotAssess,Card().OperatingStatus);
            clock=DateTimeOffset.UtcNow;Observe();Assert.True(Card().Configuration!.Revision>1);Assert.Equal(Card().Configuration!.Id,Card().Report!.Assessment.ConfigurationId);
            Control(CollectionAction.EndData);Assert.Equal(SensorOperatingState.NotCollecting,Card().OperatingStatus);Control(CollectionAction.StartData);Assert.NotEqual(first,Card().SessionId);Assert.Null(Card().Report);
            Assert.Equal(before,JsonSerializer.Serialize(store.OperatingReports(id.Value,retained.Assessment.AssessedUtc).Single(),WireJson.Options));
            coordinator.End(id.Value);id=null;Assert.Equal(SensorOperatingState.NotCollecting,Card().OperatingStatus);
            Assert.Equal(before,JsonSerializer.Serialize(new MissionStore(store.DatabasePath).OperatingReports(retained.Assessment.MissionId,retained.Assessment.AssessedUtc).Single(),WireJson.Options));
        }
        finally{if(id is {} mission)coordinator?.End(mission);research?.Dispose();science?.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
