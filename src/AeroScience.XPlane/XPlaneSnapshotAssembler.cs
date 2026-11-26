using System.Globalization;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.XPlane;

internal sealed record XPlaneValueConfirmation(long Id, JsonElement? Value, DateTimeOffset ReceivedUtc, long ReceivedStamp);
internal sealed record XPlaneConfirmationBatch(DateTimeOffset StartedUtc, long StartedStamp,
    DateTimeOffset CompletedUtc, long CompletedStamp, XPlaneValueConfirmation[] Values);

internal sealed class XPlaneSnapshotAssembler
{
    private sealed class Field(JsonElement raw, DateTimeOffset utc, long stamp, bool valid)
    {
        internal JsonElement Raw = raw;
        internal DateTimeOffset ChangedUtc = utc, ReceivedUtc = utc;
        internal DateTimeOffset? ConfirmedUtc;
        internal long ReceivedStamp = stamp;
        internal bool Valid = valid;
    }
    private readonly object gate = new();
    private readonly XPlaneOptions options;
    private readonly TimeProvider time;
    private readonly Dictionary<long, XPlaneResolvedDataref> definitions;
    private readonly Dictionary<long, Field> fields = [];
    private readonly HashSet<long> seeded = [];
    private readonly long requestId;
    private readonly SimulatorOrigin origin;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string[] unavailable;
    private bool acknowledged;
    private XPlaneConfirmationBatch? batch;
    private Dictionary<string, JsonElement?> confirmed = [];
    private long generation, published;
    private SimulatorClock? previousClock;
    private long? lastProgressStamp;
    private string? aircraftIdentity;
    private string? aircraftToken;
    private (double Lat, double Lon, double Speed, double FlightSeconds, DateTimeOffset Utc)? previousPosition;

    internal Guid Epoch => origin.ConnectionEpoch;
    internal Task Ready => ready.Task;
    internal int CachedFieldCount { get { lock (gate) return fields.Count; } }
    internal XPlaneSnapshotAssembler(XPlaneOptions options, TimeProvider time, XPlaneDiscovery discovery,
        SimulatorOrigin origin, long requestId)
    {
        options.Validate(); this.options = options; this.time = time; this.origin = origin; this.requestId = requestId;
        definitions = discovery.Found.ToDictionary(d => d.Id); unavailable = discovery.Unavailable;
    }

