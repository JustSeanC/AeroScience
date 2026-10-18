namespace AeroScience.Missions;

public sealed partial class StorageManager
{
    public string Compact(StorageKind kind,bool confirmed,bool recording)
    {
        if(!confirmed||recording)throw new InvalidOperationException("End recording and explicitly confirm compaction. No data was changed.");
        var settings=preferences.Load();var directory=kind==StorageKind.Missions?settings.MissionDirectory:settings.ScienceLibraryDirectory;
        ValidateLocalPath(directory);if(kind==StorageKind.ScienceLibrary)_=LibraryId(directory);else RequireMissionDatabase(directory);
        using var lease=Acquire(directory);var database=Path.Combine(directory,kind==StorageKind.Missions?"missions.db":ScienceStore.RelativeDatabasePath);
        if(!File.Exists(database))throw new IOException("There is no database to compact.");
        var before=DatabaseBytes(database);if(disk.FreeBytes(directory)<before*3+MinimumFreeBytes)throw new IOException("Insufficient space for a verified backup, VACUUM working copy and safety reserve.");
        var backups=Path.Combine(directory,kind==StorageKind.Missions?"Backups":"Metadata/Backups");Directory.CreateDirectory(backups);
        var backup=DataBackup.Create(database,backups,kind==StorageKind.ScienceLibrary);var fingerprint=DatabaseFingerprint(database,kind==StorageKind.ScienceLibrary);
        using(var db=OpenDatabase(database,false))using(var command=db.CreateCommand())
        {command.CommandText="PRAGMA wal_checkpoint(TRUNCATE)";command.ExecuteNonQuery();command.CommandText="VACUUM";command.ExecuteNonQuery();}
        if(DatabaseFingerprint(database,kind==StorageKind.ScienceLibrary)!=fingerprint)throw new IOException("Post-compaction fingerprint differs. Preserve the verified backup and restore it before recording: "+backup);
        return $"Compaction verified. Database/WAL/SHM footprint before: {before} bytes; after: {DatabaseBytes(database)} bytes; actual database bytes reclaimed: {before-DatabaseBytes(database)}. Verified recovery backup retained: {backup}. Backup storage is additional; no original observations were deleted.";
    }
}
