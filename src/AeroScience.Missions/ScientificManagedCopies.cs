using System.Text.Json;

namespace AeroScience.Missions;

internal sealed record ScientificManagedCopy(int Version,Guid LibraryId,string Path,string Kind,Guid? Id=null);
public sealed partial class ScienceStore
{
    internal static void RegisterScientificCopy(string library,string path,string kind)
    {
        StorageManager.ValidateLocalPath(path);var id=StorageManager.LibraryId(library);
        var record=Path.Combine(library,"Metadata","ManagedCopies",Guid.NewGuid().ToString("N")+".json");PreferenceStore.WriteAtomic(record,new ScientificManagedCopy(1,id,Path.GetFullPath(path),kind));
    }
    private IEnumerable<ScientificManagedCopy> RegisteredCopies()
    {
        var directory=Path.Combine(DirectoryPath,"Metadata","ManagedCopies");if(!Directory.Exists(directory))yield break;
        foreach(var path in Directory.EnumerateFiles(directory,"*.json"))
        {
            StorageManager.ValidateLocalPath(path);if(new FileInfo(path).Length>65536)throw new IOException("Managed backup ownership is invalid; deletion is blocked.");
            var record=ReadCatalog<ScientificManagedCopy>(File.ReadAllText(path));if(record.Version!=1||record.LibraryId!=LibraryId||record.Kind is not("database" or "recovery"))throw new IOException("Managed backup ownership/version is invalid; deletion is blocked.");
            StorageManager.ValidateLocalPath(record.Path);if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out var recordId))throw new IOException("Invalid managed-copy identity.");yield return record with{Id=recordId};
        }
    }
    private string ResolveCollectedPath(string relative)
    {
        if(!relative.StartsWith("ManagedCopies/",StringComparison.Ordinal))return StorageManager.ResolveDataset(DirectoryPath,LibraryId,relative);
        var parts=relative.Split('/',3);if(parts.Length!=3||!Guid.TryParseExact(parts[1],"N",out var id)||!parts[2].StartsWith("Products/",StringComparison.Ordinal))throw new IOException("Invalid managed scientific copy path.");
        var record=RegisteredCopies().SingleOrDefault(c=>c.Id==id&&c.Kind=="recovery")??throw new IOException("Managed recovery-copy ownership is unavailable.");
        var marker=Path.Combine(record.Path,"recovery.json");StorageManager.ValidateLocalPath(marker);if(new FileInfo(marker).Length>65536)throw new IOException("Invalid recovery-copy ownership metadata.");
        using var json=JsonDocument.Parse(File.ReadAllText(marker));if(!json.RootElement.TryGetProperty("libraryId",out var identity)||identity.GetGuid()!=LibraryId)throw new IOException("Recovery copy belongs to another Library.");
        if(parts[2].Split('/').Any(p=>p is ".." or "."||p.Contains('\\')||p.Contains(':')))throw new IOException("Invalid recovery-copy relative path.");
        var target=Path.GetFullPath(Path.Combine(record.Path,parts[2]));StorageManager.ValidateLocalPath(target);return target;
    }
    private IEnumerable<string> AllReplicaRoots()=> (ManagedReplicaRoots?.Invoke()??[]).Concat(RegisteredCopies().Where(c=>c.Kind=="recovery").Select(c=>c.Path)).Distinct(StringComparer.OrdinalIgnoreCase);
}
