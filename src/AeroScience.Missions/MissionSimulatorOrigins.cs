using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    private void MigrateSimulatorOrigins(SqliteConnection db)
    {
        using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            CREATE TABLE mission_simulator_origins (
              mission_id TEXT PRIMARY KEY REFERENCES missions(id) ON DELETE CASCADE,
              version INTEGER NOT NULL CHECK(version=1), frozen_utc TEXT NOT NULL,
              payload_json TEXT NOT NULL, sha256 TEXT NOT NULL);
            PRAGMA user_version=14;
            """;
        cmd.ExecuteNonQuery();EvidenceFault?.Invoke("simulator-origin-migration-before-commit");tx.Commit();
    }

    private static SimulatorOrigin ReadSimulatorOrigin(string json,string hash)
    {
        if(EvidenceDigest(json)!=hash)throw new IOException("Frozen simulator origin failed its original hash verification.");
        var origin=EvidenceContracts.Read<SimulatorOrigin>(json);
        SimulatorProvenance.Validate(origin);return origin;
    }

    public SimulatorOrigin? SimulatorOrigin(Guid missionId)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        if(!HasSimulatorOriginSchema(db))return null;
        cmd.CommandText="SELECT payload_json,sha256 FROM mission_simulator_origins WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id",missionId.ToString());using var row=cmd.ExecuteReader();
        return row.Read()?ReadSimulatorOrigin(row.GetString(0),row.GetString(1)):null;
    }
    private static bool HasSimulatorOriginSchema(SqliteConnection db)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";var version=Convert.ToInt32(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture);
        if(version>CurrentSchemaVersion)throw new InvalidOperationException("A newer archive schema cannot be interpreted by this version.");
        if(version<14)
        {
            cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='mission_simulator_origins'";
            if(Convert.ToInt64(cmd.ExecuteScalar(),System.Globalization.CultureInfo.InvariantCulture)!=0)
                throw new IOException("Simulator-origin records conflict with the declared older schema. Preserve and recover the archive; source identity cannot be downgraded.");
        }
        return version>=14;
    }
    private static string SelectionFor(SqliteConnection db)=>HasSimulatorOriginSchema(db)?Selection:
        Selection.Replace("o.payload_json,o.sha256","NULL,NULL",StringComparison.Ordinal)
            .Replace(" LEFT JOIN mission_simulator_origins o ON o.mission_id=m.id","",StringComparison.Ordinal);

    private void WriteSimulatorOriginEvidence(Guid missionId,Utf8JsonWriter writer)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT frozen_utc,payload_json,sha256 FROM mission_simulator_origins WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id",missionId.ToString());using var row=cmd.ExecuteReader();if(!row.Read())throw new IOException("Frozen source evidence is missing.");
        _=ReadSimulatorOrigin(row.GetString(1),row.GetString(2));
        writer.WritePropertyName("simulatorOriginEvidence");writer.WriteStartObject();writer.WriteNumber("version",1);
        writer.WriteString("frozenUtc",row.GetString(0));writer.WriteString("sha256",row.GetString(2));writer.WritePropertyName("origin");
        writer.WriteRawValue(row.GetString(1));writer.WriteEndObject();
    }

    private static void FreezeSimulatorOrigin(SqliteConnection db,SqliteTransaction tx,Guid id,DateTimeOffset at,SimulatorOrigin? origin)
    {
        if(origin is null)return;SimulatorProvenance.Validate(origin);
        var json=JsonSerializer.Serialize(origin,WireJson.Options);
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO mission_simulator_origins VALUES($id,1,$at,$json,$hash)";
        cmd.Parameters.AddWithValue("$id",id.ToString());cmd.Parameters.AddWithValue("$at",Stamp(at));
        cmd.Parameters.AddWithValue("$json",json);cmd.Parameters.AddWithValue("$hash",EvidenceDigest(json));cmd.ExecuteNonQuery();
    }

    private static void RequireSampleOrigin(SqliteConnection db,SqliteTransaction tx,Guid id,TelemetryFrame frame)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT payload_json,sha256 FROM mission_simulator_origins WHERE mission_id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        using var row=cmd.ExecuteReader();
        if(!row.Read())
        {
            if(frame.SimulatorOrigin is not null||frame.SimulatorContext is not null)throw new InvalidOperationException("Source-qualified telemetry requires a frozen recording origin before its first sample.");
            return;
        }
        var origin=ReadSimulatorOrigin(row.GetString(0),row.GetString(1));
        if(frame.Source!=TelemetrySource.Simulator||frame.SimulatorOrigin is not {} actual||!SimulatorProvenance.SameRecording(origin,actual))
            throw new InvalidOperationException("The sample source differs from the frozen recording origin; start a separate flight.");
        SimulatorProvenance.Validate(actual);
        if(origin.SimulatorId==SimulatorPreferences.XPlane12)
        {
            if(frame.SimulatorContext is not {} context||context.ConnectionEpoch!=origin.ConnectionEpoch)
                throw new InvalidOperationException("X-Plane samples require original same-epoch timing evidence.");
            SimulatorProvenance.Validate(context);
        }
    }
}
