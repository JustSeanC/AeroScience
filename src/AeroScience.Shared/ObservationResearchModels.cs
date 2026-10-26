namespace AeroScience.Shared;

public sealed record ObservationResearchDefinition(int Version=1,
    string Question="What notable features or events can I document during this flight?",
    string IntendedObservations="Describe visible features, changes and points of interest in my own words.",
    string Approach="Record observations as they occur along a free-flight route; distinguish description from interpretation.",
    string[]? Categories=null,int MinimumObservations=3,bool RequireEveryCategory=false,bool RequirePosition=false,string[]? SupportingInstruments=null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] SelectedCategories=>Categories??["observation","finding","note"];
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] SelectedInstruments=>SupportingInstruments??[];
}
public sealed record PilotObservationContext(int Version,ResearchCardReference[] Cards,DateTimeOffset? PositionTimestampUtc,
    ObservationQuality PositionQuality,string Provenance="Pilot Observation");
public sealed record ObservationCategorySummary(string Category,long Documented,long Qualified);
public sealed record ObservationResearchSummary(int Version,long Documented,long Qualified,long Located,long Excluded,
    ObservationCategorySummary[] Categories,string DocumentationSha256,Guid? LibraryId,string SupportingSha256,
    AtmosphericQuantitySummary[] SupportingMeasurements,string[] Limitations);
// Regenerated bounded views of the existing notes/markers and science archive, never a new archive.
public sealed record PilotObservationEntry(Guid Id,string Kind,string Category,DateTimeOffset TimestampUtc,string Text,
    double? Latitude,double? Longitude,double? AltitudeMslFeet,DateTimeOffset? PositionTimestampUtc,bool Qualified,long[] SupportingObservationIds,string? ExclusionReason=null);
public sealed record ObservationResearchReview(bool MatchesSavedEvidence,bool SupportingEvidenceMatches,ObservationResearchSummary Retained,
    PilotObservationEntry[] Preview,ScienceObservation[] SupportingPreview,bool Downsampled,string Message);
