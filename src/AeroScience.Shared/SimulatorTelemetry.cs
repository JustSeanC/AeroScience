namespace AeroScience.Shared;

// Live host availability only. Never assigns identity to historical evidence.
public sealed record SimulatorAvailability(int Version, string Id, bool ProductionRecordingReady,
    bool ScienceIntegrationReady, string ReasonKey);

// Versioned source identity. Presence is not an installed-simulator attestation.
public sealed record SimulatorOrigin(int Version, string SimulatorId, string? ReportedVersion,
    string AdapterId, string AdapterVersion, string? ApiVersion, Guid ConnectionEpoch,
    string MappingRevision, long? ReportedBuild = null, string? AircraftType = null,
    string? AircraftRegistration = null) : IVersionedEvidence
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TimestampBasis { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Limitations { get; init; }
    public bool Equals(SimulatorOrigin? other) => other is not null&&Version==other.Version&&SimulatorId==other.SimulatorId
        &&ReportedVersion==other.ReportedVersion&&AdapterId==other.AdapterId&&AdapterVersion==other.AdapterVersion&&ApiVersion==other.ApiVersion
        &&ConnectionEpoch==other.ConnectionEpoch&&MappingRevision==other.MappingRevision&&ReportedBuild==other.ReportedBuild
        &&AircraftType==other.AircraftType&&AircraftRegistration==other.AircraftRegistration&&TimestampBasis==other.TimestampBasis
        &&(Limitations is null?other.Limitations is null:other.Limitations is not null&&Limitations.SequenceEqual(other.Limitations));
    public override int GetHashCode()=>HashCode.Combine(Version,SimulatorId,AdapterId,MappingRevision,ConnectionEpoch);
}

public sealed record SimulatorClock(double RunningSeconds, double FlightSeconds, bool Paused,
    bool Replay, double? ZuluSeconds = null, int? LocalDay = null, int? ZuluDay = null,
    int? Year = null, int? RequestedRate = null, double? ActualRate = null,
    double? DilationAwareRate = null, double? DistanceRate = null);

public sealed record SimulatorFieldTiming(string Dataref, DateTimeOffset LastChangedUtc,
    DateTimeOffset LastReceivedUtc, DateTimeOffset? LastConfirmedUtc, bool Available);
public sealed record SimulatorReportedValue(string Channel,string Dataref,double Value,string Unit,DateTimeOffset ConfirmedUtc);

public sealed record SimulatorCapabilities(string[] AvailableChannels, string[] UnavailableChannels,
    string[] Limitations, bool ProductionRecordingReady = false, bool ScienceIntegrationReady = false)
{
    public bool Equals(SimulatorCapabilities? other)=>other is not null&&ProductionRecordingReady==other.ProductionRecordingReady&&ScienceIntegrationReady==other.ScienceIntegrationReady
        &&AvailableChannels.SequenceEqual(other.AvailableChannels)&&UnavailableChannels.SequenceEqual(other.UnavailableChannels)&&Limitations.SequenceEqual(other.Limitations);
    public override int GetHashCode()=>HashCode.Combine(ProductionRecordingReady,ScienceIntegrationReady,AvailableChannels.Length,UnavailableChannels.Length);
}

public sealed record SimulatorSampleContext(int Version, Guid ConnectionEpoch,
    DateTimeOffset HostReceivedUtc, DateTimeOffset ConfirmationStartedUtc,
    DateTimeOffset ConfirmationCompletedUtc, double SnapshotSpanMilliseconds,
    SimulatorClock? Clock, SimulatorFieldTiming[] Fields, SimulatorCapabilities Capabilities,
    string Continuity = "New epoch; flight continuity unverified", double? CoreSnapshotSpanMilliseconds = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SimulatorReportedValue[]? ReportedValues { get; init; }
    public bool Equals(SimulatorSampleContext? other)=>other is not null&&Version==other.Version&&ConnectionEpoch==other.ConnectionEpoch
        &&HostReceivedUtc==other.HostReceivedUtc&&ConfirmationStartedUtc==other.ConfirmationStartedUtc&&ConfirmationCompletedUtc==other.ConfirmationCompletedUtc
        &&SnapshotSpanMilliseconds==other.SnapshotSpanMilliseconds&&Clock==other.Clock&&Fields.SequenceEqual(other.Fields)
        &&Capabilities==other.Capabilities&&Continuity==other.Continuity&&CoreSnapshotSpanMilliseconds==other.CoreSnapshotSpanMilliseconds
        &&(ReportedValues is null?other.ReportedValues is null:other.ReportedValues is not null&&ReportedValues.SequenceEqual(other.ReportedValues));
    public override int GetHashCode()=>HashCode.Combine(Version,ConnectionEpoch,HostReceivedUtc,ConfirmationStartedUtc,ConfirmationCompletedUtc);
}
