using System.Text.Json;
using System.Text;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class ScientificReview041Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-041-" + Guid.NewGuid().ToString("N"));
    private readonly MissionStore missions; private readonly ScienceStore science;
    public ScientificReview041Tests() { var paths = new StorageManager(new PreferenceStore(root)).Initialize(); missions = new(Path.Combine(paths.MissionDirectory, "missions.db")); missions.Initialize(); science = new(paths.ScienceLibraryDirectory); science.Initialize(); missions.ScienceLibrary = science; }
    private static TelemetryFrame Frame(DateTimeOffset utc, double latitude = 40, double longitude = -75) => new(utc, TelemetrySource.Simulated, "Explicit algorithm fixture — no MSFS", latitude, longitude, 1000, 328, 100, 110, 110, 0, 0, 0, 0, 0);
    private async Task<ScientificSource> Source(bool regional=false)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N")); var path = RemoteFixtureFiles.Create(folder, RemoteKind.Terrain); if(regional)RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"elevation.tif"),123.25f,size:128); var manifest = ScienceStore.ReadSourceManifest(path) with { ObservationUtc = DateTimeOffset.UtcNow.AddYears(-1) }; return await science.ImportSourceAsync(manifest, folder);
    }
    [Fact]
    public void PlanSelectionPersistsWithoutRecordingOrStartingPreparationAndMissingPlanDoesNotFallback()
    {
        var plan = missions.SavePlan(new(Guid.NewGuid(), "Selected fixture", Mode: ScienceMode.Realistic, Waypoints: [new("A", 40, -75), new("B", 40.1, -75.1)]));
        var coordinator = new MissionCoordinator(missions, 1000); coordinator.SelectPlan(plan.Id); Assert.Null(coordinator.Snapshot().ActiveMission); Assert.Equal(plan.Id, new MissionCoordinator(missions, 1000).Snapshot().SelectedPlanId);
        Assert.Equal(2, new RouteMapService(coordinator).State(null, null).Waypoints);
        Assert.Throws<ArgumentException>(() => coordinator.SelectPlan(Guid.NewGuid())); Assert.Equal(plan.Id, coordinator.Snapshot().SelectedPlanId);
        var started = coordinator.Start(new("Selected", PlanId: plan.Id)); Assert.Equal(ScienceMode.Realistic, started.Mode); Assert.Equal(plan.Id, missions.Research(started.Id).PlanSnapshot!.Id); coordinator.End(started.Id);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(20)]
    public void RepeatedTransectUsesExactAlternatingEndpointsWithNoInventedTurn(int passes)
    {
        var preview = FlightPatterns.Generate(new(Guid.NewGuid(), FlightPatternKind.StraightTransect, new(40, -75), 37, 10000, Passes: passes));
        Assert.Equal(passes + 1, preview.Waypoints.Length); for (var i = 0; i < preview.Waypoints.Length; i++) { Assert.Equal(preview.Waypoints[i % 2].Latitude, preview.Waypoints[i].Latitude); Assert.Equal(preview.Waypoints[i % 2].Longitude, preview.Waypoints[i].Longitude); }
        Assert.Equal(10000 * passes, preview.DistanceMetres, 3); Assert.Null(preview.TransitionDistanceMetres); Assert.Equal(passes, preview.Segments!.Length);
        var plan = new MissionPlan(Guid.NewGuid(), "Repeated", Waypoints: preview.Waypoints, Patterns: [preview.Definition], CruisingAltitudeFeet: 1000);
        foreach (var format in new[] { "csv", "gpx", "kml" }) Assert.Equal(passes + 1, RouteExports.Generate(new(plan, format)).Preview.Waypoints);
        if (passes > 1) { var navigation = RouteExports.Generate(new(plan)); Assert.Equal(2, navigation.Preview.Waypoints); Assert.Contains(navigation.Preview.Warnings, w => w.Contains("pilot-managed")); }
    }
    [Fact]
    public void TransectRecognitionNeedsIntermediateContinuityAndDirection()
    {
        var definition = new PatternDefinition(Guid.NewGuid(), FlightPatternKind.StraightTransect, new(40, -75), 0, 1000, Passes: 3);
        var endpoints = FlightPatterns.Generate(definition).Waypoints; var a = Geodesy.Point(endpoints[0]); var b = Geodesy.Point(endpoints[1]); var now = DateTimeOffset.UtcNow;
        var track = new List<TelemetryFrame>(); for (var pass = 0; pass < 3; pass++) for (var i = 0; i <= 20; i++) { var p = Geodesy.Direct(pass % 2 == 0 ? a : b, pass % 2 == 0 ? Geodesy.Bearing(a, b) : Geodesy.Bearing(b, a), 1000 * i / 20); track.Add(Frame(now.AddSeconds(track.Count), p.Latitude, p.Longitude)); }
        var results = TransectPasses.Recognize(definition, track, 50); Assert.All(results, p => Assert.True(p.Completed)); Assert.Equal("B → A", results[1].Direction);
        Assert.False(TransectPasses.Recognize(definition, [track[0], track[20]], 50)[0].Completed);
        var gapped = track.Select((f, i) => i < 10 ? f : f with { TimestampUtc = f.TimestampUtc.AddMinutes(1) }); Assert.False(TransectPasses.Recognize(definition, gapped, 50)[0].Completed);
    }
    [Fact]
    public async Task ExactMissedSourceRequestReconstructsOnceAfterFlightAndPreservesOriginalRecording()
    {
        using var research = new ResearchOperations(missions, science); var coordinator = new MissionCoordinator(missions, 1000, research: research); var mission = coordinator.Start(new("Recovery", Instruments: [new("terrain-scanner", true)]));
        var utc = DateTimeOffset.UtcNow; coordinator.Apply(new(new(ConnectionState.Connected, TelemetrySource.Simulated, "Fixture", utc), Frame(utc)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (missions.Recovery(mission.Id).Length == 0) await Task.Delay(10, timeout.Token); coordinator.End(mission.Id);
        var slot = Assert.Single(missions.Recovery(mission.Id)); Assert.Equal("Pending", slot.State); Assert.True(missions.VerifyRecoveryEvidence(slot)); Assert.True(missions.ScientificReview(mission.Id).Provisional);
        var original = JsonSerializer.Serialize(missions.StreamSamples(mission.Id).Single(), WireJson.Options); await Source();
        using var remote = new RemoteAcquisition(missions, science); var first = remote.Reconstruct(slot, default); var again = remote.Reconstruct(slot, default); Assert.Equal(first.Id, again.Id); Assert.Single(science.Products(mission.Id)); Assert.Contains("Post-flight derived reconstruction", first.Provenance);
        Assert.Equal(original, JsonSerializer.Serialize(missions.StreamSamples(mission.Id).Single(), WireJson.Options)); Assert.Equal(utc, first.FlightPacketUtc); Assert.NotEqual(first.FlightPacketUtc, first.SourceObservationUtc);
        missions.UpdateRecovery(slot with { State = "Recovered", ProductId = first.Id, DerivedCompleted = true }); Assert.False(missions.ScientificReview(mission.Id).Provisional);
        missions.Initialize(); Assert.Equal("Recovered", Assert.Single(missions.Recovery(mission.Id)).State);
        var changed = slot with { Evidence = slot.Evidence with { Frame = slot.Evidence.Frame with { Latitude = 41 } } }; Assert.False(missions.VerifyRecoveryEvidence(changed));
    }
    [Fact]
    public void PausedAbsentPacketAndDisabledEvidenceCannotBeReconstructedAndControlsPersist()
    {
        var now = DateTimeOffset.UtcNow; var config = InstrumentProfiles.Freeze([new("terrain-scanner", true)]); var id = missions.Create("Evidence", null, now, ScienceMode.Universal, instruments: config); var mission = missions.Detail(id)!.Mission; var request = new RemoteAcquisitionRequest(mission, missions.Research(id), config[0], Frame(now.AddSeconds(1)), 0);
        missions.SaveRecovery(request, "Missing source", true); var slot = Assert.Single(missions.Recovery(id)); Assert.False(missions.VerifyRecoveryEvidence(slot));
        missions.Append(id, request.Frame); Assert.True(missions.VerifyRecoveryEvidence(slot)); missions.AddEvent(id, now, "science-paused", "User pause"); Assert.False(missions.VerifyRecoveryEvidence(slot));
        missions.ControlRecovery(id, "pause"); Assert.Equal("Paused", Assert.Single(missions.Recovery(id)).State); missions.ControlRecovery(id, "cancel"); Assert.Equal("Cancelled", Assert.Single(missions.Recovery(id)).State); missions.ControlRecovery(id, "resume"); Assert.Equal("Pending", Assert.Single(missions.Recovery(id)).State);
        Assert.Throws<ArgumentException>(() => missions.ControlRecovery(id, "erase"));
    }
    [Fact]
    public async Task SpatialQueryPaginatesAcrossMissionsAndFiltersWithoutModifyingProducts()
    {
        await Source(); using var research = new ResearchOperations(missions, science); var coordinator = new MissionCoordinator(missions, 1000, research: research); var ids = new List<Guid>();
        for (var n = 0; n < 2; n++) { var mission = coordinator.Start(new("Map " + n, Instruments: [new("terrain-scanner", true)])); var utc = DateTimeOffset.UtcNow; coordinator.Apply(new(new(ConnectionState.Connected, TelemetrySource.Simulated, "Fixture", utc), Frame(utc))); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (science.Products(mission.Id).Length == 0) await Task.Delay(10, timeout.Token); coordinator.End(mission.Id); ids.Add(mission.Id); }
        var first = science.MapPage(new(Limit: 1)); Assert.Single(first.Products); Assert.True(first.HasMore); var second = science.MapPage(new(After: first.Next, Limit: 1)); Assert.Single(second.Products); Assert.NotEqual(first.Products[0].Id, second.Products[0].Id);
        Assert.Single(science.MapPage(new(Missions: [ids[0]])).Products); Assert.Empty(science.MapPage(new(Area: new(10, 10, 11, 11))).Products); Assert.Empty(science.MapPage(new(Instrument: "thermal-imager")).Products);
        var data = science.ReadProduct(first.Products[0].Id); var layer = JsonSerializer.SerializeToElement(ScientificMapLayer.Create(data, "elevation"), WireJson.Options); Assert.True(layer.GetProperty("features").GetArrayLength() > 0);
        var comparison = science.Compare(new([first.Products[0].Id, second.Products[0].Id], "elevation")); Assert.True(comparison.Comparable); Assert.Equal(0, comparison.MeanDelta); Assert.Contains(comparison.Reasons, r => r.Contains("not independently"));
        var summary = ScientificResearchSummary.Build(missions, science, ids[0]); Assert.Single(summary.Instruments); Assert.Equal(1, summary.Instruments[0].RemoteProducts);
        var legacy=first.Products[0] with{Statistics=null};var originalSha=legacy.Sha256;
        var stats=science.ProductStatistics(legacy);Assert.Contains(stats,s=>s.Minimum==123.25&&s.Maximum==123.25);
        Assert.Equal(JsonSerializer.Serialize(stats),JsonSerializer.Serialize(science.ProductStatistics(legacy)));Assert.Equal(originalSha,science.ReadProduct(legacy.Id).Product.Sha256);
    }
    [Fact]
    public void PreparationRestartsWithoutFalseReadyAndFlightPreemptionDoesNotBecomeUserPause()
    {
        var settings = new ScientificAccessSettings(root); using var automatic = new AutomaticScientificData(science, settings); var sessions = new ScientificPlanData(automatic, settings);
        var plan = missions.SavePlan(new(Guid.NewGuid(), "Preparation", Waypoints: [new("A", 40, -75), new("B", 40.01, -75)], Instruments: [new("terrain-scanner", true)]));
        sessions.Prepare(plan, true); sessions.Control(plan.Id, "pause"); Assert.Equal("Paused", sessions.Snapshot().Single().Status);
        sessions = new(automatic, settings); Assert.Equal("Paused", sessions.Snapshot().Single().Status); sessions.Control(plan.Id, "resume"); sessions.Tick(true); Assert.Equal("Waiting for active flight", sessions.Snapshot().Single().Status); sessions.Tick(false); Assert.NotEqual("Paused", sessions.Snapshot().Single().Status);
        var instruments = sessions.Instruments(plan.Id); Assert.Single(instruments); Assert.Equal(instruments[0].Total, instruments[0].Ready + instruments[0].Awaiting + instruments[0].Unavailable);
    }
    [Fact]
    public void ExplicitConcurrencyPreferenceSurvivesAndInvalidLimitsAreRejected()
    {
        var settings = new ScientificAccessSettings(root); Assert.Equal(1, settings.Get().Preferences.ConcurrentDownloads); settings.Save(new(ConcurrentDownloads: 1)); Assert.Equal(1, new ScientificAccessSettings(root).Get().Preferences.ConcurrentDownloads); settings.Save(new(ConcurrentDownloads: 8)); Assert.Equal(8, settings.Get().Preferences.ConcurrentDownloads); Assert.Throws<ArgumentException>(() => settings.Save(new(ConcurrentDownloads: 9)));
    }
    [Fact]
    public async Task SourcePolicyChangesReverifyReadyRegionsWithoutRemovingRetainedOriginals()
    {
        var source=await Source(regional:true);var settings=new ScientificAccessSettings(root);
        using var automatic=new AutomaticScientificData(science,settings);var sessions=new ScientificPlanData(automatic,settings);
        var plan=missions.SavePlan(new(Guid.NewGuid(),"Policy recheck",Waypoints:[new("A",39.99,-74.99)],Instruments:[new("terrain-scanner",true)]));
        sessions.Prepare(plan,true);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(sessions.Instruments(plan.Id).Single().Status!="Ready"){sessions.Tick();await Task.Delay(20,deadline.Token);}
        automatic.Configure(settings.Get().Preferences with{ExcludedSourceIds=[source.Id]});sessions.Tick();Assert.NotEqual("Ready",sessions.Instruments(plan.Id).Single().Status);
        while(sessions.Regions(plan.Id).Single().Status!="Offline"){sessions.Tick();await Task.Delay(20,deadline.Token);}
        Assert.Equal("Waiting for Network",sessions.Instruments(plan.Id).Single().Status);Assert.Equal(0,sessions.Instruments(plan.Id).Single().Ready);Assert.Contains(science.Sources(),s=>s.Id==source.Id);
    }
    [Fact]
    public void PilotManagedWideTurnDoesNotStartOrInvalidateTheNextStraightSciencePass()
    {
        var definition=new PatternDefinition(Guid.NewGuid(),FlightPatternKind.StraightTransect,new(40,-75),0,1000,Passes:2,PassAltitudesFeet:[1000,2000],TargetGroundspeedKnots:110);
        var points=FlightPatterns.Generate(definition).Waypoints;var a=Geodesy.Point(points[0]);var b=Geodesy.Point(points[1]);var time=DateTimeOffset.UtcNow;
        var track=new List<TelemetryFrame>();
        void Add(Coordinates point)=>track.Add(Frame(time.AddSeconds(track.Count),point.Latitude,point.Longitude));
        for(var i=0;i<=20;i++)Add(Geodesy.Direct(a,Geodesy.Bearing(a,b),i*50));
        for(var i=0;i<16;i++)Add(Geodesy.Direct(b,i*22.5,200));
        Add(b);for(var i=0;i<=20;i++)Add(Geodesy.Direct(b,Geodesy.Bearing(b,a),i*50));
        var passes=TransectPasses.Recognize(definition,track,50);Assert.All(passes,p=>Assert.True(p.Completed));Assert.Equal(2000,passes[1].TargetAltitudeFeet);Assert.Equal(110,passes[1].TargetGroundspeedKnots);Assert.Equal(110,passes[1].MeanGroundspeedKnots);
        Assert.Contains("B to A",RouteExports.Generate(new(new(Guid.NewGuid(),"Passes",Waypoints:points,Patterns:[definition]),"csv")).Content);
    }
    [Fact]
    public async Task ActualDownlinkWorkerResumesPersistentEvidenceAndRefreshesDerivedProducts()
    {
        using var research=new ResearchOperations(missions,science);
        var coordinator=new MissionCoordinator(missions,1000,research:research);
        var mission=coordinator.Start(new("Background downlink fixture",Instruments:[new("terrain-scanner",true)]));
        var now=DateTimeOffset.UtcNow;coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Fixture",now),Frame(now)));
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(missions.Recovery(mission.Id).Length==0)await Task.Delay(10,deadline.Token);
        coordinator.End(mission.Id);missions.ControlRecovery(mission.Id,"pause");await Source();
        var settings=new ScientificAccessSettings(root);using var automatic=new AutomaticScientificData(science,settings);
        using var worker=new ScientificDownlinkWorker(missions,science,automatic,settings,coordinator,new(),Microsoft.Extensions.Logging.Abstractions.NullLogger<ScientificDownlinkWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            await Task.Delay(2100,deadline.Token);Assert.Empty(science.Products(mission.Id));
            missions.ControlRecovery(mission.Id,"resume");
            while(missions.ScientificReview(mission.Id).Provisional)await Task.Delay(50,deadline.Token);
            var slot=Assert.Single(missions.Recovery(mission.Id));Assert.Equal("Recovered",slot.State);Assert.True(slot.DerivedCompleted);
            Assert.Contains(science.Artifacts(mission.Id),a=>a.Kind=="mission-samples");Assert.Contains(science.Artifacts(mission.Id),a=>a.Kind=="coverage");Assert.Single(science.Products(mission.Id));
        }
        finally{await worker.StopAsync(default);}
    }
    [Fact]
    public void DescriptiveInterpretationRequiresDistinctPacketsAltitudeSpanAndStrongRecordedAssociation()
    {
        var time=DateTimeOffset.UtcNow;
        var frames=Enumerable.Range(0,20).Select(i=>Frame(time.AddSeconds(i)) with{AltitudeMslFeet=1000+i*100,Atmosphere=new(time.AddSeconds(i),20-i*.5,29-i*.1,null,null,null)}).ToArray();
        var result=ScientificInterpretation.Describe(frames);Assert.All(result,r=>Assert.Equal(20,r.IndependentPackets));Assert.All(result,r=>Assert.Contains("generally decreased",r.Explanation));
        var reused=frames.Select(f=>f with{Atmosphere=frames[0].Atmosphere});Assert.All(ScientificInterpretation.Describe(reused),r=>Assert.DoesNotContain("generally decreased",r.Explanation));
        Assert.All(ScientificInterpretation.Describe([]),r=>Assert.Equal(0,r.IndependentPackets));
    }
    public void Dispose() { science.Dispose(); Directory.Delete(root, true); }
}
