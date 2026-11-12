using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using Geometry=NetTopologySuite.Geometries.Geometry;
using Coordinates=AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

internal sealed record TerrainShapeBinding(Guid SessionId,Guid ConfigurationId,string ProfileId,int ProfileVersion,int Chunk);
internal sealed record TerrainTileWork(int Source,int Column,int Row);
internal static class TerrainNativeGeometry
{
    internal static readonly GeometryFactory Factory=new();
    public static Coordinate Project(RasterGrid grid,Coordinates point)
    {
        double x,y;
        if(grid.Crs=="EPSG:4326"){x=point.Longitude;y=point.Latitude;}
        else
        {
            if(!int.TryParse(grid.Crs.Replace("EPSG:",""),out var code)||code is <32601 or >32760||code is >32660 and <32701
                ||point.Latitude is <-80 or >84||code<32700&&point.Latitude<0||code>=32700&&point.Latitude>0)throw new InvalidDataException("Unsupported horizontal transformation; only the declared WGS84 geographic/UTM hemisphere is accepted.");
            var center=(code%100)*6-183;var delta=(point.Longitude-center+540)%360-180;
            if(Math.Abs(delta)>12)throw new InvalidDataException("Point is outside the verified UTM projection domain.");
            var p=GeographicLib.Projections.TransverseMercator.UTM.Forward(center,point.Latitude,point.Longitude);
            x=p.Item1+500000;y=p.Item2+(code>=32700?10000000:0);
        }
        var dx=x-grid.OriginX;var dy=y-grid.OriginY;var det=grid.PixelWidth*grid.PixelHeight-grid.RowX*grid.ColumnY;
        if(!double.IsFinite(det)||Math.Abs(det)<1e-20)throw new InvalidDataException("Original grid transform is singular.");
        var col=(dx*grid.PixelHeight-dy*grid.RowX)/det;var row=(dy*grid.PixelWidth-dx*grid.ColumnY)/det;
        if(!double.IsFinite(col)||!double.IsFinite(row))throw new InvalidDataException("Nonfinite projected native cell coordinate.");
        return new(col,row);
    }
    internal static Geometry From(TerrainSurveyGeometry geometry)=>Factory.CreateMultiPolygon(geometry.Coordinates.Select(p=>Factory.CreatePolygon(
        Factory.CreateLinearRing(p[0].Select(c=>new Coordinate(c[0],c[1])).ToArray()),p.Skip(1).Select(r=>Factory.CreateLinearRing(r.Select(c=>new Coordinate(c[0],c[1])).ToArray())).ToArray())).ToArray());
    internal static TerrainSurveyGeometry Record(Geometry geometry)
    {
        geometry.Normalize();var parts=new List<double[][][]>();
        void Visit(Geometry g)
        {
            if(g is Polygon p&&!p.IsEmpty)parts.Add(new[]{p.ExteriorRing.Coordinates.Select(c=>new[]{c.X,c.Y}).ToArray()}
                .Concat(Enumerable.Range(0,p.NumInteriorRings).Select(i=>p.GetInteriorRingN(i).Coordinates.Select(c=>new[]{c.X,c.Y}).ToArray())).ToArray());
            else for(var i=0;i<g.NumGeometries;i++){var child=g.GetGeometryN(i);if(!ReferenceEquals(g,child))Visit(child);}
        }
        Visit(geometry);return new("MultiPolygon",parts.ToArray());
    }
    internal static Geometry Transform(Geometry geographic,RasterGrid grid,int maximumVertices,CancellationToken token)
    {
        // RFC 7946 boundaries are linear in geographic coordinates. Geographic
        // source grids use an exact affine transform. For UTM, adaptively densify
        // until midpoint chord error <=.001 native cell, bounded by 50k vertices.
        var count=0;
        Coordinate[] Ring(LineString line)
        {
            var points=new List<Coordinate>();
            void Edge(Coordinate a,Coordinate b,Coordinate pa,Coordinate pb,int depth)
            {
                token.ThrowIfCancellationRequested();var mid=new Coordinate((a.X+b.X)/2,(a.Y+b.Y)/2);var pm=Project(grid,new(mid.Y,mid.X));
                var error=pm.Distance(new((pa.X+pb.X)/2,(pa.Y+pb.Y)/2));
                foreach(var fraction in new[]{.25,.75})
                {
                    var q=Project(grid,new(a.Y+(b.Y-a.Y)*fraction,a.X+(b.X-a.X)*fraction));
                    error=Math.Max(error,q.Distance(new(pa.X+(pb.X-pa.X)*fraction,pa.Y+(pb.Y-pa.Y)*fraction)));
                }
                if(error>.001)
                {if(depth>=20)throw new InvalidDataException("Projection refinement exceeds the supported bound.");Edge(a,mid,pa,pm,depth+1);Edge(mid,b,pm,pb,depth+1);}
                else{if(++count>maximumVertices)throw new InvalidOperationException("Projected geometry vertex budget exceeded.");points.Add(pa);}
            }
            var c=line.Coordinates;for(var i=0;i<c.Length-1;i++)Edge(c[i],c[i+1],Project(grid,new(c[i].Y,c[i].X)),Project(grid,new(c[i+1].Y,c[i+1].X)),0);
            if(points.Count>0)points.Add(points[0].Copy());return points.ToArray();
        }
        Geometry Convert(Geometry g)
        {
            if(g is Polygon p)return Factory.CreatePolygon(Factory.CreateLinearRing(Ring(p.ExteriorRing)),Enumerable.Range(0,p.NumInteriorRings).Select(i=>Factory.CreateLinearRing(Ring(p.GetInteriorRingN(i)))).ToArray());
            var polygons=new List<Polygon>();
            for(var i=0;i<g.NumGeometries;i++)
            {
                var child=g.GetGeometryN(i);if(ReferenceEquals(child,g))continue;
                var converted=Convert(child);for(var j=0;j<converted.NumGeometries;j++)if(converted.GetGeometryN(j) is Polygon polygon)polygons.Add(polygon);
            }
            return Factory.CreateMultiPolygon(polygons.ToArray());
        }
        var result=Convert(geographic);if(!result.IsValid)throw new InvalidDataException("Projected corridor topology is invalid; no bounding rectangle was substituted.");return result;
    }
    internal static Geometry ClipToGrid(Geometry geographic,RasterGrid grid,int maximumVertices,CancellationToken token)
    {
        Geometry usable=geographic;
        if(grid.Crs!="EPSG:4326")
        {
            if(!int.TryParse(grid.Crs.Replace("EPSG:",""),out var code)||code is <32601 or >32760||code is >32660 and <32701)throw new InvalidDataException("Unsupported source CRS.");
            var center=(code%100)*6-183;var regions=new List<Geometry>();
            for(var band=-1;band<=1;band++)
            {
                var west=Math.Max(-180,center-12+band*360);var east=Math.Min(180,center+12+band*360);
                if(east>west)regions.Add(geographic.Intersection(Factory.ToGeometry(new Envelope(west,east,code>=32700?-80:0,code>=32700?0:84))));
            }
            usable=ScientificLocalGeometry.Union(regions);
        }
        if(usable.IsEmpty||usable.Area<=0)return Factory.CreateMultiPolygon();
        return Transform(usable,grid,maximumVertices,token).Intersection(Factory.ToGeometry(new Envelope(0,grid.Width,0,grid.Height)));
    }
}

