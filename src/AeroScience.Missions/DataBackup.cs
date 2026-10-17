using Microsoft.Data.Sqlite;

namespace AeroScience.Missions;

public static class DataBackup
{
    // Call under the application's maintenance/storage leases. SQLite backup includes committed WAL data.
    public static string Create(string source, string destinationFolder, bool science)
    {
        StorageManager.ValidateLocalPath(destinationFolder);
        if (!Directory.Exists(destinationFolder)) throw new IOException("Choose an existing backup folder.");
        if (new LocalStorageCapacity().FreeBytes(destinationFolder) < new FileInfo(source).Length * 2 + StorageManager.MinimumFreeBytes)
            throw new IOException("Insufficient backup capacity. No data was cleared.");
        var name = $"AeroScience-{(science ? "science" : "missions")}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sqlite";
        var target = Path.Combine(destinationFolder, name); var temporary = target + ".partial";
        try
        {
            using (var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            { from.Open(); to.Open(); from.BackupDatabase(to); }
            if (StorageManager.DatabaseFingerprint(source, science) != StorageManager.DatabaseFingerprint(temporary, science))
                throw new IOException("Backup verification failed. No data was cleared.");
            File.Move(temporary, target); return target;
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
}
