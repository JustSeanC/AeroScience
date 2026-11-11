using System.Text.Json.Serialization;

namespace AeroScience.Shared;

// Derived support geometry. These contracts are deliberately outside ScienceObservation,
// RemoteProduct and mission grading: a modeled corridor is never a new acquisition.
public sealed record TerrainSurveyOptions(
    double MaximumGapSeconds = 5, double MaximumPositionAgeSeconds = 6,
    double MaximumLegMetres = 5000, double MaximumLatitudeDegrees = 85,
    double InterpolationStepMetres = 50, double InterpolationAngleDegrees = 2,
    double MaximumRelativeHeightStep = .05, int MaximumStepsPerLeg = 128,
    double ChunkRadiusMetres = 20000, int MaximumLegsPerChunk = 64,
    int MaximumVerticesPerChunk = 20000, long MaximumArchiveRows = 500000,
    long MaximumArchiveBytes = 268435456, long MaximumOutputBytes = 67108864,
    long MaximumModeledPoses = 1000000, int MaximumChunks = 20000);

public sealed record TerrainSurveyTelemetryReference(string ReceiptId, string Sha256,
    DateTimeOffset ObservedUtc, long CollectionEventSequence, EvidenceOrigin Origin, DateTimeOffset? RecordedUtc = null);
public sealed record TerrainSurveyLeg(TerrainSurveyTelemetryReference From,
    TerrainSurveyTelemetryReference To, int InterpolatedSteps, double GeodesicMetres);
public sealed record TerrainSurveyGeometry(string Type, double[][][][] Coordinates);
public sealed record TerrainSurveyWindow(double West, double South, double East, double North,
    int PolygonIndex, string Crs = "EPSG:4326");

// Empty in P05a. Later source-backed stages must retain genuine source identity,
// dates, CRS/datum, QA and provenance rather than manufacturing aircraft returns.
public sealed record TerrainSurveySource(Guid SourceId, string Sha256, string EvidenceClass,
    DateTimeOffset? AcquisitionFromUtc, DateTimeOffset? AcquisitionToUtc,
    string HorizontalCrs, string? VerticalDatum, string[] Quality, string[] Provenance);

public sealed record TerrainSurveyHeader(Guid MissionId, Guid FlightId, string Algorithm,
    string InputFingerprint, TerrainSurveyOptions Options, DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc, TerrainSurveySource[] Sources, string[] Limitations,
    string Invalidation = "Missing/deleted/changed archive, unsupported algorithm or changed product bytes invalidate this derivation.",
    EvidenceOrigin Origin = EvidenceOrigin.ModeledGeometry, bool GrantsOriginalScienceCredit = false);
public sealed record TerrainSurveyChunk(int Index, Guid SessionId,
    EffectiveInstrumentConfiguration Configuration, PayloadConfiguration GeometryProfile, bool UsesLegacyDefaultProfile,
    DateTimeOffset FromUtc, DateTimeOffset ToUtc, TerrainSurveyLeg[] Legs,
    TerrainSurveyGeometry Geometry, TerrainSurveyWindow[] SourceSearchWindows,
    double LocalUnionSquareMetres, int Vertices, string GeometrySha256,
    string Projection = "WGS84 geodesic azimuthal-equidistant; regional union; EPSG:4326 export",
    string Classification = "Modeled sensor-support region; no measured coverage or source pixels");
public sealed record TerrainSurveyGap(string Reason, DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc, Guid? SessionId = null, long? CollectionEventSequence = null,
    string? ReceiptId = null);
public sealed record TerrainSurveyAcquisition(Guid Id, Guid SessionId, Guid ConfigurationId,
    string InstrumentId, DateTimeOffset ExposureUtc, string InputReceiptId,
    string State, Guid? OriginalProductId, string? OriginalProductSha256);
public sealed record TerrainSurveySummary(string State, long InputRecords, int Chunks,
    long Legs, long ModeledPoses, long Exclusions, long ReferencedAcquisitionIntents,
    int PeakChunkLegs, int PeakChunkVertices,
    string AreaLimitation = "Chunks may overlap. Their areas must not be summed as unique or measured scientific coverage.");

// A streaming JSON array of records: exactly one header first and one summary last.
// Incomplete/failed writes are never published by the existing artifact transaction.
public sealed record TerrainSurveyRecord([property: JsonRequired] int Version, string Kind,
    TerrainSurveyHeader? Header = null, TerrainSurveyChunk? Chunk = null,
    TerrainSurveyGap? Gap = null, TerrainSurveyAcquisition? Acquisition = null,
    TerrainSurveySummary? Summary = null) : IVersionedEvidence;
public sealed record TerrainSurveyStatus(Guid ArtifactId, string State, string? Reason);
public sealed record TerrainSurveyChunkPage(Guid ArtifactId, string InputFingerprint,
    TerrainSurveyChunk[] Chunks, int? NextAfter);
