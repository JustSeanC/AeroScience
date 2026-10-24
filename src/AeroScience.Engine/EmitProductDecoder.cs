using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AeroScience.Engine;

// NASA definitions pinned in docs/research/v0.6.0-emit-product-definitions.json.
// HDF parsing stays exclusively in the existing isolated reader. No original,
// source manifest, mission, grade or Library database is written here.
public sealed partial class EmitProductDecoder(ScientificHdfReader reader)
{
    public const string Policy="emit-l2a-rfl002-uncert002-mask003-seven/1";
    public const int Bands=285,MaximumPixels=4096,MaximumSpectralValues=32768;
    private static readonly string[] Roots=["instrument","title","flight_line","product_version","time_coverage_start","time_coverage_end","geotransform","spatial_ref","Conventions","date_created","software_build_version","software_delivery_version","naming_authority","_NCProperties"];
    private static readonly string[] Attributes=["units","_FillValue","scale_factor","add_offset","long_name","standard_name"];
    private static readonly string[] MaskLabels=["Cloud Flag","Cirrus Flag","Water Flag","Dilated Cloud Flag","SpecTf-Cloud Probability","SpecTf-Cloud Flag","SpecTf-Buffer Distance"];
    [GeneratedRegex(@"^EMIT_L2A_(RFL|RFLUNCERT|MASK)_(002|003)_(\d{8}T\d{6})(_\d{7}_\d{3})?\.nc$",RegexOptions.CultureInvariant)]
    private static partial Regex FileIdentity();

    public static EmitOriginals ResolveOriginals(IReadOnlyCollection<HdfOriginal> candidates)
    {
        if(candidates.Count!=3)throw Error("Companion","Select exactly one reflectance, uncertainty and compatible mask original; missing or ambiguous companions are not guessed.");
        var byRole=new Dictionary<string,HdfOriginal>();string? key=null;
        foreach(var candidate in candidates)
        {
            var m=FileIdentity().Match(candidate.RelativePath.Split('/')[^1]);
            if(!m.Success||m.Groups[2].Value!=(m.Groups[1].Value=="MASK"?"003":"002"))throw Error("UnsupportedVersion","An original filename/product version is unsupported.");
            var identity=m.Groups[3].Value+m.Groups[4].Value;if(key is not null&&key!=identity||!byRole.TryAdd(m.Groups[1].Value,candidate))throw Error("Companion","Duplicate roles or different acquisitions are not compatible companions.");key=identity;
        }
        if(byRole.Count!=3)throw Error("Companion","Three distinct original product roles are required.");return new(byRole["RFL"],byRole["RFLUNCERT"],byRole["MASK"]);
    }

