using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class PredictiveScienceTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-predict-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;
    public PredictiveScienceTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();}
    private static TelemetryFrame Frame(DateTimeOffset time,Coordinates p,double knots=450,double heading=0,double track=90)=>new(time,TelemetrySource.Simulated,"Synthetic predictive test",p.Latitude,p.Longitude,10000,1000,knots,knots,knots,heading,track,0,0,0);
    private static (ScientificPredictionResult Result,ScientificMotionPredictor Predictor,TelemetryFrame Frame) Predict(double knots,MissionPlan? plan=null)
    {
        var predictor=new ScientificMotionPredictor();var time=DateTimeOffset.UtcNow;var p=new Coordinates(40,-75);var f=Frame(time,p,knots);
        predictor.Predict(f,time,[RemoteKind.Terrain],new(),45,plan);var q=Geodesy.Direct(p,90,knots*.514444444444*2);f=Frame(time.AddSeconds(2),q,knots);
        return(predictor.Predict(f,f.TimestampUtc,[RemoteKind.Terrain],new(),45,plan),predictor,f);
    }
    [Fact]public void FastUnplannedAircraftPrepareAheadUsingGroundMovementAndNotWindDriftHeading()
    {
        var fast=Predict(450).Result;var slow=Predict(90).Result;
        Assert.Equal("Predicting",fast.Status);Assert.InRange(fast.TrackTrueDegrees,89,91);Assert.False(fast.RouteConsistent);
        Assert.InRange(fast.DistanceMetres/slow.DistanceMetres,4.99,5.01);Assert.InRange(fast.LookaheadSeconds,90,600);
        Assert.Contains(fast.Areas,a=>a.ArrivalSeconds>30&&a.Area.Center.Longitude> -74.95);Assert.InRange(fast.Areas.Length,2,128);
    }
    [Fact]public void RouteFollowingIsOptionalAndNinetyDegreeDepartureReprioritizesActualTrajectory()
    {
        var plan=new MissionPlan(Guid.NewGuid(),"East route",Waypoints:[new("A",40,-75.1),new("B",40,-74)]);
        var (prediction,predictor,frame)=Predict(450,plan);Assert.True(prediction.RouteConsistent);
        var north=Geodesy.Direct(new(frame.Latitude,frame.Longitude),0,450*.514444444444*2);frame=Frame(frame.TimestampUtc.AddSeconds(2),north,450,90,0);
        var turned=predictor.Predict(frame,frame.TimestampUtc,[RemoteKind.Terrain],new(),45,plan);
        Assert.False(turned.RouteConsistent);Assert.InRange(turned.TrackTrueDegrees,0,1);Assert.Contains(turned.Areas,a=>a.Area.North>40.02);
    }
    [Fact]public void SustainedTurnsBoundTheCorridorAndReversalsReuseGeographicCells()
    {
        var (_,predictor,f)=Predict(150);var point=new Coordinates(f.Latitude,f.Longitude);ScientificPredictionResult? result=null;
        for(var i=1;i<=14;i++){point=Geodesy.Direct(point,90+i*10,150*.514444444444*2);f=Frame(f.TimestampUtc.AddSeconds(2),point,150,0,90+i*10);result=predictor.Predict(f,f.TimestampUtc,[RemoteKind.Terrain],new(),120);}
        Assert.NotNull(result);Assert.InRange(result.TurnDegreesPerSecond,4,6);Assert.InRange(result.Areas.Length,1,128);
        Assert.True(result.Areas.Max(a=>Geodesy.Distance(point,a.Area.Center))<result.DistanceMetres);Assert.Contains(result.Areas,a=>a.Basis=="Turn uncertainty");
    }
    [Fact]public void StalePausedGapsTeleportationAndStationaryTelemetryDoNotProjectFalseFastFlights()
    {
        var (_,predictor,f)=Predict(450);var paused=predictor.Predict(f with{IsPaused=true},f.TimestampUtc,[RemoteKind.Terrain],new(),45);Assert.Equal("Suspended",paused.Status);Assert.Empty(paused.Areas);
        Assert.Equal("Stabilizing",predictor.Predict(f,f.TimestampUtc,[RemoteKind.Terrain],new(),45).Status);
        var stationary=f with{TimestampUtc=f.TimestampUtc.AddSeconds(2)};var still=predictor.Predict(stationary,stationary.TimestampUtc,[RemoteKind.Terrain],new(),45);Assert.Equal(0,still.DistanceMetres);
        var jump=stationary with{TimestampUtc=stationary.TimestampUtc.AddSeconds(2),Latitude=50};Assert.Equal("Stabilizing",predictor.Predict(jump,jump.TimestampUtc,[RemoteKind.Terrain],new(),45).Status);
        Assert.Equal("Suspended",predictor.Predict(jump,jump.TimestampUtc.AddSeconds(10),[RemoteKind.Terrain],new(),45).Status);
    }
    [Fact]public void PositionSlewingWithStationaryGroundspeedDoesNotScheduleAFalseFastCorridor()
    {
        var time=DateTimeOffset.UtcNow;var p=new Coordinates(40,-75);var predictor=new ScientificMotionPredictor();var first=Frame(time,p,0);predictor.Predict(first,time,[RemoteKind.Terrain],new(),45);
        var moved=Geodesy.Direct(p,90,500);var next=Frame(time.AddSeconds(2),moved,0);var result=predictor.Predict(next,next.TimestampUtc,[RemoteKind.Terrain],new(),45);Assert.Equal("Stabilizing",result.Status);Assert.Empty(result.Areas);Assert.Contains("possible slew",result.Message);
    }
    [Fact]public void SensorFootprintAndLatencyExtendReadinessWithinExplicitBounds()
    {
        var time=DateTimeOffset.UtcNow;var predictor=new ScientificMotionPredictor();var p=new Coordinates(40,-75);var a=Frame(time,p,450) with{AltitudeAglFeet=60000};predictor.Predict(a,time,[RemoteKind.Thermal,RemoteKind.Multispectral,RemoteKind.Terrain],new(),120);
        var q=Geodesy.Direct(p,90,450*.514444444444*2);var f=a with{TimestampUtc=time.AddSeconds(2),Latitude=q.Latitude,Longitude=q.Longitude};var result=predictor.Predict(f,f.TimestampUtc,[RemoteKind.Thermal,RemoteKind.Multispectral,RemoteKind.Terrain],new(),120);
        Assert.InRange(result.LookaheadSeconds,518,519);Assert.InRange(result.DistanceMetres,119999,120001);Assert.InRange(result.Areas.Length,2,128);Assert.Contains(result.Areas,a=>a.Basis.Contains("footprint"));Assert.Contains(result.Areas,a=>a.Area.South<39.98);Assert.True(result.Limited);
    }
    [Fact]public void LongPreflightEnumerationProgressesBeyondEightWithoutAContinentBoundingBox()
    {
        var plan=new MissionPlan(Guid.NewGuid(),"Long route",Waypoints:[new("A",40,-75),new("B",42,-72)],Instruments:[new("terrain-scanner",true)]);
        var cells=ScientificPlanPreparation.Cells(plan).Take(50).ToArray();Assert.Equal(50,cells.Length);Assert.Equal(50,cells.Distinct().Count());Assert.All(cells,c=>Assert.InRange(c.East-c.West,.019,.021));
        var settings=new ScientificAccessSettings(root);using var automatic=new AutomaticScientificData(science,settings);var sessions=new ScientificPlanData(automatic,settings);sessions.Prepare(plan,true);Assert.True(sessions.Snapshot().Single().AreasQueued>8);sessions.Control(plan.Id,"pause");Assert.Equal("Paused",sessions.Snapshot().Single().Status);sessions.Control(plan.Id,"resume");Assert.Equal("Preparing",sessions.Snapshot().Single().Status);
    }
    private sealed class Blocking:IScientificDataProvider
    {
        public readonly List<double> Order=[];public readonly TaskCompletionSource First=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id=>"isolated-priority-fixture";public bool Supports(RemoteKind k)=>true;
        public async Task<ScientificDiscovery?> Discover(RemoteKind kind,GeographicBounds area,ScientificAccessPreferences p,ScientificDownloadBudget b,CancellationToken token)
        {int count;lock(Order){Order.Add(area.West);count=Order.Count;}if(count==1){First.SetResult();await Task.Delay(Timeout.Infinite,token);}return null;}
    }
    [Fact]public async Task UrgentFlightDataBypassesDistantPreflightAndRepeatedUpdatesDeduplicate()
    {
        var settings=new ScientificAccessSettings(root);settings.Save(new(true,true));var provider=new Blocking();using var automatic=new AutomaticScientificData(science,settings,providers:[provider]);
        var a=new GeographicBounds(-75,40,-74.98,40.02);automatic.Prepare(a,[RemoteKind.Terrain],priority:10000,scope:"plan:test");await provider.First.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var distant=a with{West=-74,East=-73.98};automatic.Prepare(distant,[RemoteKind.Terrain],priority:10001,scope:"plan:test");var urgent=a with{West=-76,East=-75.98};
        automatic.ReprioritizeFlight(new Dictionary<string,double>{{AutomaticScientificData.AreaKey(RemoteKind.Terrain,urgent),-90}});
        for(var i=0;i<10;i++)automatic.Prepare(urgent,[RemoteKind.Terrain],priority:-90,scope:"flight");Assert.InRange(automatic.QueueSummary().Queued,0,2);
        await Wait(()=>{lock(provider.Order)return provider.Order.Count==3;});lock(provider.Order)Assert.Equal(new[]{-75d,-76,-74},provider.Order);
        Assert.Empty(science.Sources());Assert.Empty(science.Products());
    }
    [Fact]public async Task MissingHyperspectralDoesNotUseNetworkOrBlockOtherInstrumentAndSourceModesPersist()
    {
        var settings=new ScientificAccessSettings(root);var modes=new Dictionary<RemoteKind,string>{{RemoteKind.Terrain,"local"},{RemoteKind.Thermal,"prefer-local"},{RemoteKind.Hyperspectral,"local"}};settings.Save(new(SourceModes:modes));
        Assert.Equal("prefer-local",new ScientificAccessSettings(root).Get().Preferences.SourceModes![RemoteKind.Thermal]);
        using var automatic=new AutomaticScientificData(science,settings);automatic.Prepare(new(-75,40,-74.98,40.02),[RemoteKind.Hyperspectral,RemoteKind.Terrain]);await Wait(()=>automatic.Snapshot().All(s=>s.Status!="Queued"));
        Assert.Contains(automatic.Snapshot(),s=>s.Kind==RemoteKind.Hyperspectral&&s.Status=="Dataset Required");Assert.Contains(automatic.Snapshot(),s=>s.Kind==RemoteKind.Terrain&&s.Message.Contains("My Datasets Only"));Assert.Equal(0,automatic.QueueSummary().HourlyBytes);
    }
    private static async Task Wait(Func<bool> condition){using var c=new CancellationTokenSource(TimeSpan.FromSeconds(6));while(!condition())await Task.Delay(20,c.Token);}
    public void Dispose(){science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
