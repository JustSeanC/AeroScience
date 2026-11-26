namespace AeroScience.Shared;

// These checks validate a versioned evidence contract, not the truth of a simulator
// or real-world measurement. Runtime eligibility is decided by the host policy.
public static class SimulatorProvenance
{
    public const int Version = 1;
    public const string HostReceiptBasis = "Host UTC at source receipt; simulator clock components are separate; no inferred simulator UTC year";

    public static void Validate(SimulatorOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (origin.Version != Version || origin.SimulatorId is not ("msfs2024" or "xplane12")
            || origin.ConnectionEpoch == Guid.Empty || !Text(origin.AdapterId, 80) || !Text(origin.AdapterVersion, 80)
            || !Text(origin.MappingRevision, 100) || !Optional(origin.ReportedVersion, 100)
            || !Optional(origin.ApiVersion, 40) || !Optional(origin.AircraftType, 260)
            || !Optional(origin.AircraftRegistration, 260) || origin.ReportedBuild is < 0
            || !Optional(origin.TimestampBasis, 500) || origin.Limitations is {Length: > 32}
            || origin.Limitations?.Any(v => !Text(v, 1000)) == true)
            throw new ArgumentException("Unsupported or malformed simulator-origin evidence. Keep the original record; do not infer or downgrade its source.");
    }

    public static void Validate(SimulatorSampleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Version != Version || context.ConnectionEpoch == Guid.Empty
            || !Utc(context.HostReceivedUtc) || !Utc(context.ConfirmationStartedUtc) || !Utc(context.ConfirmationCompletedUtc)
            || context.ConfirmationCompletedUtc < context.ConfirmationStartedUtc
            || context.HostReceivedUtc < context.ConfirmationStartedUtc || context.HostReceivedUtc > context.ConfirmationCompletedUtc
            || !double.IsFinite(context.SnapshotSpanMilliseconds) || context.SnapshotSpanMilliseconds < 0
            || context.CoreSnapshotSpanMilliseconds is {} span && (!double.IsFinite(span) || span < 0 || span > context.SnapshotSpanMilliseconds)
            || context.Fields is not {Length: >= 1 and <= 128} || context.Fields.Any(f=>f is null)||context.Fields.Select(f=>f.Dataref).Distinct().Count()!=context.Fields.Length
            || context.Fields.Any(f=>!Text(f.Dataref,200)||!Utc(f.LastChangedUtc)||!Utc(f.LastReceivedUtc)
                || f.LastChangedUtc>f.LastReceivedUtc || f.LastConfirmedUtc is {} at && !Utc(at))
            || context.Capabilities is not {} capabilities || !Channels(capabilities.AvailableChannels) || !Channels(capabilities.UnavailableChannels)
            || capabilities.AvailableChannels.Intersect(capabilities.UnavailableChannels).Any()
            || capabilities.Limitations is not {Length: <= 32} || capabilities.Limitations.Any(v=>!Text(v,1000))
            || !Text(context.Continuity,1000))
            throw new ArgumentException("Unsupported or malformed simulator sample timing evidence.");
        if(context.ReportedValues is {} values&&(values.Length>64||values.Any(v=>v is null)||values.Select(v=>v.Channel).Distinct().Count()!=values.Length
            ||values.Any(v=>!Text(v.Channel,80)||!Text(v.Dataref,200)||!Text(v.Unit,40)||!double.IsFinite(v.Value)||!Utc(v.ConfirmedUtc)
                ||v.ConfirmedUtc<context.ConfirmationStartedUtc||v.ConfirmedUtc>context.ConfirmationCompletedUtc)))
            throw new ArgumentException("Invalid original simulator value/unit confirmation evidence.");
        if(context.Clock is {} clock && (!double.IsFinite(clock.RunningSeconds)||clock.RunningSeconds<0
            ||!double.IsFinite(clock.FlightSeconds)||clock.FlightSeconds<0
            ||new[]{clock.ZuluSeconds,clock.ActualRate,clock.DilationAwareRate,clock.DistanceRate}.Any(v=>v is {} n&&!double.IsFinite(n))))
            throw new ArgumentException("Invalid reported simulator clock evidence.");
    }

    public static bool SameRecording(SimulatorOrigin a, SimulatorOrigin b) => a.Version==b.Version
        && a.SimulatorId==b.SimulatorId && a.ConnectionEpoch==b.ConnectionEpoch && a.AdapterId==b.AdapterId
        && a.AdapterVersion==b.AdapterVersion && a.ApiVersion==b.ApiVersion && a.MappingRevision==b.MappingRevision;
    private static bool Utc(DateTimeOffset value) => value!=default && value.Offset==TimeSpan.Zero;
    private static bool Text(string? value,int max) => !string.IsNullOrWhiteSpace(value)&&value.Length<=max&&!value.Any(char.IsControl);
    private static bool Optional(string? value,int max) => value is null||Text(value,max);
    private static bool Channels(string[]? values) => values is {Length: <= 128}&&values.Distinct().Count()==values.Length&&values.All(v=>Text(v,200));
}
