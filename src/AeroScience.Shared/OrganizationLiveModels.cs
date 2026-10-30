namespace AeroScience.Shared;

// Current-state disclosure only. These contracts cannot contain notes, raw samples or tracks.
public static class OrganizationLiveProtocol
{
    public const int Version=1, ConsentVersion=1, MaximumFlights=64, MaximumCards=8, MaximumInstruments=8;
    public const int UpdateSeconds=5, StaleSeconds=15, PositionLifetimeSeconds=30, IdleSeconds=120, MaximumBytes=16384;
}
public enum LiveSourceState { Unavailable, Connected, Paused, Stale }
public enum LiveResearchProgress { Activated, AttemptInProgress, AttemptRecorded, LocallyEvaluated, MissingEvidence, Attached }
public sealed record LiveCardStatus(Guid ClaimId,Guid CardId,int CardRevision,Guid? PointId,Guid? AttemptId,LiveResearchProgress Progress,bool Active=false,ResearchAttemptOutcome? Outcome=null);
public sealed record LiveInstrumentStatus(string InstrumentId,SensorOperatingState State,SensorScientificDataState SourceState);
public sealed record LiveScienceStatus(Guid? SessionId,CollectionPhase? Phase,LiveInstrumentStatus[] Instruments,bool Truncated=false);
public sealed record LiveFlightUpdate(int Version,Guid SessionId,int ConsentRevision,long Sequence,DateTimeOffset? SourceUtc,
    TelemetrySource? Source,LiveSourceState State,string? Aircraft,double? Latitude,double? Longitude,double? HeadingDegrees,
    double? AltitudeMslFeet,double? IndicatedAirspeedKnots,LiveCardStatus[] Cards,LiveScienceStatus? Science,bool CardsTruncated=false);
public sealed record LiveSharingStart(int Version,Guid RequestId,Guid OrganizationId,Guid FlightId,DateTimeOffset FlightStartedUtc,
    int ExpectedPolicyRevision,string DisplayLabel,Guid[] ClaimIds,bool ConfirmAllActiveMembers,int ConsentVersion=1);
public sealed record LiveSharingSession(Guid Id,Guid OrganizationId,Guid PilotId,Guid FlightId,int ConsentRevision,
    DateTimeOffset StartedUtc,DateTimeOffset ExpiresUtc,int PolicyRevision,int UpdateSeconds,string Status);
public sealed record LiveSharingStop(int Version,Guid RequestId,Guid SessionId,int ConsentRevision,string Reason="pilotStopped");
public sealed record LiveUpdateReceipt(Guid SessionId,long Sequence,bool Applied,DateTimeOffset ReceivedUtc,int RetryAfterSeconds=0);
public sealed record LiveCardView(Guid ClaimId,Guid OpportunityId,int IssuedRevision,string Name,Guid CardId,int CardRevision,
    Guid? PointId,Guid? AttemptId,LiveResearchProgress Progress,string Meaning,Guid? CampaignId=null,string? CampaignName=null,string? PointName=null,bool Active=false,ResearchAttemptOutcome? Outcome=null);
public sealed record LiveFlightView(Guid SessionId,Guid FlightId,string DisplayLabel,DateTimeOffset FlightStartedUtc,
    DateTimeOffset ConsentExpiresUtc,DateTimeOffset? ReceivedUtc,bool Stale,LiveFlightUpdate? Current,LiveCardView[] Cards);
public sealed record OrganizationLivePolicy(int Version,int Revision,bool Enabled,Guid[] RestrictedCampaigns);
public sealed record LivePolicyCommand(int Version,Guid RequestId,int ExpectedRevision,bool Enabled,Guid[] RestrictedCampaigns);
public sealed record LiveDashboard(int Version,DateTimeOffset CheckedUtc,OrganizationLivePolicy Policy,LiveFlightView[] Flights,
    int MaximumFlights,int StaleSeconds,int PositionLifetimeSeconds,string Notice);
public sealed record LocalLiveConsent(Guid ConnectionId,Guid FlightId,int ExpectedPolicyRevision,string DisplayLabel,bool ConfirmAllActiveMembers);
public sealed record LocalLiveStatus(Guid ConnectionId,Guid FlightId,string OrganizationName,string DisplayLabel,string Status,
    Guid? SessionId,DateTimeOffset? LastSentUtc,string Message);

// Durable event metadata is separate from ephemeral aircraft positions. No arbitrary event publisher.
public enum OrganizationEventType { LiveSharingStarted, LiveSharingStopped, AttemptRecorded, PilotMarkedCompletion,
    LocalEvaluationReported, EvidenceReceived, RequirementApproved, RequirementRejected, MoreEvidenceRequested,
    DocumentedExceptionAccepted, DocumentedExceptionWithdrawn, ApprovedCardResult, ApprovedResultSuperseded }
public enum OrganizationEventVisibility { ActorOnly, AuthorizedReviewers, ActiveMembers }
public sealed record OrganizationEventView(Guid Id,int Version,Guid OrganizationId,OrganizationEventType Type,
    string Authority,Guid SourceId,int? SourceRevision,Guid ActorId,DateTimeOffset TimestampUtc,
    OrganizationEventVisibility Visibility,string Meaning,Guid? CardId=null,string? RequirementKey=null);
public sealed record OrganizationEventPage(int Version,OrganizationEventView[] Items,long? NextAfter);
