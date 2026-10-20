using System.Text.Json.Serialization;

namespace AeroScience.Shared;

// Contract versions are independent of SQLite schemas and the native toolbar protocol.
public interface IVersionedEvidence { int Version { get; } }
public enum CollectionPhase { Unknown, Armed, Collecting, Paused, BetweenSessions, Stopped }
public enum CollectionAuthority { Unknown, Manual, Automatic }
public enum CollectionAction { Unknown, StartData, PauseData, ResumeData, EndData, RearmAutomation }
public enum CollectionTriggerKind { Unknown, Waypoint, DirectionalGate, AreaEntry, AreaExit }
public enum InitialCollectionPolicy { Unknown, Manual, Armed }
public enum InitialInsidePolicy { Unknown, RequireWitnessedEntry, StartWhenInitiallyInside }
public enum CollectionSessionStatus { Unknown, Collecting, Paused, Ended, Interrupted }
public enum CollectionEndReason { Unknown, Manual, Trigger, FlightEnded, Interrupted }
public enum CollectionEventOrigin { Unknown, Manual, RouteTrigger, Boundary, Engine }
public enum CollectionEventOutcome { Unknown, Applied, Suppressed, NoOp, Conflict }
public enum CollectionHoldKind { Unknown, Manual, Route, Boundary, Continuity }
public enum EvidenceOrigin { Unknown, SimulatorReported, ExternalDataset, Derived, ModeledGeometry, SyntheticFixture }

public sealed record EvidenceReference(string Id, int Revision, string Sha256);
public sealed record EvidenceLink(string Id, string Sha256, DateTimeOffset ObservedUtc,
    EvidenceOrigin Origin, Guid? SourceId = null, DateTimeOffset? SourceObservationUtc = null);

public sealed record CollectionTrigger(Guid Id, Guid OccurrenceId, CollectionAction Action,
    CollectionTriggerKind Kind, Guid TargetId, int Pass, double ToleranceMetres,
    double? DirectionTrueDegrees = null, string? PlannedSessionKey = null,
    InitialInsidePolicy InitialInside = InitialInsidePolicy.RequireWitnessedEntry);
public sealed record BoundaryCollectionPolicy(Guid[] SurveyAreaIds, bool PauseOnExit, bool ResumeOnEntry,
    double HysteresisMetres, double DwellSeconds);
public sealed record CollectionContinuityPolicy(double MaximumGapSeconds, double MaximumPositionAgeSeconds);
// Identity binding for an explicitly saved collection plan; no IDs are backfilled into legacy geometry.
public sealed record CollectionSurveyArea(Guid Id, int PlanAreaIndex, string GeometrySha256);
public sealed record CollectionHold(CollectionHoldKind Kind, string Reason, Guid? SessionId = null, Guid? CauseId = null);
public sealed record CollectionPlan([property: JsonRequired] int Version, Guid MissionPlanId,
    InitialCollectionPolicy InitialPolicy, CollectionTrigger[] Triggers,
    CollectionContinuityPolicy Continuity, BoundaryCollectionPolicy? Boundary = null,
    CollectionSurveyArea[]? SurveyAreas = null) : IVersionedEvidence;

public sealed record CollectionState([property: JsonRequired] int Version, Guid MissionId, long Revision,
    CollectionPhase Phase, CollectionAuthority Authority, Guid? CurrentSessionId = null,
    Guid? LastSessionId = null, bool ManualAutomationHold = false, bool ContinuityHold = false,
    Guid[]? FiredOccurrenceIds = null, Guid[]? SuppressedOccurrenceIds = null,
    CollectionHold[]? Holds = null) : IVersionedEvidence;

// Null ExpectedSessionId explicitly means "no open session", not "whichever session is current".
// EndData closes the expected current session; End Flight remains the existing separate command.
public sealed record CollectionControlRequest([property: JsonRequired] int Version, Guid MissionId,
    Guid RequestId, [property: JsonRequired] long ExpectedRevision, CollectionAction Action,
    [property: JsonRequired] Guid? ExpectedSessionId) : IVersionedEvidence;

public sealed record ScienceCollectionSession([property: JsonRequired] int Version, Guid Id,
    Guid MissionId, long Sequence, DateTimeOffset StartedUtc, CollectionSessionStatus Status,
    EffectiveInstrumentConfiguration[] Configurations, EvidenceLink[] Evidence,
    string? PlannedSessionKey = null, DateTimeOffset? EndedUtc = null,
    CollectionEndReason? EndReason = null) : IVersionedEvidence;

public sealed record CollectionEvent([property: JsonRequired] int Version, Guid MissionId,
    long Sequence, DateTimeOffset RecordedUtc, DateTimeOffset InputUtc, Guid? SessionId,
    CollectionAction Action, CollectionEventOrigin Origin, CollectionEventOutcome Outcome,
    CollectionPhase From, CollectionPhase To, CollectionAuthority Authority, string Reason,
    EvidenceLink[] Evidence, Guid? RequestId = null, Guid? OccurrenceId = null,
    Guid? ExpectedSessionId = null, long? SessionSequence = null,
    string? PlannedSessionKey = null, EvidenceReference? CollectionPolicy = null,
    Guid[]? ConfigurationIds = null) : IVersionedEvidence;
