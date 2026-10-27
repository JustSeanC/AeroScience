using System.Text.Json;
using System.Text.Json.Serialization;
namespace AeroScience.Shared;

// Optional versioned additions; historical one-point and scientific JSON is unchanged.
public sealed record FlightTestProcedureDefinition(int Version,bool Ordered,FlightTestProcedurePoint[] Points)
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? Extensions {get;init;}
}
public sealed record FlightTestProcedurePoint(Guid Id,FlightRegimeDefinition Conditions,int Repetitions=1)
{
    [JsonExtensionData] public Dictionary<string,JsonElement>? Extensions {get;init;}
}
public sealed record FlightTestConditionEvidence(string Signal,ResearchAttemptOutcome Outcome,double LongestHoldSeconds,string Explanation);
public sealed record FlightTestPointEvidence(Guid PointId,ResearchAttemptOutcome Outcome,double LongestHoldSeconds,
    DateTimeOffset? HoldStartUtc,DateTimeOffset? HoldEndUtc,ResearchEvidenceReference Evidence,
    FlightTestConditionEvidence[] Conditions,string Explanation,long SampleGaps=0,long UnsupportedReadings=0,long RecordedBreaks=0,string? Aircraft=null);
public sealed record FlightTestPointProgress(Guid PointId,int RequiredRepetitions,int CompletedRepetitions,
    ResearchAttemptOutcome Outcome,double LongestHoldSeconds,string Explanation);
public sealed record FlightTestProcedureSummary(int Version,bool Ordered,ResearchAttemptOutcome Outcome,
    FlightTestPointProgress[] Points,long AttemptCount,Guid? NextPointId,string Explanation);
public sealed record FlightTestProcedureReview(bool MatchesSavedEvidence,FlightTestProcedureSummary Summary,
    FlightTestMarkedReview[] Preview,bool MoreAttempts,string Message);
public sealed record FlightTestMarkedReview(RecordedResearchAttempt Attempt,bool Qualified,string Qualification);
