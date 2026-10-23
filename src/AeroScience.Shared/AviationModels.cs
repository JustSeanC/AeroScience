namespace AeroScience.Shared;

// Reference data only. These identities never denote scientific observations or source QA.
public enum AviationProduct { Unknown, Sectional, TerminalArea, Helicopter, IfrLow, IfrHigh, Nasr, TfrNotices }
public sealed record AviationCatalogItem(int Version, string Id, AviationProduct Product, string Edition,
    string OriginalFileName, string SourceUrl, string Attribution, long? OriginalBytes = null,
    string? OriginalSha256 = null, DateTimeOffset? EffectiveUtc = null, DateTimeOffset? ExpiresUtc = null,
    GeographicBounds? Bounds = null, string? Crs = null, string GeographicVerification = "Not verified");
public sealed record AviationCatalogSnapshot(int Version, DateTimeOffset RetrievedUtc, string MetadataSha256,
    AviationCatalogItem[] Items, string[] Limitations);
public sealed record AviationTfrNotice(int Version, string NotamId, string Type, string Facility, string State,
    string Description, string CreationDateText, string OfficialUrl,
    string GeometryStatus = "Not provided by the notice list");
public sealed record AviationTfrSnapshot(int Version, DateTimeOffset RetrievedUtc, string MetadataSha256,
    AviationTfrNotice[] Notices, string[] Limitations);
public sealed record AviationCacheStatus(bool Available, Guid? StoreId, DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastAttemptUtc, string? Error, long? DatabaseBytes);