// Disposable spatial index contains only modeled geometry/work keys, never source
// elevation values. It is rebuilt from verified inputs after restart. Originals
// and durable output tiles live in the existing Library, not this scratch file.
internal sealed class TerrainElevationWorkIndex:IDisposable
{
    readonly AnalysisScratch scratch;readonly SqliteConnection db;readonly TerrainElevationOptions options;
    public string ScienceDirectory{get;}
    public long TotalTiles{get;private set;}
    public TerrainElevationWorkIndex(ScienceStore science,MissionStore missions,ScientificArtifact geometry,TerrainElevationSource[] sources,TerrainElevationOptions options,CancellationToken token)
    {
        this.options=options;ScienceDirectory=science.DirectoryPath;scratch=science.CreateAnalysisScratch(options.MaximumScratchBytes);
        db=new(new SqliteConnectionStringBuilder{DataSource=scratch.Path,Pooling=false}.ToString());
        try
        {
            // This disposable geometry index is rebuilt after interruption, so no
            // rollback journal can consume a second unreserved copy of its pages.
            // Durable job/checkpoint transactions retain the Library's normal journal.
            db.Open();using var command=db.CreateCommand();command.CommandText=$"""
                PRAGMA page_size=4096;PRAGMA journal_mode=OFF;PRAGMA temp_store=FILE;
                PRAGMA max_page_count={options.MaximumScratchBytes/4096};
                CREATE TABLE shapes(id INTEGER PRIMARY KEY,source INTEGER NOT NULL,geometry TEXT NOT NULL,binding TEXT NOT NULL);
                CREATE VIRTUAL TABLE bounds USING rtree(id,minx,maxx,miny,maxy);
                CREATE TABLE work(source INTEGER NOT NULL,col INTEGER NOT NULL,row INTEGER NOT NULL,PRIMARY KEY(source,row,col));
                """;command.ExecuteNonQuery();long id=0;
            foreach(var chunk in ContinuousTerrainSurveys.StreamCurrentChunks(science,missions,geometry,token))
            {
                if(chunk.Index<options.FirstGeometryChunk)continue;
                if(options.LastGeometryChunk is {} last&&chunk.Index>last)break;
                var geo=TerrainNativeGeometry.From(chunk.Geometry);
                for(var s=0;s<sources.Length;s++)
                {
                    token.ThrowIfCancellationRequested();var grid=sources[s].Grid;
                    var projected=TerrainNativeGeometry.ClipToGrid(geo,grid,options.MaximumProjectedVertices,token);
                    if(projected.IsEmpty||projected.Area<=0)continue;
                    var e=projected.EnvelopeInternal;var size=options.TileSize;
                    var left=Math.Max(0,(int)Math.Floor(e.MinX/size));var right=Math.Min((grid.Width-1)/size,(int)Math.Floor(e.MaxX/size));
                    var top=Math.Max(0,(int)Math.Floor(e.MinY/size));var bottom=Math.Min((grid.Height-1)/size,(int)Math.Floor(e.MaxY/size));
                    using var tx=db.BeginTransaction();command.Transaction=tx;command.Parameters.Clear();
                    command.CommandText="INSERT INTO shapes VALUES($id,$source,$geometry,$binding)";
                    command.Parameters.AddWithValue("$id",++id);command.Parameters.AddWithValue("$source",s);
                    command.Parameters.AddWithValue("$geometry",JsonSerializer.Serialize(TerrainNativeGeometry.Record(projected),WireJson.Options));
                    command.Parameters.AddWithValue("$binding",JsonSerializer.Serialize(new TerrainShapeBinding(chunk.SessionId,chunk.Configuration.Id,chunk.GeometryProfile.Profile.Id,chunk.GeometryProfile.Profile.Version,chunk.Index),WireJson.Options));command.ExecuteNonQuery();
                    command.CommandText="INSERT INTO bounds VALUES($id,$minx,$maxx,$miny,$maxy)";command.Parameters.AddWithValue("$minx",e.MinX);command.Parameters.AddWithValue("$maxx",e.MaxX);command.Parameters.AddWithValue("$miny",e.MinY);command.Parameters.AddWithValue("$maxy",e.MaxY);command.ExecuteNonQuery();
                    command.Parameters.Clear();command.CommandText="INSERT OR IGNORE INTO work VALUES($s,$c,$r)";command.Parameters.AddWithValue("$s",s);command.Parameters.Add("$c",SqliteType.Integer);command.Parameters.Add("$r",SqliteType.Integer);
                    for(var row=top;row<=bottom;row++)for(var col=left;col<=right;col++)
                    {
                        token.ThrowIfCancellationRequested();command.Parameters["$c"].Value=col;command.Parameters["$r"].Value=row;
                        if(command.ExecuteNonQuery()>0&&++TotalTiles>options.MaximumTiles)throw new InvalidOperationException("Terrain native-tile budget exceeded. Review a smaller corridor/source scope.");
                    }
                    tx.Commit();command.Transaction=null;
                    if(TotalTiles*size*size>options.MaximumCandidateCells)throw new InvalidOperationException("Terrain candidate-cell budget exceeded before reading numeric cells.");
                    if(new FileInfo(scratch.Path).Length>options.MaximumScratchBytes)throw new IOException("Terrain spatial working index exceeded its reservation.");
                }
            }
        }
        catch{Dispose();throw;}
    }
    public IEnumerable<TerrainTileWork> Tiles()
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT source,col,row FROM work ORDER BY source,row,col";using var rows=cmd.ExecuteReader();while(rows.Read())yield return new(rows.GetInt32(0),rows.GetInt32(1),rows.GetInt32(2));
    }
    public (Geometry Geometry,TerrainShapeBinding Binding)[] Shapes(TerrainTileWork tile,CancellationToken token)
    {
        var size=options.TileSize;var envelope=new Envelope(tile.Column*size,(tile.Column+1d)*size,tile.Row*size,(tile.Row+1d)*size);
        // Sort only bounded integer IDs. Sorting geometry JSON here could build a
        // large SQLite temporary payload before the vertex guard was evaluated.
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT s.id FROM bounds b JOIN shapes s ON s.id=b.id WHERE s.source=$s AND b.minx<=$right AND b.maxx>=$left AND b.miny<=$bottom AND b.maxy>=$top ORDER BY s.id LIMIT $limit";
        cmd.Parameters.AddWithValue("$s",tile.Source);cmd.Parameters.AddWithValue("$right",envelope.MaxX);cmd.Parameters.AddWithValue("$left",envelope.MinX);cmd.Parameters.AddWithValue("$bottom",envelope.MaxY);cmd.Parameters.AddWithValue("$top",envelope.MinY);cmd.Parameters.AddWithValue("$limit",options.MaximumShapesPerTile+1);
        var ids=new List<long>();using(var rows=cmd.ExecuteReader())while(rows.Read())ids.Add(rows.GetInt64(0));
        if(ids.Count>options.MaximumShapesPerTile)throw new InvalidOperationException("Too many overlapping corridor shapes in one tile; reduce tile size or review processing bounds.");
        cmd.Parameters.Clear();cmd.CommandText="SELECT geometry,binding FROM shapes WHERE id=$id";cmd.Parameters.Add("$id",SqliteType.Integer);
        var result=new List<(Geometry,TerrainShapeBinding)>();var vertices=0;foreach(var id in ids)
        {
            token.ThrowIfCancellationRequested();cmd.Parameters["$id"].Value=id;using var rows=cmd.ExecuteReader();if(!rows.Read())throw new IOException("Disposable terrain index changed.");
            var shape=TerrainNativeGeometry.From(JsonSerializer.Deserialize<TerrainSurveyGeometry>(rows.GetString(0),WireJson.Options)!).Intersection(TerrainNativeGeometry.Factory.ToGeometry(envelope));
            if(!shape.IsEmpty&&shape.Area>0){vertices=checked(vertices+shape.NumPoints);if(vertices>options.MaximumProjectedVertices)throw new InvalidOperationException("Overlapping tile geometry exceeds the bounded vertex limit.");result.Add((shape,JsonSerializer.Deserialize<TerrainShapeBinding>(rows.GetString(1),WireJson.Options)!));}
        }
        return result.ToArray();
    }
    public void Dispose(){db.Dispose();scratch.Dispose();}
}
