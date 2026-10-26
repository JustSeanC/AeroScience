using System.Text.Json.Serialization;
namespace AeroScience.Shared;

// Definitions are local and organization-independent. New templates/rules require new versions.
public sealed record ResearchCardReference(Guid Id, int Revision);
public sealed record FlightRegimeDefinition(double AirspeedKnots, double AirspeedToleranceKnots,
    double AltitudeMslFeet, double AltitudeToleranceFeet, double HoldSeconds);
public sealed record PersonalResearchCard(int Version, Guid Id, int Revision, string Name,
    string TemplateId, [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FlightRegimeDefinition? FlightRegime, DateTimeOffset CreatedUtc,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AtmosphericResearchDefinition? AtmosphericProfile=null);
public sealed record SaveResearchCardRequest(string Name, FlightRegimeDefinition? FlightRegime,
    Guid? Id = null, int? ExpectedRevision = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AtmosphericResearchDefinition? AtmosphericProfile=null);
public enum ResearchAttemptOutcome { Pass, Fail, InsufficientEvidence, CollectionComplete, PartialCollection }
public sealed record ResearchAttemptRequest(Guid RequestId, Guid FlightId,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? RecordedAttemptId = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? CardId = null);
public sealed record ResearchEvidenceReference(long FirstSequence, long LastSequence, long SampleCount,
    string Sha256, string PlanSha256, TelemetrySource? Source);
public sealed record PersonalResearchAttempt(int Version, Guid Id, Guid FlightId,
    PersonalResearchCard Card, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset EvaluatedUtc,
    string EvaluatorVersion, ResearchAttemptOutcome Outcome, double LongestSupportedHoldSeconds,
    DateTimeOffset? HoldStartUtc, DateTimeOffset? HoldEndUtc,
    ResearchEvidenceReference Evidence, string Explanation,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? RecordedAttemptId = null,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AtmosphericResearchSummary? AtmosphericProfile=null);

public enum ResearchCardAction { Unknown, Activate, Deactivate, BeginAttempt, EndAttempt }
public enum ResearchAttemptStatus { Recording, Recorded, Interrupted }
public sealed record ResearchCardControl([property: JsonRequired] int Version, Guid FlightId, Guid RequestId,
    [property: JsonRequired] long ExpectedRevision, ResearchCardAction Action, Guid CardId,
    [property: JsonRequired] Guid? ExpectedAttemptId);
public sealed record RecordedResearchAttempt(Guid Id, Guid FlightId, Guid CardId, int CardRevision,
    DateTimeOffset StartUtc, DateTimeOffset? EndUtc, ResearchAttemptStatus Status,
    long BeginEventId, long? EndEventId, string? EndReason);
public sealed record ResearchFlightCard(PersonalResearchCard Card, bool Active,
    RecordedResearchAttempt? OpenAttempt, string? ActivationBlocked);
public sealed record ResearchFlightState(int Version, Guid FlightId, long Revision, bool Recording,
    ResearchFlightCard[] Cards, DateTimeOffset GeneratedUtc, bool CanBeginAttempt, string Message,
    int TotalCards, int? NextOffset = null,int PageSize=4);
public sealed record ResearchCardReceipt(Guid RequestId, Guid FlightId, long Revision, long EventId,
    Guid CardId, Guid? AttemptId, DateTimeOffset TimestampUtc, string Message);
public sealed record ResearchCardResult(ResearchCardReceipt Receipt, ResearchFlightState State);

public enum ResearchEntryIntent { Unknown, FlyNow, Customize }
public sealed record ResearchStarter(string Id,int Version,string Name,string Description,string[] Measurements,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FlightRegimeDefinition? Example,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] AtmosphericResearchDefinition? AtmosphericExample=null);
public sealed record ResearchStarterRequest(int Version,Guid RequestId,string StarterId,ResearchEntryIntent Intent,ResearchCardReference? Card=null,int StarterVersion=1);
public sealed record ResearchStarterOrigin(int Version,Guid RequestId,string RequestSha256,string StarterId,ResearchEntryIntent Intent,ResearchCardReference? OriginalCard);
public sealed record PreparedResearchStarter(MissionPlan Plan,PersonalResearchCard Card,bool Reused);

// Collection criteria, distinct from aircraft performance or a proven atmospheric hypothesis.
public sealed record AtmosphericResearchDefinition(int Version=1,string Question="How does air temperature change as my aircraft climbs or descends?",
    double BandWidthFeet=500,int RequiredBands=3,int MinimumReadingsPerBand=3,double MaximumGapSeconds=5);
public sealed record AtmosphericResearchBand(int Index,double LowerFeetMsl,double UpperFeetMsl,long TemperatureReadings,
    long LongestContinuousReadings,bool Covered,double? MinimumTemperatureCelsius,double? MaximumTemperatureCelsius);
public sealed record AtmosphericQuantitySummary(string InstrumentId,string MeasurementId,string Unit,long ValidReadings,
    long ExcludedReadings,double? Minimum,double? Maximum);
public sealed record AtmosphericResearchSummary(int Version,Guid LibraryId,long OriginalObservations,long ValidTemperatureReadings,
    long ExcludedReadings,double? MinimumAltitudeFeetMsl,double? MaximumAltitudeFeetMsl,long MajorGaps,
    AtmosphericResearchBand[] Bands,AtmosphericQuantitySummary[] Quantities,bool BandsTruncated,string[] Limitations);
public sealed record AtmosphericResearchReview(bool MatchesSavedEvidence,AtmosphericResearchSummary Retained,
    ScienceObservation[] Preview,bool Downsampled,string Message);
