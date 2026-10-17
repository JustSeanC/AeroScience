using System.Text;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

public sealed class ExperienceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-experience-" + Guid.NewGuid());
    private readonly MissionStore store; private readonly ScienceStore science; private readonly ResearchOperations research; private readonly MissionCoordinator coordinator;
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    public ExperienceTests() { var settings = new StorageManager(new PreferenceStore(root)).Initialize(); store = new(Path.Combine(settings.MissionDirectory, "missions.db")); store.Initialize(); science = new(settings.ScienceLibraryDirectory); store.ScienceLibrary = science; research = new(store, science); coordinator = new(store, 1000, research: research); }
    private void Apply(int s) { var f = SimulatedTelemetryProvider.Generate(now, s); var c = new ConnectionInfo(ConnectionState.Connected, TelemetrySource.Simulated, "test fixture", f.TimestampUtc); coordinator.Apply(new(c, f, new(f.TimestampUtc, 15, 29.92, 12, 270, .0023))); }
    [Theory]
    [InlineData("temperature", 0, 32, "°F")]
    [InlineData("pressure", 29.92, 1013.2075888, "hPa / mbar")]
    [InlineData("wind-speed", 1, .5144444444, "m/s")]
    [InlineData("density", .0023, 1.1853712823, "kg/m³")]
    public void ConversionsUseCanonicalValuesWithoutChangingThem(string id, double input, double expected, string label)
    {
        var original = new MeasurementReading(id, input, "canonical"); var converted = ScienceUnits.Display(original, ScienceUnits.Scientific with { Temperature = "fahrenheit" });
        Assert.Equal(expected, converted.DisplayValue!.Value, 7); Assert.Equal(label, converted.DisplayUnit); Assert.Equal(input, converted.Value); Assert.Equal("canonical", converted.Unit);
    }
    [Fact]
    public void AllUnitOptionsRoundTripAndPreferencesPersistAtStableConfigurationLocation()
    {
        foreach (var options in ScienceUnits.Options.Values) foreach (var option in options) { var canonical = 12.345; Assert.Equal(canonical, ((canonical * option.Scale + option.Offset) - option.Offset) / option.Scale, 9); }
        var settings = new ScienceUnitSettings(root); settings.Set(ScienceUnits.Scientific); Assert.Equal(ScienceUnits.Scientific, new ScienceUnitSettings(root).Get());
        Assert.Throws<ArgumentException>(() => settings.Set(new(Temperature: "bogus"))); Assert.Equal(ScienceUnits.Scientific, settings.Get());
    }
    [Fact]
    public void PauseIsIdempotentKeepsFlightAndDatasetsAndExcludesIntentionalCadenceSlots()
    {
        var m = coordinator.Start(new StartMissionRequest("Pause", Instruments: [new("ambient-wind", true)])); Apply(0); Apply(1);
        Assert.True(research.SetPaused(true, now.AddSeconds(2))); Assert.False(research.SetPaused(true, now.AddSeconds(2))); Apply(10); Apply(11);
        Assert.Equal(4, science.StreamObservations(m.Id).Count()); Assert.Equal(4, store.StreamSamples(m.Id).Count()); Assert.True(coordinator.Snapshot().Research!.IntentionallyPaused);
        Assert.True(research.SetPaused(false, now.AddSeconds(12))); Assert.False(research.SetPaused(false, now.AddSeconds(12))); Apply(12); Apply(13);
        var state = coordinator.Snapshot().Research!.Instruments.Single(i => i.InstrumentId == "ambient-wind"); Assert.Equal(0, state.MissingIntervals); Assert.Equal(1, state.ActualAverageIntervalSeconds); Assert.Equal(8, state.ObservationCount); Assert.Equal(4, state.AcquisitionSets); Assert.Single(science.Datasets(m.Id));
        Assert.Equal(10, store.SciencePauses(m.Id).Single().Seconds);
        coordinator.End(m.Id); Assert.Single(store.StreamEvents(m.Id), e => e.Kind == "science-paused"); Assert.Single(store.StreamEvents(m.Id), e => e.Kind == "science-resumed"); Assert.Equal(ScienceCollectionStatus.Stopped, coordinator.Snapshot().Research!.CollectionStatus);
        var restarted = new MissionCoordinator(store, 1000, research: new ResearchOperations(store, science)); Assert.False(restarted.Snapshot().Research!.IntentionallyPaused); Assert.Null(restarted.Snapshot().ActiveMission);
    }
    [Fact]
    public void SimultaneousPauseCommandsAndCommBusShareOneStateAndAreBounded()
    {
        var m = coordinator.Start(new StartMissionRequest("State", Instruments: ScienceCatalog.Definitions.Select(d => new InstrumentConfiguration(d.Id, true)).ToArray())); Apply(0);
        var handler = new ToolbarCommandHandler(coordinator); var command = JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "science-pause", MissionId: m.Id), WireJson.Options);
        Parallel.For(0, 20, _ => handler.Handle(command)); Assert.Single(store.StreamEvents(m.Id), e => e.Kind == "science-paused"); Assert.True(coordinator.Snapshot().Research!.IntentionallyPaused); Assert.True(Encoding.UTF8.GetByteCount(handler.Handle(command)) < 8192);
        var resume = handler.Handle(JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "science-resume", MissionId: m.Id), WireJson.Options)); Assert.Contains("\"ok\":true", resume); Assert.False(coordinator.Snapshot().Research!.IntentionallyPaused); coordinator.End(m.Id);
    }
    [Theory]
    [InlineData(FlightPatternKind.Lawnmower, 12)]
    [InlineData(FlightPatternKind.CreepingLine, 12)]
    [InlineData(FlightPatternKind.ExpandingSquare, 7)]
    [InlineData(FlightPatternKind.Racetrack, 75)]
    [InlineData(FlightPatternKind.Orbit, 73)]
    [InlineData(FlightPatternKind.Box, 5)]
    [InlineData(FlightPatternKind.RadialSpokes, 18)]
    [InlineData(FlightPatternKind.StraightTransect, 2)]
    public void EveryPatternIsDeterministicEditableAndGeographicallyValid(FlightPatternKind kind, int count)
    {
        var definition = new PatternDefinition(Guid.NewGuid(), kind, new(72, 179.95), HeadingDegrees: 90); var p = FlightPatterns.Generate(definition); var again = FlightPatterns.Generate(definition);
        Assert.Equal(count, p.Waypoints.Length); Assert.Equal(p.Waypoints, again.Waypoints); Assert.All(p.Waypoints, w => { Assert.True(MissionPlanning.Position(w.Latitude, w.Longitude)); Assert.Equal(definition.Id, w.PatternId); }); Assert.True(p.DistanceMetres > 0);
        var saved = store.SavePlan(new(Guid.Empty, "Pattern", Waypoints: p.Waypoints, Patterns: [definition])); Assert.Equal(p.Waypoints, store.Plan(saved.Id)!.Waypoints);
        store.SavePlan(saved with { Waypoints = saved.Waypoints!.Select((w, i) => i == 0 ? w with { Name = "Edited", TargetAltitudeFeet = 7000 } : w).ToArray() }); Assert.Equal("Edited", store.Plan(saved.Id)!.Waypoints![0].Name);
    }
    [Fact]
    public void GeodesicDimensionsSpacingHeadingAndOrbitDirectionAreCorrectAcrossDateline()
    {
        var d = new PatternDefinition(Guid.NewGuid(), FlightPatternKind.Lawnmower, new(60, 179.99), 90, LengthMetres: 12000, SpacingMetres: 1500);
        var p = FlightPatterns.Generate(d); Assert.InRange(Geodesy.Distance(Geodesy.Point(p.Waypoints[0]), Geodesy.Point(p.Waypoints[1])), 11990, 12010); Assert.InRange(Geodesy.Distance(Geodesy.Point(p.Waypoints[1]), Geodesy.Point(p.Waypoints[2])), 1499, 1501);
        Assert.Contains(p.Waypoints, w => w.Longitude < 0); var orbit = FlightPatterns.Generate(d with { Kind = FlightPatternKind.Orbit }); Assert.All(orbit.Waypoints, w => Assert.Equal(d.RadiusMetres, Geodesy.Distance(d.Center, Geodesy.Point(w)), 5));
        var reverse = FlightPatterns.Generate(d with { Kind = FlightPatternKind.Orbit, Clockwise = false }); Assert.NotEqual(orbit.Waypoints[1], reverse.Waypoints[1]); Assert.InRange(Geodesy.Distance(Geodesy.Point(orbit.Waypoints[0]), Geodesy.Point(orbit.Waypoints[^1])), 0, .0001);
        Assert.Throws<ArgumentException>(() => FlightPatterns.Generate(d with { Center = new(double.NaN, 0) })); Assert.Throws<ArgumentException>(() => FlightPatterns.Generate(d with { SpacingMetres = 0 }));
    }
    [Fact]
    public void AnalysisDoesNotTurnUnorderedProximityOrGapsIntoCompletedRouteOrScienceCoverage()
    {
        var plan = new MissionPlan(Guid.NewGuid(), "Plan", Waypoints: [new("A", 0, 0, 6000), new("B", 0, .01)], SurveyAreas: [new("Region", [new(-1, -1), new(-1, 1), new(1, 1), new(1, -1)])]);
        TelemetryFrame Frame(int s, double lon) => SimulatedTelemetryProvider.Generate(now, s) with { Latitude = 0, Longitude = lon, AltitudeMslFeet = 6500 };
        var analysis = MissionAnalysis.Compare(plan, [Frame(0, .01), Frame(1, 0), Frame(20, .02)], new(100, 5)); Assert.True(analysis.Waypoints[0].Reached); Assert.False(analysis.Waypoints[1].Reached); Assert.Equal(0, analysis.Waypoints[1].ClosestDistanceMetres); Assert.Equal(500, analysis.Waypoints[0].AltitudeErrorFeet); Assert.Equal(1, analysis.ExcludedGaps); Assert.InRange(analysis.RecordedDistanceMetres, 1113, 1114); Assert.Equal(3, analysis.Surveys[0].SamplesInside); Assert.Contains("not established", analysis.Surveys[0].CoverageExplanation);
        store.SaveAnalysisSettings(store.Create("Review", null, now), new(250, 10));
    }
    [Fact]
    public void PresetsPersistIndependentlyKeepUnknownsDisabledAndNeverRewriteSnapshots()
    {
        var preset = store.SavePreset(new(Guid.Empty, "Wind", "Profile", [new("ambient-wind", true, 2000), new("future-lidar", true)]));
        var resolved = MissionStore.ResolvePreset(preset); Assert.Single(resolved.Warnings); Assert.DoesNotContain(resolved.Instruments, c => c.InstrumentId == "future-lidar");
        var m = coordinator.Start(new StartMissionRequest("Snapshot", Instruments: resolved.Instruments)); Apply(0); coordinator.End(m.Id);
        store.SavePreset(preset with { Name = "Changed", Instruments = [] }); store.DeletePreset(preset.Id); Assert.True(store.Research(m.Id).Instruments.Single(c => c.InstrumentId == "ambient-wind").Enabled); Assert.Equal(2000, store.Research(m.Id).Instruments.Single(c => c.InstrumentId == "ambient-wind").IntervalMs);
    }
    [Fact]
    public void VersionFourUpgradeBacksUpAndRetainsOriginalSamplesModesAndReferences()
    {
        var m = coordinator.Start(new StartMissionRequest("Legacy", ScienceMode.Realistic, Instruments: [new("ambient-temperature", true)])); Apply(0); coordinator.End(m.Id); var original = store.StreamSamples(m.Id).Single();
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString())) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE instrument_presets;DROP TABLE analysis_settings;PRAGMA user_version=4"; cmd.ExecuteNonQuery(); }
        var hash = StorageManager.DatabaseFingerprint(store.DatabasePath); store.Initialize(); Assert.Equal(original, store.StreamSamples(m.Id).Single()); Assert.Equal(ScienceMode.Realistic, store.Detail(m.Id)!.Mission.Mode); Assert.True(store.ResolvedDatasetReferences(m.Id).Single().Available);
        var backup = Directory.GetFiles(Path.Combine(root, "Backups"), "missions-schema4-*.db").Single(); Assert.Equal(hash, StorageManager.DatabaseFingerprint(backup)); Assert.Empty(store.Presets());
    }
    [Fact]
    public void BackupDestinationCannotBeWithinDeletedDataOrReplaceableInstall()
    {
        foreach (var path in new[] { root, Path.Combine(root, "ScienceLibrary"), Path.Combine(root, "Backups") }) Assert.Throws<IOException>(() => ArchiveClearPolicy.ValidateBackupDestination(path, root));
        ArchiveClearPolicy.ValidateBackupDestination(root + "-separate", root);
    }
    [Fact]
    public void PermanentClearWithoutNewBackupNeverOpensPickerAndCancelledBackupNeverMeansPermanentClear()
    {
        var pickerCalls = 0;
        var noBackup = ArchiveClearPolicy.ChooseBackup(false, () => { pickerCalls++; return root; }, _ => throw new IOException("must not validate an unrequested destination"));
        Assert.Equal(0, pickerCalls); Assert.False(noBackup.Cancelled); Assert.Null(noBackup.Directory);
        var cancelled = ArchiveClearPolicy.ChooseBackup(true, () => null, _ => throw new IOException("cancelled")); Assert.True(cancelled.Cancelled); Assert.Null(cancelled.Directory);
        Assert.Throws<IOException>(() => ArchiveClearPolicy.ChooseBackup(true, () => root, path => ArchiveClearPolicy.ValidateBackupDestination(path, root)));
    }
    [Fact]
    public void ClearingFlightArchiveRetainsIndependentScienceAndReusablePresets()
    {
        var preset = store.SavePreset(new(Guid.Empty, "Keep independent", null, [new("ambient-temperature", true)]));
        var mission = coordinator.Start(new StartMissionRequest("Clear fixture", Instruments: preset.Instruments)); Apply(0); coordinator.End(mission.Id);
        store.ClearFlightData("DELETE FLIGHT DATA", false); Assert.Empty(store.List()); Assert.Equal(preset.Id, store.Presets().Single().Id); Assert.Single(science.StreamObservations(mission.Id));
    }
    [Fact]
    public void SegmentCompletionRequiresObservedIntermediatePathWithoutGapsOrDetours()
    {
        var plan = new MissionPlan(Guid.NewGuid(), "Segments", Waypoints: [new("A", 0, 0), new("B", 0, .01)]);
        TelemetryFrame Frame(int s, double lat, double lon) => SimulatedTelemetryProvider.Generate(now, s) with { Latitude = lat, Longitude = lon };
        var direct = MissionAnalysis.Compare(plan, [Frame(0, 0, 0), Frame(1, 0, .005), Frame(2, 0, .01)], new(100, 5));
        Assert.True(direct.Segments!.Single().ObservedWithinTolerance); Assert.Equal(3, direct.DeviationStatistics!.Samples); Assert.InRange(direct.DeviationStatistics.MaximumMetres!.Value, 0, .01);
        var gap = MissionAnalysis.Compare(plan, [Frame(0, 0, 0), Frame(10, 0, .01)], new(100, 5)); Assert.False(gap.Segments!.Single().ObservedWithinTolerance);
        var detour = MissionAnalysis.Compare(plan, [Frame(0, 0, 0), Frame(1, .1, .005), Frame(2, 0, .01)], new(100, 5)); Assert.False(detour.Segments!.Single().ObservedWithinTolerance); Assert.True(detour.DeviationStatistics!.MaximumMetres > 10000);
    }
    public void Dispose() { science.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
