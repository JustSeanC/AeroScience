using System.Diagnostics;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
namespace AeroScience.Tests;

// Synthetic simulated time; elapsed build/serialization time is measured separately.
// No installed simulator, network endpoint, or owner database is used.
public sealed class Storage081MeasurementsTests(ITestOutputHelper output)
{
    [Fact] public async Task RepresentativeSourceProofUsesRealRecordingScienceAndExportPaths()
    {
        var results=new List<object>();const int samples=300;
        foreach(var simulator in new[]{"msfs2024","xplane12"})
        {
            var root=Path.Combine(Path.GetTempPath(),"p05-storage-"+Guid.NewGuid().ToString("N"));
            try{
                var fixture=new XPlaneAssemblerFixture();fixture.Seed();fixture.Confirm();
                var store=new MissionStore(Path.Combine(root,"Missions","missions.db"));store.Initialize();
                var folder=Path.Combine(root,"Science");Directory.CreateDirectory(folder);PreferenceStore.WriteAtomic(Path.Combine(folder,StorageManager.LibraryManifest),new{libraryId=Guid.NewGuid(),formatVersion=1});
                using var science=new ScienceStore(folder);science.Initialize();store.ScienceLibrary=science;
                var policy=new SimulatorAccessPolicy(simulator,()=>fixture.Time.GetUtcNow());using var research=new ResearchOperations(store,science,simulatorAccess:policy);
                research.Configure([new("ambient-temperature",true),new("ambient-pressure",true),new("ambient-wind",true),new("ambient-density",true)]);
                var coordinator=new MissionCoordinator(store,1000,research:research,simulatorAccess:policy,utcNow:()=>fixture.Time.GetUtcNow());
                void Apply(){var update=fixture.Assembler.Snapshot()!;if(simulator=="msfs2024")update=update with{Connection=update.Connection with{Origin=null},SampleContext=null};coordinator.Apply(update);}
                Apply();var baseline=StorageManager.DatabaseBytes(store.DatabasePath)+StorageManager.DatabaseBytes(science.DatabasePath);
                var allocated=GC.GetTotalAllocatedBytes();var clock=Stopwatch.StartNew();var mission=coordinator.Start("Synthetic P05 storage comparison");
                long maxWorkingSet=0;
                for(var i=1;i<=samples;i++){
                    fixture.Time.Advance(TimeSpan.FromSeconds(1));fixture.Confirm(i+1);Apply();
                    if(i%50==0){using var process=Process.GetCurrentProcess();process.Refresh();maxWorkingSet=Math.Max(maxWorkingSet,process.WorkingSet64);}
                }
                coordinator.End(mission.Id);clock.Stop();var count=science.StreamObservations(mission.Id).LongCount();
                Assert.Equal(samples,store.Detail(mission.Id)!.Mission.SampleCount);Assert.Equal(samples*5,count);
                using var archive=new MemoryStream();await store.ExportJsonAsync(mission.Id,archive);
                var persisted=Assert.Single(store.Samples(mission.Id,1,0));Assert.Equal(simulator,persisted.SimulatorOrigin!.SimulatorId);
                Assert.Equal(simulator=="xplane12",persisted.SimulatorContext?.ReportedValues is {Length:>0});
                var originBytes=JsonSerializer.SerializeToUtf8Bytes(mission.SimulatorOrigin,WireJson.Options).Length;
                var sampleBytes=JsonSerializer.SerializeToUtf8Bytes(persisted,WireJson.Options).Length;
                var observationBytes=JsonSerializer.SerializeToUtf8Bytes(science.StreamObservations(mission.Id).First(),WireJson.Options).Length;
                results.Add(new{simulator,synthetic=true,samples,simulatedDurationSeconds=samples,observations=count,originBytes,sampleBytes,observationBytes,
                    missionDatabaseBytes=StorageManager.DatabaseBytes(store.DatabasePath),scienceDatabaseBytes=StorageManager.DatabaseBytes(science.DatabasePath),
                    totalDatabaseGrowthBytes=StorageManager.DatabaseBytes(store.DatabasePath)+StorageManager.DatabaseBytes(science.DatabasePath)-baseline,
                    jsonExportBytes=archive.Length,recordingAndSerializationElapsedMilliseconds=clock.Elapsed.TotalMilliseconds,
                    managedAllocatedBytes=GC.GetTotalAllocatedBytes()-allocated,processWorkingSetSampleMaximumBytes=maxWorkingSet,
                    limitations="Synthetic 1 Hz input, five atmospheric observations per sample; allocations/working set include shared test-process activity. Not installed-simulator endurance, sustainable cadence, FPS, or a reservation/production storage estimate."});
            }finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
        }
        var report=JsonSerializer.Serialize(new{format="aeroscience-p05-storage-measurements",version=1,runtime=Environment.Version.ToString(),os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),results},new JsonSerializerOptions{WriteIndented=true});
        output.WriteLine(report);
        var receipt=Environment.GetEnvironmentVariable("AEROSCIENCE_P05_MEASUREMENTS");
        if(!string.IsNullOrWhiteSpace(receipt)){var path=Path.GetFullPath(receipt);Directory.CreateDirectory(Path.GetDirectoryName(path)!);if(File.Exists(path))throw new IOException("Preserve the existing measurement receipt; select a fresh path.");File.WriteAllText(path,report);}
    }
}
