using System.Text.Json;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

public sealed record SurveyProfileQuery(string ExpectedSha256,Guid SourceId,Guid? SessionId=null,int AfterPose=0,
    int MaximumPoses=4096,int MaximumSamples=513,Coordinates[]? Transect=null);
public sealed record SurveyProfileSample(double DistanceMetres,Coordinates Position,double? Value,double? DecodedSourceValue,
    string Unit,string Quality,int Segment,int? Column,int? Row,Guid? TileId,string? TileSha256,Guid[] Sessions,
    bool Connect,long? FromPose,long? ToPose,DateTimeOffset? FromUtc,DateTimeOffset? ToUtc);
public sealed record SurveyAircraftSample(double DistanceMetres,long Ordinal,DateTimeOffset Utc,Coordinates Position,
    double AltitudeMslFeet,bool BreakBefore,string Origin);
public sealed record SurveyProfileResult(Guid ArtifactId,string ArtifactSha256,SurveyViewSource Source,
    SurveyProfileSample[] Samples,SurveyAircraftSample[] Aircraft,double DistanceMetres,int? NextAfter,string Method,string[] Limitations);

// Read-only analysis of retained native cells, not a new observation or a raster resampler.
public static class SurveyProfiles
{
    sealed record Leg(SurveyTrackPoint From,SurveyTrackPoint To,double Start,double Length,int Segment);
    sealed record Candidate(TerrainElevationCell Cell,ScientificArtifact Artifact);
    public static SurveyProfileResult Create(ScienceStore science,MissionStore missions,Guid id,SurveyProfileQuery q,
        ScientificAccessPreferences? preferences=null,CancellationToken token=default)
    {
        if(q.MaximumSamples is <2 or >1025||q.MaximumPoses is <2 or >4096||q.AfterPose is <0 or >500000
            ||q.Transect is not null&&(q.Transect.Length!=2||q.Transect.Any(p=>!MissionPlanning.Position(p.Latitude,p.Longitude))))throw new ArgumentException("Select 2–1025 profile samples, up to 4096 archived poses, or a valid two-point transect.");
        var manifest=SurveyResults.Manifest(science,missions,id,preferences,token);
        if(manifest.Kind!=TerrainElevations.IndexKind||manifest.Sha256!=q.ExpectedSha256)throw new IOException("Choose the exact current source-backed elevation index for a profile.");
        var source=manifest.Sources.SingleOrDefault(s=>s.Id==q.SourceId)??throw new ArgumentException("Choose one original source from this product.");var grid=source.Grid??throw new IOException("Native source grid is missing.");
        var a=science.Artifact(id);var index=JsonSerializer.Deserialize<TerrainElevationIndex>(File.ReadAllBytes(science.VerifiedArtifactPath(a)),WireJson.Options)!;
        var frames=new List<SurveyTrackPoint>();int? next=q.AfterPose;
        if(q.Transect is {} line){frames.Add(new(0,DateTimeOffset.MinValue,line[0],0,true,false,"Drawn analysis transect; not flown telemetry"));frames.Add(new(1,DateTimeOffset.MinValue,line[1],0,false,false,"Drawn analysis transect; not flown telemetry"));next=null;}
        else while(next is not null&&frames.Count<q.MaximumPoses){var page=SurveyResults.Track(missions,manifest.MissionId,next.Value,Math.Min(2048,q.MaximumPoses-frames.Count),token);frames.AddRange(page.Points);next=page.NextAfter;if(next-q.AfterPose>=q.MaximumPoses)break;}
        var legs=new List<Leg>();var aircraft=new List<SurveyAircraftSample>();double distance=0;var segment=0;
        for(var i=0;i<frames.Count;i++){
            token.ThrowIfCancellationRequested();var f=frames[i];var gap=i==0||f.BreakBefore||f.Paused||frames[i-1].Paused;
            if(gap)segment++;else{var length=Geodesy.Distance(frames[i-1].Position,f.Position);if(length>0){legs.Add(new(frames[i-1],f,distance,length,segment));distance+=length;}}
            if(q.Transect is null)aircraft.Add(new(distance,f.Ordinal,f.Utc,f.Position,f.AltitudeMslFeet,gap,f.Origin));
        }
        if(distance<=0||legs.Count==0)throw new ArgumentException("The selected path has no supported nonzero ground-distance leg. Choose another archived segment or transect.");
        var positions=new List<(double Distance,Coordinates Position,Leg Leg,Coordinate Native)>();var legIndex=0;
        for(var i=0;i<q.MaximumSamples;i++){
            token.ThrowIfCancellationRequested();var d=distance*i/(q.MaximumSamples-1d);while(legIndex<legs.Count-1&&legs[legIndex].Start+legs[legIndex].Length<d)legIndex++;
            var leg=legs[legIndex];var p=Geodesy.Direct(leg.From.Position,Geodesy.Bearing(leg.From.Position,leg.To.Position),Math.Max(0,Math.Min(leg.Length,d-leg.Start)));positions.Add((d,p,leg,TerrainNativeGeometry.Project(grid,p)));
        }
        var needed=positions.Select(p=>((int)Math.Floor(p.Native.X/index.Options.TileSize),(int)Math.Floor(p.Native.Y/index.Options.TileSize))).ToHashSet();
        var cells=new Dictionary<(int,int),Candidate>();long bytes=0;var tiles=0;
        foreach(var reference in index.Tiles.Where(t=>t.SourceId==q.SourceId&&needed.Contains((t.TileColumn,t.TileRow)))){
            token.ThrowIfCancellationRequested();if(++tiles>128||(bytes=checked(bytes+reference.Bytes))>SurveyResults.MaximumPageBytes)throw new IOException("Profile tile budget exceeded. Choose a shorter regional product or path.");
            var tileArtifact=science.Artifact(reference.ArtifactId);if(tileArtifact.Sha256!=reference.Sha256||tileArtifact.Bytes!=reference.Bytes||tileArtifact.Bytes>16777216)throw new IOException("Profile native tile identity or size changed.");
            var path=TerrainElevations.RequireCurrent(science,missions,tileArtifact,token);var tile=JsonSerializer.Deserialize<TerrainElevationTile>(File.ReadAllBytes(path),WireJson.Options)!;
            if(tile.Source.Original.Id!=q.SourceId||tile.Source.Original.ManifestSha256!=source.ManifestSha256||tile.GeometrySha256!=manifest.GeometrySha256||tile.MissionId!=manifest.MissionId||tile.Cells.Length>1024||tile.GrantsOriginalScienceCredit)throw new IOException("Profile source, geometry or native-cell scope changed.");
            foreach(var c in tile.Cells)if(!cells.TryAdd((c.Column,c.Row),new(c,tileArtifact)))throw new IOException("Duplicate original cell identities in profile tiles.");
            if(cells.Count>65536)throw new IOException("Profile native-cell budget exceeded. Choose a smaller regional product.");
        }
        // Retain only time/session metadata here; original chunk geometry remains in its artifact.
        var intervals=ContinuousTerrainSurveys.StreamCurrentChunks(science,missions,science.Artifact(manifest.GeometryId),token)
            .Select(c=>(c.Index,c.SessionId,c.FromUtc,c.ToUtc)).Take(20001).ToArray();if(intervals.Length>20000)throw new IOException("Profile session metadata exceeds the bounded scope.");
        var samples=new List<SurveyProfileSample>();Geometry? previousMask=null;Coordinate? previousNative=null;Leg? previousLeg=null;Guid[] previousSessions=[];
        foreach(var p in positions){
            token.ThrowIfCancellationRequested();int col=(int)Math.Floor(p.Native.X),row=(int)Math.Floor(p.Native.Y);cells.TryGetValue((col,row),out var candidate);var c=candidate?.Cell;
            var fromTime=q.Transect is null?p.Leg.From.Utc+(p.Leg.To.Utc-p.Leg.From.Utc)*((p.Distance-p.Leg.Start)/p.Leg.Length):(DateTimeOffset?)null;
            var current=fromTime is {} utc?intervals.Where(t=>utc>=t.FromUtc&&utc<=t.ToUtc).ToArray():intervals;
            var support=c?.Support.Where(s=>(q.SessionId is null||q.SessionId==s.SessionId)&&current.Any(t=>t.SessionId==s.SessionId&&s.GeometryChunks.Contains(t.Index))).ToArray()??[];
            var mask=support.Length>0?ScientificLocalGeometry.Union(support.Select(s=>TerrainNativeGeometry.From(s.Mask))):null;
            var supported=mask?.Covers(TerrainNativeGeometry.Factory.CreatePoint(p.Native))==true;var valid=supported&&c?.Quality=="valid"&&c.Value is not null;
            var sessions=supported?support.Select(s=>s.SessionId).Distinct().Order().ToArray():[];
            var connect=false;
            if(valid&&previousMask is not null&&previousNative is not null&&previousLeg==p.Leg&&sessions.SequenceEqual(previousSessions)){
                var union=previousMask.Union(mask!);connect=union.Covers(TerrainNativeGeometry.Factory.CreateLineString([previousNative,p.Native]));
            }
            samples.Add(new(p.Distance,p.Position,valid?c!.Value:null,supported?c!.DecodedSourceValue:null,source.Unit,!supported?"unsupported-path-or-session":c!.Quality,p.Leg.Segment,supported?col:null,supported?row:null,supported?candidate!.Artifact.Id:null,supported?candidate!.Artifact.Sha256:null,sessions,connect,q.Transect is null?p.Leg.From.Ordinal:null,q.Transect is null?p.Leg.To.Ordinal:null,q.Transect is null?p.Leg.From.Utc:null,q.Transect is null?p.Leg.To.Utc:null));
            previousMask=valid?mask:null;previousNative=p.Native;previousLeg=p.Leg;previousSessions=sessions;
        }
        return new(id,a.Sha256,source,samples.ToArray(),aircraft.ToArray(),distance,next,
            "WGS84 geodesic ground distance along archived contiguous legs or the explicitly drawn analysis transect. Exact retained native cell and clipped support lookup; no elevation interpolation or new observations.",
            ["Only verified retained native cells provide elevations. Nodata, source/session exclusions and unsupported collection intervals remain gaps. Bounded samples are joined only where the retained support masks prove the connecting native segment is covered.",
             "The distance axis sums known contiguous archived legs; unknown displacement across telemetry gaps is excluded and segment boundaries remain disconnected. Sample path positions are derived, not extra recorded flight poses.",
             "One explicit DEM/DSM source and reference. Original source units/values are retained. No vertical datum conversion, height above ground or equivalence to aircraft MSL is inferred.",
             "Aircraft altitude is separately recorded simulator context at original pose ordinals/times. A drawn transect is analysis geometry, not a flown route.",
             next is null?"End of selected archived path.":"Archived path display is partial; continue with NextAfter for the next bounded segment."]);
    }
}
