using AeroScience.Shared;
using System.Text.Json;

namespace AeroScience.Missions;

// All mutations pass through this gate, including telemetry and commands from every display.
public sealed class MissionCoordinator(MissionStore store, int samplingIntervalMs, bool requireFreshTelemetry = false,
    Action? ensureRecordingCapacity = null, ResearchOperations? research = null)
{
    private readonly object gate = new();
    private TelemetryFrame? frame;
    private Mission? active;
    private DateTimeOffset? lastRecordedUtc;
    private string? lastAircraft;
    private DateTimeOffset? samplingAnchor;
    private long lastSlot = -1, received, skipped, missing;
    private DateTimeOffset? firstRecordedUtc;
    private bool maintenance;
    private ConnectionInfo connection = new(ConnectionState.Initializing, TelemetrySource.Simulator,
        "Waiting for telemetry provider", DateTimeOffset.UtcNow);

    public EngineSnapshot Snapshot()
    {
        lock (gate)
        {
            var count = active?.SampleCount ?? 0;
            var seconds = (lastRecordedUtc - firstRecordedUtc)?.TotalSeconds;
            return new(connection, frame, active, samplingIntervalMs, Sampling: new(received, count, skipped, missing,
                count > 1 && seconds > 0 ? (count - 1) / seconds : null, firstRecordedUtc, lastRecordedUtc,
                active is not null && active.DurationSeconds >= 1 ? count / active.DurationSeconds : null,
                active is not null && firstRecordedUtc is not null ? Math.Max(0, (firstRecordedUtc.Value - active.StartedUtc).TotalSeconds) : null),
                Research: research?.Snapshot(connection, frame));
        }
    }

    public void Apply(TelemetryUpdate update)
    {
        lock (gate)
        {
            if (active is not null && connection.State != update.Connection.State)
                store.AddEvent(active.Id, DateTimeOffset.UtcNow, "connection", update.Connection.Message);
            connection = update.Connection;
            frame = connection.State != ConnectionState.Connected ? null
                : update.Frame is { IsValid: true } ? update.Frame : update.Frame is not null ? null : frame;
            research?.Observe(update, frame);
            if (active is null || frame is null || update.Frame is null) return;
            received++;
            if (frame.IsPaused || lastRecordedUtc is not null && frame.TimestampUtc <= lastRecordedUtc)
            { skipped++; return; }
            samplingAnchor ??= frame.TimestampUtc;
            // Quantize observations to a stable schedule, not the last write's timestamp.
            // A bounded 10% (at most 100ms) phase tolerance avoids dropping every
            // other nominal 1Hz observation when delivery jitters just below 1000ms.
            var tolerance = Math.Min(100, samplingIntervalMs * .1);
            var slot = (long)Math.Floor(((frame.TimestampUtc - samplingAnchor.Value).TotalMilliseconds + tolerance) / samplingIntervalMs);
            if (slot <= lastSlot) { skipped++; return; }
            if (lastSlot >= 0 && slot > lastSlot + 1)
            {
                missing += slot - lastSlot - 1;
                store.AddEvent(active.Id, frame.TimestampUtc, "telemetry-gap", $"No committed observation in {slot - lastSlot - 1} configured recording intervals.");
            }
            if (lastAircraft is not null && lastAircraft != frame.Aircraft)
                store.AddEvent(active.Id, frame.TimestampUtc, "aircraft-changed", $"Aircraft changed to {frame.Aircraft}.");
            if (ensureRecordingCapacity is not null)
            {
                try { ensureRecordingCapacity(); }
                catch (IOException error)
                {
                    store.Finish(active.Id, MissionStatus.Interrupted, lastRecordedUtc ?? active.StartedUtc, "Recording stopped to protect storage: " + error.Message);
                    research?.End(); active = null; connection = connection with { Message = error.Message }; return;
                }
            }
            store.Append(active.Id, frame with { Guidance = research?.CurrentGuidance(connection, frame) });
            firstRecordedUtc ??= frame.TimestampUtc;
            lastSlot = slot;
            lastRecordedUtc = frame.TimestampUtc;
            lastAircraft = frame.Aircraft;
            active = active with
            {
                Aircraft = active.Aircraft ?? frame.Aircraft,
                Source = active.Source ?? frame.Source,
                Departure = active.Departure ?? new(frame.Latitude, frame.Longitude),
                Arrival = new(frame.Latitude, frame.Longitude),
                SampleCount = active.SampleCount + 1,
                MinimumAltitudeFeet = Math.Min(active.MinimumAltitudeFeet ?? frame.AltitudeMslFeet, frame.AltitudeMslFeet),
                MaximumAltitudeFeet = Math.Max(active.MaximumAltitudeFeet ?? frame.AltitudeMslFeet, frame.AltitudeMslFeet)
            };
        }
    }

    public Mission Start(string? name, ScienceMode mode = ScienceMode.Universal) => Start(new StartMissionRequest(name ?? "", mode));

    public Mission Start(StartMissionRequest request)
    {
        var name = request.Name;
        var mode = request.Mode;
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Choose Universal or Realistic mode.");
        if (name?.Any(char.IsControl) == true) throw new ArgumentException("Mission names cannot contain control characters.");
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
            throw new ArgumentException("Use a mission name of 1–100 characters without control characters.");
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Storage/settings maintenance is in progress. Try again after it completes.");
            if (active is not null) throw new InvalidOperationException("A mission is already recording.");
            var plan = request.PlanId is { } planId ? store.Plan(planId) ?? throw new ArgumentException("Saved mission plan not found.") : null;
            if (plan is not null) mode = plan.Mode;
            var configurations = ScienceCatalog.Normalize(request.Instruments ?? plan?.Instruments ?? research?.Configurations);
            try { ensureRecordingCapacity?.Invoke(); }
            catch (IOException error) { throw new InvalidOperationException(error.Message, error); }
            if (requireFreshTelemetry && (connection.State != ConnectionState.Connected || frame is null
                || DateTimeOffset.UtcNow - frame.TimestampUtc > TimeSpan.FromSeconds(6) || frame.IsPaused))
                throw new InvalidOperationException("Live aircraft telemetry is unavailable. Mission History remains accessible.");
            research?.EnsureCanStart(configurations);
            var now = DateTimeOffset.UtcNow;
            var id = store.Create(name, frame, now, mode, plan, configurations, request.Objective);
            active = store.Detail(id)!.Mission;
            research?.Begin(active, store.Research(id));
            lastRecordedUtc = null;
            firstRecordedUtc = samplingAnchor = null;
            lastSlot = -1; received = skipped = missing = 0;
            lastAircraft = null;
            // Take a fresh sample on the next provider update; do not backdate the first sample.
            return active;
        }
    }

    public void ConfigureInstruments(InstrumentConfiguration[] configurations)
    {
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Storage maintenance is in progress.");
            if (research is null) throw new InvalidOperationException("Scientific acquisition is unavailable in this host.");
            research.Configure(configurations);
        }
    }
    public ResearchState SetSciencePaused(Guid id, bool paused)
    {
        lock (gate)
        {
            if (maintenance || active?.Id != id) throw new InvalidOperationException("This mission is not currently recording.");
            if (research is null) throw new InvalidOperationException("Science Collection is unavailable in this host.");
            research.SetPaused(paused); return research.Snapshot(connection, frame);
        }
    }
    public MissionMarker AddMarker(Guid id, MarkerRequest request)
    {
        lock (gate)
        {
            if (maintenance || active?.Id != id) throw new InvalidOperationException("Observation markers require an active mission.");
            var available = connection.State == ConnectionState.Connected && frame is { IsPaused: false }
                && DateTimeOffset.UtcNow - frame.TimestampUtc <= TimeSpan.FromSeconds(6) ? frame : null;
            return store.AddMarker(id, request, DateTimeOffset.UtcNow, available,
                JsonSerializer.Serialize(research?.Snapshot(connection, frame), WireJson.Options));
        }
    }
    public MissionNote AddResearchNote(Guid id, NoteRequest request)
    {
        lock (gate)
        {
            if (maintenance) throw new InvalidOperationException("Data maintenance is in progress.");
            if (request.Phase == "in-flight" && active?.Id != id) throw new InvalidOperationException("In-flight notes require that active mission.");
            return store.AddNote(id, request);
        }
    }

    public IDisposable EnterMaintenance()
    {
        lock (gate)
        {
            if (active is not null) throw new InvalidOperationException("End the active mission before storage/settings maintenance.");
            if (maintenance) throw new InvalidOperationException("Storage/settings maintenance is already running.");
            maintenance = true;
            return new MaintenanceLease(this);
        }
    }
    private sealed class MaintenanceLease(MissionCoordinator owner) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.maintenance = false; }
    }

    public Mission End(Guid id)
    {
        lock (gate)
        {
            if (active?.Id != id) throw new InvalidOperationException("This mission is not currently recording.");
            store.Finish(id, MissionStatus.Completed, DateTimeOffset.UtcNow, "Mission completed by operator.");
            research?.End();
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
            research?.End();
            active = null;
        }
    }
}
