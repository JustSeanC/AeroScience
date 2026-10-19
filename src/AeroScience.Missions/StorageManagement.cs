using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record StorageLimits(long? MissionLimitBytes = 25000000000, long? ScienceLimitBytes = 50000000000,
    bool AutomaticCacheCleanup = true, bool ProtectPinned = true);
public sealed record ManagedStorageStatus(string Kind, string Directory, bool Available, long UsedBytes, long? LimitBytes,
    double? Percentage, long? FreeBytes, string Status, long Missions = 0, long Datasets = 0, long Products = 0,
    long SourceBytes = 0, long CacheBytes = 0, long DatabaseBytes = 0, long BackupBytes = 0, string? Error = null,
    string Accounting = "Managed catalogued files and SQLite DB/WAL/SHM; unrelated files excluded, backups shown separately", long PendingBytes = 0, int PendingOperations = 0, long ReservedBytes = 0);
public sealed record StorageManagementSnapshot(StorageLimits Preferences, ManagedStorageStatus Flight, ManagedStorageStatus Science, DateTimeOffset CheckedUtc);
public sealed class StorageLimitException(string message) : IOException(message) { public string Reason => "storageLimitReached"; }

// Preferences are stable configuration; stores/migrations retain their identities and paths.
public sealed class StorageManagement
{
    private readonly string path;
    private readonly MissionStore missions;
    private readonly ScienceStore science;
    private readonly IStorageCapacity disk;
    private readonly object gate = new();
    private StorageLimits limits;
    private long scienceFiles, sourceBytes, cacheBytes, productCount, datasetCount, backups, pendingBytes;
    private int pendingOperations;
    private long mutationVersion;
    private long reservedBytes;
    public string? ConfigurationError { get; private set; }
    private DateTimeOffset checkedUtc = DateTimeOffset.MinValue;
    public Func<bool>? AutomaticCleanup;
    public StorageManagement(string configuration, MissionStore missions, ScienceStore science, IStorageCapacity? disk = null)
    {
        path = Path.Combine(configuration, "storage-limits.json"); this.missions = missions; this.science = science; this.disk = disk ?? new LocalStorageCapacity();
        limits = new();
        try { limits = File.Exists(path) ? JsonSerializer.Deserialize<StorageLimits>(File.ReadAllText(path)) ?? throw new IOException("Invalid storage settings.") : new(); Validate(limits); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { limits = new(); ConfigurationError = "Storage limit preferences cannot be read. Offline history remains available; new recording is blocked until you save reviewed limits in Settings. " + error.Message; }
    }
    public StorageLimits Preferences { get { lock (gate) return limits; } }
    private static void Validate(StorageLimits value)
    {
        if (value.MissionLimitBytes is <= 0 || value.ScienceLimitBytes is <= 0 || !value.ProtectPinned) throw new ArgumentException("Storage limits must be positive byte values or Unlimited (null). Pinned-item protection remains enabled.");
    }
    public StorageLimits Configure(StorageLimits value)
    { Validate(value); lock (gate) { if(reservedBytes>0&&value.ScienceLimitBytes is {} cap&&scienceFiles+science.DatabaseUsageBytes()+reservedBytes>=cap)throw new InvalidOperationException("Wait for the active scientific import/acquisition budget to finish before lowering the limit below its reserved space.");PreferenceStore.WriteAtomic(path, value); limits = value; ConfigurationError = null; return limits; } }
    private static long Size(string path) {try{return new FileInfo(path).Length;}catch(FileNotFoundException){return 0;}catch(DirectoryNotFoundException){return 0;}}
    private static long BackupBytes(string path) => Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).Sum(Size) : 0;
    public void RefreshScience()
    {
        long version;lock(gate)version=mutationVersion;
        if (science.AvailabilityError() is { } error) throw new IOException(error);
        var originals = science.Sources().SelectMany(s => s.Files.Select(f => (Source: s, File: f))).ToArray();
        var catalogued = new HashSet<string>(StringComparer.Ordinal);
        long sources = 0;
        foreach (var group in originals.GroupBy(v => v.Source.RelativeDirectory + "/" + v.File.RelativePath, StringComparer.Ordinal)) { var file=StorageManager.ResolveDataset(science.DirectoryPath, science.LibraryId, group.Key); catalogued.Add(file); sources += Size(file); }
        long products = 0, count = 0;
        foreach (var product in science.AllProducts()) { var file=StorageManager.ResolveDataset(science.DirectoryPath, product.LibraryId, product.RelativePath); catalogued.Add(file); products += Size(file); count++; }
        var caches = science.CacheEntries().Sum(c => Size(StorageManager.ResolveDataset(science.DirectoryPath, science.LibraryId, c.RelativePath)));
        var metadata = Size(Path.Combine(science.DirectoryPath, StorageManager.LibraryManifest)) + Size(Path.Combine(science.DirectoryPath, ".science-storage.json"));
        foreach (var source in science.Sources()) { var file=StorageManager.ResolveDataset(science.DirectoryPath, science.LibraryId, source.RelativeDirectory + "/source.json"); catalogued.Add(file); metadata += Size(file); }
        var pending=science.PendingArtifacts(); var retained=pending.Artifacts.Where(p=>!catalogued.Contains(p)).Sum(Size); metadata+=pending.Ledgers.Sum(Size);
        foreach(var category in new[]{"CleanupHistory","Clears"})
        {
            var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,"Metadata/"+category);
            if(Directory.Exists(folder))foreach(var file in Directory.EnumerateFiles(folder,"*.json").Take(10000))
                if(Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out _)){StorageManager.ValidateLocalPath(file);metadata+=Size(file);}
        }
        long datasets = 0; for (var offset = 0; ; offset += 100) { var page = science.Datasets(limit: 100, offset: offset); datasets += page.Count; if (page.Count < 100) break; }
        lock (gate) { if(version!=mutationVersion)return;scienceFiles = sources + products + caches + metadata + retained; sourceBytes = sources; cacheBytes = caches; productCount = count; datasetCount = datasets; pendingBytes=retained; pendingOperations=pending.Ledgers.Length; backups = BackupBytes(Path.Combine(science.DirectoryPath, "Metadata", "Backups")); checkedUtc = DateTimeOffset.UtcNow; }
    }
    public void AddedScienceFile(long bytes, bool cache = false, bool product = false, bool source = false)
    { lock (gate) { mutationVersion++;scienceFiles = Math.Max(0, scienceFiles + bytes); if (cache) cacheBytes = Math.Max(0, cacheBytes + bytes); if(product)productCount++;if(source)sourceBytes+=bytes; } }
    public void AddedDataset() { lock(gate){mutationVersion++;datasetCount++;} }
    public void RequireScienceCapacity(long expectedBytes = 65536)
    {
        if(ConfigurationError is {} invalid)throw new IOException(invalid);
        if (checkedUtc == DateTimeOffset.MinValue) RefreshScience();
        void Check()
        {
            if (science.AvailabilityError() is { } unavailable) throw new IOException(unavailable);
            var free = disk.FreeBytes(science.DirectoryPath);
            long reserved;lock(gate)reserved=reservedBytes;
            if (free < expectedBytes + reserved + StorageManager.MinimumFreeBytes) throw new StorageLimitException("Scientific drive has insufficient unreserved free space; new science writes are suspended. Completed research is preserved; flight recording is independent.");
            lock (gate) if (limits.ScienceLimitBytes is { } cap && scienceFiles + science.DatabaseUsageBytes() + reservedBytes + expectedBytes >= cap)
                throw new StorageLimitException("Scientific storage limit reached. New science writes are suspended; change the limit or clean regenerable caches. Completed data and flight telemetry are preserved.");
        }
        try { Check(); }
        catch (StorageLimitException) when (Preferences.AutomaticCacheCleanup && AutomaticCleanup is not null)
        { _ = AutomaticCleanup(); Check(); }
    }
    public IDisposable ReserveScience(long expectedBytes)
    {
        if(expectedBytes<=0)throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        RequireScienceCapacity(expectedBytes);
        lock(gate)
        {
            if(limits.ScienceLimitBytes is {} cap&&scienceFiles+science.DatabaseUsageBytes()+reservedBytes+expectedBytes>=cap)throw new StorageLimitException("Scientific capacity was reserved by another import/acquisition. New science writes are suspended; flight recording continues.");
            reservedBytes=checked(reservedBytes+expectedBytes);
        }
        return new Reservation(()=>{lock(gate)reservedBytes=Math.Max(0,reservedBytes-expectedBytes);});
    }
    private sealed class Reservation(Action release):IDisposable
    {private int disposed;public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)release();}}
    public void RequireFlightCapacity(long expectedBytes = 65536)
    {
        if(ConfigurationError is {} invalid)throw new IOException(invalid);
        StorageManager.RequireMissionDatabase(Path.GetDirectoryName(missions.DatabasePath)!);
        if (disk.FreeBytes(Path.GetDirectoryName(missions.DatabasePath)!) < expectedBytes + StorageManager.MinimumFreeBytes)
            throw new StorageLimitException("Flight storage has insufficient physical disk space. Recording stopped safely; committed history is preserved.");
        if (Preferences.MissionLimitBytes is { } cap && StorageManager.DatabaseBytes(missions.DatabasePath) + expectedBytes >= cap)
            throw new StorageLimitException("Flight storage limit reached. Recording stopped safely; committed history is preserved. Change the cap in Storage Management to resume a new mission.");
    }
    public StorageManagementSnapshot Snapshot(bool refresh = false)
    {
        string? scienceError = null;
        if (refresh || checkedUtc == DateTimeOffset.MinValue) try { RefreshScience(); } catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { scienceError = error.Message; }
        ManagedStorageStatus Status(string kind, string directory, long used, long? cap, long database, long missionCount = 0)
        {
            try
            {
                if (kind == "science" && (scienceError ?? science.AvailabilityError()) is { } unavailable) throw new IOException(unavailable);
                if (kind == "flight") StorageManager.RequireMissionDatabase(directory);
                var free = disk.FreeBytes(directory); var percent = cap is > 0 ? used * 100d / cap : null;
                var status = free < StorageManager.MinimumFreeBytes ? "Insufficient disk space" : percent >= 100 ? "Storage limit reached" : percent >= 95 ? "Nearly full" : percent >= 80 ? "Approaching storage limit" : "Normal";
                return new(kind, directory, true, used, cap, percent, free, status, missionCount, kind == "science" ? datasetCount : 0, kind == "science" ? productCount : 0,
                    kind == "science" ? sourceBytes : 0, kind == "science" ? cacheBytes : 0, database, kind == "science" ? backups : BackupBytes(Path.Combine(directory, "Backups")), Error: ConfigurationError, PendingBytes: kind=="science"?pendingBytes:0, PendingOperations: kind=="science"?pendingOperations:0,ReservedBytes:kind=="science"?reservedBytes:0);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(kind, directory, false, used, cap, null, null, "Storage unavailable", Error: error.Message); }
        }
        lock (gate)
        {
            var flightDb = StorageManager.DatabaseBytes(missions.DatabasePath); var scienceDb = science.DatabaseUsageBytes();
            long missionCount=0; try { StorageManager.RequireMissionDatabase(Path.GetDirectoryName(missions.DatabasePath)!); missionCount=missions.MissionCount(); } catch(Exception e) when(e is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { }
            return new(limits, Status("flight", Path.GetDirectoryName(missions.DatabasePath)!, flightDb, limits.MissionLimitBytes, flightDb, missionCount),
                Status("science", science.DirectoryPath, scienceFiles + scienceDb, limits.ScienceLimitBytes, scienceDb), checkedUtc);
        }
    }
}

