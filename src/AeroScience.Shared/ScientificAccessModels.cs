namespace AeroScience.Shared;

public sealed record GeographicBounds(double West,double South,double East,double North)
{
    public Coordinates[] Corners => [new(South,West),new(South,East),new(North,East),new(North,West)];
    public Coordinates Center => new((South+North)/2,(West+East)/2);
    public bool Contains(Coordinates p)=>p.Longitude>=West&&p.Longitude<=East&&p.Latitude>=South&&p.Latitude<=North;
    public static GeographicBounds Around(Coordinates p)
    {
        // A latitude-aligned ~2 km cell; never expand longitude into a huge polar download.
        var south=Math.Floor(p.Latitude/.02)*.02;var west=Math.Floor(p.Longitude/.02)*.02;
        return new(west,south,Math.Min(180,west+.02),Math.Min(90,south+.02));
    }
}
public sealed record ScientificAccessPreferences(bool OnlineEnabled=false,bool PrivacyAccepted=false,
    bool UnmeteredOnly=false,long MaximumDownloadBytes=67108864,int ConcurrentDownloads=1,
    bool BackgroundRetrieval=true,string SelectionPolicy="clear-recent",int LookbackDays=730,double MaximumCloudPercent=30,
    DateTimeOffset? PreferredDateUtc=null);
public sealed record ScientificAccessConfiguration(ScientificAccessPreferences Preferences,string Privacy,string? Warning=null,string? NetworkCost=null);
public sealed record ScientificDataReadiness(string Key,RemoteKind Kind,string Status,string Message,GeographicBounds Area,
    string? Provider=null,string? Dataset=null,DateTimeOffset? ObservationUtc=null,string? Quality=null,
    long DownloadedBytes=0,long? EstimatedDownloadBytes=null,long? EstimatedStorageBytes=null,
    Guid? SourceId=null,string? Authorization=null,DateTimeOffset? UpdatedUtc=null,HyperspectralDiscovery[]? Discoveries=null);
public sealed record HyperspectralDiscovery(string Catalog,string Dataset,DateTimeOffset? ObservationUtc,string Format,string Authorization,string MetadataUrl);
public sealed record ScientificPreparationRequest(double Latitude,double Longitude,RemoteKind[]? Kinds=null);
public sealed record AutomaticSourceProvenance(string Workflow,string Catalog,string Item,string[] UnsignedAssets,
    GeographicBounds RequestedArea,DateTimeOffset RetrievedUtc,string Preparation,string Quality,string NativeResolution,
    long NetworkBytes,string[] Limitations,string? ProviderMetadataJson=null,int? CheckedPixels=null,int? UsablePixels=null);
