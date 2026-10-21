using AeroScience.Engine;
using AeroScience.Missions;
using Xunit;

namespace AeroScience.Tests;

public sealed class ValidationIsolationTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-isolation",Guid.NewGuid().ToString("N"));
    private PreferenceStore Confined => new(Path.Combine(root,"Validation"),Path.Combine(root,"Validation"));

    [Fact] public void ProductionAndValidationIdentitiesAreDistinctAndProductionStable()
    {
        var production=new ApplicationProfile(false);var validation=new ApplicationProfile(true);
        Assert.Equal("AeroScience",production.Folder);Assert.Equal(5080,production.Port);
        Assert.Equal(@"Software\AeroScience",production.RegistryKey);
        Assert.Equal("aeroscience-toolbar",production.ToolbarPackage);
        Assert.Equal("AeroScience.Command.v1",production.CommandEvent);
        Assert.Equal(@"Local\AeroScience.Desktop",ApplicationProfile.DesktopMutex);
        Assert.NotEqual(production.ConfigurationDirectory,validation.ConfigurationDirectory);
        Assert.NotEqual(production.RegistryKey,validation.RegistryKey);Assert.Equal(5085,validation.Port);
        Assert.NotEqual(production.ToolbarPackage,validation.ToolbarPackage);
        Assert.NotEqual(production.CommandEvent,validation.CommandEvent);Assert.NotEqual(production.ReplyEvent,validation.ReplyEvent);
        Assert.StartsWith(validation.ConfigurationDirectory,validation.Preferences().Load().MissionDirectory);
    }
    [Fact] public void SharedPathsAndTamperedPreferencesFailBeforeOpeningAStore()
    {
        var store=Confined;var defaults=store.Load();var production=Path.Combine(root,"Production");Directory.CreateDirectory(production);
        var original=Path.Combine(production,"sentinel.txt");File.WriteAllText(original,"production archive");
        Assert.Throws<IOException>(()=>store.Save(defaults with{MissionDirectory=production}));
        Assert.Throws<IOException>(()=>new StorageManager(store).UseExisting(StorageKind.Missions,production,true,false));
        Assert.Throws<IOException>(()=>new StorageManager(store).Migrate(StorageKind.Missions,production,true,false));
        Assert.False(File.Exists(Path.Combine(production,"engine.lock")));
        PreferenceStore.WriteAtomic(store.SettingsPath,defaults with{ScienceLibraryDirectory=production});
        Assert.Throws<IOException>(()=>new StorageManager(store).Initialize());
        Assert.Equal("production archive",File.ReadAllText(original));Assert.False(File.Exists(Path.Combine(store.DirectoryPath,"missions.db")));
    }
    [Fact] public void EscapingMigrationJournalCannotAcquireProductionLease()
    {
        var store=Confined;var production=Path.Combine(root,"Production");Directory.CreateDirectory(production);
        PreferenceStore.WriteAtomic(store.JournalPath,new MigrationJournal(StorageKind.Missions,production,store.DirectoryPath,"copying"));
        Assert.Throws<IOException>(()=>new StorageManager(store).RecoverInterrupted());
        Assert.Throws<IOException>(()=>new StorageManager(store).RollbackInterrupted(true));
        Assert.Empty(Directory.GetFileSystemEntries(production));
    }
    [Fact] public void LinksAndSiblingPrefixCannotEscapeValidationRoot()
    {
        var store=Confined;var original=Path.Combine(root,"Production");Directory.CreateDirectory(original);Directory.CreateDirectory(store.DirectoryPath);
        Assert.Throws<IOException>(()=>store.ValidateDirectory(store.DirectoryPath+"-production"));
        // Junctions need Windows privileges; the same attribute guard is exercised by
        // existing Windows path tests. Unix creates a genuine symlink here.
        if(!OperatingSystem.IsWindows())
        {
            var link=Path.Combine(store.DirectoryPath,"linked");Directory.CreateSymbolicLink(link,original);
            Assert.Throws<IOException>(()=>store.Save(store.Load() with{MissionDirectory=link}));
            Directory.Delete(link);
        }
    }
    [Fact] public void DisposableCopyUsesVerifiedBackupsAndRetainsOriginalScience()
    {
        var original=new PreferenceStore(Path.Combine(root,"Original"));var settings=new StorageManager(original).Initialize();
        using(var science=new ScienceStore(settings.ScienceLibraryDirectory)){science.Initialize();}
        var originalDb=Path.Combine(settings.MissionDirectory,"missions.db");var before=StorageManager.DatabaseFingerprint(originalDb);
        var sourceFile=Path.Combine(settings.ScienceLibraryDirectory,"Imagery","protected-original.bin");File.WriteAllBytes(sourceFile,[1,4,7,9]);
        var store=Confined;var destination=Path.Combine(store.DirectoryPath,"Copies","one");
        var copied=ValidationArchiveCopy.Create(store,settings.MissionDirectory,settings.ScienceLibraryDirectory,destination);
        Assert.Equal(before,StorageManager.DatabaseFingerprint(originalDb));
        Assert.Equal(before,StorageManager.DatabaseFingerprint(Path.Combine(copied.MissionDirectory,"missions.db")));
        Assert.Equal(StorageManager.LibraryId(settings.ScienceLibraryDirectory),StorageManager.LibraryId(copied.ScienceLibraryDirectory));
        Assert.Equal(File.ReadAllBytes(sourceFile),File.ReadAllBytes(Path.Combine(copied.ScienceLibraryDirectory,"Imagery","protected-original.bin")));
        new ScientificAccessSettings(store.DirectoryPath).Save(new(true,true));
        ValidationArchiveCopy.Select(store,copied);
        Assert.False(new ScientificAccessSettings(store.DirectoryPath).Get().Preferences.OnlineEnabled);
        Assert.Equal(copied.MissionDirectory,store.Load().MissionDirectory);
        Assert.Equal(settings.MissionDirectory,original.Load().MissionDirectory);
        Assert.Throws<IOException>(()=>ValidationArchiveCopy.Create(store,settings.MissionDirectory,settings.ScienceLibraryDirectory,destination));
        Directory.Delete(destination,true);
        Assert.True(File.Exists(originalDb));Assert.True(File.Exists(sourceFile));Assert.True(File.Exists(original.SettingsPath));
    }
    [Fact] public void ValidationEngineRejectsProductionPortsAndRoots()
    {
        var validation=new ApplicationProfile(true);
        Assert.Throws<IOException>(()=>validation.ValidateEngine(new EngineOptions()));
        var defaults=validation.Preferences().Load();
        validation.ValidateEngine(new(){ConfigurationDirectory=validation.ConfigurationDirectory,DataDirectory=defaults.MissionDirectory,ScienceLibraryDirectory=defaults.ScienceLibraryDirectory,Port=5085});
        Assert.Throws<IOException>(()=>validation.ValidateEngine(new(){ConfigurationDirectory=validation.ConfigurationDirectory,DataDirectory=new ApplicationProfile(false).ConfigurationDirectory,ScienceLibraryDirectory=defaults.ScienceLibraryDirectory,Port=5085}));
    }
    [Fact] public void ValidationToolbarRequiresExactSourceBeforeAnyControlOrMapRequest()
    {
        var profile=new ApplicationProfile(true);
        Assert.NotNull(profile.AuthorizeToolbar("{}"));
        Assert.NotNull(profile.AuthorizeToolbar("{\"validationSource\":\"another-build\"}"));
        Assert.Null(profile.AuthorizeToolbar(System.Text.Json.JsonSerializer.Serialize(new{validationSource=ApplicationProfile.SourceCommit})));
        Assert.Null(new ApplicationProfile(false).AuthorizeToolbar("{}"));
    }
    [Fact] public void WrongSourceNativeStartIsRejectedByTheActualHandler()
    {
        Directory.CreateDirectory(root);var store=new MissionStore(Path.Combine(root,"missions.db"));store.Initialize();
        var coordinator=new MissionCoordinator(store,1000);
        var handler=new ToolbarCommandHandler(coordinator,authorize:new ApplicationProfile(true).AuthorizeToolbar);
        var command=System.Text.Json.JsonSerializer.Serialize(new{protocol=1,requestId=Guid.NewGuid(),op="start",name="must not start",validationSource="old-candidate"});
        using var result=System.Text.Json.JsonDocument.Parse(handler.Handle(command));
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Validation toolbar source mismatch",result.RootElement.GetProperty("error").GetString());
        Assert.Null(coordinator.Snapshot().ActiveMission);Assert.Empty(store.List(10,0));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
