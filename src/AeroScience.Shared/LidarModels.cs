namespace AeroScience.Shared;

// Historical delivered point records; never flight acquisitions or raster samples.
public sealed record LidarVlr(string UserId,int RecordId,string Description,string Base64,bool Extended=false);
public sealed record LidarLasHeader(int VersionMinor,int PointFormat,int RecordLength,long PointCount,bool Compressed,
    double[] Scale,double[] Offset,double[] NativeBounds,string? HorizontalCrs,string VerticalReference,string VerticalUnit,
    string? Wkt,LidarVlr[] Vlrs,bool Copc,string[] Dimensions,string[] Limitations,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int GlobalEncoding=0,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? OriginalHeaderBase64=null);
public sealed record LidarOriginal(string File,LidarLasHeader Header,string? EptNode=null);
public sealed record LidarSourceDefinition(int Version,string Format,GeographicBounds Domain,LidarOriginal[] Originals,
    string? EptProject=null,string? EptMetadataSha256=null,string? EptMetadataJson=null,
    string EvidenceClass="Historical source LiDAR returns; not returns emitted by the MSFS aircraft");
public sealed record LidarImportMetadata(string Name,string Project,string License,string Citation,
    DateTimeOffset? AcquiredFromUtc=null,DateTimeOffset? AcquiredToUtc=null,DateTimeOffset? PublishedUtc=null);
public sealed record LidarDiscoveryRequest(GeographicBounds Area,string? Prefix=null,string? Cursor=null,string? Project=null,bool CachedOnly=false);
public sealed record LidarEptProject(string Project,string MetadataSha256,string MetadataJson,GeographicBounds Bounds,
    string HorizontalCrs,string VerticalReference,long PointCount,string[] Dimensions,string State,string[] Limitations);
public sealed record LidarDiscovery(LidarEptProject[] Projects,System.Text.Json.JsonElement[] OfficialWorkUnits,string? NextCursor,string? Prefix,
    DateTimeOffset AsOfUtc,bool Cached,string[] Limitations,string[]? UnavailableProjects=null);
public sealed record LidarDownloadRequest(string Project,string MetadataSha256,GeographicBounds Area,long MaximumTransferBytes=16777216,int MaximumNodes=32,long MaximumCandidatePoints=1000000);
public sealed record LidarDownloadNode(string Key,long Points);
public sealed record LidarDownloadBundle(LidarEptProject Source,GeographicBounds Area,LidarDownloadNode[] Nodes,string[] HierarchyJson,
    string Revision,long CandidatePoints,bool CompleteHierarchy,long MaximumTransferBytes,string[] Limitations);
