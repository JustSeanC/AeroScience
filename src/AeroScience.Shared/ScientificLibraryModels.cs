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

public sealed record LibraryWorkUnit(int Index,ScientificSceneSelection Scene,GeographicBounds Area,Guid? ReuseSourceId,long StorageAllowanceBytes);
public sealed record LibraryDownloadPreview(int Version,Guid Id,ScientificLibrarySelection Selection,string SelectionHash,
    string SettingsHash,DateTimeOffset CreatedUtc,DateTimeOffset ExpiresUtc,LibraryWorkUnit[] Work,
    ScientificCatalogItem[] Products,long MaximumTransferBytes,long PerItemTransferLimit,long TemporaryStorageAllowanceBytes,
    long ReusableBytes,long? EstimatedTransferBytes,long? EstimatedRetainedBytes,string[] Warnings,int UncoveredCells=0);
public sealed record LibraryDownloadItem(int Index,string State,long DownloadedBytes,long UncertainBytes,long ReservedBytes,
    int Attempts,Guid? SourceId,long VerifiedBytes,string? Error,DateTimeOffset UpdatedUtc);
public sealed record LibraryDownloadJob(Guid Id,string State,LibraryDownloadPreview Preview,Guid? ApprovalId,
    DateTimeOffset UpdatedUtc,LibraryDownloadItem[] Items,string? Error=null)
{
    public long DownloadedBytes=>Items.Sum(i=>i.DownloadedBytes);
    public long UncertainBytes=>Items.Sum(i=>i.UncertainBytes);
    public int CompletedItems=>Items.Count(i=>i.State=="Completed");
}
public sealed record LibraryApprovalRequest(string SelectionHash);
public sealed record LibraryJobControl(string Action);
