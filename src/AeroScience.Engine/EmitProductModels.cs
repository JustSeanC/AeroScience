using System.Collections.Immutable;
using AeroScience.Shared;

namespace AeroScience.Engine;

// Internal science contracts, not a browser import API. No provider authentication
// or scientific-original acceptance is inferred from a matching local file layout.
public sealed record EmitOriginals(HdfOriginal Reflectance,HdfOriginal Uncertainty,HdfOriginal Mask);
public sealed record EmitSourceEvidence(string Role,string Collection,string Version,string Granule,string OriginalFileName,
    Guid LibraryId,string RelativePath,long Bytes,string Sha256,
    ImmutableDictionary<string,HdfAttributeValue> OriginalAttributes);
public sealed record EmitVariable(string Path,string NumericType,ImmutableArray<ulong> Dimensions,
    string Units,double FillValue,double ScaleFactor,double AddOffset,string ScalingOrigin,
    ImmutableDictionary<string,HdfAttributeValue> OriginalAttributes);
public sealed record EmitDefinition(int ContractVersion,string DecoderPolicy,string Acquisition,
    DateTimeOffset StartUtc,DateTimeOffset EndUtc,int Rows,int Columns,int Bands,
    int GridRows,int GridColumns,ImmutableArray<double> GridTransform,string OriginalCrs,GeographicBounds DerivedGltDomain,
    ImmutableArray<double> WavelengthNm,ImmutableArray<double> FwhmNm,ImmutableArray<byte> GoodWavelengths,
    ImmutableArray<string> MaskLabels,ImmutableArray<EmitSourceEvidence> Sources,
    ImmutableDictionary<string,EmitVariable> Variables,string SpatialAlignment,string Acceptance);
public sealed record EmitNativeWindow(int Row,int Column,int Rows,int Columns,int FirstBand,int BandCount);
public sealed record EmitGridWindow(int Row,int Column,int Rows,int Columns,int FirstBand,int BandCount);
public sealed record EmitQualityPolicy(bool ExcludeWater=false,bool ExcludeInterpolatedBands=true)
{
    public const string Version="emit-cloud-cirrus-dilated-spectf/1";
    public string PolicyVersion=>Version;
}
public sealed record EmitSpectrum(int Row,int Column,double? Latitude,double? Longitude,string State,
    ImmutableArray<double?> Reflectance,ImmutableArray<double?> Uncertainty,
    ImmutableArray<double?> UsableReflectance,ImmutableArray<double?> UsableUncertainty,
    ImmutableArray<string> BandStates,byte[] OriginalReflectanceBytes,byte[] OriginalUncertaintyBytes,
    byte[] OriginalQualityBytes,byte[] OriginalPackedBandMask,ImmutableArray<double?> QualityValues,
    byte[] OriginalLatitudeBytes,byte[] OriginalLongitudeBytes);
public sealed record EmitNativeResult(string DecoderPolicy,EmitQualityPolicy QualityPolicy,EmitNativeWindow Window,
    ImmutableArray<EmitSpectrum> Pixels,ImmutableArray<EmitSourceEvidence> Sources,string Support);
public sealed record EmitGridCell(int GridRow,int GridColumn,double GridLatitude,double GridLongitude,
    int? SourceRow,int? SourceColumn,string State,EmitSpectrum? Spectrum);
public sealed record EmitGeographicResult(string DecoderPolicy,EmitQualityPolicy QualityPolicy,EmitGridWindow Window,
    ImmutableArray<EmitGridCell> Cells,ImmutableArray<EmitSourceEvidence> Sources,string Support);

public sealed class EmitProductException(string code,string message):IOException(message)
{public string Code{get;}=code;}

// Opaque validated handle: callers cannot forge a definition or change the originals
// used by subsequent reads. Paths stay private; portable evidence uses relative IDs.
public sealed class EmitProduct
{
    public EmitDefinition Definition{get;}
    internal EmitOriginals Originals{get;}
    internal EmitProduct(EmitOriginals originals,EmitDefinition definition){Originals=originals;Definition=definition;}
}
