using AeroScience.Shared;

namespace AeroScience.Missions;

// All mutations pass through this gate, including telemetry and commands from every display.
public sealed class MissionCoordinator(MissionStore store, int samplingIntervalMs)
{
    private readonly object gate = new();
    private TelemetryFrame? frame;
    private Mission? active;
    private DateTimeOffset? lastRecordedUtc;
    private string? lastAircraft;
    private ConnectionInfo connection = new(ConnectionState.Initializing, TelemetrySource.Simulated,
        "Waiting for telemetry provider", DateTimeOffset.UtcNow);

    public EngineSnapshot Snapshot()
    {
        lock (gate) return new(connection, frame, active, samplingIntervalMs);
    }

    public void Apply(TelemetryUpdate update)
    {
        lock (gate)
        {
            if (active is not null && connection.State != update.Connection.State)
                store.AddEvent(active.Id, DateTimeOffset.UtcNow, "connection", update.Connection.Message);
            connection = update.Connection;
            frame = connection.State == ConnectionState.Connected && update.Frame is { IsValid: true } ? update.Frame : null;
            if (active is null || frame is null) return;
            if (lastRecordedUtc is not null && frame.TimestampUtc - lastRecordedUtc < TimeSpan.FromMilliseconds(samplingIntervalMs)) return;
            if (lastAircraft is not null && lastAircraft != frame.Aircraft)
                store.AddEvent(active.Id, frame.TimestampUtc, "aircraft-changed", $"Aircraft changed to {frame.Aircraft}.");
            store.Append(active.Id, frame);
            lastRecordedUtc = frame.TimestampUtc;
            lastAircraft = frame.Aircraft;
            active = active with
            {
                Aircraft = active.Aircraft ?? frame.Aircraft, Source = active.Source ?? frame.Source,
                Departure = active.Departure ?? new(frame.Latitude, frame.Longitude),
                Arrival = new(frame.Latitude, frame.Longitude), SampleCount = active.SampleCount + 1,
                MinimumAltitudeFeet = Math.Min(active.MinimumAltitudeFeet ?? frame.AltitudeMslFeet, frame.AltitudeMslFeet),
                MaximumAltitudeFeet = Math.Max(active.MaximumAltitudeFeet ?? frame.AltitudeMslFeet, frame.AltitudeMslFeet)
            };
        }
    }

    public Mission Start(string? name, ScienceMode mode = ScienceMode.Universal)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose Universal or Realistic mode.");
        if (name?.Any(char.IsControl) == true) throw new ArgumentException("Mission names cannot contain control characters.");
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Use a mission name of 1–100 characters without control characters.");
        lock (gate)
        {
            if (active is not null) throw new InvalidOperationException("A mission is already recording.");
            var now = DateTimeOffset.UtcNow;
            var id = store.Create(name, frame, now, mode);
            active = store.Detail(id)!.Mission;
            lastRecordedUtc = null;
            lastAircraft = null;
            // Take a fresh sample on the next provider update; do not backdate the first sample.
            return active;
        }
    }

    public Mission End(Guid id)
    {
        lock (gate)
        {
            if (active?.Id != id) throw new InvalidOperationException("This mission is not currently recording.");
            store.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Mission completed by operator.");
            active = null;
            return store.Detail(id)!.Mission;
        }
    }

    public void Interrupt()
    {
        lock (gate)
        {
            if (active is null) return;
            store.Finish(active.Id, MissionStatus.Interrupted, lastRecordedUtc ?? active.StartedUtc,
                "Engine stopped before the mission was completed.");
            active = null;
        }
    }
}
