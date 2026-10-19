namespace AeroScience.Shared;

public enum ScientificCapability { Elevation, SurfaceModel, LidarPoints, MultispectralReflectance, HyperspectralRadiance, HyperspectralReflectance, SurfaceTemperature, LandCover, AtmosphericState, Radar }
public enum SensorGeometryKind { FrameCamera, Pushbroom, ScanningLidar }
public sealed record SpectralChannel(string Id, double? CentreNanometres, double? WidthNanometres);
public sealed record InstrumentProfile(string Id, int Version, string Name, string InstrumentId, SensorGeometryKind Geometry,
    double HorizontalFovDegrees, double VerticalFovDegrees, int? CrossTrackPixels, int? AlongTrackPixels,
    ScientificCapability[] CompatibleCapabilities, string[] References, string[] Limitations,
    SpectralChannel[]? Channels = null, double? IfovMilliradians = null, double? LineRateHz = null,
    double? MeasurementRateHz = null, double? MinimumOperatingCelsius = null, double? MaximumOperatingCelsius = null,
    double? PixelPitchMicrometres = null, double? FocalLengthMillimetres = null);
public sealed record PayloadConfiguration(InstrumentProfile Profile, double ForwardDegrees = 0, double RightDegrees = 0,
    bool Custom = false, string GeometryAlgorithm = "sensor-plane-2");
public sealed record SensorPlanningRequest(InstrumentConfiguration Instrument, double AltitudeAglMetres,
    double GroundspeedMetresPerSecond, double SideOverlap = .3, double ForwardOverlap = .6,
    PatternDefinition? Pattern = null,Guid? TerrainSourceId=null);
public sealed record SensorPlanningSettings(string InstrumentId,double AltitudeAglMetres,double GroundspeedMetresPerSecond,
    double SideOverlap=.3,double ForwardOverlap=.6,Guid? TerrainSourceId=null);
public sealed record SensorPlanningResult(PayloadConfiguration Payload, double SwathMetres, double AlongTrackMetres,
    double TrackSpacingMetres, double? NominalCrossTrackGsdMetres, double? NominalAlongTrackGsdMetres,
    double? RecommendedIntervalSeconds, double? TheoreticalPointDensityPerSquareMetre, PatternPreview? Pattern,
    double? DurationSeconds, string[] Advisories, string Method,Coordinates[][]? ModeledSwaths=null,ObservationFootprint? TerrainPlanningFootprint=null,string? TerrainStatus=null);
public sealed record EnvironmentReading(string Id, DateTimeOffset TimestampUtc, double? Value, string Unit,
    string SimulatorVariable, ObservationQuality Quality = ObservationQuality.Valid, string? Limitation = null);
public sealed record EnvironmentFrame(EnvironmentReading[] Readings, string Source = "MSFS 2024 / SimConnect",
    string GroundVerticalReference = "Simulator surface altitude; geodetic vertical datum unspecified");
public sealed record AtmosphericMissionSettings(string Direction = "both", double AltitudeIntervalMetres = 100,
    double MaximumGapSeconds = 5, double? MinimumAltitudeFeet = null, double? MaximumAltitudeFeet = null);
public sealed record SourceCapabilitySummary(Guid SourceId, ScientificCapability[] Capabilities, string Provider,
    string Collection, string[] Limitations);
