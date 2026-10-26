namespace AeroScience.Shared;

public enum SurveyMeasurement { AirTemperature, TerrainElevation, LandSurfaceTemperature }
public sealed record SurveyResearchDefinition(int Version,SurveyMeasurement[] Measurements,
    string Question="What terrain and temperature characteristics were sampled along my survey?",int MinimumValidSamples=3,
    int MinimumLocations=2,double LocationSpacingMetres=100,int? MaximumSourceAgeDays=null,string SamplingStrategy="recorded-locations")
{public static SurveyResearchDefinition Default=>new(1,[SurveyMeasurement.AirTemperature]);}
public sealed record SurveyGroupSummary(string Compatibility,string Unit,string Datum,long Samples,int Locations,double Minimum,double Maximum);
public sealed record SurveyObjectiveSummary(SurveyMeasurement Measurement,ResearchAttemptOutcome Outcome,long ValidSamples,long ExcludedSamples,
    long UnavailableProducts,SurveyGroupSummary[] Groups,string[] Issues);
public sealed record SurveySourceSummary(Guid Id,string Name,string Provider,string Citation,string Scene,DateTimeOffset? ObservationUtc,
    string Datum,string Unit,RasterGrid Grid,string ManifestSha256,string OriginalFilesSha256);
public sealed record SurveyResearchSummary(int Version,Guid LibraryId,SurveyObjectiveSummary[] Objectives,SurveySourceSummary[] Sources,
    bool SourcesTruncated,long OriginalObservations,long Products,long Gaps,string[] Limitations);
// A bounded, regenerated view of original retained evidence. Never a second observation archive.
public sealed record SurveyResearchSample(SurveyMeasurement Measurement,double Value,string Unit,Coordinates Position,DateTimeOffset FlightUtc,
    DateTimeOffset? SourceUtc,Guid? SourceId,long? ObservationId,Guid? ProductId,int? Column,int? Row,string Datum,double? UncertaintyKelvin=null,string Compatibility="");
public sealed record SurveyResearchReview(bool MatchesSavedEvidence,SurveyResearchSummary Retained,SurveyResearchSample[] Preview,bool Downsampled,
    string Message,Dictionary<Guid,bool> OriginalsRetained);
