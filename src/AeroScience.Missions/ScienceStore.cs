using System.Globalization;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

// The science database belongs to the independently movable library, not missions.db.
public sealed partial class ScienceStore(string directory, IStorageCapacity? capacity = null) : IDisposable
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
    public long DatabaseUsageBytes()
    {
        if(AvailabilityError() is {} error)throw new IOException(error);
        try{return StorageManager.DatabaseBytes(DatabasePath);}
        catch(FileNotFoundException)when(!File.Exists(Path.Combine(DirectoryPath,MarkerName))){return 0;} // A verified library that has never initialized its science DB.
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
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL"; cmd.ExecuteNonQuery(); if(write){cmd.CommandText="PRAGMA secure_delete=ON";cmd.ExecuteNonQuery();}return db;
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
            cmd.CommandText="PRAGMA journal_mode=WAL";cmd.ExecuteNonQuery();
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
            var schema=Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (schema is not(1 or 2 or 3 or 4)) throw new IOException("Unsupported science schema; preserve this library.");
            if(schema<CurrentSchemaVersion&&existed){var backups=Path.Combine(DirectoryPath,"Metadata","Backups");Directory.CreateDirectory(backups);_=DataBackup.Create(DatabasePath,backups,true);}
            InitializeRemoteTables(db);
            InitializeArtifactTable(db);
            InitializeSpatialIndex(db);
            InitializeAtmosphereIndex(db);
            InitializeStatisticsCache(db);
            if(schema<3)InitializeLibraryJobs(db);
            InitializeSourceIndex(db,schema<4);
            if (!File.Exists(Path.Combine(DirectoryPath, MarkerName)))
                PreferenceStore.WriteAtomic(Path.Combine(DirectoryPath, MarkerName), new { formatVersion = 1, libraryId = id, database = RelativeDatabasePath });
            if(!sourceDeletionRecoveryDone){RecoverSourceDeletionJournals();sourceDeletionRecoveryDone=true;}
        }
    }
    public ScienceDataset EnsureDataset(Mission mission, InstrumentConfiguration configuration, MissionResearch research)
    {
        if(File.Exists(DatabasePath))
        {
            using var existingDb=Open();using var query=existingDb.CreateCommand();
            query.CommandText="SELECT payload_json FROM datasets WHERE mission_id=$mission AND instrument_id=$instrument";
            query.Parameters.AddWithValue("$mission",mission.Id.ToString());query.Parameters.AddWithValue("$instrument",configuration.InstrumentId);
            if(query.ExecuteScalar() is string saved)return ReadCatalog<ScienceDataset>(saved);
        }
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
                disclaimer = RemoteAcquisition.Instruments.ContainsKey(definition.Id) ? "Historical external geographic datasets sampled along a simulator-derived approximate footprint. Source dates, CRS, vertical datum, masks and raw values are retained with each product; not a live airborne sensor." : "Simulator-reported atmospheric values; not independent calibrated real-world measurements. No modeled instrument output is substituted."
            }, WireJson.Options));
        using var db = Open(write: true); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO datasets VALUES($id,$mission,$instrument,$json)";
        cmd.Parameters.AddWithValue("$id", dataset.Id.ToString()); cmd.Parameters.AddWithValue("$mission", mission.Id.ToString());
        cmd.Parameters.AddWithValue("$instrument", configuration.InstrumentId); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(dataset, WireJson.Options));
        if(cmd.ExecuteNonQuery()>0)Management?.AddedDataset();
        cmd.CommandText = "SELECT payload_json FROM datasets WHERE mission_id=$mission AND instrument_id=$instrument";
        return JsonSerializer.Deserialize<ScienceDataset>((string)cmd.ExecuteScalar()!, WireJson.Options)!;
    }
    public int Append(IReadOnlyList<ScienceObservation> observations)
    {
        if (observations.Count == 0) return 0;
        lock (gate)
        {
            Management?.RequireScienceCapacity(65536);
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
    public IEnumerable<ScienceObservation> StreamObservations(Guid? missionId = null, Guid? datasetId = null, long after = 0, int? limit = null,string? instrumentId=null)
    {
        if (AvailabilityError() is { } error) throw new IOException(error);
        if (!File.Exists(DatabasePath)) yield break;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,payload_json FROM observations WHERE id>$after"
            + (missionId is null ? "" : " AND mission_id=$mission") + (datasetId is null ? "" : " AND dataset_id=$dataset")
            + (instrumentId is null?"":" AND dataset_id IN (SELECT id FROM datasets WHERE instrument_id=$instrument)")
            + " ORDER BY id" + (limit is null ? "" : " LIMIT $limit");
        cmd.Parameters.AddWithValue("$instrument",instrumentId??"");cmd.Parameters.AddWithValue("$after", Math.Max(after, 0)); cmd.Parameters.AddWithValue("$mission", missionId?.ToString() ?? ""); cmd.Parameters.AddWithValue("$dataset", datasetId?.ToString() ?? "");
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
        var managed = ManagedProductSummary();
        var caches=CacheEntries();
        return new("science", DirectoryPath, 0, 0, datasets, observations, StorageManager.DatabaseBytes(DatabasePath) + managed.Bytes + caches.Sum(c=>c.Bytes),
            "AeroScience atmospheric observations, dataset metadata and indexed generated remote-sensing product files. Only unchanged, checksum-matching managed files are removed.",
            "Original imported Sources, flight recordings, telemetry, plans, settings, library identity and unrelated files. Targeted scientific content is also purged from managed backups; unresolved shared copies block completion", "", managed.Count, managed.Bytes, managed.Paths, ProtectedDatasetCount(),caches.LongLength);
    }
    public CollectedScienceDeletionResult Clear(SourceDeletionExecution reviewed,MissionStore missions,Func<IDisposable> maintenance)=>DeleteCollectedScience(reviewed,missions,maintenance);
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
            if (RemoteAcquisition.Instruments.TryGetValue(config.InstrumentId, out var kind))
            {
                var bands = Sources().Where(s => s.Manifest.Kind == kind).Select(s => s.Manifest.Bands.Length).DefaultIfEmpty(kind == RemoteKind.Hyperspectral ? 512 : kind == RemoteKind.Multispectral ? 5 : kind == RemoteKind.Thermal ? 2 : 1).Max();
                var estimate = RemoteEstimate(config, minutes.Value, bands);
                counts[config.InstrumentId] = estimate.Acquisitions; bytes = checked(bytes + estimate.ConservativeOutputBytes + estimate.TemporaryBytes); continue;
            }
            var definition = ScienceCatalog.Definitions.Single(d => d.Id == config.InstrumentId);
            var count = checked((long)Math.Ceiling(minutes.Value * 60000 / config.IntervalMs) * definition.Measurements.Length);
            counts[config.InstrumentId] = count; bytes = checked(bytes + count * perRow + 4096 * 2);
            if (config.Calibration is {} calibration)
            {
                var metadata = JsonSerializer.SerializeToUtf8Bytes(calibration, WireJson.Options).Length;
                bytes = checked(bytes + metadata * 2L);
                if (calibration.Adjustment is not null)
                {
                    var derivedCount = checked((long)Math.Ceiling(minutes.Value * 60000 / config.IntervalMs));
                    counts[config.InstrumentId] += derivedCount;
                    bytes = checked(bytes + derivedCount * (perRow + (metadata + 512L) * 2));
                }
            }
        }
        if (counts.Count == 0) bytes = 0;
        return new(bytes, free, free is { } available && bytes + StorageManager.MinimumFreeBytes > available,
            $"Science only: {perRow} estimated bytes/atmospheric observation, plus 24×24 remote samples × source bands × 32 bytes/value, 256 KiB product metadata/acquisition and 32 MiB temporary reserve per remote instrument. No compression savings assumed.",
            ["Estimate, not a reservation; allocation/metadata vary. Counts include configured optional derived scalar channels and their retained calibration metadata; invalid/missing inputs may produce fewer derived rows. Counts for remote instruments are acquisitions, not pixels.", "Original source imports are additional; their exact bytes/free space are checked before copying. Dense footprint sampling does not improve source resolution."], counts);
    }
    public void Dispose() { lock (gate) { lease?.Dispose(); lease = null; } }
}
