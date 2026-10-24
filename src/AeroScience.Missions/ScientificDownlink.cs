using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

public sealed record ScientificRecoverySlot(string Id, Guid MissionId, string InstrumentId, DateTimeOffset FlightUtc,
    string State, string Reason, RemoteAcquisitionRequest Evidence, DateTimeOffset UpdatedUtc, Guid? ProductId = null, bool DerivedCompleted = false, string? DerivedLimitations = null);
public sealed record ScientificReviewStatus(string FlightStatus, string Status, bool Provisional, long Pending, long Recovered, long PermanentGaps, long Cancelled, string Message);
public sealed partial class MissionStore
{
    private static void MigrateScientificReview(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scientific_recovery(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL REFERENCES missions(id),instrument_id TEXT NOT NULL,
                flight_utc TEXT NOT NULL,state TEXT NOT NULL,payload_json TEXT NOT NULL,UNIQUE(mission_id,instrument_id,flight_utc));
            CREATE INDEX IF NOT EXISTS recovery_pending ON scientific_recovery(state,mission_id,flight_utc);
            PRAGMA user_version=7;
            """; cmd.ExecuteNonQuery(); transaction.Commit();
    }
    public void SaveRecovery(RemoteAcquisitionRequest request, string reason, bool eligible)
    {
        if(ScienceDeleted(request.Mission.Id))throw new InvalidOperationException("Collected science was deliberately deleted; no recovery may recreate it.");
        // An actual scheduled acquisition carries its exact frozen configuration, never a guessed interval.
        var identity = $"{request.Mission.Id:N}|{request.Configuration.InstrumentId}|{request.Frame.TimestampUtc:O}";
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var slot = new ScientificRecoverySlot(id, request.Mission.Id, request.Configuration.InstrumentId, request.Frame.TimestampUtc,
            eligible ? "Pending" : "Gap", reason, request with { Research = new(null, [], null, "Archived acquisition evidence", []) }, DateTimeOffset.UtcNow);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO scientific_recovery VALUES($id,$mission,$instrument,$utc,$state,$json)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$mission", slot.MissionId.ToString()); cmd.Parameters.AddWithValue("$instrument", slot.InstrumentId);
        cmd.Parameters.AddWithValue("$utc", slot.FlightUtc.ToUniversalTime().ToString("O")); cmd.Parameters.AddWithValue("$state", slot.State); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(slot, WireJson.Options)); cmd.ExecuteNonQuery();
    }
    public ScientificRecoverySlot[] Recovery(Guid? mission = null, int limit = 100, int offset = 0, bool pendingOnly = false)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM scientific_recovery WHERE ($mission='' OR mission_id=$mission) AND ($pending=0 OR (state IN ('Pending','Downloading','Reconstructing','Blocked') OR state='Recovered' AND COALESCE(json_extract(payload_json,'$.derivedCompleted'),0)=0)) ORDER BY CASE WHEN $pending=1 THEN json_extract(payload_json,'$.updatedUtc') ELSE flight_utc END,id LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$mission", mission?.ToString() ?? ""); cmd.Parameters.AddWithValue("$pending", pendingOnly ? 1 : 0); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader(); var rows = new List<ScientificRecoverySlot>(); while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ScientificRecoverySlot>(reader.GetString(0), WireJson.Options)!); return rows.ToArray();
    }
    public ScientificRecoverySlot? RecoveryById(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM scientific_recovery WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ScientificRecoverySlot>(json, WireJson.Options) : null;
    }
    public void UpdateRecovery(ScientificRecoverySlot slot)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE scientific_recovery SET state=$state,payload_json=$json WHERE id=$id AND state NOT IN ('Paused','Cancelled')";
        cmd.Parameters.AddWithValue("$id", slot.Id); cmd.Parameters.AddWithValue("$state", slot.State); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(slot with { UpdatedUtc = DateTimeOffset.UtcNow }, WireJson.Options)); cmd.ExecuteNonQuery();
    }
    public void ControlRecovery(Guid mission, string action)
    {
        if (action is not ("pause" or "resume" or "cancel")) throw new ArgumentException("Choose pause, resume or cancel.");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        // SQL updates both indexed state and payload atomically. Completed original products are never changed.
        var state = action == "pause" ? "Paused" : action == "cancel" ? "Cancelled" : "Pending";
        cmd.CommandText = "UPDATE scientific_recovery SET state=$state,payload_json=json_set(payload_json,'$.state',$state,'$.reason',$reason) WHERE mission_id=$mission AND state!='Gap' AND (state!='Recovered' OR COALESCE(json_extract(payload_json,'$.derivedCompleted'),0)=0)";
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); cmd.Parameters.AddWithValue("$state", state); cmd.Parameters.AddWithValue("$reason", $"Post-flight science {action} requested by operator."); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public bool VerifyRecoveryEvidence(ScientificRecoverySlot slot)
    {
        var frame = slot.Evidence.Frame; if (!frame.IsValid || frame.IsPaused || !slot.Evidence.Configuration.Enabled || !RemoteAcquisition.Instruments.ContainsKey(slot.InstrumentId)) return false;
        if(slot.Evidence.IntentId is {} intentId&&slot.Evidence.SessionId is {} sessionId)
        {
            var intent=ScienceIntent(intentId)?.Intent;
            if(intent is null||intent.MissionId!=slot.MissionId||intent.SessionId!=sessionId||intent.InstrumentId!=slot.InstrumentId||intent.ExposureUtc!=frame.TimestampUtc)return false;
            var packet=CollectionInput(slot.MissionId,intent.InputReceiptId);
            var configuration=CollectionSession(slot.MissionId,sessionId)?.Configurations.SingleOrDefault(c=>c.Id==intent.ConfigurationId);
            return packet?.Packet.Frame is {} original&&JsonSerializer.Serialize(original,WireJson.Options)==JsonSerializer.Serialize(frame,WireJson.Options)&&configuration is not null&&
                JsonSerializer.Serialize(configuration.Settings,WireJson.Options)==JsonSerializer.Serialize(slot.Evidence.Configuration,WireJson.Options);
        }
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM telemetry WHERE mission_id=$mission AND timestamp_utc=$utc LIMIT 1";
        cmd.Parameters.AddWithValue("$mission", slot.MissionId.ToString()); cmd.Parameters.AddWithValue("$utc", frame.TimestampUtc.ToUniversalTime().ToString("O"));
        if (cmd.ExecuteScalar() is not string json) return false;
        var recorded = JsonSerializer.Deserialize<TelemetryFrame>(json, WireJson.Options)!;
        if (recorded.Latitude != frame.Latitude || recorded.Longitude != frame.Longitude || recorded.PitchDegrees != frame.PitchDegrees || recorded.BankDegrees != frame.BankDegrees || recorded.AltitudeMslFeet != frame.AltitudeMslFeet) return false;
        var context = ReplayInstruments(slot.MissionId, frame.TimestampUtc);
        var config = context.Instruments?.FirstOrDefault(c => c.InstrumentId == slot.InstrumentId);
        return context.ScienceState is not ("Paused" or "Stopped" or "Not archived") && config?.Enabled == true && JsonSerializer.Serialize(config, WireJson.Options) == JsonSerializer.Serialize(slot.Evidence.Configuration, WireJson.Options);
    }
    public ScientificReviewStatus ScientificReview(Guid id)
    {
        var mission = Detail(id)?.Mission ?? throw new ArgumentException("Mission not found.");
        if(ScienceDeleted(id))return new(mission.Status.ToString(),"Deleted",false,0,0,0,0,"Collected scientific data and dependent analysis were permanently deleted. Flight recording and external source files remain separate.");
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT state,COUNT(*) FROM scientific_recovery WHERE mission_id=$id GROUP BY state"; cmd.Parameters.AddWithValue("$id", id.ToString());
        var counts = new Dictionary<string, long>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt64(1);
        cmd.CommandText = "SELECT COUNT(*) FROM scientific_recovery WHERE mission_id=$id AND state='Recovered' AND COALESCE(json_extract(payload_json,'$.derivedCompleted'),0)=0"; var derived = Convert.ToInt64(cmd.ExecuteScalar());
        long N(string state) => counts.GetValueOrDefault(state); var pending = derived + N("Pending") + N("Downloading") + N("Reconstructing") + N("Blocked") + N("Paused");
        var status = derived > 0 ? "Processing Scientific Results" : N("Reconstructing") > 0 ? "Reconstructing" : N("Downloading") > 0 ? "Downloading" : N("Paused") > 0 ? "Paused" : N("Blocked") > 0 ? "Blocked" : pending > 0 ? "Downlink Pending" : N("Gap") > 0 ? "Partially Complete" : N("Cancelled") > 0 ? "Cancelled" : "Ready";
        return new(mission.Status.ToString(), status, pending > 0 || N("Cancelled") > 0, pending, N("Recovered"), N("Gap"), N("Cancelled"),
            "Historical geographic reconstruction uses only archived scheduled evidence. Missing atmosphere and unknown legacy acquisition intervals cannot be recovered. Source time differs from flight time. Legacy archives without recovery evidence have no inferred backlog.");
    }
}
