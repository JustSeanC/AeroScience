using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// A small write-ahead ledger accounts for partial/orphan output without treating
// unknown files as disposable. Interrupted artifacts remain for explicit recovery.
public sealed record ManagedScientificWork(Guid Id, DateTimeOffset StartedUtc, string Operation, string[] Paths);
public sealed partial class ScienceStore
{
    internal string BeginManagedWrite(string operation, IEnumerable<string> paths)
    {
        var work = new ManagedScientificWork(Guid.NewGuid(), DateTimeOffset.UtcNow, operation, paths.Distinct(StringComparer.Ordinal).ToArray());
        foreach (var path in work.Paths) _ = StorageManager.ResolveDataset(DirectoryPath, LibraryId, path);
        var file = StorageManager.ResolveDataset(DirectoryPath, LibraryId, $"Metadata/Work/{work.Id:N}.json");
        PreferenceStore.WriteAtomic(file, work); return file;
    }
    public (string[] Artifacts, string[] Ledgers) PendingArtifacts()
    {
        var folder = StorageManager.ResolveDataset(DirectoryPath, LibraryId, "Metadata/Work");
        if (!Directory.Exists(folder)) return ([], []);
        var files = Directory.EnumerateFiles(folder, "*.json").Take(10001).ToArray();
        if(files.Length>10000)throw new IOException("Too many unfinished processing records. Review retained work before more science writes; nothing was deleted.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            StorageManager.ValidateLocalPath(file);
            if (new FileInfo(file).Length > 1048576) throw new IOException("Managed work ledger is oversized; review retained processing files before recording.");
            ManagedScientificWork work;
            try{work = JsonSerializer.Deserialize<ManagedScientificWork>(File.ReadAllText(file), WireJson.Options)
                ?? throw new IOException("A managed work ledger is invalid; retained artifacts were not removed.");}
            catch(JsonException error){throw new IOException("A managed work ledger is invalid; retained files need review, not automatic removal.",error);}
            if (Path.GetFileName(file) != work.Id.ToString("N") + ".json" || work.Paths is null || work.Paths.Length > 2050)
                throw new IOException("Managed work identity is invalid; retained artifacts were not removed.");
            foreach (var relative in work.Paths) paths.Add(StorageManager.ResolveDataset(DirectoryPath, LibraryId, relative));
        }
        return (paths.ToArray(), files);
    }
}
