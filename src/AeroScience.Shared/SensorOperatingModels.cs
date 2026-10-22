namespace AeroScience.Shared;

// Presentation only: these states never replace recorded P04 rules or affect acquisition/grades.
public enum SensorOperatingState { WithinCheckedLimits, Caution, CannotAssess, NotCollecting, Disabled }
public enum SensorScientificDataState { Ready, PartiallyAvailable, Unavailable, Pending, Unknown }
public sealed record SensorScientificDataStatus(SensorScientificDataState Status, string Explanation, string? AcquisitionMessage = null);
public sealed record SensorOperatingCard(string InstrumentId, string Name, bool Enabled, string ProfileName,
    int? ProfileVersion, string CollectionState, Guid? SessionId, EffectiveInstrumentConfiguration? Configuration,
    SensorOperatingState OperatingStatus, string PrimaryIssue, string? Guidance, string[] AdditionalAdvice, string[] Uncertainty,
    int CheckedRules, int UnassessedRules, SensorScientificDataStatus ScientificData,
    DateTimeOffset? AssessedUtc, DateTimeOffset? EvidenceUtc, bool EvidenceFresh, InstrumentOperatingReport? Report);
public sealed record SensorOperatingOverview(int Version, DateTimeOffset GeneratedUtc, Guid? MissionId,
    Guid? SessionId, CollectionPhase? Phase, SensorOperatingCard[] Instruments, string[] Limitations);
