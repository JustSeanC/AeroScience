namespace AeroScience.Shared;

// Administrative workflow only. These records never replace scientific outcomes or approvals.
public enum WorkflowExceptionKind { RequirementWaiver, ApprovedWithExceptions }
public enum PhaseAdvancement { ManagerRelease, AutomaticAfterApprovals }
public enum PhaseCommandAction { Release, Hold, Reopen }
public sealed record ProgramWorkflowPolicy(int Version=1,bool PhaseGatesEnabled=false,bool AdministrativeHold=false,
    bool AllowCampaignWaivers=false,bool AllowCampaignExceptions=false,bool CountAuthorizedPartialContributors=false);
public sealed record ProgramPolicyCommand(int Version,Guid RequestId,int ExpectedRevision,ProgramWorkflowPolicy Policy,string Reason);
public sealed record ProgramPolicyView(Guid Id,Guid ProgramId,int Revision,int RulesRevision,ProgramWorkflowPolicy Policy,
    Guid ActorId,string Actor,string ActorRole,DateTimeOffset TimestampUtc,string Reason);
public sealed record PhaseRequirement(Guid OpportunityId,int PublicationRevision,string PublicationSha256,string? RequirementKey=null,
    int RequiredPilots=1,bool AllowWaivers=false,bool AllowExceptions=false,bool CountAuthorizedPartialContributors=false);
public sealed record TestPhaseDefinition(int Version,string Name,string Description,int Sequence,PhaseAdvancement Advancement,
    Guid[] Prerequisites,PhaseRequirement[] Requirements,bool Enabled=true);
public sealed record PhaseSaveCommand(int Version,Guid RequestId,int ExpectedRevision,TestPhaseDefinition Definition,string Reason);
public sealed record PhaseStateCommand(int Version,Guid RequestId,int ExpectedRevision,PhaseCommandAction Action,string Reason,bool Confirm=false);
public sealed record WorkflowExceptionCommand(int Version,Guid RequestId,int ExpectedPolicyRevision,Guid OpportunityId,
    int PublicationRevision,string PublicationSha256,WorkflowExceptionKind Kind,string Reason,string Justification,string UnmetCriteria,
    string? RequirementKey=null,Guid? PhaseId=null,int? ExpectedPhaseDefinitionRevision=null,Guid? RootReceiptId=null,Guid? LocalCardId=null,
    ReviewEvidenceReference[]? Evidence=null,bool CountAsPartialContributor=false,bool Confirm=false,Guid? SupersedesId=null,int? ExpectedSupersededRevision=null,int? ExpectedReviewRevision=null,string? ExpectedReviewHeadSha256=null);
public sealed record ExceptionRevokeCommand(int Version,Guid RequestId,int ExpectedRevision,string Reason);
public sealed record WorkflowEvidenceReference(Guid RootReceiptId,Guid CardId,Guid ClaimId,Guid PilotId,int ReviewRevision,string ReviewHeadSha256,Guid? ExceptionId=null,int? ExceptionRevision=null);
public sealed record WorkflowExceptionSnapshot(int Version,Guid Id,Guid ProgramId,Guid CampaignId,int Revision,int RulesRevision,
    WorkflowExceptionCommand Definition,bool Active,Guid ActorId,string Actor,string ActorRole,DateTimeOffset TimestampUtc,
    string Action,Guid? SupersededBy,int? SourceReviewRevision,string? SourceReviewHeadSha256,Guid? PilotId,Guid? ClaimId,ApprovedOutcomeCount[] OriginalOutcomes,string SourceSignature,
    ResearchReviewState? SourceState,string EvidenceSha256,string ScopeSha256,string CardName,int? PhaseDefinitionRevision);
