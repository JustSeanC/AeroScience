using PureHDF;
using PureHDF.Selections;
using PureHDF.VOL.Native;

// Research only, excluded from the application. Metadata below is a strict synthetic
// contract, NOT a claim that every NASA release has these exact attribute encodings.
public sealed class EmitWindowReader:IDisposable
{
    private readonly NativeFile reflectance,uncertainty,mask;
    private readonly NativeDataset rfl,unc,qa,packed,gltX,gltY,latitude,longitude;
    private readonly Dictionary<NativeDataset,H5DatasetAccess> caches=new();
    private readonly object gate=new();
    public ulong[] Shape{get;}
    public float[] Wavelengths{get;}
    public float[] Fwhm{get;}
    public int[] GoodWavelengths{get;}
    public string[] QualityNames{get;}
    public string Scene{get;}
    public string Observation{get;}
    private readonly float rflFill,uncFill;
    private readonly double[] gt;
    public EmitWindowReader(string rflPath,string uncPath,string maskPath)
    {
        // The product/dependency gate has deliberately not enabled arbitrary user files.
        reflectance=H5File.OpenRead(rflPath);try{uncertainty=H5File.OpenRead(uncPath);try{mask=H5File.OpenRead(maskPath);}catch{uncertainty.Dispose();throw;}}catch{reflectance.Dispose();throw;}
        try
        {
            string Attr(NativeFile f,string name)=>f.Attribute(name).Read<string>();
            Scene=Attr(reflectance,"scene_id");Observation=Attr(reflectance,"time_coverage_start");
            if(Attr(reflectance,"product_version")!="002"||Attr(uncertainty,"product_version")!="002"||Attr(mask,"product_version")!="003")throw new NotSupportedException("Unverified product version combination; V2 reflectance/uncertainty may pair with V3 mask.");
            if(new[]{uncertainty,mask}.Any(f=>Attr(f,"scene_id")!=Scene||Attr(f,"time_coverage_start")!=Observation))throw new InvalidDataException("Mismatched scene/acquisition companions.");
            rfl=FloatCube(reflectance,"reflectance");unc=FloatCube(uncertainty,"reflectance_uncertainty");Shape=rfl.Space.Dimensions;
            if(!unc.Space.Dimensions.SequenceEqual(Shape)||Shape.Length!=3||Shape.Any(n=>n is <1 or >1048576)||Shape[2]>2048)throw new InvalidDataException("Unsupported spectral dimensions/companion alignment.");
            if(rfl.Attribute("units").Read<string>()!="1"||unc.Attribute("units").Read<string>()!="1")throw new NotSupportedException("Unverified reflectance/uncertainty units; never convert radiance silently.");
            rflFill=rfl.Attribute("_FillValue").Read<float>();uncFill=unc.Attribute("_FillValue").Read<float>();
            Wavelengths=Spectral(reflectance,"wavelengths");Fwhm=Spectral(reflectance,"fwhm");
            if(!Spectral(uncertainty,"wavelengths").SequenceEqual(Wavelengths)||!Spectral(uncertainty,"fwhm").SequenceEqual(Fwhm))throw new InvalidDataException("Spectral companion mismatch.");
            var good=Dataset(reflectance,"sensor_band_parameters/good_wavelengths");Check(good);if(good.Type.Class!=H5DataTypeClass.FixedPoint||good.Type.Size!=4||!good.Space.Dimensions.SequenceEqual(new[]{Shape[2]}))throw new InvalidDataException("Bad-band dimensions differ.");GoodWavelengths=good.Read<int[]>();if(GoodWavelengths.Any(v=>v is not(0 or 1)))throw new InvalidDataException("Unknown bad-band encoding.");
            qa=Dataset(mask,"mask");packed=Dataset(mask,"band_mask");Check(qa);Check(packed);
            var names=Dataset(mask,"sensor_band_parameters/mask_bands");Check(names);QualityNames=names.Read<string[]>();
            if(qa.Type.Class!=H5DataTypeClass.FloatingPoint||qa.Type.Size!=4||qa.Space.Dimensions.Length!=3||qa.Space.Dimensions[0]!=Shape[0]||qa.Space.Dimensions[1]!=Shape[1]||qa.Space.Dimensions[2]!= (ulong)QualityNames.Length||QualityNames.Length>32||QualityNames.Any(s=>s.Length>100))throw new InvalidDataException("Unsupported mask layout.");
            if(packed.Type.Class!=H5DataTypeClass.FixedPoint||packed.Type.Size!=1||!packed.Space.Dimensions.SequenceEqual(new[]{Shape[0],Shape[1],(Shape[2]+7)/8}))throw new InvalidDataException("Unsupported packed band-mask layout.");
            gltX=Dataset(reflectance,"location/glt_x");gltY=Dataset(reflectance,"location/glt_y");Check(gltX);Check(gltY);
            if(gltX.Type.Class!=H5DataTypeClass.FixedPoint||gltX.Type.Size!=4||gltY.Type.Class!=H5DataTypeClass.FixedPoint||gltY.Type.Size!=4||gltX.Space.Dimensions.Length!=2||!gltX.Space.Dimensions.SequenceEqual(gltY.Space.Dimensions))throw new InvalidDataException("Unsupported GLT dimensions/type.");
            latitude=Dataset(reflectance,"location/lat");longitude=Dataset(reflectance,"location/lon");Check(latitude);Check(longitude);
            if(!latitude.Space.Dimensions.SequenceEqual(Shape[..2])||!longitude.Space.Dimensions.SequenceEqual(Shape[..2])||latitude.Type.Class!=H5DataTypeClass.FloatingPoint||longitude.Type.Class!=H5DataTypeClass.FloatingPoint||latitude.Type.Size!=8||longitude.Type.Size!=8)throw new InvalidDataException("Unsupported native swath lat/lon dimensions.");
            gt=reflectance.Attribute("geotransform").Read<double[]>();if(gt.Length!=6||gt.Any(v=>!double.IsFinite(v))||Math.Abs(gt[1]*gt[5]-gt[2]*gt[4])<1e-20||Attr(reflectance,"spatial_ref")!="EPSG:4326")throw new NotSupportedException("Unverified geographic GLT transform/CRS.");
        }
        catch{Dispose();throw;}
    }
    private static NativeDataset Dataset(NativeFile file,string path)=>(NativeDataset)file.Dataset(path);
    private static void Check(NativeDataset dataset)
    {
        ulong count=1;foreach(var n in dataset.Space.Dimensions){if(n is <1 or >1048576)throw new NotSupportedException("Unsupported dataset extent.");count=checked(count*n);}
        if(dataset.Layout.Class==H5DataLayoutClass.Chunked){ulong chunk=(ulong)dataset.Type.Size;foreach(var n in dataset.Layout.Chunks)chunk=checked(chunk*n);if(chunk>4194304)throw new NotSupportedException("Decompressed source chunk exceeds the 4-MiB research bound.");}
        else if(dataset.Layout.Class is not(H5DataLayoutClass.Compact or H5DataLayoutClass.Contiguous))throw new NotSupportedException("Unsupported HDF layout.");
    }
    private static NativeDataset FloatCube(NativeFile file,string path){var d=Dataset(file,path);Check(d);if(d.Type.Class!=H5DataTypeClass.FloatingPoint||d.Type.Size!=4)throw new NotSupportedException("Only explicitly supported original float32 cubes.");return d;}
    private float[] Spectral(NativeFile f,string name)
    {
        var d=Dataset(f,"sensor_band_parameters/"+name);Check(d);if(!d.Space.Dimensions.SequenceEqual(new[]{Shape[2]})||d.Type.Class!=H5DataTypeClass.FloatingPoint||d.Type.Size!=4||d.Attribute("units").Read<string>()!="nm")throw new InvalidDataException("Missing/unsupported spectral metadata.");
        var v=d.Read<float[]>();if(v.Any(x=>!float.IsFinite(x)||x<=0)||name=="wavelengths"&&v.Zip(v.Skip(1)).Any(p=>p.First>=p.Second))throw new InvalidDataException("Invalid original spectral coordinates.");return v;
    }
    private T[] Read<T>(NativeDataset d,ulong[] start,ulong[] count)
    {
        if(!caches.TryGetValue(d,out var access)){access=new(ChunkCache:new SimpleReadingChunkCache(2,262144));caches.Add(d,access);}
        return d.Read<T[]>(access,fileSelection:new HyperslabSelection(start.Length,start,count));
    }
    public sealed record Spectrum(string State,int? SourceColumn,int? SourceRow,double? NativeLatitude,double? NativeLongitude,double GeographicPixelLatitude,double GeographicPixelLongitude,float[] RawReflectance,float?[] Reflectance,float[] RawUncertainty,float?[] Uncertainty,float[] RawQuality,byte[] RawPackedBandMask,bool[] BandMasked,string QualityInterpretation);
    public Spectrum ReadSpectrum(int gridColumn,int gridRow,CancellationToken token=default)
    {lock(gate)return ReadSpectrumCore(gridColumn,gridRow,token);}
    private Spectrum ReadSpectrumCore(int gridColumn,int gridRow,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var dims=gltX.Space.Dimensions;
        if(gridColumn<0||gridRow<0||(ulong)gridColumn>=dims[1]||(ulong)gridRow>=dims[0])throw new ArgumentOutOfRangeException(nameof(gridColumn));
        var x=Read<int>(gltX,[(ulong)gridRow,(ulong)gridColumn],[1,1])[0];var y=Read<int>(gltY,[(ulong)gridRow,(ulong)gridColumn],[1,1])[0];
        var lon=gt[0]+(gridColumn+.5)*gt[1]+(gridRow+.5)*gt[2];var lat=gt[3]+(gridColumn+.5)*gt[4]+(gridRow+.5)*gt[5];
        if(x==0||y==0)return new("Not geolocated",null,null,null,null,lat,lon,[],[],[],[],[],[],[],"Unknown; zero GLT never maps to source pixel zero.");
        if(x<1||y<1||(ulong)x>Shape[1]||(ulong)y>Shape[0])return new("Invalid GLT reference",null,null,null,null,lat,lon,[],[],[],[],[],[],[],"Unknown; invalid GLT is never clamped to a valid pixel.");
        token.ThrowIfCancellationRequested();ulong[] start=[(ulong)y-1,(ulong)x-1,0],count=[1,1,Shape[2]];
        var raw=Read<float>(rfl,start,count);token.ThrowIfCancellationRequested();var error=Read<float>(unc,start,count);token.ThrowIfCancellationRequested();
        var quality=Read<float>(qa,start,[1,1,qa.Space.Dimensions[2]]);var bits=Read<byte>(packed,start,[1,1,packed.Space.Dimensions[2]]);
        var maskBits=Enumerable.Range(0,(int)Shape[2]).Select(i=>(bits[i/8]&(1<<(7-i%8)))!=0).ToArray();
        var nativeLat=Read<double>(latitude,start[..2],[1,1])[0];var nativeLon=Read<double>(longitude,start[..2],[1,1])[0];token.ThrowIfCancellationRequested();
        if(!double.IsFinite(nativeLat)||!double.IsFinite(nativeLon)||Math.Abs(nativeLat)>90||Math.Abs(nativeLon)>180)throw new InvalidDataException("Invalid native swath geolocation.");
        return new("Original source window",x-1,y-1,nativeLat,nativeLon,lat,lon,raw,raw.Select(v=>v==rflFill||!float.IsFinite(v)?(float?)null:v).ToArray(),error,error.Select(v=>v==uncFill||!float.IsFinite(v)?(float?)null:v).ToArray(),quality,bits,maskBits,"Raw flags retained; source-version-specific QA interpretation remains unverified. No Good Data/accuracy claim.");
    }
    public void Dispose(){mask.Dispose();uncertainty.Dispose();reflectance.Dispose();}
}
