using System.Text.Json;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

// Read-only presentation contracts. These never publish observations, jobs or caches.
public sealed record SurveyResultChoice(Guid Id,string Kind,string Sha256,long Bytes,string State,string? Reason);
public sealed record SurveyViewSource(Guid Id,string Name,string Project,string Surface,string Unit,string VerticalReference,
    bool ReferenceVerified,string HorizontalCrs,DateTimeOffset? ObservationUtc,string License,string Citation,
    string ManifestSha256,string[] Dimensions,RasterGrid? Grid=null);
public sealed record SurveyViewTile(Guid Id,string Sha256,long Bytes,Guid SourceId,long Records,long Missing=0);
public sealed record SurveyViewManifest(int Version,Guid Id,Guid MissionId,string Kind,string Sha256,Guid GeometryId,
    string GeometrySha256,string InputFingerprint,string Algorithm,string EvidenceClass,SurveyViewSource[] Sources,
    SurveyViewTile[] Tiles,long Values,long Missing,string[] Limitations,bool OriginalScienceCredit=false);
public sealed record SurveyViewQuery(string ExpectedSha256,long Cursor=0,int Limit=512,Guid? SourceId=null,Guid? SessionId=null,
    double? West=null,double? South=null,double? East=null,double? North=null);
public sealed record SurveyViewValue(string Id,Guid ArtifactId,string ArtifactSha256,Guid SourceId,Coordinates Position,
    TerrainSurveyGeometry? Geometry,string Unit,string Quality,double? Value,double? DecodedSourceValue,
    int? Column,int? Row,LidarRetainedPoint? Point,Guid[] Sessions,string EvidenceClass);
public sealed record SurveyViewPage(Guid Id,string Sha256,SurveyViewSource[] Sources,SurveyViewValue[] Values,
    long? NextCursor,long TotalValues,int ScannedTiles,bool Partial,string[] Limitations);
public sealed record SurveyGeometryPage(Guid Id,string Sha256,TerrainSurveyRecord[] Records,long? NextCursor);
public sealed record SurveyTrackPoint(long Ordinal,DateTimeOffset Utc,Coordinates Position,double AltitudeMslFeet,
    bool BreakBefore,bool Paused,string Origin);
public sealed record SurveyTrackPage(SurveyTrackPoint[] Points,int? NextAfter);

