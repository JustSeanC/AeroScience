namespace AeroScience.Shared;

// Official horizontal reference evidence, never a live clearance or scientific observation.
public sealed record AviationTfrBoundary(int Version, string Id, string SourceFeatureId, long SourceGeometryId,
    string NotamId, string NotamKey, string Title, string Facility, string State, string Type,
    string ModificationText, Coordinates[][][] Polygons, string OfficialUrl,
    string SegmentStatus = "Official geometry ID retained; NOTAM segment identity unverified",
    string TimeStatus = "Exact UTC activation and cancellation status Unknown",
    string AltitudeStatus = "Altitude limits and datum Unknown",
    AviationTfrOperationalView? Operational = null);
public sealed record AviationTfrGeometrySnapshot(int Version, DateTimeOffset RetrievedUtc,
    string NoticeSha256, string GeometrySha256, string NoShapeSha256, string[] NoticeIds, string[] NoShapeNoticeIds,
    AviationTfrBoundary[] Boundaries, int OmittedFeatures, string[] Limitations, AviationTfrNoticeMetadata[] NoticeInventory);
public sealed record AviationTfrNoticeMetadata(string NotamId,string Title,string Facility,string State,string Type,string OfficialUrl);
public sealed record AviationTfrBoundaryPage(int Version, string Status, AviationCacheStatus? Cache,
    DateTimeOffset? RetrievedUtc, bool SnapshotComplete, int Offset, int? NextOffset, int BoundingCandidates,
    int ReturnedVertices, AviationTfrBoundary[] Boundaries, int NoticesWithoutBoundary, string[] Limitations);
public sealed record AviationTfrScreenRequest(int Version, Coordinates[] Route, Coordinates[]? Area = null,
    DateTimeOffset? PlannedStartUtc = null, DateTimeOffset? PlannedEndUtc = null,
    double? MinimumMslFeet = null, double? MaximumMslFeet = null, string TimeBasis = "Unspecified");
public sealed record AviationTfrScreenResult(int Version, string Outcome, string Explanation,
    string[] PotentialNotamIds, DateTimeOffset? SourceRetrievedUtc, string Freshness, bool SnapshotComplete,
    string[] Limitations, AviationTfrScreenFinding[]? Findings = null);

// Separate, versioned evidence in the existing aviation cache. Raw public originals never go to browser APIs.
public sealed record AviationTfrOperationalSnapshot(int Version, string NotamId, string NoticeRevision,
    DateTimeOffset RetrievedUtc, byte[] XmlOriginal, byte[] AixmOriginal, string XmlSha256, string AixmSha256);
public sealed record AviationTfrWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record AviationTfrLevel(double? Value, string Unit, string Reference, string Inclusion);
public sealed record AviationTfrOperationalArea(string Id, string Name, Coordinates[][][]? Polygons,
    AviationTfrWindow[] Windows, string ScheduleStatus, AviationTfrLevel Lower, AviationTfrLevel Upper,
    string WindowStatus = "Unknown");
public sealed record AviationTfrInterpretation(int Version, string NotamId, string WorkIdentity, string IssuedText,
    string LifecycleStatus, AviationTfrOperationalArea[] Areas, string Facility);
public sealed record AviationTfrOperationalView(int Version, string NotamId, string Status,
    DateTimeOffset? RetrievedUtc, DateTimeOffset? LastAttemptUtc, string? Error,
    string? XmlSha256, string? AixmSha256, string XmlUrl, string AixmUrl,
    string LifecycleStatus, AviationTfrOperationalArea[] Areas, string[] Limitations,
    string? WorkIdentity = null, string? IssuedText = null);
public sealed record AviationTfrScreenFinding(string NotamId, string BoundaryId, string? AreaId,
    string Horizontal, string Time, string Altitude, string Outcome, string Explanation,
    DateTimeOffset? OperationalRetrievedUtc, string? XmlSha256, string? AixmSha256);
public sealed record AviationTfrOperationalRequest(string NotamId);