    public async Task<EmitProduct> OpenAsync(EmitOriginals originals,CancellationToken token=default)
    {
        var files=new[]{originals.Reflectance,originals.Uncertainty,originals.Mask};
        if(files.Select(f=>f.LibraryId).Distinct().Count()!=1||files.Select(f=>f.RootDirectory).Distinct().Count()!=1||files.Select(f=>f.Sha256).Distinct().Count()!=3)
            throw Error("Companion","Use three distinct approved originals from the same configured Library.");
        var roles=new[]{"RFL","RFLUNCERT","MASK"};var versions=new[]{"002","002","003"};var products=new[]{"reflectance","reflectance_uncertainty","mask"};
        var sources=new List<EmitSourceEvidence>();var variables=new Dictionary<string,EmitVariable>();
        string? acquisition=null,reflectanceBuild=null,reflectanceDelivery=null;DateTimeOffset? start=null,end=null;ulong[]? shape=null,grid=null;double[]? transform=null,wavelength=null,fwhm=null;byte[]? good=null;string? crs=null;
        for(var i=0;i<3;i++)
        {
            token.ThrowIfCancellationRequested();var match=FileIdentity().Match(files[i].RelativePath.Split('/')[^1]);
            if(!match.Success||match.Groups[1].Value!=roles[i]||match.Groups[2].Value!=versions[i])throw Error("UnsupportedVersion","Only original RFL002, RFLUNCERT002 and MASK003 NetCDF filenames are supported; radiance, legacy and newer versions are not substituted.");
            var key=match.Groups[3].Value+match.Groups[4].Value;
            if(acquisition is not null&&acquisition!=key)throw Error("Companion","Original acquisition/orbit/scene identities differ.");acquisition=key;
            List<HdfWindow> requests=[new(products[i],Attributes:Attributes),new("location/lat",Attributes:Attributes),new("location/lon",Attributes:Attributes),new("location/glt_x",Attributes:Attributes),new("location/glt_y",Attributes:Attributes)];
            if(i<2){requests.Add(new("sensor_band_parameters/wavelengths",[0],[Bands],Attributes));requests.Add(new("sensor_band_parameters/fwhm",[0],[Bands],Attributes));requests.Add(new("sensor_band_parameters/good_wavelengths",[0],[Bands],Attributes));}
            else{requests.Add(new("sensor_band_parameters/mask_bands",[0],[(ulong)MaskLabels.Length]));requests.Add(new("band_mask",Attributes:Attributes));}
            var h=await reader.ReadAsync(new(1,files[i],requests.ToArray(),Roots),token);
            string Text(string name)=>String(h.RootAttributes,name);
            var version=Text("product_version");
            if(Text("instrument")!="EMIT"||Text("flight_line")!="emit"+match.Groups[3].Value.Replace('T','t')||!new[]{"V"+versions[i],"V0"+int.Parse(versions[i],CultureInfo.InvariantCulture),versions[i]}.Contains(version))
                throw Error("Identity","Original EMIT instrument, flight-line or product-version metadata differs from its filename.");
            if(!Text("Conventions").Split(',').Any(v=>v.Trim().StartsWith("CF-",StringComparison.Ordinal)))throw Error("Metadata","Original CF conventions are required; arbitrary HDF arrays are not an EMIT product.");
            var title=i switch{0=>"EMIT L2A Estimated Surface Reflectance 60 m ",1=>"EMIT L2A Estimated Surface Reflectance Uncertainty 60 m ",_=>"EMIT L2A Masks 60 m "};
            if(!Text("title").StartsWith(title,StringComparison.Ordinal))throw Error("Identity","Original product title is not the selected L2A scientific product.");
            var build=Text("software_build_version");var delivery=Text("software_delivery_version");
            if(string.IsNullOrWhiteSpace(build)||string.IsNullOrWhiteSpace(delivery))throw Error("Metadata","Original processing build/delivery provenance is unavailable.");
            if(i==0){reflectanceBuild=build;reflectanceDelivery=delivery;}
            if(i==1&&(build!=reflectanceBuild||delivery!=reflectanceDelivery))throw Error("Companion","Reflectance and uncertainty processing build/delivery revisions differ. Mask processing is versioned separately.");
            var s=Utc(Text("time_coverage_start"));var e=Utc(Text("time_coverage_end"));
            if(e<s||s.UtcDateTime.ToString("yyyyMMddTHHmmss",CultureInfo.InvariantCulture)!=match.Groups[3].Value||start is not null&&(s!=start||e!=end))throw Error("Companion","Original acquisition times differ or conflict with the scene identity.");start=s;end=e;
            var cube=h.Windows[0];Require(cube,"Float32",3);if(cube.Dimensions[2]!=(ulong)(i<2?Bands:MaskLabels.Length))throw Error("UnsupportedLayout","Unsupported spectral/mask dimension. No channel count is invented.");
            if(shape is not null&&!cube.Dimensions[..2].SequenceEqual(shape[..2]))throw Error("Companion","Native spatial dimensions differ.");shape??=cube.Dimensions;
            var variable=Variable(cube);if(variable.Units!="unitless"&&variable.Units!="1")throw Error("Units","Reflectance, uncertainty and mask must retain documented fraction/unitless units; radiance is unsupported.");
            if(i==2&&(variable.ScaleFactor!=1||variable.AddOffset!=0))throw Error("UnsupportedLayout","Scaled flag encodings are unsupported.");variables[roles[i]]=variable;
            foreach(var loc in h.Windows.Skip(1).Take(2)){Require(loc,"Float64",2);if(!loc.Dimensions.SequenceEqual(cube.Dimensions[..2]))throw Error("Companion","Native location dimensions differ from the cube.");var lv=Variable(loc);var expected=loc.Dataset.EndsWith("lat",StringComparison.Ordinal)?new[]{"degrees north","degrees_north"}:new[]{"degrees east","degrees_east"};if(!expected.Contains(lv.Units)||lv.ScaleFactor!=1||lv.AddOffset!=0)throw Error("Geolocation","Unsupported native WGS84 latitude/longitude units or scaling.");if(i>0&&lv.FillValue!=variables["RFL:"+loc.Dataset].FillValue)throw Error("Companion","Native coordinate fill definitions differ between companions.");variables[roles[i]+":"+loc.Dataset]=lv;}
            foreach(var glt in h.Windows.Skip(3).Take(2)){Require(glt,"Int32",2);if(grid is not null&&!glt.Dimensions.SequenceEqual(grid))throw Error("Companion","Companion GLT grid dimensions differ.");grid??=glt.Dimensions;var gv=Variable(glt);if(gv.FillValue!=0||gv.ScaleFactor!=1||gv.AddOffset!=0||gv.Units!="pixel location")throw Error("Geolocation","Unsupported original GLT indexing metadata; no guessed indices.");variables[roles[i]+":"+glt.Dataset]=gv;}
            var gt=Numbers(h.RootAttributes["geotransform"]);if(gt.Length!=6||gt.Any(v=>!double.IsFinite(v))||gt[1]<=0||gt[5]>=0||gt[2]!=0||gt[4]!=0)throw Error("Geolocation","Only documented geographic EMIT GLT grids are supported, not an affine original swath.");
            var sourceCrs=Text("spatial_ref");if(!Wgs84(sourceCrs))throw Error("Geolocation","Unsupported GLT CRS; no datum or projection is guessed.");if(transform is not null&&!transform.SequenceEqual(gt))throw Error("Companion","Companion grid transforms differ.");transform??=gt;crs??=sourceCrs;
            if(i<2)
            {
                var wave=h.Windows[5];var width=h.Windows[6];var flags=h.Windows[7];Require(wave,"Float32",1);Require(width,"Float32",1);Require(flags,"UInt8",1);
                variables[roles[i]+":"+wave.Dataset]=Variable(wave);variables[roles[i]+":"+width.Dataset]=Variable(width);variables[roles[i]+":"+flags.Dataset]=Variable(flags);
                if(new[]{wave,width,flags}.Any(v=>Variable(v).ScaleFactor!=1||Variable(v).AddOffset!=0))throw Error("Spectral","Scaled spectral-coordinate/flag encodings are not verified for this product definition.");
                if(String(wave.Attributes,"units")!="nm"||String(width.Attributes,"units")!="nm")throw Error("Units","Original wavelength/FWHM units must be nm; absent values are not inferred.");
                var w=Numbers(wave);var f=Numbers(width);var g=flags.LittleEndianValues!;
                if(w.Any(v=>!double.IsFinite(v)||v<=0)||f.Any(v=>!double.IsFinite(v)||v<=0)||w.Zip(w.Skip(1)).Any(p=>p.First>=p.Second)||g.Any(v=>v is not(0 or 1)))throw Error("Spectral","Invalid spectral coordinates or unsupported good-wavelength flags.");
                if(wavelength is not null&&(!wavelength.SequenceEqual(w)||!fwhm!.SequenceEqual(f)||!good!.SequenceEqual(g)))throw Error("Companion","Reflectance/uncertainty wavelength, FWHM or good-band metadata differs.");wavelength??=w;fwhm??=f;good??=g;
            }
            else
            {
                if(h.Windows[5].Strings is not {} labels||!labels.SequenceEqual(MaskLabels))throw Error("UnsupportedLayout","Unsupported mask labels/order. The older 11-layer guide and current seven-layer V003 producer are not interchangeable.");
                var packed=h.Windows[6];Require(packed,"UInt8",3);if(!packed.Dimensions.SequenceEqual(new[]{shape![0],shape[1],(ulong)((Bands+7)/8)}))throw Error("Companion","Packed band-mask dimensions do not match the original 285 channels.");
                var pv=Variable(packed);if(pv.ScaleFactor!=1||pv.AddOffset!=0||pv.FillValue is <0 or >255||pv.FillValue!=Math.Truncate(pv.FillValue)||pv.Units!="unitless")throw Error("UnsupportedLayout","Unsupported packed mask metadata.");variables["MASK:band_mask"]=pv;
            }
            // RFLUNCERT is an asset of the RFL granule, not a fabricated separate CMR granule.
            var granule="EMIT_L2A_"+(i<2?"RFL":"MASK")+"_"+versions[i]+"_"+acquisition;
            sources.Add(new(roles[i],i<2?"EMITL2ARFL":"EMITL2AMASK",versions[i],granule,Path.GetFileName(files[i].RelativePath),files[i].LibraryId,files[i].RelativePath,files[i].Bytes,files[i].Sha256,h.RootAttributes.ToImmutableDictionary()));
        }
        var domain=new AeroScience.Shared.GeographicBounds(transform![0],transform[3]+grid![0]*transform[5],transform[0]+grid[1]*transform[1],transform[3]);
        var definition=new EmitDefinition(1,Policy,acquisition!,start!.Value,end!.Value,checked((int)shape![0]),checked((int)shape[1]),Bands,checked((int)grid[0]),checked((int)grid[1]),transform.ToImmutableArray(),crs!,domain,wavelength!.ToImmutableArray(),fwhm!.ToImmutableArray(),good!.ToImmutableArray(),MaskLabels.ToImmutableArray(),sources.ToImmutableArray(),variables.ToImmutableDictionary(),
            "Identity, acquisition, dimensions, spectral vectors and GLT transform matched. Each returned window also checks original companion GLT/native coordinates. No complete-scene geographic/QA coverage is inferred.",
            "Definition-compatible local originals; authentic NASA original numerical acceptance is separate and is not asserted by the decoder.");
        CheckGridDomain(definition);return new(originals,definition);
    }

