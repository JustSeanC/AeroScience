using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroScience.Shared;

public enum TelemetrySource { Simulated, Simulator }
public enum ConnectionState { Initializing, Connected, Disconnected, Unavailable }
public enum MissionStatus { Recording, Completed, Interrupted }
public enum ScienceMode { Universal, Realistic }

public sealed record TelemetryFrame(
    DateTimeOffset TimestampUtc, TelemetrySource Source, string Aircraft,
    double Latitude, double Longitude, double AltitudeMslFeet, double? AltitudeAglFeet,
    double IndicatedAirspeedKnots, double TrueAirspeedKnots, double GroundspeedKnots,
    double HeadingDegrees, double GroundTrackDegrees, double PitchDegrees,
    double BankDegrees, double VerticalSpeedFeetPerMinute, bool IsPaused = false, double? MachNumber = null,
    FlightGuidance? Guidance = null, EnvironmentFrame? Environment = null, AtmosphericFrame? Atmosphere = null)
{
    public bool IsValid => TimestampUtc.Offset == TimeSpan.Zero && !string.IsNullOrWhiteSpace(Aircraft)
        && double.IsFinite(Latitude) && Latitude is >= -90 and <= 90
        && double.IsFinite(Longitude) && Longitude is >= -180 and <= 180
        && new[] { AltitudeMslFeet, IndicatedAirspeedKnots, TrueAirspeedKnots, GroundspeedKnots,
            HeadingDegrees, GroundTrackDegrees, PitchDegrees, BankDegrees, VerticalSpeedFeetPerMinute }
            .All(double.IsFinite)
        && (AltitudeAglFeet is null || double.IsFinite(AltitudeAglFeet.Value));
}

public sealed record ConnectionInfo(ConnectionState State, TelemetrySource Source, string Message,
    DateTimeOffset ChangedUtc);
public sealed record TelemetryUpdate(ConnectionInfo Connection, TelemetryFrame? Frame,
    AtmosphericFrame? Atmosphere = null, FlightGuidance? Guidance = null, EnvironmentReading? Environment = null);
public sealed record Coordinates(double Latitude, double Longitude);
public sealed record Mission(Guid Id, string Name, string? Aircraft, TelemetrySource? Source,
    DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, MissionStatus Status,
    Coordinates? Departure, Coordinates? Arrival, int SampleCount, double? MinimumAltitudeFeet,
    double? MaximumAltitudeFeet, ScienceMode Mode = ScienceMode.Universal)
{
    public double DurationSeconds => Math.Max(0, ((EndedUtc ?? DateTimeOffset.UtcNow) - StartedUtc).TotalSeconds);
}
public sealed record MissionEvent(long Id, DateTimeOffset TimestampUtc, string Kind, string Message);
public sealed record MissionDetail(Mission Mission, IReadOnlyList<MissionEvent> Events);
public sealed record SamplingDiagnostics(long ReceivedUpdates, long CommittedSamples, long SkippedUpdates,
    long MissingIntervals, double? AchievedHz, DateTimeOffset? FirstSampleUtc, DateTimeOffset? LastSampleUtc,
    double? MissionAverageHz = null, double? FirstSampleDelaySeconds = null);
public sealed record EngineSnapshot(ConnectionInfo Connection, TelemetryFrame? Telemetry,
    Mission? ActiveMission, int SamplingIntervalMs, string Version = "0.4.0", SamplingDiagnostics? Sampling = null,
    ResearchState? Research = null, MapLayerState? Maps = null, RecordingStopInfo? RecordingStop = null);
public sealed record RecordingStopInfo(string Code, string Message, Guid MissionId, DateTimeOffset TimestampUtc);
public sealed record MapLayerState(string Revision, bool OnlineEnabled, string Layer);
public sealed record StartMissionRequest(string Name, ScienceMode Mode = ScienceMode.Universal,
    Guid? PlanId = null, InstrumentConfiguration[]? Instruments = null, string? Objective = null);
public sealed record InstrumentMissionContext(Guid MissionId, ScienceMode Mode);
public sealed record DatasetReference(Guid DatasetId, Guid LibraryId, string RelativePath, string MetadataJson,
    bool Available = true);

// Legacy provenance labels remain readable in historical archives.
public enum ObservationProvenance { SimulatorTelemetry, ExternalDataset, SyntheticMeasurement, SimulatedInstrument }
public interface IScienceInstrument
{
    InstrumentDefinition Definition { get; }
    IReadOnlyList<MeasurementReading> Read(AtmosphericFrame source);
}

public static class WireJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
