using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using AeroScience.Telemetry;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AeroScience.Tests;

public sealed class ResearchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-research-" + Guid.NewGuid());
    private readonly MissionStore store;
    private readonly ScienceStore science;
    private readonly ResearchOperations research;
    private readonly MissionCoordinator coordinator;
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private readonly ITestOutputHelper output;
    public ResearchTests(ITestOutputHelper output)
    {
        this.output = output;
        var p = new PreferenceStore(root); var settings = new StorageManager(p).Initialize();
        store = new(Path.Combine(settings.MissionDirectory, "missions.db")); store.Initialize();
        science = new(settings.ScienceLibraryDirectory); store.ScienceLibrary = science;
        research = new(store, science);
        coordinator = new(store, 1000, research: research,utcNow:()=>now);
    }
    private void Apply(int second, double? temperature = 12, bool paused = false)
    {
        var f = SimulatedTelemetryProvider.Generate(now, second) with { IsPaused = paused };
        var c = new ConnectionInfo(ConnectionState.Connected, TelemetrySource.Simulated, "Test fixture only", f.TimestampUtc);
        coordinator.Apply(new(c, f));
        coordinator.Apply(new(c, null, new(f.TimestampUtc, temperature, 28.5, 8, 270, .0023, paused ? ObservationQuality.Paused : ObservationQuality.Valid)));
    }
    [Fact]
    public void NativeAuxiliaryDefinitionsKeepCoreAndAllFieldOffsetsUnchanged()
    {
        Assert.Equal(360, Marshal.SizeOf<SimConnectTelemetryProvider.AircraftData>());
        Assert.Equal(40, Marshal.SizeOf<ResearchTelemetry.AtmosphericData>());
        Assert.Equal(248, Marshal.SizeOf<ResearchTelemetry.GuidanceData>());
        var fields = typeof(ResearchTelemetry.GuidanceData).GetFields(); Assert.Equal(31, fields.Length);
        for (var i = 0; i < fields.Length; i++) Assert.Equal(i * 8, Marshal.OffsetOf<ResearchTelemetry.GuidanceData>(fields[i].Name).ToInt32());
        Assert.Equal(fields.Length, ResearchTelemetry.GuidanceVariables.Length);
        Assert.Equal(("AMBIENT PRESSURE", "inHg"), ResearchTelemetry.AtmosphericVariables[1]);
        Assert.Equal(("AMBIENT DENSITY", "slugs per cubic feet"), ResearchTelemetry.AtmosphericVariables[^1]);
    }
    [Fact]
    public void InvalidAtmosphericChannelsRemainMissingWithoutDiscardingOtherChannels()
    {
        var frame = ResearchTelemetry.DecodeAtmosphere(new() { Temperature = double.NaN, Pressure = 0, WindSpeed = 9, WindDirection = 270, Density = .002 }, now);
        Assert.Null(frame.TemperatureCelsius); Assert.Null(frame.PressureInHg); Assert.Equal(9, frame.WindSpeedKnots);
        Assert.Equal(ObservationQuality.Valid, frame.Quality);
        Assert.Null(ScienceCatalog.Instrument("ambient-temperature").Read(frame)[0].Value);
    }
    [Fact]
    public void GuidanceUsesOnlyActualModeFlagsAndSelectedTargetSlot()
    {
        var raw = new ResearchTelemetry.GuidanceData
        {
            Available = 1,
            Master = 1,
            HeadingLock = 1,
            HeadingSlot = 2,
            Heading0 = 9,
            Heading2 = 245,
            AltitudeLock = 1,
            AltitudeSlot = 1,
            Altitude1 = 12000,
            Ias = 180,
            AirspeedHold = 1,
            GlideslopeArm = 1
        };
        var g = ResearchTelemetry.DecodeGuidance(raw, now);
        Assert.Equal("HDG", g.Lateral.Mode); Assert.Equal(245, g.Lateral.Target); Assert.Equal("ALT", g.Vertical.Mode); Assert.Equal(12000, g.Vertical.Target);
        Assert.Contains("arm indication", g.Vertical.Armed); Assert.Equal("IAS", g.Speed.Mode);
        raw.NavLock = 1; Assert.Null(ResearchTelemetry.DecodeGuidance(raw, now).Lateral.Mode);
        raw.HeadingLock = 0; raw.ApproachHold = raw.ApproachCaptured = raw.IsLocalizer = 1;
        Assert.Equal("LOC", ResearchTelemetry.DecodeGuidance(raw, now).Lateral.Mode);
        raw.MachHold = 1; Assert.Null(ResearchTelemetry.DecodeGuidance(raw, now).Speed.Mode);
        raw.Master = 0; Assert.Equal(AutopilotEngagement.Disengaged, ResearchTelemetry.DecodeGuidance(raw, now).Autopilot);
        raw.Available = 0; Assert.Equal(ObservationQuality.Unavailable, ResearchTelemetry.DecodeGuidance(raw, now).Quality);
    }
    [Fact]
    public void PlansAreEditableButRecordedSnapshotsAreImmutableAndSpontaneousStillWorks()
    {
        var plan = store.SavePlan(new(Guid.Empty, "Profile", MissionTemplateKind.AtmosphericProfile, ScienceMode.Realistic,
            Objective: "Survey", Waypoints: [new("A", 12, 34), new("B", 15, 36)], SurveyAreas: [new("Area", [new(1, 2), new(2, 3), new(3, 2)])]));
        Assert.All(plan.Instruments!.Where(c=>!RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)), c => Assert.True(c.Enabled));
        Assert.All(plan.Instruments!.Where(c=>RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)), c => Assert.False(c.Enabled));
        var m = coordinator.Start(new StartMissionRequest("Recording", PlanId: plan.Id)); Apply(0);
        store.SavePlan(plan with { Name = "Changed", Waypoints = plan.Waypoints!.Reverse().ToArray() });
        Assert.Equal("Profile", store.Research(m.Id).PlanSnapshot!.Name); Assert.Equal("A", store.Research(m.Id).PlanSnapshot!.Waypoints![0].Name);
        Assert.Equal("B", store.Plan(plan.Id)!.Waypoints![0].Name); Assert.Equal(ScienceMode.Realistic, m.Mode);
        coordinator.End(m.Id); store.DeletePlan(plan.Id); Assert.NotNull(store.Research(m.Id).PlanSnapshot);
        var spontaneous = coordinator.Start("Spontaneous"); Assert.Null(store.Research(spontaneous.Id).PlanSnapshot); coordinator.End(spontaneous.Id);
    }
    [Fact]
    public void AcquisitionHasIndependentIntervalsNoDuplicateOrInventedSamplesAndSurvivesMissingChannels()
    {
        var m = coordinator.Start(new StartMissionRequest("Science", Instruments: [new("ambient-temperature", true, 2000), new("ambient-wind", true, 1000)]));
        Apply(0); Apply(1); Apply(2, null); Apply(3, paused: true); Apply(6); Apply(6); Apply(7);
        var rows = science.StreamObservations(m.Id).ToArray();
        Assert.Equal(2, rows.Count(r => r.MeasurementId == "temperature"));
        Assert.Equal(10, rows.Count(r => r.InstrumentId == "ambient-wind"));
        Assert.All(rows, r => { Assert.Equal(ScienceProvenance.SimulatorReported, r.Provenance); Assert.Equal(now.AddSeconds((r.TimestampUtc - now).TotalSeconds), r.TimestampUtc); });
        Assert.DoesNotContain(rows, r => r.TimestampUtc == now.AddSeconds(3) || r.TimestampUtc == now.AddSeconds(4) || r.TimestampUtc == now.AddSeconds(5));
        var state = coordinator.Snapshot().Research!; Assert.True(state.Instruments.Single(i => i.InstrumentId == "ambient-temperature").RejectedObservations > 0);
        Assert.True(state.Instruments.Single(i => i.InstrumentId == "ambient-wind").MissingIntervals > 0);
        coordinator.ConfigureInstruments([new("ambient-temperature", false), new("ambient-wind", false)]); Apply(8); Assert.Equal(rows.Length, science.StreamObservations(m.Id).Count());
        coordinator.End(m.Id); Assert.NotEmpty(store.ResolvedDatasetReferences(m.Id));
    }
    [Fact]
    public void GuidanceEventsAreDeduplicatedAndDisconnectDoesNotInventTransition()
    {
        var m = coordinator.Start("Guidance"); Apply(0); var c = new ConnectionInfo(ConnectionState.Connected, TelemetrySource.Simulated, "fixture", now);
        FlightGuidance G(int second, string mode) => new(now.AddSeconds(second), AutopilotEngagement.Engaged, new(mode, 123, "deg"), new("ALT", 5000, "ft"), new("IAS", 180, "kt"));
        coordinator.Apply(new(c, null, Guidance: G(0, "HDG"))); coordinator.Apply(new(c, null, Guidance: G(1, "NAV"))); coordinator.Apply(new(c, null, Guidance: G(2, "NAV") with { Lateral = new("NAV", 250, "deg") }));
        Assert.Single(store.StreamEvents(m.Id), e => e.Kind == "flight-guidance");
        coordinator.Apply(new(c with { State = ConnectionState.Disconnected }, null)); Assert.Equal(ObservationQuality.SourceDisconnected, coordinator.Snapshot().Research!.Guidance!.Quality);
        coordinator.Apply(new(c, null, Guidance: G(3, "HDG"))); Assert.Single(store.StreamEvents(m.Id), e => e.Kind == "flight-guidance"); coordinator.End(m.Id);
    }
    [Fact]
    public void MarkersKeepActualCoordinatesAndContextWithoutChangingTrackOrder()
    {
        var m = coordinator.Start("Markers"); Apply(0); var marker = coordinator.AddMarker(m.Id, new("interesting-weather", "Observed front"));
        Assert.Equal(SimulatedTelemetryProvider.Generate(now, 0).Latitude, marker.Latitude); Assert.NotNull(marker.ScientificContextJson);
        coordinator.AddResearchNote(m.Id, new("In flight", "in-flight")); coordinator.End(m.Id); coordinator.AddResearchNote(m.Id, new("Reviewed"));
        Assert.Equal(2, store.StreamNotes(m.Id).Count()); Assert.Throws<InvalidOperationException>(() => coordinator.AddMarker(m.Id, new()));
        Assert.Equal(1, store.Track(m.Id).Points.Single().Sequence);
    }
    [Fact]
    public void SeparateClearsPreserveOtherArchiveSettingsTemplatesAndUnrelatedFiles()
    {
        var plan = store.SavePlan(new(Guid.Empty, "Keep plan")); var m = coordinator.Start(new StartMissionRequest("Archive", Instruments: [new("ambient-temperature", true)])); Apply(0);
        Assert.Throws<InvalidOperationException>(() => store.ClearFlightData("DELETE FLIGHT DATA")); Assert.Throws<InvalidOperationException>(() => science.Clear("DELETE SCIENCE DATA", true));
        coordinator.End(m.Id); var unrelated = Path.Combine(science.DirectoryPath, "my-file.txt"); File.WriteAllText(unrelated, "keep"); var libraryId = science.LibraryId;
        Assert.Throws<InvalidOperationException>(() => science.Clear("wrong", false)); Assert.Single(science.StreamObservations(m.Id));
        science.Clear("DELETE SCIENCE DATA", false); Assert.Single(store.StreamSamples(m.Id)); Assert.Empty(science.StreamObservations(m.Id));
        Assert.False(store.ResolvedDatasetReferences(m.Id).Single().Available); Assert.Equal(libraryId, science.LibraryId); Assert.Equal("keep", File.ReadAllText(unrelated));
        var m2 = coordinator.Start(new StartMissionRequest("Second", Instruments: [new("ambient-temperature", true)])); Apply(1); coordinator.End(m2.Id);
        store.ClearFlightData("DELETE FLIGHT DATA"); Assert.Empty(store.List(20, 0)); Assert.Single(science.StreamObservations(m2.Id)); Assert.NotNull(store.Plan(plan.Id));
        Assert.All(Enum.GetValues<MissionTemplateKind>(),kind=>Assert.Single(ScienceCatalog.Templates,t=>t.Id==kind));
    }
    [Fact]
    public void ScienceDeletionRollbackAndMissingDatabaseDoNotCreateFalseEmptyArchive()
    {
        var m = coordinator.Start(new StartMissionRequest("Rollback", Instruments: [new("ambient-temperature", true)])); Apply(0); coordinator.End(m.Id);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = science.DatabasePath, Pooling = false }.ToString()))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER reject_clear BEFORE DELETE ON datasets BEGIN SELECT RAISE(ABORT,'test interruption'); END;"; cmd.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => science.Clear("DELETE SCIENCE DATA", false)); Assert.Single(science.StreamObservations(m.Id));
        science.Dispose(); File.Move(science.DatabasePath, science.DatabasePath + ".saved"); Assert.NotNull(science.AvailabilityError()); Assert.Throws<IOException>(science.Initialize); Assert.False(File.Exists(science.DatabasePath)); Assert.Single(store.StreamSamples(m.Id));
    }
    [Fact]
    public void VerifiedBackupsContainAllRowsAndRestartRetainsStableDatasetIdentity()
    {
        var m = coordinator.Start(new StartMissionRequest("Backup", Instruments: [new("ambient-temperature", true)])); Apply(0); coordinator.End(m.Id);
        var flight = DataBackup.Create(store.DatabasePath, root, false); var scientific = DataBackup.Create(science.DatabasePath, root, true);
        Assert.Equal(StorageManager.DatabaseFingerprint(store.DatabasePath), StorageManager.DatabaseFingerprint(flight)); Assert.Equal(StorageManager.DatabaseFingerprint(science.DatabasePath, true), StorageManager.DatabaseFingerprint(scientific, true));
        var dataset = store.ResolvedDatasetReferences(m.Id).Single(); science.Dispose(); using var reopened = new ScienceStore(science.DirectoryPath); reopened.Initialize(); Assert.True(reopened.ContainsDataset(dataset.DatasetId)); Assert.Single(reopened.StreamObservations(m.Id));
    }
    [Fact]
    public void StorageEstimateUsesRealChannelCountIntervalDurationAndCapacityOnly()
    {
        var config = new[] { new InstrumentConfiguration("ambient-temperature", true, 1000), new InstrumentConfiguration("ambient-wind", true, 2000) };
        var estimate = science.Estimate(config, 10); Assert.True(estimate.EstimatedBytes > 0); Assert.Equal(1200, estimate.EstimatedObservationCounts.Values.Sum());
        Assert.Null(science.Estimate(config, null).EstimatedBytes); Assert.Equal(0, science.Estimate([], 10).EstimatedBytes);
        using var low = new ScienceStore(science.DirectoryPath, new Capacity(100)); Assert.True(low.Estimate(config, 10).CapacityWarning);
        Assert.Throws<ArgumentException>(() => science.Estimate([new("ambient-temperature", true, 10)], 10));
    }
    private sealed class Capacity(long bytes) : IStorageCapacity { public long FreeBytes(string directory) => bytes; }
    [Fact]
    public async Task CompleteExportsRetainEveryScienceRowAndPreciseCoordinateOrderBeyondDisplayLimits()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var m = coordinator.Start(new StartMissionRequest("Exports", Instruments: [new("ambient-temperature", true)])); Apply(0);
        var dataset = science.Datasets(m.Id).Single(); var first = science.StreamObservations(m.Id).Single();
        science.Append(Enumerable.Range(1, 5100).Select(i => first with { TimestampUtc = now.AddSeconds(i), Value = 10 + i * .001 }).ToArray());
        ArchiveFixture.Telemetry(store, m.Id, Enumerable.Range(1, 1600).Select(i => SimulatedTelemetryProvider.Generate(now, i) with { MachNumber = i % 2 == 0 ? .82 : null }));
        coordinator.End(m.Id);
        var points = store.Track(m.Id, 100); Assert.True(points.Downsampled); Assert.InRange(points.Points.Length, 2, 100); Assert.Equal(1601, points.TotalSamples);
        using var json = new MemoryStream(); await store.ExportJsonAsync(m.Id, json); using var parsed = JsonDocument.Parse(json.ToArray());
        Assert.Equal(5101, parsed.RootElement.GetProperty("science").GetProperty("observations").GetArrayLength()); Assert.Equal(1601, parsed.RootElement.GetProperty("telemetry").GetArrayLength());
        using var csv = new MemoryStream(); await ResearchExports.CsvAsync(store, m.Id, "science", csv, default); Assert.Equal(5102, Encoding.UTF8.GetString(csv.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        using var gpx = new MemoryStream(); await ResearchExports.GpxAsync(store, m.Id, gpx, default); var xml = XDocument.Parse(Encoding.UTF8.GetString(gpx.ToArray())); XNamespace gn = "http://www.topografix.com/GPX/1/1"; var point = xml.Descendants(gn + "trkpt").First(); Assert.Equal(first.Latitude!.Value, double.Parse(point.Attribute("lat")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        using var kml = new MemoryStream(); await ResearchExports.KmlAsync(store, m.Id, kml, default); XNamespace kn = "http://www.opengis.net/kml/2.2"; var coordinates = XDocument.Parse(Encoding.UTF8.GetString(kml.ToArray())).Descendants(kn + "coordinates").First().Value.Trim().Split('\n')[0].Split(','); Assert.Equal(first.Longitude!.Value, double.Parse(coordinates[0], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("Simulator", ResearchExports.Report(store, m.Id), StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"Research fixture performance: 5,101 scientific rows + 1,601 flight rows; complete JSON/CSV/GPX/KML and bounded 100-point overview: {clock.Elapsed.TotalSeconds:0.000}s. Linux fixture only, not simulator performance.");
    }
    [Fact]
    public void SchemaThreeUpgradePreservesExactLegacyPayloadAndCreatesVerifiedBackup()
    {
        var id = store.Create("Legacy realistic", null, now, ScienceMode.Realistic); var frame = SimulatedTelemetryProvider.Generate(now, 0);
        store.Append(id, frame); store.Finish(id, MissionStatus.Completed, now, "Done");
        EvidenceTestDatabase.RemoveSchemaEight(store.DatabasePath);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DROP TABLE mission_plans; DROP TABLE mission_research; DROP TABLE research_markers; DROP TABLE research_notes; ALTER TABLE dataset_references DROP COLUMN available; PRAGMA user_version=3;"; cmd.ExecuteNonQuery(); }
        var fingerprint = StorageManager.DatabaseFingerprint(store.DatabasePath); store.Initialize();
        Assert.Equal(ScienceMode.Realistic, store.Detail(id)!.Mission.Mode); Assert.Equal(frame, store.StreamSamples(id).Single()); Assert.Null(store.Research(id).PlanSnapshot);
        var backup = Directory.GetFiles(Path.Combine(root, "Backups"), "missions-schema3-*.db").Single(); Assert.Equal(fingerprint, StorageManager.DatabaseFingerprint(backup));
        store.SavePlan(new(Guid.Empty, "New after upgrade")); Assert.Single(store.Plans());
    }
    [Fact]
    public void MovingPopulatedScienceLibraryPreservesStableReferencesAndFlightHistory()
    {
        var m = coordinator.Start(new StartMissionRequest("Move", Instruments: [new("ambient-temperature", true)])); Apply(0); coordinator.End(m.Id);
        var original = science.DirectoryPath; var id = science.LibraryId; var dataset = science.Datasets(m.Id).Single().Id; science.Dispose();
        var preferences = new PreferenceStore(root); var manager = new StorageManager(preferences); var target = Path.Combine(root, "other-drive-library");
        manager.Migrate(StorageKind.ScienceLibrary, target, true, false); using var moved = new ScienceStore(target); moved.Initialize(); store.ScienceLibrary = moved;
        Assert.Equal(id, moved.LibraryId); Assert.True(moved.ContainsDataset(dataset)); Assert.True(store.ResolvedDatasetReferences(m.Id).Single().Available);
        Assert.Single(moved.StreamObservations(m.Id)); Assert.True(File.Exists(Path.Combine(original, ScienceStore.RelativeDatabasePath))); Assert.Single(store.StreamSamples(m.Id));
    }
    [Fact]
    public void SharedToolbarResearchStateStaysBoundedAndMarkersAreIdempotent()
    {
        var m = coordinator.Start(new StartMissionRequest(new string('飛', 100), Instruments: ScienceCatalog.Definitions.Select(d => new InstrumentConfiguration(d.Id, true)).ToArray())); Apply(0);
        var handler = new ToolbarCommandHandler(coordinator); var command = JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "marker", MissionId: m.Id, Notes: "Observation"), WireJson.Options);
        var reply = handler.Handle(command); Assert.Equal(reply, handler.Handle(command)); Assert.Single(store.StreamMarkers(m.Id)); Assert.True(Encoding.UTF8.GetByteCount(reply) < 8192);
        using var parsed = JsonDocument.Parse(reply); Assert.Equal(ScienceCatalog.Definitions.Length, parsed.RootElement.GetProperty("state").GetProperty("research").GetProperty("instruments").GetArrayLength());
        coordinator.End(m.Id);
    }
    [Fact]
    public void ScienceLowCapacityDoesNotStopFlightAndRejectedReadingsAreRetainedAsDiagnostics()
    {
        using var low = new ScienceStore(science.DirectoryPath, new Capacity(100)); using var research = new ResearchOperations(store, low); var c = new MissionCoordinator(store, 1000, research: research,utcNow:()=>now);
        var m = c.Start(new StartMissionRequest("Low science disk", Instruments: [new("ambient-temperature", true)])); var frame = SimulatedTelemetryProvider.Generate(now, 0);
        var connection = new ConnectionInfo(ConnectionState.Connected, TelemetrySource.Simulated, "fixture", now);
        c.Apply(new(connection, frame, new(now, 12, 29, 10, 270, .0023))); Assert.Single(store.StreamSamples(m.Id)); Assert.Empty(low.StreamObservations(m.Id));
        c.End(m.Id); Assert.True(store.InstrumentDiagnostics(m.Id).Single().RejectedObservations > 0); Assert.Contains(store.StreamEvents(m.Id), e => e.Kind == "science-error");
    }
    [Fact]
    public void ScientificOneHertzJitterAndCombinedDuplicateGuidanceDoNotDropFreshAtmosphere()
    {
        var m = coordinator.Start(new StartMissionRequest("Jitter", Instruments: [new("ambient-temperature", true)]));
        var oldGuidance = new FlightGuidance(now, AutopilotEngagement.Disengaged, new(null), new(null), new(null));
        for (var i = 0; i < 120; i++)
        {
            var time = now.AddMilliseconds(i * 1000 + (i % 2 == 1 ? -5 : 0)); var frame = SimulatedTelemetryProvider.Generate(now, 0) with { TimestampUtc = time };
            coordinator.Apply(new(new(ConnectionState.Connected, TelemetrySource.Simulated, "fixture", time), frame, new(time, 12, 29, 10, 270, .0023), oldGuidance));
        }
        Assert.Equal(120, science.StreamObservations(m.Id).Count()); Assert.InRange(coordinator.Snapshot().Research!.Instruments[0].ActualAverageIntervalSeconds!.Value, .99, 1.01); coordinator.End(m.Id);
    }
    [Fact]
    public void FlightClearFailureRollsBackResearchNotesMarkersAndTelemetryTogether()
    {
        var m = coordinator.Start("Rollback flight"); Apply(0); coordinator.AddMarker(m.Id, new()); coordinator.AddResearchNote(m.Id, new("Keep me", "in-flight")); coordinator.End(m.Id);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()))
        { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER protect_flight BEFORE DELETE ON telemetry BEGIN SELECT RAISE(ABORT,'test interruption'); END;"; cmd.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => store.ClearFlightData("DELETE FLIGHT DATA")); Assert.Single(store.StreamMarkers(m.Id)); Assert.Single(store.StreamNotes(m.Id)); Assert.Single(store.StreamSamples(m.Id)); Assert.NotNull(store.Detail(m.Id));
    }
    [Fact] public void RecentResearchPagesStayVisibleBeyondPreviewLimitWhileStreamsKeepEveryRow()
    {
        var mission=coordinator.Start("Long notes");Apply(0);
        for(var index=0;index<220;index++){store.AddMarker(mission.Id,new(Notes:index.ToString()),now.AddSeconds(index),null,null);store.AddNote(mission.Id,new(index.ToString(),"in-flight"));}
        Assert.Equal("219",store.Markers(mission.Id,200).First().Notes);Assert.Equal("219",store.Notes(mission.Id,200).First().Text);
        Assert.Equal(20,store.Markers(mission.Id,200,200).Count);Assert.Equal(220,store.StreamMarkers(mission.Id).Count());Assert.Equal(220,store.StreamNotes(mission.Id).Count());coordinator.End(mission.Id);
    }
    public void Dispose()
    {
        // Remote acquisition may still be finishing a database write after End().
        // Stop and join its workers before releasing storage or deleting test files.
        research.Dispose();
        science.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
