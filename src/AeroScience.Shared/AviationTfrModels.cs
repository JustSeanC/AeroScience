namespace AeroScience.Shared;

// Official horizontal reference evidence, never a live clearance or scientific observation.
public sealed record AviationTfrBoundary(int Version, string Id, string SourceFeatureId, long SourceGeometryId,
    string NotamId, string NotamKey, string Title, string Facility, string State, string Type,
    string ModificationText, Coordinates[][][] Polygons, string OfficialUrl,
    string SegmentStatus = "Official geometry ID retained; NOTAM segment identity unverified",
    string TimeStatus = "Exact UTC activation and cancellation status Unknown",
    string AltitudeStatus = "Altitude limits and datum Unknown");
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
    string[] Limitations);
