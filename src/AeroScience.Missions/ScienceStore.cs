using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

// The science database belongs to the independently movable library, not missions.db.
public sealed class ScienceStore(string directory, IStorageCapacity? capacity = null) : IDisposable
{
    public const string RelativeDatabasePath = "Observations/science.sqlite";
    private const string MarkerName = ".science-storage.json";
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string DatabasePath => Path.Combine(DirectoryPath, RelativeDatabasePath);
    public Guid LibraryId => StorageManager.LibraryId(DirectoryPath);
    private readonly object gate = new();
    private FileStream? lease;
    private readonly IStorageCapacity disk = capacity ?? new LocalStorageCapacity();
    public string? AvailabilityError()
    {
        try
        {
            _ = LibraryId;
            StorageManager.ValidateLocalPath(DatabasePath);
            if (File.Exists(Path.Combine(DirectoryPath, MarkerName)) && !File.Exists(DatabasePath))
                throw new IOException("The configured science database is missing. Reconnect/restore its library; an empty replacement was not created.");
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return error.Message; }
    }
    private SqliteConnection Open(bool write = false, bool create = false)
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!create && !File.Exists(DatabasePath)) throw new IOException("This library has no recorded scientific datasets yet.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
            DefaultTimeout = 10
        }.ToString());
        try
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; cmd.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    public void Initialize()
    {
        lock (gate)
        {
            if (AvailabilityError() is { } error) throw new IOException(error);
            var id = LibraryId;
            lease ??= StorageManager.Acquire(DirectoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            var existed = File.Exists(DatabasePath);
            using var db = Open(write: true, create: !existed); using var cmd = db.CreateCommand();
            if (!existed)
            {
                using var tx = db.BeginTransaction(); cmd.Transaction = tx;
                cmd.CommandText = """
                    CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                    CREATE TABLE datasets(id TEXT PRIMARY KEY,mission_id TEXT NOT NULL,instrument_id TEXT NOT NULL,payload_json TEXT NOT NULL,
                      UNIQUE(mission_id,instrument_id));
                    CREATE TABLE observations(id INTEGER PRIMARY KEY AUTOINCREMENT,dataset_id TEXT NOT NULL REFERENCES datasets(id),
                      mission_id TEXT NOT NULL,timestamp_utc TEXT NOT NULL,measurement_id TEXT NOT NULL,value REAL NOT NULL,
                      unit TEXT NOT NULL,quality TEXT NOT NULL,payload_json TEXT NOT NULL,
                      UNIQUE(dataset_id,measurement_id,timestamp_utc));
                    CREATE INDEX science_mission_timeline ON observations(mission_id,id);
                    CREATE INDEX science_dataset_timeline ON observations(dataset_id,id);
                    INSERT INTO metadata VALUES('library_id',$library);
                    PRAGMA user_version=1;
                    """;
                cmd.Parameters.AddWithValue("$library", id.ToString()); cmd.ExecuteNonQuery(); tx.Commit(); cmd.Transaction = null;
            }
            cmd.CommandText = "SELECT value FROM metadata WHERE key='library_id'";
            if (cmd.ExecuteScalar() as string != id.ToString()) throw new IOException("Science database/library identity mismatch; nothing was replaced.");
            cmd.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new IOException("Unsupported science schema; preserve this library.");
            if (!File.Exists(Path.Combine(DirectoryPath, MarkerName)))
                PreferenceStore.WriteAtomic(Path.Combine(DirectoryPath, MarkerName), new { formatVersion = 1, libraryId = id, database = RelativeDatabasePath });
        }
    }
    public ScienceDataset EnsureDataset(Mission mission, InstrumentConfiguration configuration, MissionResearch research)
    {
        Initialize();
        var definition = ScienceCatalog.Definitions.Single(d => d.Id == configuration.InstrumentId);
        var dataset = new ScienceDataset(Guid.NewGuid(), LibraryId, mission.Id, mission.Name, definition.Id, DateTimeOffset.UtcNow,
            RelativeDatabasePath, 0, JsonSerializer.Serialize(new
            {
                definition,
                configuration,
                mission.Mode,
                mission.Source,
                research.PlanSnapshot,
                research.Objective,
                timestampBasis = "Host UTC when the SimConnect packet was received",
                disclaimer = "Simulator-reported atmospheric values; not independent calibrated real-world measurements. No modeled instrument output is substituted."
            }, WireJson.Options));
        using var db = Open(write: true); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO datasets VALUES($id,$mission,$instrument,$json)";
        cmd.Parameters.AddWithValue("$id", dataset.Id.ToString()); cmd.Parameters.AddWithValue("$mission", mission.Id.ToString());
        cmd.Parameters.AddWithValue("$instrument", configuration.InstrumentId); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(dataset, WireJson.Options)); cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT payload_json FROM datasets WHERE mission_id=$mission AND instrument_id=$instrument";
        return JsonSerializer.Deserialize<ScienceDataset>((string)cmd.ExecuteScalar()!, WireJson.Options)!;
    }
    public int Append(IReadOnlyList<ScienceObservation> observations)
    {
        if (observations.Count == 0) return 0;
        lock (gate)
        {
            if (disk.FreeBytes(DirectoryPath) < StorageManager.MinimumFreeBytes) throw new IOException("Science storage is low. No data was deleted; science acquisition is paused.");
            using var db = Open(write: true); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            var inserted = 0;
            foreach (var item in observations)
            {
                if (!double.IsFinite(item.Value) || item.TimestampUtc.Offset != TimeSpan.Zero
                    || item.Latitude is { } latitude && item.Longitude is { } longitude && !MissionPlanning.Position(latitude, longitude))
                    throw new ArgumentException("Scientific observations require finite values, UTC timestamps and valid available positions.");
                cmd.Parameters.Clear(); cmd.CommandText = "SELECT COUNT(*) FROM datasets WHERE id=$dataset AND mission_id=$mission AND instrument_id=$instrument";
                cmd.Parameters.AddWithValue("$dataset", item.DatasetId.ToString()); cmd.Parameters.AddWithValue("$mission", item.MissionId.ToString());
                cmd.Parameters.AddWithValue("$instrument", item.InstrumentId);
                if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Observation/dataset provenance mismatch.");
                cmd.CommandText = "INSERT OR IGNORE INTO observations(dataset_id,mission_id,timestamp_utc,measurement_id,value,unit,quality,payload_json) VALUES($dataset,$mission,$time,$measurement,$value,$unit,$quality,$json)";
                cmd.Parameters.AddWithValue("$time", item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$measurement", item.MeasurementId);
                cmd.Parameters.AddWithValue("$value", item.Value); cmd.Parameters.AddWithValue("$unit", item.Unit); cmd.Parameters.AddWithValue("$quality", item.Quality.ToString());
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, WireJson.Options)); inserted += cmd.ExecuteNonQuery();
            }
            tx.Commit(); return inserted;
        }
    }
    public IReadOnlyList<ScienceDataset> Datasets(Guid? missionId = null, int limit = 100, int offset = 0)
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT d.payload_json,COUNT(o.id) FROM datasets d LEFT JOIN observations o ON o.dataset_id=d.id "
            + (missionId is null ? "" : "WHERE d.mission_id=$mission ") + "GROUP BY d.id ORDER BY d.rowid DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$mission", missionId?.ToString() ?? ""); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100)); cmd.Parameters.AddWithValue("$offset", Math.Max(offset, 0));
        using var reader = cmd.ExecuteReader(); var rows = new List<ScienceDataset>();
        while (reader.Read()) rows.Add(JsonSerializer.Deserialize<ScienceDataset>(reader.GetString(0), WireJson.Options)! with { ObservationCount = reader.GetInt64(1) }); return rows;
    }
    public bool ContainsDataset(Guid id)
    {
        if (AvailabilityError() is not null || !File.Exists(DatabasePath)) return false;
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM datasets WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString()); return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }
    public IEnumerable<ScienceObservation> StreamObservations(Guid? missionId = null, Guid? datasetId = null, long after = 0, int? limit = null)
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!File.Exists(DatabasePath)) yield break;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,payload_json FROM observations WHERE id>$after"
            + (missionId is null ? "" : " AND mission_id=$mission") + (datasetId is null ? "" : " AND dataset_id=$dataset")
            + " ORDER BY id" + (limit is null ? "" : " LIMIT $limit");
        cmd.Parameters.AddWithValue("$after", Math.Max(after, 0)); cmd.Parameters.AddWithValue("$mission", missionId?.ToString() ?? ""); cmd.Parameters.AddWithValue("$dataset", datasetId?.ToString() ?? "");
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit.Value, 1, 5000));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<ScienceObservation>(reader.GetString(1), WireJson.Options)! with { Id = reader.GetInt64(0) };
    }
    public IReadOnlyList<MeasurementSummary> Summary(Guid missionId)
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!File.Exists(DatabasePath)) return [];
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT measurement_id,unit,COUNT(*),MIN(value),MAX(value),AVG(value),MIN(timestamp_utc),MAX(timestamp_utc) FROM observations WHERE mission_id=$id GROUP BY measurement_id,unit";
        cmd.Parameters.AddWithValue("$id", missionId.ToString()); using var r = cmd.ExecuteReader(); var rows = new List<MeasurementSummary>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5),
            DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture), DateTimeOffset.Parse(r.GetString(7), CultureInfo.InvariantCulture))); return rows;
    }
    public DataClearPreview PreviewClear()
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        long datasets = 0, observations = 0;
        if (File.Exists(DatabasePath))
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT (SELECT COUNT(*) FROM datasets),(SELECT COUNT(*) FROM observations)";
            using var r = cmd.ExecuteReader(); r.Read(); datasets = r.GetInt64(0); observations = r.GetInt64(1);
        }
        return new("science", DirectoryPath, 0, 0, datasets, observations, StorageManager.DatabaseBytes(DatabasePath),
            "AeroScience atmospheric observations and dataset metadata in Observations/science.sqlite. Unregistered files are not removed.",
            "Flight recordings, telemetry, plans, settings, library identity and folders; unrelated files", "DELETE SCIENCE DATA");
    }
    public void Clear(string confirmation, bool recording)
    {
        if (confirmation != "DELETE SCIENCE DATA" || recording) throw new InvalidOperationException("End recording and type DELETE SCIENCE DATA to confirm.");
        lock (gate)
        {
            if (AvailabilityError() is { } error) throw new IOException(error);
            if (!File.Exists(DatabasePath)) return;
            Initialize(); using var db = Open(write: true); using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM observations; DELETE FROM datasets; INSERT OR REPLACE INTO metadata VALUES('last_clear_utc',$now)";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); cmd.ExecuteNonQuery(); tx.Commit();
        }
    }
    public ScienceStorageEstimate Estimate(InstrumentConfiguration[]? input, double? minutes)
    {
        var configurations = ScienceCatalog.Normalize(input); var counts = new Dictionary<string, long>();
        long? free = null;
        try { if (AvailabilityError() is null) free = disk.FreeBytes(DirectoryPath); } catch (IOException) { }
        if (minutes is null || !double.IsFinite(minutes.Value) || minutes <= 0 || minutes > 43200)
            return new(null, free, false, "Science products only; enter an estimated duration of up to 30 days.", ["No flight-telemetry, future imagery or unknown instrument data rates included."], counts);
        // Estimate the actual serialized observation model plus duplicated SQL keys/value columns,
        // two indexes and a 2x allocation/variable-length margin. Include SQLite schema/dataset pages.
        var exemplar = new ScienceObservation(0, Guid.Empty, Guid.Empty, "ambient-temperature", DateTimeOffset.UnixEpoch,
            "temperature", -273.15, "degC", ScienceProvenance.SimulatorReported, "MSFS 2024 / SimConnect AMBIENT TEMPERATURE",
            -89.999999, -179.999999, 100000, ObservationQuality.AcquisitionGap, DateTimeOffset.UnixEpoch);
        var perRow = checked((JsonSerializer.SerializeToUtf8Bytes(exemplar, WireJson.Options).Length + 256L) * 2);
        long bytes = 4096 * 8;
        foreach (var config in configurations.Where(c => c.Enabled))
        {
            var definition = ScienceCatalog.Definitions.Single(d => d.Id == config.InstrumentId);
            var count = checked((long)Math.Ceiling(minutes.Value * 60000 / config.IntervalMs) * definition.Measurements.Length);
            counts[config.InstrumentId] = count; bytes = checked(bytes + count * perRow + 4096 * 2);
        }
        if (counts.Count == 0) bytes = 0;
        return new(bytes, free, free is { } available && bytes + StorageManager.MinimumFreeBytes > available,
            $"Science only: {perRow} estimated bytes/observation (serialized row, SQL keys/indexes and 2x margin), plus schema/dataset pages.",
            ["Estimate, not a reservation; actual SQLite allocation and metadata sizes vary.", "Source is at most 1 Hz; no intermediate observations or future remote-sensing rates are assumed."], counts);
    }
    public void Dispose() { lock (gate) { lease?.Dispose(); lease = null; } }
}
