using System.Security.Cryptography;
using AeroScience.Missions;
using Microsoft.Data.Sqlite;

namespace AeroScience.Engine;

public static class ValidationArchiveCopy
{
    public static void Select(PreferenceStore preferences, AppPreferences copy)
    {
        if(!preferences.IsConfined)throw new IOException("A confined validation profile is required.");
        var current=preferences.Load();preferences.ValidateDirectory(copy.MissionDirectory);preferences.ValidateDirectory(copy.ScienceLibraryDirectory);
        // Copied approved jobs are evidence, not new network consent for this test.
        var access=new ScientificAccessSettings(preferences.DirectoryPath);
        access.Save(access.Get().Preferences with{OnlineEnabled=false});
        preferences.Save(current with{MissionDirectory=copy.MissionDirectory,ScienceLibraryDirectory=copy.ScienceLibraryDirectory,Initialized=true});
    }
    // Explicit local-UI operation only. Never initialize/migrate the source stores.
    public static AppPreferences Create(PreferenceStore confinedPreferences, string missionFolder, string scienceFolder, string destination)
    {
        if (!confinedPreferences.IsConfined) throw new IOException("A confined validation profile is required.");
        confinedPreferences.ValidateDirectory(destination);
        StorageManager.ValidateLocalPath(missionFolder); StorageManager.ValidateLocalPath(scienceFolder);
        StorageManager.RequireMissionDatabase(missionFolder); _ = StorageManager.LibraryId(scienceFolder);
        var originalDb = Path.Combine(missionFolder, "missions.db");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=originalDb, Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM missions WHERE status='Recording'";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new IOException("The source archive has a recording/interrupted mission. Recover it in its original application before copying.");
        }
        if (Directory.Exists(destination)) throw new IOException("Keep previous test copies; choose a new destination.");
        if (Within(destination, missionFolder) || Within(missionFolder, destination) || Within(destination, scienceFolder) || Within(scienceFolder, destination))
            throw new IOException("Test copies must be separate from source archives.");
        var sourceFiles = Files(scienceFolder).ToArray();
        var bytes = checked(sourceFiles.Sum(f=>new FileInfo(f).Length) + new FileInfo(originalDb).Length * 2);
        if (new LocalStorageCapacity().FreeBytes(destination) < bytes * 2 + StorageManager.MinimumFreeBytes)
            throw new IOException("Insufficient space for the verified disposable copy and reserve.");
        var stage = destination + ".partial-" + Guid.NewGuid().ToString("N");
        confinedPreferences.ValidateDirectory(stage);
        Directory.CreateDirectory(stage);
        var missions = Path.Combine(stage,"Missions"); var science=Path.Combine(stage,"ScienceLibrary");
        Directory.CreateDirectory(missions); Directory.CreateDirectory(science);
        // Existing WAL-aware backup/fingerprint logic, not raw database-file copies.
        var missionBackup=DataBackup.Create(originalDb,missions,false); File.Move(missionBackup,Path.Combine(missions,"missions.db"));
        var relativeDatabase=ScienceStore.RelativeDatabasePath.Replace('/',Path.DirectorySeparatorChar);
        foreach (var source in sourceFiles)
        {
            var relative=Path.GetRelativePath(scienceFolder,source);
            if(relative==relativeDatabase || relative==relativeDatabase+"-wal" || relative==relativeDatabase+"-shm" || Path.GetFileName(source)=="engine.lock")continue;
            var target=Path.Combine(science,relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source,target,false);
            using var from=File.OpenRead(source);using var to=File.OpenRead(target);
            if(!SHA256.HashData(from).SequenceEqual(SHA256.HashData(to)))throw new IOException("Original file changed during copy. The incomplete test copy is retained; source files were not modified.");
        }
        var scienceDb=Path.Combine(scienceFolder,relativeDatabase);
        if(File.Exists(scienceDb))
        {
            var target=Path.Combine(science,relativeDatabase);Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(DataBackup.Create(scienceDb,Path.GetDirectoryName(target)!,true),target);
        }
        Directory.Move(stage,destination);
        return new(Path.Combine(destination,"Missions"),Path.Combine(destination,"ScienceLibrary"),Initialized:true);
    }
    private static bool Within(string child,string parent) => Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar)
        .StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(child).Equals(Path.GetFullPath(parent),StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Files(string directory)
    {
        foreach(var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Source archives cannot contain links or junctions.");
            if(Directory.Exists(path)){foreach(var file in Files(path))yield return file;}else yield return path;
        }
    }
}
