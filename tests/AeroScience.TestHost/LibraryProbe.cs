using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
internal static class LibraryProbe
{
    public static void Run(string[] args)
    {
        var folder=Path.GetFullPath(args[1]);var storage=new StorageManager(new PreferenceStore(folder)).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();
        if(args[0]=="inspect")
        {science.RecoverLibraryJobs();Console.WriteLine(JsonSerializer.Serialize(science.LibraryJobs().Single(),WireJson.Options));return;}
        var scene=new ScientificSceneSelection("usgs-3dep","SYNTHETIC-CRASH-FIXTURE",new string('a',64),["elevation"]);var area=new GeographicBounds(-75,40,-74.98,40.02);var selection=new ScientificLibrarySelection(1,[area],[scene]);var now=DateTimeOffset.UtcNow;
        var preview=new LibraryDownloadPreview(1,Guid.NewGuid(),selection,new string('b',64),new string('c',64),now,now.AddMinutes(30),[new(0,scene,area,null,37748736)],[],1048576,1048576,40000000,0,null,null,["SYNTHETIC persistence fixture; no provider or MSFS acceptance."]);
        science.SaveLibraryPreview(preview);science.SetLibraryState(preview.Id,"Downloading",approve:true);science.SetLibraryItem(preview.Id,0,"Downloading");science.LibraryBudget(preview.Id,0,0,65536);
        if(args[0]=="charged")science.LibraryBudget(preview.Id,0,1234,65536);
        File.WriteAllText(args[2],"ready");Thread.Sleep(Timeout.Infinite);
    }
}
