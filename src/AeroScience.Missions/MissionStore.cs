using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public sealed partial class MissionStore
{
    public const int CurrentSchemaVersion = 10;
    private readonly string connectionString;
    public string DatabasePath { get; }

    public MissionStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString();
    }

    private SqliteConnection Open(bool allowCreate = false)
    {
        var selected = new SqliteConnectionStringBuilder(connectionString);
        if (allowCreate) selected.Mode = SqliteOpenMode.ReadWriteCreate;
        var connection = new SqliteConnection(selected.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        InitializeCore();
        using var db = Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) < 9) MigratePersonalResearch(db);
        cmd.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) < 10) MigrateResearchOperations(db);
        cmd.CommandText="CREATE INDEX IF NOT EXISTS personal_card_attempts_card ON personal_card_attempts(card_id,started_utc DESC,id)";
        cmd.ExecuteNonQuery(); // Additive read index; no schema-version change or historical row rewrite.
    }

    private void InitializeCore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var db = Open(allowCreate: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion) throw new InvalidOperationException("Database was created by a newer AeroScience version; refusing to modify it.");
        if (version is >= 1 and < CurrentSchemaVersion)
        {
            var size = StorageManager.DatabaseBytes(DatabasePath);
            if ((EvidenceFreeBytes?.Invoke() ?? new LocalStorageCapacity().FreeBytes(Path.GetDirectoryName(DatabasePath)!)) < size + StorageManager.MinimumFreeBytes)
                throw new IOException("Free space is insufficient for the verified upgrade backup. The original archive was not modified.");
            var backups = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Backups"); Directory.CreateDirectory(backups);
            var backup = Path.Combine(backups, $"missions-schema{version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
            { target.Open(); db.BackupDatabase(target); }
            if (StorageManager.DatabaseFingerprint(DatabasePath) != StorageManager.DatabaseFingerprint(backup))
                throw new IOException("Upgrade backup verification failed; the original archive was not modified.");
        }
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        if (version == CurrentSchemaVersion)
        {
            // Already current: no JSON or historical sample is rewritten.
            return;
        }
        if (version is 8 or 9) return;
        if (version == 7) { MigrateCollectionEvidence(db); return; }
        if (version == 6) { MigrateScientificReview(db); MigrateCollectionEvidence(db); return; }
        if (version == 5) { MigrateMissionScience(db); MigrateCollectionEvidence(db); return; }
        if (version == 4) { MigrateExperience(db); MigrateMissionScience(db); MigrateCollectionEvidence(db); return; }
        if (version == 3) { MigrateResearch(db); MigrateExperience(db); MigrateMissionScience(db); MigrateCollectionEvidence(db); return; }
        if (version == 2) { MigrateLibraryReferences(db); MigrateResearch(db); MigrateExperience(db); MigrateMissionScience(db); MigrateCollectionEvidence(db); return; }
        if (version == 1) { MigrateMode(db); MigrateLibraryReferences(db); MigrateResearch(db); MigrateExperience(db); MigrateMissionScience(db); MigrateCollectionEvidence(db); return; }
        using var transaction = db.BeginTransaction();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE missions (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, aircraft TEXT, source TEXT,
              started_utc TEXT NOT NULL, ended_utc TEXT, status TEXT NOT NULL,
              departure_lat REAL, departure_lon REAL, arrival_lat REAL, arrival_lon REAL);
            CREATE UNIQUE INDEX one_recording_mission ON missions(status) WHERE status='Recording';
            CREATE TABLE telemetry (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, mission_id TEXT NOT NULL REFERENCES missions(id),
              timestamp_utc TEXT NOT NULL, latitude REAL NOT NULL, longitude REAL NOT NULL,
              altitude_msl_ft REAL NOT NULL, payload_json TEXT NOT NULL);
            CREATE INDEX telemetry_timeline ON telemetry(mission_id,sequence);
            CREATE INDEX telemetry_receipt_time ON telemetry(mission_id,timestamp_utc,sequence);
            CREATE TABLE events (
              id INTEGER PRIMARY KEY AUTOINCREMENT, mission_id TEXT NOT NULL REFERENCES missions(id),
              timestamp_utc TEXT NOT NULL, kind TEXT NOT NULL, message TEXT NOT NULL);
            CREATE INDEX event_timeline ON events(mission_id,id);
            CREATE TABLE instrument_metadata (
              mission_id TEXT NOT NULL REFERENCES missions(id), instrument_id TEXT NOT NULL,
              metadata_json TEXT NOT NULL, PRIMARY KEY(mission_id,instrument_id));
            CREATE TABLE observations (
              id INTEGER PRIMARY KEY, mission_id TEXT NOT NULL REFERENCES missions(id),
              instrument_id TEXT NOT NULL, timestamp_utc TEXT NOT NULL, provenance TEXT NOT NULL,
              payload_json TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();
        MigrateMode(db);
        MigrateLibraryReferences(db);
        MigrateResearch(db);
        MigrateExperience(db);
        MigrateMissionScience(db);
        MigrateCollectionEvidence(db);
    }

    private static void MigrateLibraryReferences(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE dataset_references (
              dataset_id TEXT PRIMARY KEY, mission_id TEXT NOT NULL REFERENCES missions(id),
              library_id TEXT NOT NULL, relative_path TEXT NOT NULL, metadata_json TEXT NOT NULL);
            PRAGMA user_version=3;
            """;
        cmd.ExecuteNonQuery(); tx.Commit();
    }

    public IEnumerable<TelemetryFrame> StreamSamples(Guid id)
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT payload_json FROM telemetry WHERE mission_id=$id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", id.ToString());
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<TelemetryFrame>(reader.GetString(0), WireJson.Options)!;
    }

    public IReadOnlyList<DatasetReference> DatasetReferences(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT dataset_id,library_id,relative_path,metadata_json,available FROM dataset_references WHERE mission_id=$id ORDER BY dataset_id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); using var reader = cmd.ExecuteReader();
        var references = new List<DatasetReference>();
        while (reader.Read()) references.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return references;
    }

    public void AddDatasetReference(Guid missionId, DatasetReference reference)
    {
        if (Path.IsPathRooted(reference.RelativePath) || reference.RelativePath.Split('/', '\\').Any(p => p is "." or "..") || reference.RelativePath.Contains(':'))
            throw new ArgumentException("Dataset paths must be relative to a stable library identifier.");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO dataset_references(dataset_id,mission_id,library_id,relative_path,metadata_json) VALUES($dataset,$mission,$library,$path,$metadata)";
        cmd.Parameters.AddWithValue("$dataset", reference.DatasetId.ToString()); cmd.Parameters.AddWithValue("$mission", missionId.ToString());
        cmd.Parameters.AddWithValue("$library", reference.LibraryId.ToString()); cmd.Parameters.AddWithValue("$path", reference.RelativePath);
        cmd.Parameters.AddWithValue("$metadata", reference.MetadataJson); cmd.ExecuteNonQuery();
    }

    public async Task ExportJsonAsync(Guid id, Stream output, CancellationToken cancellationToken = default)
    {
        var detail = Detail(id) ?? throw new ArgumentException("Mission not found.");
        if (detail.Mission.Status == MissionStatus.Recording) throw new InvalidOperationException("End the mission before exporting its complete archive.");
        // Buffer JSON tokens, then use asynchronous stream writes (Kestrel forbids sync I/O).
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        async Task FlushAsync()
        {
            writer.Flush(); await output.WriteAsync(buffer.WrittenMemory, cancellationToken); buffer.Clear();
        }
        writer.WriteStartObject(); writer.WriteNumber("formatVersion", 2);
        writer.WriteString("application", "AeroScience Science Mission Operations");
        writer.WriteString("timestampBasis", "Host UTC at source packet receipt; recorded timestamps are preserved");
        writer.WritePropertyName("units"); JsonSerializer.Serialize(writer, ResearchExports.TelemetryUnits, WireJson.Options);
        writer.WritePropertyName("mission"); JsonSerializer.Serialize(writer, detail.Mission, WireJson.Options);
        writer.WritePropertyName("events");
        // Export every event, not the detail dialog's bounded preview.
        writer.WriteStartArray();
        using (var db = Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id,timestamp_utc,kind,message FROM events WHERE mission_id=$id ORDER BY id";
            cmd.Parameters.AddWithValue("$id", id.ToString()); using var reader = cmd.ExecuteReader();
            var eventCount = 0;
            while (reader.Read())
            {
                JsonSerializer.Serialize(writer, new MissionEvent(reader.GetInt64(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3)), WireJson.Options);
                if (++eventCount % 256 == 0) await FlushAsync();
            }
        }
        writer.WriteEndArray(); writer.WritePropertyName("datasets"); JsonSerializer.Serialize(writer, ResolvedDatasetReferences(id), WireJson.Options);
        writer.WritePropertyName("telemetry"); writer.WriteStartArray();
        var count = 0;
        foreach (var sample in StreamSamples(id))
        {
            cancellationToken.ThrowIfCancellationRequested(); JsonSerializer.Serialize(writer, sample, WireJson.Options);
            if (++count % 256 == 0) await FlushAsync();
        }
        writer.WriteEndArray();
        await WriteResearchJsonAsync(id, writer, FlushAsync, cancellationToken);
        await WriteCollectionEvidenceJsonAsync(id, writer, FlushAsync, cancellationToken);
        writer.WriteEndObject(); await FlushAsync();
    }

    private static void MigrateMode(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE missions ADD COLUMN mode TEXT NOT NULL DEFAULT 'Universal'; PRAGMA user_version=2;";
        cmd.ExecuteNonQuery(); tx.Commit();
    }

    public void RecoverInterrupted()
    {
        foreach (var mission in List(100, 0, recordingOnly: true))
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT MAX(timestamp_utc) FROM telemetry WHERE mission_id=$id";
            cmd.Parameters.AddWithValue("$id", mission.Id.ToString());
            var last = cmd.ExecuteScalar() as string;
            Finish(mission.Id, MissionStatus.Interrupted, last is null ? mission.StartedUtc : Parse(last),
                "Recording recovered after the engine stopped; retained all committed samples.");
        }
    }

    public Guid Create(string name, TelemetryFrame? frame, DateTimeOffset now, ScienceMode mode = ScienceMode.Universal,
        MissionPlan? plan = null, InstrumentConfiguration[]? instruments = null, string? objective = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown science operating mode.");
        var id = Guid.NewGuid();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO missions(id,name,aircraft,source,started_utc,status,departure_lat,departure_lon,mode)
            VALUES($id,$name,$aircraft,$source,$now,'Recording',$lat,$lon,$mode)
            """;
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$mode", mode.ToString());
        cmd.Parameters.AddWithValue("$aircraft", (object?)frame?.Aircraft ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)frame?.Source.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Stamp(now));
        cmd.Parameters.AddWithValue("$lat", (object?)frame?.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)frame?.Longitude ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        Event(db, tx, id, now, "started", "Mission recording started.");
        WriteResearch(db, tx, id, plan, instruments, objective);
        FreezeResearchCards(db, tx, id, plan);
        Event(db,tx,id,now,"payload-snapshot",JsonSerializer.Serialize(instruments??plan?.Instruments??[],WireJson.Options));
        tx.Commit();
        return id;
    }

    public void Append(Guid id, TelemetryFrame frame)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        Append(db, tx, id, frame);
        tx.Commit();
    }

    private static void Append(SqliteConnection db, SqliteTransaction tx, Guid id, TelemetryFrame frame)
    {
        if (!frame.IsValid) throw new ArgumentException("Invalid telemetry cannot be recorded.");
        if (frame.MachNumber is { } mach && (!double.IsFinite(mach) || mach < 0)) frame = frame with { MachNumber = null };
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT status,aircraft,source FROM missions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "Recording") throw new InvalidOperationException("Mission is not recording.");
        }
        cmd.CommandText = """
            INSERT INTO telemetry(mission_id,timestamp_utc,latitude,longitude,altitude_msl_ft,payload_json)
            VALUES($id,$time,$lat,$lon,$alt,$json);
            UPDATE missions SET aircraft=COALESCE(aircraft,$aircraft),source=COALESCE(source,$source),
              departure_lat=COALESCE(departure_lat,$lat),departure_lon=COALESCE(departure_lon,$lon),
              arrival_lat=$lat,arrival_lon=$lon WHERE id=$id;
            """;
        cmd.Parameters.AddWithValue("$time", Stamp(frame.TimestampUtc));
        cmd.Parameters.AddWithValue("$lat", frame.Latitude);
        cmd.Parameters.AddWithValue("$lon", frame.Longitude);
        cmd.Parameters.AddWithValue("$alt", frame.AltitudeMslFeet);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(frame, WireJson.Options));
        cmd.Parameters.AddWithValue("$aircraft", frame.Aircraft);
        cmd.Parameters.AddWithValue("$source", frame.Source.ToString());
        cmd.ExecuteNonQuery();
    }

    public void AddEvent(Guid id, DateTimeOffset now, string kind, string message)
    {
        using var db = Open();
        Event(db, null, id, now, kind, message);
    }

    public void Finish(Guid id, MissionStatus status, DateTimeOffset now, string message)
    {
        if (status == MissionStatus.Recording) throw new ArgumentException("Final status required.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE missions SET status=$status,ended_utc=$now WHERE id=$id AND status='Recording'";
        cmd.Parameters.AddWithValue("$status", status.ToString());
        cmd.Parameters.AddWithValue("$now", Stamp(now));
        cmd.Parameters.AddWithValue("$id", id.ToString());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Mission is not recording.");
        Event(db, tx, id, now, status.ToString().ToLowerInvariant(), message);
        CloseEvidenceForFlight(db, tx, id, status, now);
        CloseResearchOperations(db, tx, id, now);
        tx.Commit();
    }

    private const string Selection = """
        SELECT m.id,m.name,m.aircraft,m.source,m.started_utc,m.ended_utc,m.status,
          m.departure_lat,m.departure_lon,m.arrival_lat,m.arrival_lon,
          COUNT(t.sequence) sample_count, MIN(t.altitude_msl_ft) minimum_altitude,
          MAX(t.altitude_msl_ft) maximum_altitude, CASE WHEN m.mode='Realistic' THEN 'Realistic' ELSE 'Universal' END
        FROM missions m LEFT JOIN telemetry t ON t.mission_id=m.id
        """;

    public IReadOnlyList<Mission> List(int limit = 50, int offset = 0, bool recordingOnly = false, string? search = null, string? status = null, string? sort = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        var ordering=sort switch { "oldest"=>"m.started_utc ASC,m.id", "name"=>"m.name COLLATE NOCASE ASC,m.started_utc DESC,m.id", _=>"m.started_utc DESC,m.id" };
        cmd.CommandText = Selection + " WHERE ($recording=0 OR m.status='Recording') AND ($search='' OR instr(lower(m.name || ' ' || COALESCE(m.aircraft,'')),lower($search))>0) AND ($status='' OR lower(m.status)=lower($status))"
            + " GROUP BY m.id ORDER BY " + ordering + " LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$recording", recordingOnly?1:0);cmd.Parameters.AddWithValue("$search",search?.Trim()??"");cmd.Parameters.AddWithValue("$status",status??"");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        var result = new List<Mission>();
        while (reader.Read()) result.Add(ReadMission(reader));
        return result;
    }

    public MissionDetail? Detail(Guid id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = Selection + " WHERE m.id=$id GROUP BY m.id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        Mission mission;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            mission = ReadMission(reader);
        }
        cmd.CommandText = "SELECT id,timestamp_utc,kind,message FROM events WHERE mission_id=$id ORDER BY id LIMIT 1000";
        using var eventsReader = cmd.ExecuteReader();
        var events = new List<MissionEvent>();
        while (eventsReader.Read()) events.Add(new(eventsReader.GetInt64(0), Parse(eventsReader.GetString(1)),
            eventsReader.GetString(2), eventsReader.GetString(3)));
        return new(mission, events);
    }

    public IReadOnlyList<TelemetryFrame> Samples(Guid id, int limit, int offset)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM telemetry WHERE mission_id=$id ORDER BY sequence LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        var frames = new List<TelemetryFrame>();
        while (reader.Read()) frames.Add(JsonSerializer.Deserialize<TelemetryFrame>(reader.GetString(0), WireJson.Options)!);
        return frames;
    }

    private static Mission ReadMission(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : Enum.Parse<TelemetrySource>(r.GetString(3)),
        Parse(r.GetString(4)), r.IsDBNull(5) ? null : Parse(r.GetString(5)), Enum.Parse<MissionStatus>(r.GetString(6)),
        r.IsDBNull(7) ? null : new(r.GetDouble(7), r.GetDouble(8)),
        r.IsDBNull(9) ? null : new(r.GetDouble(9), r.GetDouble(10)), r.GetInt32(11),
        r.IsDBNull(12) ? null : r.GetDouble(12), r.IsDBNull(13) ? null : r.GetDouble(13), Enum.Parse<ScienceMode>(r.GetString(14)));

    private static void Event(SqliteConnection db, SqliteTransaction? tx, Guid id, DateTimeOffset now, string kind, string message)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events(mission_id,timestamp_utc,kind,message) VALUES($id,$now,$kind,$message)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$now", Stamp(now));
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.ExecuteNonQuery();
    }
    private static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string time) => DateTimeOffset.Parse(time, CultureInfo.InvariantCulture);
}
