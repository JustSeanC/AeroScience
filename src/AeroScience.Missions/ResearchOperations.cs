using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

// Invoked under MissionCoordinator's existing command/acquisition gate.
public sealed class ResearchOperations(MissionStore missions, ScienceStore science, Func<ScienceUnitPreferences>? displayUnits = null,Func<ScientificAccessPreferences>? sourcePreferences=null) : IDisposable
{
    private readonly RemoteAcquisition remote = new(missions, science,sourcePreferences);
    public void Dispose() => remote.Dispose();
    private sealed class Acquisition
    {
        public DateTimeOffset? Anchor, LastSeen, FirstSuccess, LastSuccess, CadenceReference;
        public double IntervalSeconds; public long IntervalCount;
        public long Slot = -1, Observations, Rejected, Missing, Acquisitions;
        public ScienceDataset? Dataset;
        public string? Error;
    }
    private InstrumentConfiguration[] configurations = ScienceCatalog.Normalize(null);
    private readonly Dictionary<string, Acquisition> acquisitions = [];
    private Mission? mission;
    private MissionResearch? context;
    private AtmosphericFrame? atmosphere;
    private FlightGuidance? guidance, baseline;
    private string? aircraft;
    private bool intentionallyPaused;
    private DateTimeOffset? pauseStarted;
    private double pauseSeconds;
    public InstrumentConfiguration[] Configurations => configurations.ToArray();
    public void EnsureCanStart(InstrumentConfiguration[] input)
    {
        if (!ScienceCatalog.Normalize(input).Any(c => c.Enabled)) return;
        try { science.Initialize(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        { throw new InvalidOperationException("Scientific acquisition needs its selected library. Reconnect it or disable instruments to record flight data only. " + error.Message, error); }
    }
    public void Begin(Mission active, MissionResearch research)
    {
        mission = active; context = research; configurations = ScienceCatalog.Normalize(research.Instruments);
        acquisitions.Clear(); baseline = null; intentionallyPaused = false; pauseStarted = null; pauseSeconds = 0;
        remote.Begin();
    }
    public void End()
    {
        var now=DateTimeOffset.UtcNow;
        try
        {
            if(mission is not null)missions.AddEvent(mission.Id,now,"science-stopped",$"Science collection stopped. Intentional pause seconds: {pauseSeconds+(pauseStarted is {} began?Math.Max(0,(now-began).TotalSeconds):0):0.###}. Collection will not resume automatically after a restart.");
        }
        finally{pauseStarted=null;intentionallyPaused=false;mission=null;context=null;baseline=null;remote.Reset(true);}
    }
    public void Configure(InstrumentConfiguration[] input)
    {
        var next = InstrumentProfiles.Freeze(ScienceCatalog.Normalize(input));
        if (JsonSerializer.Serialize(configurations,WireJson.Options)==JsonSerializer.Serialize(next,WireJson.Options)) return;
        if (mission is not null) EnsureCanStart(next);
        foreach (var config in next)
        {
            var previous = configurations.First(c => c.InstrumentId == config.InstrumentId);
            if (JsonSerializer.Serialize(previous,WireJson.Options)!=JsonSerializer.Serialize(config,WireJson.Options) && acquisitions.TryGetValue(config.InstrumentId, out var run))
            { run.Anchor = null; run.Slot = -1; run.Error = null; run.CadenceReference = null; }
        }
        configurations = next;
        remote.Reset(intentionallyPaused);
        if (mission is not null)
        {
            missions.SetInstruments(mission.Id, next);
            missions.AddEvent(mission.Id, DateTimeOffset.UtcNow, "instruments-configured", JsonSerializer.Serialize(next, WireJson.Options));
        }
    }
    public bool SetPaused(bool paused, DateTimeOffset? timestamp = null)
    {
        if (mission is null) throw new InvalidOperationException("Science Collection controls require an active mission.");
        if (intentionallyPaused == paused) return false;
        var now = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var seconds = !paused && pauseStarted is { } began ? Math.Max(0, (now - began).TotalSeconds) : 0;
        missions.AddEvent(mission.Id, now, paused ? "science-paused" : "science-resumed", paused ? "Science Collection paused by the user. Flight recording continues." : $"Science Collection resumed after {seconds:0.###} seconds. Intentional slots are excluded from missing counts.");
        intentionallyPaused = paused;
        remote.Reset(paused);
        if (paused) pauseStarted = now; else { pauseSeconds += seconds; pauseStarted = null; }
        foreach (var run in acquisitions.Values) { run.Anchor = null; run.Slot = -1; run.CadenceReference = null; }
        return true;
    }
    public void Observe(TelemetryUpdate update, TelemetryFrame? position)
    {
        if (update.Connection.State != ConnectionState.Connected)
        { baseline = null; return; }
        if (position is not null && aircraft != position.Aircraft) { aircraft = position.Aircraft; baseline = null; }
        if (position?.IsPaused == true) baseline = null;
        if (update.Guidance is { } observed && (guidance is null || observed.TimestampUtc > guidance.TimestampUtc))
        {
            var previous = guidance; guidance = observed;
            if (observed.Quality != ObservationQuality.Valid || observed.Autopilot == AutopilotEngagement.Unavailable
                || position?.IsPaused == true || previous is not null && observed.TimestampUtc - previous.TimestampUtc > TimeSpan.FromSeconds(6)) baseline = null;
            else
            {
                if (mission is not null && baseline is { } before)
                {
                    var changes = new List<string>();
                    if (before.Autopilot != observed.Autopilot) changes.Add($"AP {before.Autopilot} → {observed.Autopilot}");
                    foreach (var (name, oldMode, newMode) in new[] { ("Lateral", before.Lateral, observed.Lateral), ("Vertical", before.Vertical, observed.Vertical), ("Speed", before.Speed, observed.Speed) })
                        if (oldMode.Mode is not null && newMode.Mode is not null && oldMode.Mode != newMode.Mode)
                            changes.Add($"{name} {oldMode.Mode} → {newMode.Mode}{(newMode.Target is { } target ? $" ({target:0.###} {newMode.Unit})" : "")}");
                    if (changes.Count > 0) missions.AddEvent(mission.Id, observed.TimestampUtc, "flight-guidance", string.Join("; ", changes));
                }
                baseline = observed;
            }
        }
        if (mission is not null && context is not null && !intentionallyPaused) remote.Offer(mission, context, configurations, update);
        if (update.Atmosphere is not { } source) return;
        if (atmosphere is not null && source.TimestampUtc <= atmosphere.TimestampUtc) return;
        atmosphere = source;
        if (mission is null || context is null || intentionallyPaused || position?.IsPaused == true || source.Quality != ObservationQuality.Valid) return;
        var located = position is not null && Math.Abs((source.TimestampUtc - position.TimestampUtc).TotalSeconds) <= 3 ? position : null;
        foreach (var config in configurations.Where(c => c.Enabled && !RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)))
        {
            if (!acquisitions.TryGetValue(config.InstrumentId, out var run)) acquisitions[config.InstrumentId] = run = new();
            if (run.LastSeen is not null && source.TimestampUtc <= run.LastSeen) continue;
            run.LastSeen = source.TimestampUtc; run.Anchor ??= source.TimestampUtc;
            var slot = (long)Math.Floor(((source.TimestampUtc - run.Anchor.Value).TotalMilliseconds + Math.Min(100, config.IntervalMs * .1)) / config.IntervalMs);
            if (slot <= run.Slot) continue;
            var gap = run.Slot >= 0 ? Math.Max(0, slot - run.Slot - 1) : 0; run.Missing += gap; run.Slot = slot;
            var instrument = ScienceCatalog.Instrument(config.InstrumentId);
            var readings = instrument.Read(source); run.Rejected += readings.Count(v => v.Value is null || v.Quality != ObservationQuality.Valid);
            var valid = readings.Where(v => v.Value is not null && v.Quality == ObservationQuality.Valid).ToArray();
            if (valid.Length == 0) { Persist(config, run, readings.ToArray(), ObservationQuality.Unavailable); continue; }
            try
            {
                run.Dataset ??= science.EnsureDataset(mission, config, context);
                missions.AddDatasetReference(mission.Id, new(run.Dataset.Id, run.Dataset.LibraryId, run.Dataset.RelativePath, run.Dataset.ProvenanceJson));
                var values = valid.Select(v => new ScienceObservation(0, run.Dataset.Id, mission.Id, config.InstrumentId,
                    source.TimestampUtc, v.MeasurementId, v.Value!.Value, v.Unit, v.Provenance,
                    (update.Connection.Source == TelemetrySource.Simulated ? "Synthetic test fixture / " : "MSFS 2024 / SimConnect ") + instrument.Definition.Measurements.Single(m => m.Id == v.MeasurementId).SimulatorVariable,
                    located?.Latitude, located?.Longitude, located?.AltitudeMslFeet, gap > 0 ? ObservationQuality.AcquisitionGap : ObservationQuality.Valid,
                    located?.TimestampUtc)).ToArray();
                var count = science.Append(values);
                if (run.Error is not null) missions.AddEvent(mission.Id, source.TimestampUtc, "science-recovered", config.InstrumentId + ": acquisition resumed"); run.Observations += count;
                if (count > 0)
                {
                    if (run.CadenceReference is { } previousSuccess) { run.IntervalSeconds += (source.TimestampUtc - previousSuccess).TotalSeconds; run.IntervalCount++; }
                    run.CadenceReference = source.TimestampUtc; run.FirstSuccess ??= source.TimestampUtc; run.LastSuccess = source.TimestampUtc; run.Acquisitions++;
                }
                run.Error = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
            { if (run.Error != error.Message) missions.AddEvent(mission.Id, source.TimestampUtc, "science-error", config.InstrumentId + ": " + error.Message); run.Error = error.Message; run.Rejected += valid.Length; }
            Persist(config, run, readings.ToArray(), run.Error is null ? gap > 0 ? ObservationQuality.AcquisitionGap : ObservationQuality.Valid : ObservationQuality.Unavailable);
        }
    }
    private void Persist(InstrumentConfiguration config, Acquisition run, MeasurementReading[] readings, ObservationQuality quality)
    {
        if (mission is null) return;
        missions.SaveInstrumentState(mission.Id, new(config.InstrumentId, config.Enabled, config.IntervalMs, quality, run.LastSuccess,
            run.Observations, run.Rejected, run.Missing, run.IntervalCount > 0 ? run.IntervalSeconds / run.IntervalCount : null, readings, run.Error, run.Acquisitions));
    }
    public FlightGuidance? CurrentGuidance(ConnectionInfo connection, TelemetryFrame? position)
    {
        if (guidance is null) return null;
        var quality = connection.State != ConnectionState.Connected ? ObservationQuality.SourceDisconnected
            : position?.IsPaused == true ? ObservationQuality.Paused
            : DateTimeOffset.UtcNow - guidance.TimestampUtc > TimeSpan.FromSeconds(6) ? ObservationQuality.Stale : guidance.Quality;
        return quality == ObservationQuality.Valid ? guidance : guidance with
        {
            Quality = quality,
            Autopilot = AutopilotEngagement.Unavailable,
            Lateral = new(null, Status: quality.ToString()),
            Vertical = new(null, Status: quality.ToString()),
            Speed = new(null, Status: quality.ToString())
        };
    }
    public ResearchState Snapshot(ConnectionInfo connection, TelemetryFrame? position)
    {
        var states = configurations.Select(config =>
        {
            if (RemoteAcquisition.Instruments.ContainsKey(config.InstrumentId))
            {
                var r = remote.Status(config.InstrumentId);
                var q = !config.Enabled ? ObservationQuality.Disabled : intentionallyPaused ? ObservationQuality.Paused
                    : connection.State != ConnectionState.Connected ? ObservationQuality.SourceDisconnected
                    : position is null || DateTimeOffset.UtcNow - position.TimestampUtc > TimeSpan.FromSeconds(6) ? ObservationQuality.Stale : r.Quality;
                return new InstrumentState(config.InstrumentId, config.Enabled, config.IntervalMs, q, r.LastSuccessfulUtc, r.Products, r.Rejected, r.MissingIntervals, null, [], r.Processing ? "Processing a bounded geographic subset; flight recording continues." : r.Status+" · "+r.Message+" Processing: "+r.ProcessingSeconds.ToString("0.000")+" s", r.Products,
                    config.Payload is {} payload?(payload.Custom?"Custom · ":"")+payload.Profile.Name:null,config.Payload?.Profile.Id);
            }
            acquisitions.TryGetValue(config.InstrumentId, out var run);
            var quality = !config.Enabled ? ObservationQuality.Disabled
                : intentionallyPaused ? ObservationQuality.Paused
                : run?.Error is not null ? ObservationQuality.Unavailable
                : connection.State != ConnectionState.Connected ? ObservationQuality.SourceDisconnected
                : position?.IsPaused == true ? ObservationQuality.Paused
                : atmosphere is null ? ObservationQuality.Waiting
                : DateTimeOffset.UtcNow - atmosphere.TimestampUtc > TimeSpan.FromSeconds(6) ? ObservationQuality.Stale : atmosphere.Quality;
            var readings = atmosphere is null ? [] : ScienceCatalog.Instrument(config.InstrumentId).Read(atmosphere).ToArray();
            if (quality == ObservationQuality.Valid && readings.All(v => v.Value is null)) quality = ObservationQuality.Unavailable;
            return new InstrumentState(config.InstrumentId, config.Enabled, config.IntervalMs, quality, run?.LastSuccess,
                run?.Observations ?? 0, run?.Rejected ?? 0, run?.Missing ?? 0,
                run is { IntervalCount: > 0 } ? run.IntervalSeconds / run.IntervalCount : null,
                readings.Select(r => ScienceUnits.Display(r, (displayUnits?.Invoke() ?? ScienceUnits.Aviation))).ToArray(), run?.Error, run?.Acquisitions ?? 0);
        }).ToArray();
        return new(CurrentGuidance(connection, position), atmosphere, states, context?.PlanSnapshot?.Id,
            context?.Template, science.AvailabilityError(),
            mission is null ? ScienceCollectionStatus.Stopped : intentionallyPaused ? ScienceCollectionStatus.Paused :
            !states.Any(i => i.Enabled) ? ScienceCollectionStatus.Stopped :
            connection.State != ConnectionState.Connected || atmosphere is null || position?.IsPaused == true ? ScienceCollectionStatus.AwaitingSource :
            states.Where(i => i.Enabled).All(i => i.Quality is ObservationQuality.Unavailable or ObservationQuality.Stale) ? ScienceCollectionStatus.Unavailable : ScienceCollectionStatus.Collecting,
            intentionallyPaused, pauseStarted, pauseSeconds + (pauseStarted is { } start ? Math.Max(0, (DateTimeOffset.UtcNow - start).TotalSeconds) : 0), displayUnits?.Invoke() ?? ScienceUnits.Aviation);
    }
}