public static class SurveyResults
{
    public const int MaximumPageValues=1024;
    public const long MaximumProductBytes=33554432;
    public const long MaximumPageBytes=67108864;
    public static readonly string[] ViewLimitations=[
        "Read-only post-flight view of retained evidence. Opening this view never downloads, processes or grants original science credit.",
        "Display pages and levels of detail contain a bounded subset of actual records. Unloaded detail is not missing source coverage; sparse records do not establish complete geographic coverage.",
        "Modeled geometry, historical elevation cells, historical LiDAR returns and original flight acquisitions remain distinct. Source dates/references stay unknown when not established.",
        "Rendered cell edges are a geographic presentation of the retained native mask. Original row/column, grid and clipped native mask remain authoritative; no missing elevation or height reference is inferred."
    ];
    static bool Supported(string kind)=>kind is ContinuousTerrainSurveys.ArtifactKind or TerrainElevations.IndexKind or LidarProcessing.IndexKind;
    static T Read<T>(ScienceStore science,ScientificArtifact artifact)
    {
        if(artifact.Format!="json"||artifact.Bytes>MaximumProductBytes)throw new IOException("Choose a smaller bounded survey product.");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(science.VerifiedArtifactPath(artifact)),WireJson.Options)
            ??throw new IOException("Invalid retained survey product.");
    }
    public static SurveyResultChoice[] Catalog(ScienceStore science,MissionStore missions,Guid mission,CancellationToken token=default)
    {
        if(missions.Detail(mission) is null)throw new ArgumentException("Recorded mission not found.");
        return science.Artifacts(mission).Where(a=>Supported(a.Kind)).OrderByDescending(a=>a.CreatedUtc)
            .DistinctBy(a=>(a.Kind,a.InputIdentity)).Take(64).Select(a=>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var metadata=JsonDocument.Parse(a.MetadataJson);
                    if(metadata.RootElement.TryGetProperty("sourceIds",out var ids)&&ids.EnumerateArray().Any(s=>science.Source(s.GetGuid()) is null))
                        return new SurveyResultChoice(a.Id,a.Kind,a.Sha256,a.Bytes,"Invalidated","A dependent original source was deleted. No numerical copy will be displayed.");
                    return new SurveyResultChoice(a.Id,a.Kind,a.Sha256,a.Bytes,"Retained","Original bytes, geometry and current source policy are verified when opened. Retention alone is not a coverage claim.");
                }
                catch(Exception e)when(e is IOException or JsonException or InvalidOperationException)
                {return new SurveyResultChoice(a.Id,a.Kind,a.Sha256,a.Bytes,"Unavailable",e.Message);}
            }).ToArray();
    }
    static SurveyViewSource Source(TerrainElevationSource s)=>new(s.Original.Id,s.Original.Manifest.Name,s.Original.Manifest.SceneId??s.Original.Manifest.Provider,
        s.Surface,s.Band.Unit,s.Original.Manifest.VerticalDatum,s.DatumVerified,s.Grid.Crs,s.Original.Manifest.ObservationUtc,
        s.Original.Manifest.License,s.Original.Manifest.Citation,s.Original.ManifestSha256,[s.Band.Id],s.Grid);
    static SurveyViewSource Source(LidarSourceSummary s)=>new(s.Id,s.Name,s.EptProject??s.Project,"historical-lidar",s.VerticalUnit,
        s.VerticalReference,!Unverified(s.VerticalReference)&&s.VerticalUnit is "m" or "ft",s.HorizontalCrs,s.OriginalAcquisitionUtc,
        s.License,s.Citation,s.ManifestSha256,s.Dimensions);
    internal static bool Unverified(string? value)=>string.IsNullOrWhiteSpace(value)||new[]{"unknown","unverified","unspecified","not established","not supplied"}.Any(v=>value.Contains(v,StringComparison.OrdinalIgnoreCase));
    public static SurveyViewManifest Manifest(ScienceStore science,MissionStore missions,Guid id,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        var a=science.Artifact(id);if(!Supported(a.Kind))throw new ArgumentException("Choose an original modeled corridor or a verified terrain/LiDAR index.");
        if(a.Kind==ContinuousTerrainSurveys.ArtifactKind)
        {
            var page=Geometry(science,missions,id,a.Sha256,0,1,token);var h=page.Records.FirstOrDefault()?.Header??throw new IOException("Missing modeled corridor header.");
            return new(1,a.Id,a.MissionId,a.Kind,a.Sha256,a.Id,a.Sha256,h.InputFingerprint,h.Algorithm,
                "Modeled scanner support, not measured elevation or laser coverage",[],[],0,0,h.Limitations.Concat(ViewLimitations).ToArray());
        }
        if(a.Kind==LidarProcessing.IndexKind)
        {
            _=LidarProcessing.RequireCurrent(science,missions,a,preferences,token);var index=Read<LidarPointIndex>(science,a);
            if(index.Version!=1||index.MissionId!=a.MissionId||index.Tiles.Length>20000||index.Sources.Length>16||index.GrantsOriginalScienceCredit)throw new IOException("Unsupported historical point index identity or bounds.");
            return new(1,a.Id,a.MissionId,a.Kind,a.Sha256,index.GeometryArtifactId,index.GeometrySha256,index.GeometryInputFingerprint,index.Algorithm,
                "Original historical delivered LiDAR returns; not generated by the simulator",index.Sources.Select(Source).ToArray(),
                index.Tiles.Select(t=>new SurveyViewTile(t.ArtifactId,t.Sha256,t.Bytes,t.SourceId,t.RetainedPoints)).ToArray(),index.RetainedPoints,0,index.Limitations.Concat(ViewLimitations).ToArray());
        }
        _=TerrainElevations.RequireCurrent(science,missions,a,token);var terrain=Read<TerrainElevationIndex>(science,a);
        if(terrain.Version!=1||terrain.MissionId!=a.MissionId||terrain.Algorithm!=TerrainElevations.Algorithm||terrain.Tiles.Length>20000||terrain.Sources.Length>16||terrain.GrantsOriginalScienceCredit)throw new IOException("Unsupported source-backed terrain index identity or bounds.");
        // Reuse the original hash/grid/policy verifier, never a viewer's guessed source.
        long verified=0;
        foreach(var s in terrain.Sources)
        {
            verified=checked(verified+s.Original.Files.Sum(f=>f.Bytes));if(verified>2147483648)throw new IOException("Viewer original verification exceeds 2 GiB. Choose a smaller regional product.");
            var bound=TerrainElevationSources.Bind(science,TerrainElevationSources.Choice(s),terrain.Options,preferences,token);
            if(ScientificCatalog.Hash(bound)!=ScientificCatalog.Hash(s))throw new IOException("Retained terrain definition changed.");
        }
        if(terrain.Tiles.Select(t=>t.ArtifactId).Distinct().Count()!=terrain.Tiles.Length||terrain.Tiles.Sum(t=>t.ValidCells)!=terrain.ValidCells||terrain.Tiles.Sum(t=>t.MissingCells)!=terrain.MissingCells)throw new IOException("Terrain index counts/references differ.");
        return new(1,a.Id,a.MissionId,a.Kind,a.Sha256,terrain.GeometryArtifactId,terrain.GeometrySha256,terrain.GeometryInputFingerprint,terrain.Algorithm,
            "Historical source-backed native elevation cells; not new flight observations",terrain.Sources.Select(Source).ToArray(),
            terrain.Tiles.Select(t=>new SurveyViewTile(t.ArtifactId,t.Sha256,t.Bytes,t.SourceId,t.ValidCells,t.MissingCells)).ToArray(),terrain.ValidCells,terrain.MissingCells,terrain.Limitations.Concat(ViewLimitations).ToArray());
    }
    public static SurveyGeometryPage Geometry(ScienceStore science,MissionStore missions,Guid id,string expected,long cursor=0,int limit=8,CancellationToken token=default)
    {
        if(cursor is <0 or >1000000||limit is <1 or >32)throw new ArgumentException("Choose a bounded modeled geometry page.");
        var a=science.Artifact(id);if(a.Sha256!=expected)throw new IOException("Modeled corridor selection changed.");var path=ContinuousTerrainSurveys.RequireCurrent(science,missions,a,token);
        var records=new List<TerrainSurveyRecord>();long position=0;var vertices=0;long? next=null;
        using var stream=File.OpenRead(path);
        foreach(var record in JsonSerializer.DeserializeAsyncEnumerable<TerrainSurveyRecord>(stream,WireJson.Options,token).ToBlockingEnumerable(token))
        {
            token.ThrowIfCancellationRequested();if(record is null||record.Version!=1)throw new IOException("Unsupported modeled record.");if(position++<cursor)continue;
            var count=record.Chunk?.Geometry.Coordinates.Sum(p=>p.Sum(r=>r.Length))??0;
            if(count>65536)throw new IOException("Modeled chunk exceeds the bounded display geometry.");
            if(records.Count==limit||vertices+count>65536){next=position-1;break;}vertices+=count;records.Add(record);
        }
        return new(a.Id,a.Sha256,records.ToArray(),next);
    }
    static void Validate(SurveyViewQuery q)
    {
        if(q.ExpectedSha256?.Length!=64||q.Cursor is <0 or >81920000||q.Limit is <1 or >MaximumPageValues)throw new ArgumentException("Choose an identified page of 1–1024 actual survey records.");
        var values=new[]{q.West,q.South,q.East,q.North};if(values.Any(v=>v is not null)&&values.Any(v=>v is null))throw new ArgumentException("Supply a complete geographic view window.");
        if(q.West is {} west&&(values.Any(v=>!double.IsFinite(v!.Value))||west is <-180 or >180||q.East is <-180 or >180||q.South is <-90 or >90||q.North is <-90 or >90||q.South>q.North))throw new ArgumentException("Invalid geographic view window.");
    }
    static bool Visible(Coordinates p,SurveyViewQuery q)=>q.West is null||p.Latitude>=q.South&&p.Latitude<=q.North&&
        (q.West<=q.East?p.Longitude>=q.West&&p.Longitude<=q.East:p.Longitude>=q.West||p.Longitude<=q.East);
    static TerrainSurveyGeometry Geographic(TerrainSurveyGeometry native,RasterGrid grid)
    {
        var count=0;
        return new("MultiPolygon",native.Coordinates.Select(p=>p.Select(r=>r.Select(c=>
        {
            if(++count>65536)throw new IOException("Native mask exceeds the bounded display geometry.");var point=ScientificRaster.InspectionGridPoint(grid,c[0],c[1]);return new[]{point.Longitude,point.Latitude};
        }).ToArray()).ToArray()).ToArray());
    }
    public static SurveyViewPage Page(ScienceStore science,MissionStore missions,Guid id,SurveyViewQuery query,ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        Validate(query);var manifest=Manifest(science,missions,id,preferences,token);if(manifest.Sha256!=query.ExpectedSha256)throw new IOException("Survey selection changed. Reopen the exact verified product.");
        if(manifest.Kind==ContinuousTerrainSurveys.ArtifactKind)throw new ArgumentException("Modeled outlines use the geometry page, never an elevation/point page.");
        if(query.SourceId is {} source&&!manifest.Sources.Any(s=>s.Id==source))throw new ArgumentException("The selected source is not in this product.");
        if(query.SourceId is null&&manifest.Sources.Length>1)throw new ArgumentException("Select one original source; incompatible references are never combined by the viewer.");
        var values=new List<SurveyViewValue>();var tileNumber=(int)(query.Cursor/4096);var offset=(int)(query.Cursor%4096);var scanned=0;long bytes=0;long? next=null;var vertices=0;
        for(;tileNumber<manifest.Tiles.Length;tileNumber++,offset=0)
        {
            token.ThrowIfCancellationRequested();var reference=manifest.Tiles[tileNumber];
            if(query.SourceId is {} chosen&&reference.SourceId!=chosen)continue;
            if(reference.Records==0&&manifest.Kind==LidarProcessing.IndexKind)continue;
            if(scanned>=32||bytes+reference.Bytes>MaximumPageBytes){next=(long)tileNumber*4096+offset;break;}
            var a=science.Artifact(reference.Id);if(a.MissionId!=manifest.MissionId||a.Sha256!=reference.Sha256||a.Bytes!=reference.Bytes)throw new IOException("Survey tile identity changed.");scanned++;bytes=checked(bytes+a.Bytes);
            if(manifest.Kind==LidarProcessing.IndexKind)
            {
                _=LidarProcessing.RequireCurrent(science,missions,a,preferences,token);var tile=LidarProcessing.ReadTile(science,a);
                if(tile.Source.Id!=reference.SourceId||tile.GeometryArtifactId!=manifest.GeometryId||tile.GeometrySha256!=manifest.GeometrySha256||tile.Points.LongLength!=reference.Records)throw new IOException("Historical point tile scope differs.");
                for(var i=offset;i<tile.Points.Length;i++)
                {
                    var p=tile.Points[i];if(query.SessionId is {} session&&!p.Support.Any(s=>s.SessionId==session)||!Visible(p.Geographic,query))continue;
                    if(values.Count==query.Limit){next=(long)tileNumber*4096+i;break;}
                    values.Add(new($"{tile.OriginalSha256}:{p.Original.Record}",a.Id,a.Sha256,tile.Source.Id,p.Geographic,null,manifest.Sources.Single(s=>s.Id==tile.Source.Id).Unit,"original-historical-return",p.Original.Z,p.Original.RawZ,null,null,p,p.Support.Select(s=>s.SessionId).Distinct().ToArray(),tile.EvidenceClass));
                }
            }
            else
            {
                _=TerrainElevations.RequireCurrent(science,missions,a,token);var tile=Read<TerrainElevationTile>(science,a);
                if(a.Kind!=TerrainElevations.TileKind||tile.Version!=1||tile.MissionId!=manifest.MissionId||tile.GeometryArtifactId!=manifest.GeometryId||tile.GeometrySha256!=manifest.GeometrySha256||tile.Source.Original.Id!=reference.SourceId||tile.Cells.Length>1024||tile.GrantsOriginalScienceCredit||tile.Algorithm!=TerrainElevations.Algorithm)throw new IOException("Native cell tile identity or bounds differ.");
                for(var i=offset;i<tile.Cells.Length;i++)
                {
                    var c=tile.Cells[i];var support=c.Support.Where(s=>query.SessionId is null||s.SessionId==query.SessionId).ToArray();if(support.Length==0)continue;
                    var native=TerrainNativeGeometry.Record(ScientificLocalGeometry.Union(support.Select(s=>TerrainNativeGeometry.From(s.Mask))));
                    var shape=Geographic(native,tile.Source.Grid);var center=ScientificRaster.InspectionGridPoint(tile.Source.Grid,c.Column+.5,c.Row+.5);
                    var bounds=TerrainNativeGeometry.From(shape).EnvelopeInternal;
                    if(query.West is not null&&!Visible(center,query)&&!shape.Coordinates.SelectMany(p=>p.SelectMany(r=>r)).Any(p=>Visible(new(p[1],p[0]),query))&&!(query.West<=query.East&&bounds.Intersects(new Envelope(query.West.Value,query.East!.Value,query.South!.Value,query.North!.Value))))continue;
                    var count=shape.Coordinates.Sum(p=>p.Sum(r=>r.Length));if(values.Count==query.Limit||vertices+count>65536){next=(long)tileNumber*4096+i;break;}vertices+=count;
                    values.Add(new($"{tile.Source.Original.ManifestSha256}:{tile.Source.Band.Id}:{c.Column}:{c.Row}",a.Id,a.Sha256,tile.Source.Original.Id,center,shape,tile.Source.Band.Unit,c.Quality,c.Value,c.DecodedSourceValue,c.Column,c.Row,null,support.Select(s=>s.SessionId).Distinct().ToArray(),tile.EvidenceClass));
                }
            }
            if(next is not null)break;
        }
        var result=new SurveyViewPage(id,manifest.Sha256,manifest.Sources,values.ToArray(),next,manifest.Values,scanned,next is not null,ViewLimitations);
        if(JsonSerializer.SerializeToUtf8Bytes(result,WireJson.Options).Length>4194304)throw new IOException("Display page exceeds 4 MiB. Reduce the requested record count.");return result;
    }
    public static SurveyTrackPage Track(MissionStore missions,Guid mission,int after=0,int limit=1024,CancellationToken token=default)
    {
        if(after is <0 or >500000||limit is <1 or >2048)throw new ArgumentException("Choose a bounded archived trajectory page.");if(missions.Detail(mission) is null)throw new ArgumentException("Recorded mission not found.");
        var list=new List<SurveyTrackPoint>();TelemetryFrame? previous=null;var ordinal=0;int? next=null;
        foreach(var f in missions.StreamSamples(mission))
        {
            token.ThrowIfCancellationRequested();if(ordinal++<after){previous=f;continue;}if(ordinal-after>limit){next=ordinal-1;break;}
            var gap=previous is null||!previous.IsValid||!f.IsValid||previous.IsPaused||f.IsPaused||f.TimestampUtc<=previous.TimestampUtc||(f.TimestampUtc-previous.TimestampUtc).TotalSeconds>1.5||Geodesy.Distance(new(previous.Latitude,previous.Longitude),new(f.Latitude,f.Longitude))>5000;
            if(f.IsValid)list.Add(new(ordinal-1,f.TimestampUtc,new(f.Latitude,f.Longitude),f.AltitudeMslFeet,gap,f.IsPaused,f.Source.ToString()));previous=f;
        }
        return new(list.ToArray(),next);
    }
}
