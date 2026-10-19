namespace AeroScience.Shared;

public enum MissionTemplateKind { GeneralResearch, AtmosphericProfile, GeographicSurvey, ObservationFlight, CloudStructureSurvey, AtmosphericTransect }
public enum ObservationQuality { Valid, Missing, Unavailable, Stale, Paused, Invalid, AcquisitionGap, SourceDisconnected, Disabled, Waiting }
public enum ScienceCollectionStatus { Collecting, Paused, Stopped, AwaitingSource, Unavailable }
public enum ScienceProvenance { SimulatorReported, Derived, InstrumentModeled, ExternalDataset }
public enum AutopilotEngagement { Unavailable, Disengaged, Engaged }
public sealed record ResearchWaypoint(string Name, double Latitude, double Longitude, double? TargetAltitudeFeet = null,
    string? Instructions = null, string? Objective = null, Guid? PatternId = null, Guid? Id = null, string? NavigationIdentifier = null);
public sealed record SurveyArea(string Name, Coordinates[] Vertices);
public sealed record InstrumentConfiguration(string InstrumentId, bool Enabled = false, int IntervalMs = 1000, PayloadConfiguration? Payload = null);
public sealed record MissionPlan(Guid Id, string Name, MissionTemplateKind Template = MissionTemplateKind.GeneralResearch,
    ScienceMode Mode = ScienceMode.Universal, string? Description = null, string? Objective = null,
    string? IntendedAircraft = null, string? Departure = null, string? Destination = null,
    ResearchWaypoint[]? Waypoints = null, SurveyArea[]? SurveyAreas = null, double? MinimumAltitudeFeet = null,
    double? MaximumAltitudeFeet = null, double? EstimatedDurationMinutes = null,
    InstrumentConfiguration[]? Instruments = null, string? PreflightNotes = null, DateTimeOffset? UpdatedUtc = null, PatternDefinition[]? Patterns = null,
    string FlightRules = "VFR", double? CruisingAltitudeFeet = null, AtmosphericMissionSettings? AtmosphericSettings = null,SensorPlanningSettings? SensorPlanning=null);
public sealed record MissionTemplate(MissionTemplateKind Id, string Name, string Objective,
    InstrumentConfiguration[] Instruments, string[] NoteCategories);
public sealed record MeasurementDefinition(string Id, string Name, string Unit, string SimulatorVariable);
public sealed record InstrumentDefinition(string Id, string Name, string Description, string DataSource,
    MeasurementDefinition[] Measurements, int MinimumIntervalMs = 1000);
public sealed record AtmosphericFrame(DateTimeOffset TimestampUtc, double? TemperatureCelsius, double? PressureInHg,
    double? WindSpeedKnots, double? WindDirectionTrueDegrees, double? DensitySlugsPerCubicFoot,
    ObservationQuality Quality = ObservationQuality.Valid, string? Message = null);
public sealed record MeasurementReading(string MeasurementId, double? Value, string Unit,
    ScienceProvenance Provenance = ScienceProvenance.SimulatorReported, ObservationQuality Quality = ObservationQuality.Valid, double? DisplayValue = null, string? DisplayUnit = null);
public sealed record ScienceObservation(long Id, Guid DatasetId, Guid MissionId, string InstrumentId,
    DateTimeOffset TimestampUtc, string MeasurementId, double Value, string Unit, ScienceProvenance Provenance,
    string Source, double? Latitude, double? Longitude, double? AltitudeMslFeet, ObservationQuality Quality,
    DateTimeOffset? PositionTimestampUtc = null);
public sealed record ScienceDataset(Guid Id, Guid LibraryId, Guid MissionId, string MissionName, string InstrumentId,
    DateTimeOffset CreatedUtc, string RelativePath, long ObservationCount, string ProvenanceJson);
public sealed record InstrumentState(string InstrumentId, bool Enabled, int IntervalMs, ObservationQuality Quality,
    DateTimeOffset? LastSuccessfulUtc, long ObservationCount, long RejectedObservations, long MissingIntervals,
    double? ActualAverageIntervalSeconds, MeasurementReading[] Measurements, string? Message = null, long AcquisitionSets = 0,string? PayloadProfileName=null,string? PayloadProfileId=null);
public sealed record GuidanceMode(string? Mode, double? Target = null, string? Unit = null,
    string? Armed = null, string? Status = null);
public sealed record FlightGuidance(DateTimeOffset TimestampUtc, AutopilotEngagement Autopilot,
    GuidanceMode Lateral, GuidanceMode Vertical, GuidanceMode Speed,
    ObservationQuality Quality = ObservationQuality.Valid, string Source = "MSFS 2024 standard SimConnect variables");
public sealed record ResearchState(FlightGuidance? Guidance, AtmosphericFrame? Atmosphere, InstrumentState[] Instruments,
    Guid? PlanId = null, string? Template = null, string? ScienceStorageError = null, ScienceCollectionStatus CollectionStatus = ScienceCollectionStatus.Stopped,
    bool IntentionallyPaused = false, DateTimeOffset? CollectionPausedUtc = null, double IntentionalPauseSeconds = 0, ScienceUnitPreferences? Units = null);
