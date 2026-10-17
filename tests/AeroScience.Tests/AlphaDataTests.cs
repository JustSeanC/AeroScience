using System.Runtime.InteropServices;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using AeroScience.Telemetry;
using Xunit;

namespace AeroScience.Tests;

public sealed class AlphaDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "aeroscience-alpha", Guid.NewGuid().ToString("N"));
    private MissionStore Store() { var s = new MissionStore(Path.Combine(root, "missions.db")); s.Initialize(); return s; }
    private static TelemetryUpdate Update(TelemetryFrame f) => new(new(ConnectionState.Connected, f.Source, "Connected", f.TimestampUtc), f);

    [Fact]
    public void MachIsAppendedWithoutMovingOriginalNativeFields()
    {
        Assert.Equal(360, Marshal.SizeOf<SimConnectTelemetryProvider.AircraftData>());
        Assert.Equal(256, Marshal.OffsetOf<SimConnectTelemetryProvider.AircraftData>("Latitude").ToInt32());
        Assert.Equal(344, Marshal.OffsetOf<SimConnectTelemetryProvider.AircraftData>("VerticalSpeed").ToInt32());
        Assert.Equal(352, Marshal.OffsetOf<SimConnectTelemetryProvider.AircraftData>("Mach").ToInt32());
        Assert.Equal(("AIRSPEED MACH", "mach"), SimConnectTelemetryProvider.VariableDefinitions[^1]);
        var names = new[] { "Latitude", "Longitude", "AltitudeMsl", "AltitudeAgl", "Ias", "Tas", "Groundspeed", "Heading", "Track", "Pitch", "Bank", "VerticalSpeed", "Mach" };
        for (var i = 0; i < names.Length; i++) Assert.Equal(256+i*8, Marshal.OffsetOf<SimConnectTelemetryProvider.AircraftData>(names[i]).ToInt32());
        var frame = SimConnectTelemetryProvider.Decode(new() { Title = "T-38", Mach = 1.45 }, DateTimeOffset.UtcNow, false);
        Assert.Equal(1.45, frame.MachNumber); Assert.True(frame.IsValid);
        var json = JsonSerializer.Serialize(frame, WireJson.Options);
        Assert.Equal(frame, JsonSerializer.Deserialize<TelemetryFrame>(json, WireJson.Options));
    }

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(-1)]
    public void InvalidMachDoesNotDiscardValidAircraftData(double value)
    {
        var frame = SimConnectTelemetryProvider.Decode(new() { Title = "Unknown aircraft", Mach = value }, DateTimeOffset.UtcNow, false);
        Assert.True(frame.IsValid); Assert.Null(frame.MachNumber);
    }

    [Fact]
    public async Task OldSamplesWithoutMachAndAllExportedSamplesRemainReadable()
    {
        var store = Store(); var time = DateTimeOffset.UtcNow;
        var frame = SimulatedTelemetryProvider.Generate(time, 0);
        var oldJson = JsonSerializer.SerializeToNode(frame, WireJson.Options)!;
        oldJson.AsObject().Remove("machNumber");
        Assert.Null(JsonSerializer.Deserialize<TelemetryFrame>(oldJson.ToJsonString(), WireJson.Options)!.MachNumber);
        var id = store.Create("Export", frame, time, ScienceMode.Realistic);
        for (var i = 0; i < 5100; i++) store.Append(id, frame with { TimestampUtc = time.AddSeconds(i), MachNumber = i == 0 ? null : .82 });
        store.Finish(id, MissionStatus.Completed, time.AddSeconds(5100), "Done");
        using var output = new MemoryStream(); await store.ExportJsonAsync(id, output);
        using var export = JsonDocument.Parse(output.ToArray());
        Assert.Equal(5100, export.RootElement.GetProperty("telemetry").GetArrayLength());
        Assert.Equal("realistic", export.RootElement.GetProperty("mission").GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, export.RootElement.GetProperty("telemetry")[0].GetProperty("machNumber").ValueKind);
        Assert.Equal(.82, export.RootElement.GetProperty("telemetry")[1].GetProperty("machNumber").GetDouble());
        Assert.Equal(5100, store.Detail(id)!.Mission.SampleCount);
    }

    [Fact]
    public void SustainedJitterDoesNotHalveConfiguredOneHertzRecording()
    {
        var store = Store(); var coordinator = new MissionCoordinator(store, 1000);
        var start = DateTimeOffset.UtcNow; var f = SimulatedTelemetryProvider.Generate(start, 0);
        var id = coordinator.Start("Jitter regression").Id;
        var observations = Enumerable.Range(0, 120).Select(i => start.AddMilliseconds(i * 1000 + (i % 2 == 1 ? -5 : 0))).ToArray();
        DateTimeOffset? oldAccepted = null; var oldCount = 0;
        foreach (var stamp in observations)
        {
            if (oldAccepted is null || stamp - oldAccepted >= TimeSpan.FromSeconds(1)) { oldAccepted = stamp; oldCount++; }
            coordinator.Apply(Update(f with { TimestampUtc = stamp }));
        }
        Assert.InRange(oldCount, 59, 61); // Reproduces the previous last-accepted-time gate.
        Assert.Equal(120, coordinator.Snapshot().Sampling!.CommittedSamples);
        Assert.InRange(coordinator.Snapshot().Sampling!.AchievedHz!.Value, .99, 1.01);
        coordinator.Apply(Update(f with { TimestampUtc = start.AddSeconds(130) }));
        Assert.Equal(10, coordinator.Snapshot().Sampling!.MissingIntervals);
        Assert.Equal(observations, store.Samples(id, 120, 0).Select(x => x.TimestampUtc));
        coordinator.End(id);
    }

    [Fact]
    public void OfflineArchiveDoesNotPermitNewLiveRecordingOrRecreateMissingSelectedDatabase()
    {
        var store = Store(); var id = store.Create("Archive", null, DateTimeOffset.UtcNow);
        store.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Done");
        var coordinator = new MissionCoordinator(store, 1000, requireFreshTelemetry: true);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start("Offline"));
        Assert.Equal(MissionStatus.Completed, store.Detail(id)!.Mission.Status);
        var missing = Path.Combine(root, "unplugged", "archive");
        Assert.Throws<IOException>(() => new DataDirectoryLease(new EngineOptions { DataDirectory = missing, RequireExistingDatabase = true }));
        Assert.False(Directory.Exists(missing));
        Assert.Throws<InvalidOperationException>(() => new EngineOptions { Telemetry = "Simulated" }.Validate());
    }

    [Fact]
    public void ActualVersionTwoDatabaseWithoutMachUpgradesWithoutRewritingOldSamples()
    {
        var store = Store(); var now = DateTimeOffset.UtcNow;
        var frame = SimulatedTelemetryProvider.Generate(now, 0);
        var id = store.Create("v0.1.0", frame, now); store.Append(id, frame); store.Finish(id, MissionStatus.Completed, now, "Done");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + store.DatabasePath))
        {
            db.Open(); using var cmd = db.CreateCommand();
            var json = JsonSerializer.SerializeToNode(frame, WireJson.Options)!.AsObject(); json.Remove("machNumber");
            cmd.CommandText = "UPDATE telemetry SET payload_json=$json; DROP TABLE dataset_references; PRAGMA user_version=2;";
            cmd.Parameters.AddWithValue("$json", json.ToJsonString()); cmd.ExecuteNonQuery();
        }
        store.Initialize();
        Assert.Null(store.Samples(id, 1, 0).Single().MachNumber);
        Assert.Equal(ScienceMode.Universal, store.Detail(id)!.Mission.Mode);
        Assert.Empty(store.DatasetReferences(id));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(root, "Backups"), "*.db"));
        using var original = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + backup);
        original.Open(); using var version = original.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, version.ExecuteScalar());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
