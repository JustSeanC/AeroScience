namespace AeroScience.Shared;

// Optional protocol-v1 extension. Stable identities and numbers are never shortened.
public sealed record ToolbarScienceMetric(string InstrumentId,string Id,AssessmentStatus Status,
    double? Numerator,double? Denominator,string Unit,long Missing=0,long Unassessed=0);
public sealed record ToolbarIntelligence(int Version,DateTimeOffset GeneratedUtc,Guid? MissionId,
    long? Revision,long? ControlRevision,Guid? SessionId,long? SessionSequence,CollectionPhase Phase,
    CollectionAuthority Authority,bool ManualHold,bool ContinuityHold,CollectionAction[] Actions,
    MissionGradeStatus GradeStatus,double? ScoreLower,double? ScoreUpper,double? EvidenceFraction,
    DateTimeOffset? MetricsUtc,ToolbarScienceMetric[] Metrics,string[] Warnings,string TriggerStatus,
    bool DetailsOmitted=false);
public sealed record ToolbarControlReceipt(long Sequence,CollectionEventOutcome Outcome,Guid? SessionId,string Reason);
