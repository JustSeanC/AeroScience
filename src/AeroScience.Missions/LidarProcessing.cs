using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public static class LidarProcessing
{
    public const string Algorithm="historical-lidar-corridor-1/native-records-1",TileKind="lidar-point-tile",IndexKind="lidar-point-index";
    public static string SourceWriteOperation(Guid source)=>"Historical LiDAR points: source="+source.ToString("N");
    public static readonly string[] Limitations=[
        "Authentic historical delivered LiDAR points clipped to the selected modeled scanner corridor. These returns were not emitted or acquired by the MSFS aircraft.",
        "Original flight acquisitions, modeled sensor geometry, raster DEM/DSM cells and historical point records are separate evidence classes. No acquisition/yield/grade/card/organization credit is granted.",
        "Points outside selected polygons, holes, invalid telemetry gaps and other geometry chunks are excluded. Search rectangles are only prefilters. One original record is counted once, with every supported session/configuration association retained.",
        "Original delivered XYZ, scale/offset, attributes, class/withheld flags and extra bytes remain source values. Geographic XY supports clipping only; no vertical/height-unit transformation, MSL comparison or accuracy certification.",
        "Overlapping surveys stay separate. Counts describe retained records, not geographic completeness, full-density coverage, laser pulse timing or original flight observations. Missing source support remains unavailable.",
        "Source/geometry changes invalidate dependent results. Permanent source deletion removes managed point-value tiles; user originals and unrelated flight science remain protected.",
        "JSON is a derived analysis format with original record references, not an exact LAS/LAZ export or a 3D Results viewer."];
    public static void Validate(LidarProcessingOptions o)
    {
        if(o.RecordsPerTile is <1 or >4096||o.MaximumCandidatePoints is <1 or >20000000||o.MaximumTiles is <1 or >20000||o.MaximumOutputBytes is <4096 or >536870912||o.MaximumScratchBytes is <1048576 or >536870912||o.MaximumVerifiedSourceBytes is <1024 or >8589934592||o.FirstGeometryChunk is <1 or >20000||o.LastGeometryChunk is <1 or >20000||o.LastGeometryChunk<o.FirstGeometryChunk||o.MaximumGeometryVertices is <16 or >1000000||o.MaximumShapesPerPoint is <1 or >512||o.MaximumSupportLinksPerTile is <1 or >32768)throw new ArgumentException("Choose bounded historical point processing, regional geometry, source verification, support and output limits.");
    }
    static LidarSourceSummary Summary(ScientificSource s)
    {
        var cloud=s.Manifest.PointCloud!;var o=cloud.Originals[0];var h=o.Header;var file=s.Files.Single(f=>f.RelativePath==o.File);
        return new(s.Id,s.ManifestSha256,s.Manifest.Name,s.Manifest.SceneId,cloud.EptProject,o.EptNode,o.File,file.Sha256,h.HorizontalCrs!,h.VerticalReference,h.VerticalUnit,s.Manifest.ObservationUtc,s.Manifest.PublicationUtc,s.Manifest.License,s.Manifest.Citation,h.Dimensions,h.Scale,h.Offset,h.PointCount,h.Limitations);
    }
    public static object Review(ScienceStore science,MissionStore missions,Guid geometryId,int afterChunk=0,long afterSource=0,long? through=null,string? search=null,CancellationToken token=default)
    {
        using var lease=science.BeginAnalysis();var geometry=science.Artifact(geometryId);var chunks=ContinuousTerrainSurveys.Chunks(science,missions,geometry,afterChunk,1,token);var windows=chunks.Chunks.SelectMany(c=>c.SourceSearchWindows).ToArray();
        var area=windows.Length==0?null:new GeographicBounds(windows.Min(w=>w.West),windows.Min(w=>w.South),windows.Max(w=>w.East),windows.Max(w=>w.North));
        var page=science.SourcePage(new(Kind:RemoteKind.Terrain,Area:area,Search:search,PointCloud:true),afterSource,20,through);
        return new{geometryArtifactId=geometry.Id,geometrySha256=geometry.Sha256,inputFingerprint=geometry.InputIdentity,geometry.MissionId,
            sources=page.Items.Select(s=>new{source=Summary(s),state=science.InspectAvailability(s).FilesAvailable?"ReviewRequired":"Unavailable",limitation="Indexed source bounds are candidates. Verify exact original hashes and vertical-reference limitations before processing; a bounding box does not establish actual corridor point support."}).ToArray(),nextSource=page.Next,throughSource=page.Through,geometryChunks=chunks.Chunks.Select(c=>c.Index).ToArray(),searchWindows=windows,nextChunk=chunks.NextAfter,limitations=Limitations};
    }
    static ScientificArtifact Geometry(ScienceStore science,MissionStore missions,LidarProcessingJob job,CancellationToken token)
    {
        var geometry=science.Artifact(job.GeometryArtifactId);if(geometry.MissionId!=job.MissionId||geometry.Sha256!=job.GeometrySha256||geometry.InputIdentity!=job.GeometryInputFingerprint)throw new InvalidOperationException("Frozen corridor identity changed; rebuild and review explicitly.");_=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);return geometry;
    }
    static ScientificSource[] Bind(ScienceStore science,LidarSourceChoice[] choices,LidarProcessingOptions options,ScientificAccessPreferences? preferences,CancellationToken token)
    {
        if(choices is null||choices.Length is <1 or >16||choices.Select(c=>c.SourceId).Distinct().Count()!=choices.Length)throw new ArgumentException("Select 1–16 distinct verified historical point sources.");
        var result=choices.Select(c=>LidarImports.Bind(science,c.SourceId,c.ManifestSha256,c.UnverifiedVerticalAcknowledged,preferences,token)).ToArray();
        if(result.Sum(s=>s.Files.Sum(f=>f.Bytes))>options.MaximumVerifiedSourceBytes)throw new IOException("Combined original verification exceeds the reviewed source-byte limit.");
        if(result.Sum(s=>s.Manifest.PointCloud!.Originals[0].Header.PointCount)>options.MaximumCandidatePoints)throw new IOException("The selected originals exceed the candidate-record scan budget. Choose smaller indexed nodes/local tiles or explicitly review a larger supported bound.");
        var originals=result.Select(s=>s.Files.Single(f=>f.RelativePath==s.Manifest.PointCloud!.Originals[0].File).Sha256).ToArray();if(originals.Distinct().Count()!=originals.Length)throw new ArgumentException("The same verified point file is selected through multiple source identities. Choose one; it cannot be counted twice.");return result;
    }
    static string Recipe(ScientificArtifact geometry,LidarSourceChoice[] choices,LidarProcessingOptions options)=>ScientificComposites.Identity(new{Algorithm,decoderSource=LidarDecoder.SourceIdentity,geometry.Id,geometry.Sha256,geometry.InputIdentity,options,sources=choices});
    public static LidarProcessingJob Create(ScienceStore science,MissionStore missions,Guid mission,LidarProcessingRequest request,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        var options=request.Options??new();Validate(options);if(request.RequestId==Guid.Empty)throw new ArgumentException("Use an identified historical-point review request.");using var lease=science.BeginAnalysis();var geometry=science.Artifact(request.GeometryArtifactId);if(geometry.MissionId!=mission)throw new InvalidOperationException("The corridor belongs to another mission.");_=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);
        var choices=request.Sources?.OrderBy(c=>c.SourceId).ToArray()??[];var sources=Bind(science,choices,options,preferences,token);var total=sources.Sum(s=>(s.Manifest.PointCloud!.Originals[0].Header.PointCount+options.RecordsPerTile-1)/options.RecordsPerTile);if(total>options.MaximumTiles)throw new IOException("Selected originals exceed the reviewed point-tile limit.");
        using var index=new LidarPointGeometry(science,missions,geometry,options,token);var now=DateTimeOffset.UtcNow;
        return science.SaveLidarReview(new(Guid.NewGuid(),science.LibraryId,mission,1,"Reviewed",geometry.Id,geometry.Sha256,geometry.InputIdentity,Recipe(geometry,choices,options),options,choices,now,now,TotalTiles:total,Message:"Verified historical originals and modeled corridor selected. Start processing explicitly; this review authorizes no network access or science collection."),request.RequestId);
    }
    static string TileIdentity(LidarProcessingJob job,ScientificSource source,long first,int count)=>ScientificComposites.Identity(new{job.Recipe,sourceId=source.Id,fileSha256=source.Files.Single(f=>f.RelativePath==source.Manifest.PointCloud!.Originals[0].File).Sha256,first,count});
    public static async Task<ScientificArtifact> Process(ScienceStore science,MissionStore missions,Guid id,ScientificAccessPreferences? preferences=null,CancellationToken token=default,Action<long,long>? progress=null)
    {
        using var lease=science.BeginAnalysis();var job=science.LidarJob(id);if(job.State!="Queued")throw new InvalidOperationException("Explicitly queue this reviewed historical-point job before processing.");
        science.ChangeLidarJob(id,j=>j.State=="Queued"?j with{State="Running",Message="Verifying frozen corridor, original point files and checkpoints; no network access."}:throw new OperationCanceledException());
        try
        {
            Validate(job.Options);var geometry=Geometry(science,missions,job,token);if(Recipe(geometry,job.Sources,job.Options)!=job.Recipe)throw new IOException("Frozen point-processing recipe or decoder source changed. Existing checkpoints cannot be reinterpreted.");
            var sources=Bind(science,job.Sources,job.Options,preferences,token);var checkpoints=science.LidarCheckpoints(id).ToArray();
            if(checkpoints.Length!=job.CompletedTiles||checkpoints.Sum(c=>c.RecordsRead)!=job.ScannedPoints||checkpoints.Sum(c=>c.RetainedPoints)!=job.RetainedPoints||checkpoints.Sum(c=>c.Bytes)!=job.OutputBytes)throw new IOException("Durable point-checkpoint counters are inconsistent; no unverified progress is accepted.");
            var total=sources.Sum(s=>(s.Manifest.PointCloud!.Originals[0].Header.PointCount+job.Options.RecordsPerTile-1)/job.Options.RecordsPerTile);if(total!=job.TotalTiles||total>job.Options.MaximumTiles)throw new IOException("Frozen original record/tile counts differ.");
            using var index=new LidarPointGeometry(science,missions,geometry,job.Options,token);
            foreach(var source in sources)
            {
                var original=source.Manifest.PointCloud!.Originals[0];var file=source.Files.Single(f=>f.RelativePath==original.File);var path=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),original.File);
                for(var first=0L;first<original.Header.PointCount;first+=job.Options.RecordsPerTile)
                {
                    token.ThrowIfCancellationRequested();var current=science.LidarJob(id);if(current.State!="Running")throw new OperationCanceledException();var count=(int)Math.Min(job.Options.RecordsPerTile,original.Header.PointCount-first);var identity=TileIdentity(job,source,first,count);var checkpoint=science.LidarCheckpoint(id,source.Id,first);
                    if(checkpoint is not null)
                    {
                        var artifact=science.Artifact(checkpoint.ArtifactId);var output=ReadTile(science,artifact);ValidateTile(job,source,first,count,identity,artifact,output);
                        if(checkpoint.Bytes!=artifact.Bytes||checkpoint.Sha256!=artifact.Sha256||checkpoint.RecordsRead!=count||checkpoint.RetainedPoints!=output.Points.Length)throw new IOException("Historical point checkpoint counts/bytes changed.");continue;
                    }
                    var existing=science.LidarTileArtifact(job.MissionId,identity);ScientificArtifact published;LidarPointTile tile;
                    if(existing is not null){published=existing;tile=ReadTile(science,published);ValidateTile(job,source,first,count,identity,published,tile);}
                    else
                    {
                        var decoded=await LidarDecoder.Read(path,original.Header,first,count,token);var retained=new List<LidarRetainedPoint>();var links=0;
                        foreach(var p in decoded){token.ThrowIfCancellationRequested();if(!double.IsFinite(p.Z))throw new IOException("Nonfinite original Z coordinate.");var location=LidarCoordinates.Geographic(original.Header.HorizontalCrs!,p.X,p.Y);var support=index.Support(location,token);if(support.Length==0)continue;links=checked(links+support.Sum(s=>s.GeometryChunks.Length));if(links>job.Options.MaximumSupportLinksPerTile)throw new IOException("Point/session support links exceed this bounded tile. Reduce selected chunks or records per tile; no evidence association is truncated.");retained.Add(new(p,location,support));}
                        tile=new(1,job.MissionId,geometry.Id,geometry.Sha256,geometry.InputIdentity,Algorithm,LidarDecoder.SourceIdentity,job.Recipe,source,original.File,file.Sha256,first,count,retained.ToArray());
                        var maximum=Math.Min(16777216,job.Options.MaximumOutputBytes-current.OutputBytes);if(maximum<1)throw new StorageLimitException("Reviewed historical-point output cap reached. Completed tiles remain separate and visible.");
                        published=science.PublishArtifact(job.MissionId,TileKind,"json",identity,new{sourceIds=new[]{source.Id},sourceHashes=new[]{source.ManifestSha256},geometryArtifactId=geometry.Id,geometrySha256=geometry.Sha256,job.Recipe,first,count,Algorithm,originalScienceCredit=false},maximum,p=>Write(p,tile,maximum,token),token,retainedSourceOwner:source.Id);
                    }
                    if(current.OutputBytes+published.Bytes>job.Options.MaximumOutputBytes)throw new StorageLimitException("Completed/reused point tiles exceed the reviewed output bound.");
                    science.SaveLidarCheckpoint(id,new(source.Id,first,count,tile.Points.LongLength,published.Id,published.Sha256,published.Bytes));var updated=science.LidarJob(id);progress?.Invoke(updated.CompletedTiles,total);
                }
            }
            token.ThrowIfCancellationRequested();_=Geometry(science,missions,job,token);_=Bind(science,job.Sources,job.Options,preferences,token);var complete=science.LidarJob(id);
            if(complete.State!="Running")throw new OperationCanceledException();if(complete.CompletedTiles!=total||complete.ScannedPoints!=sources.Sum(s=>s.Manifest.PointCloud!.Originals[0].Header.PointCount))throw new IOException("Checkpoint scope contains extra/missing original records.");
            var result=new LidarPointIndex(1,job.MissionId,geometry.Id,geometry.Sha256,geometry.InputIdentity,Algorithm,LidarDecoder.SourceIdentity,job.Recipe,job.Options,sources.Select(Summary).ToArray(),science.LidarCheckpoints(id).ToArray(),complete.ScannedPoints,complete.RetainedPoints,"PartiallyComplete",Limitations);
            var bound=Math.Min(16777216,job.Options.MaximumOutputBytes-complete.OutputBytes);if(bound<1)throw new StorageLimitException("Historical-point index exceeds the reviewed output cap.");
            var final=science.PublishArtifact(job.MissionId,IndexKind,"json",job.Recipe,new{sourceIds=job.Sources.Select(s=>s.SourceId).ToArray(),sourceHashes=job.Sources.Select(s=>s.ManifestSha256).ToArray(),geometryArtifactId=geometry.Id,geometrySha256=geometry.Sha256,job.Recipe,Algorithm,originalScienceCredit=false},bound,p=>Write(p,result,bound,token),token);
            science.ChangeLidarJob(id,j=>j.State=="Running"?j with{State="PartiallyComplete",ResultArtifactId=final.Id,OutputBytes=j.OutputBytes+final.Bytes,Message="Selected historical point records processed. Source gaps/density and unselected corridor regions remain incomplete; no new flight science credit."}:throw new OperationCanceledException());return final;
        }
        catch(OperationCanceledException){science.ChangeLidarJob(id,j=>j with{State=j.State=="Cancelled"?"Cancelled":j.State=="Pausing"?"Paused":"Interrupted",Message="Stopped safely. Explicit resume revalidates original files, frozen geometry and completed point-tile hashes; no network restart."});throw;}
        catch(Exception e)when(e is not OutOfMemoryException){science.ChangeLidarJob(id,j=>j with{State=j.State=="Invalidated"?"Invalidated":"Failed",Message=e.Message});throw;}
    }
    static void ValidateTile(LidarProcessingJob job,ScientificSource source,long first,int count,string identity,ScientificArtifact artifact,LidarPointTile t)
    {
        if(artifact.Kind!=TileKind||artifact.InputIdentity!=identity||t.Version!=1||t.MissionId!=job.MissionId||t.GeometryArtifactId!=job.GeometryArtifactId||t.GeometrySha256!=job.GeometrySha256||t.GeometryInputFingerprint!=job.GeometryInputFingerprint||t.Algorithm!=Algorithm||t.DecoderSource!=LidarDecoder.SourceIdentity||t.Recipe!=job.Recipe||t.FirstRecord!=first||t.RecordsRead!=count||t.GrantsOriginalScienceCredit||ScientificCatalog.Hash(t.Source)!=ScientificCatalog.Hash(source)||t.OriginalFile!=source.Manifest.PointCloud!.Originals[0].File||t.OriginalSha256!=source.Files.Single(f=>f.RelativePath==t.OriginalFile).Sha256||t.Points.Length>count||t.Points.Any(p=>p.Original.Record<first||p.Original.Record>=first+count||p.Support.Length==0)||t.Points.Select(p=>p.Original.Record).Distinct().Count()!=t.Points.Length)throw new IOException("Historical point tile/checkpoint identity or original record references differ.");
    }
    public static LidarPointTile ReadTile(ScienceStore science,ScientificArtifact artifact)
    {if(artifact.Kind!=TileKind||artifact.Bytes>16777216)throw new IOException("Choose a bounded historical point tile.");using var input=File.OpenRead(science.VerifiedArtifactPath(artifact));return JsonSerializer.Deserialize<LidarPointTile>(input,WireJson.Options)??throw new IOException("Invalid historical point tile.");}
    public static string RequireCurrent(ScienceStore science,MissionStore missions,ScientificArtifact artifact,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        if(artifact.Bytes>16777216||artifact.Kind is not(TileKind or IndexKind))throw new IOException("Unsupported historical point product.");var path=science.VerifiedArtifactPath(artifact);using var data=JsonDocument.Parse(File.ReadAllBytes(path));var j=data.RootElement;
        if(j.GetProperty("version").GetInt32()!=1||j.GetProperty("missionId").GetGuid()!=artifact.MissionId||j.GetProperty("algorithm").GetString()!=Algorithm||j.GetProperty("decoderSource").GetString()!=LidarDecoder.SourceIdentity||j.GetProperty("grantsOriginalScienceCredit").GetBoolean())throw new IOException("Historical point product evidence/algorithm identity differs.");
        var geometry=science.Artifact(j.GetProperty("geometryArtifactId").GetGuid());if(geometry.MissionId!=artifact.MissionId||geometry.Sha256!=j.GetProperty("geometrySha256").GetString()||geometry.InputIdentity!=j.GetProperty("geometryInputFingerprint").GetString())throw new IOException("Historical point geometry dependency changed.");_=ContinuousTerrainSurveys.RequireCurrent(science,missions,geometry,token);
        if(artifact.Kind==TileKind){var s=j.GetProperty("source");_=LidarImports.Bind(science,s.GetProperty("id").GetGuid(),s.GetProperty("manifestSha256").GetString()!,true,preferences,token);}
        else foreach(var s in j.GetProperty("sources").EnumerateArray())_=LidarImports.Bind(science,s.GetProperty("id").GetGuid(),s.GetProperty("manifestSha256").GetString()!,true,preferences,token);
        return path;
    }
    static void Write<T>(string path,T value,long maximum,CancellationToken token)
    {using var f=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);using(var bounded=new LidarBoundedOutput(f,maximum,token))JsonSerializer.Serialize(bounded,value,WireJson.Options);f.Flush(true);}
    sealed class LidarBoundedOutput(Stream target,long maximum,CancellationToken token):Stream
    {
        long written;public override void Write(byte[] b,int o,int n)=>Write(b.AsSpan(o,n));public override void Write(ReadOnlySpan<byte> b){token.ThrowIfCancellationRequested();if(b.Length>maximum-written)throw new StorageLimitException("Historical point output exceeded its bounded reservation; no partial tile is published.");target.Write(b);written+=b.Length;}
        public override void Flush()=>target.Flush();public override bool CanRead=>false;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>written;public override long Position{get=>written;set=>throw new NotSupportedException();}public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
}
