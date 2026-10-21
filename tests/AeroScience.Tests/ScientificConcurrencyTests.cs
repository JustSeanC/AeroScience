using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using System.Text.Json;
using Xunit;
namespace AeroScience.Tests;

// Only the two physical-I/O concurrency probes run without competing test classes.
// Four production acquisition workers and the blocked writer/recorder still run concurrently.
[CollectionDefinition(ScientificIoCollection.Name,DisableParallelization=true)]
public sealed class ScientificIoCollection { public const string Name="Scientific I/O concurrency"; }

[Collection(ScientificIoCollection.Name)]
public sealed class ScientificConcurrencyTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-concurrency-"+Guid.NewGuid().ToString("N"));
    private readonly MissionStore missions;private readonly ScienceStore science;
    public ScientificConcurrencyTests()
    {
        var paths=new StorageManager(new PreferenceStore(root)).Initialize();
        missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();
        science=new(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
    }
    private static TelemetryFrame Frame(DateTimeOffset time)=>new(time,TelemetrySource.Simulator,"Fixture aircraft",40,-75,1000,328.0839895,140,150,160,0,0,0,0,0,MachNumber:.25);
    private static async Task Wait(Func<bool> predicate)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(!predicate())await Task.Delay(10,timeout.Token);}
    [Fact]public async Task FourInstrumentOneSecondCollectionKeepsRecorderAndIndependentQueuesMoving()
    {
        using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research);
        void Apply(TelemetryFrame frame)=>coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"Isolated fixture; no MSFS connection",frame.TimestampUtc),frame,new(frame.TimestampUtc,15,29,5,90,.002)));
        foreach(var kind in Enum.GetValues<RemoteKind>())await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"parallel-"+kind),kind));
        var configs=RemoteAcquisition.Instruments.Keys.Select(id=>new InstrumentConfiguration(id,true,1000)).ToArray();
        var mission=coordinator.Start(new("Synthetic four-instrument performance fixture",Instruments:configs));
        var anchor=DateTimeOffset.UtcNow;var wall=System.Diagnostics.Stopwatch.StartNew();var cpu=System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;var ingress=new List<double>();
        for(var i=0;i<10;i++)
        {
            var packetUtc=anchor.AddSeconds(i);var wait=packetUtc-DateTimeOffset.UtcNow;
            if(wait>TimeSpan.Zero)await Task.Delay(wait);
            // A fixed logical packet clock avoids cumulative OS timer jitter turning a nominal
            // one-second fixture into skipped slots. Real wall-clock scheduling stays at 1 Hz.
            var timing=System.Diagnostics.Stopwatch.StartNew();Apply(Frame(packetUtc));ingress.Add(timing.Elapsed.TotalMilliseconds);
        }
        try { await Wait(()=>science.Products(mission.Id,200).Length>=36); }
        catch(OperationCanceledException error){throw new TimeoutException("Four instrument fixture did not drain: "+JsonSerializer.Serialize(new{wallSeconds=wall.Elapsed.TotalSeconds,cpuSeconds=(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalSeconds,processors=Environment.ProcessorCount,ingressMilliseconds=ingress,research=coordinator.Snapshot().Research},WireJson.Options),error);}
        Assert.Equal(10,coordinator.Snapshot().ActiveMission!.SampleCount);
        foreach(var id in RemoteAcquisition.Instruments.Keys)Assert.InRange(science.Products(mission.Id,200,instrument:id).Length,9,10);
        Assert.Null(coordinator.Snapshot().RecordingStop);
    }
    [Fact] public async Task BlockedBackgroundOutputDoesNotHoldRecorderOrAtmosphericObservationGate()
    {
        var utc=DateTimeOffset.UtcNow;var seed=missions.Create("Blocked-output fixture",null,utc,ScienceMode.Universal);missions.Finish(seed,MissionStatus.Completed,utc,"Fixture");
        var mission=missions.Detail(seed)!.Mission;
        using var progress=new SemaphoreSlim(0,120);
        var began=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        // Dedicated thread: a deliberately blocked writer must not consume an xUnit/thread-pool continuation slot.
        var worker=Task.Factory.StartNew(()=>science.PublishArtifact(mission.Id,"concurrency-fixture","json","fixture",new{fixture=true},1048576,path=>
        {began.SetResult();release.Wait();File.WriteAllText(path,"{\"fixture\":true}");}),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var recording=Task.Factory.StartNew(()=>
            {
                using var research=new ResearchOperations(missions,science);var recorder=new MissionCoordinator(missions,1000,research:research,utcNow:()=>utc);
                var active=recorder.Start(new StartMissionRequest("Concurrent logical 1 Hz fixture",Instruments:[new("ambient-temperature",true)]));
                for(var i=0;i<120;i++)
                {
                    var f=SimulatedTelemetryProvider.Generate(utc,i);var connection=new ConnectionInfo(ConnectionState.Connected,TelemetrySource.Simulated,"Logical-clock test fixture",f.TimestampUtc);
                    recorder.Apply(new(connection,f,new(f.TimestampUtc,10,29,2,90,.002)));progress.Release();
                }
                Assert.Equal(120,recorder.Snapshot().ActiveMission!.SampleCount);Assert.Equal(0,recorder.Snapshot().Sampling!.MissingIntervals);
                Assert.Equal(120,science.StreamObservations(active.Id).Count());recorder.End(active.Id);
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            try
            {
                // This is a lock/progress test, not a 120-packet burst-throughput benchmark.
                // Keep output blocked until every durable packet has actually progressed.
                for(var packet=0;packet<120;packet++)
                {
                    var packetTimer=System.Diagnostics.Stopwatch.StartNew();var advanced=await progress.WaitAsync(TimeSpan.FromSeconds(5));
                    if(!advanced||packet%20==19)Console.WriteLine($"Blocked-output durable progress: packet {packet+1}/120; advanced={advanced}; wait={packetTimer.Elapsed.TotalMilliseconds:F1} ms; test temp={Path.GetTempPath()}");
                    Assert.True(advanced,$"Recorder made no progress at packet {packet+1} while scientific output was blocked.");
                    Assert.False(worker.IsCompleted);
                }
                await recording;
            }
            finally { release.Set();await recording; }
        }
        finally{release.Set();await worker;}
    }
    public void Dispose(){science.Dispose();Directory.Delete(root,true);}
}
