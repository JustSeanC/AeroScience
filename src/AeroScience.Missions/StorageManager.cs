using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public enum StorageKind { Missions, ScienceLibrary }
public sealed record AppPreferences(string MissionDirectory, string ScienceLibraryDirectory,
    bool Initialized = false, bool EnableLan = false, string? LanAddress = null, int SamplingIntervalMs = 1000,
    string? CommunityDirectory = null, bool SetupCompleted = false);
public sealed record StorageStatus(string Directory, bool Available, long? UsedBytes, long? FreeBytes, string? Error);
public sealed record MigrationJournal(StorageKind Kind, string Source, string Destination, string Phase,
    string? Fingerprint = null);
public interface IStorageCapacity { long FreeBytes(string directory); }
public sealed class LocalStorageCapacity : IStorageCapacity
{
    public long FreeBytes(string directory)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
        if (!drive.IsReady) throw new IOException("The selected drive is unavailable.");
        if (drive.DriveType == DriveType.Network) throw new IOException("Network storage is not supported in this release.");
        return drive.AvailableFreeSpace;
    }
}

// Settings stay in a stable local folder independently of either movable store.
public sealed class PreferenceStore(string configurationDirectory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(configurationDirectory);
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string JournalPath => Path.Combine(DirectoryPath, "storage-migration.json");
    public string DefaultMissionDirectory => DirectoryPath;
    public string DefaultLibraryDirectory => Path.Combine(DirectoryPath, "ScienceLibrary");
    public AppPreferences Load() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(SettingsPath))
            ?? throw new IOException("Application settings are invalid. Restore a settings backup; no archive was replaced.")
        : new(DefaultMissionDirectory, DefaultLibraryDirectory);
    public void Save(AppPreferences settings) => WriteAtomic(SettingsPath, settings);
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class StorageManager(PreferenceStore preferences, IStorageCapacity? capacity = null)
{
    public const long MinimumFreeBytes = 64L * 1024 * 1024;
    public const long LibraryLowSpaceWarningBytes = 1024L * 1024 * 1024;
    public const string LargeDataWarning = "Scientific mapping and remote-sensing missions may generate substantial amounts of data. Storage requirements will vary by instrument, recording settings, and mission duration. Consider using a dedicated high-capacity drive.";
    private readonly IStorageCapacity disk = capacity ?? new LocalStorageCapacity();
    private readonly object gate = new();
    private static readonly string[] LibraryFolders = ["Imagery", "Elevation", "Observations", "Reports", "Exports"];
    public const string LibraryManifest = "library.json";

    public AppPreferences Initialize()
    {
        lock (gate)
        {
            var settings = preferences.Load();
            if (settings.Initialized) { RequireMissionDatabase(settings.MissionDirectory); return settings; }
            ValidateLocalPath(settings.MissionDirectory);
            Directory.CreateDirectory(settings.MissionDirectory);
            using var lease = Acquire(settings.MissionDirectory);
            var store = new MissionStore(Path.Combine(settings.MissionDirectory, "missions.db"));
            store.Initialize();
            ValidateLocalPath(settings.ScienceLibraryDirectory);
            Directory.CreateDirectory(settings.ScienceLibraryDirectory);
            if (!File.Exists(Path.Combine(settings.ScienceLibraryDirectory, LibraryManifest)))
                PreferenceStore.WriteAtomic(Path.Combine(settings.ScienceLibraryDirectory, LibraryManifest), new { libraryId = Guid.NewGuid(), formatVersion = 1 });
            foreach (var folder in LibraryFolders) Directory.CreateDirectory(Path.Combine(settings.ScienceLibraryDirectory, folder));
            settings = settings with { Initialized = true }; preferences.Save(settings); return settings;
        }
    }

    public StorageStatus Status(StorageKind kind)
    {
        var path = Selected(preferences.Load(), kind);
        try
        {
            if (kind == StorageKind.Missions) RequireMissionDatabase(path); else _ = LibraryId(path);
            return new(path, true, Usage(path, kind), disk.FreeBytes(path), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new(path, false, null, null, e.Message); }
    }

    public void Migrate(StorageKind kind, string destination, bool confirmed, bool recording,
        IProgress<string>? progress = null)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm migration before copying or switching storage.");
        if (recording) throw new InvalidOperationException("End recording before changing either storage location.");
        lock (gate)
        {
            if (File.Exists(preferences.JournalPath))
                throw new InvalidOperationException("A previous migration needs recovery/review before another migration.");
            var settings = preferences.Load();
            var source = Selected(settings, kind); destination = Path.GetFullPath(destination);
            ValidateLocalPath(source); ValidateLocalPath(destination);
            if (SamePath(source, destination)) return;
            if (kind == StorageKind.ScienceLibrary && (IsWithin(source, destination) || IsWithin(destination, source)))
                throw new IOException("Choose a separate folder outside the current library.");
            RequireMissionDatabase(settings.MissionDirectory);
            using var sourceLease = Acquire(source);
            using var missionLease = kind == StorageKind.ScienceLibrary ? Acquire(settings.MissionDirectory) : null;
            // A stopped engine must have finalized/recovered its active mission before migration.
            using (var db = OpenDatabase(Path.Combine(settings.MissionDirectory, "missions.db"), true))
            using (var cmd = db.CreateCommand())
            { cmd.CommandText = "SELECT COUNT(*) FROM missions WHERE status='Recording'"; if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidOperationException("A mission is still recording; migration is blocked."); }
            if (kind == StorageKind.Missions) RequireMissionDatabase(source); else _ = LibraryId(source);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("The destination is not empty. Choose an empty folder, or explicitly open its existing archive/library; nothing was merged or overwritten.");
            var required = checked(Usage(source, kind) + MinimumFreeBytes);
            if (disk.FreeBytes(destination) < required) throw new IOException("Insufficient free space for a verified copy and the storage safety reserve.");
            Directory.CreateDirectory(destination);
            using var destinationLease = Acquire(destination);
            var probe = Path.Combine(destination, ".write-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { } File.Delete(probe);
            var journal = new MigrationJournal(kind, source, destination, "copying");
            PreferenceStore.WriteAtomic(preferences.JournalPath, journal);
            progress?.Report("Copying; the original remains selected and intact.");
            string fingerprint;
            if (kind == StorageKind.Missions)
            {
                var sourceDb = Path.Combine(source, "missions.db");
                var temporary = Path.Combine(destination, "missions.db.partial");
                using (var from = OpenDatabase(sourceDb, true))
                using (var to = OpenDatabase(temporary, false)) from.BackupDatabase(to);
                using (var durable = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
                fingerprint = DatabaseFingerprint(sourceDb);
                if (DatabaseFingerprint(temporary) != fingerprint) throw new IOException("Database copy verification failed; the original remains selected.");
                File.Move(temporary, Path.Combine(destination, "missions.db"));
            }
            else
            {
                fingerprint = LibraryFingerprint(source);
                foreach (var folder in EnumerateDirectories(source)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, folder)));
                foreach (var path in EnumerateFiles(source))
                {
                    var target = Path.Combine(destination, Path.GetRelativePath(source, path));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var from = File.OpenRead(path);
                    using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    from.CopyTo(to); to.Flush(true);
                }
                if (LibraryFingerprint(source) != fingerprint || LibraryFingerprint(destination) != fingerprint)
                    throw new IOException("Library verification failed; the original remains selected.");
            }
            journal = journal with { Phase = "verified", Fingerprint = fingerprint };
            PreferenceStore.WriteAtomic(preferences.JournalPath, journal);
            progress?.Report("Verified. Switching settings; original data will be retained.");
            preferences.Save(Replace(settings, kind, destination));
            // A crash before this deletion is resolved by RecoverInterrupted, using the verified fingerprint.
            File.Move(preferences.JournalPath, preferences.JournalPath + ".complete-" + Guid.NewGuid().ToString("N"));
            progress?.Report("Complete. The original copy has been preserved.");
        }
    }

    // Choosing an existing store is an explicit switch, never an automatic merge.
    public void UseExisting(StorageKind kind, string path, bool confirmed, bool recording)
    {
        if (!confirmed || recording) throw new InvalidOperationException("Stop recording and explicitly confirm opening the existing store.");
        lock (gate)
        {
            if (File.Exists(preferences.JournalPath)) throw new InvalidOperationException("Review the interrupted migration first.");
            ValidateLocalPath(path);
            using var lease = Acquire(path);
            if (kind == StorageKind.Missions) { RequireMissionDatabase(path); _ = DatabaseFingerprint(Path.Combine(path, "missions.db")); }
            else _ = LibraryFingerprint(path);
            preferences.Save(Replace(preferences.Load(), kind, Path.GetFullPath(path)));
        }
    }

    public string? RecoverInterrupted()
    {
        lock (gate)
        {
            if (!File.Exists(preferences.JournalPath)) return null;
            var journal = JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(preferences.JournalPath))
                ?? throw new IOException("Invalid migration journal; preserve it and use the original store.");
            var settings = preferences.Load();
            if (SamePath(Selected(settings, journal.Kind), journal.Destination))
            {
                var actual = journal.Kind == StorageKind.Missions
                    ? DatabaseFingerprint(Path.Combine(journal.Destination, "missions.db")) : LibraryFingerprint(journal.Destination);
                if (journal.Phase != "verified" || actual != journal.Fingerprint)
                    throw new IOException("Migration verification needs recovery. Original data is retained at " + journal.Source);
            }
            else if (!SamePath(Selected(settings, journal.Kind), journal.Source))
                throw new IOException("Migration settings conflict; no data or settings were replaced.");
            // Archive the journal for review. Never delete partial/destination/source data.
            File.Move(preferences.JournalPath, preferences.JournalPath + ".review-" + Guid.NewGuid().ToString("N"));
            return "An interrupted migration was recovered. The selected store is unchanged; review the retained original and destination copies. No data was deleted.";
        }
    }

    public void RollbackInterrupted(bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("Confirm restoring the original storage setting.");
        lock (gate)
        {
            var journal = JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllText(preferences.JournalPath))
                ?? throw new IOException("Migration journal is invalid.");
            using var sourceLease = Acquire(journal.Source);
            if (journal.Kind == StorageKind.Missions) _ = DatabaseFingerprint(Path.Combine(journal.Source, "missions.db"));
            else _ = LibraryFingerprint(journal.Source);
            preferences.Save(Replace(preferences.Load(), journal.Kind, journal.Source));
            File.Move(preferences.JournalPath, preferences.JournalPath + ".rollback-" + Guid.NewGuid().ToString("N"));
        }
    }

    public static Guid LibraryId(string directory)
    {
        ValidateLocalPath(directory);
        var path = Path.Combine(directory, LibraryManifest);
        if (!File.Exists(path)) throw new IOException("The selected Scientific Data Library is unavailable. Reconnect its drive; no empty replacement was created.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("libraryId", out var identity)
            || identity.ValueKind != JsonValueKind.String || !identity.TryGetGuid(out var id))
            throw new IOException("Science Library identity is invalid. Select a verified library; existing files were preserved.");
        return id;
    }
    public static string ResolveDataset(string directory, Guid libraryId, string relativePath)
    {
        if (LibraryId(directory) != libraryId) throw new IOException("Dataset belongs to a different science library.");
        if (Path.IsPathRooted(relativePath) || relativePath.Split('/', '\\').Any(p => p is ".." or ".") || relativePath.Contains(':'))
            throw new ArgumentException("Dataset references must be portable relative paths.");
        var path = Path.GetFullPath(Path.Combine(directory, relativePath));
        if (!IsWithin(directory, path)) throw new ArgumentException("Dataset path is outside the library.");
        ValidateLocalPath(path);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Dataset references cannot follow symbolic links.");
        return path;
    }
    public static void RequireMissionDatabase(string path)
    {
        ValidateLocalPath(path);
        var database = Path.Combine(path, "missions.db");
        if (!File.Exists(database)) throw new IOException("The selected mission database is unavailable. Reconnect its drive or select an existing archive; no empty replacement was created.");
        if ((File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0) throw new IOException("Mission databases cannot be symbolic links.");
    }
    public static FileStream Acquire(string directory)
    {
        if (!Directory.Exists(directory)) throw new IOException("Storage directory is unavailable.");
        return new(Path.Combine(directory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public static void ValidateLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("Choose an absolute local/internal or external-drive folder. Network storage is not supported.");
        if (OperatingSystem.IsWindows())
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (!drive.IsReady) throw new IOException("The selected drive is unavailable.");
            if (drive.DriveType == DriveType.Network) throw new IOException("Network storage is not supported in this release.");
        }
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Storage paths cannot contain symbolic links or junctions.");
    }
    public static long Usage(string directory, StorageKind kind) => kind == StorageKind.Missions
        ? new[] { "missions.db", "missions.db-wal", "missions.db-shm" }.Select(n => Path.Combine(directory, n)).Where(File.Exists).Sum(p => new FileInfo(p).Length)
        : EnumerateFiles(directory).Sum(p => new FileInfo(p).Length);
    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        // Enumerate without following reparse points, including links to files.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Library contains a link/junction; migration stopped.");
            if (Directory.Exists(entry)) { foreach (var file in EnumerateFiles(entry)) yield return file; }
            else if (Path.GetFileName(entry) != "engine.lock") yield return entry;
        }
    }
    public static string LibraryFingerprint(string directory)
    {
        _ = LibraryId(directory);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var folder in EnumerateDirectories(directory)) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes("directory:" + Path.GetRelativePath(directory, folder).Replace('\\', '/')));
        foreach (var file in EnumerateFiles(directory))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file).Replace('\\', '/')));
            using var stream = File.OpenRead(file); hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Library contains a link/junction.");
            if (!Directory.Exists(entry)) continue;
            yield return entry;
            foreach (var folder in EnumerateDirectories(entry)) yield return folder;
        }
    }
    public static string DatabaseFingerprint(string path, bool science = false)
    {
        if (!File.Exists(path)) throw new IOException("Database copy is missing.");
        using var db = OpenDatabase(path, true);
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        if (command.ExecuteScalar() as string != "ok") throw new IOException("Database integrity check failed.");
        command.CommandText = "PRAGMA foreign_key_check";
        using (var check = command.ExecuteReader()) if (check.Read()) throw new IOException("Database references failed integrity validation.");
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<(string, string)>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));
        if (!tables.Any(t => t.Item1 == (science ? "datasets" : "missions")) || !tables.Any(t => t.Item1 == (science ? "observations" : "telemetry"))) throw new IOException("Not an AeroScience database of the selected kind.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, schema) in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(schema));
            command.CommandText = "SELECT * FROM \"" + name.Replace("\"", "\"\"") + "\" ORDER BY rowid";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static SqliteConnection OpenDatabase(string path, bool readOnly)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString()); db.Open(); return db;
    }
    private static AppPreferences Replace(AppPreferences p, StorageKind kind, string path) => kind == StorageKind.Missions
        ? p with { MissionDirectory = path } : p with { ScienceLibraryDirectory = path };
    private static string Selected(AppPreferences p, StorageKind kind) => kind == StorageKind.Missions ? p.MissionDirectory : p.ScienceLibraryDirectory;
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
