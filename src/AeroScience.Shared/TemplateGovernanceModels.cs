namespace AeroScience.Shared;

// Legacy is the explicit migration default: administrator campaign publication,
// manager opportunity publication. New reusable-template publication requires an administrator.
public enum ResearchPublishingMode { Legacy, DirectManager, AdministratorApproval }
public enum TemplateProposalStatus { Submitted, ChangesRequested, AcceptedForDrafting, Rejected, Published }
public enum PublicationReviewStatus { Pending, Approved, Rejected }
public sealed record ResearchPublishingPolicy(int Version,int Revision,ResearchPublishingMode Mode);
public sealed record ResearchPublishingPolicyCommand(int Version,Guid RequestId,int ExpectedRevision,ResearchPublishingMode Mode);
public sealed record TemplateProposalCommand(int Version,Guid RequestId,int ExpectedRevision,Guid OrganizationId,string Contents,string Purpose,string Message,bool ConfirmDefinitionOnly,string? ProposedName=null);
public sealed record TemplateProposalDecision(int Version,Guid RequestId,int ExpectedRevision,TemplateProposalStatus Status,string Explanation);
public sealed record ResearchDraftCommand(int Version,Guid RequestId,int ExpectedRevision,string Contents,Guid? ProgramId=null,Guid? CampaignId=null);
public sealed record TemplateDocument(Guid Id,int Revision,PortableResearchTemplate Template,Guid AuthorId,string AuthorName,
    DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,TemplateProposalStatus Status,string Purpose="",string Message="",
    string Explanation="",Guid? DraftId=null,Guid? ProposalId=null,int? PublishedRevision=null,Guid? ProgramId=null,Guid? CampaignId=null,
    PublicationAuthorship? Publication=null,string? OriginalTemplateSha256=null);
public sealed record PublicationAuthorship(Guid AuthorId,Guid? ApproverId,Guid PublisherId,int PolicyRevision,ResearchPublishingMode Mode,int DraftRevision,string DraftSha256,DateTimeOffset PublishedUtc);
public sealed record ResearchPublicationRecord(Guid Id,string Kind,Guid ResourceId,int PublishedRevision,PublicationAuthorship Authorization);
public sealed record PublicationReviewCommand(int Version,Guid RequestId,string Kind,Guid ResourceId,int DraftRevision,string DraftSha256,int PolicyRevision);
public sealed record PublicationReviewDecision(int Version,Guid RequestId,int ExpectedRevision,PublicationReviewStatus Status,string Explanation);
public sealed record PublicationReviewView(Guid Id,int Revision,string Kind,Guid ResourceId,int DraftRevision,string DraftSha256,int PolicyRevision,
    string Name,Guid AuthorId,Guid RequestedBy,PublicationReviewStatus Status,Guid? ApproverId,string Explanation,DateTimeOffset UpdatedUtc,bool Current);
