using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ScientificReusePiece(GeographicBounds Area,ScientificSource Source);
public sealed record ScientificReusePlan(ScientificReusePiece[] Reused,GeographicBounds[] Missing,GeographicBounds[] QualityLimited,string[] Limitations,bool Uncertain=false);
// Request-scoped cache only: a bounded amount of immutable support, never a second
// source inventory or hidden scientific-data copy. Originals remain authoritative.
public sealed class ScientificReuseContext
{
    internal long RemainingReads=1048576;
    internal readonly Dictionary<Guid,(GeographicBounds[] Support,string? Limitation)> Supports=[];
    internal int Rectangles;
}

public static partial class ScientificSourceSelection
{
    private static GeographicBounds? Intersection(GeographicBounds a,GeographicBounds b)
    {
        var r=new GeographicBounds(Math.Max(a.West,b.West),Math.Max(a.South,b.South),Math.Min(a.East,b.East),Math.Min(a.North,b.North));
        return r.East>r.West&&r.North>r.South?r:null;
    }
    // Exact axis-aligned request-window partition, not an area/coverage approximation.
    private static IEnumerable<GeographicBounds> Difference(GeographicBounds a,GeographicBounds covered)
    {
        if(Intersection(a,covered) is not {} i){yield return a;yield break;}
        if(a.West<i.West)yield return new(a.West,a.South,i.West,a.North);
        if(i.East<a.East)yield return new(i.East,a.South,a.East,a.North);
        if(a.South<i.South)yield return new(i.West,a.South,i.East,i.South);
        if(i.North<a.North)yield return new(i.West,i.North,i.East,a.North);
    }
    private static string ReuseFamily(ScientificSource s)
    {
        var m=s.Manifest;var a=m.Acquisition;
        return JsonSerializer.Serialize(new{m.Kind,m.Provider,m.VerticalDatum,m.ObservationUtc,m.License,
            scene=m.Kind==RemoteKind.Terrain&&a?.Workflow=="usgs-3dep-numeric"?null:m.SceneId,
            a?.Workflow,revision=ScientificCatalog.RetainedRevision(s),
            bands=m.Bands.OrderBy(b=>b.Id).Select(b=>new{b.Id,b.Unit,b.Scale,b.Offset,b.NoData,b.WavelengthNm,b.BadBand,
                grid=s.Grids[b.Id] with{OriginX=0,OriginY=0,Width=0,Height=0,
                    // The documented 3DEP composite has variable source resolution.
                    // Each original grid remains native; never claim a common mosaic GSD.
                    PixelWidth=a?.Workflow=="usgs-3dep-numeric"?0:s.Grids[b.Id].PixelWidth,
                    PixelHeight=a?.Workflow=="usgs-3dep-numeric"?0:s.Grids[b.Id].PixelHeight}})},WireJson.Options);
    }
    public static ScientificReusePlan ReuseArea(ScienceStore science,GeographicBounds area,Func<ScientificSource,bool> compatible,
        Func<ScientificSource,bool> verify,CancellationToken token=default,ScientificReuseContext? context=null)
    {
        AutomaticScientificData.ValidateArea(area);context??=new();var gaps=new List<GeographicBounds>{area};var pieces=new List<ScientificReusePiece>();var invalid=new List<GeographicBounds>();var notes=new HashSet<string>();string? family=null;bool uncertain=false;
        foreach(var source in science.StreamSources(new(Area:area)))
        {
            token.ThrowIfCancellationRequested();if(!compatible(source)||!science.InspectAvailability(source).FilesAvailable||source.Manifest.Swath is not null)continue;
            var identity=ReuseFamily(source);if(family is not null&&family!=identity){notes.Add("Overlapping sources from another date, datum, resolution, recipe or processing identity were not combined.");continue;}
            // RequestedArea is a verified subset contract, not the catalog footprint.
            var requested=source.Manifest.Acquisition?.RequestedArea;
            if(requested is null){notes.Add("Partial union for local originals without a verified request-window contract remains Unknown; exact source reuse is still available.");uncertain=true;continue;}
            if(Intersection(area,requested) is not {} candidate||!CoversArea(source,candidate))continue;
            var bands=source.Manifest.Bands.Where(b=>!b.BadBand).ToArray();
            var grid=source.Grids.Values.OrderBy(g=>Math.Abs(g.PixelWidth*g.PixelHeight)).First();
            var support=new List<GeographicBounds>();
            if(context.Supports.TryGetValue(source.Id,out var cached))
            {if(cached.Limitation is {} reason){notes.Add(reason);uncertain=true;continue;}if(!verify(source))throw new IOException("Retained original changed; cached support was discarded. No replacement download was attempted.");support.AddRange(cached.Support);}
            else
            {
            if(context.RemainingReads==0){notes.Add("The request-scoped numeric budget was exhausted; split the review to assess remaining coverage. No unexamined source was marked missing.");uncertain=true;break;}
            if(bands.Length==0||source.Grids.Values.Any(g=>g.Crs!=grid.Crs||g.RowX!=0||g.ColumnY!=0||g.PixelWidth<=0||g.PixelHeight>=0)||
                bands.Sum(b=>(long)source.Grids[b.Id].Width*source.Grids[b.Id].Height)>context.RemainingReads)
            {notes.Add("Numeric union validation exceeded a bounded read or unsupported grid relationship; remaining coverage is uncertain, not proved missing.");uncertain=true;continue;}
            // Fine-grid cells may use aligned coarser QA, never an implicit offset resample.
            if(source.Grids.Values.Any(g=>g.PixelWidth/grid.PixelWidth!=Math.Round(g.PixelWidth/grid.PixelWidth)||g.PixelHeight/grid.PixelHeight!=Math.Round(g.PixelHeight/grid.PixelHeight)||
                (g.OriginX-grid.OriginX)/grid.PixelWidth!=Math.Round((g.OriginX-grid.OriginX)/grid.PixelWidth)||(g.OriginY-grid.OriginY)/grid.PixelHeight!=Math.Round((g.OriginY-grid.OriginY)/grid.PixelHeight)))
            {notes.Add("Unaligned QA/native grids require separate sampling; union coverage remains Unknown.");uncertain=true;continue;}
            if((long)grid.Width*grid.Height*bands.Length>context.RemainingReads){notes.Add("The bounded numeric-read budget was reached; no positive coverage claim was made for unexamined pixels.");uncertain=true;continue;}
            if(!verify(source))throw new IOException("Retained scientific original changed; restore/reimport it. No replacement download was attempted.");
            context.RemainingReads-=(long)grid.Width*grid.Height*bands.Length;
            var readers=new List<ScientificRaster>();var values=bands.Select(_=>new double?[grid.Width*grid.Height]).ToArray();
            try
            {
                readers.AddRange(bands.Select(b=>new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),b)));
                for(var row=0;row<grid.Height;row++){token.ThrowIfCancellationRequested();for(var col=0;col<grid.Width;col++){var p=ScientificRaster.GridPoint(grid,col+.5,row+.5);for(var b=0;b<bands.Length;b++)values[b][row*grid.Width+col]=readers[b].Read(p);}}
            }
            finally{foreach(var reader in readers)reader.Dispose();}
            var numeric=bands.Select((b,i)=>new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,values[i],"Native coverage validation, not a flight observation",source.Grids[b.Id])).ToArray();
            _=RemoteCalculations.Apply(source.Manifest.Kind,numeric);
            var scientific=numeric.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).ToArray();
            bool Valid(int i)=>scientific.Length>0&&scientific.All(b=>b.Values[i] is {} v&&double.IsFinite(v));
            if(grid.Crs!="EPSG:4326")
            {
                if(Enumerable.Range(0,grid.Width*grid.Height).All(Valid))support.Add(requested);
                else{const string reason="Projected source contains excluded pixels: its requested rectangle is not asserted fully valid. Exact originals remain reusable; inspect pixel QA before new acquisition.";notes.Add(reason);if(context.Supports.Count<128)context.Supports[source.Id]=([],reason);uncertain=true;continue;}
            }
            else
            {
                // Coalesce equal horizontal runs vertically, retaining genuine nodata holes.
                var pending=new Dictionary<(int Left,int Right),GeographicBounds>();
                for(var row=0;row<grid.Height;row++)
                {
                    token.ThrowIfCancellationRequested();var next=new Dictionary<(int,int),GeographicBounds>();
                    for(var col=0;col<grid.Width;){if(!Valid(row*grid.Width+col)){col++;continue;}var left=col;while(col<grid.Width&&Valid(row*grid.Width+col))col++;var key=(left,col);
                        var a=ScientificRaster.GridPoint(grid,left,row+1);var b=ScientificRaster.GridPoint(grid,col,row);var r=new GeographicBounds(a.Longitude,a.Latitude,b.Longitude,b.Latitude);
                        if(pending.Remove(key,out var previous))r=r with{North=previous.North};next[key]=r;}
                    support.AddRange(pending.Values);pending=next;
                    if(support.Count+pending.Count>4096)throw new InvalidOperationException("Valid-pixel support exceeds this bounded review. Choose smaller work windows; no source count limit applies.");
                }
                support.AddRange(pending.Values);
            }
            if(context.Supports.Count<128&&context.Rectangles+support.Count<=4096){context.Supports[source.Id]=(support.ToArray(),null);context.Rectangles+=support.Count;}
            }
            if(grid.Crs=="EPSG:4326")
            {var absent=new List<GeographicBounds>{candidate};foreach(var r in support)absent=absent.SelectMany(g=>Difference(g,r)).ToList();invalid.AddRange(absent);}
            foreach(var rectangle in support)
            {
                if(Intersection(rectangle,candidate) is not {} covered)continue;
                foreach(var gap in gaps)if(Intersection(gap,covered) is {} part){pieces.Add(new(part,source));family??=identity;}
                gaps=gaps.SelectMany(g=>Difference(g,covered)).ToList();
                if(pieces.Count+gaps.Count>4096)throw new InvalidOperationException("Reuse partition exceeds this bounded job. Choose smaller windows; retained Library capacity is unchanged.");
            }
            if(gaps.Count==0)break;
        }
        var limited=invalid.SelectMany(i=>gaps.Select(g=>Intersection(i,g))).OfType<GeographicBounds>().ToArray();
        return new(pieces.ToArray(),gaps.ToArray(),limited,notes.ToArray(),uncertain&&gaps.Count>0);
    }
}
