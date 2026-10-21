using System.Text.Json;
namespace AeroScience.Shared;

// Catalog outlines describe archived provider products, never QA-valid flight coverage.
public sealed record ScientificCatalogQuery(string Provider,GeographicBounds Area,DateTimeOffset? FromUtc=null,
    DateTimeOffset? ToUtc=null,double MaximumCloudPercent=100,int PageSize=10,string? Cursor=null,bool CachedOnly=false);
public sealed record ScientificCatalogBand(string Id,string Units,double Scale,double Offset,double? NoData,
    double? ResolutionMeters,string Format,long? OriginalAssetBytes);
public sealed record ScientificCatalogItem(string Provider,string Product,string Scene,string Revision,RemoteKind Kind,
    GeographicBounds Bounds,JsonElement? Geometry,DateTimeOffset? ObservationUtc,DateTimeOffset? PublishedUtc,
    ScientificCatalogBand[] Bands,string? Crs,string VerticalDatum,string Quality,string Resolution,
    string License,string Reference,string AcquisitionMethod,string[] Limitations);
public sealed record ScientificCatalogPage(string Status,ScientificCatalogItem[] Items,string? NextCursor,
    DateTimeOffset AsOfUtc,DateTimeOffset ExpiresUtc,bool Cached,string[] Limitations,string? Error=null);
public sealed record ScientificCatalogProvider(string Id,string Name,string Product,RemoteKind Kind,string[] RequiredBands,
    bool SceneDates,bool ScenePagination,string Description);
public sealed record ScientificSceneSelection(string Provider,string Scene,string Revision,string[] Bands);
public sealed record ScientificLibrarySelection(int Version,GeographicBounds[] Areas,ScientificSceneSelection[] Scenes,
    Coordinates[]? Polygon=null,long? MaximumTransferBytes=null);
