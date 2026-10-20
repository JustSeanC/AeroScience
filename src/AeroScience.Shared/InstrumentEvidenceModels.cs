using System.Text.Json.Serialization;

namespace AeroScience.Shared;

public enum CalibrationInformationStatus { Unknown, Documented, UserDefined, Unavailable }
public enum AssessmentStatus { Unknown, NotAssessed, Supported, Caution, Invalid, Unavailable, Missing, Unsupported }
public enum ReadinessStatus { NotYetAssessed, Ready, Caution, Unavailable }

public sealed record CalibrationParameter(string Id, double Value, string Unit, string Basis);
public sealed record InstrumentSpecification([property: JsonRequired] int Version, string Id,
    int Revision, InstrumentDefinition Definition, string[] References, string[] Limitations) : IVersionedEvidence;
public sealed record CalibrationDefinition([property: JsonRequired] int Version, string Id,
    int Revision, string InstrumentId, CalibrationInformationStatus Status,
    CalibrationParameter[] Parameters, string[] References, string[] Limitations,
    string? Author = null, DateTimeOffset? DefinedUtc = null) : IVersionedEvidence;

// Complete settings are retained; references never substitute today's mutable catalog content.
public sealed record EffectiveInstrumentConfiguration(Guid Id, int Revision, DateTimeOffset EffectiveUtc,
    InstrumentConfiguration Settings, CalibrationDefinition? Calibration = null,
    EvidenceReference? OperatingPolicy = null, InstrumentSpecification? Specification = null);
public sealed record OperatingRuleAssessment(EvidenceReference Rule, string Parameter, string Unit,
    double? ObservedValue, AssessmentStatus Compliance, AssessmentStatus DerivationValidity,
    AssessmentStatus SourceQuality, string[] Reasons, string[] AffectedDerivations);
public sealed record OperatingAssessment([property: JsonRequired] int Version, Guid MissionId,
    Guid? SessionId, Guid ConfigurationId, DateTimeOffset AssessedUtc,
    OperatingRuleAssessment[] Rules, EvidenceLink[] Evidence) : IVersionedEvidence;
public sealed record ReadinessCheck(string Id, ReadinessStatus Status, string[] Reasons, string[] Actions);
public sealed record ReadinessAssessment([property: JsonRequired] int Version, string PlanSha256,
    DateTimeOffset AssessedUtc, ReadinessCheck[] Checks, string[] Limitations) : IVersionedEvidence;