public sealed record MissionResearch(MissionPlan? PlanSnapshot, InstrumentConfiguration[] Instruments,
    string? Objective, string Template, MissionNote[] Notes, InstrumentState[]? Diagnostics = null);
public sealed record MissionMarker(Guid Id, Guid MissionId, string Category, DateTimeOffset TimestampUtc,
    double? Latitude, double? Longitude, double? AltitudeMslFeet, string? Notes, string? ScientificContextJson = null);
public sealed record MarkerRequest(string Category = "observation", string? Notes = null);
public sealed record MissionNote(Guid Id, Guid MissionId, string Phase, string Text, DateTimeOffset TimestampUtc);
public sealed record NoteRequest(string Text, string Phase = "postflight");
public sealed record TrackPoint(long Sequence, DateTimeOffset TimestampUtc, double Latitude, double Longitude,
    double AltitudeMslFeet, bool BreakBefore = false);
public sealed record TrackPage(TrackPoint[] Points, long NextSequence, bool HasMore, long TotalSamples, bool Downsampled);
public sealed record MeasurementSummary(string MeasurementId, string Unit, long Count, double Minimum,
    double Maximum, double Mean, DateTimeOffset FirstUtc, DateTimeOffset LastUtc);
public sealed record FlightStatistics(long Samples, double? MinimumAltitudeFeet, double? MaximumAltitudeFeet,
    double? MeanAltitudeFeet, double? MinimumIasKnots, double? MaximumIasKnots, double? MeanIasKnots,
    double? MinimumMach, double? MaximumMach, double? MeanMach, long MachSamples, long GapEvents,
    double? ActualAverageIntervalSeconds);
public sealed record ScienceStorageEstimate(long? EstimatedBytes, long? AvailableBytes, bool CapacityWarning,
    string Basis, string[] Limitations, Dictionary<string, long> EstimatedObservationCounts);
public sealed record DataClearPreview(string Kind, string Directory, long Missions, long Plans, long Datasets,
    long Observations, long ApproximateBytes, string Removes, string Preserves, string ConfirmationPhrase,
    long ManagedProductFiles = 0, long ManagedProductBytes = 0, string[]? ProductPathPreview = null,
    long ProtectedItems = 0, long ManagedCacheFiles = 0);

public enum FlightPatternKind { Lawnmower, CreepingLine, ExpandingSquare, Racetrack, Orbit, Box, RadialSpokes, StraightTransect }
public sealed record PatternDefinition(Guid Id, FlightPatternKind Kind, Coordinates Center, double HeadingDegrees = 0,
    double LengthMetres = 10000, double WidthMetres = 5000, double SpacingMetres = 1000, double RadiusMetres = 2500,
    int Legs = 6, int Orbits = 1, bool Clockwise = true, double? TargetAltitudeFeet = null, string? Notes = null,string FitMode="envelope",Coordinates[]? ClippingPolygon=null);
public sealed record PatternRouteSegment(int Start,int End,string Role);
public sealed record PatternPreview(PatternDefinition Definition, ResearchWaypoint[] Waypoints, double DistanceMetres, string Method,PatternRouteSegment[]? Segments=null,double? ScanDistanceMetres=null,double? TransitionDistanceMetres=null);
public sealed record InstrumentPreset(Guid Id, string Name, string? Description, InstrumentConfiguration[] Instruments, DateTimeOffset? UpdatedUtc = null);
public sealed record PresetResolution(InstrumentConfiguration[] Instruments, string[] Warnings);
public sealed record AnalysisSettings(double WaypointToleranceMetres = 1000, double MaximumGapSeconds = 5, double? SwathWidthMetres = null);
public sealed record WaypointComparison(int Index, string Name, bool Reached, double? ClosestDistanceMetres, DateTimeOffset? ReachedUtc, double? AltitudeErrorFeet);
public sealed record RouteDeviation(DateTimeOffset TimestampUtc, double DistanceMetres, int Segment);
public sealed record SurveyComparison(string Name, long SamplesInside, double TraversedMetres, string CoverageExplanation);
public sealed record PlanComparison(AnalysisSettings Settings, double PlannedDistanceMetres, double RecordedDistanceMetres,
    long ExcludedGaps, WaypointComparison[] Waypoints, RouteDeviation[] Deviations, SurveyComparison[] Surveys, string[] Limitations, RouteDeviationStatistics? DeviationStatistics = null, SegmentComparison[]? Segments = null);
public sealed record SciencePauseInterval(DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, double? Seconds, bool Interrupted);

public sealed record RouteDeviationStatistics(long Samples, double? MeanMetres, double? MaximumMetres);
public sealed record SegmentComparison(int Index, string From, string To, bool ObservedWithinTolerance, long Samples, long ExcludedGaps, double? MaximumDeviationMetres);
