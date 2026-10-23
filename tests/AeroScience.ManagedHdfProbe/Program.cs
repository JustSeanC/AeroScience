using PureHDF;
using System.Security.Cryptography;
using PureHDF.Selections;
using System.Text.Json;
var fixture=Path.Combine(AppContext.BaseDirectory,"synthetic-netcdf4.nc");
using(var input=File.OpenRead(fixture))if(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()!="80d927d8b2c6dc53e43f531328332d574c9f0d067f66a105512048c6e3e5358d")throw new Exception("Synthetic fixture bytes changed.");
using var file = H5File.OpenRead(fixture);
var cube = file.Dataset("reflectance");
var window = cube.Read<float[]>(fileSelection: new HyperslabSelection(3, new ulong[]{1,2,0}, new ulong[]{1,1,4}));
if(!window.SequenceEqual(new float[]{.20f,.21f,-9999f,.23f}))throw new Exception("Native compressed NetCDF window differs.");
var fill = cube.Attribute("_FillValue").Read<float>();if(fill!=-9999)throw new Exception("Fill differs.");
var wavelength = file.Dataset("sensor_band_parameters/wavelengths").Read<float[]>();
if(!wavelength.SequenceEqual(new float[]{400,500,700,900}))throw new Exception("Group wavelength mismatch.");
var unit = file.Dataset("sensor_band_parameters/wavelengths").Attribute("units").Read<string>();if(unit!="nm")throw new Exception("Units differ.");
var x = file.Dataset("location/glt_x").Read<int[]>();var y = file.Dataset("location/glt_y").Read<int[]>();
if(x[4]!=0||y[4]!=0||x[5]-1!=2||y[5]-1!=1)throw new Exception("GLT reference bytes differ.");
var originalStorageResult=new{evidence="Synthetic netCDF-C fixture, compressed HDF5 groups/window/fill only; not EMIT original or product integration",window,fill,wavelength,unit,x,y};

