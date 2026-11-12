using System.Text.Json;
using AeroScience.Shared;
using NetTopologySuite.Geometries;

namespace AeroScience.Missions;

// Derived historical source cells only. This code never publishes a RemoteProduct,
// observation, acquisition acknowledgement, grade, card or organization submission.
public static class TerrainElevations
{
    public const string Algorithm="terrain-native-cells-1/pixel-clip-1";
    public const string TileKind="terrain-elevation-tile";
    public const string IndexKind="terrain-elevation-index";
    public static string SourceWriteOperation(Guid source)=>"Terrain elevation cells: source="+source.ToString("N");
    public static readonly string[] Limitations=[
        "Historical source elevations clipped to MODELED scanner support, not new aircraft measurements or LiDAR returns. No original science credit.",
        "Each source keeps its original cells, scale/offset, units, CRS, dates and vertical declaration. DEM, DSM, composite and incompatible references remain separate; no interpolation, blending or datum transformation. Cell counts are per-source lattice counts, not unique geographic area across different grids.",
        "Only valid cells inside the explicit processed chunk range support elevation results. Nodata, quality exclusions and unsupported portions of those chunks remain genuine gaps. Other chunks were not evaluated. Delivered grid availability is not valid-pixel coverage.",
        "Mask coordinates are original native pixel coordinates. Cell fractions measure planar native-cell support, not geodesic scientific coverage or accuracy.",
        "Geographic grids use exact affine clipping. WGS84 UTM edges are adaptively densified to <=0.001 cell midpoint chord error; unsupported projection domains are excluded, never approximated with rectangles.",
        "Processing is local-only. Missing sources require a separate explicit Library discovery, preview and Download & Import approval for a bounded region.",
        "Deleting an original source permanently removes its managed elevation tiles. An index is metadata, not an independent retained copy of deleted values."
    ];
    public static TerrainElevationReview Review(ScienceStore science,MissionStore missions,Guid geometryId,int afterChunk=0,long afterSource=0,long? through=null,CancellationToken token=default,ScientificAccessPreferences? preferences=null,string? search=null)
    {
        var geometry=science.Artifact(geometryId);var chunks=ContinuousTerrainSurveys.Chunks(science,missions,geometry,afterChunk,1,token);
        var windows=chunks.Chunks.SelectMany(c=>c.SourceSearchWindows).ToArray();
        var area=windows.Length==0?null:new GeographicBounds(windows.Min(w=>w.West),windows.Min(w=>w.South),windows.Max(w=>w.East),windows.Max(w=>w.North));
        var page=science.SourcePage(new(Kind:RemoteKind.Terrain,Area:area,Search:search),afterSource,20,through);
        var sources=page.Items.Select(s=>
        {
            var m=s.Manifest;var bands=m.Bands.Where(b=>!b.BadBand&&b.Format is "geotiff" or "envi"&&b.Unit is "m" or "ft").ToArray();
            var reasons=new List<string>();
            var support=new List<TerrainElevationGridSupport>();
            foreach(var band in bands)if(s.Grids.TryGetValue(band.Id,out var grid))foreach(var chunk in chunks.Chunks)
            {
                try{var mask=TerrainNativeGeometry.ClipToGrid(TerrainNativeGeometry.From(chunk.Geometry),grid,50000,token);if(!mask.IsEmpty&&mask.Area>0)support.Add(new(band.Id,chunk.Index,TerrainNativeGeometry.Record(mask)));}
                catch(Exception e)when(e is InvalidDataException or InvalidOperationException){reasons.Add(band.Id+": "+e.Message);}
            }
            if(bands.Length==0||m.Swath is not null)reasons.Add("No supported affine numeric elevation band. An elevation-like name does not establish compatibility.");
            var available=science.InspectAvailability(s).FilesAvailable;
            if(!available)reasons.Add("Original files are unavailable; reconnect the retained Library.");
            // Policy filtering only; potential spatial support is the precise mask
            // above. The Select helper's point predicate must not replace that mask.
            var permitted=ScientificSourceSelection.Select([new(s,available,true,null)],RemoteKind.Terrain,preferences) is not null;
            if(!permitted)reasons.Add("Current scientific source policy does not permit this retained source.");
            var surface=m.Acquisition?.Workflow=="usgs-3dep-numeric"?"composite-dem":m.Provider.Contains("Copernicus",StringComparison.OrdinalIgnoreCase)?"dsm":"requires-local-definition";
            reasons.Add("Source date: "+(m.ObservationUtc?.ToString("O")??"unknown")+". Vertical reference: "+m.VerticalDatum);
            reasons.Add("Review the original elevation band, DEM/DSM definition and any quality policy. Exact hashes are verified when a processing review is created and on resume.");
            reasons.Add(support.Count==0?"No supported grid intersection in this corridor page. This source cannot supply this region.":"Native grid intersections are available below. Nodata/QA and file hashes still require processing; these outlines are not valid elevation coverage.");
            return new TerrainElevationSourceReview(s.Id,m.Name,bands.Length==0||!available||!permitted?"Unavailable":support.Count==0?"NoGridOverlap":"ReviewRequired",reasons.ToArray(),surface,m.VerticalDatum,m.ObservationUtc,
                string.Join("; ",s.Grids.Select(g=>$"{g.Key}: {g.Value.Crs}, {g.Value.Width} × {g.Value.Height}, affine ({g.Value.PixelWidth:R},{g.Value.ColumnY:R};{g.Value.RowX:R},{g.Value.PixelHeight:R})")),s.ManifestSha256,m.Bands,support.ToArray());
        }).ToArray();
        return new(geometryId,geometry.Sha256,geometry.InputIdentity,geometry.MissionId,sources,windows,chunks.NextAfter,Limitations,NextSource:page.Next,ThroughSource:page.Through,GeometryChunks:chunks.Chunks.Select(c=>c.Index).ToArray());
    }
    public static TerrainElevationJob Create(ScienceStore science,MissionStore missions,Guid mission,TerrainElevationRequest request,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        var options=request.Options??new();TerrainElevationSources.Validate(options);
        if(request.Sources is null||request.Sources.Length>16||request.Sources.Select(s=>s.SourceId).Distinct().Count()!=request.Sources.Length)throw new ArgumentException("Choose at most 16 distinct retained terrain sources per bounded job.");
        using var lease=science.BeginAnalysis();var geometry=science.Artifact(request.GeometryArtifactId);
        if(geometry.MissionId!=mission)throw new InvalidOperationException("Geometry belongs to another mission.");
        _=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);
        long bytes=0;var sources=new List<TerrainElevationSource>();
        foreach(var choice in request.Sources)
        {
            var source=TerrainElevationSources.Bind(science,choice,options,preferences,token);bytes=checked(bytes+source.Original.Files.Sum(f=>f.Bytes));
            if(bytes>options.MaximumVerifiedSourceBytes)throw new InvalidOperationException("Combined original verification budget exceeded.");sources.Add(source);
        }
        sources.Sort((a,b)=>a.Original.Id.CompareTo(b.Original.Id));
        var recipe=ScientificComposites.Identity(new{Algorithm,geometry.Id,geometry.Sha256,geometry.InputIdentity,options,sources});
        using var work=sources.Count>0?new TerrainElevationWorkIndex(science,missions,geometry,sources.ToArray(),options,token):null;
        var now=DateTimeOffset.UtcNow;
        return science.SaveTerrainReview(new(Guid.NewGuid(),science.LibraryId,mission,1,"Reviewed",geometry.Id,geometry.Sha256,geometry.InputIdentity,recipe,options,sources.ToArray(),now,now,
            TotalTiles:work?.TotalTiles,
            Message:sources.Count==0?"No sources selected. Prepare a bounded region explicitly; no network work has been authorized.":"Verified retained originals selected. Processing has not started; uncovered corridor areas and invalid source pixels remain gaps."));
    }
    static ScientificArtifact Geometry(ScienceStore science,MissionStore missions,TerrainElevationJob job,CancellationToken token)
    {
        var geometry=science.Artifact(job.GeometryArtifactId);
        if(geometry.MissionId!=job.MissionId||geometry.Sha256!=job.GeometrySha256||geometry.InputIdentity!=job.GeometryInputFingerprint)throw new InvalidOperationException("Frozen geometry identity changed. Rebuild/review explicitly.");
        _=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);return geometry;
    }
    static string TileIdentity(TerrainElevationJob job,TerrainTileWork tile)=>ScientificComposites.Identity(new{job.Recipe,source=job.Sources[tile.Source].Original.Id,tile.Column,tile.Row});
    public static ScientificArtifact Process(ScienceStore science,MissionStore missions,Guid id,ScientificAccessPreferences? preferences=null,CancellationToken token=default,Action<long,long>? progress=null)
    {
        using var lease=science.BeginAnalysis();var job=science.TerrainJob(id);
        if(job.State!="Queued")throw new InvalidOperationException("Explicitly queue this reviewed terrain job before processing.");
        science.ChangeTerrainJob(id,j=>j.State=="Queued"?j with{State="Running",Message="Verifying immutable geometry, originals and checkpoint tiles; no network access."}:throw new OperationCanceledException());
        try
        {
            TerrainElevationSources.Validate(job.Options);var geometry=Geometry(science,missions,job,token);
            var recipe=ScientificComposites.Identity(new{Algorithm,geometry.Id,geometry.Sha256,geometry.InputIdentity,options=job.Options,sources=job.Sources});
            if(recipe!=job.Recipe)throw new IOException("Frozen terrain processing recipe changed; no checkpoint can be reused.");
            var checkpoints=science.TerrainCheckpoints(id).ToArray();
            if(checkpoints.Length!=job.CompletedTiles||checkpoints.Sum(c=>c.ValidCells)!=job.ValidCells||checkpoints.Sum(c=>c.MissingCells)!=job.MissingCells||checkpoints.Sum(c=>c.Bytes)!=job.OutputBytes)
                throw new IOException("Durable terrain checkpoint counters are inconsistent; review/rebuild explicitly.");
            foreach(var source in job.Sources)
            {
                var current=TerrainElevationSources.Bind(science,TerrainElevationSources.Choice(source),job.Options,preferences,token);
                if(ScientificComposites.Identity(current)!=ScientificComposites.Identity(source))throw new IOException("Frozen source definition changed; review again.");
            }
            using var index=new TerrainElevationWorkIndex(science,missions,geometry,job.Sources,job.Options,token);
            science.ChangeTerrainJob(id,j=>j with{TotalTiles=index.TotalTiles,Message="Processing original native cells in bounded tiles. Missing pixels are not filled."});
            foreach(var tile in index.Tiles())
            {
                token.ThrowIfCancellationRequested();var state=science.TerrainJob(id);if(state.State is "Pausing" or "Paused" or "Cancelled")throw new OperationCanceledException();
                var identity=TileIdentity(job,tile);var source=job.Sources[tile.Source];var checkpoint=science.TerrainCheckpoint(id,source.Original.Id,tile.Column,tile.Row);
                if(checkpoint is not null)
                {
                    var a=science.Artifact(checkpoint.ArtifactId);if(a.Kind!=TileKind||a.InputIdentity!=identity||a.Sha256!=checkpoint.Sha256)throw new IOException("Terrain checkpoint identity mismatch.");
                    var saved=ReadOutput(science,a);ValidateOutput(job,tile,saved);
                    if(checkpoint.SourceId!=source.Original.Id||checkpoint.Bytes!=a.Bytes||checkpoint.ValidCells!=saved.Cells.LongCount(c=>c.Quality=="valid")||checkpoint.MissingCells!=saved.Cells.LongCount(c=>c.Quality!="valid"))throw new IOException("Terrain checkpoint counts/source mismatch.");continue;
                }
                // Publication and checkpoint are separate atomic commits. Recover a verified
                // visible published tile if interruption occurred between them; never copy it.
                var existing=science.TerrainTileArtifact(job.MissionId,identity);
                ScientificArtifact artifact;TerrainElevationTile output;
                if(existing is not null)
                {
                    artifact=existing;output=ReadOutput(science,existing);ValidateOutput(job,tile,output);
                }
                else
                {
                    output=ReadTile(job,tile,index,token);var data=JsonSerializer.SerializeToUtf8Bytes(output,WireJson.Options);
                    if(data.LongLength>16777216||state.OutputBytes+data.LongLength>job.Options.MaximumOutputBytes)throw new StorageLimitException("Terrain output byte limit reached. Completed tiles remain visible; increase explicitly in a new bounded review.");
                    artifact=science.PublishArtifact(job.MissionId,TileKind,"json",identity,new{sourceIds=new[]{source.Original.Id},sourceHashes=new[]{source.Original.ManifestSha256},geometryArtifactId=geometry.Id,geometrySha256=geometry.Sha256,job.Recipe,tile.Column,tile.Row,Algorithm,originalScienceCredit=false},data.LongLength,path=>File.WriteAllBytes(path,data),token,retainedSourceOwner:source.Original.Id);
                }
                if(state.OutputBytes+artifact.Bytes>job.Options.MaximumOutputBytes)throw new StorageLimitException("Reused tiles exceed the reviewed output bound.");
                science.SaveTerrainCheckpoint(id,new(source.Original.Id,tile.Column,tile.Row,artifact.Id,artifact.Sha256,output.Cells.LongCount(c=>c.Quality=="valid"),output.Cells.LongCount(c=>c.Quality!="valid"),artifact.Bytes));
                var currentJob=science.TerrainJob(id);progress?.Invoke(currentJob.CompletedTiles,index.TotalTiles);
            }
            token.ThrowIfCancellationRequested();_=Geometry(science,missions,job,token);var completed=science.TerrainJob(id);
            if(completed.State!="Running")throw new OperationCanceledException();
            // PartiallyComplete is deliberate: delivered-grid and QA gaps are explicit,
            // and this bounded checkpoint makes no mission-wide complete-coverage claim.
            var result=new TerrainElevationIndex(1,job.MissionId,geometry.Id,geometry.Sha256,geometry.InputIdentity,Algorithm,job.Options,job.Recipe,job.Sources,science.TerrainCheckpoints(id).ToArray(),completed.ValidCells,completed.MissingCells,"PartiallyComplete",Limitations);
            var json=JsonSerializer.SerializeToUtf8Bytes(result,WireJson.Options);
            if(completed.OutputBytes+json.LongLength>job.Options.MaximumOutputBytes)throw new StorageLimitException("Terrain index exceeds the reviewed total output bound.");
            var final=science.PublishArtifact(job.MissionId,IndexKind,"json",job.Recipe,new{sourceIds=job.Sources.Select(s=>s.Original.Id).ToArray(),sourceHashes=job.Sources.Select(s=>s.Original.ManifestSha256).ToArray(),geometryArtifactId=geometry.Id,geometrySha256=geometry.Sha256,Algorithm,originalScienceCredit=false},json.LongLength,path=>File.WriteAllBytes(path,json),token);
            science.ChangeTerrainJob(id,j=>j.State=="Running"?j with{State="PartiallyComplete",ResultArtifactId=final.Id,OutputBytes=j.OutputBytes+final.Bytes,Message="Source-supported tiles published. Unrepresented corridor and rejected cells remain gaps; no whole-flight completeness or original science credit is claimed."}:throw new OperationCanceledException());return final;
        }
        catch(OperationCanceledException)
        {
            science.ChangeTerrainJob(id,j=>j with{State=j.State=="Cancelled"?"Cancelled":j.State=="Pausing"?"Paused":"Interrupted",Message="Stopped safely. Completed tiles remain visible. Explicit resume verifies geometry, original bytes and checkpoints before continuing."});throw;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {science.ChangeTerrainJob(id,j=>j with{State=e is InvalidOperationException&&e.Message.Contains("invalidat",StringComparison.OrdinalIgnoreCase)?"Invalidated":"Failed",Message=e.Message});throw;}
    }
    static TerrainElevationTile ReadOutput(ScienceStore science,ScientificArtifact artifact)
    {
        if(artifact.Bytes>16777216)throw new IOException("Terrain checkpoint exceeds the bounded tile size.");
        return JsonSerializer.Deserialize<TerrainElevationTile>(File.ReadAllBytes(science.VerifiedArtifactPath(artifact)),WireJson.Options)??throw new IOException("Invalid terrain checkpoint product.");
    }
    static void ValidateOutput(TerrainElevationJob job,TerrainTileWork tile,TerrainElevationTile value)
    {
        if(value.Version!=1||value.MissionId!=job.MissionId||value.FlightId!=job.MissionId||value.GeometryArtifactId!=job.GeometryArtifactId||value.GeometrySha256!=job.GeometrySha256||value.GeometryInputFingerprint!=job.GeometryInputFingerprint||value.Algorithm!=Algorithm||value.GrantsOriginalScienceCredit||value.TileColumn!=tile.Column||value.TileRow!=tile.Row||value.TileSize!=job.Options.TileSize||value.Cells.Length>job.Options.TileSize*job.Options.TileSize||ScientificComposites.Identity(value.Source)!=ScientificComposites.Identity(job.Sources[tile.Source]))throw new IOException("Terrain checkpoint payload identity mismatch.");
    }
    static TerrainElevationTile ReadTile(TerrainElevationJob job,TerrainTileWork tile,TerrainElevationWorkIndex index,CancellationToken token)
    {
        var source=job.Sources[tile.Source];var size=job.Options.TileSize;var groups=index.Shapes(tile,token).GroupBy(s=>(s.Binding.SessionId,s.Binding.ConfigurationId,s.Binding.ProfileId,s.Binding.ProfileVersion))
            .Select(g=>(g.Key,Chunks:g.Select(s=>s.Binding.Chunk).Distinct().Order().ToArray(),Shape:ScientificLocalGeometry.Union(g.Select(s=>s.Geometry))))
            .OrderBy(g=>g.Key.SessionId).ThenBy(g=>g.Key.ConfigurationId).ThenBy(g=>g.Key.ProfileId,StringComparer.Ordinal).ToArray();
        var union=ScientificLocalGeometry.Union(groups.Select(g=>g.Shape));var cells=new List<TerrainElevationCell>();
        using var raster=new ScientificRaster(Path.Combine(index.ScienceDirectory,source.Original.RelativeDirectory),source.Band);
        using var quality=source.QualityBand is {} qb?new ScientificRaster(Path.Combine(index.ScienceDirectory,source.Original.RelativeDirectory),qb):null;
        if(raster.Grid!=source.Grid||quality is not null&&quality.Grid!=source.Grid)throw new IOException("Native elevation/QA grid changed.");
        var outputVertices=0;
        for(var row=tile.Row*size;row<Math.Min(source.Grid.Height,((long)tile.Row+1)*size);row++)for(var col=tile.Column*size;col<Math.Min(source.Grid.Width,((long)tile.Column+1)*size);col++)
        {
            token.ThrowIfCancellationRequested();var box=TerrainNativeGeometry.Factory.ToGeometry(new Envelope(col,col+1,row,row+1));var mask=union.Intersection(box);if(mask.IsEmpty||mask.Area<=0)continue;
            var supports=new List<TerrainElevationSupport>();foreach(var group in groups)
            {
                var part=group.Shape.Intersection(box);if(!part.IsEmpty&&part.Area>0){outputVertices=checked(outputVertices+part.NumPoints);if(outputVertices>100000)throw new InvalidOperationException("Clipped tile mask exceeds the output vertex bound; reduce tile size.");supports.Add(new(group.Key.SessionId,group.Key.ConfigurationId,group.Key.ProfileId,group.Key.ProfileVersion,group.Chunks,TerrainNativeGeometry.Record(part)));}
            }
            var raw=raster.ReadNativeCell(col,row);var qa=quality?.ReadNativeCell(col,row);var accepted=quality is null||qa is {} sample&&source.AcceptedQualityValues.Contains(sample.DecodedSourceValue);
            // QA-excluded source numbers are not exported as valid terrain values.
            cells.Add(new(col,row,accepted?raw?.DecodedSourceValue:null,accepted?raw?.Value:null,raw is null?"nodata":accepted?"valid":"quality-excluded",Math.Clamp(mask.Area,0,1),supports.ToArray()));
        }
        return new(1,job.MissionId,job.MissionId,job.GeometryArtifactId,job.GeometrySha256,job.GeometryInputFingerprint,Algorithm,source,tile.Column,tile.Row,size,cells.ToArray());
    }
    public static string RequireCurrent(ScienceStore science,MissionStore missions,ScientificArtifact artifact,CancellationToken token=default)
    {
        if(artifact.Kind is not(TileKind or IndexKind))throw new ArgumentException("Choose a terrain elevation product.");
        using var metadata=JsonDocument.Parse(artifact.MetadataJson);var root=metadata.RootElement;var geometry=science.Artifact(root.GetProperty("geometryArtifactId").GetGuid());
        if(geometry.Sha256!=root.GetProperty("geometrySha256").GetString())throw new InvalidOperationException("Terrain geometry checksum changed.");
        _=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);
        var ids=root.GetProperty("sourceIds").EnumerateArray().Select(s=>s.GetGuid()).ToArray();var hashes=root.GetProperty("sourceHashes").EnumerateArray().Select(s=>s.GetString()).ToArray();
        if(ids.Length!=hashes.Length)throw new IOException("Terrain source identity metadata is inconsistent.");
        for(var i=0;i<ids.Length;i++)if(science.Source(ids[i])?.ManifestSha256!=hashes[i])throw new InvalidOperationException("Terrain result invalidated by source deletion/revision; no numeric copy is served.");
        return science.VerifiedArtifactPath(artifact);
    }
}