    private static void Require(HdfWindowResult window,string type,int rank){if(window.NumericType!=type||window.Dimensions.Length!=rank)throw Error("UnsupportedLayout","Required original variable has an unsupported numeric type or rank: "+window.Dataset);}
    private static EmitProductException Error(string code,string message)=>new(code,message);
    private static string String(IReadOnlyDictionary<string,HdfAttributeValue> attributes,string key)
    {if(!attributes.TryGetValue(key,out var value)||value.State!="Original"||value.Strings is not{Length:1})throw Error("Metadata","Required original text metadata is missing or unsupported: "+key);return value.Strings[0];}
    private static DateTimeOffset Utc(string value)
    {
        // The NASA SDS writes strftime('%z'): +0000, not necessarily a trailing Z.
        if(!Regex.IsMatch(value,@"(Z|\+00:?00)$"))throw Error("Metadata","Original acquisition time lacks an explicit supported UTC offset.");
        var normalized=value.EndsWith("+0000",StringComparison.Ordinal)?value[..^5]+"+00:00":value;
        if(!DateTimeOffset.TryParse(normalized,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)||date.Offset!=TimeSpan.Zero)throw Error("Metadata","Original acquisition time must be a supported explicit UTC value.");return date;
    }
    private static EmitVariable Variable(HdfWindowResult w)
    {
        double Scalar(string key,double? fallback=null){if(!w.Attributes.TryGetValue(key,out var a)||a.State=="Missing"){if(fallback is{} f)return f;throw Error("Metadata","Required original numeric metadata is missing: "+key);}var n=Numbers(a);if(n.Length!=1||!double.IsFinite(n[0]))throw Error("Metadata","Unsupported original scalar metadata: "+key);return n[0];}
        var scale=Scalar("scale_factor",1);if(scale==0)throw Error("Metadata","A zero scale factor is unsupported.");return new(w.Dataset,w.NumericType,w.Dimensions.ToImmutableArray(),String(w.Attributes,"units"),Scalar("_FillValue"),scale,Scalar("add_offset",0),
            w.Attributes["scale_factor"].State=="Missing"&&w.Attributes["add_offset"].State=="Missing"?"CF defaults: identity; no source calibration invented":"Original CF scale_factor/add_offset, with CF defaults for absent members",w.Attributes.ToImmutableDictionary());
    }
    private static double[] Numbers(HdfWindowResult w)=>Numbers(w.NumericType,w.LittleEndianValues??throw Error("Metadata","Original numeric values are unavailable."));
    private static double[] Numbers(HdfAttributeValue a)=>a.State=="Original"?Numbers(a.Type,a.LittleEndianValues??throw Error("Metadata","Original numeric attribute is unsupported.")):throw Error("Metadata","Original numeric attribute is unavailable.");
    private static double[] Numbers(string type,byte[] bytes)
    {
        var size=type switch{"Float32" or "Int32" or "UInt32"=>4,"Float64" or "Int64" or "UInt64"=>8,"UInt8" or "Int8"=>1,"Int16" or "UInt16"=>2,_=>throw Error("UnsupportedLayout","Unsupported numeric representation.")};
        if(bytes.Length%size!=0)throw Error("Integrity","Original numeric buffer size is inconsistent.");var values=new double[bytes.Length/size];for(var i=0;i<values.Length;i++)values[i]=type switch{"Float32"=>BitConverter.ToSingle(bytes,i*size),"Float64"=>BitConverter.ToDouble(bytes,i*size),"Int32"=>BitConverter.ToInt32(bytes,i*size),"UInt32"=>BitConverter.ToUInt32(bytes,i*size),"UInt8"=>bytes[i],"Int8"=>(sbyte)bytes[i],"Int16"=>BitConverter.ToInt16(bytes,i*size),"UInt16"=>BitConverter.ToUInt16(bytes,i*size),_=>throw Error("UnsupportedLayout","64-bit integer metadata is not interpreted through lossy double conversion.")};return values;
    }

