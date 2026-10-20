using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record AtmosphericMapPage(ScienceObservation[] Items, long Next, bool HasMore);
public sealed partial class ScienceStore
{
    private void InitializeAtmosphereIndex(SqliteConnection db)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name FROM sqlite_master WHERE name='atmospheric_spatial'"; if (cmd.ExecuteScalar() is not null) return;
        using var tx = db.BeginTransaction(); cmd.Transaction = tx; cmd.CommandText = """
            CREATE VIRTUAL TABLE atmospheric_spatial USING rtree(id,west,east,south,north);
            INSERT INTO atmospheric_spatial SELECT id,json_extract(payload_json,'$.longitude'),json_extract(payload_json,'$.longitude'),json_extract(payload_json,'$.latitude'),json_extract(payload_json,'$.latitude') FROM observations WHERE json_type(payload_json,'$.longitude') IN ('integer','real') AND json_type(payload_json,'$.latitude') IN ('integer','real');
            CREATE TRIGGER atmospheric_spatial_insert AFTER INSERT ON observations WHEN json_type(new.payload_json,'$.longitude') IN ('integer','real') AND json_type(new.payload_json,'$.latitude') IN ('integer','real') BEGIN
              INSERT INTO atmospheric_spatial VALUES(new.id,json_extract(new.payload_json,'$.longitude'),json_extract(new.payload_json,'$.longitude'),json_extract(new.payload_json,'$.latitude'),json_extract(new.payload_json,'$.latitude'));
            END;
            CREATE TRIGGER atmospheric_spatial_delete AFTER DELETE ON observations BEGIN DELETE FROM atmospheric_spatial WHERE id=old.id; END;
            """; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public AtmosphericMapPage AtmosphereMap(ScienceMapQuery query)
    {
        if (query.Missions?.Length > 100) throw new ArgumentException("Choose up to 100 missions or All Missions."); if (!File.Exists(DatabasePath)) return new([], query.After, false);
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = """
            SELECT o.id,o.payload_json FROM observations o JOIN atmospheric_spatial geo ON geo.id=o.id
            WHERE o.id>$after AND ($instrument='' OR json_extract(o.payload_json,'$.instrumentId')=$instrument)
             AND ($from='' OR timestamp_utc>=$from) AND ($to='' OR timestamp_utc<=$to)
             AND ($area=0 OR (geo.north>=$south AND geo.south<=$north AND (($west<=$east AND geo.east>=$west AND geo.west<=$east) OR ($west>$east AND (geo.east>=$west OR geo.west<=$east)))))
            """;
        if (query.Missions is { Length: > 0 }) { var names = query.Missions.Select((id, i) => { var name = "$mission" + i; cmd.Parameters.AddWithValue(name, id.ToString()); return name; }); cmd.CommandText += " AND mission_id IN (" + string.Join(',', names) + ")"; }
        cmd.CommandText += " ORDER BY o.id LIMIT $limit"; var limit = Math.Clamp(query.Limit, 1, 1000);
        foreach (var (key, value) in new (string, object)[] { ("$after", Math.Max(0, query.After)), ("$limit", limit + 1), ("$instrument", query.Instrument ?? ""), ("$from", query.From?.ToUniversalTime().ToString("O") ?? ""), ("$to", query.To?.ToUniversalTime().ToString("O") ?? ""), ("$area", query.Area is null ? 0 : 1), ("$west", query.Area?.West ?? -180), ("$east", query.Area?.East ?? 180), ("$south", query.Area?.South ?? -90), ("$north", query.Area?.North ?? 90) }) cmd.Parameters.AddWithValue(key, value);
        using var reader = cmd.ExecuteReader(); var rows = new List<ScienceObservation>(); long next = query.After; bool more = false; while (reader.Read()) { if (rows.Count == limit) { more = true; break; } next = reader.GetInt64(0); rows.Add(ReadCatalog<ScienceObservation>(reader.GetString(1)) with { Id = next }); }
        return new(rows.ToArray(), next, more);
    }
}
