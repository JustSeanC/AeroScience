using System.Collections.Immutable;

namespace AeroScience.Engine;

public sealed partial class EmitProductDecoder
{
    public async Task<EmitNativeResult> ReadNativeAsync(EmitProduct product,EmitNativeWindow window,EmitQualityPolicy? policy=null,CancellationToken token=default)
    {
        var d=product.Definition;Validate(window.Row,window.Column,window.Rows,window.Columns,window.FirstBand,window.BandCount,d.Rows,d.Columns);policy??=new();
        ulong[] start=[(ulong)window.Row,(ulong)window.Column,(ulong)window.FirstBand],count=[(ulong)window.Rows,(ulong)window.Columns,(ulong)window.BandCount];
        var originals=new[]{product.Originals.Reflectance,product.Originals.Uncertainty,product.Originals.Mask};var names=new[]{"reflectance","reflectance_uncertainty","mask"};var result=new HdfReadResult[3];
        for(var i=0;i<3;i++)
        {
            List<HdfWindow> requests=[new(names[i],i==2?[start[0],start[1],0]:start,i==2?[count[0],count[1],7]:count),new("location/lat",start[..2],count[..2]),new("location/lon",start[..2],count[..2])];
            if(i==2)requests.Add(new("band_mask",[start[0],start[1],0],[count[0],count[1],36]));
            result[i]=await reader.ReadAsync(new(1,originals[i],requests.ToArray()),token);
        }
        for(var i=1;i<3;i++)for(var j=1;j<=2;j++)if(!result[0].Windows[j].LittleEndianValues!.SequenceEqual(result[i].Windows[j].LittleEndianValues!))throw Error("Companion","Returned native coordinates disagree between companions; no spectrum was published.");
        var raw=Numbers(result[0].Windows[0]);var errors=Numbers(result[1].Windows[0]);var quality=Numbers(result[2].Windows[0]);var lat=Numbers(result[0].Windows[1]);var lon=Numbers(result[0].Windows[2]);
        var pixels=ImmutableArray.CreateBuilder<EmitSpectrum>();var rv=d.Variables["RFL"];var uv=d.Variables["RFLUNCERT"];var mv=d.Variables["MASK"];var lv=d.Variables["RFL:location/lat"];var ov=d.Variables["RFL:location/lon"];
        for(var p=0;p<window.Rows*window.Columns;p++)
        {
            token.ThrowIfCancellationRequested();var q=quality.AsSpan(p*7,7).ToArray();var bits=result[2].Windows[3].LittleEndianValues!.AsSpan(p*36,36).ToArray();
            var selected=policy.ExcludeWater?new[]{0,1,2,3,5}:new[]{0,1,3,5};var unknown=selected.Any(n=>!Original(q[n],mv)||q[n] is not(0 or 1));var excluded=selected.Any(n=>q[n]==1);
            // Individual packed bytes can legitimately equal the encoded fill byte.
            // A whole fill vector is indeterminate; never count it as cleared QA.
            var bandQualityUnknown=policy.ExcludeInterpolatedBands&&bits.All(b=>b==d.Variables["MASK:band_mask"].FillValue);
            var located=Original(lat[p],lv)&&Original(lon[p],ov)&&lat[p] is >= -90 and <=90&&lon[p] is >= -180 and <=180;
            var values=ImmutableArray.CreateBuilder<double?>();var uncertainty=ImmutableArray.CreateBuilder<double?>();var usable=ImmutableArray.CreateBuilder<double?>();var usableUncertainty=ImmutableArray.CreateBuilder<double?>();var states=ImmutableArray.CreateBuilder<string>();
            for(var b=0;b<window.BandCount;b++)
            {
                var index=p*window.BandCount+b;var band=window.FirstBand+b;var v=Physical(raw[index],rv);var u=Physical(errors[index],uv);
                var state=!located?"Invalid geolocation":v is null?"Reflectance fill or invalid":d.GoodWavelengths[band]==0?"Excluded wavelength":bandQualityUnknown?"Band quality unknown":policy.ExcludeInterpolatedBands&&(bits[band/8]&(1<<(7-band%8)))!=0?"Interpolated band excluded":unknown?"Quality unknown":excluded?"Quality excluded":u is null||u<0?"Uncertainty unavailable or invalid":"Usable under selected QA policy";
                values.Add(v);uncertainty.Add(u);states.Add(state);usable.Add(state=="Usable under selected QA policy"?v:null);usableUncertainty.Add(state=="Usable under selected QA policy"?u:null);
            }
            var valid=usable.Count(v=>v.HasValue);var summary=valid==window.BandCount?"Usable under selected QA policy":valid>0?"Partially usable":!located?"Invalid geolocation":unknown||bandQualityUnknown?"Quality unknown":"No usable requested bands";
            pixels.Add(new(window.Row+p/window.Columns,window.Column+p%window.Columns,located?lat[p]:null,located?lon[p]:null,summary,values.ToImmutable(),uncertainty.ToImmutable(),usable.ToImmutable(),usableUncertainty.ToImmutable(),states.ToImmutable(),
                result[0].Windows[0].LittleEndianValues!.AsSpan(p*window.BandCount*4,window.BandCount*4).ToArray(),result[1].Windows[0].LittleEndianValues!.AsSpan(p*window.BandCount*4,window.BandCount*4).ToArray(),result[2].Windows[0].LittleEndianValues!.AsSpan(p*7*4,7*4).ToArray(),bits,q.Select(v=>Original(v,mv)?(double?)v:null).ToImmutableArray(),result[0].Windows[1].LittleEndianValues!.AsSpan(p*8,8).ToArray(),result[0].Windows[2].LittleEndianValues!.AsSpan(p*8,8).ToArray()));
        }
        return new(Policy,policy,window,pixels.ToImmutable(),d.Sources,"Only these requested native samples and channels were assessed. Raw bytes, source scaling and QA exclusions remain distinct; no whole-scene coverage, airborne observation or accuracy certification is inferred.");
    }

