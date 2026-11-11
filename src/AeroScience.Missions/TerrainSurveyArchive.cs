using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

internal sealed record TerrainSurveyInput(TelemetryFrame? Frame = null,
    TerrainSurveyTelemetryReference? Reference = null, Guid? SessionId = null,
    EffectiveInstrumentConfiguration? Configuration = null, TerrainSurveyGap? Gap = null);

public sealed partial class MissionStore
{
    internal TerrainSurveyArchive OpenTerrainSurveyArchive(Guid mission, TerrainSurveyOptions options,
        CancellationToken token = default) => new(DatabasePath, mission, options, token);
    public void RequireArchivedTerrainSurvey(Guid mission)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT status,ended_utc FROM missions WHERE id=$id"; cmd.Parameters.AddWithValue("$id", mission.ToString());
        using var row = cmd.ExecuteReader(); if (!row.Read()) throw new ArgumentException("Flight archive not found.");
        if (row.GetString(0) == "Recording" || row.IsDBNull(1)) throw new InvalidOperationException("End recording before reconstructing a terrain survey.");
    }
}

// A pinned, read-only SQLite snapshot, not replay through today's live controller.
// No migration, writes, repaired poses, or synthesized legacy collection sessions.
internal sealed class TerrainSurveyArchive : IDisposable
{
    const int RowLimit = 1048576;
    readonly SqliteConnection db;
    readonly SqliteTransaction tx;
    readonly Guid mission;
    readonly TerrainSurveyOptions options;
    readonly CancellationToken token;
    public DateTimeOffset StartedUtc { get; }
    public DateTimeOffset EndedUtc { get; }
    public string Fingerprint { get; }
    public long Rows { get; private set; }

