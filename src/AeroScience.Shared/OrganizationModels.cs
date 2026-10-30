namespace AeroScience.Shared;

// Versioned organization metadata only. No personal evidence transfer implied.
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
public sealed record OrganizationPublishRequest(int Version,Guid RequestId,int ExpectedRevision,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DraftSha256=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ApprovalId=null);
public sealed record OrganizationProgramView(Guid Id,int Revision,string Name,string Description,DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationCampaignView(Guid Id,Guid ProgramId,int Revision,string Name,string Description,string Objective,
    OrganizationFocus Focus,OrganizationPublication Status,int? PublishedRevision,OrganizationResearchDefinition Definition,
    DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName,int? ProgramRevision=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OrganizationTemplateReference? SourceTemplate=null);
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
// Browser-approved scoped device grant; existing/default grants remain read-published-only.
// This is not an OAuth/OIDC compliance claim.
public sealed record OrganizationDeviceStart(Guid OrganizationId,string ClientName,string Challenge,OrganizationDesktopPermission Permission=OrganizationDesktopPermission.ReadPublished);
public sealed record OrganizationDevicePending(Guid Id,string DeviceSecret,string UserCode,string ApprovalUrl,DateTimeOffset ExpiresUtc,int PollSeconds,OrganizationDesktopPermission Permission=OrganizationDesktopPermission.ReadPublished);
public sealed record OrganizationDeviceApproval(Guid Id,Guid OrganizationId,bool Approve,bool ConfirmCode,bool ConfirmClaimManagement=false,bool ConfirmSubmission=false,bool ConfirmLiveSharing=false);
public sealed record OrganizationDevicePoll(Guid Id,Guid OrganizationId,string DeviceSecret,string Verifier);
public sealed record OrganizationDeviceToken(string Status,Guid? ConnectionId=null,string? Credential=null,DateTimeOffset? ExpiresUtc=null,OrganizationDesktopPermission Permission=OrganizationDesktopPermission.ReadPublished);

public enum OrganizationDesktopPermission { ReadPublished, ManageOwnClaims, SubmitEvidence, ShareLiveFlight }
public enum OrganizationParticipation { Exclusive, MultiPilot }
public enum OrganizationClaimState { Reserved, Claimed, Released }
public enum OrganizationClaimAction { Claim, Accept, Release, Assign, ManagerRelease }
public sealed record OrganizationClaimPolicy(OrganizationParticipation Participation,int RequiredDistinctPilots,int MaximumActiveClaims,bool AllowVolunteers,string[] EligibleRoles,string ReviewPolicy);
public sealed record OrganizationOpportunityRequest(int Version,Guid RequestId,int ExpectedRevision,int CampaignRevision,string Name,string Description,string Objective,string Instructions,OrganizationClaimPolicy Policy);
public sealed record OrganizationOpportunitySnapshot(int Version,Guid Id,Guid OrganizationId,Guid ProgramId,Guid CampaignId,int CampaignRevision,int Revision,OrganizationPublication Status,string Name,string Description,string Objective,string Instructions,OrganizationClaimPolicy Policy,OrganizationResearchDefinition Definition,DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,Guid AuthorId,string AuthorName);
public sealed record OrganizationOpportunityView(OrganizationOpportunitySnapshot Card,string Sha256,int? PublishedRevision,bool Suspended,int AvailabilityRevision,int HeldClaims,int AvailableSlots,bool Eligible,string Availability,OrganizationOpportunitySnapshot? PublishedCard=null);
public sealed record OrganizationOpportunityAvailability(int Version,Guid RequestId,int ExpectedAvailabilityRevision,bool Suspended);
public sealed record OrganizationClaimCommand(int Version,Guid RequestId,Guid OrganizationId,Guid OpportunityId,int PublishedRevision,OrganizationClaimAction Action,Guid? ClaimId=null,int? ExpectedClaimRevision=null,Guid? PilotId=null);
public sealed record OrganizationClaimView(Guid Id,Guid PilotId,string? PilotName,Guid OrganizationId,Guid OpportunityId,int Revision,int IssuedRevision,string IssuedSha256,OrganizationClaimState State,DateTimeOffset AssignedUtc,DateTimeOffset? AcceptedUtc,DateTimeOffset? EndedUtc,string? EndReason,OrganizationOpportunitySnapshot IssuedCard,bool UpdateAvailable,int? LatestPublishedRevision,string Availability,string EvidenceStatus="Pending / Unverified — claiming is not a scientific contribution");
public sealed record OrganizationClaimEventView(long Id,Guid ClaimId,int ClaimRevision,string Action,DateTimeOffset TimestampUtc,Guid ActorId);
// Selection is explicitly opt-in, takes effect on the next flight only, and requires P16 integration.
// P15 exposes the update offer but does not mutate an issued revision or execute this future contract.
public sealed record OrganizationClaimRevisionSelection(int Version,Guid ClaimId,int ExpectedClaimRevision,int DesiredPublishedRevision,bool ExplicitConsent,string EffectiveBoundary="nextFlight");

public sealed record OrganizationClaimEventPage(int Version,OrganizationClaimEventView[] Items,long? NextAfter);
