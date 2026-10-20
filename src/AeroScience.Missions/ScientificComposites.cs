using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record CompositeRequest(Guid SourceId,string BandId,string Mode="mission-samples",string Selection="last",GeographicBounds? Area=null,bool CombineCompatibleRegions=true);
public sealed record RasterProductMetadata(string Algorithm,string Mode,Guid MissionId,Guid SourceId,string SourceScene,DateTimeOffset? SourceObservationUtc,
    string VerticalReference,RasterGrid Grid,string[] Bands,string[] Units,string Selection,Guid[] AcquisitionIds,string[] AcquisitionHashes,
    string SourceManifestHash,PayloadConfiguration[] Profiles,string[] Methods,long ValidCells,Guid[]? SourceIds=null,string[]? SourceManifestHashes=null,
    RasterGrid? CompositeGrid=null,int TileColumn=0,int TileRow=0,long TotalTiles=1);
public static class ScientificComposites
{
    private sealed record CompositeIndex(Guid[] Artifacts,long TotalTiles,long SkippedNodataTiles);
    private static readonly string ImplementationIdentity=Identity(new{missions=AssemblyDigest(typeof(ScientificComposites).Assembly),shared=AssemblyDigest(typeof(RasterGrid).Assembly)});
    private static string AssemblyDigest(System.Reflection.Assembly assembly)
    {if(string.IsNullOrEmpty(assembly.Location))return assembly.FullName??"unknown";using var stream=File.OpenRead(assembly.Location);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
    public static string Identity(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options))).ToLowerInvariant();
    public static ScientificArtifact[] Build(ScienceStore science,MissionStore missions,Guid mission,CompositeRequest request,CancellationToken token=default,Action<long,long>? progress=null)
    {
        ResearchExports.RequireCompleted(missions,mission);
        if(request.Mode is not("mission-samples" or "retained-source")||request.Selection is not("first" or "last"))throw new ArgumentException("Choose mission samples or retained source, with deterministic first/last selection.");
        using var analysis=science.BeginAnalysis();
        var available=science.Sources();var source=available.SingleOrDefault(s=>s.Id==request.SourceId)??throw new ArgumentException("Retained source not found.");
        var regions=available.Where(s=>s.Id==source.Id||request.CombineCompatibleRegions&&Compatible(source,s)).OrderBy(s=>s.Id).ToArray();
        var sourceIds=regions.Select(s=>s.Id).ToHashSet();
        var bounded=science.AllProducts(mission).Where(p=>sourceIds.Contains(p.SourceId)).Take(20001).ToArray();
        if(bounded.Length>20000)throw new InvalidOperationException("This analysis supports up to 20,000 acquisition records per scene; choose a shorter mission for this bounded operation.");
        var products=bounded.OrderBy(p=>p.FlightPacketUtc).ThenBy(p=>p.Id).ToArray();
        if(products.Length==0)throw new InvalidOperationException("This source has no recorded mission acquisition. Source catalog availability does not establish mission sampling.");
        var bandIds=request.BandId switch{"natural-color"=>new[]{"B04","B03","B02"},"false-color"=>["B08","B04","B03"],_=>[request.BandId]};
        var first=science.ReadProduct(products[0].Id);var selected=bandIds.Select(id=>ScientificSupport.Band(first,id)).ToArray();
        var grid=selected[0].Grids[0];
        // This slice keeps exact native source pixels. Incompatible grids must be separate outputs, never silently interpolated.
        if(selected.SelectMany(b=>b.Grids).Any(g=>!Aligned(grid,g)))throw new InvalidOperationException("Selected bands use incompatible native grids. Export bands separately; no implicit scientific resampling was performed.");
        var points=request.Area is {} area?area.Corners:products.SelectMany(p=>p.Footprint.Boundary).ToArray();
        if(request.Area is {} region&&(!MissionPlanning.Position(region.South,region.West)||!MissionPlanning.Position(region.North,region.East)||region.East<=region.West||region.North<=region.South))throw new ArgumentException("Choose finite ordered WGS84 area bounds.");
        (double Col,double Row) Pixel(Coordinates p)
        {var q=ScientificRaster.Project(p,grid.Crs);var x=q.X-grid.OriginX;var y=q.Y-grid.OriginY;var det=grid.PixelWidth*grid.PixelHeight-grid.RowX*grid.ColumnY;return((x*grid.PixelHeight-y*grid.RowX)/det,(y*grid.PixelWidth-x*grid.ColumnY)/det);}
        var pixels=points.Select(Pixel).ToArray();
        regions=regions.Where(s=>{var extent=ScientificRaster.GridCoverage(s.Grids.Values.First(g=>Aligned(grid,g))).Select(Pixel).ToArray();return extent.Max(p=>p.Col)>pixels.Min(p=>p.Col)&&extent.Min(p=>p.Col)<pixels.Max(p=>p.Col)&&extent.Max(p=>p.Row)>pixels.Min(p=>p.Row)&&extent.Min(p=>p.Row)<pixels.Max(p=>p.Row);}).ToArray();
        if(regions.Length==0)throw new InvalidOperationException("Retained regions do not cover the requested analysis extent.");
        if(regions.Length>32)throw new InvalidOperationException("This operation supports up to 32 compatible retained regions. Select a narrower region or disable region combining; no whole-scene download was initiated.");
        var nativeBounds=regions.SelectMany(s=>ScientificRaster.GridCoverage(s.Grids.Values.First(g=>Aligned(grid,g)))).Select(Pixel).ToArray();
        var regionExtents=regions.ToDictionary(s=>s.Id,s=>{var bounds=ScientificRaster.GridCoverage(s.Grids.Values.First(g=>Aligned(grid,g))).Select(Pixel).ToArray();return(Left:bounds.Min(p=>p.Col),Right:bounds.Max(p=>p.Col),Top:bounds.Min(p=>p.Row),Bottom:bounds.Max(p=>p.Row));});
        var left=Math.Max((int)Math.Floor(nativeBounds.Min(p=>p.Col)+1e-6),(int)Math.Floor(pixels.Min(p=>p.Col)));var right=Math.Min((int)Math.Ceiling(nativeBounds.Max(p=>p.Col)-1e-6),(int)Math.Ceiling(pixels.Max(p=>p.Col)));
        var top=Math.Max((int)Math.Floor(nativeBounds.Min(p=>p.Row)+1e-6),(int)Math.Floor(pixels.Min(p=>p.Row)));var bottom=Math.Min((int)Math.Ceiling(nativeBounds.Max(p=>p.Row)-1e-6),(int)Math.Ceiling(pixels.Max(p=>p.Row)));
        if(right<=left||bottom<=top)throw new InvalidOperationException("Source and requested mission area do not intersect.");
        if((long)(right-left)*(bottom-top)>16777216)throw new InvalidOperationException("Select a smaller analysis region: this operation supports up to 16 million native cells, without reducing scientific resolution.");
        var commonRecipe=Identity(new{algorithm="scientific-composite-2",implementation=ImplementationIdentity,request,left,right,top,bottom,grid,inputs=products.Select(p=>p.Sha256),sources=regions.Select(s=>s.ManifestSha256)});
        var completed=science.Artifacts(mission).FirstOrDefault(a=>a.Kind=="composite-index"&&a.InputIdentity==commonRecipe);
        if(completed is not null)
        {
            var index=JsonSerializer.Deserialize<CompositeIndex>(File.ReadAllText(science.VerifiedArtifactPath(completed)),WireJson.Options)??throw new IOException("Composite completion index is invalid.");
            var cached=index.Artifacts.Select(science.Artifact).ToArray();foreach(var artifact in cached)_=science.VerifiedArtifactPath(artifact);progress?.Invoke(index.TotalTiles,index.TotalTiles);return cached;
        }
        var expectedCells=Math.Min((long)(right-left)*(bottom-top),products.Sum(p=>(long)Math.Clamp(p.Width,1,32)*Math.Clamp(p.Height,1,32)));
        using var scratch=request.Mode=="mission-samples"?science.CreateAnalysisScratch(Math.Max(1048576,Math.Min(2147483648,expectedCells*128+1048576))):null;
        using var samples=scratch is null?null:new ScientificSampleIndex(scratch,request.Selection);
        var inputHashes=new List<string>();var profiles=new Dictionary<string,PayloadConfiguration>();
        if(request.Mode=="mission-samples")foreach(var p in products)
        {
            token.ThrowIfCancellationRequested();var data=science.ReadProduct(p.Id);inputHashes.Add(p.Sha256);
            if(data.Evidence?.AcquisitionSettings.Payload is {} payload)profiles.TryAdd(Identity(payload),payload);
            var bands=bandIds.Select(id=>ScientificSupport.Band(data,id)).ToArray();
            if(data.SampleLocations.Length>1024)throw new InvalidDataException("An acquisition exceeds its supported 1,024-sample scientific subset.");
            if(bands.SelectMany(b=>b.Grids).Any(g=>!Aligned(grid,g)))throw new InvalidDataException("Saved acquisition grids are not on one compatible native lattice; scientific resampling is unavailable.");
            using var transaction=samples!.Begin();
            for(var i=0;i<data.SampleLocations.Length;i++)
            {
                if(i%64==0)token.ThrowIfCancellationRequested();var values=bands.Select(b=>b.Values[i]).ToArray();if(values.Any(v=>v is null||!double.IsFinite(v.Value)))continue;
                var projected=Pixel(data.SampleLocations[i]);var col=(int)Math.Floor(projected.Col+1e-8);var row=(int)Math.Floor(projected.Row+1e-8);if(col<left||col>=right||row<top||row>=bottom)continue;
                samples.Put(col-left,row-top,right-left,values);
            }
            transaction.Commit();samples.VerifySize();
        }
        else foreach(var p in products){inputHashes.Add(p.Sha256);var data=science.ReadProduct(p.Id);if(data.Evidence?.AcquisitionSettings.Payload is {} payload)profiles.TryAdd(Identity(payload),payload);}
        // Every retained original is rehashed before numeric source mosaicking. Never trust just a filename or length.
        if(request.Mode=="retained-source")foreach(var regionSource in regions)foreach(var file in regionSource.Files)
        {token.ThrowIfCancellationRequested();var root=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,regionSource.RelativeDirectory);using var stream=File.OpenRead(ScientificRaster.SafeFile(root,file.RelativePath));if(stream.Length!=file.Bytes||Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=file.Sha256)throw new IOException("Retained original failed its hash check; no mosaic was accepted.");}
        var readers=new Dictionary<Guid,Dictionary<string,ScientificRaster>>();var artifacts=new List<ScientificArtifact>();
        try
        {
            if(request.Mode=="retained-source")foreach(var regionSource in regions)
            {
                var root=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,regionSource.RelativeDirectory);var set=new Dictionary<string,ScientificRaster>();readers.Add(regionSource.Id,set);
                var required=source.Manifest.Kind switch{RemoteKind.Thermal=>new[]{"ST_B10","QA_PIXEL","QA_RADSAT"},RemoteKind.Multispectral=>bandIds.Concat(["B04","B08","SCL"]).Distinct().ToArray(),RemoteKind.Hyperspectral=>bandIds,_=>new[]{regionSource.Manifest.Bands.First(b=>!b.BadBand).Id}};
                foreach(var band in regionSource.Manifest.Bands.Where(b=>!b.BadBand&&required.Contains(b.Id)))set.Add(band.Id,new(root,band));
            }
            var tiles=(long)Math.Ceiling((right-left)/512d)*(long)Math.Ceiling((bottom-top)/512d);long done=0,skipped=0;
            var compositeOrigin=PixelOrigin(grid,left,top);var compositeGrid=grid with{OriginX=compositeOrigin.X,OriginY=compositeOrigin.Y,Width=right-left,Height=bottom-top};
            for(var y=top;y<bottom;y+=512)for(var x=left;x<right;x+=512)
            {
                token.ThrowIfCancellationRequested();var width=Math.Min(512,right-x);var height=Math.Min(512,bottom-y);var origin=PixelOrigin(grid,x,y);
                var tile=grid with{Width=width,Height=height,OriginX=origin.X,OriginY=origin.Y};var values=bandIds.Select(_=>new double?[width*height]).ToArray();
                if(request.Mode=="mission-samples")
                {
                    samples!.Fill(((y-top)/512)*((right-left+511)/512)+(x-left)/512,width,values,token);
                }
                else
                {
                    // Bounded row batches retain provider scale/offset, bad-band exclusions and original QA masks.
                    for(var row=0;row<height;row++)
                    {
                        token.ThrowIfCancellationRequested();var locations=Enumerable.Range(0,width).Select(col=>ScientificRaster.GridPoint(tile,col+.5,row+.5)).ToArray();
                        foreach(var regionSource in regions)
                        {
                            var extent=regionExtents[regionSource.Id];if(y+row+.5<extent.Top||y+row+.5>=extent.Bottom||x+width<=extent.Left||x>=extent.Right)continue;
                            var set=readers[regionSource.Id];var numeric=regionSource.Manifest.Bands.Where(b=>set.ContainsKey(b.Id)).Select(b=>new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(set[b.Id].Read).ToArray(),"nearest native source pixel",set[b.Id].Grid)).ToArray();
                            var derived=RemoteCalculations.Apply(source.Manifest.Kind,numeric);
                            var selectedValues=bandIds.Select(id=>id=="temperature-celsius"?derived["temperature"].Select(v=>v-273.15).ToArray():derived.GetValueOrDefault(id)??numeric.Single(n=>n.Id==id).Values).ToArray();
                            for(var col=0;col<width;col++)
                            {
                                var index=row*width+col;if(selectedValues.Any(b=>b[col] is null)||request.Selection=="first"&&values[0][index] is not null)continue;
                                for(var b=0;b<bandIds.Length;b++)values[b][index]=selectedValues[b][col];
                            }
                        }
                    }
                }
                var valid=Enumerable.Range(0,width*height).LongCount(i=>values.All(b=>b[i] is {} v&&double.IsFinite(v)));
                if(valid==0){skipped++;progress?.Invoke(++done,tiles);continue;}
                var metadata=new RasterProductMetadata("scientific-composite-2",request.Mode,mission,source.Id,source.Manifest.SceneId,source.Manifest.ObservationUtc,source.Manifest.VerticalDatum,
                    tile,bandIds,selected.Select(b=>b.Unit).ToArray(),request.Selection,products.Select(p=>p.Id).ToArray(),inputHashes.ToArray(),source.ManifestSha256,profiles.Values.ToArray(),
                    [request.Mode=="mission-samples"?"Only finite QA-filtered values actually saved in mission acquisition products are written at their original native pixel indices. Unobserved cells remain NaN nodata; no source reads fill gaps.":"Retained source mosaic: numeric originals are sampled with original scale/offset and source QA. Its extent is selected from mission footprint bounds (or the explicit region); its pixels are not all aircraft acquisitions.",
                     "Exact native lattice, resolution, affine transform and Float64 values; no scientific upsampling/interpolation. Only matching provider/scene/date/publication/unit/datum/calibration/catalog metadata and integer-aligned regional windows can be mosaicked. Incompatible inputs require separate outputs.",
                     "Mission samples: first/last by flight packet UTC then stable acquisition ID. Retained mosaic overlaps: first/last valid region by stable source ID. No averaging or invented gap values.",
                     "512×512 bounded tiles. Mission sample pixel support is a selected source pixel, not complete modeled sensor-footprint coverage. Consult the separately clipped coverage result; tile metadata identifies the complete requested lattice."],valid,
                     regions.Select(s=>s.Id).ToArray(),regions.Select(s=>s.ManifestSha256).ToArray(),compositeGrid,(x-left)/512,(y-top)/512,tiles);
                var identity=Identity(new{commonRecipe,tile});
                var cached=science.Artifacts(mission).FirstOrDefault(a=>a.InputIdentity==identity&&a.Kind==request.Mode);
                if(cached is not null){_=science.VerifiedArtifactPath(cached);artifacts.Add(cached);}
                else artifacts.Add(science.PublishArtifact(mission,request.Mode,"tif",identity,metadata,(long)width*height*bandIds.Length*8+4194304,
                    path=>ScientificRaster.WriteGeoTiff(path,tile,values,metadata.Units,new{metadata.Algorithm,metadata.Mode,metadata.SourceId,metadata.SourceScene,metadata.SourceObservationUtc,metadata.Bands,metadata.Units,metadata.VerticalReference,identity},token),token));
                progress?.Invoke(++done,tiles);
            }
            if(artifacts.Count>0)
            {
                var index=new CompositeIndex(artifacts.Select(a=>a.Id).ToArray(),tiles,skipped);
                _=science.PublishArtifact(mission,"composite-index","json",commonRecipe,new{algorithm="scientific-composite-2",implementation=ImplementationIdentity,request,sourceIds=regions.Select(s=>s.Id),compositeGrid,totalTiles=tiles,skippedNodataTiles=skipped,method="Completed operation index; scientific tiles remain individually hashed. Input acquisition/source hashes and the processing assemblies identify this deterministic recipe."},1048576,path=>File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(index,WireJson.Options)),token);
            }
        }
        finally{foreach(var set in readers.Values)foreach(var reader in set.Values)reader.Dispose();}
        if(artifacts.Count==0)throw new InvalidOperationException("No valid selected values exist in this region; no empty scientific raster was accepted.");return artifacts.ToArray();
    }
    private static (double X,double Y) PixelOrigin(RasterGrid g,int x,int y)=>(g.OriginX+x*g.PixelWidth+y*g.RowX,g.OriginY+x*g.ColumnY+y*g.PixelHeight);
    public static bool Aligned(RasterGrid first,RasterGrid next)
    {
        if(first.Crs!=next.Crs||first.PixelWidth!=next.PixelWidth||first.PixelHeight!=next.PixelHeight||first.RowX!=next.RowX||first.ColumnY!=next.ColumnY)return false;
        var dx=next.OriginX-first.OriginX;var dy=next.OriginY-first.OriginY;var det=first.PixelWidth*first.PixelHeight-first.RowX*first.ColumnY;
        var x=(dx*first.PixelHeight-dy*first.RowX)/det;var y=(dy*first.PixelWidth-dx*first.ColumnY)/det;
        return Math.Abs(x-Math.Round(x))<1e-6&&Math.Abs(y-Math.Round(y))<1e-6;
    }
    public static bool Compatible(ScientificSource first,ScientificSource next)
    {
        var a=first.Manifest;var b=next.Manifest;
        if(a.Kind!=b.Kind||a.Provider!=b.Provider||a.SceneId!=b.SceneId||a.ObservationUtc!=b.ObservationUtc||a.PublicationUtc!=b.PublicationUtc||a.VerticalDatum!=b.VerticalDatum||a.License!=b.License||a.Citation!=b.Citation)return false;
        if(a.ObservationUtc is null&&a.Acquisition is null||a.SceneId.Contains("unknown",StringComparison.OrdinalIgnoreCase))return false;
        if(a.Acquisition?.Workflow!=b.Acquisition?.Workflow||a.Acquisition?.ProviderMetadataJson!=b.Acquisition?.ProviderMetadataJson)return false;
        object Calibration(SourceBand band)=>new{band.Id,band.Channel,band.Unit,band.Scale,band.Offset,band.NoData,band.WavelengthNm,band.BadBand,band.SceneId};
        if(Identity(a.Bands.Select(Calibration))!=Identity(b.Bands.Select(Calibration)))return false;
        return first.Grids.All(g=>next.Grids.TryGetValue(g.Key,out var other)&&Aligned(g.Value,other));
    }
}