    public async Task<EmitGeographicResult> ReadGeographicAsync(EmitProduct product,EmitGridWindow window,EmitQualityPolicy? policy=null,CancellationToken token=default)
    {
        var d=product.Definition;Validate(window.Row,window.Column,window.Rows,window.Columns,window.FirstBand,window.BandCount,d.GridRows,d.GridColumns);policy??=new();
        if((long)window.Rows*window.Columns>64)throw Error("Limit","Choose at most 64 geographic cells per bounded read.");
        ulong[] start=[(ulong)window.Row,(ulong)window.Column],count=[(ulong)window.Rows,(ulong)window.Columns];
        int[]? x=null,y=null;foreach(var original in new[]{product.Originals.Reflectance,product.Originals.Uncertainty,product.Originals.Mask})
        {
            var read=await reader.ReadAsync(new(1,original,[new("location/glt_x",start,count),new("location/glt_y",start,count)]),token);var xx=Numbers(read.Windows[0]).Select(v=>checked((int)v)).ToArray();var yy=Numbers(read.Windows[1]).Select(v=>checked((int)v)).ToArray();
            if(x is not null&&(!x.SequenceEqual(xx)||!y!.SequenceEqual(yy)))throw Error("Companion","Original GLT mappings disagree between companions.");x??=xx;y??=yy;
        }
        bool Mapped(int p)=>x![p]>=1&&y![p]>=1&&x[p]<=d.Columns&&y[p]<=d.Rows;
        var mapped=Enumerable.Range(0,x!.Length).Where(Mapped).ToArray();var spectra=new Dictionary<(int,int),EmitSpectrum>();
        if(mapped.Length>0)
        {
            var row=mapped.Min(p=>y![p])-1;var column=mapped.Min(p=>x[p])-1;var rows=mapped.Max(p=>y![p])-row;var columns=mapped.Max(p=>x[p])-column;
            var native=await ReadNativeAsync(product,new(row,column,rows,columns,window.FirstBand,window.BandCount),policy,token);spectra=native.Pixels.ToDictionary(p=>(p.Row,p.Column));
        }
        var cells=ImmutableArray.CreateBuilder<EmitGridCell>();for(var p=0;p<x.Length;p++)
        {
            var row=window.Row+p/window.Columns;var col=window.Column+p%window.Columns;var lat=d.GridTransform[3]+(row+.5)*d.GridTransform[5];var lon=d.GridTransform[0]+(col+.5)*d.GridTransform[1];
            if(!Mapped(p)){cells.Add(new(row,col,lat,lon,null,null,x[p]==0||y![p]==0?"Not geolocated":"Invalid GLT reference",null));continue;}
            var pixel=spectra[(y![p]-1,x[p]-1)];cells.Add(new(row,col,lat,lon,pixel.Row,pixel.Column,pixel.State,pixel));
        }
        return new(Policy,policy,window,cells.ToImmutable(),d.Sources,"Derived GLT cell centers map through original one-based indices to unchanged native swath samples. Zero/negative/out-of-range cells are never wrapped or clamped. QA-valid support is reported only for requested cells/bands, not the scene outline or flown route.");
    }

    private static bool Original(double v,EmitVariable metadata)=>double.IsFinite(v)&&v!=metadata.FillValue;
    private static double? Physical(double v,EmitVariable metadata)
    {if(!Original(v,metadata))return null;var result=v*metadata.ScaleFactor+metadata.AddOffset;return double.IsFinite(result)?result:null;}
    private static void Validate(int row,int column,int rows,int columns,int firstBand,int bandCount,int height,int width)
    {if(row<0||column<0||rows<1||columns<1||rows>height||columns>width||row>height-rows||column>width-columns||firstBand<0||bandCount<1||bandCount>Bands||firstBand>Bands-bandCount)throw new ArgumentOutOfRangeException(nameof(row),"Use an in-bounds native/grid window and zero-based spectral channel range.");if((long)rows*columns>MaximumPixels||(long)rows*columns*bandCount>MaximumSpectralValues)throw Error("Limit","Requested spectral work exceeds the bounded window; choose fewer pixels or channels.");}
}
