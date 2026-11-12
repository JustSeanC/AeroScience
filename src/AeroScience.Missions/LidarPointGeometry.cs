using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
namespace AeroScience.Missions;

// Rebuildable index stores modeled polygons and identities only, never LiDAR values.
internal sealed class LidarPointGeometry:IDisposable
{
    readonly AnalysisScratch scratch;readonly SqliteConnection db;readonly LidarProcessingOptions options;
    readonly Dictionary<long,(IPreparedGeometry Shape,TerrainShapeBinding Binding)> cache=[];
    public int Chunks{get;private set;}
    public LidarPointGeometry(ScienceStore science,MissionStore missions,ScientificArtifact geometry,LidarProcessingOptions options,CancellationToken token)
    {
        this.options=options;scratch=science.CreateAnalysisScratch(options.MaximumScratchBytes);db=new(new SqliteConnectionStringBuilder{DataSource=scratch.Path,Pooling=false}.ToString());
        try
        {
            db.Open();using var command=db.CreateCommand();command.CommandText=$"""
                PRAGMA page_size=4096;PRAGMA journal_mode=OFF;PRAGMA temp_store=MEMORY;PRAGMA cache_size=-8192;PRAGMA max_page_count={options.MaximumScratchBytes/4096};
                CREATE TABLE shapes(id INTEGER PRIMARY KEY,geometry TEXT NOT NULL,binding TEXT NOT NULL);
                CREATE VIRTUAL TABLE bounds USING rtree(id,minx,maxx,miny,maxy);
                """;command.ExecuteNonQuery();long vertices=0;
            foreach(var chunk in ContinuousTerrainSurveys.StreamCurrentChunks(science,missions,geometry,token))
            {
                token.ThrowIfCancellationRequested();if(chunk.Index<options.FirstGeometryChunk)continue;if(options.LastGeometryChunk is {} end&&chunk.Index>end)break;
                var shape=TerrainNativeGeometry.From(chunk.Geometry);if(!shape.IsValid||shape.IsEmpty||shape.NumPoints>50000)throw new InvalidDataException("Original corridor polygon topology or per-chunk complexity is unsupported.");var e=shape.EnvelopeInternal;
                vertices=checked(vertices+shape.NumPoints);if(++Chunks>20000||vertices>options.MaximumGeometryVertices)throw new IOException("Selected modeled geometry exceeds the bounded vertex/chunk budget. Review fewer regional chunks.");
                command.Parameters.Clear();command.CommandText="INSERT INTO shapes VALUES($id,$geometry,$binding); INSERT INTO bounds VALUES($id,$x0,$x1,$y0,$y1)";
                command.Parameters.AddWithValue("$id",chunk.Index);command.Parameters.AddWithValue("$geometry",JsonSerializer.Serialize(chunk.Geometry,WireJson.Options));command.Parameters.AddWithValue("$binding",JsonSerializer.Serialize(new TerrainShapeBinding(chunk.SessionId,chunk.Configuration.Id,chunk.GeometryProfile.Profile.Id,chunk.GeometryProfile.Profile.Version,chunk.Index),WireJson.Options));
                command.Parameters.AddWithValue("$x0",e.MinX);command.Parameters.AddWithValue("$x1",e.MaxX);command.Parameters.AddWithValue("$y0",e.MinY);command.Parameters.AddWithValue("$y1",e.MaxY);command.ExecuteNonQuery();
            }
            if(Chunks==0)throw new InvalidOperationException("No valid modeled geometry chunks are selected. No arbitrary track or rectangle is substituted.");
        }
        catch{db.Dispose();scratch.Dispose();throw;}
    }
    internal LidarPointSupport[] Support(AeroScience.Shared.Coordinates point,CancellationToken token)
    {
        var bindings=new List<TerrainShapeBinding>();
        foreach(var longitude in Math.Abs(point.Longitude)==180?new[]{-180d,180d}:new[]{point.Longitude})
        {
            using var command=db.CreateCommand();command.CommandText="SELECT id FROM bounds WHERE minx<=$x AND maxx>=$x AND miny<=$y AND maxy>=$y ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$x",longitude);command.Parameters.AddWithValue("$y",point.Latitude);command.Parameters.AddWithValue("$limit",options.MaximumShapesPerPoint+1);var ids=new List<long>();using(var rows=command.ExecuteReader())while(rows.Read())ids.Add(rows.GetInt64(0));
            if(ids.Count>options.MaximumShapesPerPoint)throw new IOException("Too many overlapping modeled chunks at this point. Narrow the selected geometry scope; no binding is truncated.");
            foreach(var id in ids)
            {
                token.ThrowIfCancellationRequested();if(!cache.TryGetValue(id,out var value))
                {
                    using var read=db.CreateCommand();read.CommandText="SELECT geometry,binding FROM shapes WHERE id=$id";read.Parameters.AddWithValue("$id",id);using var row=read.ExecuteReader();if(!row.Read())throw new IOException("Modeled geometry index is incomplete.");
                    value=(PreparedGeometryFactory.Prepare(TerrainNativeGeometry.From(JsonSerializer.Deserialize<TerrainSurveyGeometry>(row.GetString(0),WireJson.Options)!)),JsonSerializer.Deserialize<TerrainShapeBinding>(row.GetString(1),WireJson.Options)!);
                    if(cache.Count>=16)cache.Remove(cache.Keys.First());cache.Add(id,value);
                }
                if(value.Shape.Covers(TerrainNativeGeometry.Factory.CreatePoint(new Coordinate(longitude,point.Latitude))))bindings.Add(value.Binding);
            }
        }
        return bindings.Distinct().GroupBy(b=>(b.SessionId,b.ConfigurationId,b.ProfileId,b.ProfileVersion)).OrderBy(g=>g.Key.SessionId).ThenBy(g=>g.Key.ConfigurationId).ThenBy(g=>g.Key.ProfileId,StringComparer.Ordinal).ThenBy(g=>g.Key.ProfileVersion)
            .Select(g=>new LidarPointSupport(g.Key.SessionId,g.Key.ConfigurationId,g.Key.ProfileId,g.Key.ProfileVersion,g.Select(b=>b.Chunk).Distinct().Order().ToArray())).ToArray();
    }
    public void Dispose(){cache.Clear();db.Dispose();scratch.Dispose();}
}
