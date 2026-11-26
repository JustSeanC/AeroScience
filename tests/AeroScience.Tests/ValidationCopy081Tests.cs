using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;
public sealed class ValidationCopy081Tests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"p05-copy-"+Guid.NewGuid().ToString("N"));
    [Fact] public void CopyRegistersFinalOwnedPathsAndCannotInheritAuthorityOverOriginalBackups()
    {
        var originalPreferences=new PreferenceStore(Path.Combine(root,"Original"));var original=new StorageManager(originalPreferences).Initialize();
        var store=new MissionStore(Path.Combine(original.MissionDirectory,"missions.db"));var origin=new SimulatorOrigin(1,"msfs2024",null,"fixture","1",null,Guid.NewGuid(),"map");
        var id=store.Create("Original source identity",null,DateTimeOffset.UtcNow,simulatorOrigin:origin);store.Finish(id,MissionStatus.Completed,DateTimeOffset.UtcNow,"Fixture");
        using(var science=new ScienceStore(original.ScienceLibraryDirectory))science.Initialize();
        var originalsBackup=Path.Combine(root,"Original-Backups");Directory.CreateDirectory(originalsBackup);
        _=DataBackup.Create(Path.Combine(original.ScienceLibraryDirectory,ScienceStore.RelativeDatabasePath),originalsBackup,true);
        var dbBefore=StorageManager.DatabaseFingerprint(store.DatabasePath);var sciencePath=Path.Combine(original.ScienceLibraryDirectory,ScienceStore.RelativeDatabasePath);var scienceBefore=StorageManager.DatabaseFingerprint(sciencePath,true);
        var confined=new PreferenceStore(Path.Combine(root,"Candidate"),Path.Combine(root,"Candidate"));var destination=Path.Combine(confined.DirectoryPath,"Copies","one");
        var copy=ValidationArchiveCopy.Create(confined,original.MissionDirectory,original.ScienceLibraryDirectory,destination);
        Assert.Equal(dbBefore,StorageManager.DatabaseFingerprint(store.DatabasePath));Assert.Equal(scienceBefore,StorageManager.DatabaseFingerprint(sciencePath,true));
        Assert.Equal(origin,new MissionStore(Path.Combine(copy.MissionDirectory,"missions.db")).Detail(id)!.Mission.SimulatorOrigin);
        Assert.False(Directory.Exists(Path.Combine(copy.ScienceLibraryDirectory,"Metadata","ManagedCopies")));
        var missionRecord=Assert.Single(Directory.GetFiles(Path.Combine(original.MissionDirectory,"Metadata","ScientificBackupLocations"),"*.json"));
        using(var record=JsonDocument.Parse(File.ReadAllText(missionRecord))){Assert.Equal(Path.Combine(copy.MissionDirectory,"missions.db"),record.RootElement.GetProperty("backup").GetString());Assert.True(File.Exists(record.RootElement.GetProperty("backup").GetString()));}
        var records=Directory.GetFiles(Path.Combine(original.ScienceLibraryDirectory,"Metadata","ManagedCopies"),"*.json").Select(p=>JsonDocument.Parse(File.ReadAllText(p))).ToArray();
        try{Assert.Equal(2,records.Length);Assert.Contains(records,r=>r.RootElement.GetProperty("Path").GetString()==Path.Combine(copy.ScienceLibraryDirectory,ScienceStore.RelativeDatabasePath));Assert.All(records,r=>Assert.True(File.Exists(r.RootElement.GetProperty("Path").GetString())));}finally{foreach(var r in records)r.Dispose();}
        ValidationArchiveCopy.Select(confined,copy);Assert.False(new ScientificAccessSettings(confined.DirectoryPath).Get().Preferences.OnlineEnabled);
        Assert.Equal(original,originalPreferences.Load());
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
