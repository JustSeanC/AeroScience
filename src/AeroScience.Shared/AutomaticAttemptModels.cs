namespace AeroScience.Shared;

// Optional additions: manual attempt JSON and historic frozen definitions retain their shape.
public sealed record AutomaticAttemptMetadata(int Version,Guid SessionId,string PolicyVersion,long FirstSequence,
    long? LastSequence=null,ResearchAttemptStatus? CompletionStatus=null);
public sealed record AutomaticDetectionStatus(int Version,Guid SessionId,Guid PointId,bool Enabled,string Status,
    Guid? LastAttemptId,ResearchAttemptOutcome? LastOutcome,string Message);
public sealed record AutomaticDetectionScan(Guid RequestId);

public sealed record ManualAttemptSource(long AfterSequence,long? ThroughSequence=null);
