using AeroScience.Shared;
namespace AeroScience.Organization;

public sealed class CampaignCoverageIndex
{
    public Guid RootId {get;set;}public int Version {get;set;}public int ReviewRevision {get;set;}public string HeadSha256 {get;set;}="";public string ApprovalSignature {get;set;}="";public bool Valid {get;set;}
}
// Derived approved sampling/location metadata and original pointers only. No science value,
// note text, frame, cube, source file, private conversation, credential or package copy.
public sealed class CampaignCoverageObservation
{
    public Guid RootId {get;set;}public Guid CardId {get;set;}public string RequirementKey {get;set;}="";public string IdentitySha256 {get;set;}="";public string ContentSha256 {get;set;}="";
    public Guid CampaignId {get;set;}public Guid OpportunityId {get;set;}public int PublicationRevision {get;set;}public string PublicationSha256 {get;set;}="";
    public string ReferenceId {get;set;}="";public string CompatibilityId {get;set;}="";public Guid FlightId {get;set;}public string FlightReference {get;set;}="";public Guid PilotId {get;set;}public string Contributor {get;set;}="";
    public string Variable {get;set;}="";public string Name {get;set;}="";public string Instrument {get;set;}="";public string Unit {get;set;}="";public string Provenance {get;set;}="";public CoverageEvidenceCategory Category {get;set;}public string SourceSemanticsSha256 {get;set;}="";
    public DateTimeOffset TimestampUtc {get;set;}public DateOnly Date {get;set;}public double? AltitudeFeetMsl {get;set;}public double? Latitude {get;set;}public double? Longitude {get;set;}
    public string OriginalReferenceJson {get;set;}="";
}
public sealed class CampaignCoverageGoalRecord
{
    public Guid Id {get;set;}public Guid CampaignId {get;set;}public int Revision {get;set;}public string Name {get;set;}="";public string SearchName {get;set;}="";public DateTimeOffset UpdatedUtc {get;set;}public string SnapshotJson {get;set;}="";
}
