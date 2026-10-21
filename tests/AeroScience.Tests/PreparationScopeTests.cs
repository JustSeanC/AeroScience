using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class PreparationScopeTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"preparation-scope-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;private readonly ScientificAccessSettings settings;
    public PreparationScopeTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();settings=new(root);}
    public static MissionPlan LongPlan()=>new(Guid.NewGuid(),"204 actual route regions",Waypoints:[new("A",.001,-75.99),new("B",.001,-67.87)],Instruments:[new("terrain-scanner",true),new("thermal-imager",true)],UpdatedUtc:DateTimeOffset.UtcNow);
    private static MissionPlan Ordered()=>new(Guid.NewGuid(),"Ordered research",Waypoints:[new("Transit",40,-75.15,Id:Guid.NewGuid()),new("Start",40,-75.1,Id:Guid.NewGuid()),new("Middle",40,-75,Id:Guid.NewGuid()),new("End",40,-74.9,Id:Guid.NewGuid()),new("Home",40,-74.8,Id:Guid.NewGuid())],Instruments:[new("terrain-scanner",true),new("thermal-imager",true)]);
    private static MissionPlan WithActions(){var p=Ordered();return p with{Collection=new(1,p.Id,InitialCollectionPolicy.Armed,[new(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.StartData,CollectionTriggerKind.Waypoint,p.Waypoints![1].Id!.Value,1,100),new(Guid.NewGuid(),Guid.NewGuid(),CollectionAction.EndData,CollectionTriggerKind.Waypoint,p.Waypoints[3].Id!.Value,1,100)],new(10,6))};}
    [Fact]public void Actual204RegionTwoInstrumentInventoryNeverUsesTwentyFourRecentJobs()
    {
        using var automatic=new AutomaticScientificData(science,settings);automatic.Pause(true);var sessions=new ScientificPlanData(automatic,settings);var plan=LongPlan();sessions.Prepare(plan,true);
        Assert.Equal(204,ScientificPlanPreparation.Cells(plan,.04).Count());Assert.InRange(automatic.Snapshot().Length,12,24);
        Assert.All(sessions.Instruments(plan.Id),i=>{Assert.Equal(204,i.Total);Assert.Equal(204,i.Ready+i.Awaiting+i.Unavailable);});Assert.Equal(408,sessions.Snapshot().Single().Awaiting);
        sessions.Control(plan.Id,"pause");Assert.All(sessions.Activity().Inventories.Single().Instruments,i=>Assert.Equal(204,i.Total));
        var reopened=new ScientificPlanData(automatic,settings);Assert.All(reopened.Instruments(plan.Id),i=>Assert.Equal(204,i.Total));Assert.Equal("Paused",reopened.Snapshot().Single().Status);
    }
    [Fact]public void FirstHalfFollowsActualCollectionLengthExcludesDepartureAndReturnTransit()
    {
        var plan=WithActions();var half=ScientificPreparationScope.Preview(plan,new("first-half"));Assert.True(half.FirstHalfAvailable);Assert.True(half.Areas.Length<half.FullRegions);
        Assert.All(half.Areas,a=>Assert.InRange(a.Center.Longitude,-75.1400001,-74.9799999));Assert.Equal(half.Areas.Length*2,half.InstrumentUnits);
        var reversed=plan with{Waypoints=plan.Waypoints!.Reverse().ToArray(),Collection=null,SurveyAreas=[new("Research",[new(39.99,-75.11),new(39.99,-74.89),new(40.01,-74.89),new(40.01,-75.11)])]};
        Assert.All(ScientificPreparationScope.Preview(reversed,new("first-half")).Areas,a=>Assert.True(a.Center.Longitude> -75.03));
        Assert.Throws<ArgumentException>(()=>ScientificPreparationScope.Preview(Ordered(),new("first-half")));
    }
    [Fact]public void ScopeExpansionRevisionAndRestartPreserveFullInventoryWithoutChangingMissionObligations()
    {
        using var automatic=new AutomaticScientificData(science,settings);automatic.Pause(true);var sessions=new ScientificPlanData(automatic,settings);var plan=WithActions();var half=ScientificPreparationScope.Preview(plan,new("first-half"));sessions.Prepare(plan,true,half.Selection);
        var total=sessions.Instruments(plan.Id).Sum(i=>i.Total);var snapshot=sessions.Snapshot().Single();Assert.True(snapshot.SelectedUnits<total);Assert.Equal("first-half",snapshot.Scope);
        var reopened=new ScientificPlanData(automatic,settings);Assert.Equal(snapshot.SelectedUnits,reopened.Snapshot().Single().SelectedUnits);
        Assert.Throws<InvalidOperationException>(()=>reopened.Prepare(plan with{Name="Revised"},true,half.Selection));
        reopened.Prepare(plan,true,new("entire",PlanRevision:half.PlanRevision));Assert.Equal(total,reopened.Snapshot().Single().SelectedUnits);Assert.Equal(total,reopened.Instruments(plan.Id).Sum(i=>i.Total));Assert.NotNull(plan.Collection);Assert.Empty(science.Products());
        var custom=ScientificPreparationScope.Preview(plan,new("areas",SegmentIndices:[1]));Assert.True(custom.Areas.Length<custom.FullRegions);
    }
    [Fact]public void ChangedSavedPlanHoldsOldScopeUntilReviewButAnOngoingFrozenFlightCanContinue()
    {
        using var automatic=new AutomaticScientificData(science,settings);automatic.Pause(true);var sessions=new ScientificPlanData(automatic,settings);var plan=WithActions();sessions.Prepare(plan,true,new("first-half"));
        sessions.PlanChanged(plan with{Name="Revised",UpdatedUtc=DateTimeOffset.UtcNow});Assert.True(sessions.Snapshot().Single().PlanStale);Assert.Throws<InvalidOperationException>(()=>sessions.Control(plan.Id,"resume"));
        sessions.Tick(true,plan.Id,frozenPlan:plan);Assert.False(sessions.Snapshot().Single().PlanStale);Assert.NotEqual("Plan revision changed — review scope",sessions.Instruments(plan.Id)[0].Status);
        sessions.Tick(false);Assert.True(sessions.Snapshot().Single().PlanStale);Assert.Throws<InvalidOperationException>(()=>sessions.Control(plan.Id,"continue"));
        sessions.Prepare(plan with{Name="Revised",UpdatedUtc=DateTimeOffset.UtcNow},true,new("entire"));Assert.False(sessions.Snapshot().Single().PlanStale);
    }
    private sealed class Blocking:IScientificDataProvider
    {
        public string Id=>"synthetic-in-flight-control";public bool Supports(RemoteKind kind)=>true;
        public readonly List<GeographicBounds> Started=[];public readonly TaskCompletionSource Began=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences preferences,ScientificDownloadBudget budget,CancellationToken token){lock(Started)Started.Add(area);Began.TrySetResult();await Task.Delay(Timeout.Infinite,token);return null;}
    }
    private sealed class NoCoverage:IScientificDataProvider
    {
        public string Id=>"synthetic-retry";public int Calls;public bool Supports(RemoteKind kind)=>true;
        public Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences preferences,ScientificDownloadBudget budget,CancellationToken token){Interlocked.Increment(ref Calls);return Task.FromResult<ScientificDiscovery?>(null);}
    }
    [Fact]public async Task ExplicitNewPlanPreparationCanRetryASelectedAreaPreviouslyFailedInAnotherScope()
    {
        settings.Save(new(true,true));var provider=new NoCoverage();using var automatic=new AutomaticScientificData(science,settings,providers:[provider]);var area=new GeographicBounds(-75,40,-74.96,40.04);automatic.Prepare(area,[RemoteKind.Terrain],scope:"plan:earlier");await Wait(()=>automatic.Snapshot().Any(s=>s.Status=="Unavailable"));
        var plan=new MissionPlan(Guid.NewGuid(),"Explicit selected retry",Waypoints:[new("Area",40.001,-74.999)],Instruments:[new("terrain-scanner",true)]);var plans=new ScientificPlanData(automatic,settings);plans.Prepare(plan,true);await Wait(()=>provider.Calls==2);Assert.Empty(science.Sources());Assert.Equal(1,plans.Instruments(plan.Id)[0].Total);
    }
    [Fact]public async Task RelevantPreparationContinuesDuringFlightWithBoundedConcurrencyAndHonorsResourceAndUserHolds()
    {
        settings.Save(new(true,true,ConcurrentDownloads:4));var provider=new Blocking();using var automatic=new AutomaticScientificData(science,settings,providers:[provider]);automatic.Pause(true);var sessions=new ScientificPlanData(automatic,settings);var plan=WithActions();sessions.Prepare(plan,true,new("first-half"));
        sessions.Tick(true,plan.Id);automatic.Pause(false);await provider.Began.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(1,automatic.QueueSummary().Downloading);Assert.NotEqual("Waiting for active flight",sessions.Snapshot().Single().Status);
        var area=automatic.Snapshot().First(s=>s.Status=="Downloading").Area;for(int i=0;i<5;i++)automatic.Prepare(area,[RemoteKind.Terrain],scope:"flight",priority:-1);Assert.Equal(1,automatic.QueueSummary().Downloading);
        sessions.Tick(true,plan.Id,flightHold:"Synthetic recorder pressure");await Wait(()=>automatic.QueueSummary().Downloading==0);Assert.Equal("Waiting for Flight Priority",sessions.Snapshot().Single().Status);
        sessions.Tick(true,plan.Id);await Wait(()=>automatic.QueueSummary().Downloading==1);sessions.Control(plan.Id,"pause");await Wait(()=>automatic.QueueSummary().Downloading==0);Assert.False(sessions.AutomaticFlightAllowed(plan.Id));
        sessions.Control(plan.Id,"resume");sessions.Tick(true,plan.Id);await Wait(()=>automatic.QueueSummary().Downloading==1);
        sessions.Tick(false);await Wait(()=>automatic.QueueSummary().Downloading==0);Assert.Equal("Awaiting future-flight approval",sessions.Snapshot().Single().Status);sessions.Tick(false);Assert.Equal(0,automatic.QueueSummary().Downloading);
        sessions.Control(plan.Id,"continue");sessions.Tick();await Wait(()=>automatic.QueueSummary().Downloading>0);
    }
    private static async Task Wait(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(4));while(!condition())await Task.Delay(20,timeout.Token);}
    [Fact]public async Task AircraftNearLaterResearchReplacesTheUnsentEarlyWindowWithoutDuplicatingTransfers()
    {
        settings.Save(new(true,true));var provider=new Blocking();using var automatic=new AutomaticScientificData(science,settings,providers:[provider]);automatic.Pause(true);var plans=new ScientificPlanData(automatic,settings);var plan=LongPlan();plans.Prepare(plan,true,new("areas",SegmentIndices:[0]));
        var frame=new TelemetryFrame(DateTimeOffset.UtcNow,TelemetrySource.Simulated,"Synthetic route-priority fixture",.001,-68,1000,500,150,150,150,90,90,0,0,0);
        plans.Tick(true,plan.Id,frame);automatic.Pause(false);await provider.Began.Task.WaitAsync(TimeSpan.FromSeconds(3));lock(provider.Started)Assert.True(provider.Started[0].West> -68.2,"The urgent late-route window must replace unsent early cells, not wait behind the first 24 jobs.");Assert.Equal(1,automatic.QueueSummary().Downloading);Assert.All(plans.Instruments(plan.Id),i=>Assert.Equal(204,i.Total));
    }
    public void Dispose(){science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
