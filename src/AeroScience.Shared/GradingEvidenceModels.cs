using System.Text.Json.Serialization;

namespace AeroScience.Shared;

public enum RubricCategory { Unknown, MissionDesign, SensorOperatingCompliance, FlightPlanExecution, AcquisitionDiscipline }
public enum MissionGradeStatus { NotGraded, Provisional, Final, Revised, InsufficientEvidence, Unsupported }
public enum MissionGradeLetter { A, B, C, D, F }
public sealed record RubricCategoryWeight(RubricCategory Category, double Weight);
public sealed record RubricCriterion(string Id, RubricCategory Category, double Weight,
    bool Required, string[] RequiredEvidenceClasses, string Method);
public sealed record GradeThresholds(double A, double B, double C, double D);
public sealed record GradingRubric([property: JsonRequired] int Version, string Id, int Revision,
    MissionTemplateKind Template, RubricCategoryWeight[] Categories, RubricCriterion[] Criteria,
    GradeThresholds Thresholds, double MinimumEvidenceWeightFraction,
    bool RequireSingleLetterScoreRange, string[] Limitations) : IVersionedEvidence;
public sealed record MissionObligation(Guid Id, string CriterionId, double Weight,
    string? PlannedSessionKey = null, Guid? WaypointId = null, Guid? SurveyAreaId = null,
    DateTimeOffset? WindowStartUtc = null, DateTimeOffset? WindowEndUtc = null,
    double? TargetAmount = null, string? Unit = null, string? RepeatRuleId = null);
public sealed record MissionAssessmentContext([property: JsonRequired] int Version, Guid MissionId,
    DateTimeOffset FrozenUtc, ScienceMode Mode, string PlanSha256, GradingRubric Rubric,
    MissionObligation[] Obligations, EffectiveInstrumentConfiguration[] Configurations) : IVersionedEvidence;

// Unknown values stay nullable. These contracts do not calculate scores or science measurements.
public sealed record ScienceMetric(string Id, AssessmentStatus Status, double? Numerator,
    double? Denominator, string Unit, string Method, long Missing, long Unassessed,
    EvidenceOrigin Origin, string[] Limitations);
public sealed record ScienceYield([property: JsonRequired] int Version, Guid MissionId,
    Guid? SessionId, string InstrumentId, DateTimeOffset AsOfUtc, ScienceMetric[] Metrics,
    string[] Limitations) : IVersionedEvidence;
public sealed record GradingCriterionResult(Guid ObligationId, string CriterionId,
    AssessmentStatus Status, double? Score, string[] Reasons, string[] Recommendations);
public sealed record SessionPerformanceBreakdown(Guid SessionId, GradingCriterionResult[] Criteria,
    ScienceMetric[] Metrics, string[] Limitations);
public sealed record MissionGrade([property: JsonRequired] int Version, Guid MissionId,
    DateTimeOffset AsOfUtc, MissionGradeStatus Status = MissionGradeStatus.NotGraded,
    string? RubricSha256 = null, string? EvidenceSha256 = null, double? ScoreLower = null,
    double? ScoreUpper = null, double? EvidenceWeightFraction = null, MissionGradeLetter? Letter = null,
    GradingCriterionResult[]? Criteria = null, SessionPerformanceBreakdown[]? Sessions = null,
    string[]? Limitations = null) : IVersionedEvidence;
