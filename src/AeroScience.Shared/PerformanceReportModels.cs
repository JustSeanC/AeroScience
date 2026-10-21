namespace AeroScience.Shared;

// Stored evaluator inputs contain audited facts and identities, not filesystem locations or provider secrets.
public sealed record CriterionEvidence(Guid ObligationId,double? ScoreLower,double? ScoreUpper,double EvidenceFraction,
    string[] Classes,string[] Reasons,string[] Recommendations,string[] References,ScienceMetric[]? Metrics=null);
public sealed record CategoryPerformance(RubricCategory Category,double OriginalWeight,double EffectiveWeight,
    bool Applicable,double ScoreLower,double ScoreUpper,double EvidenceFraction,string Method);
public sealed record SessionEvaluation(ScienceCollectionSession Session,ScienceYield[] Yield,
    GradingCriterionResult[] Criteria,CollectionEvent[] Events,string[] Limitations);
public sealed record MissionEvaluationInput(int Version,Guid MissionId,DateTimeOffset AsOfUtc,bool Completed,
    MissionAssessmentContext? Context,CriterionEvidence[] Evidence,ScienceYield[] Yield,SessionEvaluation[] Sessions,
    string[] References,string[] Limitations, ScientificSourceEvidence[]? Sources=null,DateTimeOffset? SourceCutoffUtc=null,TelemetrySource? FlightSource=null,bool Interrupted=false);
public sealed record MissionPerformanceReport(int Version,Guid MissionId,int Revision,DateTimeOffset CreatedUtc,
    string Reason,string? PreviousSha256,MissionEvaluationInput Input,MissionGrade Grade,
    CategoryPerformance[] Categories,double? SupportedScoreLower,double? SupportedScoreUpper,string Sha256);

public sealed record ScientificBandEvidence(string Id,string Unit,double Scale,double Offset,double? NoData,double? WavelengthNm,RasterGrid? Grid);
public sealed record ScientificSourceEvidence(Guid Id,string Provider,string Dataset,string Scene,DateTimeOffset? ObservedUtc,DateTimeOffset RetrievedUtc,string VerticalDatum,string ManifestSha256,ScientificBandEvidence[] Bands,string[] Limitations);