    // Exact GDAL WKT1 geographic forms emitted by the referenced NASA converter.
    // No substring-based acceptance of arbitrary projected/WKT coordinates.
    private const string GeographicWkt="GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]],UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]],AUTHORITY[\"EPSG\",\"4326\"]]";
    private static bool Wgs84(string crs)
    {
        static string Normalize(string s)=>Regex.Replace(s,@"\s+","");
        static string KnownAuthorities(string s)=>Regex.Replace(s,",AUTHORITY\\[\"EPSG\",\"(7030|6326|8901|9122|9102|4326)\"\\]","");
        var value=KnownAuthorities(Normalize(crs));var reference=KnownAuthorities(Normalize(GeographicWkt));
        return crs=="EPSG:4326"||value==reference||value==reference[..^1]+",AXIS[\"Latitude\",NORTH],AXIS[\"Longitude\",EAST]]";
    }
    private static void CheckGridDomain(EmitDefinition d)
    {var west=d.GridTransform[0];var north=d.GridTransform[3];var east=west+d.GridColumns*d.GridTransform[1];var south=north+d.GridRows*d.GridTransform[5];if(west< -180||east>180||north>90||south< -90)throw Error("Geolocation","Unsupported dateline/polar GLT extent; no bounding rectangle is fabricated.");}
}
