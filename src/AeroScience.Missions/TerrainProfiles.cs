using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record TerrainProfileRequest(Guid? MissionId=null,Guid? SourceId=null,string Path="track",Coordinates[]? Line=null,
    Guid? PlanId=null,int? Leg=null,int StartIndex=0,int? EndIndex=null,int MaximumPoints=1025,bool SimulatorContext=false,DateTimeOffset? FromUtc=null,DateTimeOffset? ToUtc=null);
public sealed record TerrainProfilePoint(double DistanceMetres,Coordinates Position,double? ElevationMetres,bool Connect=false);
public sealed record SimulatorTerrainPoint(double DistanceMetres,Coordinates Position,double ApproximateElevationMetres,DateTimeOffset TimestampUtc);
public sealed record TerrainProfileResult(TerrainProfilePoint[] Points,SimulatorTerrainPoint[] SimulatorContext,double DistanceMetres,
    double? MinimumMetres,double? MaximumMetres,double? NetChangeMetres,ScientificSourceManifest? Source,Guid? SourceId,RasterGrid? Grid,string Method,string[] Warnings);

// Read-only derived analysis. It never publishes observations, coverage, grades or source files.
public static class TerrainProfiles
{
    private sealed record Vertex(Coordinates Position,TelemetryFrame? Frame=null);
    public static TerrainProfileResult Create(TerrainProfileRequest request,MissionStore missions,ScienceStore science,
        Func<ScientificSource,bool> verify,CancellationToken token=default)
    {
        if(request.MaximumPoints is <2 or >2049||request.StartIndex<0||request.EndIndex<request.StartIndex||request.FromUtc>request.ToUtc)throw new ArgumentException("Choose 2-2049 profile points and an ordered flight segment.");
        if(request.MissionId is {} mission&&missions.Detail(mission) is null)throw new ArgumentException("Recorded flight not found.");
        Vertex[] vertices;
        if(request.Path=="track"){
            if(request.MissionId is not {} flight)throw new ArgumentException("Select a recorded flight for its ground track.");
            var take=request.EndIndex is {} end?Math.Min(100001,(long)end-request.StartIndex+1):100001;
            var frames=missions.StreamSamples(flight).Where(f=>(request.FromUtc is null||f.TimestampUtc>=request.FromUtc)&&(request.ToUtc is null||f.TimestampUtc<=request.ToUtc)).Skip(request.StartIndex).Take((int)take).ToArray();
            if(frames.Length>100000)throw new ArgumentException("Choose a shorter recorded segment (at most 100,000 original positions per profile).");
            vertices=frames.Where(f=>f.IsValid).Select(f=>new Vertex(new(f.Latitude,f.Longitude),f)).ToArray();
        }else if(request.Path=="plan"){
            var plan=request.PlanId is {} id?missions.Plan(id):request.MissionId is {} recorded?missions.Research(recorded).PlanSnapshot:null;
            var route=plan?.Waypoints??throw new ArgumentException("Select a saved plan or a flight with a recorded plan snapshot.");
            if(request.Leg is {} leg){if(leg<0||leg>=route.Length-1)throw new ArgumentException("Choose an existing route leg.");route=route.Skip(leg).Take(2).ToArray();}
            vertices=route.Select(p=>new Vertex(new(p.Latitude,p.Longitude))).ToArray();
        }else if(request.Path=="line"&&request.Line is {Length:2} line)vertices=line.Select(p=>new Vertex(p)).ToArray();
        else throw new ArgumentException("Choose a recorded track, saved-plan route/leg, or two-point line.");
        if(vertices.Length<2||vertices.Length>100000||vertices.Any(v=>!double.IsFinite(v.Position.Latitude)||!double.IsFinite(v.Position.Longitude)||Math.Abs(v.Position.Latitude)>90||Math.Abs(v.Position.Longitude)>180))throw new ArgumentException("A profile needs at least two valid geographic positions.");
        var distances=new double[vertices.Length];var gaps=new bool[vertices.Length];
        for(var i=1;i<vertices.Length;i++){
            token.ThrowIfCancellationRequested();distances[i]=distances[i-1]+Geodesy.Distance(vertices[i-1].Position,vertices[i].Position);
            gaps[i]=vertices[i].Frame is {} next&&vertices[i-1].Frame is {} prior&&(next.TimestampUtc<=prior.TimestampUtc||(next.TimestampUtc-prior.TimestampUtc).TotalSeconds>1.5||next.IsPaused||prior.IsPaused);
        }
        var total=distances[^1];if(!double.IsFinite(total)||total<=0)throw new ArgumentException("Choose a route with a nonzero horizontal distance.");
        var warnings=new List<string>();if(gaps.Any(g=>g))warnings.Add("Recorded position gaps are not reconstructed. The distance axis includes straight endpoint displacement across those gaps; it is not a complete flown-distance measurement.");
        var context=new List<SimulatorTerrainPoint>();
        if(request.SimulatorContext&&request.Path=="track"){
            var step=Math.Max(1,(int)Math.Ceiling(vertices.Length/2049d));
            for(var i=0;i<vertices.Length;i+=step)if(vertices[i].Frame is {Source:TelemetrySource.Simulator,IsPaused:false,AltitudeAglFeet:>=0} f&&double.IsFinite(f.AltitudeAglFeet.Value))context.Add(new(distances[i],vertices[i].Position,(f.AltitudeMslFeet-f.AltitudeAglFeet.Value)*.3048,f.TimestampUtc));
            warnings.Add("MSFS Terrain - Simulated Context: sparse estimates from recorded simulator MSL minus AGL. Vertical reference is unverified. These points are not historical DEM science and never count toward yield, coverage or grading.");
        }
        using var lease=request.SourceId is not null?science.BeginAnalysis():null;
        ScientificSource? source=null;ScientificRaster? raster=null;RasterGrid? grid=null;double resolution=double.NaN;
        try{
            if(request.SourceId is {} sourceId){
                source=science.Source(sourceId)??throw new ArgumentException("Selected retained source no longer exists.");
                if(source.Manifest.Kind!=RemoteKind.Terrain||source.Manifest.Swath is not null)throw new ArgumentException("Select a retained affine DEM/DSM terrain source.");
                var band=source.Manifest.Bands.FirstOrDefault(b=>b.Id=="elevation"&&!b.BadBand&&b.Unit=="m")??throw new ArgumentException("The selected source has no supported valid metre elevation band.");
                if(!verify(source))throw new IOException("Retained DEM source failed its original-file integrity check.");var root=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,source.RelativeDirectory);raster=new(root,band);grid=raster.Grid;
                var origin=ScientificRaster.GridPoint(grid,grid.Width/2,grid.Height/2);
                resolution=Math.Min(Geodesy.Distance(origin,ScientificRaster.GridPoint(grid,grid.Width/2+1,grid.Height/2)),Geodesy.Distance(origin,ScientificRaster.GridPoint(grid,grid.Width/2,grid.Height/2+1)));
            }else warnings.Add("No retained DEM selected. Scientific elevations remain unavailable; simulator context, when enabled, stays a separate series.");
            if(source is not null&&(string.IsNullOrWhiteSpace(source.Manifest.VerticalDatum)||source.Manifest.VerticalDatum.Contains("unknown",StringComparison.OrdinalIgnoreCase)))warnings.Add("Source vertical reference is unknown. No MSL or cross-datum conversion is inferred.");
            var count=double.IsFinite(resolution)&&resolution>0?Math.Clamp((int)Math.Min(2049,Math.Ceiling(total/(resolution*.45))+1),2,request.MaximumPoints):request.MaximumPoints;
            bool NeighborsKnown(Coordinates position){
                if(raster is null||grid is null)return false;
                var p=ScientificRaster.Project(position,grid.Crs);var x=p.X-grid.OriginX;var y=p.Y-grid.OriginY;var determinant=grid.PixelWidth*grid.PixelHeight-grid.RowX*grid.ColumnY;
                var col=(long)Math.Floor((x*grid.PixelHeight-y*grid.RowX)/determinant);var row=(long)Math.Floor((y*grid.PixelWidth-x*grid.ColumnY)/determinant);
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)if(raster.Read(ScientificRaster.GridPoint(grid,col+dx+.5,row+dy+.5)) is null)return false;
                return true;
            }
            var points=new List<TerrainProfilePoint>();var segment=1;var priorNeighbors=false;
            for(var i=0;i<count;i++){
                token.ThrowIfCancellationRequested();var distance=total*i/(count-1d);
                var priorSegment=segment;while(segment<distances.Length-1&&distances[segment]<distance)segment++;
                var length=distances[segment]-distances[segment-1];var position=length<=0?vertices[segment].Position:Geodesy.Direct(vertices[segment-1].Position,Geodesy.Bearing(vertices[segment-1].Position,vertices[segment].Position),distance-distances[segment-1]);
                var value=gaps[segment]?null:raster?.Read(position);var neighbors=value is not null&&NeighborsKnown(position);var connect=i>0&&value is not null&&points[^1].ElevationMetres is not null&&neighbors&&priorNeighbors&&double.IsFinite(resolution)&&distance-points[^1].DistanceMetres<=resolution*.5;
                // Never bridge a known missing DEM sample or an unobserved flight interval.
                if(i>0&&gaps.Skip(priorSegment).Take(segment-priorSegment+1).Any(g=>g))connect=false;
                points.Add(new(distance,position,value,connect));priorNeighbors=neighbors;
            }
            if(points.Skip(1).Any(p=>p.ElevationMetres is not null&&!p.Connect))warnings.Add("Bounded point profile: disconnected or coarsely spaced samples are not joined across unverified support. Select a shorter segment for finer detail.");
            var valid=points.Where(p=>p.ElevationMetres is not null).Select(p=>p.ElevationMetres!.Value).ToArray();
            return new(points.ToArray(),context.ToArray(),total,valid.Length==0?null:valid.Min(),valid.Length==0?null:valid.Max(),points[0].ElevationMetres is {} first&&points[^1].ElevationMetres is {} last&&points.All(p=>p.ElevationMetres is not null)?last-first:null,
                source?.Manifest,source?.Id,grid,"Nearest original DEM pixel along WGS84 path distances; no interpolation, vertical-datum transformation, new acquisition or implied complete scientific coverage. One explicit source per profile; incompatible sources are never blended.",warnings.ToArray());
        }finally{raster?.Dispose();}
    }
}
