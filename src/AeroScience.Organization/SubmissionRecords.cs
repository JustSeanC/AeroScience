using AeroScience.Shared;
namespace AeroScience.Organization;

public sealed class SubmissionPolicyRecord
{
    public Guid OpportunityId {get;set;}
    public int Revision {get;set;}=1;
    public SubmissionPolicyMode Mode {get;set;}
    public DateTimeOffset PublishedUtc {get;set;}
    public Guid AuthorId {get;set;}
}
public sealed class ReceivedSubmissionRecord
{
    public Guid PilotId {get;set;}
    public Guid Id {get;set;}
    public Guid ReceiptId {get;set;}
    public Guid CampaignId {get;set;}
    public string Name {get;set;}="";
    public string SearchName {get;set;}="";
    public string ManifestSha256 {get;set;}="";
    public string PayloadSha256 {get;set;}="";
    public int PayloadBytes {get;set;}
    public string PayloadJson {get;set;}="";
    public string ReceiptJson {get;set;}="";
    public DateTimeOffset ReceivedUtc {get;set;}
}
public sealed class CancelledSubmissionRecord
{
    public Guid PilotId {get;set;}
    public Guid Id {get;set;}
    public string ManifestSha256 {get;set;}="";
    public DateTimeOffset CancelledUtc {get;set;}
}
public sealed class SubmissionUsageRecord
{
    public int Id {get;set;}=1;
    public long PayloadBytes {get;set;}
}
public sealed class OrganizationSubmissionOptions
{
    public long MaximumRetainedPayloadBytes {get;set;}=1024L*1024*1024;
}
