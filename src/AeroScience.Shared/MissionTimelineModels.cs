namespace AeroScience.Shared;

public enum MissionTimelineSource { FlightEvent, CollectionEvent, Telemetry, ScienceObservation, PilotMarker, ResearchNote }
public enum MissionTimelineLane { Flight, ResearchCards, ScienceSessions, Measurements, ObservationsWarnings }
public sealed record MissionTimelineQuery(DateTimeOffset? FromUtc=null,DateTimeOffset? ToUtc=null,
    MissionTimelineLane? Lane=null,Guid? CardId=null,Guid? SessionId=null,string? InstrumentId=null,
    string? Cursor=null,bool Recent=false,int Limit=50);
public sealed record MissionTimelineReference(MissionTimelineSource Source,string Id,string Sha256);
public sealed record MissionTimelinePointer(MissionTimelineReference Reference,DateTimeOffset TimestampUtc);
public sealed record MissionTimelineItem(MissionTimelineReference Reference,DateTimeOffset TimestampUtc,
    MissionTimelineLane Lane,string Title,string Summary,Guid? CardId=null,Guid? SessionId=null,
    string? InstrumentId=null,Guid? AttemptId=null,Guid? TestPointId=null,string? MeasurementId=null,
    double? Value=null,string? Unit=null,ObservationQuality? Quality=null,ScienceProvenance? Provenance=null,
    double? Latitude=null,double? Longitude=null,double? AltitudeMslFeet=null);
public sealed record MissionTimelinePage(int Version,Guid FlightId,DateTimeOffset GeneratedUtc,
    DateTimeOffset FlightStartedUtc,DateTimeOffset? FlightEndedUtc,bool Recording,bool ScienceDeleted,
    MissionTimelineItem[] Items,string? NextCursor,string[] Limitations);
