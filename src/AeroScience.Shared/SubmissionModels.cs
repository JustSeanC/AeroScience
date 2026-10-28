using System.Text.Json.Serialization;
namespace AeroScience.Shared;

// Local persistence contains these references/consent only, never SubmissionEvidenceData.
public enum SubmissionPolicyMode { RequireMinimumEvidence, AllowIncompleteSubmission }
public enum SubmissionDeliveryState { Draft, ApprovedQueued, Sending, RetryAvailable, HeldEvidenceMissing, HeldAuthorization, ServerReceived, Cancelled }
public enum SubmissionEvidenceKind { Definition, Result, Telemetry, ScientificObservations, PilotDocumentation, CollectionSessions, MissionConfiguration }
public sealed record SubmissionPolicy(int Version,Guid OpportunityId,int Revision,SubmissionPolicyMode Mode,string MinimumRule,int MaximumPayloadBytes,DateTimeOffset PublishedUtc,string RetentionDisclosure);
public sealed record SubmissionPolicyCommand(int Version,Guid RequestId,int ExpectedRevision,SubmissionPolicyMode Mode);
public sealed record SubmissionCapabilities(Guid OrganizationId,Guid PilotId,SubmissionPolicy Policy,OrganizationDesktopPermission Permission);
public sealed record SubmissionCardBinding(Guid ClaimId,Guid OpportunityId,Guid CampaignId,int IssuedRevision,string IssuedSha256,
    Guid CardId,int CardRevision,string DefinitionSha256,DateTimeOffset StartUtc,DateTimeOffset EndUtc,long KnownRecordedAttempts);
public sealed record SubmissionEvidenceReference(string Key,SubmissionEvidenceKind Kind,Guid CardId,Guid ResultId,Guid FlightId,
    string OriginalId,string SourceSha256,string ContentSha256,int ContentBytes,long Records,DateTimeOffset StartUtc,DateTimeOffset EndUtc,string Description,string Privacy);
public sealed record SubmissionManifest(int Version,Guid Id,string Name,string Origin,Guid OrganizationId,string OrganizationName,
    Guid CampaignId,string CampaignName,Guid PilotId,Guid FlightId,Guid[] ResultIds,SubmissionCardBinding[] Cards,SubmissionPolicy[] Policies,
    SubmissionEvidenceReference[] Items,string HistoryDisclosure,string PrivacyDisclosure);
public sealed record SubmissionConsent(int Version,Guid GrantId,string ManifestSha256,string PayloadSha256,int PayloadBytes,
    DateTimeOffset ApprovedUtc,bool ExplicitApproval,string Scope="submitEvidence");
public sealed record SubmissionReceipt(int Version,Guid Id,Guid SubmissionId,Guid OrganizationId,Guid PilotId,Guid CampaignId,
    string ManifestSha256,string PayloadSha256,int PayloadBytes,DateTimeOffset ReceivedUtc,string Status,string RetentionDisclosure);
public sealed record SubmissionCancellation(Guid SubmissionId,string ManifestSha256);
public sealed record SubmissionRemoteState(string Status,SubmissionReceipt? Receipt=null);
public sealed record SubmissionAssessment(bool MinimumMet,bool RuleSupported,string[] Warnings);
public sealed record SubmissionConfiguration(Guid FlightId,string OriginalPlanSha256,string Template,InstrumentConfiguration[] Instruments);
public sealed record SubmissionTelemetrySample(long Sequence,TelemetryFrame Frame);
public sealed record SubmissionTelemetry(SubmissionTelemetrySample[] Samples,DateTimeOffset[] Breaks,RecordedResearchAttempt[] Attempts,MissionStatus FlightStatus);
public sealed record SubmissionEvidenceData(
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PersonalResearchCard? Definition=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PersonalResearchAttempt? Result=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SubmissionTelemetry? Telemetry=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ScienceObservation[]? Observations=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] PilotObservationEntry[]? Documentation=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ScienceCollectionSession[]? Sessions=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] SubmissionConfiguration? Configuration=null);
public sealed record SubmissionEvidenceItem(SubmissionEvidenceReference Reference,SubmissionEvidenceData Data);
public sealed record SubmissionDocument(SubmissionManifest Manifest,SubmissionEvidenceItem[] Evidence);
public sealed record SubmissionUpload(SubmissionDocument Document,SubmissionConsent Consent);
public sealed record SubmissionPreview(SubmissionManifest Manifest,string ManifestSha256,string PayloadSha256,int PayloadBytes,
    SubmissionAssessment Assessment,string[] Unsupported,bool PolicyVerified,bool CanApprove);
public sealed record SubmissionDraft(Guid Id,int Revision,SubmissionDeliveryState State,SubmissionManifest Manifest,
    Guid? ConnectionId,SubmissionConsent? Consent,SubmissionReceipt? Receipt,DateTimeOffset UpdatedUtc,string Message,int DeliveryAttempts=0,DateTimeOffset? NextAttemptUtc=null);
public sealed record SubmissionBuilderRequest(Guid FlightId,Guid? CardId=null);
public sealed record SubmissionDraftRequest(int Version,Guid Id,int ExpectedRevision,Guid FlightId,Guid[] ResultIds,string[]? SelectedKeys,
    string Name,Guid? ConnectionId=null);
public sealed record SubmissionApproval(int Version,int ExpectedRevision,string ManifestSha256,string PayloadSha256,int PayloadBytes,Guid ConnectionId,bool ExplicitApproval);
public sealed record SubmissionChange(int ExpectedRevision);
public sealed record SubmissionChoice(Guid ResultId,Guid CardId,string CardName,Guid ClaimId,Guid OrganizationId,string OrganizationName,
    Guid CampaignId,string CampaignName,string Origin,DateTimeOffset StartUtc,DateTimeOffset EndUtc,ResearchAttemptOutcome Outcome,string? Unsupported=null);
public sealed record SubmissionInventory(int Version,SubmissionChoice[] Choices,SubmissionDraft[] Drafts,int? NextOffset);
public sealed record SubmissionServerSummary(Guid Id,string Name,Guid PilotId,Guid CampaignId,DateTimeOffset ReceivedUtc,string Status,string ManifestSha256,int PayloadBytes);

public sealed record SubmissionStatusSummary(long ReceivedPackages,long PendingPackages,long Drafts,long HeldPackages,long CancelledPackages);