public sealed record WorkflowExceptionView(Guid Id,int Revision,Guid ProgramId,Guid CampaignId,Guid OpportunityId,int PublicationRevision,
    string PublicationSha256,string CardName,string? RequirementKey,Guid? PhaseId,int? PhaseDefinitionRevision,WorkflowExceptionKind Kind,
    bool Active,bool Effective,string Status,string Reason,string Justification,string UnmetCriteria,bool CountAsPartialContributor,
    string Contributor,bool IsOwnContribution,ApprovedOutcomeCount[] OriginalOutcomes,string Actor,string ActorRole,DateTimeOffset TimestampUtc,
    string Action,Guid? SupersededBy,WorkflowExceptionSnapshot? ManagerDetails=null,string RequirementName="Entire published card");
public sealed record WorkflowRuleProgress(PhaseRequirement Requirement,string CardName,Guid CampaignId,string CampaignName,
    long ApprovedScientificContributors,long AuthorizedPartialContributors,long DistinctOrganizationalContributors,long ActiveWaivers,
    long ActiveExceptions,bool Fulfilled,string Status,bool HistoricalPublication,long AwaitingReview,string RequirementName="Entire published card");
public sealed record WorkflowWaiverReference(Guid Id,int Revision,string SnapshotSha256);
public sealed record PhaseSupport(PhaseRequirement Requirement,WorkflowEvidenceReference[] Contributions,WorkflowWaiverReference[] Waivers);
public sealed record PhaseAuthorizationProof(int Version,int DefinitionRevision,int RulesRevision,DateTimeOffset CheckedUtc,
    PhaseSupport[] Support,Guid[] Prerequisites,string Sha256);
public sealed record TestPhaseSnapshot(Guid Id,Guid ProgramId,int Revision,int DefinitionRevision,TestPhaseDefinition Definition,
    bool Held,bool Released,int AuthorizedDefinitionRevision,int AuthorizedRulesRevision,Guid ActorId,string Actor,string ActorRole,
    DateTimeOffset TimestampUtc,string Action,string Reason,PhaseAuthorizationProof? Proof);
public sealed record TestPhaseView(Guid Id,int Revision,int DefinitionRevision,TestPhaseDefinition Definition,bool Held,
    bool Ready,bool Authorized,bool PrerequisitesSatisfied,string Status,WorkflowRuleProgress[] Requirements,string[] Gaps,
    string LastAction,string LastReason,string LastActor,DateTimeOffset LastDecisionUtc,string? AuthorizationSha256);
public sealed record ProgramWorkflowView(int Version,Guid OrganizationId,Guid ProgramId,string Name,DateTimeOffset CheckedUtc,
    bool CanManage,ProgramPolicyView Policy,Guid? CurrentPhaseId,TestPhaseView[] ActivePhases,string[] Limitations);
public sealed record WorkflowHistoryView(Guid Id,int Revision,string Kind,string Action,string Reason,string Actor,
    string ActorRole,DateTimeOffset TimestampUtc,string Sha256);
public sealed record CampaignWorkflowView(int Version,Guid ProgramId,Guid CampaignId,DateTimeOffset CheckedUtc,
    ProgramPolicyView Policy,OrganizationPage<WorkflowRuleProgress> Cards,string CountingRule);
public sealed record WorkflowSourceChoice(Guid RootReceiptId,Guid LocalCardId,string Title,string Contributor,
    ResearchReviewState State,int ReviewRevision,Guid ClaimId);
public sealed record PublishedWorkflowCard(OrganizationOpportunitySnapshot Card,ReviewRequirementDefinition[] Requirements);
public sealed record WorkflowReviewContext(Guid ProgramId,Guid CampaignId,Guid OpportunityId,int PublicationRevision,string PublicationSha256,
    Guid RootReceiptId,Guid LocalCardId,int ReviewRevision,string ReviewHeadSha256);
public sealed record WorkflowExceptionPreviewRequest(Guid OpportunityId,int PublicationRevision,string? RequirementKey,
    Guid RootReceiptId,Guid LocalCardId,int ExpectedReviewRevision,string ExpectedReviewHeadSha256,ReviewEvidenceReference[] Evidence);
public sealed record WorkflowExceptionEvidence(WorkflowReviewContext Context,ApprovedOutcomeCount[] OriginalOutcomes,bool HasOriginals,
    string EvidenceSha256,string SourceSignature);
