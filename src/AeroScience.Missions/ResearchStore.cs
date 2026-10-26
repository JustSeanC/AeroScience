using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public ScienceStore? ScienceLibrary { get; set; }
    public IReadOnlyList<DatasetReference> ResolvedDatasetReferences(Guid id) => DatasetReferences(id).Select(reference =>
    {
        if (ScienceLibrary is null) return reference;
        try
        {
            var path = StorageManager.ResolveDataset(ScienceLibrary.DirectoryPath, reference.LibraryId, reference.RelativePath);
            return reference with { Available = File.Exists(path) && (reference.RelativePath != ScienceStore.RelativeDatabasePath || ScienceLibrary.ContainsDataset(reference.DatasetId)) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException) { return reference with { Available = false }; }
    }).ToArray();
    private async Task WriteResearchJsonAsync(Guid id, Utf8JsonWriter writer, Func<Task> flush, CancellationToken cancellationToken)
    {
        var research = Research(id);
        writer.WritePropertyName("research"); writer.WriteStartObject();
        writer.WritePropertyName("planSnapshot"); JsonSerializer.Serialize(writer, research.PlanSnapshot, WireJson.Options);
        writer.WriteString("template", research.Template); writer.WriteString("objective", research.Objective);
        writer.WritePropertyName("instruments"); JsonSerializer.Serialize(writer, research.Instruments, WireJson.Options);
        writer.WritePropertyName("analysisSettings"); JsonSerializer.Serialize(writer, GetAnalysisSettings(id), WireJson.Options);
        writer.WritePropertyName("sciencePauseIntervals"); JsonSerializer.Serialize(writer, SciencePauses(id), WireJson.Options);
        writer.WritePropertyName("instrumentDiagnostics"); JsonSerializer.Serialize(writer, research.Diagnostics, WireJson.Options);
        writer.WritePropertyName("flightStatistics"); JsonSerializer.Serialize(writer, Statistics(id), WireJson.Options);
        writer.WritePropertyName("markers"); writer.WriteStartArray(); var count = 0;
        foreach (var marker in StreamMarkers(id))
        { cancellationToken.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, marker, WireJson.Options); if (++count % 128 == 0) await flush(); }
        writer.WriteEndArray(); writer.WritePropertyName("notes"); writer.WriteStartArray();
        foreach (var note in StreamNotes(id))
        { cancellationToken.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, note, WireJson.Options); if (++count % 128 == 0) await flush(); }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.WritePropertyName("science"); writer.WriteStartObject();
        var error = ScienceLibrary?.AvailabilityError();
        writer.WriteString("status", ScienceLibrary is null ? "Library not configured in this host" : error ?? "available");
        writer.WriteString("provenance", "Simulator-reported atmospheric values, not independent calibrated real-world measurements");
        writer.WritePropertyName("observations"); writer.WriteStartArray();
        if (ScienceLibrary is not null && error is null)
            foreach (var observation in ScienceLibrary.StreamObservations(id))
            { cancellationToken.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, observation, WireJson.Options); if (++count % 128 == 0) await flush(); }
        writer.WriteEndArray(); writer.WriteEndObject();
    }
    private static void MigrateResearch(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS mission_plans(id TEXT PRIMARY KEY,name TEXT NOT NULL,updated_utc TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS mission_research(mission_id TEXT PRIMARY KEY REFERENCES missions(id),
              plan_id TEXT,plan_snapshot_json TEXT,configuration_json TEXT NOT NULL,objective TEXT,template TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS research_markers(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL REFERENCES missions(id),
              timestamp_utc TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS markers_timeline ON research_markers(mission_id,timestamp_utc);
            CREATE TABLE IF NOT EXISTS research_notes(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL REFERENCES missions(id),
              timestamp_utc TEXT NOT NULL,payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS notes_timeline ON research_notes(mission_id,timestamp_utc);
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA table_info(dataset_references)";
        var columns = new List<string>(); using (var reader = cmd.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("available")) { cmd.CommandText = "ALTER TABLE dataset_references ADD COLUMN available INTEGER NOT NULL DEFAULT 1"; cmd.ExecuteNonQuery(); }
        cmd.CommandText = "PRAGMA user_version=4"; cmd.ExecuteNonQuery(); transaction.Commit();
    }
    private static void WriteResearch(SqliteConnection db, SqliteTransaction transaction, Guid id, MissionPlan? plan,
        InstrumentConfiguration[]? instruments, string? objective)
    {
        var configurations = ScienceCatalog.Normalize(instruments ?? plan?.Instruments);
        using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO mission_research VALUES($id,$plan,$snapshot,$configs,$objective,$template)";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$plan", (object?)plan?.Id.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$snapshot", plan is null ? DBNull.Value : JsonSerializer.Serialize(plan, WireJson.Options));
        cmd.Parameters.AddWithValue("$configs", JsonSerializer.Serialize(configurations, WireJson.Options));
        cmd.Parameters.AddWithValue("$objective", (object?)MissionPlanning.Text(objective ?? plan?.Objective) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$template", (plan?.Template ?? MissionTemplateKind.GeneralResearch).ToString()); cmd.ExecuteNonQuery();
    }
    public MissionPlan SavePlan(MissionPlan input)
    {
        if(input.ResearchStarter is {} origin&&Plan(input.Id)?.ResearchStarter!=origin)
            throw new ArgumentException("Starter provenance must match the prepared plan. Prepare a separate copy from My Research.");
        foreach (var card in SelectedResearchCards(input)) _ = ResearchCard(card.Id, card.Revision);
        var plan = MissionPlanning.Validate(input with { Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id });
        using var db = Open();WritePlan(db,null,plan);return plan;
    }
    static void WritePlan(SqliteConnection db,SqliteTransaction? tx,MissionPlan plan)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText = "INSERT INTO mission_plans VALUES($id,$name,$time,$json) ON CONFLICT(id) DO UPDATE SET name=$name,updated_utc=$time,payload_json=$json";
        cmd.Parameters.AddWithValue("$id", plan.Id.ToString()); cmd.Parameters.AddWithValue("$name", plan.Name);
        cmd.Parameters.AddWithValue("$time", Stamp(plan.UpdatedUtc!.Value)); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan, WireJson.Options));
        cmd.ExecuteNonQuery();
    }
    public MissionPlan? Plan(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload_json FROM mission_plans WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<MissionPlan>(json, WireJson.Options) : null;
    }
    public IReadOnlyList<MissionPlan> Plans(int limit = 50, int offset = 0)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM mission_plans ORDER BY updated_utc DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        using var reader = cmd.ExecuteReader(); var result = new List<MissionPlan>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<MissionPlan>(reader.GetString(0), WireJson.Options)!); return result;
    }
    public bool DeletePlan(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM mission_plans WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); return cmd.ExecuteNonQuery() == 1;
    }
    public MissionResearch Research(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT plan_snapshot_json,configuration_json,objective,template FROM mission_research WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        MissionPlan? plan = null; InstrumentConfiguration[] configurations = []; string? objective = null; var template = "GeneralResearch";
        using (var reader = cmd.ExecuteReader()) if (reader.Read())
        {
            plan = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<MissionPlan>(reader.GetString(0), WireJson.Options);
            configurations = JsonSerializer.Deserialize<InstrumentConfiguration[]>(reader.GetString(1), WireJson.Options) ?? [];
            objective = reader.IsDBNull(2) ? null : reader.GetString(2); template = reader.GetString(3);
        }
        return new(plan, configurations, objective, template, Notes(id, 200, 0).ToArray(), InstrumentDiagnostics(id));
    }
    public void SetInstruments(Guid id, InstrumentConfiguration[] configurations)
    {
        configurations = ScienceCatalog.Normalize(configurations);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE mission_research SET configuration_json=$json WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(configurations, WireJson.Options)); cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
    }
    internal MissionMarker? MarkerReceipt(Guid missionId,MarkerRequest request)
    {
        if(request.RequestId==Guid.Empty)throw new ArgumentException("Use a non-empty observation request identity.");
        if(request.RequestId is not {} id)return null;using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM research_markers WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        if(cmd.ExecuteScalar() is not string json)return null;var old=JsonSerializer.Deserialize<MissionMarker>(json,WireJson.Options)!;
        if(old.MissionId!=missionId||old.Category!=request.Category||old.Notes!=MissionPlanning.Text(request.Notes))throw new InvalidOperationException("This observation identity already belongs to a different selection or note.");return old;
    }
    ResearchCardReference[] ObservationCards(Guid mission)=>ResearchOperations(mission).Cards.Where(c=>c.Card.ObservationFlight is not null).Select(c=>new ResearchCardReference(c.Card.Id,c.Card.Revision)).ToArray();
    public MissionMarker AddMarker(Guid missionId, MarkerRequest request, DateTimeOffset timestamp, TelemetryFrame? frame, string? context)
    {
        if (!ScienceCatalog.MarkerCategories.Contains(request.Category)) throw new ArgumentException("Choose a known observation category.");
        if(MarkerReceipt(missionId,request) is {} prior)return prior;
        var cards=ObservationCards(missionId);if(cards.Length>0&&ScienceDeleted(missionId))throw new InvalidOperationException("Collected pilot research was permanently deleted.");
        var located=frame is{IsValid:true,IsPaused:false}&&(timestamp-frame.TimestampUtc).TotalSeconds is >=0 and <=6?frame:null;
        var marker = new MissionMarker(request.RequestId??Guid.NewGuid(), missionId, request.Category, timestamp.ToUniversalTime(), located?.Latitude,
            located?.Longitude, located?.AltitudeMslFeet, MissionPlanning.Text(request.Notes), context,
            cards.Length==0?null:new(1,cards,located?.TimestampUtc,located is null?ObservationQuality.Unavailable:ObservationQuality.Valid));
        InsertResearch("research_markers", marker.Id, missionId, timestamp, marker); return marker;
    }
    internal MissionNote? NoteReceipt(Guid mission,NoteRequest request)
    {
        if(request.RequestId==Guid.Empty)throw new ArgumentException("Use a non-empty note request identity.");if(request.RequestId is not {} id)return null;
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT payload_json FROM research_notes WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        if(cmd.ExecuteScalar() is not string json)return null;var old=JsonSerializer.Deserialize<MissionNote>(json,WireJson.Options)!;
        if(old.MissionId!=mission||old.Phase!=request.Phase||old.Text!=MissionPlanning.Text(request.Text))throw new InvalidOperationException("This note identity already belongs to different text or a different flight.");return old;
    }
    public MissionNote AddNote(Guid missionId, NoteRequest request)
    {
        if(NoteReceipt(missionId,request) is {} receipt)return receipt;
        if (Detail(missionId) is null) throw new ArgumentException("Mission not found.");
        if (request.Phase is not ("preflight" or "in-flight" or "postflight")) throw new ArgumentException("Choose a research note phase.");
        var text = MissionPlanning.Text(request.Text);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter research note text.");
        var cards=ObservationCards(missionId);if(cards.Length>0&&ScienceDeleted(missionId))throw new InvalidOperationException("Collected pilot research was permanently deleted.");
        var note = new MissionNote(request.RequestId??Guid.NewGuid(), missionId, request.Phase, text, DateTimeOffset.UtcNow,cards.Length==0?null:cards);
        InsertResearch("research_notes", note.Id, missionId, note.TimestampUtc, note); return note;
    }
    private void InsertResearch<T>(string table, Guid id, Guid mission, DateTimeOffset time, T item)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table} VALUES($id,$mission,$time,$json)";
        cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$mission", mission.ToString());
        cmd.Parameters.AddWithValue("$time", Stamp(time)); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, WireJson.Options)); cmd.ExecuteNonQuery();
    }
    private IEnumerable<T> StreamResearch<T>(string table, Guid mission, int? limit = null, int offset = 0)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT payload_json FROM {table} WHERE mission_id=$id ORDER BY "
            + (limit is null ? "timestamp_utc,rowid" : "timestamp_utc DESC,rowid DESC LIMIT $limit OFFSET $offset");
        if (limit is not null) { cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit.Value, 1, 1000)); cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset)); }
        cmd.Parameters.AddWithValue("$id", mission.ToString()); using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<T>(reader.GetString(0), WireJson.Options)!;
    }
    public IEnumerable<MissionMarker> StreamMarkers(Guid id) => StreamResearch<MissionMarker>("research_markers", id);
    public IEnumerable<MissionNote> StreamNotes(Guid id) => StreamResearch<MissionNote>("research_notes", id);
    public IReadOnlyList<MissionMarker> Markers(Guid id, int limit = 200, int offset = 0) => StreamResearch<MissionMarker>("research_markers", id, limit, offset).ToArray();
    public IReadOnlyList<MissionNote> Notes(Guid id, int limit = 200, int offset = 0) => StreamResearch<MissionNote>("research_notes", id, limit, offset).ToArray();
    public IEnumerable<MissionEvent> StreamEvents(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,timestamp_utc,kind,message FROM events WHERE mission_id=$id ORDER BY id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3));
    }
    public TrackPage Track(Guid id, int limit = 1500, long? after = null, bool recent = false)
    {
        limit = Math.Clamp(limit, 3, 3000);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*),COALESCE(MAX(sequence),0) FROM telemetry WHERE mission_id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString());
        long count, last; using (var reader = cmd.ExecuteReader()) { reader.Read(); count = reader.GetInt64(0); last = reader.GetInt64(1); }
        DateTimeOffset? previousTime = null;
        if (after is not null)
        {
            cmd.CommandText = "SELECT timestamp_utc FROM telemetry WHERE mission_id=$id AND sequence<=$after ORDER BY sequence DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$after", Math.Max(0, after.Value));
            if (cmd.ExecuteScalar() is string stamp) previousTime = Parse(stamp);
        }
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        if (after is not null)
            cmd.CommandText = "SELECT sequence,timestamp_utc,latitude,longitude,altitude_msl_ft,0 FROM telemetry WHERE mission_id=$id AND sequence>$after ORDER BY sequence LIMIT $limit";
        else if (recent)
            cmd.CommandText = "SELECT sequence,timestamp_utc,latitude,longitude,altitude_msl_ft,0 FROM telemetry WHERE mission_id=$id ORDER BY sequence DESC LIMIT $limit";
        else
        {
            // Cumulative gap counts preserve real discontinuities between reduced display
            // vertices, without treating deliberate downsampling as missing telemetry.
            cmd.CommandText = """
                WITH samples AS (SELECT sequence,timestamp_utc,latitude,longitude,altitude_msl_ft,
                  LAG(timestamp_utc) OVER(ORDER BY sequence) previous FROM telemetry WHERE mission_id=$id),
                ranked AS (SELECT *,ROW_NUMBER() OVER(ORDER BY sequence) n,
                  SUM(CASE WHEN previous IS NOT NULL AND ((julianday(timestamp_utc)-julianday(previous))*86400>5.001
                    OR julianday(timestamp_utc)<=julianday(previous)) THEN 1 ELSE 0 END) OVER(ORDER BY sequence) gaps FROM samples)
                SELECT sequence,timestamp_utc,latitude,longitude,altitude_msl_ft,gaps FROM ranked
                  WHERE n=1 OR sequence=$last OR (n-1)%$stride=0 ORDER BY sequence LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$last", last); cmd.Parameters.AddWithValue("$stride", Math.Max(1, (long)Math.Ceiling((count - 2) / (double)(limit - 2))));
        }
        using var rows = cmd.ExecuteReader(); var found = new List<(TrackPoint Point, long Gaps)>();
        while (rows.Read()) found.Add((new(rows.GetInt64(0), Parse(rows.GetString(1)), rows.GetDouble(2), rows.GetDouble(3), rows.GetDouble(4)), rows.GetInt64(5)));
        var more = after is not null && found.Count > limit;
        if (recent && after is null) found.Reverse();
        if (found.Count > limit) found.RemoveAt(recent && after is null ? 0 : found.Count - 1);
        long gaps = 0; var points = new List<TrackPoint>();
        foreach (var item in found)
        {
            var discontinuity = after is null && !recent ? item.Gaps > gaps : previousTime is { } t && (item.Point.TimestampUtc - t).TotalSeconds is > 5.001 or <= 0;
            points.Add(item.Point with { BreakBefore = discontinuity }); gaps = item.Gaps; previousTime = item.Point.TimestampUtc;
        }
        return new(points.ToArray(), after is null ? last : points.LastOrDefault()?.Sequence ?? after.Value, more, count, after is null && count > points.Count);
    }
    public FlightStatistics Statistics(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.CommandText = """
            SELECT COUNT(*),MIN(altitude_msl_ft),MAX(altitude_msl_ft),AVG(altitude_msl_ft),
              MIN(json_extract(payload_json,'$.indicatedAirspeedKnots')),MAX(json_extract(payload_json,'$.indicatedAirspeedKnots')),
              AVG(json_extract(payload_json,'$.indicatedAirspeedKnots')),MIN(json_extract(payload_json,'$.machNumber')),
              MAX(json_extract(payload_json,'$.machNumber')),AVG(json_extract(payload_json,'$.machNumber')),
              COUNT(json_extract(payload_json,'$.machNumber')),
              (SELECT COUNT(*) FROM events WHERE mission_id=$id AND kind='telemetry-gap'),
              CASE WHEN COUNT(*)>1 THEN (julianday(MAX(timestamp_utc))-julianday(MIN(timestamp_utc)))*86400.0/(COUNT(*)-1) END
            FROM telemetry WHERE mission_id=$id
            """;
        using var r = cmd.ExecuteReader(); r.Read(); double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        return new(r.GetInt64(0), D(1), D(2), D(3), D(4), D(5), D(6), D(7), D(8), D(9), r.GetInt64(10), r.GetInt64(11), D(12));
    }
    public DataClearPreview PreviewFlightClear(bool includePlans)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT (SELECT COUNT(*) FROM missions),(SELECT COUNT(*) FROM mission_plans)";
        using var reader = cmd.ExecuteReader(); reader.Read();
        return new("flight", Path.GetDirectoryName(DatabasePath)!, reader.GetInt64(0), includePlans ? reader.GetInt64(1) : 0, 0, 0,
            StorageManager.DatabaseBytes(DatabasePath), "Flight recordings, telemetry, events, markers, recording notes and dataset links" + (includePlans ? "; saved plans and preflight notes" : ""),
            "Scientific Data Library products, library identity, application settings, built-in templates" + (includePlans ? "" : ", saved mission plans"), "DELETE FLIGHT DATA", ProtectedItems: ProtectedMissionCount());
    }
    public void ClearFlightData(string confirmation, bool includePlans = false)
    {
        if (confirmation != "DELETE FLIGHT DATA") throw new InvalidOperationException("Type DELETE FLIGHT DATA to confirm this exact scope.");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM missions WHERE status='Recording'";
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException("End recording before clearing data.");
        cmd.CommandText = "SELECT COUNT(*) FROM observations";
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException("Legacy scientific rows exist inside this mission database. Export them before clearing flight history.");
        cmd.CommandText = "DELETE FROM scientific_recovery; DELETE FROM analysis_settings; DELETE FROM instrument_metadata; DELETE FROM research_markers; DELETE FROM research_notes; DELETE FROM mission_research; DELETE FROM dataset_references; DELETE FROM events; DELETE FROM telemetry; DELETE FROM missions;"
            + (includePlans ? " DELETE FROM mission_plans;" : "");
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    public void SaveInstrumentState(Guid id, InstrumentState state)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO instrument_metadata VALUES($mission,$instrument,$json)";
        cmd.Parameters.AddWithValue("$mission", id.ToString()); cmd.Parameters.AddWithValue("$instrument", state.InstrumentId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, WireJson.Options)); cmd.ExecuteNonQuery();
    }
    public InstrumentState[] InstrumentDiagnostics(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT metadata_json FROM instrument_metadata WHERE mission_id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); using var reader = cmd.ExecuteReader(); var result = new List<InstrumentState>();
        while (reader.Read()) { try { var state = JsonSerializer.Deserialize<InstrumentState>(reader.GetString(0), WireJson.Options); if (state is not null && state.Measurements is not null) result.Add(state); } catch (JsonException) { /* Preserve readable legacy instrument metadata in the database. */ } }
        return result.ToArray();
    }
    public void ReconcileDatasetReferences(Guid libraryId, Func<Guid, bool> exists)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT dataset_id FROM dataset_references WHERE library_id=$library AND relative_path='Observations/science.sqlite'";
        cmd.Parameters.AddWithValue("$library", libraryId.ToString()); var ids = new List<Guid>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        using var tx = db.BeginTransaction(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE dataset_references SET available=$available WHERE dataset_id=$id";
        cmd.Parameters.AddWithValue("$available", 0); cmd.Parameters.AddWithValue("$id", "");
        foreach (var id in ids) { cmd.Parameters["$available"].Value = exists(id) ? 1 : 0; cmd.Parameters["$id"].Value = id.ToString(); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
}
