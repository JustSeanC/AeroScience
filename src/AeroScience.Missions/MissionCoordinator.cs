using AeroScience.Shared;
using System.Text.Json;

namespace AeroScience.Missions;

// All mutations pass through this gate, including telemetry and commands from every display.
public sealed class MissionCoordinator(MissionStore store, int samplingIntervalMs, bool requireFreshTelemetry = false,
    Action? ensureRecordingCapacity = null, ResearchOperations? research = null)
{
    internal MissionStore Store => store;
    private readonly object gate = new();
    private string SelectionPath => Path.Combine(Path.GetDirectoryName(store.DatabasePath)!, "selected-plan.json");
    private Guid? SelectedPlan()
    {
        if (!File.Exists(SelectionPath)) return null;
        return JsonSerializer.Deserialize<Guid?>(File.ReadAllText(SelectionPath), WireJson.Options);
    }
    public Guid? SelectPlan(Guid? id)
    {
        lock (gate)
        {
            if (active is not null || maintenance) throw new InvalidOperationException("Finish recording or maintenance before selecting a plan.");
            if (id is {} plan && store.Plan(plan) is null) throw new ArgumentException("Saved plan no longer exists. Refresh missions; no unplanned flight was selected.");
            PreferenceStore.WriteAtomic(SelectionPath, id); return id;
        }
    }
    private readonly EnvironmentTelemetryCache environment = new();
    private TelemetryFrame? frame;
    private Mission? active;
    private DateTimeOffset? lastRecordedUtc;
    private string? lastAircraft;
    private DateTimeOffset? samplingAnchor;
    private long lastSlot = -1, received, skipped, missing;
    private DateTimeOffset? firstRecordedUtc;
    private bool maintenance;
    private RecordingStopInfo? recordingStop = store.LastRecordingStop();
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
                Research: research?.Snapshot(connection, frame), RecordingStop: recordingStop, SelectedPlanId: SelectedPlan());
        }
    }

    public void Apply(TelemetryUpdate update)
    {
        lock (gate)
        {
            if (active is not null && connection.State != update.Connection.State)
                store.AddEvent(active.Id, DateTimeOffset.UtcNow, "connection", update.Connection.Message);
            connection = update.Connection;
            environment.Apply(update);
            if(update.Frame is {IsValid:true} incoming)update=update with{Frame=environment.Enrich(incoming)};
            frame = connection.State != ConnectionState.Connected ? null
                : update.Frame is { IsValid: true } ? update.Frame : update.Frame is not null ? null : frame;
            try { research?.Observe(update, frame); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { ScientificDiagnostics.Record("science-processing-error",active?.Id.ToString()??"none","flight",RemoteKind.Terrain,result:error.GetType().Name+": "+error.Message); }
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
            {
                try { ensureRecordingCapacity?.Invoke(); store.Append(active.Id, frame with { Guidance = research?.CurrentGuidance(connection, frame) }); }
                catch (Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    recordingStop = new(error is StorageLimitException ? "storageLimitReached" : "storageUnavailable", error.Message, active.Id, DateTimeOffset.UtcNow);
                    ScientificDiagnostics.Record("recording-storage-error",active.Id.ToString(),"flight",RemoteKind.Terrain,result:error.GetType().Name+": "+error.Message);
                    try { store.AddEvent(active.Id, recordingStop.TimestampUtc, recordingStop.Code, error.Message);store.Finish(active.Id, MissionStatus.Interrupted,lastRecordedUtc??active.StartedUtc,"Recording stopped to protect storage: "+error.Message); }
                    catch(Exception finalization)when(finalization is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){ScientificDiagnostics.Record("recording-finalization-error",active.Id.ToString(),"flight",RemoteKind.Terrain,result:finalization.GetType().Name+": "+finalization.Message);}
                    try{research?.End();}catch(Exception endError)when(endError is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){ScientificDiagnostics.Record("science-stop-error",active.Id.ToString(),"flight",RemoteKind.Terrain,result:endError.Message);}
                    active = null; connection = connection with { Message = error.Message }; return;
                }
            }
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
            var configurations = InstrumentProfiles.Freeze(ScienceCatalog.Normalize(request.Instruments ?? plan?.Instruments ?? research?.Configurations));
            try { ensureRecordingCapacity?.Invoke(); }
            catch (Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { throw new InvalidOperationException(error.Message, error); }
            if (requireFreshTelemetry && (connection.State != ConnectionState.Connected || frame is null
                || DateTimeOffset.UtcNow - frame.TimestampUtc > TimeSpan.FromSeconds(6) || frame.IsPaused))
                throw new InvalidOperationException("Live aircraft telemetry is unavailable. Mission History remains accessible.");
            research?.EnsureCanStart(configurations);
            var now = DateTimeOffset.UtcNow;
            var id = store.Create(name, frame, now, mode, plan, configurations, request.Objective);
            active = store.Detail(id)!.Mission;
            recordingStop = null;
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
    public void ConfigureInstrument(string id,bool? enabled,int? intervalMs)
    {
        lock(gate)
        {
            if(research is null)throw new InvalidOperationException("Scientific acquisition is unavailable in this host.");
            var current=research.Configurations;if(!current.Any(c=>c.InstrumentId==id))throw new ArgumentException("Unknown instrument.");
            ConfigureInstruments(current.Select(c=>c.InstrumentId==id?c with{Enabled=enabled??c.Enabled,IntervalMs=intervalMs??c.IntervalMs}:c).ToArray());
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
