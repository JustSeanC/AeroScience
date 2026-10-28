using AeroScience.Shared;
namespace AeroScience.Organization;

// Review projections and append-only metadata reference the existing received bytes, never copy them.
public sealed class ResearchReviewCaseRecord
{
    public Guid Id {get;set;} public Guid PilotId {get;set;} public Guid SubmissionId {get;set;} public Guid ProgramId {get;set;} public Guid CampaignId {get;set;}
    public string PilotName {get;set;}="";public string ProgramName {get;set;}="";public string CampaignName {get;set;}="";public string Title {get;set;}="";public string CardNames {get;set;}="";
    public string SearchName {get;set;}="";public string SearchPilot {get;set;}="";public string SearchCampaign {get;set;}="";public string SearchProgram {get;set;}="";
    public string RootPayloadSha256 {get;set;}="";public string Completeness {get;set;}="";public int Revision {get;set;} public string HeadSha256 {get;set;}=new('0',64);
    public ResearchReviewState State {get;set;} public DateTimeOffset ReceivedUtc {get;set;} public DateTimeOffset UpdatedUtc {get;set;} public int PackageCount {get;set;}
}
public sealed class ResearchReviewRequirementRecord
{
    public Guid RootId {get;set;}public Guid CardId {get;set;}public string Key {get;set;}="";public string DefinitionJson {get;set;}="";
    public ResearchReviewState State {get;set;} public string? FindingJson {get;set;}
}
public sealed class ResearchReviewEventRecord
{
    public Guid RootId {get;set;}public int Sequence {get;set;}public Guid Id {get;set;}public string PayloadJson {get;set;}="";public string Sha256 {get;set;}="";
}
public sealed class ResearchReviewRequestRecord
{
    public Guid Id {get;set;}public Guid RootId {get;set;}public Guid CardId {get;set;}public string RequirementKey {get;set;}="";public string TargetJson {get;set;}="";
    public string Note {get;set;}="";public Guid AuthorId {get;set;}public string AuthorName {get;set;}="";public DateTimeOffset CreatedUtc {get;set;}public ResearchReviewRequestState State {get;set;}
}
public sealed class ResearchReviewPackageRecord
{
    public Guid ReceiptId {get;set;}public Guid RootId {get;set;}public Guid PilotId {get;set;}public Guid SubmissionId {get;set;}public Guid? RequestEventId {get;set;}
    public DateTimeOffset ReceivedUtc {get;set;}public string PayloadSha256 {get;set;}="";public Guid FlightId {get;set;}
}
public sealed class CampaignReviewGrantRecord
{
    public Guid CampaignId {get;set;}public Guid ReviewerId {get;set;}public int Revision {get;set;}public bool Allowed {get;set;}public Guid UpdatedBy {get;set;}public DateTimeOffset UpdatedUtc {get;set;}
}