    internal bool Ingest(Guid epoch, ReadOnlyMemory<byte> bytes)
    {
        lock (gate)
        {
            if (epoch != Epoch) return false;
            var message = XPlaneProtocol.Parse(bytes, options.MaximumMessageBytes);
            var type = XPlaneProtocol.Text(message, "type", 64);
            if (type == "result")
            {
                var id = XPlaneProtocol.Id(message, "req_id");
                if (id > int.MaxValue) throw new XPlaneProtocolException("InvalidAcknowledgementId");
                if (id != requestId) return false; // Late or unrelated receipt cannot acknowledge this subscription.
                if (!XPlaneProtocol.Property(message, "success", out var success)
                    || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new XPlaneProtocolException("MalformedAcknowledgement");
                if (!success.GetBoolean()) throw new XPlaneProtocolException("SubscriptionRejected");
                acknowledged = true;
            }
            else if (type == "dataref_update_values")
            {
                if (!XPlaneProtocol.Property(message, "data", out var data) || data.ValueKind != JsonValueKind.Object)
                    throw new XPlaneProtocolException("MalformedValueUpdate");
                var utc = time.GetUtcNow().ToUniversalTime(); var stamp = time.GetTimestamp();
                foreach (var property in data.EnumerateObject())
                {
                    if (!long.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                        || id <= 0 || property.Name != id.ToString(CultureInfo.InvariantCulture)
                        || !definitions.TryGetValue(id, out var definition)) throw new XPlaneProtocolException("UnexpectedUpdateId");
                    Apply(definition, property.Value, utc, stamp, confirmation: false);
                    seeded.Add(id);
                }
            }
            else throw new XPlaneProtocolException("UnexpectedMessageType");
            if (acknowledged && definitions.Values.Where(d => d.Definition.Required).All(d => seeded.Contains(d.Id)))
            {
                if (previousClock is null) previousClock = Clock(fields.Where(p => p.Value.Valid)
                    .ToDictionary(p => definitions[p.Key].Definition.Key, p => (JsonElement?)p.Value.Raw));
                ready.TrySetResult();
            }
            return true;
        }
    }

    private void Apply(XPlaneResolvedDataref definition, JsonElement value, DateTimeOffset utc, long stamp, bool confirmation)
    {
        var valid = Valid(definition.Definition, value);
        fields.TryGetValue(definition.Id, out var existing);
        if (existing is not null && stamp < existing.ReceivedStamp)
        {
            // A GET may finish before a newer WebSocket delta but be assembled
            // afterward. Keep the latest cache and the earlier read's real time.
            if (confirmation && valid) existing.ConfirmedUtc = utc;
            return;
        }
        if (valid && existing is { Valid: true })
        {
            if (definition.Definition.Key is "running-seconds" or "flight-seconds"
                && value.GetDouble() < existing.Raw.GetDouble())
                throw new XPlaneProtocolException("SimulatorClockReset", ConnectionState.Initializing);
            if (definition.Definition.Key is "replay" or "requested-rate" or "distance-rate"
                && !JsonElement.DeepEquals(existing.Raw, value))
                throw new XPlaneProtocolException(definition.Definition.Key == "replay" ? "ReplayTransition" : "AccelerationTransition", ConnectionState.Initializing);
        }
        if (valid && aircraftIdentity is not null && definition.Definition.Key == "aircraft"
            && Text(definition.Definition, value) != aircraftIdentity)
            throw new XPlaneProtocolException("AircraftChanged", ConnectionState.Initializing);
        if (valid && aircraftToken is not null && definition.Definition.Key == "aircraft-token"
            && Text(definition.Definition, value) != aircraftToken)
            throw new XPlaneProtocolException("AircraftReloadCandidate", ConnectionState.Initializing);
        if (!fields.TryGetValue(definition.Id, out var field)) fields[definition.Id] = field = new(value.Clone(), utc, stamp, valid);
        else
        {
            if (!JsonElement.DeepEquals(field.Raw, value)) field.ChangedUtc = utc;
            field.Raw = value.Clone(); field.ReceivedUtc = utc; field.ReceivedStamp = stamp; field.Valid = valid;
        }
        if (confirmation && valid) field.ConfirmedUtc = utc;
    }

    internal bool Confirm(Guid epoch, XPlaneConfirmationBatch next)
    {
        lock (gate)
        {
            if (epoch != Epoch) return false;
            if (next.Values.Length != definitions.Count || next.Values.Select(v => v.Id).Distinct().Count() != definitions.Count
                || time.GetElapsedTime(next.StartedStamp, next.CompletedStamp) < TimeSpan.Zero
                || next.Values.Any(v => !definitions.ContainsKey(v.Id)
                    || time.GetElapsedTime(next.StartedStamp, v.ReceivedStamp) < TimeSpan.Zero
                    || time.GetElapsedTime(v.ReceivedStamp, next.CompletedStamp) < TimeSpan.Zero))
                throw new XPlaneProtocolException("InvalidConfirmationGroup");
            if (batch is { } old && (time.GetElapsedTime(old.CompletedStamp, next.StartedStamp) > options.MaximumConfirmationAge
                || next.CompletedUtc < old.CompletedUtc)) throw new XPlaneProtocolException("ConfirmationContinuityLost", ConnectionState.Initializing);
            var values = new Dictionary<string, JsonElement?>();
            foreach (var item in next.Values)
            {
                var definition = definitions[item.Id];
                if (item.Value is { } value)
                {
                    Apply(definition, value, item.ReceivedUtc, item.ReceivedStamp, confirmation: true);
                    values[definition.Definition.Key] = Valid(definition.Definition, value) ? value.Clone() : null;
                }
                else
                {
                    if (fields.TryGetValue(item.Id, out var field)) field.Valid = false;
                    values[definition.Definition.Key] = null;
                }
            }
            batch = next; confirmed = values; generation++;
            var clock = Clock(values);
            if (clock is null) return true;
            if (previousClock is { } previous)
            {
                if (clock.RunningSeconds < previous.RunningSeconds || clock.FlightSeconds < previous.FlightSeconds)
                    throw new XPlaneProtocolException("SimulatorClockReset", ConnectionState.Initializing);
                if (clock.Replay != previous.Replay) throw new XPlaneProtocolException("ReplayTransition", ConnectionState.Initializing);
                if (clock.RequestedRate is { } rate && previous.RequestedRate is { } oldRate && rate != oldRate
                    || clock.DistanceRate is { } distanceRate && previous.DistanceRate is { } oldDistanceRate && distanceRate != oldDistanceRate)
                    throw new XPlaneProtocolException("AccelerationTransition", ConnectionState.Initializing);
                if (clock.Paused || clock.Paused != previous.Paused) lastProgressStamp = null;
                if (!clock.Paused && clock.FlightSeconds > previous.FlightSeconds && clock.RunningSeconds > previous.RunningSeconds)
                    lastProgressStamp = next.Values.Where(v => definitions[v.Id].Definition.Key is "running-seconds" or "flight-seconds")
                        .Min(v => v.ReceivedStamp);
            }
            previousClock = clock;
            return true;
        }
    }

    internal TelemetryUpdate? Snapshot()
    {
        lock (gate)
        {
            if (!ready.Task.IsCompletedSuccessfully || batch is not { } current) return Status("WaitingForInitialSnapshot");
            var now = time.GetTimestamp();
            if (time.GetElapsedTime(current.CompletedStamp, now) > options.MaximumConfirmationAge) return Status("ConfirmationStale");
            var coreEnd = current.Values.Where(v => definitions[v.Id].Definition.Required).MaxBy(v => v.ReceivedStamp)!;
            if (current.CompletedUtc < current.StartedUtc || time.GetElapsedTime(current.StartedStamp, coreEnd.ReceivedStamp) > options.MaximumSnapshotSpan)
                return Status("SnapshotSpanExceeded");
            if (definitions.Values.Any(d => d.Definition.Required && (!confirmed.TryGetValue(d.Definition.Key, out var raw)
                || raw is null || !fields.TryGetValue(d.Id, out var field) || !field.Valid
                || time.GetElapsedTime(current.Values.Single(v => v.Id == d.Id).ReceivedStamp, now) is var age
                    && (age < TimeSpan.Zero || age > options.MaximumConfirmationAge)))) return Status("CoreMeasurementUnavailable");
            var clock = Clock(confirmed);
            if (clock is null) return Status("ClockUnavailable");
            if (definitions.Values.Single(d => d.Definition.Key == "paused") is { } pauseRef
                && fields[pauseRef.Id].Raw.GetInt32() != (clock.Paused ? 1 : 0))
                return Status("PauseChangeAwaitingConfirmation");
            if (clock.Replay) return Status("ReplayNotLiveFlight");
            if (!clock.Paused && (lastProgressStamp is null || time.GetElapsedTime(lastProgressStamp.Value, now) > options.MaximumConfirmationAge))
                return Status("FlightProgressUnverified");
            if (generation == published) return null; // A heartbeat/consumer timer cannot mint a new measurement.

            var aircraftDefinition = definitions.Values.Single(d => d.Definition.Key == "aircraft").Definition;
            var aircraft = Text(aircraftDefinition, confirmed["aircraft"]!.Value)!;
            double Value(string key) => Number(confirmed, key)!.Value;
            double? Optional(string key)
            {
                var definition = definitions.Values.FirstOrDefault(d => d.Definition.Key == key);
                return definition is not null && fields.TryGetValue(definition.Id, out var field) && field.Valid
                    && time.GetElapsedTime(current.Values.Single(v => v.Id == definition.Id).ReceivedStamp, now) is var age
                    && age >= TimeSpan.Zero && age <= options.MaximumConfirmationAge
                    && time.GetElapsedTime(coreEnd.ReceivedStamp, current.Values.Single(v => v.Id == definition.Id).ReceivedStamp).Duration()
                        <= options.MaximumSnapshotSpan ? Number(confirmed, key) : null;
            }
            var utc = coreEnd.ReceivedUtc.ToUniversalTime();
            var frame = new TelemetryFrame(utc, TelemetrySource.Simulator, aircraft, Value("latitude"), Value("longitude"),
                XPlaneDatarefs.Feet(Value("msl")), Optional("agl") is { } agl ? XPlaneDatarefs.Feet(agl) : null,
                Value("ias"), XPlaneDatarefs.Knots(Value("tas")), XPlaneDatarefs.Knots(Value("groundspeed")),
                XPlaneDatarefs.Heading(Value("heading")), XPlaneDatarefs.Heading(Value("track")), Value("pitch"), Value("bank"),
                XPlaneDatarefs.FeetPerMinute(Value("vertical-speed")), clock.Paused, Optional("mach"));
            if (!frame.IsValid) return Status("NormalizedFrameInvalid");
            if (previousPosition is { } prior && !clock.Paused)
            {
                var elapsed = Math.Max((utc - prior.Utc).TotalSeconds, clock.FlightSeconds - prior.FlightSeconds);
                var allowance = Math.Max(prior.Speed, Value("groundspeed")) * Math.Max(0, elapsed) * Math.Max(1, clock.DistanceRate ?? 1)
                    + options.DiscontinuityToleranceMetres;
                if (Distance(prior.Lat, prior.Lon, frame.Latitude, frame.Longitude) > allowance)
                    throw new XPlaneProtocolException("PositionDiscontinuityCandidate", ConnectionState.Initializing);
            }
            previousPosition = (frame.Latitude, frame.Longitude, Value("groundspeed"), clock.FlightSeconds, utc);
            aircraftIdentity = aircraft;
            var tokenDefinition = definitions.Values.FirstOrDefault(d => d.Definition.Key == "aircraft-token");
            if (tokenDefinition is not null && confirmed.GetValueOrDefault("aircraft-token") is { } tokenValue)
                aircraftToken = Text(tokenDefinition.Definition, tokenValue);

            var temperature = Optional("temperature"); var pressure = Optional("pressure"); var wind = Optional("wind-speed");
            var direction = Optional("wind-direction"); var density = Optional("density");
            var atmosphereIds = definitions.Values.Where(d => d.Definition.Key is "temperature" or "pressure" or "wind-speed" or "wind-direction" or "density")
                .Where(d => Optional(d.Definition.Key) is not null)
                .Select(d => d.Id).ToHashSet();
            var atmosphereUtc = current.Values.Where(v => atmosphereIds.Contains(v.Id) && v.Value is not null)
                .Select(v => v.ReceivedUtc).DefaultIfEmpty(utc).Max();
            var atmosphere = new AtmosphericFrame(atmosphereUtc.ToUniversalTime(), temperature,
                pressure is { } p ? XPlaneDatarefs.InHg(p) : null, wind is { } w ? XPlaneDatarefs.Knots(w) : null,
                direction is { } d ? XPlaneDatarefs.Heading(d) : null, density is { } rho ? XPlaneDatarefs.SlugsPerCubicFoot(rho) : null,
                clock.Paused ? ObservationQuality.Paused : temperature is null && pressure is null && wind is null && direction is null && density is null
                    ? ObservationQuality.Unavailable : ObservationQuality.Valid,
                "X-Plane aircraft-local reported state; not independent real-world atmospheric measurements.");
            published = generation;
            return new(Connection(ConnectionState.Connected, "X-Plane candidate telemetry connected; installed flight-state validation pending."), frame, atmosphere)
                { SampleContext = Context(clock) };
        }
    }

    private TelemetryUpdate Status(string code) => new(Connection(ConnectionState.Initializing, code), null)
        { SampleContext = batch is null ? null : Context(Clock(confirmed)) };
    private ConnectionInfo Connection(ConnectionState state, string code)
    {
        string? AircraftText(string key)
        {
            var definition = definitions.Values.FirstOrDefault(d => d.Definition.Key == key);
            return definition is not null && confirmed.GetValueOrDefault(key) is { } raw ? Text(definition.Definition, raw) : null;
        }
        return new(state, TelemetrySource.Simulator, code, time.GetUtcNow().ToUniversalTime())
        {
            Origin = origin with { ReportedBuild = Number(confirmed, "build") is { } build ? checked((long)build) : null,
                AircraftType = AircraftText("aircraft-type"), AircraftRegistration = AircraftText("registration") }
        };
    }
    private SimulatorSampleContext Context(SimulatorClock? clock)
    {
        var current = batch!;
        var now = time.GetTimestamp();
        var coreEnd = current.Values.Where(v => definitions[v.Id].Definition.Required).MaxBy(v => v.ReceivedStamp)!;
        var available = definitions.Values.Where(d => confirmed.GetValueOrDefault(d.Definition.Key) is not null
            && fields.TryGetValue(d.Id, out var field) && field.Valid
            && time.GetElapsedTime(current.Values.Single(v => v.Id == d.Id).ReceivedStamp, now) is var age
            && age >= TimeSpan.Zero && age <= options.MaximumConfirmationAge
            && (d.Definition.Required || time.GetElapsedTime(coreEnd.ReceivedStamp, current.Values.Single(v => v.Id == d.Id).ReceivedStamp).Duration()
                <= options.MaximumSnapshotSpan)).Select(d => d.Definition.Key).Order().ToArray();
        var missing = XPlaneDatarefs.All.Select(d => d.Key).Except(available).Concat(unavailable)
            .Concat(["humidity-at-aircraft", "in-cloud", "cloud-density", "precipitation", "wind-vectors", "ambient-visibility", "detailed-guidance"]).Distinct().Order().ToArray();
        var timings = fields.Select(p => new SimulatorFieldTiming(definitions[p.Key].Definition.Name,
            p.Value.ChangedUtc, p.Value.ReceivedUtc, p.Value.ConfirmedUtc, available.Contains(definitions[p.Key].Definition.Key))).OrderBy(p => p.Dataref).ToArray();
        return new(1, Epoch, coreEnd.ReceivedUtc, current.StartedUtc, current.CompletedUtc,
            time.GetElapsedTime(current.StartedStamp, current.CompletedStamp).TotalMilliseconds, clock, timings,
            new(available, missing, ["Candidate flight/loading and attitude conventions require genuine X-Plane validation.",
                "Read confirmations are a bounded non-atomic composite; field receipt times remain separate.",
                "Host policy independently qualifies recording and supported science; simulator UTC year is unknown.",
                "A new epoch does not establish continuity with an earlier flight."]),
            CoreSnapshotSpanMilliseconds: time.GetElapsedTime(current.StartedStamp, coreEnd.ReceivedStamp).TotalMilliseconds)
            {ReportedValues=definitions.Values.Where(d=>available.Contains(d.Definition.Key)&&XPlaneDatarefs.ReportedUnits.ContainsKey(d.Definition.Key))
                .Select(d=>new SimulatorReportedValue(d.Definition.Key,d.Definition.Name,Number(confirmed,d.Definition.Key)!.Value,XPlaneDatarefs.ReportedUnits[d.Definition.Key],
                    current.Values.Single(v=>v.Id==d.Id).ReceivedUtc)).OrderBy(v=>v.Channel).ToArray()};
    }

    private static SimulatorClock? Clock(IReadOnlyDictionary<string, JsonElement?> values)
    {
        if (Number(values, "running-seconds") is not { } running || Number(values, "flight-seconds") is not { } flight
            || Number(values, "paused") is not { } paused || Number(values, "replay") is not { } replay) return null;
        int? Integer(string key) => Number(values, key) is { } value ? checked((int)value) : null;
        return new(running, flight, paused == 1, replay == 1, Number(values, "zulu-seconds"), Integer("local-day"),
            Integer("zulu-day"), null, Integer("requested-rate"), Number(values, "actual-rate"),
            Number(values, "dilation-rate"), Number(values, "distance-rate"));
    }
    private static double? Number(IReadOnlyDictionary<string, JsonElement?> values, string key)
    {
        if (values.TryGetValue(key, out var raw) && raw is { ValueKind: JsonValueKind.Number } value
            && value.TryGetDouble(out var number) && double.IsFinite(number)) return number;
        return null;
    }
    private static bool Valid(XPlaneDataref definition, JsonElement value)
    {
        if (definition.ValueType == "data") return Text(definition, value) is not null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
            || definition.ValueType == "float" && Math.Abs(number) > float.MaxValue
            || definition.ValueType == "int" && !value.TryGetInt32(out _)) return false;
        return definition.Key switch
        {
            "latitude" => number is >= -90 and <= 90,
            "longitude" => number is >= -180 and <= 180,
            "pitch" => number is >= -90 and <= 90,
            "bank" => number is >= -180 and <= 180,
            "paused" or "replay" => number is 0 or 1,
            "ias" or "tas" or "groundspeed" or "agl" or "mach" or "wind-speed" or "running-seconds" or "flight-seconds"
                or "requested-rate" or "actual-rate" or "dilation-rate" or "distance-rate" => number >= 0,
            "local-day" or "zulu-day" => number is >= 0 and <= 366,
            "zulu-seconds" => number is >= 0 and < 86400,
            "build" => number is > 0 and <= int.MaxValue,
            "temperature" => number >= -273.15,
            "pressure" or "density" => number > 0,
            "wind-direction" => number is >= 0 and <= 360,
            _ => true
        };
    }
    private static string? Text(XPlaneDataref definition, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        try
        {
            var raw = Convert.FromBase64String(value.GetString()!);
            if (raw.Length is < 1 || raw.Length > definition.MaximumBytes) return null;
            var text = new UTF8Encoding(false, true).GetString(raw).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.Contains('\ufffd') ? null : text;
        }
        catch (Exception error) when (error is FormatException or DecoderFallbackException or InvalidOperationException) { return null; }
    }
    private static double Distance(double lat1, double lon1, double lat2, double lon2)
    {
        static double Radians(double value) => value * Math.PI / 180;
        var sinLat = Math.Sin(Radians(lat2 - lat1) / 2); var sinLon = Math.Sin(Radians(lon2 - lon1) / 2);
        var a = sinLat * sinLat + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * sinLon * sinLon;
        // Diagnostic only, not a new science/coverage calculation or a safe-speed limit.
        return 6371008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }
}
