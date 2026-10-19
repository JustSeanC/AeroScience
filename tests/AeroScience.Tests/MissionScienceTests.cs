using System.Runtime.InteropServices;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using AeroScience.Telemetry;
using Xunit;
namespace AeroScience.Tests;

public sealed class MissionScienceTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-040-tests",Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset utc=new(2026,10,19,12,0,0,TimeSpan.Zero);
    private TelemetryFrame Frame(int second=0)=>SimulatedTelemetryProvider.Generate(utc,second) with{AltitudeMslFeet=1000+second*10,AltitudeAglFeet=500,GroundspeedKnots=100};
    [Fact] public void CameraPlanningUsesDocumentedOpticsAndGeodesicPatternWithoutEnhancingSourceResolution()
    {
        var p=InstrumentProfiles.Catalog.Single(p=>p.Id=="micasense.rededge-p.multispectral");
        var request=new SensorPlanningRequest(new("multispectral-imager",true,1000,new(p)),120,50,.3,.6,
            new(Guid.NewGuid(),FlightPatternKind.Lawnmower,new(35,-110),LengthMetres:1000,WidthMetres:500));
        var result=InstrumentProfiles.Plan(request);
        Assert.Equal(2*120*Math.Tan(49.6*Math.PI/360),result.SwathMetres,8);
        Assert.Equal(result.SwathMetres*.7,result.TrackSpacingMetres,8);
        Assert.Equal(result.SwathMetres/1456,result.NominalCrossTrackGsdMetres!.Value,8);
        Assert.Equal(result.TrackSpacingMetres,result.Pattern!.Definition.SpacingMetres,8);
        Assert.Contains(result.Advisories,x=>x.Contains("Sentinel-2"));
        var changed=InstrumentProfiles.Plan(request with{SideOverlap=.6});Assert.True(changed.Pattern!.Waypoints.Length>result.Pattern.Waypoints.Length);
    }
    [Fact] public void PushbroomAndScannerPlanningDoNotMasqueradeAsFrameCamerasOrRealReturns()
    {
        var aviris=InstrumentProfiles.Catalog.Single(p=>p.Geometry==SensorGeometryKind.Pushbroom);
        var r=InstrumentProfiles.Plan(new(new("hyperspectral-imager",true,1000,new(aviris)),1000,100));
        Assert.Null(r.RecommendedIntervalSeconds);Assert.Equal(1,r.NominalAlongTrackGsdMetres);Assert.Null(aviris.IfovMilliradians);
        var lidar=InstrumentProfiles.Catalog.Single(p=>p.Geometry==SensorGeometryKind.ScanningLidar);
        var l=InstrumentProfiles.Plan(new(new("terrain-scanner",true,1000,new(lidar)),1000,100));
        Assert.Equal(2660000/(l.SwathMetres*100),l.TheoreticalPointDensityPerSquareMetre!.Value,8);
        Assert.Contains(l.Advisories,x=>x.Contains("no laser returns"));
    }
    [Fact] public void PayloadReferencesRejectUnidentifiedEditsAndUnboundedCustomValues()
    {
        var p=InstrumentProfiles.Catalog[1];
        Assert.Throws<ArgumentException>(()=>InstrumentProfiles.Validate(new(p with{HorizontalFovDegrees=90}),p.InstrumentId));
        Assert.Equal(90,InstrumentProfiles.Validate(new(p with{HorizontalFovDegrees=90},Custom:true),p.InstrumentId).Profile.HorizontalFovDegrees);
        Assert.Throws<ArgumentException>(()=>InstrumentProfiles.Validate(new(p with{HorizontalFovDegrees=double.NaN},Custom:true),p.InstrumentId));
        Assert.Throws<ArgumentException>(()=>InstrumentProfiles.Validate(new(p),"thermal-imager"));
    }
    [Fact] public void ToolbarFieldChangesKeepEffectivePayloadAndEquivalentConfigurationDoesNotRewriteHistory()
    {
        var preferences=new PreferenceStore(root);var paths=new StorageManager(preferences).Initialize();var store=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));store.Initialize();using var science=new ScienceStore(paths.ScienceLibraryDirectory);
        using var research=new ResearchOperations(store,science);var coordinator=new MissionCoordinator(store,1000,research:research);
        var profile=InstrumentProfiles.Catalog.Single(p=>p.Id=="micasense.rededge-p.multispectral");coordinator.ConfigureInstruments([new("multispectral-imager",false,1000,new(profile))]);
        var mission=coordinator.Start("Profile events");var before=store.Detail(mission.Id)!.Events.Count;
        var copy=JsonSerializer.Deserialize<InstrumentConfiguration[]>(JsonSerializer.Serialize(research.Configurations,WireJson.Options),WireJson.Options)!;
        coordinator.ConfigureInstruments(copy);Assert.Equal(before,store.Detail(mission.Id)!.Events.Count);
        coordinator.ConfigureInstrument("multispectral-imager",null,5000);
        Assert.Equal(profile.Id,research.Configurations.Single(c=>c.InstrumentId=="multispectral-imager").Payload!.Profile.Id);
        Assert.Equal(5000,research.Configurations.Single(c=>c.InstrumentId=="multispectral-imager").IntervalMs);
        var initial=store.Detail(mission.Id)!.Events.Single(e=>e.Kind=="payload-snapshot");var initialConfigs=JsonSerializer.Deserialize<InstrumentConfiguration[]>(initial.Message,WireJson.Options)!;
        Assert.Equal(1000,initialConfigs.Single(c=>c.InstrumentId=="multispectral-imager").IntervalMs);coordinator.End(mission.Id);
    }
    [Fact] public void TerrainPlanningIntersectsRelativeDemWithoutMixingSimulatorVerticalReferences()
    {
        var centre=new Coordinates(35,-110);var mount=new SensorMount(40,30);var terrain=TerrainPlanning.Intersect(centre,300,0,mount,_=>100,"Fixture relative surface");
        var expected=SensorGeometry.Footprint(Frame()with{Latitude=35,Longitude=-110,AltitudeAglFeet=300/.3048,HeadingDegrees=0,PitchDegrees=0,BankDegrees=0},mount);
        for(var i=0;i<4;i++)Assert.InRange(Geodesy.Distance(expected.Boundary[i],terrain.Boundary[i]),0,.001);
        var slope=TerrainPlanning.Intersect(centre,300,0,mount,p=>100+Geodesy.Distance(centre,p)*Math.Sin(Geodesy.Bearing(centre,p)*Math.PI/180)*.2,"Fixture relative surface");
        Assert.True(Geodesy.Distance(centre,slope.Boundary[1])<Geodesy.Distance(centre,terrain.Boundary[1]));
        Assert.Throws<InvalidOperationException>(()=>TerrainPlanning.Intersect(centre,300,0,mount,p=>Geodesy.Distance(centre,p)>20?null:100,"fixture"));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>TerrainPlanning.Intersect(centre,300,0,mount,_=>100,"fixture",cancel.Token));
        Assert.Contains(terrain.Limitations,s=>s.Contains("does not transform or correct simulator"));
    }
    [Fact] public void PlanSensorSettingsAndAtmosphericIntervalsRemainCanonicalAndRoundTrip()
    {
        Directory.CreateDirectory(root);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
        var plan=store.SavePlan(new(Guid.NewGuid(),"Scientific plan",MissionTemplateKind.CloudStructureSurvey,SensorPlanning:new("thermal-imager",1000,123.456,.25,.65),AtmosphericSettings:new("ascent",125,6)));
        Assert.Equal(plan.SensorPlanning,store.Plan(plan.Id)!.SensorPlanning);Assert.Equal(plan.AtmosphericSettings,store.Plan(plan.Id)!.AtmosphericSettings);
        Assert.Throws<ArgumentException>(()=>store.SavePlan(plan with{SensorPlanning=plan.SensorPlanning!with{SideOverlap=1}}));
        Assert.Throws<ArgumentException>(()=>store.SavePlan(plan with{AtmosphericSettings=plan.AtmosphericSettings!with{AltitudeIntervalMetres=0}}));
    }
    [Fact] public void HistoricalJsonStaysMissingAndNewSnapshotsRemainFrozenOnPlanEdits()
    {
        var legacy=JsonSerializer.Deserialize<InstrumentConfiguration>("{\"instrumentId\":\"thermal-imager\",\"enabled\":true,\"intervalMs\":1000}",WireJson.Options)!;
        Assert.Null(legacy.Payload);
        var f=JsonSerializer.SerializeToNode(Frame(),WireJson.Options)!;f.AsObject().Remove("environment");f.AsObject().Remove("atmosphere");
        var old=f.Deserialize<TelemetryFrame>(WireJson.Options)!;Assert.Null(old.Environment);Assert.Null(old.Atmosphere);
        Directory.CreateDirectory(root);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
        var profile=InstrumentProfiles.Catalog.Single(p=>p.Id=="flir.a655sc.lens-25");
        var plan=store.SavePlan(new(Guid.Empty,"Thermal",Instruments:[legacy with{Payload=new(profile)}]));
        var configs=plan.Instruments!;var id=store.Create("Recorded",Frame(),utc,ScienceMode.Universal,plan,configs,null);
        store.SavePlan(plan with{Name="Edited",Instruments=[legacy]});
        Assert.Equal(profile.Id,store.Research(id).Instruments.Single(c=>c.InstrumentId=="thermal-imager").Payload!.Profile.Id);
    }
    [Fact] public void AuxiliaryDecoderUsesSeparateScalarsCorrectUnitsAndNullableInvalidValues()
    {
        Assert.Equal(360,Marshal.SizeOf<SimConnectTelemetryProvider.AircraftData>());
        Assert.Equal(40,Marshal.SizeOf<ResearchTelemetry.AtmosphericData>());Assert.Equal(248,Marshal.SizeOf<ResearchTelemetry.GuidanceData>());
        Assert.Equal(9,EnvironmentalTelemetry.Variables.Length);
        Assert.Equal("millimeters of water",EnvironmentalTelemetry.Decode(4,2,utc).Unit);
        Assert.Equal(.4,EnvironmentalTelemetry.Decode(2,.4,utc).Value);Assert.Null(EnvironmentalTelemetry.Decode(2,40,utc).Value);
        Assert.Null(EnvironmentalTelemetry.Decode(1,.2,utc).Value);Assert.Null(EnvironmentalTelemetry.Decode(0,double.NaN,utc).Value);
        Assert.Equal(12,EnvironmentalTelemetry.Decode(5,12,utc).Value);
        Assert.Equal(ObservationQuality.Unavailable,EnvironmentalTelemetry.Unavailable(2,utc).Quality);
        Assert.Equal(ObservationQuality.Paused,EnvironmentalTelemetry.Decode(0,50,utc,true).Quality);
    }
    [Fact] public void EnvironmentCacheRetainsIndependentTimesAndClearsAcrossAircraftLoadingAndStaleness()
    {
        var cache=new EnvironmentTelemetryCache();var c=new ConnectionInfo(ConnectionState.Connected,TelemetrySource.Simulated,"fixture",utc);
        cache.Apply(new(c,null,Environment:EnvironmentalTelemetry.Decode(0,100,utc)));
        cache.Apply(new(c,Frame(1)));var frame=cache.Enrich(Frame(1));Assert.Equal(utc,frame.Environment!.Readings[0].TimestampUtc);
        Assert.Null(cache.Enrich(Frame(4)).Environment);
        cache.Apply(new(c,Frame(2) with{Aircraft="Another"}));Assert.Null(cache.Enrich(Frame(2)).Environment);
        cache.Apply(new(c,null,Environment:EnvironmentalTelemetry.Decode(0,100,utc.AddSeconds(3))));
        cache.Apply(new(c with{State=ConnectionState.Initializing},null));Assert.Null(cache.Enrich(Frame(3)).Environment);
    }
    [Fact] public void ReplayPagesSeekActualSamplesAndNeverFillGaps()
    {
        Directory.CreateDirectory(root);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
        var id=store.Create("Replay",Frame(),utc);foreach(var second in new[]{0,1,9,10})store.Append(id,Frame(second));store.Finish(id,MissionStatus.Interrupted,utc.AddSeconds(10),"Fixture interruption");
        var first=store.Replay(id,limit:2);Assert.True(first.HasMore);Assert.Equal(2,first.Samples.Length);
        var next=store.Replay(id,after:first.NextSequence);Assert.False(next.HasMore);Assert.Equal(utc.AddSeconds(9),next.Samples[0].Frame.TimestampUtc);
        var seek=store.Replay(id,at:utc.AddSeconds(4));Assert.Equal(next.Samples[0].Sequence,seek.Samples[0].Sequence);
        var active=store.Create("Active",Frame(),utc);Assert.Throws<InvalidOperationException>(()=>store.Replay(active));
    }
    [Fact] public void AtmosphereFindingsUseActualTransitionsLeaveGapsOpenAndBoundChartMemory()
    {
        var frames=Enumerable.Range(0,12000).Select(i=>Frame(i) with{Atmosphere=new(utc.AddSeconds(i),20-i*.001,29,5,90,.002),Environment=new([
            EnvironmentalTelemetry.Decode(0,100+i*.1,utc.AddSeconds(i)),EnvironmentalTelemetry.Decode(1,i is >1 and <5?1:0,utc.AddSeconds(i))])});
        var result=AtmosphericAnalysis.Calculate(frames,maximumPoints:100);
        Assert.Equal(12000,result.TotalSamples);Assert.True(result.Downsampled);Assert.InRange(result.Profile.Length,1,100);
        Assert.Single(result.Clouds);Assert.True(result.Clouds[0].EntryObserved);Assert.True(result.Clouds[0].ExitObserved);
        var gapped=AtmosphericAnalysis.Calculate([Frame(),Frame(10) with{Environment=new([EnvironmentalTelemetry.Decode(1,1,utc.AddSeconds(10))])},Frame(20)]);
        Assert.Equal(2,gapped.Gaps);Assert.False(gapped.Clouds[0].EntryObserved);Assert.False(gapped.Clouds[0].ExitObserved);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
