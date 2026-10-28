namespace AeroScience.Shared;

public enum CoverageEvidenceCategory { SimulatorReported, SyntheticFixture, PilotObservation, ModeledOutput, HistoricalDataset, Unknown }
public sealed record CoverageAltitudeTarget(double MinimumFeetMsl,double MaximumFeetMsl,double BandFeet);
public sealed record CoverageSamplingGrid(double South,double West,double North,double East,int Rows,int Columns);
public sealed record CoverageGoalDefinition(int Version,string Name,string Question,Guid OpportunityId,int PublicationRevision,
    string PublicationSha256,string Variable,string Instrument,string Unit,CoverageEvidenceCategory Category,
    string? RequirementKey=null,string? SourceSemanticsSha256=null,CoverageAltitudeTarget? Altitude=null,
    CoverageSamplingGrid? Grid=null,DateTimeOffset? FromUtc=null,DateTimeOffset? ThroughUtc=null,
    int MinimumObservations=1,int MinimumFlights=1,int MinimumDates=1,int MinimumPilots=1);
public sealed record CoverageGoalSave(int Version,Guid RequestId,int ExpectedRevision,CoverageGoalDefinition Definition);
public sealed record CoverageGoalView(Guid Id,Guid CampaignId,int Revision,CoverageGoalDefinition Definition,
    DateTimeOffset UpdatedUtc,string Sha256,bool Current,bool HistoricalPublication);
public sealed record CoverageVariable(string Id,string Name,string Instrument,string Unit,string Meaning,bool Supported);
public sealed record CampaignCoverageFilter(string? CompatibilityId=null,Guid? OpportunityId=null,int? PublicationRevision=null,
    string? RequirementKey=null,string? Variable=null,string? Instrument=null,CoverageEvidenceCategory? Category=null,
    DateTimeOffset? FromUtc=null,DateTimeOffset? ThroughUtc=null,string? FlightReference=null);
public sealed record CampaignCoverageView(int Version,Guid OrganizationId,Guid CampaignId,string Name,DateTimeOffset CheckedUtc,
    bool CanManage,long ApprovedCards,long IndexedCards,long UnavailableCards,bool HasGoals,string[] Methods,CoverageVariable[] Variables);
public sealed record CoverageGroup(string Id,Guid OpportunityId,int PublicationRevision,string PublicationSha256,
    string Variable,string Name,string Instrument,string Unit,string Provenance,CoverageEvidenceCategory Category,
    string SourceSemanticsSha256,long Observations,long Flights,long Dates,long Pilots,long LocatedObservations,
    long AltitudeObservations,long DuplicateReferences,long ConflictingIdentities,DateTimeOffset? FirstUtc,DateTimeOffset? LastUtc);
public sealed record CoverageCounts(long Observations,long Flights,long Dates,long Pilots,long LocatedObservations=0);
public sealed record CoverageBand(int Index,double LowerFeetMsl,double UpperFeetMsl,bool UpperInclusive,CoverageCounts Counts,
    bool? Satisfied,string Status);
public sealed record CoverageCell(int Row,int Column,double South,double West,double North,double East,CoverageCounts Counts,
    bool? Satisfied,string Status);
public sealed record CoverageGoalAssessment(CoverageGoalView Goal,string State,bool Complete,bool Compatible,
    string? CompatibilityId,CoverageCounts Counts,CoverageBand[] Bands,CoverageCell[] Cells,string[] Gaps,string[] Limitations,
    long OtherCompatibilityGroups,bool HasMoreCells=false);
public sealed record CoverageLocation(string Identity,string CompatibilityId,string Variable,string Instrument,string Unit,
    string Provenance,CoverageEvidenceCategory Category,string Contributor,string FlightReference,DateTimeOffset TimestampUtc,
    double Latitude,double Longitude,double? AltitudeFeetMsl,Guid ApprovedReceiptId,Guid CardId,string RequirementKey);
public sealed record CoverageTemporalSlice(DateOnly Date,CoverageCounts Counts,long RepeatObservations);
// Internal original pointers contain no credentials, numeric science readings, note text or raw telemetry.
public sealed record CoverageOriginalReference(Guid SubmissionId,string ItemKey,SubmissionEvidenceKind Kind,
    Guid? DatasetId=null,long? ObservationId=null,long? Sequence=null,Guid? MarkerId=null);
public sealed record ApprovedCoverageSample(int Version,string IdentitySha256,string ContentSha256,Guid FlightId,
    string Variable,string Name,string Instrument,string Unit,string Provenance,CoverageEvidenceCategory Category,
    string SourceSemanticsSha256,DateTimeOffset TimestampUtc,double? AltitudeFeetMsl,double? Latitude,double? Longitude,
    CoverageOriginalReference Original);
