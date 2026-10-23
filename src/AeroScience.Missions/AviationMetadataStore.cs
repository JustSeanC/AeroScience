using System.Text.Json;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

// Opaque process-local proof issued only after opening the same identified file.
// Never serialized/persisted or accepted from an HTTP request.
public sealed class AviationIntegrityStamp
{
    internal AviationIntegrityStamp(string directory, Guid id, DateTime createdUtc)
    { Directory = directory; Id = id; CreatedUtc = createdUtc; }
    internal string Directory { get; }
    internal Guid Id { get; }
    internal DateTime CreatedUtc { get; }
    internal bool Matches(string directory, Guid id, DateTime createdUtc) => Id == id && CreatedUtc == createdUtc &&
        string.Equals(Directory, directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

// Metadata only. No scientific source IDs, measurements, transfer allowances or original-file deletion.
// Creation is explicit. Every later read/write requires the same retained identity and database.
public sealed partial class AviationMetadataStore : IDisposable
{
    public const int SchemaVersion = 3;
    private sealed record Identity(int Version, Guid Id);
    private readonly string directory, database;
    private readonly FileStream lease;
    private readonly object gate = new();
    private readonly Identity identity;
    private bool disposed;
    public Guid StoreId => identity.Id;
    public AviationIntegrityStamp IntegrityStamp { get; }
    public bool FullIntegrityCheckPerformed { get; }
    public AviationMetadataStore(string directory, bool create = false, AviationIntegrityStamp? integrity = null)
    {
        StorageManager.ValidateLocalPath(directory);
        this.directory = Path.GetFullPath(directory);
        database = Path.Combine(this.directory, "aviation.db");
        var manifest = Path.Combine(this.directory, "aviation-store.json");
        if (!Directory.Exists(this.directory) && !create) throw new IOException("Aviation storage is unavailable. Reconnect the configured drive; no replacement store was created.");
        if (create) Directory.CreateDirectory(this.directory);
        if (create && !File.Exists(manifest) && !File.Exists(database) &&
            Directory.EnumerateFileSystemEntries(this.directory).Any(p => Path.GetFileName(p) != "engine.lock"))
            throw new IOException("Create aviation storage in its own empty directory; existing data was preserved.");
        lease = StorageManager.Acquire(this.directory);
        try
        {
            if (!File.Exists(manifest) && !File.Exists(database) && create)
            {
                identity = new(1, Guid.NewGuid());
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    BEGIN IMMEDIATE;
                    CREATE TABLE identity(id TEXT NOT NULL);
                    CREATE TABLE snapshots(kind TEXT PRIMARY KEY,success_utc TEXT NOT NULL,payload TEXT NOT NULL);
                    CREATE TABLE attempts(kind TEXT PRIMARY KEY,attempt_utc TEXT NOT NULL,error_code TEXT);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery(); command.CommandText = "INSERT INTO identity VALUES($id)";
                command.Parameters.AddWithValue("$id", identity.Id.ToString()); command.ExecuteNonQuery();
                command.CommandText = "COMMIT"; command.ExecuteNonQuery();
                PreferenceStore.WriteAtomic(manifest, identity);
            }
            else
            {
                if (!File.Exists(manifest) || !File.Exists(database)) throw new IOException("Aviation store is incomplete or unavailable. Preserve existing files for recovery; no replacement was created.");
                if (new FileInfo(manifest).Length > 4096) throw new IOException("Invalid aviation identity metadata.");
                identity = JsonSerializer.Deserialize<Identity>(File.ReadAllText(manifest)) ?? throw new IOException("Missing aviation identity.");
            }
            if (identity.Version != 1 || identity.Id == Guid.Empty) throw new NotSupportedException("Unsupported aviation identity. Preserve this store.");
            MigrateJobs();
            using var verified = Open();
            var createdUtc = File.GetCreationTimeUtc(database);
            FullIntegrityCheckPerformed = integrity?.Matches(this.directory, identity.Id, createdUtc) != true;
            if (FullIntegrityCheckPerformed)
            {
                using var check = verified.CreateCommand(); check.CommandText = "PRAGMA quick_check";
                if (check.ExecuteScalar() as string != "ok") throw new IOException("Aviation database integrity failed. Preserve it for recovery.");
            }
            IntegrityStamp = new(this.directory, identity.Id, createdUtc);
        }
        catch { lease.Dispose(); throw; }
    }
    private SqliteConnection Open(bool write = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Do not recreate a missing selected directory, identity or external database.
        StorageManager.ValidateLocalPath(directory);
        var manifest = Path.Combine(directory, "aviation-store.json");
        foreach (var file in new[] { database, manifest, database + "-wal", database + "-shm" })
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Aviation storage cannot follow file links or reparse points.");
        if (!File.Exists(database) || !File.Exists(manifest) || new FileInfo(manifest).Length > 4096 ||
            JsonSerializer.Deserialize<Identity>(File.ReadAllText(manifest)) != identity)
            throw new IOException("The configured aviation store is missing or changed. No replacement was opened.");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(command.ExecuteScalar()) != SchemaVersion) throw new NotSupportedException("Unsupported aviation database schema. Preserve the original database.");
            command.CommandText = "SELECT id FROM identity";
            if (command.ExecuteScalar() as string != identity.Id.ToString()) throw new IOException("Aviation database identity does not match its configured store.");
            if (write) { command.CommandText = "PRAGMA synchronous=FULL"; command.ExecuteNonQuery(); }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void Kind(string kind)
    {
        if (kind is not ("vfr" or "ifr" or "tfr" or "nasr")) throw new ArgumentException("Select a supported aviation metadata category.");
    }
    private void Write<T>(string kind, T snapshot, DateTimeOffset utc)
    {
        var payload = JsonSerializer.Serialize(snapshot, WireJson.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 8 * 1024 * 1024) throw new InvalidDataException("Aviation snapshot exceeds 8 MiB.");
        lock (gate)
        {
            using var db = Open(true); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
            // Never let an older concurrent fetch replace newer successful metadata.
            command.CommandText = "SELECT success_utc FROM snapshots WHERE kind=$kind"; command.Parameters.AddWithValue("$kind", kind);
            if (command.ExecuteScalar() is string previous && DateTimeOffset.Parse(previous, System.Globalization.CultureInfo.InvariantCulture) > utc)
                throw new InvalidOperationException("A newer aviation snapshot already exists; stale results were discarded.");
            command.CommandText = "INSERT INTO snapshots VALUES($kind,$utc,$payload) ON CONFLICT(kind) DO UPDATE SET success_utc=$utc,payload=$payload";
            command.Parameters.AddWithValue("$utc", utc.ToString("O")); command.Parameters.AddWithValue("$payload", payload); command.ExecuteNonQuery(); tx.Commit();
        }
    }
    public void Save(string kind, AviationCatalogSnapshot snapshot)
    {
        Kind(kind); if (kind == "tfr") throw new ArgumentException("Use the notice snapshot contract."); AviationCatalog.Validate(snapshot);
        if (snapshot.Items.Any(i => (i.Product is AviationProduct.IfrLow or AviationProduct.IfrHigh) != (kind == "ifr") || (i.Product == AviationProduct.Nasr) != (kind == "nasr")))
            throw new InvalidDataException("Catalog records belong to a different FAA product category.");
        Write(kind, snapshot, snapshot.RetrievedUtc);
    }
    public void Save(AviationTfrSnapshot snapshot) { AviationCatalog.Validate(snapshot); Write("tfr", snapshot, snapshot.RetrievedUtc); }
    private T? Read<T>(string kind) where T : class
    {
        Kind(kind); lock (gate)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT CASE WHEN length(CAST(payload AS BLOB))<=8388608 THEN payload ELSE NULL END FROM snapshots WHERE kind=$kind"; command.Parameters.AddWithValue("$kind", kind);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, WireJson.Options) ?? throw new IOException("Invalid aviation metadata.") : null;
        }
    }
    public AviationCatalogSnapshot? Catalog(string kind)
    {
        if (kind == "tfr") throw new ArgumentException("Use the notice contract.");
        var result = Read<AviationCatalogSnapshot>(kind); if (result is not null) AviationCatalog.Validate(result); return result;
    }
    public AviationTfrSnapshot? Notices() { var result = Read<AviationTfrSnapshot>("tfr"); if (result is not null) AviationCatalog.Validate(result); return result; }
    public void Attempt(string kind, DateTimeOffset utc, string? errorCode)
    {
        Kind(kind);
        if (utc.Offset != TimeSpan.Zero || errorCode is not (null or "Offline" or "InvalidMetadata" or "NetworkUnavailable" or "RateLimited" or "NotAuthorized" or "Timeout" or "ProviderUnavailable"))
            throw new ArgumentException("Use UTC and a redacted supported failure code, not raw URLs or credentials.");
        lock (gate)
        {
            using var db = Open(true); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO attempts VALUES($kind,$utc,$error) ON CONFLICT(kind) DO UPDATE SET attempt_utc=$utc,error_code=$error WHERE $utc>=attempt_utc";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$utc", utc.ToString("O")); command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value); command.ExecuteNonQuery();
        }
    }
    public AviationCacheStatus Status(string kind)
    {
        Kind(kind); lock (gate)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT (SELECT success_utc FROM snapshots WHERE kind=$kind),(SELECT attempt_utc FROM attempts WHERE kind=$kind),(SELECT error_code FROM attempts WHERE kind=$kind)";
            command.Parameters.AddWithValue("$kind", kind); using var row = command.ExecuteReader(); row.Read();
            DateTimeOffset? Utc(int i) => row.IsDBNull(i) ? null : DateTimeOffset.Parse(row.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
            return new(true, StoreId, Utc(0), Utc(1), row.IsDBNull(2) ? null : row.GetString(2), StorageManager.DatabaseBytes(database));
        }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lease.Dispose(); } }
}
