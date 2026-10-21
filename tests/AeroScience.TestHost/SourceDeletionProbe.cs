using System.Text.Json;
using System.Security.Cryptography;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Tests;
internal static class SourceDeletionProbe
{
    public static async Task Run(string[] args)
    {
        var root=Path.GetFullPath(args[1]);var settings=new StorageManager(new PreferenceStore(root)).Initialize();var missions=new MissionStore(Path.Combine(settings.MissionDirectory,"missions.db"));missions.Initialize();using var science=new ScienceStore(settings.ScienceLibraryDirectory);science.Initialize();
        var backup=Path.Combine(science.DirectoryPath,"Metadata","Backups","owner-backup.txt");
        if(args[0]=="inspect"){
            var expected=JsonSerializer.Deserialize<ScientificSource[]>(File.ReadAllText(Path.Combine(root,"expected.json")),WireJson.Options)!;var retained=science.Sources();
            foreach(var source in retained)foreach(var file in source.Files){using var stream=File.OpenRead(Path.Combine(science.DirectoryPath,source.RelativeDirectory,file.RelativePath));if(Convert.ToHexStringLower(SHA256.HashData(stream))!=file.Sha256)throw new IOException("Recovered original hash changed.");}
            if(File.ReadAllText(backup)!="preserve original backup"||expected.Any(s=>!File.Exists(Path.Combine(root,"user-"+s.Manifest.Kind,"source.json"))))throw new IOException("Unrelated backup or user originals changed.");
            Console.WriteLine(JsonSerializer.Serialize(new{sources=retained.Length,products=science.Products().Length,datasets=science.Datasets().Count,backupPreserved=true,userOriginalsPreserved=true},WireJson.Options));return;
        }
        var originals=new List<ScientificSource>();foreach(var kind in new[]{RemoteKind.Terrain,RemoteKind.Thermal})originals.Add(await science.ImportSourceAsync(RemoteFixtureFiles.Create(Path.Combine(root,"user-"+kind),kind)));
        PreferenceStore.WriteAtomic(Path.Combine(root,"expected.json"),originals);Directory.CreateDirectory(Path.GetDirectoryName(backup)!);File.WriteAllText(backup,"preserve original backup");
        var preview=science.PreviewSourceDeletion(new(All:true),missions);if(preview.EligibleSources!=2)throw new IOException("Synthetic source fixture was unexpectedly blocked.");
        science.SourceDeletionFault=point=>{if(point!=args[0])return;using(var marker=new FileStream(args[2],FileMode.CreateNew,FileAccess.Write,FileShare.Read)){marker.WriteByte(1);marker.Flush(true);}Thread.Sleep(Timeout.Infinite);};
        science.DeleteExternalSources(new(preview.Id,preview.SelectionHash,preview.Confirmation),missions,new MissionCoordinator(missions,1000).EnterMaintenance);
    }
}