var folder=AppContext.BaseDirectory;
using(var provenance=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"emit-fixture-provenance.json"))))
foreach(var source in provenance.RootElement.GetProperty("files").EnumerateArray())
{
    var name=source.GetProperty("file").GetString()!;if(Path.GetFileName(name)!=name)throw new Exception("Unsafe fixture name");
    using var stream=File.OpenRead(Path.Combine(folder,name));if(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=source.GetProperty("sha256").GetString())throw new Exception("Synthetic original hash changed");
}
var emitRfl=Path.Combine(folder,"synthetic-emit-reflectance.nc");var emitUnc=Path.Combine(folder,"synthetic-emit-uncertainty.nc");var emitMask=Path.Combine(folder,"synthetic-emit-mask.nc");
var before=new[]{emitRfl,emitUnc,emitMask}.Select(p=>SHA256.HashData(File.ReadAllBytes(p))).ToArray();
var tests=0;void Assert(bool passed,string explanation){if(!passed)throw new Exception(explanation);tests++;}
var watch=System.Diagnostics.Stopwatch.StartNew();EmitWindowReader.Spectrum sample;
using(var reader=new EmitWindowReader(emitRfl,emitUnc,emitMask))
{
    Assert(reader.Shape.SequenceEqual(new ulong[]{2,3,5}),"Native cube dimensions");
    Assert(reader.Wavelengths.SequenceEqual(new float[]{400,500,700,900,1100})&&reader.Fwhm.SequenceEqual(new float[]{8,8,9,10,10}),"Original wavelengths/FWHM");
    Assert(reader.GoodWavelengths.SequenceEqual(new[]{1,1,0,1,1}),"Original good-wavelength flags");
    Assert(reader.QualityNames.SequenceEqual(new[]{"Synthetic Cloud Flag","Synthetic Other Flag"}),"NetCDF variable-length string mask names");
    sample=reader.ReadSpectrum(1,0);
    Assert(sample.SourceColumn==1&&sample.SourceRow==0,"GLT one-based to zero-based mapping");
    Assert(sample.RawReflectance.SequenceEqual(new float[]{.05f,.06f,.07f,.08f,.09f}),"Native reflectance unchanged");
    Assert(sample.RawUncertainty.SequenceEqual(new float[]{.006f,.007f,.008f,.009f,.010f}),"Dataset cache collision: uncertainty must not return reflectance chunk");
    Assert(sample.RawQuality.SequenceEqual(new float[]{1,0})&&sample.RawPackedBandMask.SequenceEqual(new byte[]{128}),"Original numeric QA/packed bytes");
    Assert(sample.BandMasked.SequenceEqual(new[]{true,false,false,false,false}),"Packed mask MSB-first alignment");
    Assert(sample.NativeLatitude==37.99&&sample.NativeLongitude==-119.98,"Original swath latitude/longitude");
    Assert(Math.Abs(sample.GeographicPixelLatitude-37.995)<1e-12&&Math.Abs(sample.GeographicPixelLongitude+119.985)<1e-12,"GLT geographic cell center distinct from native swath location");
    Assert(reader.ReadSpectrum(3,0).State=="Not geolocated","Zero GLT is never sampled as source zero");
    Assert(reader.ReadSpectrum(3,1).State=="Invalid GLT reference","Out-of-range GLT is never clamped");
    var fillSample=reader.ReadSpectrum(2,0);Assert(fillSample.RawReflectance[2]==-9999&&fillSample.Reflectance[2] is null&&fillSample.RawUncertainty[2]==-9999&&fillSample.Uncertainty[2] is null,"Native fill retained and separately unavailable");
    var last=reader.ReadSpectrum(2,1);Assert(last.BandMasked.SequenceEqual(new[]{false,false,false,false,true}),"Trailing unused packed bits do not create spectral channels");
    for(var index=0;index<1000;index++){var a=reader.ReadSpectrum(1,0);if(a.RawUncertainty[0]!=.006f||a.RawReflectance[0]!=.05f)throw new Exception("Cross-dataset cache contamination after repeated samples");}
    Assert(true,"Repeated bounded samples maintain separate caches");
    using var cancelled=new CancellationTokenSource();cancelled.Cancel();try{reader.ReadSpectrum(0,0,cancelled.Token);throw new Exception("Cancelled sampling completed");}catch(OperationCanceledException){tests++;}
    try{reader.ReadSpectrum(-1,0);throw new Exception("Invalid negative grid index accepted");}catch(ArgumentOutOfRangeException){tests++;}
}
try{using var mismatch=new EmitWindowReader(emitRfl,emitUnc,Path.Combine(folder,"synthetic-emit-mismatch.nc"));throw new Exception("Mismatched companion accepted");}catch(InvalidDataException){tests++;}
try{using var oversized=new EmitWindowReader(Path.Combine(folder,"synthetic-emit-oversized.nc"),emitUnc,emitMask);throw new Exception("Oversized decompressed source chunk accepted");}catch(NotSupportedException){tests++;}
var after=new[]{emitRfl,emitUnc,emitMask}.Select(p=>SHA256.HashData(File.ReadAllBytes(p))).ToArray();Assert(before.Zip(after).All(pair=>pair.First.SequenceEqual(pair.Second)),"Original file bytes changed");
Console.WriteLine(JsonSerializer.Serialize(new{evidence="Synthetic NetCDF-C semantic/window prototype + actual managed process; not NASA original compatibility or production import",originalStorageResult,semanticChecksPassed=tests,sample,elapsedSeconds=watch.Elapsed.TotalSeconds,peakResidentBytes=System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,productionDependencyAdded=false,limitations=new[]{"Strict experimental metadata resolver; genuine version-specific NASA attribute/type/filter compatibility remains unverified","Mask values retained; no scientific Good Data/accuracy/activation or in-flight credit inferred","No production import, Library/downloader/UI/export integration or protected original acceptance"}}));
