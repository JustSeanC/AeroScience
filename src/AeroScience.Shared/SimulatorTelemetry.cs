namespace AeroScience.Shared;

// Live host availability only. Never assigns identity to historical evidence.
public sealed record SimulatorAvailability(int Version, string Id, bool ProductionRecordingReady,
    bool ScienceIntegrationReady, string ReasonKey);

// Additive transport context, not persisted identity or an installed-simulator attestation.
public sealed record SimulatorOrigin(int Version, string SimulatorId, string? ReportedVersion,
    string AdapterId, string AdapterVersion, string? ApiVersion, Guid ConnectionEpoch,
    string MappingRevision, long? ReportedBuild = null, string? AircraftType = null,
    string? AircraftRegistration = null);

public sealed record SimulatorClock(double RunningSeconds, double FlightSeconds, bool Paused,
    bool Replay, double? ZuluSeconds = null, int? LocalDay = null, int? ZuluDay = null,
    int? Year = null, int? RequestedRate = null, double? ActualRate = null,
    double? DilationAwareRate = null, double? DistanceRate = null);

public sealed record SimulatorFieldTiming(string Dataref, DateTimeOffset LastChangedUtc,
    DateTimeOffset LastReceivedUtc, DateTimeOffset? LastConfirmedUtc, bool Available);

public sealed record SimulatorCapabilities(string[] AvailableChannels, string[] UnavailableChannels,
    string[] Limitations, bool ProductionRecordingReady = false, bool ScienceIntegrationReady = false);

public sealed record SimulatorSampleContext(int Version, Guid ConnectionEpoch,
    DateTimeOffset HostReceivedUtc, DateTimeOffset ConfirmationStartedUtc,
    DateTimeOffset ConfirmationCompletedUtc, double SnapshotSpanMilliseconds,
    SimulatorClock? Clock, SimulatorFieldTiming[] Fields, SimulatorCapabilities Capabilities,
    string Continuity = "New epoch; flight continuity unverified", double? CoreSnapshotSpanMilliseconds = null);
