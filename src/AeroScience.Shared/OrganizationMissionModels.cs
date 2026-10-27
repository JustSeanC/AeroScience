namespace AeroScience.Shared;

// Organization metadata only. Credentials and original scientific measurements never belong here.
public sealed record OrganizationClaimOriginal(OrganizationClaimView Claim,string IssuedJson,string OrganizationName,
    string CampaignName,string PilotName,bool AvailableForPreparation,DateTimeOffset CheckedUtc);
public sealed record OrganizationAssignmentIdentity(int Version,Guid ConnectionId,string Origin,Guid OrganizationId,
    string OrganizationName,Guid CampaignId,string CampaignName,Guid OpportunityId,Guid ClaimId,Guid PilotId,string PilotName,
    int IssuedRevision,string IssuedSha256,string IssuedJson,DateTimeOffset AcceptedUtc,DateTimeOffset VerifiedUtc,int VerifiedClaimRevision);
public sealed record OrganizationMissionPreviewRequest(Guid? PlanId=null);
public sealed record OrganizationMissionChange(string Id,string Title,string Before,string After);
public sealed record OrganizationMissionPreview(OrganizationAssignmentIdentity Assignment,PersonalResearchCard Card,MissionPlan Before,
    MissionPlan Suggested,OrganizationMissionChange[] Changes,string[] Limitations,string PlanSha256,bool ExistingPlan);
public sealed record OrganizationMissionSave(int Version,Guid RequestId,Guid? PlanId,string PlanSha256,string IssuedSha256,
    int ClaimRevision,string[] AcceptedChanges,string SaveMode);
public sealed record PreparedOrganizationMission(MissionPlan Plan,PersonalResearchCard Card,bool Reused,string[] Limitations);
public sealed record OrganizationMissionAudit(Guid RequestId,Guid PlanId,Guid CardId,string RequestSha256,string OriginalPlanSha256,
    string SavedPlanSha256,string[] AcceptedChanges,string[] RejectedChanges,DateTimeOffset SavedUtc,
    OrganizationMissionChange[]? ReviewedChanges=null);
public sealed record OrganizationClaimCheck(DateTimeOffset CheckedUtc,string Status,OrganizationClaimOriginal? Original,string Message);
public sealed record OrganizationClaimReconcile(Guid? ConnectionId=null);
public sealed record OrganizationFlightLabel(string OrganizationName,Guid ClaimId,int IssuedRevision,string Status);
