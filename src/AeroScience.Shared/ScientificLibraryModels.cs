using System.Text.Json;
namespace AeroScience.Shared;

// Catalog outlines describe archived provider products, never QA-valid flight coverage.
public sealed record ScientificCatalogQuery(string Provider,GeographicBounds Area,DateTimeOffset? FromUtc=null,
    DateTimeOffset? ToUtc=null,double MaximumCloudPercent=100,int PageSize=10,string? Cursor=null,bool CachedOnly=false,bool Refresh=false,string? SceneFilter=null,string Sort="latest");
public sealed record ScientificCatalogBand(string Id,string Units,double Scale,double Offset,double? NoData,
    double? ResolutionMeters,string Format,long? OriginalAssetBytes);
public sealed record ScientificCatalogItem(string Provider,string Product,string Scene,string Revision,RemoteKind Kind,
    GeographicBounds Bounds,JsonElement? Geometry,DateTimeOffset? ObservationUtc,DateTimeOffset? PublishedUtc,
    ScientificCatalogBand[] Bands,string? Crs,string VerticalDatum,string Quality,string Resolution,
    string License,string Reference,string AcquisitionMethod,string[] Limitations,NasaGranuleMetadata? Nasa=null,double? CloudPercent=null);
public sealed record ScientificCatalogPage(string Status,ScientificCatalogItem[] Items,string? NextCursor,
    DateTimeOffset AsOfUtc,DateTimeOffset ExpiresUtc,bool Cached,string[] Limitations,string? Error=null,string? Ordering=null,bool ServerOrdered=false);
public sealed record ScientificCatalogProvider(string Id,string Name,string Product,RemoteKind Kind,string[] RequiredBands,
    bool SceneDates,bool ScenePagination,string Description);
public sealed record ScientificSceneSelection(string Provider,string Scene,string Revision,string[] Bands);
public sealed record ScientificLibrarySelection(int Version,GeographicBounds[] Areas,ScientificSceneSelection[] Scenes,
    Coordinates[]? Polygon=null,long? MaximumTransferBytes=null,JsonElement? RegionGeometry=null);

public sealed record LibraryWorkUnit(int Index,ScientificSceneSelection Scene,GeographicBounds Area,Guid? ReuseSourceId,long StorageAllowanceBytes);
public sealed record LibraryDownloadPreview(int Version,Guid Id,ScientificLibrarySelection Selection,string SelectionHash,
    string SettingsHash,DateTimeOffset CreatedUtc,DateTimeOffset ExpiresUtc,LibraryWorkUnit[] Work,
    ScientificCatalogItem[] Products,long MaximumTransferBytes,long PerItemTransferLimit,long TemporaryStorageAllowanceBytes,
    long ReusableBytes,long? EstimatedTransferBytes,long? EstimatedRetainedBytes,string[] Warnings,int UncoveredCells=0,long? AvailableDriveBytes=null,long? ScientificStorageLimitBytes=null,long? ScientificStorageUsedBytes=null,long? PreparationBudgetRemainingBytes=null,NasaDownloadBundle? Nasa=null);
public sealed record LibraryDownloadItem(int Index,string State,long DownloadedBytes,long UncertainBytes,long ReservedBytes,
    int Attempts,Guid? SourceId,long VerifiedBytes,string? Error,DateTimeOffset UpdatedUtc);
public sealed record LibraryTransferProgress(string Stage,long? TotalOriginalBytes,long? PreparedOriginalBytes,long MeasuredNetworkBytes,
    double? BytesPerSecond,double? RemainingSecondsLow,double? RemainingSecondsHigh,DateTimeOffset UpdatedUtc,string Basis,
    double? CurrentBytesPerSecond=null,int CompletedFiles=0,int TotalFiles=0,int? CurrentFile=null);
public sealed record LibraryDownloadJob(Guid Id,string State,LibraryDownloadPreview Preview,Guid? ApprovalId,
    DateTimeOffset UpdatedUtc,LibraryDownloadItem[] Items,string? Error=null,LibraryTransferProgress? Progress=null)
{
    public long DownloadedBytes=>Items.Sum(i=>i.DownloadedBytes);
    public long UncertainBytes=>Items.Sum(i=>i.UncertainBytes);
    public int CompletedItems=>Items.Count(i=>i.State=="Completed");
}
public sealed record LibraryApprovalRequest(string SelectionHash);
public sealed record LibraryJobControl(string Action);

public sealed record ScientificRegionRequest(string StateFips,string? CountyFips=null,bool CachedOnly=false);
public sealed record ScientificRegion(string Id,string Name,GeographicBounds Bounds,JsonElement Geometry,
    string Reference,string Resolution,DateTimeOffset AsOfUtc,bool Cached);
public sealed record ScientificRegionNamesRequest(string? StateFips=null,bool CachedOnly=false);
public sealed record ScientificRegionName(string Code,string Name);
public sealed record ScientificRegionNames(ScientificRegionName[] Items,string Reference,DateTimeOffset AsOfUtc,bool Cached);

// Optional authoritative NASA catalog details. Unknown metadata is nullable, never inferred pixels.
public sealed record NasaAsset(string Name,string Url,string Role);
public sealed record NasaGranuleMetadata(string Family,string CollectionId,string CollectionShortName,string CollectionVersion,
    string GranuleId,string Flightline,string ProcessingLevel,string Format,string Capability,string Authorization,
    long? OriginalBytes,int? BandCount,double[]? WavelengthsNm,string? Units,NasaAsset[] Assets,string CollectionRevision,
    string MetadataUrl,DateTimeOffset? ObservationEndUtc=null,DateTimeOffset? UpdatedUtc=null);
public sealed record NasaSelectionRequest(string Provider,string GranuleId,string Revision);
public sealed record NasaDownloadRequest(NasaSelectionRequest Selection,long MaximumTransferBytes,bool ValidationAcknowledged=false);
// Original unsigned authoritative URLs only; tokens and temporary redirects are never durable.
public sealed record NasaDownloadFile(string Name,string Url,string Role,long Bytes,string? Etag,bool Ranges,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Sha512=null);
public sealed record NasaDownloadBundle(ScientificCatalogItem Selection,ScientificCatalogItem[] Companions,NasaDownloadFile[] Files,string Revision,int Version=1);
public sealed record EarthdataConnectionStatus(bool Available,bool Connected,string Message);
