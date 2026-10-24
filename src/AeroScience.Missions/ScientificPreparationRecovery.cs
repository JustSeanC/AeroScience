using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed partial class AutomaticScientificData
{
    private async Task<ScientificSource?> RecoverPreparedSource(ScientificDiscovery discovery,CancellationToken token,Action<string>? stage,string? checkpointRoot)
    {
        var pending=science.PendingArtifacts();
        foreach(var record in pending.Ledgers)
        {
            token.ThrowIfCancellationRequested();StorageManager.ValidateLocalPath(record);
            if(new FileInfo(record).Length>1048576)throw new IOException("Interrupted preparation ownership is oversized; preserve it for review.");
            var work=JsonSerializer.Deserialize<ManagedScientificWork>(File.ReadAllText(record),WireJson.Options);
            if(work?.Operation!="Automatic numeric source preparation")continue;
            var relative=work.Paths.SingleOrDefault(p=>p.StartsWith("Downloads/",StringComparison.Ordinal)&&p.EndsWith("/source.json",StringComparison.Ordinal));if(relative is null)continue;
            var path=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);StorageManager.ValidateLocalPath(path);if(!File.Exists(path))continue;
            if(new FileInfo(path).Length>16777216)throw new IOException("Interrupted scientific metadata exceeds its verification bound.");
            var manifest=ScienceStore.ReadSourceManifest(path);
            if(manifest.Acquisition is not {} original||original.RequestedArea!=discovery.Area||original.CatalogRevision!=discovery.CatalogRevision||ScientificCatalog.Hash(manifest with{Acquisition=null})!=ScientificCatalog.Hash(discovery.Manifest with{Acquisition=null}))continue;
            var names=manifest.Bands.Select(b=>b.File).Append("source.json").Distinct(StringComparer.Ordinal).ToArray();var folder=Path.GetDirectoryName(path)!;
            if(names.Any(name=>!work.Paths.Contains(Path.GetRelativePath(science.DirectoryPath,Path.Combine(folder,name)).Replace('\\','/'),StringComparer.Ordinal)))throw new IOException("Interrupted preparation does not own its required scientific files.");
            // Re-enter the original scientific validator and transactional publisher.
            // An old failed import is not declared verified merely because files exist.
            stage?.Invoke("Reverifying retained preparation");
            var source=await science.ImportSourceAsync(path,token);
            foreach(var name in names){var file=ScientificRaster.SafeFile(folder,name);StorageManager.ValidateLocalPath(file);File.Delete(file);}
            if(!Directory.EnumerateFileSystemEntries(folder).Any()){Directory.Delete(folder);File.Delete(record);}
            if(checkpointRoot is not null)foreach(var asset in discovery.Assets){var relativeCheckpoint=checkpointRoot+"/"+asset.Band.Id;var checkpoint=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relativeCheckpoint);if(Directory.Exists(checkpoint))new ScientificRangeCheckpoint(science,relativeCheckpoint).Complete();}
            return source;
        }
        return null;
    }
}
