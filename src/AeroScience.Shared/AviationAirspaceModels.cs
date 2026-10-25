namespace AeroScience.Shared;

// Published aviation reference, not live activation, scientific observations or flight clearance.
public sealed record AviationAltitudeLimit(double? Value, string? Unit, string Reference,
    string RawValue, string RawUnit, string RawReference, string Description, string Status = "Unknown");
public sealed record AviationAirspaceSchedule(string Code, string Remarks, string Dst,
    string UtcOffset, string Interpretation);
public sealed record AviationAirspaceFeature(int Version, string Id, string Kind, string Name,
    Coordinates[][][] Polygons, Dictionary<string, string> Attributes,
    AviationAltitudeLimit Lower, AviationAltitudeLimit Upper, AviationAirspaceSchedule Schedule,
    int OriginalVertices, double DisplayToleranceDegrees, AviationAirspaceVolume[]? Volumes = null);
// Native ordered prisms, not a flattened restriction. Missing boundary inclusion stays Unknown.
public sealed record AviationAirspaceVolume(int Version,int SourceComponent,int Sequence,string Operation,
    string? VolumeId,string? SurfaceId,AviationAltitudeLimit Lower,AviationAltitudeLimit Upper,
    bool? LowerIncluded=null,bool? UpperIncluded=null,Coordinates[][][]? Polygons=null);
public sealed record AviationVolumeFinding(int SourceComponent,string Operation,string Horizontal,string Vertical,string Membership,string Explanation);
public sealed record AviationVolumeAssessment(int Version,string Outcome,AviationVolumeFinding[] Components,string[] Limitations);
public sealed record AviationAirspacePage(int Version, AviationVectorDataset Dataset, string Status,
    int Offset, int? NextOffset, int BoundingCandidates, int ReturnedVertices,
    AviationAirspaceFeature[] Features, string[] Limitations);
