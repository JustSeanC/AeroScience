using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;
using AeroScience.SimConnect;
using System.Runtime.InteropServices;
using Xunit;

namespace AeroScience.Tests;

public sealed class MissionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "aeroscience-tests", Guid.NewGuid().ToString());
    private MissionStore Store()
    {
        var store = new MissionStore(Path.Combine(directory, "missions.db")); store.Initialize(); return store;
    }
    private static TelemetryUpdate Update(TelemetryFrame frame) => new(new(ConnectionState.Connected, frame.Source, "Connected", frame.TimestampUtc), frame);

    [Fact]
    public void RecordingPersistsCompleteTelemetryAndEventsAcrossReopen()
    {
        var store = Store(); var service = new MissionCoordinator(store, 1000); var time = DateTimeOffset.UtcNow;
        service.Apply(Update(SimulatedTelemetryProvider.Generate(time, 0)));
        var mission = service.Start("  High-altitude survey  ");
        for (var i = 1; i <= 3; i++) service.Apply(Update(SimulatedTelemetryProvider.Generate(time, i)));
        var ended = service.End(mission.Id);
        Assert.Equal(MissionStatus.Completed, ended.Status); Assert.Equal(3, ended.SampleCount);
        Assert.NotNull(ended.Departure); Assert.NotNull(ended.Arrival);
        var reopened = Store(); reopened.RecoverInterrupted();
        var detail = reopened.Detail(mission.Id)!;
        Assert.Equal("High-altitude survey", detail.Mission.Name);
        Assert.Equal(TelemetrySource.Simulated, detail.Mission.Source);
        Assert.Equal(["started", "completed"], detail.Events.Select(x => x.Kind));
        var samples = reopened.Samples(mission.Id, 10, 0);
        Assert.Equal(3, samples.Count); Assert.All(samples, x => Assert.True(x.IsValid));
        Assert.Equal(samples[1], reopened.Samples(mission.Id, 1, 1).Single());
    }

    [Fact]
    public void RestartRecoversRecordingAtLastSampleAndNeverOverwritesCompletedMission()
    {
        var store = Store(); var time = DateTimeOffset.UtcNow.AddMinutes(-10);
        var completed = store.Create("Completed", null, time); store.Finish(completed, MissionStatus.Completed, time.AddMinutes(1), "Done");
        var id = store.Create("Interrupted", null, time);
        store.Append(id, SimulatedTelemetryProvider.Generate(time, 5));
        Store().RecoverInterrupted();
        var recovered = store.Detail(id)!;
        Assert.Equal(MissionStatus.Interrupted, recovered.Mission.Status);
        Assert.Equal(time.AddSeconds(5), recovered.Mission.EndedUtc);
        Assert.Equal(1, recovered.Mission.SampleCount);
        Assert.Equal(MissionStatus.Completed, store.Detail(completed)!.Mission.Status);
        store.RecoverInterrupted(); Assert.Equal(2, store.Detail(id)!.Events.Count);
    }

    [Fact]
    public void DisconnectionDoesNotRecordStaleFramesAndReconnectResumesSameMission()
    {
        var store = Store(); var service = new MissionCoordinator(store, 1000); var time = DateTimeOffset.UtcNow;
        var mission = service.Start("Disconnect test");
        service.Apply(Update(SimulatedTelemetryProvider.Generate(time, 0)));
        service.Apply(new(new(ConnectionState.Disconnected, TelemetrySource.Simulated, "Lost", time), SimulatedTelemetryProvider.Generate(time, 2)));
        Assert.Null(service.Snapshot().Telemetry); Assert.Equal(1, service.Snapshot().ActiveMission!.SampleCount);
        service.Apply(Update(SimulatedTelemetryProvider.Generate(time, 10)));
        Assert.Equal(mission.Id, service.Snapshot().ActiveMission!.Id);
        Assert.Equal(2, service.End(mission.Id).SampleCount);
        Assert.Equal(3, store.Detail(mission.Id)!.Events.Count(e => e.Kind == "connection"));
    }

    [Fact]
    public void SamplingHonorsIntervalRejectsInvalidFramesAndLogsAircraftChangeOnce()
    {
        var store = Store(); var service = new MissionCoordinator(store, 1000); var time = DateTimeOffset.UtcNow;
        var id = service.Start("Sampling").Id;
        var frame = SimulatedTelemetryProvider.Generate(time, 0);
        service.Apply(Update(frame)); service.Apply(Update(frame with { TimestampUtc = time.AddMilliseconds(100) }));
        service.Apply(Update(frame with { Latitude = double.NaN, TimestampUtc = time.AddSeconds(1) }));
        service.Apply(Update(frame with { Aircraft = "WB-57", TimestampUtc = time.AddSeconds(2) }));
        service.Apply(Update(frame with { Aircraft = "WB-57", TimestampUtc = time.AddSeconds(3) }));
        Assert.Equal(3, service.End(id).SampleCount);
        Assert.Single(store.Detail(id)!.Events, e => e.Kind == "aircraft-changed");
    }

    [Fact]
    public void ConcurrentOperatorsCannotCreateTwoActiveMissions()
    {
        var service = new MissionCoordinator(Store(), 1000); var success = 0;
        Parallel.For(0, 12, i => { try { service.Start("Operator " + i); Interlocked.Increment(ref success); } catch (InvalidOperationException) { } });
        Assert.Equal(1, success);
        Assert.Throws<InvalidOperationException>(() => service.End(Guid.NewGuid()));
        service.Interrupt(); Assert.Null(service.Snapshot().ActiveMission);
    }

    [Theory]
    [InlineData("")][InlineData("  ")][InlineData("\nmission")]
    public void InvalidNamesAreRejected(string name) => Assert.Throws<ArgumentException>(() => new MissionCoordinator(Store(), 1000).Start(name));

    [Fact]
    public void SyntheticMovementIsPlausibleAndAlwaysExplicitlyLabeled()
    {
        var time = DateTimeOffset.UtcNow;
        var frames = Enumerable.Range(0, 3600).Select(s => SimulatedTelemetryProvider.Generate(time, s)).ToArray();
        Assert.All(frames, frame => { Assert.True(frame.IsValid); Assert.Equal(TelemetrySource.Simulated, frame.Source); Assert.InRange(frame.AltitudeMslFeet, 59750, 60250); });
        Assert.NotEqual(frames[0].Longitude, frames[^1].Longitude);
        Assert.InRange(frames[1].Longitude - frames[0].Longitude, 0.001, 0.003);
    }

    [Fact]
    public void NativePacketLayoutAndUnitsDecodeCorrectly()
    {
        Assert.Equal(360, Marshal.SizeOf<SimConnectTelemetryProvider.AircraftData>());
        Assert.Equal(13, SimConnectTelemetryProvider.VariableDefinitions.Length);
        var raw = new SimConnectTelemetryProvider.AircraftData { Title = "ER-2", Latitude = 34, Longitude = -117,
            AltitudeMsl = 60000, AltitudeAgl = 55000, Ias = 200, Tas = 410, Groundspeed = 400, Heading = -10,
            Track = 361, Pitch = -3, Bank = 12, VerticalSpeed = 1000 };
        var frame = SimConnectTelemetryProvider.Decode(raw, DateTimeOffset.UtcNow, true);
        Assert.True(frame.IsValid); Assert.Equal(350, frame.HeadingDegrees); Assert.Equal(1, frame.GroundTrackDegrees);
        Assert.Equal(3, frame.PitchDegrees); Assert.Equal(-12, frame.BankDegrees); Assert.True(frame.IsPaused);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