    public TerrainSurveyArchive(string path, Guid mission, TerrainSurveyOptions options, CancellationToken token)
    {
        this.mission = mission; this.options = options; this.token = token;
        db = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); tx = db.BeginTransaction(deferred: true);
        try
        {
            using var cmd = Command("SELECT started_utc,ended_utc,status FROM missions WHERE id=$mission");
            using (var row = cmd.ExecuteReader())
            {
                if (!row.Read()) throw new ArgumentException("Flight archive not found.");
                if (row.GetString(2) == "Recording" || row.IsDBNull(1)) throw new InvalidOperationException("End recording before reconstructing a terrain survey.");
                StartedUtc = DateTimeOffset.Parse(row.GetString(0), CultureInfo.InvariantCulture);
                EndedUtc = DateTimeOffset.Parse(row.GetString(1), CultureInfo.InvariantCulture);
                if (EndedUtc < StartedUtc) throw new InvalidDataException("Invalid archived flight interval.");
            }
            if (HasTable("events") && Scalar("SELECT 1 FROM events WHERE mission_id=$mission AND kind='science-deleted' LIMIT 1") is not null)
                throw new InvalidOperationException("Collected science was permanently deleted. A derived corridor cannot recreate it.");
            Fingerprint = HashInputs();
        }
        catch { Dispose(); throw; }
    }

    SqliteCommand Command(string sql)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$mission", mission.ToString()); return cmd;
    }
    object? Scalar(string sql) { using var cmd = Command(sql); return cmd.ExecuteScalar(); }
    bool HasTable(string name)
    {
        using var cmd = Command("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name");
        cmd.Parameters.AddWithValue("$name", name); return cmd.ExecuteScalar() is not null;
    }
    static string Bounded(string column) => $"CASE WHEN length(CAST({column} AS BLOB))<={RowLimit} THEN {column} END";
    static string Text(SqliteDataReader rows, int n) => rows.IsDBNull(n)
        ? throw new InvalidDataException("An archive row is missing or exceeds the 1 MiB analysis bound; originals were preserved.") : rows.GetString(n);
    static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    string HashInputs()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long bytes = 0;
        void Add(string s)
        {
            token.ThrowIfCancellationRequested(); var b = Encoding.UTF8.GetBytes(s); bytes = checked(bytes + b.Length);
            if (bytes > options.MaximumArchiveBytes) throw new InvalidOperationException("Terrain survey archive byte budget exceeded. Choose a smaller archived flight or explicit bounded budget.");
            Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, b.Length); hash.AppendData(length); hash.AppendData(b);
        }
        Add(ContinuousTerrainSurveys.Algorithm); Add(JsonSerializer.Serialize(options, WireJson.Options));
        Add(JsonSerializer.Serialize(InstrumentProfiles.Generic("terrain-scanner"), WireJson.Options));
        Add(mission.ToString()); Add(StartedUtc.ToString("O")); Add(EndedUtc.ToString("O"));
        // Fixed table/key order; input receipts retain their exact stored JSON hashes.
        foreach (var (table, columns, order, filter) in new[] {
            ("science_sessions", $"id,{Bounded("payload_json")}", "sequence,id", ""),
            ("collection_events", $"sequence,{Bounded("payload_json")}", "sequence", ""),
            ("collection_inputs", $"receipt_id,sha256,{Bounded("payload_json")}", "receipt_id", ""),
            ("science_intents", $"id,state,COALESCE(product_id,''),COALESCE(product_sha256,''),{Bounded("payload_json")}", "state,id", ""),
            ("events", $"id,timestamp_utc,kind,{Bounded("message")}", "id", " AND kind IN ('connection','simulator-paused','simulator-resumed','collection-storage-hold','aircraft-changed','telemetry-gap','science-deleted')") })
        {
            Add(table); if (!HasTable(table)) { Add("absent"); continue; }
            using var cmd = Command($"SELECT {columns} FROM {table} WHERE mission_id=$mission{filter} ORDER BY {order}");
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                if (++Rows > options.MaximumArchiveRows) throw new InvalidOperationException("Terrain survey archive row budget exceeded.");
                for (var i = 0; i < rows.FieldCount; i++) Add(Text(rows, i));
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    ScienceCollectionSession Session(Guid id)
    {
        using var cmd = Command($"SELECT {Bounded("payload_json")} FROM science_sessions WHERE mission_id=$mission AND id=$id");
        cmd.Parameters.AddWithValue("$id", id.ToString()); using var row = cmd.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("Original collection session is missing.");
        var session = EvidenceContracts.Read<ScienceCollectionSession>(Text(row, 0));
        if (session.MissionId != mission || session.Id != id || session.StartedUtc < StartedUtc || session.EndedUtc > EndedUtc)
            throw new InvalidDataException("Archived session identity or flight interval mismatch.");
        return session;
    }
    TerrainSurveyGap Gap(string reason, CollectionEvent e, string? receipt = null) => new(reason, e.InputUtc, e.InputUtc, e.SessionId, e.Sequence, receipt);
    TerrainSurveyGap? Barrier(DateTimeOffset from, DateTimeOffset to, Guid? session)
    {
        using var cmd = Command("""
            SELECT id,timestamp_utc,kind FROM events WHERE mission_id=$mission
              AND kind IN ('connection','simulator-paused','simulator-resumed','collection-storage-hold','aircraft-changed','telemetry-gap')
              AND timestamp_utc>=$from AND timestamp_utc<=$to ORDER BY timestamp_utc,id LIMIT 1
            """);
        cmd.Parameters.AddWithValue("$from", from.ToString("O")); cmd.Parameters.AddWithValue("$to", to.ToString("O"));
        using var row = cmd.ExecuteReader(); return row.Read()
            ? new("Archived " + row.GetString(2) + " event " + row.GetInt64(0), from, to, session) : null;
    }
    public IEnumerable<TerrainSurveyInput> Inputs()
    {
        if (!HasTable("collection_events") || !HasTable("collection_inputs") || !HasTable("science_sessions"))
        { yield return new(Gap: new("Legacy archive has no verifiable collection sessions/pose receipts", StartedUtc, EndedUtc)); yield break; }
        using var cmd = Command($"SELECT sequence,{Bounded("payload_json")} FROM collection_events WHERE mission_id=$mission ORDER BY sequence");
        using var rows = cmd.ExecuteReader(); ScienceCollectionSession? session = null; DateTimeOffset? previous = null, previousRecorded = null;
        long sequence = 0; bool any = false; var phase = CollectionPhase.Armed;
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested(); any = true;
            var e = EvidenceContracts.Read<CollectionEvent>(Text(rows, 1));
            if (e.MissionId != mission || e.Sequence != rows.GetInt64(0) || e.Sequence != sequence + 1 || e.From != phase) throw new InvalidDataException("Collection event identity/order/phase history mismatch.");
            sequence = e.Sequence; phase = e.To;
            if (e.Outcome != CollectionEventOutcome.Applied) continue;
            if (e.Reason != MissionStore.IngressReason)
            {
                previous = null; yield return new(Gap: Gap("Collection boundary: " + e.Action + " / " + e.From + " → " + e.To, e)); continue;
            }
            if (e.From != CollectionPhase.Collecting || e.To != CollectionPhase.Collecting || e.SessionId is null)
            { previous = null; yield return new(Gap: Gap("Science was not collecting", e)); continue; }
            if (session?.Id != e.SessionId) session = Session(e.SessionId.Value);
            foreach (var link in e.Evidence)
            {
                using var input = Command($"SELECT sha256,{Bounded("payload_json")} FROM collection_inputs WHERE mission_id=$mission AND receipt_id=$id");
                input.Parameters.AddWithValue("$id", link.Id); string json;
                using (var row = input.ExecuteReader())
                {
                    if (!row.Read()) throw new InvalidDataException("Original pose receipt is missing.");
                    json = Text(row, 1);
                    if (Digest(json) != row.GetString(0) || row.GetString(0) != link.Sha256) throw new InvalidDataException("Original pose receipt hash mismatch.");
                }
                using var raw = JsonDocument.Parse(json);
                if (!raw.RootElement.TryGetProperty("frame", out var rawFrame) || rawFrame.ValueKind == JsonValueKind.Null) continue;
                if (MissingPose(raw.RootElement) is {} missing)
                { previous = null; yield return new(Gap: Gap(missing, e, link.Id)); continue; }
                var packet = JsonSerializer.Deserialize<TelemetryUpdate>(json, WireJson.Options)!;
                if (packet.Frame is null) continue; // Auxiliary atmosphere packets never supply position/attitude.
                var f = packet.Frame;
                var reference = new TerrainSurveyTelemetryReference(link.Id, link.Sha256, link.ObservedUtc, e.Sequence, link.Origin, e.RecordedUtc);
                string? reason = null;
                if (reason is null && (!f.IsValid || f.Source != packet.Connection.Source || link.ObservedUtc != f.TimestampUtc || e.InputUtc != f.TimestampUtc
                    || link.Origin != (f.Source == TelemetrySource.Simulated ? EvidenceOrigin.SyntheticFixture : EvidenceOrigin.SimulatorReported))) reason = "Invalid or mismatched archived pose identity/time";
                if (reason is null && (packet.Connection.State != ConnectionState.Connected || f.IsPaused)) reason = "Disconnected or simulator paused";
                var age = (e.RecordedUtc - f.TimestampUtc).TotalSeconds;
                if (reason is null && (age < -2 || age > options.MaximumPositionAgeSeconds)) reason = "Pose was stale/future-dated when archived";
                if (reason is null && (f.TimestampUtc < session.StartedUtc || f.TimestampUtc > session.EndedUtc || f.TimestampUtc < StartedUtc || f.TimestampUtc > EndedUtc)) reason = "Pose outside original session/flight interval";
                var config = session.Configurations.Where(c => c.Settings.InstrumentId == "terrain-scanner" && c.EffectiveUtc <= f.TimestampUtc).MaxBy(c => c.Revision);
                if (reason is null && config is not { Settings.Enabled: true }) reason = "Terrain Scanner inactive or frozen configuration unavailable";
                if (reason is not null) { previous = null; yield return new(Gap: Gap(reason, e, link.Id)); continue; }
                // Include the receipt/observed-time envelope. A slightly future-dated
                // pose must not hide an unsampled host connection/pause event.
                var from = previousRecorded < previous ? previousRecorded : previous;
                var to = e.RecordedUtc > f.TimestampUtc ? e.RecordedUtc : f.TimestampUtc;
                if (previous is {} before && f.TimestampUtc > before && from is {} start && Barrier(start, to, session.Id) is {} barrier) yield return new(Gap: barrier);
                previous = f.TimestampUtc; previousRecorded = e.RecordedUtc;
                yield return new(f, reference, session.Id, config);
            }
        }
        if (!any) yield return new(Gap: new("Legacy archive has no verified collection-active pose history", StartedUtc, EndedUtc));
    }
    static string? MissingPose(JsonElement packet)
    {
        if (!packet.TryGetProperty("frame", out var f) || f.ValueKind != JsonValueKind.Object) return "Missing archived pose";
        foreach (var field in new[] { "latitude", "longitude", "altitudeAglFeet", "headingDegrees", "groundTrackDegrees", "pitchDegrees", "bankDegrees", "groundspeedKnots" })
            if (!f.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !double.IsFinite(n)) return "Missing/invalid archived " + field;
        return null;
    }
    public IEnumerable<TerrainSurveyAcquisition> Acquisitions()
    {
        if (!HasTable("science_intents")) yield break;
        using var cmd = Command($"SELECT {Bounded("payload_json")},state,product_id,product_sha256,id,session_id,configuration_id FROM science_intents WHERE mission_id=$mission AND instrument_id='terrain-scanner' ORDER BY state,id");
        using var rows = cmd.ExecuteReader(); while (rows.Read())
        {
            token.ThrowIfCancellationRequested(); var i = JsonSerializer.Deserialize<ScienceAcquisitionIntent>(Text(rows, 0), WireJson.Options)!;
            if (i.MissionId != mission || i.InstrumentId != "terrain-scanner" || i.Id != Guid.Parse(rows.GetString(4))
                || i.SessionId != Guid.Parse(rows.GetString(5)) || i.ConfigurationId != Guid.Parse(rows.GetString(6))) throw new InvalidDataException("Acquisition reference identity mismatch.");
            yield return new(i.Id, i.SessionId, i.ConfigurationId, i.InstrumentId, i.ExposureUtc, i.InputReceiptId, rows.GetString(1), rows.IsDBNull(2) ? null : Guid.Parse(rows.GetString(2)), rows.IsDBNull(3) ? null : rows.GetString(3));
        }
    }
    public void Dispose() { tx.Dispose(); db.Dispose(); }
}
