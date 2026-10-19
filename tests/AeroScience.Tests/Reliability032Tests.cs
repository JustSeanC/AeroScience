using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;
public sealed class Reliability032Tests
{
    [Fact] public void DisappearingSidecarsAreBenignButPrimaryAndPermissionsStayStrict()
    {
        Assert.Equal(1024,StorageManager.DatabaseBytes("missions.db",p=>p.EndsWith(".db")?1024:throw new FileNotFoundException("SQLite removed sidecar")));
        Assert.Throws<FileNotFoundException>(()=>StorageManager.DatabaseBytes("missions.db",_=>throw new FileNotFoundException()));
        Assert.Throws<UnauthorizedAccessException>(()=>StorageManager.DatabaseBytes("missions.db",p=>p.EndsWith(".db")?1024:throw new UnauthorizedAccessException()));
        Assert.Throws<IOException>(()=>StorageManager.DatabaseBytes("missions.db",p=>p.EndsWith(".db")?1024:throw new IOException("disk error")));
    }
    [Fact] public async Task RealSqliteCommitsAndConcurrentSizeMonitoringSurviveCheckpointLifecycle()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-wal032-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
            var coordinator=new MissionCoordinator(store,1000,ensureRecordingCapacity:()=>StorageManager.DatabaseBytes(store.DatabasePath));
            var m=coordinator.Start("WAL regression");var started=DateTimeOffset.UtcNow;
            var monitoring=Task.Run(()=>{for(var i=0;i<1000;i++)Assert.True(StorageManager.DatabaseBytes(store.DatabasePath)>0);});
            for(var i=0;i<1000;i++){var f=new TelemetryFrame(started.AddSeconds(i),TelemetrySource.Simulator,"test",40,-75,1000,500,100,100,100,0,0,0,0,0);coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulator,"test",f.TimestampUtc),f));}
            await monitoring;Assert.Equal(1000,coordinator.Snapshot().ActiveMission!.SampleCount);Assert.Null(coordinator.Snapshot().RecordingStop);
            Assert.Equal(1000,store.Detail(m.Id)!.Mission.SampleCount);
        }
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void ReservationsAreConservedAt426Of800AndReleasedAfterAllExitPaths()
    {
        const long mib=1048576;var used=(long)(426.3*mib);var ledger=new ScientificTransferLedger();
        using(var seed=ledger.Acquire(()=>800*mib)){seed.EnsureTotal(used);seed.Charge(used);}
        for(var i=0;i<100;i++)
        {
            using var a=ledger.Acquire(()=>800*mib);using var b=ledger.Acquire(()=>800*mib);
            a.EnsureTotal(64*mib);b.EnsureTotal(64*mib);
            Assert.Equal(128*mib,ledger.Snapshot(800*mib).Reserved);
            Assert.Throws<ScientificAllowanceWaitException>(()=>a.EnsureTotal(400*mib));
        }
        var balance=ledger.Snapshot(800*mib);Assert.Equal(0,balance.Reserved);Assert.Equal(800*mib-used,balance.Available);Assert.Equal(0,balance.Leases);
    }
    [Fact] public void PreflightDoesNotDebitFlightAndUnlimitedOnlyRemovesAggregateVolume()
    {
        var time=DateTimeOffset.UtcNow;var flight=new ScientificTransferLedger(clock:()=>time);var pre=new ScientificTransferLedger(false);
        using(var p=pre.Acquire(()=>null)){p.EnsureTotal(900000000);p.Charge(900000000);}
        Assert.Equal(0,flight.Snapshot(268435456).Spent);Assert.Equal(900000000,pre.TotalBytes);
        using(var f=flight.Acquire(()=>268435456)){f.EnsureTotal(10000);f.Charge(10000);}
        time=time.AddHours(1);Assert.Equal(0,flight.Snapshot(268435456).Spent);
        var budget=new ScientificDownloadBudget(1024);Assert.Throws<IOException>(()=>budget.EnsureCapacity(1025));Assert.Equal(0,budget.Bytes);
    }
    private sealed class FaultProvider:IScientificDataProvider
    {
        public string Id=>"synthetic-failure-boundary";public bool Supports(RemoteKind kind)=>true;
        public Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token){budget.EnsureCapacity(65536);budget.Charge(1024);throw new IOException("Synthetic provider failure after actual charged fixture bytes");}
    }
    [Fact]public async Task FailedConcurrentJobsReleaseReservationsAndPreflightDoesNotDebitFlight()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-032-leases-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var app=new StorageManager(new PreferenceStore(root)).Initialize();using var science=new ScienceStore(app.ScienceLibraryDirectory);science.Initialize();
            var settings=new ScientificAccessSettings(root);settings.Save(new(true,true,MaximumDownloadBytes:1048576,ConcurrentDownloads:2));
            using var automatic=new AutomaticScientificData(science,settings,providers:[new FaultProvider()]);
            for(var i=0;i<20;i++)automatic.Prepare(new(-75+i*.02,40,-75+(i+1)*.02,40.02),[RemoteKind.Terrain],true);
            var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
            while((automatic.Snapshot().Count(s=>s.Status=="Unavailable")<20||automatic.QueueSummary().Downloading>0)&&DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
            Assert.Equal(20,automatic.Snapshot().Count(s=>s.Status=="Unavailable"));var flight=automatic.QueueSummary();Assert.Equal(20*1024,flight.HourlyBytes);Assert.Equal(0,flight.ReservedBytes);
            var area=new GeographicBounds(-74,40,-73.98,40.02);automatic.Prepare(area,[RemoteKind.Terrain],true,scope:"plan:synthetic");
            while((automatic.Snapshot().Single(s=>s.Area==area).Status!="Unavailable"||automatic.QueueSummary().Downloading>0)&&DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
            var after=automatic.QueueSummary();Assert.Equal(flight.HourlyBytes,after.HourlyBytes);Assert.Equal(1024,after.PreflightBytes);Assert.Equal(0,after.PreflightReservedBytes);Assert.Equal(21*1024,after.CombinedNetworkBytes);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]public async Task FreshScienceLibraryCanInitializeButMissingExistingDatabaseStaysAnError()
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-032-first-import-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var app=new StorageManager(new PreferenceStore(root)).Initialize();var missions=new MissionStore(Path.Combine(root,"missions.db"));missions.Initialize();using var science=new ScienceStore(app.ScienceLibraryDirectory);science.Management=new StorageManagement(root,missions,science);
            Assert.Equal(0,science.DatabaseUsageBytes());
            var source=await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"input"),RemoteKind.Terrain));Assert.NotEqual(Guid.Empty,source.Id);Assert.True(science.DatabaseUsageBytes()>0);
            File.Delete(science.DatabasePath);Assert.Throws<IOException>(()=>science.DatabaseUsageBytes());Assert.Throws<IOException>(()=>science.Management.RequireScienceCapacity());
        }
        finally{Directory.Delete(root,true);}
    }

    private sealed class CancellableProvider:IScientificDataProvider
    {
        public string Id=>"synthetic-cancel-boundary";public bool Supports(RemoteKind kind)=>true;
        public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget budget,CancellationToken token){budget.EnsureCapacity(65536);budget.Charge(1024);await Task.Delay(Timeout.Infinite,token);return null;}
    }
    [Theory][InlineData(false)][InlineData(true)]public async Task CancellationAndSettingsChangesReturnEveryHeldReservation(bool configure)
    {
        var root=Path.Combine(Path.GetTempPath(),"aeroscience-032-cancel-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var app=new StorageManager(new PreferenceStore(root)).Initialize();using var science=new ScienceStore(app.ScienceLibraryDirectory);science.Initialize();var settings=new ScientificAccessSettings(root);settings.Save(new(true,true,ConcurrentDownloads:2));using var automatic=new AutomaticScientificData(science,settings,providers:[new CancellableProvider()]);
            automatic.Prepare(new(-75,40,-74.98,40.02),[RemoteKind.Thermal,RemoteKind.Terrain],true);var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
            while((automatic.Snapshot().Count(s=>s.Status=="Downloading")!=2||automatic.QueueSummary().HourlyBytes<2048)&&DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
            Assert.Equal(2048,automatic.QueueSummary().HourlyBytes);Assert.True(automatic.QueueSummary().ReservedBytes>0);
            if(configure)automatic.Configure(settings.Get().Preferences with{OnlineEnabled=false,PrefetchHourlyBytes=null});else automatic.Cancel();
            while(automatic.QueueSummary().Downloading>0&&DateTimeOffset.UtcNow<deadline)await Task.Delay(20);
            Assert.Equal(0,automatic.QueueSummary().ReservedBytes);Assert.Equal(2048,automatic.QueueSummary().HourlyBytes);Assert.Equal(0,automatic.QueueSummary().Downloading);Assert.All(automatic.Snapshot(),s=>Assert.Equal("Unavailable",s.Status));
        }
        finally{Directory.Delete(root,true);}
    }

}