public sealed partial class MissionStore
{
    public RecordingStopInfo? LastRecordingStop()
    {
        if(!File.Exists(DatabasePath))return null;
        try
        {
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT kind,message,mission_id,timestamp_utc FROM events WHERE mission_id=(SELECT id FROM missions ORDER BY started_utc DESC LIMIT 1) AND kind IN ('storageLimitReached','storageUnavailable') ORDER BY id DESC LIMIT 1";
            using var row=cmd.ExecuteReader();return row.Read()?new(row.GetString(0),row.GetString(1),Guid.Parse(row.GetString(2)),DateTimeOffset.Parse(row.GetString(3),System.Globalization.CultureInfo.InvariantCulture)):null;
        }
        catch(Microsoft.Data.Sqlite.SqliteException){return null;} // Pre-initialization/legacy stores have no such event yet.
    }
    public long MissionCount() { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM missions"; return Convert.ToInt64(cmd.ExecuteScalar()); }
    public bool IsProtected(Guid id)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT message FROM events WHERE mission_id=$id AND kind='protection-changed' ORDER BY id DESC LIMIT 1";cmd.Parameters.AddWithValue("$id", id.ToString()); return cmd.ExecuteScalar() as string == "protected";
    }
    public void Protect(Guid id, bool value)
    { if (Detail(id) is null) throw new ArgumentException("Mission not found."); AddEvent(id, DateTimeOffset.UtcNow, "protection-changed", value ? "protected" : "unprotected"); }
    public long ProtectedMissionCount()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM events e WHERE kind='protection-changed' AND message='protected' AND id=(SELECT MAX(id) FROM events WHERE mission_id=e.mission_id AND kind='protection-changed')"; return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
