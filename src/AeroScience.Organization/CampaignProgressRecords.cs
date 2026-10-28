using AeroScience.Shared;
namespace AeroScience.Organization;

// Rebuildable scalar indexes of P15/P17/P18 metadata and expressly approved received evidence.
public sealed class CampaignProjectionRecord
{
    public Guid RootId {get;set;}public int Version {get;set;}public int ReviewRevision {get;set;}public string HeadSha256 {get;set;}="";public bool Valid {get;set;}
}
public sealed class CampaignContributionRecord
{
    public Guid RootId {get;set;}public Guid CardId {get;set;}public Guid PilotId {get;set;}public Guid ClaimId {get;set;}public Guid OpportunityId {get;set;}public Guid CampaignId {get;set;}public Guid ProgramId {get;set;}
    public int PublicationRevision {get;set;}public string PublicationSha256 {get;set;}="";public string DefinitionSha256 {get;set;}="";public int AcceptancePolicyVersion {get;set;}=1;
    public int ReviewRevision {get;set;}public string SourceHeadSha256 {get;set;}="";public bool Valid {get;set;}public ResearchReviewState State {get;set;}public int RequiredFindings {get;set;}public int ApprovedFindings {get;set;}public DateTimeOffset? AcceptedUtc {get;set;}public DateTimeOffset UpdatedUtc {get;set;}
    public string SummaryJson {get;set;}="";public string ApprovalSignature {get;set;}="";
}
public sealed class CampaignMeasurementRecord
{
    public Guid RootId {get;set;}public Guid CardId {get;set;}public string GroupId {get;set;}="";public string PayloadJson {get;set;}="";
}
