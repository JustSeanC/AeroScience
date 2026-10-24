using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class ExternalSourceDeletionTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"external-deletion-"+Guid.NewGuid().ToString("N"));private readonly ScienceStore science;private readonly MissionStore missions;private readonly MissionCoordinator coordinator;
    public ExternalSourceDeletionTests(){var paths=new StorageManager(new PreferenceStore(root)).Initialize();missions=new(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();science=new(paths.ScienceLibraryDirectory);science.Initialize();coordinator=new(missions,1000);}
    private Task<ScientificSource> Import(RemoteKind kind=RemoteKind.Terrain)=>science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user-original-"+Guid.NewGuid()),kind));
    private SourceDeletionResult Delete(SourceDeletionPreview p,string? hash=null)=>science.DeleteExternalSources(new(p.Id,hash??p.SelectionHash),missions,coordinator.EnterMaintenance);
    [Fact]public async Task ActualSelectiveDeletionRemovesOnlyOwnedOriginalsAndIsIdempotent()
    {
        var a=await Import();var b=await Import(RemoteKind.Thermal);var backup=Path.Combine(science.DirectoryPath,"Metadata","Backups","previous-backup.txt");Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.WriteAllText(backup,"preserved");
        var preview=science.PreviewSourceDeletion(new([a.Id]),missions);Assert.True(preview.EligibleSources==1,string.Join("; ",preview.Sources.SelectMany(s=>s.Blockers)));Assert.True(preview.EligibleManagedBytes>0);Assert.Throws<InvalidOperationException>(()=>Delete(preview,"wrong selection hash"));Assert.Equal(2,science.Sources().Length);
        var removed=Delete(preview);Assert.Equal("Completed",removed.State);Assert.Equal(preview.EligibleManagedBytes,removed.RemovedManagedBytes);Assert.Equal(b.Id,Assert.Single(science.Sources()).Id);Assert.Equal("preserved",File.ReadAllText(backup));Assert.All(Directory.GetDirectories(root,"user-original-*"),d=>Assert.True(File.Exists(Path.Combine(d,"source.json"))));Assert.Empty(science.Datasets());Assert.Empty(science.Products());Assert.Equal(removed.RemovedSources,Delete(preview).RemovedSources);
    }
    [Fact]public async Task ArchivedReferenceActiveAnalysisAndChangedFileEachBlockTheExactSelection()
    {
        var source=await Import();var mission=coordinator.Start(new("Synthetic lineage fixture"));coordinator.End(mission.Id);missions.AddDatasetReference(mission.Id,new(Guid.NewGuid(),science.LibraryId,"Sources/"+source.Id.ToString("N"),"{\"sourceId\":\""+source.Id+"\"}"));
        var p=science.PreviewSourceDeletion(new([source.Id]),missions);Assert.Contains(p.Sources.Single().Blockers,b=>b.Contains("Mission evidence"));Assert.Throws<InvalidOperationException>(()=>Delete(p));
        var other=await Import(RemoteKind.Thermal);using(var lease=science.BeginAnalysis())Assert.Equal(0,science.PreviewSourceDeletion(new([other.Id]),missions).EligibleSources);
        var clean=science.PreviewSourceDeletion(new([other.Id]),missions);File.AppendAllText(Path.Combine(science.DirectoryPath,other.RelativeDirectory,other.Files[0].RelativePath),"changed");Assert.Throws<InvalidOperationException>(()=>Delete(clean));Assert.Equal(2,science.Sources().Length);
    }
    [Theory][InlineData("quarantined",true)][InlineData("before-commit",false)][InlineData("committed",true)]
    public async Task ProcessInterruptionRecoversAccordingToAtomicDatabaseReceipt(string checkpoint,bool committed)
    {
        var a=await Import();var b=await Import(RemoteKind.Thermal);var p=science.PreviewSourceDeletion(new(All:true),missions);science.SourceDeletionFault=point=>{if(point==checkpoint)throw new SimulatedKill();};Assert.Throws<SimulatedKill>(()=>Delete(p));science.SourceDeletionFault=null;science.Dispose();
        using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();Assert.Equal(committed?0:2,reopened.Sources().Length);
        foreach(var source in new[]{a,b})Assert.Equal(!committed,File.Exists(Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Files[0].RelativePath)));
    }
    [Fact]public async Task FilePermissionFailureKeepsPendingOwnedCleanupAndNoGhostCatalogSource()
    {
        var source=await Import();var p=science.PreviewSourceDeletion(new([source.Id]),missions);science.SourceDeletionFault=point=>{if(point=="before-file-remove")throw new IOException("Synthetic locked file");};var result=Delete(p);Assert.Equal("Cleanup pending",result.State);Assert.True(result.PendingManagedBytes>0);Assert.Empty(science.Sources());science.SourceDeletionFault=null;Assert.Equal("Completed",Delete(p).State);
    }
    [Fact]public async Task SymlinkUnrelatedFileAndLostMissionDatabaseFailClosed()
    {
        var source=await Import();var folder=Path.Combine(science.DirectoryPath,source.RelativeDirectory);File.WriteAllText(Path.Combine(folder,"unrelated.txt"),"preserved");Assert.Equal(0,science.PreviewSourceDeletion(new([source.Id]),missions).EligibleSources);File.Delete(Path.Combine(folder,"unrelated.txt"));
        if(!OperatingSystem.IsWindows()){File.CreateSymbolicLink(Path.Combine(folder,"escape"),Path.Combine(root,"outside"));Assert.Equal(0,science.PreviewSourceDeletion(new([source.Id]),missions).EligibleSources);File.Delete(Path.Combine(folder,"escape"));}
        SqliteConnection.ClearAllPools();File.Move(missions.DatabasePath,missions.DatabasePath+".preserved");Assert.Equal(0,science.PreviewSourceDeletion(new([source.Id]),missions).EligibleSources);Assert.Single(science.Sources());
    }
    private sealed class SimulatedKill:Exception;
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
