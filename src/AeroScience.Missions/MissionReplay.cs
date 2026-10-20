using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ReplaySample(long Sequence,TelemetryFrame Frame);
public sealed record ReplayPage(ReplaySample[] Samples,long NextSequence,bool HasMore,Mission Mission,string TimestampBasis);
public sealed record ReplayInstrumentContext(InstrumentConfiguration[]? Instruments,DateTimeOffset? ConfigurationUtc,string ScienceState,string Method);
public sealed partial class MissionStore
{
    private static void MigrateMissionScience(Microsoft.Data.Sqlite.SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="CREATE INDEX IF NOT EXISTS telemetry_receipt_time ON telemetry(mission_id,timestamp_utc,sequence); CREATE INDEX IF NOT EXISTS events_receipt_time ON events(mission_id,timestamp_utc,id); PRAGMA user_version=6";cmd.ExecuteNonQuery();tx.Commit();MigrateScientificReview(db);
    }
    public ReplayInstrumentContext ReplayInstruments(Guid mission,DateTimeOffset at)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT timestamp_utc,message FROM events WHERE mission_id=$id AND timestamp_utc<=$utc AND kind IN ('payload-snapshot','instruments-configured') ORDER BY timestamp_utc DESC,id DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$utc",at.ToUniversalTime().ToString("O"));InstrumentConfiguration[]? configurations=null;DateTimeOffset? observed=null;
        using(var reader=cmd.ExecuteReader())if(reader.Read()){observed=Parse(reader.GetString(0));configurations=JsonSerializer.Deserialize<InstrumentConfiguration[]>(reader.GetString(1),WireJson.Options);}
        cmd.CommandText="SELECT kind FROM events WHERE mission_id=$id AND timestamp_utc<=$utc AND kind IN ('science-paused','science-resumed','science-stopped') ORDER BY timestamp_utc DESC,id DESC LIMIT 1";
        var state=cmd.ExecuteScalar() as string;return new(configurations,observed,state=="science-paused"?"Paused":state=="science-stopped"?"Stopped":configurations is null?"Not archived":configurations.Any(c=>c.Enabled)?"Enabled instruments active":"No instruments enabled",
            "Actual last archived configuration and collection event at or before this replay time. Legacy missions without an initial snapshot do not inherit their final configuration backwards in time.");
    }
    public ReplayPage Replay(Guid mission,long after=0,DateTimeOffset? at=null,int limit=200)
    {
        ResearchExports.RequireCompleted(this,mission);
        var detail=Detail(mission)??throw new ArgumentException("Mission not found.");
        limit=Math.Clamp(limit,1,500);
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText=at is null
            ?"SELECT sequence,payload_json FROM telemetry WHERE mission_id=$id AND sequence>$after ORDER BY sequence LIMIT $limit"
            :"SELECT sequence,payload_json FROM telemetry WHERE mission_id=$id AND timestamp_utc>=$utc ORDER BY timestamp_utc,sequence LIMIT $limit";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$after",Math.Max(0,after));
        cmd.Parameters.AddWithValue("$utc",(at??DateTimeOffset.MinValue).ToUniversalTime().ToString("O"));cmd.Parameters.AddWithValue("$limit",limit+1);
        using var reader=cmd.ExecuteReader();var samples=new List<ReplaySample>();
        while(reader.Read())samples.Add(new(reader.GetInt64(0),JsonSerializer.Deserialize<TelemetryFrame>(reader.GetString(1),WireJson.Options)!));
        var selected=samples.Take(limit).ToArray();
        return new(selected,selected.LastOrDefault()?.Sequence??after,samples.Count>limit,detail.Mission,
            "Host UTC at packet receipt; nearest recorded sample is selected. No science interpolation or simulator control. Auxiliary fields retain their own timestamps; legacy fields are unavailable.");
    }
    public MissionEvent[] ReplayEvents(Guid mission,long after=0,int limit=100)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT id,timestamp_utc,kind,message FROM events WHERE mission_id=$id AND id>$after ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$after",Math.Max(0,after));cmd.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,200));
        using var reader=cmd.ExecuteReader();var rows=new List<MissionEvent>();
        while(reader.Read())rows.Add(new(reader.GetInt64(0),Parse(reader.GetString(1)),reader.GetString(2),reader.GetString(3)));return rows.ToArray();
    }
}
public sealed partial class ScienceStore
{
    public ScienceObservation[] ReplayObservations(Guid mission,DateTimeOffset at)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        if(!File.Exists(DatabasePath))return [];
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id,payload_json FROM observations WHERE mission_id=$id AND timestamp_utc>=$start AND timestamp_utc<=$end ORDER BY timestamp_utc,id LIMIT 201";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$start",at.AddSeconds(-3).ToUniversalTime().ToString("O"));cmd.Parameters.AddWithValue("$end",at.AddSeconds(3).ToUniversalTime().ToString("O"));
        using var reader=cmd.ExecuteReader();var rows=new List<ScienceObservation>();while(reader.Read())rows.Add(ReadCatalog<ScienceObservation>(reader.GetString(1))with{Id=reader.GetInt64(0)});return rows.ToArray();
    }
    public RemoteProduct[] ReplayAcquisitions(Guid mission,DateTimeOffset at,double toleranceSeconds=3)
    {
        if(AvailabilityError() is {} unavailable)throw new IOException(unavailable);
        if(!File.Exists(DatabasePath))return [];
        if(!double.IsFinite(toleranceSeconds)||toleranceSeconds is <0 or >30)throw new ArgumentException("Replay alignment tolerance must be 0–30 seconds.");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT payload_json FROM remote_products WHERE mission_id=$id AND flight_utc>=$start AND flight_utc<=$end ORDER BY flight_utc,id LIMIT 101";
        cmd.Parameters.AddWithValue("$id",mission.ToString());cmd.Parameters.AddWithValue("$start",at.AddSeconds(-toleranceSeconds).ToUniversalTime().ToString("O"));cmd.Parameters.AddWithValue("$end",at.AddSeconds(toleranceSeconds).ToUniversalTime().ToString("O"));
        using var reader=cmd.ExecuteReader();var rows=new List<RemoteProduct>();while(reader.Read())rows.Add(ReadCatalog<RemoteProduct>(reader.GetString(0)));return rows.ToArray();
    }
}
