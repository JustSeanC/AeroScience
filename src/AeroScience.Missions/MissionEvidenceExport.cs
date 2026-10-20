using System.Text.Json;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    private async Task WriteCollectionEvidenceJsonAsync(Guid mission, Utf8JsonWriter writer,
        Func<Task> flush, CancellationToken cancellationToken)
    {
        // Optional additive section: a legacy export retains its existing fields and makes no
        // assertion about historical collection sessions, calibration or grading.
        var state = CollectionCheckpoint(mission); if (state is null) return;
        writer.WritePropertyName("collectionEvidence"); writer.WriteStartObject();
        writer.WriteNumber("formatVersion", 1);
        writer.WritePropertyName("state"); writer.WriteRawValue(EvidenceContracts.Write(state));
        await Rows("snapshots", "SELECT kind,sha256,payload_json FROM collection_snapshots WHERE mission_id=$mission ORDER BY kind", (reader) =>
        {
            AeroScience.Shared.IVersionedEvidence snapshot = reader.GetString(0) switch
            {
                "collection-plan" => EvidenceContracts.Read<AeroScience.Shared.CollectionPlan>(reader.GetString(2)),
                "assessment-context" => EvidenceContracts.Read<AeroScience.Shared.MissionAssessmentContext>(reader.GetString(2)),
                _ => throw new NotSupportedException("Unknown frozen collection snapshot; preserve this archive.")
            };
            EvidenceRequire(EvidenceContracts.ContentHash(snapshot) == reader.GetString(1), "Frozen snapshot hash verification failed.");
            writer.WriteStartObject(); writer.WriteString("kind", reader.GetString(0)); writer.WriteString("sha256", reader.GetString(1));
            writer.WritePropertyName("value"); writer.WriteRawValue(reader.GetString(2)); writer.WriteEndObject();
        });
        await Rows("sessions", "SELECT payload_json FROM science_sessions WHERE mission_id=$mission ORDER BY sequence",
            r => writer.WriteRawValue(r.GetString(0)));
        await Rows("events", "SELECT payload_json FROM collection_events WHERE mission_id=$mission ORDER BY sequence",
            r => writer.WriteRawValue(r.GetString(0)));
        await Rows("inputs", "SELECT receipt_id,sha256,payload_json FROM collection_inputs WHERE mission_id=$mission ORDER BY rowid", r =>
        {
            EvidenceRequire(EvidenceDigest(r.GetString(2)) == r.GetString(1), "Retained input hash verification failed.");
            writer.WriteStartObject(); writer.WriteString("id", r.GetString(0)); writer.WriteString("sha256", r.GetString(1));
            writer.WritePropertyName("packet"); writer.WriteRawValue(r.GetString(2)); writer.WriteEndObject();
        });
        await Rows("acquisitionIntents", "SELECT payload_json,state,product_id,product_sha256 FROM science_intents WHERE mission_id=$mission ORDER BY rowid", r =>
        {
            writer.WriteStartObject(); writer.WritePropertyName("intent"); writer.WriteRawValue(r.GetString(0)); writer.WriteString("state", r.GetString(1));
            writer.WriteString("productId", r.IsDBNull(2) ? null : r.GetString(2)); writer.WriteString("productSha256", r.IsDBNull(3) ? null : r.GetString(3)); writer.WriteEndObject();
        });
        writer.WriteEndObject();

        async Task Rows(string name, string sql, Action<Microsoft.Data.Sqlite.SqliteDataReader> write)
        {
            writer.WritePropertyName(name); writer.WriteStartArray(); using var db = Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = sql; cmd.Parameters.AddWithValue("$mission", mission.ToString()); using var reader = cmd.ExecuteReader();
            var count = 0;
            while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); write(reader); if (++count % 128 == 0) await flush(); }
            writer.WriteEndArray();
        }
    }
}
