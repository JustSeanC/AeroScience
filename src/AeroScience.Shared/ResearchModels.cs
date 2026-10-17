namespace AeroScience.Shared;

public enum MissionTemplateKind { GeneralResearch, AtmosphericProfile, GeographicSurvey, ObservationFlight }
public enum ObservationQuality { Valid, Missing, Unavailable, Stale, Paused, Invalid, AcquisitionGap, SourceDisconnected, Disabled, Waiting }
public enum ScienceProvenance { SimulatorReported, Derived, InstrumentModeled }
public enum AutopilotEngagement { Unavailable, Disengaged, Engaged }
public sealed record ResearchWaypoint(string Name, double Latitude, double Longitude, double? TargetAltitudeFeet = null,
    string? Instructions = null, string? Objective = null);
public sealed record SurveyArea(string Name, Coordinates[] Vertices);
public sealed record InstrumentConfiguration(string InstrumentId, bool Enabled = false, int IntervalMs = 1000);
public sealed record MissionPlan(Guid Id, string Name, MissionTemplateKind Template = MissionTemplateKind.GeneralResearch,
    ScienceMode Mode = ScienceMode.Universal, string? Description = null, string? Objective = null,
    string? IntendedAircraft = null, string? Departure = null, string? Destination = null,
    ResearchWaypoint[]? Waypoints = null, SurveyArea[]? SurveyAreas = null, double? MinimumAltitudeFeet = null,
    double? MaximumAltitudeFeet = null, double? EstimatedDurationMinutes = null,
    InstrumentConfiguration[]? Instruments = null, string? PreflightNotes = null, DateTimeOffset? UpdatedUtc = null);
public sealed record MissionTemplate(MissionTemplateKind Id, string Name, string Objective,
    InstrumentConfiguration[] Instruments, string[] NoteCategories);
public sealed record MeasurementDefinition(string Id, string Name, string Unit, string SimulatorVariable);
public sealed record InstrumentDefinition(string Id, string Name, string Description, string DataSource,
    MeasurementDefinition[] Measurements, int MinimumIntervalMs = 1000);
public sealed record AtmosphericFrame(DateTimeOffset TimestampUtc, double? TemperatureCelsius, double? PressureInHg,
    double? WindSpeedKnots, double? WindDirectionTrueDegrees, double? DensitySlugsPerCubicFoot,
    ObservationQuality Quality = ObservationQuality.Valid, string? Message = null);
public sealed record MeasurementReading(string MeasurementId, double? Value, string Unit,
    ScienceProvenance Provenance = ScienceProvenance.SimulatorReported, ObservationQuality Quality = ObservationQuality.Valid);
public sealed record ScienceObservation(long Id, Guid DatasetId, Guid MissionId, string InstrumentId,
    DateTimeOffset TimestampUtc, string MeasurementId, double Value, string Unit, ScienceProvenance Provenance,
    string Source, double? Latitude, double? Longitude, double? AltitudeMslFeet, ObservationQuality Quality,
    DateTimeOffset? PositionTimestampUtc = null);
public sealed record ScienceDataset(Guid Id, Guid LibraryId, Guid MissionId, string MissionName, string InstrumentId,
    DateTimeOffset CreatedUtc, string RelativePath, long ObservationCount, string ProvenanceJson);
public sealed record InstrumentState(string InstrumentId, bool Enabled, int IntervalMs, ObservationQuality Quality,
    DateTimeOffset? LastSuccessfulUtc, long ObservationCount, long RejectedObservations, long MissingIntervals,
    double? ActualAverageIntervalSeconds, MeasurementReading[] Measurements, string? Message = null);
public sealed record GuidanceMode(string? Mode, double? Target = null, string? Unit = null,
    string? Armed = null, string? Status = null);
public sealed record FlightGuidance(DateTimeOffset TimestampUtc, AutopilotEngagement Autopilot,
    GuidanceMode Lateral, GuidanceMode Vertical, GuidanceMode Speed,
    ObservationQuality Quality = ObservationQuality.Valid, string Source = "MSFS 2024 standard SimConnect variables");
public sealed record ResearchState(FlightGuidance? Guidance, AtmosphericFrame? Atmosphere, InstrumentState[] Instruments,
    Guid? PlanId = null, string? Template = null, string? ScienceStorageError = null);
public sealed record MissionResearch(MissionPlan? PlanSnapshot, InstrumentConfiguration[] Instruments,
    string? Objective, string Template, MissionNote[] Notes, InstrumentState[]? Diagnostics = null);
public sealed record MissionMarker(Guid Id, Guid MissionId, string Category, DateTimeOffset TimestampUtc,
    double? Latitude, double? Longitude, double? AltitudeMslFeet, string? Notes, string? ScientificContextJson = null);
public sealed record MarkerRequest(string Category = "observation", string? Notes = null);
public sealed record MissionNote(Guid Id, Guid MissionId, string Phase, string Text, DateTimeOffset TimestampUtc);
public sealed record NoteRequest(string Text, string Phase = "postflight");
public sealed record TrackPoint(long Sequence, DateTimeOffset TimestampUtc, double Latitude, double Longitude,
    double AltitudeMslFeet);
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
    long Observations, long ApproximateBytes, string Removes, string Preserves, string ConfirmationPhrase);
