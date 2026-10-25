using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record ScientificSourceInspectionRequest(GeographicBounds Area,string Band,int Samples=24);
public sealed record ScientificInspectedValue(int Column,int Row,Coordinates Location,double? DecodedSourceValue,double? Value,string Quality,Coordinates[] Boundary);
public sealed partial class ScienceStore
{
    private static readonly SemaphoreSlim sourceInspection=new(1);
    public object InspectSource(Guid id,ScientificSourceInspectionRequest request,Func<ScientificSource,bool> verify,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(request.Area);AutomaticScientificData.ValidateArea(request.Area);if(request.Samples is <2 or >32)throw new ArgumentException("Choose 2–32 sample positions per side; this is a preview bound, not source resolution.");
        if(!sourceInspection.Wait(0))throw new InvalidOperationException("A scientific source preview is already being read. Try again when it finishes.");
        try
        {
            using var original=BeginAnalysis();var source=Source(id)??throw new ArgumentException("Retained source not found. Refresh the Library.");var m=source.Manifest;
            if(m.Swath is not null)throw new InvalidOperationException("Use the existing EMIT Spectrum / Map / Export explorer for native swath data.");
            var ndvi=request.Band=="ndvi"&&m.Kind==RemoteKind.Multispectral;var band=m.Bands.SingleOrDefault(b=>b.Id==(ndvi?"B04":request.Band))??throw new ArgumentException("Choose a measurement actually retained in this source.");
            if(band.Id is "QA_PIXEL" or "QA_RADSAT" or "SCL")throw new ArgumentException("Choose a scientific measurement; quality masks are applied and reported separately.");
            if(!InspectAvailability(source).FilesAvailable)throw new IOException("The retained original files or their configured drive are unavailable. Reconnect the Library; no replacement folder was created.");
            if(!verify(source))throw new IOException("Original file verification failed. Restore or reimport the source; no values were displayed as verified.");
            var grid=source.Grids[band.Id];var points=new List<(int Column,int Row,Coordinates Center)>();var seen=new HashSet<(int,int)>();int outside=0;
            for(var y=0;y<request.Samples;y++){token.ThrowIfCancellationRequested();for(var x=0;x<request.Samples;x++)
            {
                var location=new Coordinates(request.Area.North-(y+.5)*(request.Area.North-request.Area.South)/request.Samples,request.Area.West+(x+.5)*(request.Area.East-request.Area.West)/request.Samples);
                if(!ScientificRaster.GridCovers(grid,location)){outside++;continue;}var pixel=ScientificSupport.Pixel(grid,location);if(!seen.Add(pixel))continue;
                points.Add((pixel.Column,pixel.Row,ScientificRaster.GridPoint(grid,pixel.Column+.5,pixel.Row+.5)));
            }}
            var chosen=m.Kind is RemoteKind.Thermal or RemoteKind.Multispectral?m.Bands:new[]{band};var values=new List<RemoteBandValues>();
            foreach(var b in chosen){token.ThrowIfCancellationRequested();using var raster=new ScientificRaster(Path.Combine(DirectoryPath,source.RelativeDirectory),b);values.Add(new(b.Id,b.Unit,b.WavelengthNm,points.Select(p=>{token.ThrowIfCancellationRequested();return raster.Read(p.Center);}).ToArray(),"Nearest native original pixel at its center; no aircraft observation",raster.Grid));}
            var decoded=values.Single(b=>b.Id==band.Id).Values.ToArray();var derived=RemoteCalculations.Apply(m.Kind,values.ToArray());var filtered=ndvi?derived["ndvi"]:values.Single(b=>b.Id==band.Id).Values;var unit=ndvi?"1":band.Unit;
            var samples=points.Select((p,i)=>new ScientificInspectedValue(p.Column,p.Row,p.Center,decoded[i],filtered[i],band.BadBand?"Excluded band":decoded[i] is null?"Missing / nodata":filtered[i] is null?"Quality excluded":"Valid at inspected native sample",[ScientificRaster.GridPoint(grid,p.Column,p.Row),ScientificRaster.GridPoint(grid,p.Column+1,p.Row),ScientificRaster.GridPoint(grid,p.Column+1,p.Row+1),ScientificRaster.GridPoint(grid,p.Column,p.Row+1)])).ToArray();
            var valid=samples.Where(s=>s.Value is not null).ToArray();
            var features=valid.Where(s=>s.Boundary.Max(p=>p.Longitude)-s.Boundary.Min(p=>p.Longitude)<=180).Select(s=>new{type="Feature",geometry=new{type="Polygon",coordinates=new[]{s.Boundary.Append(s.Boundary[0]).Select(p=>new[]{p.Longitude,p.Latitude}).ToArray()}},properties=new{value=s.Value,unit,column=s.Column,row=s.Row,sourceId=id,sourceUtc=m.ObservationUtc}}).ToArray();
            return new{processingVersion="source-inspection-1",sourceId=id,m.Provider,m.SceneId,m.ObservationUtc,m.VerticalDatum,m.License,m.Citation,files=source.Files,band,measurement=request.Band,unit,grid,request.Area,samples,outsideRequests=outside,
                minimum=valid.Length==0?(double?)null:valid.Min(s=>s.Value),maximum=valid.Length==0?(double?)null:valid.Max(s=>s.Value),layer=new{type="FeatureCollection",features},
                method="Historical original-source inspection only; no mission observation, acquisition credit or gap filling. Decoded values include declared scaling; QA-filtered values are distinct. Displayed native cells contain inspected samples, not proof that the entire selected region has valid data. Duplicate native pixels counted once. Negative or greater-than-one reflectance estimates are preserved.",derived=ndvi};
        }
        finally{sourceInspection.Release();}
    }
}
