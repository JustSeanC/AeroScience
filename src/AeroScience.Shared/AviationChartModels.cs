namespace AeroScience.Shared;

public sealed record AviationChartSheet(string Id, Guid JobId, string Name, string Product, string Edition,
    string OriginalSha256, string State, string Explanation, Coordinates[] MainBody, string? Crs = null,
    DateTimeOffset? EffectiveUtc = null, DateTimeOffset? ExpiresUtc = null, string Currency = "Current operational validity Unknown");
public sealed record AviationChartPage(int Version, int Offset, int? NextOffset, bool RuntimeAvailable,
    AviationChartSheet[] Sheets, string Status, string[] Limitations);
