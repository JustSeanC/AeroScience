namespace AeroScience.Shared;

// Definitions are local and organization-independent. New templates/rules require new versions.
public sealed record ResearchCardReference(Guid Id, int Revision);
public sealed record FlightRegimeDefinition(double AirspeedKnots, double AirspeedToleranceKnots,
    double AltitudeMslFeet, double AltitudeToleranceFeet, double HoldSeconds);
public sealed record PersonalResearchCard(int Version, Guid Id, int Revision, string Name,
    string TemplateId, FlightRegimeDefinition FlightRegime, DateTimeOffset CreatedUtc);
public sealed record SaveResearchCardRequest(string Name, FlightRegimeDefinition FlightRegime,
    Guid? Id = null, int? ExpectedRevision = null);
public enum ResearchAttemptOutcome { Pass, Fail, InsufficientEvidence }
public sealed record ResearchAttemptRequest(Guid RequestId, Guid FlightId,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record ResearchEvidenceReference(long FirstSequence, long LastSequence, long SampleCount,
    string Sha256, string PlanSha256, TelemetrySource? Source);
public sealed record PersonalResearchAttempt(int Version, Guid Id, Guid FlightId,
    PersonalResearchCard Card, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset EvaluatedUtc,
    string EvaluatorVersion, ResearchAttemptOutcome Outcome, double LongestSupportedHoldSeconds,
    DateTimeOffset? HoldStartUtc, DateTimeOffset? HoldEndUtc,
    ResearchEvidenceReference Evidence, string Explanation);
