using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record RasterDisplayPreview(byte[] Png,GeographicBounds Bounds,double? Minimum,double? Maximum,string[] Units,string[] Bands,string Method);
public static class ScientificArtifactPreview
{
    public static RasterDisplayPreview Create(ScienceStore science,ScientificArtifact artifact,CancellationToken token=default)
    {
        if(artifact.Format!="tif")throw new ArgumentException("Select a geographic raster product.");
        var metadata=JsonSerializer.Deserialize<RasterProductMetadata>(artifact.MetadataJson,WireJson.Options)??throw new InvalidDataException("Raster metadata is missing.");
        var path=science.VerifiedArtifactPath(artifact);var root=Path.GetDirectoryName(path)!;var file=Path.GetFileName(path);
        var corners=ScientificRaster.GridCoverage(metadata.Grid);var bounds=new GeographicBounds(corners.Min(p=>p.Longitude),corners.Min(p=>p.Latitude),corners.Max(p=>p.Longitude),corners.Max(p=>p.Latitude));
        if(bounds.East-bounds.West>180)throw new InvalidOperationException("Display preview crossing the date line requires separate regions. Numeric GeoTIFF remains available.");
        const int size=384;var values=Enumerable.Range(0,metadata.Bands.Length).Select(_=>new double?[size*size]).ToArray();var readers=new List<ScientificRaster>();
        try
        {
            for(var b=0;b<values.Length;b++)readers.Add(new(root,new(metadata.Bands[b],file,"geotiff",Channel:b)));
            for(var y=0;y<size;y++)
            {token.ThrowIfCancellationRequested();for(var x=0;x<size;x++){var point=new Coordinates(bounds.North-(y+.5)*(bounds.North-bounds.South)/size,bounds.West+(x+.5)*(bounds.East-bounds.West)/size);for(var b=0;b<values.Length;b++)values[b][y*size+x]=readers[b].Read(point);}}
        }
        finally{foreach(var reader in readers)reader.Dispose();}
        var numeric=values[0].OfType<double>().ToArray();double? min=numeric.Length==0?null:numeric.Min(),max=numeric.Length==0?null:numeric.Max();
        var rgba=new byte[size*(1+size*4)];
        for(var y=0;y<size;y++)for(var x=0;x<size;x++)
        {
            var i=y*size+x;var offset=y*(size*4+1)+1+x*4;if(values.Any(v=>v[i] is null))continue;
            if(values.Length>=3){for(var b=0;b<3;b++)rgba[offset+b]=(byte)(Math.Clamp(values[b][i]!.Value/.3,0,1)*255);}
            else{var t=Math.Clamp((values[0][i]!.Value-min!.Value)/(max==min?1:max!.Value-min.Value),0,1);rgba[offset]=(byte)(255*t);rgba[offset+1]=(byte)(210*(1-Math.Abs(t-.5)*2));rgba[offset+2]=(byte)(255*(1-t));}
            rgba[offset+3]=255;
        }
        return new(ScientificPreview.EncodePng(size,size,rgba),bounds,min,max,metadata.Units,metadata.Bands,
            "Display-only nearest-pixel reprojection to WGS84; transparent nodata. Scalar blue-to-red uses the displayed minimum/maximum; RGB uses a stated 0–0.3 reflectance stretch. This preview never changes numeric scientific products or improves native resolution.");
    }
}
