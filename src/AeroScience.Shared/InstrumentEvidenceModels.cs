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
    string? Author = null, DateTimeOffset? DefinedUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CalibrationAdjustment? Adjustment = null) : IVersionedEvidence;

// This opt-in arithmetic is a user assertion, never a physical sensor calibration.
// gain is dimensionless; offset and input bounds use the original channel's unit.
public sealed record CalibrationAdjustment(string Method, string MeasurementId, string Unit,
    double? MinimumInput = null, double? MaximumInput = null,
    DateTimeOffset? ValidFromUtc = null, DateTimeOffset? ValidUntilUtc = null);
public sealed record CalibrationDerivation(CalibrationDefinition Definition, string DefinitionSha256,
    string OriginalMeasurementId, double OriginalValue, string OriginalUnit, string Formula);
public sealed record CalibrationChannel(string MeasurementId, string Unit, bool SupportsAdjustment, string Limitation);
public sealed record InstrumentCalibrationInfo(InstrumentSpecification Specification, string SpecificationSha256,
    CalibrationDefinition Reference, string ReferenceSha256, CalibrationChannel[] Channels);
public sealed record CalibrationDefinitionRevision(CalibrationDefinition Definition, string Sha256);

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
public sealed record InstrumentOperatingReport(ScienceMode Mode, EffectiveInstrumentConfiguration Configuration,
    OperatingAssessment Assessment, TelemetryUpdate Input, string Policy = "hybrid-realistic-1",
    string Limitation = "Operating compliance, modeled geometry and source QA are separate; this is not measurement accuracy certification.");
public sealed record ReadinessCheck(string Id, ReadinessStatus Status, string[] Reasons, string[] Actions, string Category = "General", string[]? Inputs = null);
public sealed record ReadinessAssessment([property: JsonRequired] int Version, string PlanSha256,
    DateTimeOffset AssessedUtc, ReadinessCheck[] Checks, string[] Limitations, string? ContextSha256 = null, DateTimeOffset? ExpiresUtc = null) : IVersionedEvidence;
