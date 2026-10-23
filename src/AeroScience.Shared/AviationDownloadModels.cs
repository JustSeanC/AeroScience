namespace AeroScience.Shared;

// Aviation references never become scientific sources or airborne observations.
public sealed record AviationDownloadPreview(int Version, Guid Id, AviationCatalogItem Item, string SelectionHash,
    DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, long MaximumTransferBytes, long MaximumExpandedBytes,
    long? ExpectedBytes, string[] Limitations);
public sealed record AviationRetainedFile(string Name, long Bytes, string Sha256);
public sealed record AviationDownloadJob(int Version, Guid Id, AviationDownloadPreview Preview, string State,
    int Revision, long DownloadedBytes, long AttemptBytes, DateTimeOffset UpdatedUtc, bool Approved = false,
    string? Error = null, string? OriginalSha256 = null, AviationRetainedFile[]? Files = null,
    DateTimeOffset? RetryAfterUtc = null, long ChargedBytes = 0);
public sealed record AviationStorageSummary(bool Available, long? UsedBytes, long ReservedBytes, long? PhysicalFreeBytes,
    string? Error, string Policy = "Separate aviation storage. Approved per-job limits are allowances, not size estimates. Scientific budgets are unchanged.");
