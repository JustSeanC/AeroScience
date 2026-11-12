using AeroScience.Shared;
namespace AeroScience.Missions;
public sealed record LidarProcessingOptions(int RecordsPerTile=2048,long MaximumCandidatePoints=1000000,int MaximumTiles=8192,
    long MaximumOutputBytes=134217728,long MaximumScratchBytes=67108864,long MaximumVerifiedSourceBytes=2147483648,
    int FirstGeometryChunk=1,int? LastGeometryChunk=null,int MaximumGeometryVertices=200000,int MaximumShapesPerPoint=256,int MaximumSupportLinksPerTile=8192);
public sealed record LidarSourceChoice(Guid SourceId,string ManifestSha256,bool UnverifiedVerticalAcknowledged=false);
public sealed record LidarProcessingRequest(Guid GeometryArtifactId,LidarSourceChoice[] Sources,LidarProcessingOptions? Options=null,Guid RequestId=default);
public sealed record LidarProcessingJob(Guid Id,Guid LibraryId,Guid MissionId,long Revision,string State,
    Guid GeometryArtifactId,string GeometrySha256,string GeometryInputFingerprint,string Recipe,LidarProcessingOptions Options,
    LidarSourceChoice[] Sources,DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,long CompletedTiles=0,long? TotalTiles=null,
    long ScannedPoints=0,long RetainedPoints=0,long OutputBytes=0,Guid? ResultArtifactId=null,string? Message=null);
public sealed record LidarPointSupport(Guid SessionId,Guid ConfigurationId,string ProfileId,int ProfileVersion,int[] GeometryChunks);
public sealed record LidarRetainedPoint(LidarDecodedPoint Original,Coordinates Geographic,LidarPointSupport[] Support);
public sealed record LidarPointTile(int Version,Guid MissionId,Guid GeometryArtifactId,string GeometrySha256,string GeometryInputFingerprint,
    string Algorithm,string DecoderSource,string Recipe,ScientificSource Source,string OriginalFile,string OriginalSha256,long FirstRecord,
    int RecordsRead,LidarRetainedPoint[] Points,string EvidenceClass="Historical delivered LiDAR returns clipped to modeled flight corridor",bool GrantsOriginalScienceCredit=false);
public sealed record LidarPointCheckpoint(Guid SourceId,long FirstRecord,int RecordsRead,long RetainedPoints,Guid ArtifactId,string Sha256,long Bytes);
public sealed record LidarSourceSummary(Guid Id,string ManifestSha256,string Name,string Project,string? EptProject,string? EptNode,
    string OriginalFile,string OriginalSha256,string HorizontalCrs,string VerticalReference,string VerticalUnit,DateTimeOffset? OriginalAcquisitionUtc,
    DateTimeOffset? PublishedUtc,string License,string Citation,string[] Dimensions,double[] Scale,double[] Offset,long OriginalPointCount,string[] Limitations);
public sealed record LidarPointIndex(int Version,Guid MissionId,Guid GeometryArtifactId,string GeometrySha256,string GeometryInputFingerprint,
    string Algorithm,string DecoderSource,string Recipe,LidarProcessingOptions Options,LidarSourceSummary[] Sources,LidarPointCheckpoint[] Tiles,
    long ScannedPoints,long RetainedPoints,string State,string[] Limitations,bool GrantsOriginalScienceCredit=false);
