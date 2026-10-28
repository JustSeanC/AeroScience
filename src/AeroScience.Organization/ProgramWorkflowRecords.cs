using AeroScience.Shared;
namespace AeroScience.Organization;

public sealed class ProgramWorkflowPolicyRecord
{
    public Guid Id {get;set;}public Guid ProgramId {get;set;}public int Revision {get;set;}public int RulesRevision {get;set;}
    public bool PhaseGatesEnabled {get;set;}public bool AdministrativeHold {get;set;}public string SnapshotSha256 {get;set;}="";public string SnapshotJson {get;set;}="";
}
public sealed class TestPhaseRecord
{
    public Guid Id {get;set;}public Guid ProgramId {get;set;}public int Revision {get;set;}public int DefinitionRevision {get;set;}
    public int Sequence {get;set;}public bool Enabled {get;set;}public bool Held {get;set;}public bool Released {get;set;}
    public string SnapshotSha256 {get;set;}="";public string SnapshotJson {get;set;}="";
}
public sealed class PhaseCardRecord
{
    public Guid PhaseId {get;set;}public Guid OpportunityId {get;set;}public int PublicationRevision {get;set;}
}
public sealed class WorkflowReviewIndex
{
    public Guid RootId {get;set;}public int Version {get;set;}public int ReviewRevision {get;set;}public string HeadSha256 {get;set;}="";public bool Valid {get;set;}
}
public sealed class WorkflowRequirementApproval
{
    public Guid RootId {get;set;}public Guid CardId {get;set;}public string RequirementKey {get;set;}="";
    public Guid FindingId {get;set;}public string FindingSha256 {get;set;}="";
}
// Administrative metadata and original pointers only; no readings, payloads or private archive copies.
public sealed class WorkflowExceptionRecord
{
    public Guid Id {get;set;}public Guid ProgramId {get;set;}public Guid CampaignId {get;set;}public Guid OpportunityId {get;set;}
    public int PublicationRevision {get;set;}public string PublicationSha256 {get;set;}="";public string RequirementKey {get;set;}="";
    public Guid? PhaseId {get;set;}public int? PhaseDefinitionRevision {get;set;}public int Revision {get;set;}public int RulesRevision {get;set;}
    public WorkflowExceptionKind Kind {get;set;}public bool Active {get;set;}public bool CountAsPartialContributor {get;set;}
    public Guid? RootReceiptId {get;set;}public Guid? CardId {get;set;}public Guid? PilotId {get;set;}public Guid? ClaimId {get;set;}
    public int? SourceReviewRevision {get;set;}public string? SourceReviewHeadSha256 {get;set;}
    public string SourceSignature {get;set;}="";public ResearchReviewState? SourceState {get;set;}
    public string ScopeSha256 {get;set;}="";public string SnapshotSha256 {get;set;}="";public string SnapshotJson {get;set;}="";
    public DateTimeOffset TimestampUtc {get;set;}
}
