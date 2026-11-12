using AeroScience.Shared;

namespace AeroScience.Missions;

public readonly record struct NativeRasterValue(double DecodedSourceValue,double Value);
public sealed record TerrainElevationOptions(int TileSize=16,long MaximumCandidateCells=2000000,
    int MaximumTiles=8192,long MaximumOutputBytes=134217728,long MaximumScratchBytes=268435456,
    long MaximumVerifiedSourceBytes=4294967296,int MaximumShapesPerTile=512,int MaximumProjectedVertices=50000,
    int FirstGeometryChunk=1,int? LastGeometryChunk=null);
public sealed record TerrainElevationSourceChoice(Guid SourceId,string ManifestSha256,string BandId="elevation",
    string Surface="unknown",bool LocalDefinitionAcknowledged=false,bool UnverifiedDatumAcknowledged=false,
    string? QualityBandId=null,double[]? AcceptedQualityValues=null);
public sealed record TerrainElevationRequest(Guid GeometryArtifactId,TerrainElevationSourceChoice[] Sources,TerrainElevationOptions? Options=null);
public sealed record TerrainElevationSource(ScientificSource Original,SourceBand Band,RasterGrid Grid,
    string Surface,bool DatumVerified,bool UserDeclaredDefinition,SourceBand? QualityBand,double[] AcceptedQualityValues,string[] Limitations);
public sealed record TerrainElevationSourceReview(Guid SourceId,string Name,string State,string[] Reasons,
    string Surface,string VerticalDatum,DateTimeOffset? ObservationUtc,string GridResolution,string ManifestSha256,
    SourceBand[] Bands,TerrainElevationGridSupport[] PotentialGridSupport);
public sealed record TerrainElevationGridSupport(string BandId,int GeometryChunk,TerrainSurveyGeometry NativeMask);
public sealed record TerrainElevationReview(Guid GeometryArtifactId,string GeometrySha256,string InputFingerprint,
    Guid MissionId,TerrainElevationSourceReview[] Sources,TerrainSurveyWindow[] SearchWindows,int? NextChunk,
    string[] Limitations,string? JobId=null,long? NextSource=null,long? ThroughSource=null,int[]? GeometryChunks=null);
public sealed record TerrainElevationJob(Guid Id,Guid LibraryId,Guid MissionId,long Revision,string State,
    Guid GeometryArtifactId,string GeometrySha256,string GeometryInputFingerprint,string Recipe,
    TerrainElevationOptions Options,TerrainElevationSource[] Sources,DateTimeOffset CreatedUtc,DateTimeOffset UpdatedUtc,
    long CompletedTiles=0,long? TotalTiles=null,long ValidCells=0,long MissingCells=0,long OutputBytes=0,
    Guid? ResultArtifactId=null,string? Message=null);
public sealed record TerrainElevationControl(Guid RequestId,long ExpectedRevision,string Action);
public sealed record TerrainElevationSupport(Guid SessionId,Guid ConfigurationId,string ProfileId,int ProfileVersion,
    int[] GeometryChunks,TerrainSurveyGeometry Mask);
public sealed record TerrainElevationCell(int Column,int Row,double? DecodedSourceValue,double? Value,string Quality,
    double NativeCellSupportFraction,TerrainElevationSupport[] Support);
public sealed record TerrainElevationTile(int Version,Guid MissionId,Guid FlightId,Guid GeometryArtifactId,string GeometrySha256,
    string GeometryInputFingerprint,string Algorithm,TerrainElevationSource Source,int TileColumn,int TileRow,int TileSize,
    TerrainElevationCell[] Cells,string MaskCoordinates="Original native pixel coordinates: x=column, y=row; source affine grid and CRS retained",
    string EvidenceClass="Post-flight historical source-backed terrain; not a new airborne acquisition",bool GrantsOriginalScienceCredit=false);
public sealed record TerrainElevationCheckpoint(Guid SourceId,int TileColumn,int TileRow,Guid ArtifactId,string Sha256,long ValidCells,long MissingCells,long Bytes);
public sealed record TerrainElevationIndex(int Version,Guid MissionId,Guid GeometryArtifactId,string GeometrySha256,
    string GeometryInputFingerprint,string Algorithm,TerrainElevationOptions Options,string Recipe,TerrainElevationSource[] Sources,TerrainElevationCheckpoint[] Tiles,
    long ValidCells,long MissingCells,string State,string[] Limitations,bool GrantsOriginalScienceCredit=false);
