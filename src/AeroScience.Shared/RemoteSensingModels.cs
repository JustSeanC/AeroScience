namespace AeroScience.Shared;

public enum RemoteKind { Thermal, Multispectral, Hyperspectral, Terrain }
public sealed record RasterGrid(int Width, int Height, string Crs, double OriginX, double OriginY, double PixelWidth, double PixelHeight);
public sealed record SourceBand(string Id, string File, string Format, int Channel = 0, string Unit = "1", double Scale = 1,
    double Offset = 0, double? NoData = null, double? WavelengthNm = null, bool BadBand = false, string? HeaderFile = null,
    string? SceneId = null);
public sealed record ScientificSourceManifest(int FormatVersion, string Name, RemoteKind Kind, string Provider,
    string License, string Citation, string SceneId, DateTimeOffset? ObservationUtc, string VerticalDatum,
    SourceBand[] Bands, DateTimeOffset? PublicationUtc = null, string? Notes = null);
public sealed record ManagedSourceFile(string RelativePath, long Bytes, string Sha256);
public sealed record ScientificSource(Guid Id, ScientificSourceManifest Manifest, DateTimeOffset ImportedUtc,
    Dictionary<string, RasterGrid> Grids, ManagedSourceFile[] Files, string ManifestSha256, string RelativeDirectory);
public sealed record SourceAvailability(ScientificSource Source, bool FilesAvailable, bool? CoversPoint, string? Error);
public sealed record SensorMount(double HorizontalFovDegrees = 20, double VerticalFovDegrees = 20,
    double ForwardDegrees = 0, double RightDegrees = 0, bool Swath = false);
public sealed record ObservationFootprint(Coordinates Center, Coordinates[] Boundary, double HeightAbovePlaneMetres,
    string HorizontalCrs, string VerticalReference, string Method, string[] Limitations);
public sealed record RemoteAcquisitionRequest(Mission Mission, MissionResearch Research, InstrumentConfiguration Configuration,
    TelemetryFrame Frame, long Generation, AtmosphericFrame? Atmosphere = null);
public sealed record RemoteProduct(Guid Id, Guid DatasetId, Guid LibraryId, Guid MissionId, string InstrumentId, Guid SourceId,
    DateTimeOffset FlightPacketUtc, DateTimeOffset ProcessedUtc, DateTimeOffset? SourceObservationUtc,
    ObservationFootprint Footprint, string RelativePath, long Bytes, string Sha256, int Width, int Height,
    string[] AvailableViews, string Provenance, string Quality, string[] Warnings,
    ScienceProvenance SourceClass = ScienceProvenance.ExternalDataset);
public sealed record RemoteBandValues(string Id, string Unit, double? WavelengthNm, double?[] Values,
    string SamplingMethod, RasterGrid OriginalGrid);
public sealed record RemoteProductData(int FormatVersion, RemoteProduct Product, ScientificSource Source,
    Coordinates[] SampleLocations, RemoteBandValues[] Bands, Dictionary<string, double?[]> Derived,
    string[] ProcessingMethods, ScientificEvidenceContext? Evidence = null);
public sealed record ScientificEvidenceContext(int FormatVersion, TelemetryFrame Aircraft, AtmosphericFrame? Atmosphere,
    InstrumentConfiguration AcquisitionSettings, MissionPlan? PlanSnapshot, ScienceMode Mode, string TimestampBasis, SensorMount? Mount = null);
public sealed record RemoteInstrumentStatus(string InstrumentId, ObservationQuality Quality, string Message,
    DateTimeOffset? LastSuccessfulUtc, long Products, long Rejected, long MissingIntervals, bool Processing = false);
public sealed record RemoteStorageEstimate(long Acquisitions, int Width, int Height, int Bands, int BytesPerValue,
    long RawBytesPerAcquisition, long ConservativeOutputBytes, long TemporaryBytes, string Basis);
