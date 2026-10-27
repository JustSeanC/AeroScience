namespace AeroScience.Shared;

// Server-owned definition metadata only. No desktop membership, assignment or evidence transfer implied.
public enum OrganizationFocus { Unknown, ScientificResearch, ExperimentalFlightTest, Mixed }
public enum OrganizationPublication { Draft, Published }
public static class OrganizationRoles
{
    public const string Administrator="Administrator",Manager="Manager",Reviewer="Reviewer",Pilot="Pilot";
    public static readonly string[] All=[Administrator,Manager,Reviewer,Pilot];
}
public sealed record OrganizationTemplateReference(string Id,int Version);
public sealed record OrganizationResearchDefinition(int Version,OrganizationTemplateReference Template,
    FlightRegimeDefinition? FlightRegime=null,AtmosphericResearchDefinition? AtmosphericProfile=null,
    AtmosphericTransectDefinition? AtmosphericTransect=null,SurveyResearchDefinition? Survey=null,
    ObservationResearchDefinition? ObservationFlight=null,ExperimentalSensorDefinition? ExperimentalSensor=null,
    FlightTestProcedureDefinition? Procedure=null);
public sealed record OrganizationTemplate(OrganizationTemplateReference Reference,string Name,string Description,OrganizationResearchDefinition Definition);
public sealed record OrganizationProgramRequest(int Version,Guid RequestId,int ExpectedRevision,string Name,string Description);
public sealed record OrganizationCampaignRequest(int Version,Guid RequestId,int ExpectedRevision,string Name,string Description,
    string Objective,OrganizationFocus Focus,OrganizationTemplateReference Template);
public sealed record OrganizationPublishRequest(int Version,Guid RequestId,int ExpectedRevision);
public sealed record OrganizationProgramView(Guid Id,int Revision,string Name,string Description,DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationCampaignView(Guid Id,Guid ProgramId,int Revision,string Name,string Description,string Objective,
    OrganizationFocus Focus,OrganizationPublication Status,int? PublishedRevision,OrganizationResearchDefinition Definition,
    DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationPage<T>(int Version,T[] Items,int? NextOffset);

public enum OrganizationMembershipState { PendingApproval, Active, Suspended, Removed }
public enum OrganizationEnrollmentMode { InvitationOnly, ApplicationsAllowed }
public sealed record OrganizationEnrollmentPolicy(int Version,int Revision,OrganizationEnrollmentMode Mode,bool RequireApproval);
public sealed record OrganizationPolicyRequest(int Version,Guid RequestId,int ExpectedRevision,OrganizationEnrollmentMode Mode,bool RequireApproval);
public sealed record OrganizationInvitationRequest(int Version,Guid RequestId,int ExpiresHours=48);
public sealed record OrganizationInvitationView(Guid Id,DateTimeOffset CreatedUtc,DateTimeOffset ExpiresUtc,string Status,string? Link=null);
public sealed record OrganizationJoinRequest(int Version,Guid OrganizationId,string UserName,string Password,string? Invitation,string Statement,bool ConfirmOrganization);
public sealed record OrganizationMemberView(Guid UserId,string Name,string Role,OrganizationMembershipState State,int Revision,DateTimeOffset UpdatedUtc,string Statement);
public sealed record OrganizationMemberRequest(int Version,Guid RequestId,int ExpectedRevision,string Role,OrganizationMembershipState State);
public sealed record OrganizationPublicIdentity(int Version,Guid Id,string Name,string Description,string Origin,OrganizationEnrollmentPolicy Policy);
public sealed record OrganizationPublishedCampaign(Guid Id,Guid ProgramId,string ProgramName,int Revision,string Name,string Description,string Objective,OrganizationFocus Focus,string Approach);
public sealed record OrganizationMemberInformation(OrganizationPublicIdentity Organization,string Role,OrganizationMembershipState State,OrganizationPage<OrganizationPublishedCampaign> Campaigns);
// Browser-approved, read-published-only device grant. This is not an OAuth/OIDC compliance claim.
public sealed record OrganizationDeviceStart(Guid OrganizationId,string ClientName,string Challenge);
public sealed record OrganizationDevicePending(Guid Id,string DeviceSecret,string UserCode,string ApprovalUrl,DateTimeOffset ExpiresUtc,int PollSeconds);
public sealed record OrganizationDeviceApproval(Guid Id,Guid OrganizationId,bool Approve,bool ConfirmCode);
public sealed record OrganizationDevicePoll(Guid Id,Guid OrganizationId,string DeviceSecret,string Verifier);
public sealed record OrganizationDeviceToken(string Status,Guid? ConnectionId=null,string? Credential=null,DateTimeOffset? ExpiresUtc=null);
