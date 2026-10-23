namespace AeroScience.Shared;

// Original FAA reference coordinates/attributes, never scientific observations or flight controls.
public sealed record AviationVectorFeature(int Version, string Id, string Kind, string Name,
    Coordinates[] Coordinates, Dictionary<string, string> Attributes);
public sealed record AviationVectorDataset(int Version, string Id, Guid JobId, string Edition,
    string OriginalSha256, string Family, string SourceCrs, DateTimeOffset ImportedUtc,
    int Features, int OmittedRecords, string[] Limitations);
public sealed record AviationVectorPage(int Version, AviationVectorDataset? Dataset, string Status,
    int Offset, int Total, AviationVectorFeature[] Features, string[] Limitations);
