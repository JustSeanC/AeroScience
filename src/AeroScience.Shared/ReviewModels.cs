using System.Text.Json.Serialization;
namespace AeroScience.Shared;

public enum ResearchReviewState { AwaitingReview, MoreEvidenceRequested, UpdatedSubmission, Approved, Rejected }
public enum ResearchReviewAction { Open, Comment, RequestEvidence, PilotResponse, Approve, Reject, Received, SupplementalReceived }
public enum ResearchReviewRequestState { Open, Responded, EvidenceReceived, Resolved }
public sealed record SubmissionReviewLink(int Version,Guid RootReceiptId,string RootPayloadSha256,Guid RequestEventId);
public sealed record ReviewEvidenceReference(Guid ReceiptId,string Key);
public sealed record ReviewTarget(Guid CardId,string RequirementKey,Guid? AttemptId=null,string? EvidenceKey=null,Guid? ReceiptId=null);
public sealed record ResearchReviewCommand(int Version,Guid RequestId,int ExpectedRevision,ResearchReviewAction Action,
    ReviewTarget? Target=null,string Note="",ReviewEvidenceReference[]? Evidence=null,Guid[]? ResolveRequests=null,bool ConfirmApproval=false,int PolicyVersion=1,Guid? RespondTo=null,bool ExplicitSharing=false);
public sealed record CampaignReviewPermission(int Version,Guid RequestId,int ExpectedRevision,Guid ReviewerId,bool Allowed);
public sealed record CampaignReviewPermissionView(Guid CampaignId,Guid ReviewerId,string Name,bool Allowed,int Revision);
public sealed record ReviewRequirementDefinition(Guid CardId,string Key,string CardName,string Name,string Objective,string Criteria,int IssuedRevision,string DefinitionSha256,Guid? PointId=null,bool ApprovalSupported=true);
public sealed record ReviewFinding(ResearchReviewState State,Guid EventId,long Sequence,Guid ReviewerId,string ReviewerName,DateTimeOffset TimestampUtc,string Note,ReviewEvidenceReference[] Evidence,int PolicyVersion);
public sealed record ReviewRequirementView(ReviewRequirementDefinition Requirement,ResearchReviewState State,ReviewFinding? Finding);
public sealed record ResearchReviewEvent(int Version,Guid Id,Guid RootReceiptId,int Sequence,ResearchReviewAction Action,Guid ActorId,string ActorName,string ActorRole,DateTimeOffset TimestampUtc,
    ReviewTarget? Target,string Note,ReviewEvidenceReference[] Evidence,Guid[] ResolvedRequests,Guid? RespondTo,Guid ReceiptId,string PayloadSha256,int PolicyVersion,string PreviousSha256,string Sha256);
public sealed record ResearchReviewRequest(Guid EventId,ReviewTarget Target,string Note,string Author,DateTimeOffset TimestampUtc,ResearchReviewRequestState State);
public sealed record ResearchReviewSummary(int Version,Guid RootReceiptId,Guid SubmissionId,Guid OrganizationId,string OrganizationName,Guid PilotId,string PilotName,
    Guid ProgramId,string ProgramName,Guid CampaignId,string CampaignName,string Title,string CardNames,string RootPayloadSha256,int Revision,ResearchReviewState State,DateTimeOffset ReceivedUtc,DateTimeOffset UpdatedUtc,int OpenRequests,int PackageCount,string Completeness);
public sealed record ResearchReviewSnapshot(ResearchReviewSummary Summary,ReviewRequirementView[] Requirements,OrganizationPage<ResearchReviewRequest> Requests);
public sealed record ResearchReviewPackage(Guid ReceiptId,Guid SubmissionId,string Name,string PayloadSha256,DateTimeOffset ReceivedUtc,Guid FlightId,bool Supplemental,Guid? RequestEventId);
public sealed record ResearchReviewHistory(int Version,ResearchReviewEvent[] Items,int? NextAfter,string HeadSha256);
public sealed record ResearchReviewAssessment(bool Supported,bool CanApprove,string[] Blockers,string[] Limitations,ResearchAttemptOutcome[] OriginalOutcomes,Guid[] SubmissionIds,FlightTestProcedureSummary? Procedure=null,AtmosphericResearchSummary? Atmospheric=null);
public sealed record ResearchReviewSelection(Guid CardId,string RequirementKey,Guid[] ReceiptIds);
public sealed record ResearchReviewEvidencePage(ResearchReviewAssessment Assessment,ReceivedReviewEvidenceItem[] Items,int? NextOffset);
public sealed record ReceivedReviewEvidenceItem(Guid ReceiptId,SubmissionEvidenceItem Item,int TotalRecords,int? NextOffset);
public sealed record ResearchReviewItem(SubmissionEvidenceReference Reference,SubmissionEvidenceData Data,int Offset,int TotalRecords,int? NextOffset);
public sealed record ResearchReviewCommandResult(ResearchReviewEvent Event,ResearchReviewSnapshot Review);
public sealed record LocalReviewCheck(Guid ConnectionId,Guid RootReceiptId,int RequestOffset=0);
public sealed record LocalReviewResponse(Guid ConnectionId,Guid RootReceiptId,ResearchReviewCommand Command);
public sealed record CachedResearchReview(string Origin,Guid OrganizationId,Guid PilotId,Guid RootReceiptId,DateTimeOffset CheckedUtc,ResearchReviewSnapshot Snapshot);
public sealed record LocalReviewStatus(CachedResearchReview? Retained,bool VerifiedNow,string Message);
