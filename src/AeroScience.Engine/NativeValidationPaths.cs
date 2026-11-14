using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

// Extra destination guard used only by the new private profile. No installation occurs here.
public static class NativeValidationPaths
{
    public static void ValidateCommunity(string community,string bundle,IEnumerable<string>? protectedRoots=null)
    {
        var selected=Path.TrimEndingDirectorySeparator(Path.GetFullPath(community));
        if(!Directory.Exists(selected)||!(Path.GetFileName(selected).Equals("Community",StringComparison.OrdinalIgnoreCase)
            ||Path.GetFileName(selected).Equals("Community2024",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Select the actual existing MSFS Community/Community2024 folder; a data or application folder is not a package destination.");
        StorageManager.ValidateLocalPath(selected);
        var roots=protectedRoots??KnownDataRoots();
        foreach(var root in roots.Append(Path.GetDirectoryName(Path.GetFullPath(bundle))!))
        {
            var other=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if(selected.Equals(other,StringComparison.OrdinalIgnoreCase)||selected.StartsWith(other+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                ||other.StartsWith(selected+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected Community folder overlaps an application, validation profile or scientific data root. Existing files were preserved.");
        }
    }
    private static IEnumerable<string> KnownDataRoots()
    {
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots=new List<string>{Path.Combine(local,"AeroScience"),Path.Combine(local,"Programs","AeroScience")};
        if(Directory.Exists(local))roots.AddRange(Directory.EnumerateDirectories(local,"AeroScience-Validation-*"));
        foreach(var root in roots.ToArray())
        {
            var settings=Path.Combine(root,"settings.json");if(!File.Exists(settings))continue;
            if((File.GetAttributes(settings)&FileAttributes.ReparsePoint)!=0||new FileInfo(settings).Length>65536)throw new IOException("Protected settings need local inspection before choosing a toolbar destination.");
            using var doc=JsonDocument.Parse(File.ReadAllText(settings));
            foreach(var name in new[]{"MissionDirectory","ScienceLibraryDirectory"})
                if(doc.RootElement.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is {} path)roots.Add(path);
        }
        return roots;
    }
}
