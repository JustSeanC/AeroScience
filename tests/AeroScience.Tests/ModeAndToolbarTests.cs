using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.SimConnect;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AeroScience.Tests;

public sealed class ModeAndToolbarTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aeroscience-modes", Guid.NewGuid().ToString());
    private MissionStore Store()
    {
        var store = new MissionStore(Path.Combine(directory, "missions.db")); store.Initialize(); return store;
    }

    [Theory]
    [InlineData(ScienceMode.Universal)][InlineData(ScienceMode.Realistic)]
    public void SelectedModeSurvivesCompletionListingAndRestart(ScienceMode mode)
    {
        var store = Store(); var coordinator = new MissionCoordinator(store, 1000);
        var mission = coordinator.Start("Any aircraft", mode);
        Assert.Equal(mode, coordinator.Snapshot().ActiveMission!.Mode);
        coordinator.End(mission.Id);
        var reopened = Store(); reopened.RecoverInterrupted();
        Assert.Equal(mode, reopened.Detail(mission.Id)!.Mission.Mode);
        Assert.Equal(mode, reopened.List().Single().Mode);
    }

    [Fact]
    public void LegacyAndIncompleteRecordsMigrateToUniversalWithoutLosingHistory()
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(directory, "missions.db")}"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE missions(id TEXT PRIMARY KEY,name TEXT,aircraft TEXT,source TEXT,started_utc TEXT,
                  ended_utc TEXT,status TEXT,departure_lat REAL,departure_lon REAL,arrival_lat REAL,arrival_lon REAL);
                CREATE TABLE telemetry(sequence INTEGER PRIMARY KEY,mission_id TEXT,timestamp_utc TEXT,latitude REAL,
                  longitude REAL,altitude_msl_ft REAL,payload_json TEXT);
                CREATE TABLE events(id INTEGER PRIMARY KEY,mission_id TEXT,timestamp_utc TEXT,kind TEXT,message TEXT);
                INSERT INTO missions(id,name,started_utc,status) VALUES($id,'Legacy incomplete',$now,'Recording');
                PRAGMA user_version=1;
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
        }
        var store = Store(); store.RecoverInterrupted();
        Assert.Equal(ScienceMode.Universal, store.Detail(id)!.Mission.Mode);
        Assert.Equal(MissionStatus.Interrupted, store.Detail(id)!.Mission.Status);
        Assert.Equal("Legacy incomplete", store.Detail(id)!.Mission.Name);
        Assert.Equal(0, store.Detail(id)!.Mission.SampleCount);
        Store().Initialize(); // idempotent migration
        Assert.Single(store.List());
    }

    [Fact]
    public void InterruptedRealisticMissionRetainsModeAndUnspecifiedModeDefaultsUniversal()
    {
        var store = Store(); var coordinator = new MissionCoordinator(store, 1000);
        var id = coordinator.Start("Realistic", ScienceMode.Realistic).Id;
        Store().RecoverInterrupted();
        Assert.Equal(ScienceMode.Realistic, store.Detail(id)!.Mission.Mode);
        var request = JsonSerializer.Deserialize<StartMissionRequest>("{\"name\":\"Old display\"}", WireJson.Options)!;
        Assert.Equal(ScienceMode.Universal, request.Mode);
        Assert.Throws<ArgumentException>(() => coordinator.Start("Invalid", (ScienceMode)99));
    }

    [Fact]
    public void ToolbarCommandsShareModesWithBrowserStateAndRetriesAreIdempotent()
    {
        var coordinator = new MissionCoordinator(Store(), 1000); var handler = new ToolbarCommandHandler(coordinator);
        var json = JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "start", "Helicopter survey", Mode: ScienceMode.Realistic), WireJson.Options);
        var first = handler.Handle(json);
        var reply = JsonSerializer.Deserialize<ToolbarReply>(first, WireJson.Options)!;
        Assert.True(reply.Ok); Assert.Equal(ScienceMode.Realistic, reply.State.ActiveMission!.Mode);
        Assert.Equal(first, handler.Handle(json));
        Assert.Equal(reply.State.ActiveMission.Id, coordinator.Snapshot().ActiveMission!.Id);
        var end = JsonSerializer.Serialize(new ToolbarCommand(1, Guid.NewGuid(), "end", MissionId: reply.State.ActiveMission.Id), WireJson.Options);
        Assert.True(JsonSerializer.Deserialize<ToolbarReply>(handler.Handle(end), WireJson.Options)!.Ok);
        Assert.Null(coordinator.Snapshot().ActiveMission);
        var malformed = JsonSerializer.Deserialize<ToolbarReply>(handler.Handle("{bad"), WireJson.Options)!;
        Assert.False(malformed.Ok);
    }

    [Fact]
    public void CommBusDecoderRejectsMalformedFragmentedAndOversizedPackets()
    {
        var json = Encoding.UTF8.GetBytes("{\"protocol\":1}\0"); var packet = new byte[32 + json.Length];
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), value);
        Put(0, (uint)packet.Length); Put(8, 43); Put(24, 1); Put(28, 100); json.CopyTo(packet, 32);
        Assert.Equal("{\"protocol\":1}", CommBusTransport.DecodeCommand(packet));
        Put(24, 2); Assert.Null(CommBusTransport.DecodeCommand(packet));
        Put(24, 1); Put(0, uint.MaxValue); Assert.Null(CommBusTransport.DecodeCommand(packet));
        Assert.Null(CommBusTransport.DecodeCommand(new byte[10])); Assert.Null(CommBusTransport.DecodeCommand(new byte[5000]));
    }

    [Fact]
    public void UnknownAircraftAndRotaryWingHoverCanProvideBasicTelemetry()
    {
        var frame = SimConnectTelemetryProvider.Decode(new SimConnectTelemetryProvider.AircraftData
        { Title = "", Latitude = 0, Longitude = 0, AltitudeMsl = 25, AltitudeAgl = double.NaN }, DateTimeOffset.UtcNow, false);
        Assert.True(frame.IsValid); Assert.Equal("Unknown MSFS aircraft", frame.Aircraft);
        Assert.Null(frame.AltitudeAglFeet); Assert.Equal(0, frame.IndicatedAirspeedKnots);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
