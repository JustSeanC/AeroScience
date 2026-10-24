using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

public sealed class ScientificLibraryFolderTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-folder-"+Guid.NewGuid().ToString("N"));
    private sealed class Opener:IScientificFolderOpener
    {
        public int Calls;public Guid Library;public Guid? Source;
        public void OpenScientificFolder(Guid libraryId,Guid? sourceId){Calls++;Library=libraryId;Source=sourceId;}
    }
    private (ScienceStore Science,MissionStore Missions,ScientificLibraryFolders Folders,Opener Opener) Setup()
    {
        var prefs=new PreferenceStore(root);var selected=Path.Combine(root,"custom-scientific-disk","my-retained-data");
        prefs.Save(new(prefs.DefaultMissionDirectory,selected));var paths=new StorageManager(prefs).Initialize();
        var missions=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();
        var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();var storage=new StorageManagement(root,missions,science);science.Management=storage;
        var opener=new Opener();return(science,missions,new(science,storage,opener),opener);
    }
    [Fact]public void LocalAndPairedLocationUseCustomConfigurationButOnlyLocalCanOpen()
    {
        var f=Setup();using var science=f.Science;var id=science.LibraryId;
        var local=f.Folders.Location(true);Assert.Equal(science.DirectoryPath,local.Path);Assert.True(local.CanOpen);Assert.True(local.Available);Assert.NotNull(local.FreeBytes);Assert.NotNull(local.UsedBytes);
        var remote=f.Folders.Location(false);Assert.Equal(local.Path,remote.Path);Assert.False(remote.CanOpen);
        Assert.Throws<UnauthorizedAccessException>(()=>f.Folders.Open(false,new(id)));Assert.Equal(0,f.Opener.Calls);
        f.Folders.Open(true,new(id));Assert.Equal(1,f.Opener.Calls);Assert.Equal(id,f.Opener.Library);Assert.Null(f.Opener.Source);
        Assert.Throws<IOException>(()=>f.Folders.Open(true,new(Guid.NewGuid())));Assert.Equal(1,f.Opener.Calls);
    }
    [Fact]public async Task SourceFolderRequiresCatalogOwnershipAndNeverChangesOriginals()
    {
        var f=Setup();using var science=f.Science;
        var source=await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user"),RemoteKind.Terrain));
        var before=source.Files.Select(file=>File.ReadAllBytes(Path.Combine(science.DirectoryPath,source.RelativeDirectory,file.RelativePath))).ToArray();
        Assert.Equal(Path.Combine(science.DirectoryPath,source.RelativeDirectory),f.Folders.Location(true,source.Id).Path);
        f.Folders.Open(true,new(science.LibraryId,source.Id));Assert.Equal(source.Id,f.Opener.Source);
        Assert.Throws<ArgumentException>(()=>f.Folders.Open(true,new(science.LibraryId,Guid.NewGuid())));
        foreach(var pair in source.Files.Select((file,i)=>(file,i)))Assert.Equal(before[pair.i],File.ReadAllBytes(Path.Combine(science.DirectoryPath,source.RelativeDirectory,pair.file.RelativePath)));
        File.AppendAllText(Path.Combine(science.DirectoryPath,source.RelativeDirectory,"source.json"),"invalid");
        Assert.False(f.Folders.Location(true,source.Id).Available);Assert.Equal(1,f.Opener.Calls);
    }
    [Fact]public void MissingDriveReturnsUsefulLocationWithoutCreatingReplacement()
    {
        var f=Setup();var science=f.Science;var id=science.LibraryId;science.Dispose();
        Directory.Move(science.DirectoryPath,science.DirectoryPath+".offline");var location=f.Folders.Location(true);
        Assert.False(location.Available);Assert.False(location.CanOpen);Assert.Equal(science.DirectoryPath,location.Path);Assert.NotNull(location.Error);
        Assert.Throws<IOException>(()=>f.Folders.Open(true,new(id)));Assert.False(Directory.Exists(science.DirectoryPath));Assert.Equal(0,f.Opener.Calls);
    }
    [Fact]public async Task LinkedSourceDirectoryIsRejected()
    {
        if(OperatingSystem.IsWindows())return; // Actual Windows reparse behavior remains in platform coverage.
        var f=Setup();using var science=f.Science;var source=await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user"),RemoteKind.Terrain));
        var path=Path.Combine(science.DirectoryPath,source.RelativeDirectory);Directory.Move(path,path+".held");Directory.CreateSymbolicLink(path,path+".held");
        Assert.False(f.Folders.Location(true,source.Id).Available);Assert.Throws<IOException>(()=>f.Folders.Open(true,new(science.LibraryId,source.Id)));Assert.Equal(0,f.Opener.Calls);
        Directory.Delete(path);Directory.Move(path+".held",path);
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
