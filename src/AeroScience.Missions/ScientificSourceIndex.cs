using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed record SourceQuery(RemoteKind? Kind = null, Coordinates? Point = null, GeographicBounds? Area = null,
    string? Provider = null, string? Scene = null, string? ManifestKey = null, string? Fingerprint = null, string? Collection = null, string? Search = null);
public sealed record ScientificSourcePage(ScientificSource[] Items, long? Next, long Through, long Total);
// Compatibility enumeration is lazy and uses keyset pages. Length never truncates inventory.
public sealed class ScientificSourceInventory(ScienceStore store, SourceQuery? query = null) : IReadOnlyCollection<ScientificSource>
{
    public int Count => checked((int)store.CountSources(query));
    public int Length => Count;
    public IEnumerator<ScientificSource> GetEnumerator() => store.StreamSources(query).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed partial class ScienceStore
{
    public static RemoteKind? ParseSourceKind(string? value)
    {if(string.IsNullOrWhiteSpace(value))return null;if(!Enum.GetNames<RemoteKind>().Contains(value,StringComparer.OrdinalIgnoreCase)||!Enum.TryParse<RemoteKind>(value,true,out var kind))throw new ArgumentException("Choose a supported scientific category.");return kind;}
    private static readonly Regex SourceIdentityPattern = new("[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}|[a-fA-F0-9]{32}", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private bool sourceIndexReady;
    private void EnsureSourceIndex()
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!sourceIndexReady) Initialize();
    }
    private void InitializeSourceIndex(SqliteConnection db, bool migrate)
    {
        if (!migrate) { UpgradeSourceBoundsPolicy(db); sourceIndexReady = true; return; }
        using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE source_search(seq INTEGER PRIMARY KEY AUTOINCREMENT,source_id TEXT NOT NULL UNIQUE REFERENCES remote_sources(id) ON DELETE CASCADE,
              kind INTEGER NOT NULL,provider TEXT NOT NULL,scene TEXT NOT NULL,manifest_key TEXT NOT NULL,fingerprint TEXT NOT NULL,collection_key TEXT NOT NULL,
              product TEXT NOT NULL,observation_utc TEXT,managed_bytes INTEGER NOT NULL,west REAL NOT NULL,south REAL NOT NULL,east REAL NOT NULL,north REAL NOT NULL,search_text TEXT NOT NULL);
            CREATE INDEX source_search_kind ON source_search(kind,seq);
            CREATE INDEX source_search_scene ON source_search(scene,seq);
            CREATE INDEX source_search_provider ON source_search(provider,seq);
            CREATE INDEX source_search_manifest ON source_search(manifest_key,seq);
            CREATE INDEX source_search_fingerprint ON source_search(fingerprint,seq);
            CREATE INDEX source_search_collection ON source_search(collection_key,seq);
            CREATE VIRTUAL TABLE source_bounds USING rtree(seq,west,east,south,north);
            CREATE INDEX remote_products_source ON remote_products(json_extract(payload_json,'$.sourceId'));
            CREATE TABLE source_references(source_id TEXT NOT NULL REFERENCES remote_sources(id) ON DELETE CASCADE,target_id TEXT NOT NULL,PRIMARY KEY(target_id,source_id));
            CREATE TABLE science_delete_reviews(id TEXT PRIMARY KEY,payload_json TEXT NOT NULL,state TEXT NOT NULL);
            CREATE TABLE science_delete_missions(review_id TEXT NOT NULL REFERENCES science_delete_reviews(id) ON DELETE CASCADE,mission_id TEXT NOT NULL,PRIMARY KEY(review_id,mission_id));
            CREATE TABLE science_delete_files(review_id TEXT NOT NULL REFERENCES science_delete_reviews(id) ON DELETE CASCADE,path TEXT NOT NULL,bytes INTEGER NOT NULL,sha TEXT NOT NULL,PRIMARY KEY(review_id,path));
            CREATE TABLE source_delete_previews(id TEXT PRIMARY KEY,payload_json TEXT NOT NULL,state TEXT NOT NULL,created_utc TEXT NOT NULL);
            CREATE TABLE source_delete_items(preview_id TEXT NOT NULL REFERENCES source_delete_previews(id) ON DELETE CASCADE,ordinal INTEGER NOT NULL,source_id TEXT NOT NULL,payload_json TEXT NOT NULL,PRIMARY KEY(preview_id,ordinal),UNIQUE(preview_id,source_id));
            CREATE TABLE source_delete_ledgers(preview_id TEXT NOT NULL REFERENCES source_delete_previews(id) ON DELETE CASCADE,name TEXT NOT NULL,PRIMARY KEY(preview_id,name));
            CREATE TRIGGER source_bounds_add AFTER INSERT ON source_search BEGIN INSERT INTO source_bounds VALUES(new.seq,new.west,new.east,new.south,new.north); END;
            CREATE TRIGGER source_bounds_remove AFTER DELETE ON source_search BEGIN DELETE FROM source_bounds WHERE seq=old.seq; END;
            """; command.ExecuteNonQuery();
        // Stream existing payloads; migration never loads all sources or rewrites originals.
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT payload_json FROM remote_sources ORDER BY id";
            using var rows = read.ExecuteReader();
            while (rows.Read()) IndexSource(db, tx, ReadCatalog<ScientificSource>(rows.GetString(0)));
        }
        command.CommandText = "INSERT OR REPLACE INTO metadata VALUES('source_bounds_policy','2'); PRAGMA user_version=4"; command.ExecuteNonQuery(); tx.Commit();
        command.Transaction = null; command.CommandText = "PRAGMA integrity_check";
        if (command.ExecuteScalar() as string != "ok") throw new IOException("Library source-index migration failed integrity verification; restore its preserved backup.");
        sourceIndexReady = true;
    }
    private static string SourceProduct(ScientificSourceManifest m) => m.Acquisition?.Workflow switch
    {
        "usgs-3dep-numeric" => "3DEP numeric elevation",
        "stac-cog-native-subset" when m.Kind == RemoteKind.Thermal => "Landsat surface temperature",
        "stac-cog-native-subset" when m.Kind == RemoteKind.Multispectral => "Sentinel-2 Level-2A",
        "stac-cog-native-subset" when m.Kind == RemoteKind.Terrain => "Copernicus DEM",
        _ => m.Name
    };
    private static void IndexSource(SqliteConnection db, SqliteTransaction tx, ScientificSource source)
    {
        var m = source.Manifest; var corners = source.Manifest.Swath?.Domain.Corners??source.Grids.Values.SelectMany(ScientificRaster.GridCoverage).ToArray();
        if (corners.Length == 0 || corners.Any(p => !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude) || Math.Abs(p.Latitude)>90 || Math.Abs(p.Longitude)>180)) throw new IOException("Source geographic index cannot be established; original data preserved.");
        var product = SourceProduct(m); var collection = ScientificCatalog.Hash(new { m.Kind, m.Provider, product });
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO source_search(source_id,kind,provider,scene,manifest_key,fingerprint,collection_key,product,observation_utc,managed_bytes,west,south,east,north,search_text) VALUES($id,$kind,$provider,$scene,$manifest,$fingerprint,$collection,$product,$observation,$bytes,$west,$south,$east,$north,$search)";
        command.Parameters.AddWithValue("$id", source.Id.ToString()); command.Parameters.AddWithValue("$kind", (int)m.Kind);
        command.Parameters.AddWithValue("$provider",m.Provider); command.Parameters.AddWithValue("$scene",m.SceneId);
        command.Parameters.AddWithValue("$manifest",ScientificCatalog.Hash(m)); command.Parameters.AddWithValue("$fingerprint",source.ManifestSha256);
        command.Parameters.AddWithValue("$collection",collection); command.Parameters.AddWithValue("$product",product);
        command.Parameters.AddWithValue("$observation",(object?)m.ObservationUtc?.ToString("O")??DBNull.Value);
        command.Parameters.AddWithValue("$bytes",source.Files.Sum(f=>f.Bytes));
        var bounds=SourceIndexBounds(source);
        command.Parameters.AddWithValue("$west",bounds.West); command.Parameters.AddWithValue("$east",bounds.East);
        command.Parameters.AddWithValue("$south",bounds.South); command.Parameters.AddWithValue("$north",bounds.North);
        command.Parameters.AddWithValue("$search",string.Join(' ',m.Name,m.Provider,m.SceneId,m.VerticalDatum,m.Kind,m.Notes).ToLowerInvariant()); command.ExecuteNonQuery();
        var json=JsonSerializer.Serialize(source,WireJson.Options);
        foreach(var id in SourceIdentityPattern.Matches(json).Select(match=>Guid.Parse(match.Value)).Where(id=>id!=source.Id).Distinct())
        {
            command.Parameters.Clear(); command.CommandText="INSERT INTO source_references VALUES($id,$target)";
            command.Parameters.AddWithValue("$id",source.Id.ToString());command.Parameters.AddWithValue("$target",id.ToString());command.ExecuteNonQuery();
        }
    }
    private static GeographicBounds SourceIndexBounds(ScientificSource source)
    {
        if(source.Manifest.Swath is {} swath)return swath.Domain;
        var corners=source.Grids.Values.SelectMany(ScientificRaster.GridCoverage).ToArray();
        var west=corners.Min(p=>p.Longitude);var east=corners.Max(p=>p.Longitude);var south=corners.Min(p=>p.Latitude);var north=corners.Max(p=>p.Latitude);
        foreach(var grid in source.Grids.Values.Where(g=>g.Crs!="EPSG:4326"))
        {
            // A UTM edge is curved in WGS84: its four corners cannot safely prune
            // candidates. Use the existing Project() supported domain, then let
            // GridCovers and original QA make the exact scientific decision.
            var epsg=int.Parse(grid.Crs.AsSpan(5),System.Globalization.CultureInfo.InvariantCulture);
            if(epsg is not (>=32601 and <=32660 or >=32701 and <=32760))throw new IOException("Source index CRS is unsupported; original files preserved.");
            var central=(epsg%100)*6-183;var left=central-12d;var right=central+12d;
            if(left< -180||right>180){left=-180;right=180;}
            west=Math.Min(west,left);east=Math.Max(east,right);south=Math.Min(south,-80);north=Math.Max(north,84);
        }
        return new(west,south,east,north);
    }
    private static void UpgradeSourceBoundsPolicy(SqliteConnection db)
    {
        using var command=db.CreateCommand();command.CommandText="SELECT value FROM metadata WHERE key='source_bounds_policy'";var policy=command.ExecuteScalar() as string;if(policy=="2")return;
        if(policy is not (null or "1"))throw new IOException("Unsupported scientific source-index policy; original data was preserved.");
        // Derived-index repair only. Immutable source payloads/files and schema 4
        // are unchanged; transaction rollback preserves the previous index.
        using var tx=db.BeginTransaction();command.Transaction=tx;
        using(var read=db.CreateCommand()){
            read.Transaction=tx;read.CommandText="SELECT payload_json FROM remote_sources";using var rows=read.ExecuteReader();while(rows.Read()){
                var source=ReadCatalog<ScientificSource>(rows.GetString(0));var bounds=SourceIndexBounds(source);
                command.Parameters.Clear();command.CommandText="UPDATE source_search SET west=$w,east=$e,south=$s,north=$n WHERE source_id=$id; UPDATE source_bounds SET west=$w,east=$e,south=$s,north=$n WHERE seq=(SELECT seq FROM source_search WHERE source_id=$id)";
                command.Parameters.AddWithValue("$id",source.Id.ToString());command.Parameters.AddWithValue("$w",bounds.West);command.Parameters.AddWithValue("$e",bounds.East);command.Parameters.AddWithValue("$s",bounds.South);command.Parameters.AddWithValue("$n",bounds.North);command.ExecuteNonQuery();
            }
        }
        command.Parameters.Clear();command.CommandText="INSERT OR REPLACE INTO metadata VALUES('source_bounds_policy','2')";command.ExecuteNonQuery();tx.Commit();
    }
    private static string SourceWhere(SqliteCommand command,SourceQuery? query)
    {
        query ??= new(); var conditions=new List<string>();
        void Equal(string column,object? value){if(value is null)return;var name="$"+column;conditions.Add("i."+column+"="+name);command.Parameters.AddWithValue(name,value);}
        Equal("kind",query.Kind is {} k?(int)k:null);Equal("provider",query.Provider);Equal("scene",query.Scene);Equal("manifest_key",query.ManifestKey);Equal("fingerprint",query.Fingerprint);Equal("collection_key",query.Collection);
        if(query.Search is {} search){if(search.Length>200)throw new ArgumentException("Use a search up to 200 characters.");conditions.Add("instr(i.search_text,$search)>0");command.Parameters.AddWithValue("$search",search.ToLowerInvariant());}
        var region=query.Area??(query.Point is {} point?new GeographicBounds(point.Longitude,point.Latitude,point.Longitude,point.Latitude):null);
        if(region is not null)
        {
            if(new[]{region.West,region.South,region.East,region.North}.Any(v=>!double.IsFinite(v))||region.West< -180||region.East>180||region.South< -90||region.North>90||region.West>region.East||region.South>region.North)throw new ArgumentException("Use finite ordered geographic bounds; split the dateline.");
            conditions.Add("i.seq IN (SELECT seq FROM source_bounds WHERE west<=$east AND east>=$west AND south<=$north AND north>=$south)");
            command.Parameters.AddWithValue("$west",region.West);command.Parameters.AddWithValue("$east",region.East);command.Parameters.AddWithValue("$south",region.South);command.Parameters.AddWithValue("$north",region.North);
        }
        return conditions.Count==0?"1=1":string.Join(" AND ",conditions);
    }
    internal bool SourceInCollection(Guid source,string collection)
    {using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT EXISTS(SELECT 1 FROM source_search WHERE source_id=$source AND collection_key=$collection)";command.Parameters.AddWithValue("$source",source.ToString());command.Parameters.AddWithValue("$collection",collection);return Convert.ToInt64(command.ExecuteScalar())!=0;}
    internal bool IsCataloguedSourcePath(string path)
    {
        var parts=Path.GetRelativePath(DirectoryPath,path).Split(Path.DirectorySeparatorChar);
        if(parts.Length<3||parts[0]!="Sources"||!Guid.TryParseExact(parts[1],"N",out var id))return false;
        var source=Source(id);if(source is null)return false;var relative=string.Join('/',parts.Skip(2));
        return relative=="source.json"||source.Files.Any(file=>file.RelativePath==relative);
    }
    public long CountSources(SourceQuery? query=null)
    {
        EnsureSourceIndex();using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM source_search i WHERE "+SourceWhere(command,query);return (long)command.ExecuteScalar()!;
    }
    public ScientificSource? Source(Guid id)
    {
        if(AvailabilityError() is {} error)throw new IOException(error);
        if(!File.Exists(DatabasePath))return null;
        if(!sourceDeletionRecoveryDone)Initialize();
        using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT payload_json FROM remote_sources WHERE id=$id";command.Parameters.AddWithValue("$id",id.ToString());
        return command.ExecuteScalar() is string json?ReadCatalog<ScientificSource>(json):null;
    }
    public ScientificSourcePage SourcePage(SourceQuery? query=null,long after=0,int limit=50,long? through=null,bool includeTotal=true)
    {
        if(after<0||limit is <1 or >100||through<0)throw new ArgumentException("Use a nonnegative source cursor and a page of 1–100 sources. Page size is not a Library capacity limit.");
        EnsureSourceIndex();using var db=Open();using var command=db.CreateCommand();
        command.CommandText="SELECT COALESCE(MAX(seq),0) FROM source_search";var end=through??(long)command.ExecuteScalar()!;
        var where=SourceWhere(command,query);command.CommandText="SELECT COUNT(*) FROM source_search i WHERE i.seq<=$through AND "+where;command.Parameters.AddWithValue("$through",end);var total=includeTotal?(long)command.ExecuteScalar()!:0;
        command.CommandText="SELECT i.seq,CASE WHEN length(CAST(s.payload_json AS BLOB))<=16777216 THEN s.payload_json END FROM source_search i JOIN remote_sources s ON s.id=i.source_id WHERE i.seq>$after AND i.seq<=$through AND "+where+" ORDER BY i.seq LIMIT $limit";
        command.Parameters.AddWithValue("$after",after);command.Parameters.AddWithValue("$limit",limit+1);
        var items=new List<ScientificSource>();long? next=null;long last=after,bytes=0;
        using var rows=command.ExecuteReader();while(rows.Read()){
            if(items.Count==limit){next=last;break;}
            if(rows.IsDBNull(1))throw new IOException("Source metadata exceeds the supported per-source bound; original files were preserved.");
            var json=rows.GetString(1);var size=System.Text.Encoding.UTF8.GetByteCount(json);
            if(items.Count>0&&bytes+size>16777216){next=last;break;}
            last=rows.GetInt64(0);bytes+=size;items.Add(ReadCatalog<ScientificSource>(json));
        }
        return new(items.ToArray(),next,end,total);
    }
    public IEnumerable<ScientificSource> StreamSources(SourceQuery? query=null)
    {
        long after=0;long? through=null;
        while(true){var page=SourcePage(query,after,100,through,includeTotal:false);through=page.Through;foreach(var item in page.Items)yield return item;if(page.Next is not {} next)yield break;after=next;}
    }
}
