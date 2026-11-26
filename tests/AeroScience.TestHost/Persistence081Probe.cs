using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using AeroScience.Telemetry;

internal static class Persistence081Probe
{
    internal static void Create(string root)
    {
        if(Directory.Exists(root))throw new IOException("Use a fresh disposable fixture.");
        var preferences=new PreferenceStore(Path.Combine(root,"Profile"));var settings=new StorageManager(preferences).Initialize();
        var store=new MissionStore(Path.Combine(settings.MissionDirectory,"missions.db"));
        using(var science=new ScienceStore(settings.ScienceLibraryDirectory))science.Initialize();
        var at=DateTimeOffset.UtcNow.AddDays(-1);var frame=SimulatedTelemetryProvider.Generate(at,0) with{Source=TelemetrySource.Simulator};
        var legacy=store.Create("Synthetic legacy identity",frame,at);store.Append(legacy,frame);store.Finish(legacy,MissionStatus.Completed,at,"Fixture");
        var origin=new SimulatorOrigin(1,"msfs2024",null,"fixture","1",null,Guid.NewGuid(),"fixture-map");var qualified=frame with{SimulatorOrigin=origin};
        var current=store.Create("Synthetic schema 14 origin",qualified,at,simulatorOrigin:origin);store.Append(current,qualified);store.Finish(current,MissionStatus.Completed,at,"Fixture");
        PreferenceStore.WriteAtomic(Path.Combine(root,"fixture.json"),new{version=1,missionDirectory=settings.MissionDirectory,scienceDirectory=settings.ScienceLibraryDirectory,configuration=preferences.DirectoryPath,legacy,current,fingerprint=StorageManager.DatabaseFingerprint(store.DatabasePath)});
    }
    internal static void Verify(string root)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"fixture.json")));var r=doc.RootElement;var path=Path.Combine(r.GetProperty("missionDirectory").GetString()!,"missions.db");
        if(StorageManager.DatabaseFingerprint(path)!=r.GetProperty("fingerprint").GetString())throw new IOException("Older executable modified schema 14 evidence.");
        var store=new MissionStore(path);
        if(store.Detail(r.GetProperty("legacy").GetGuid())!.Mission.SimulatorOrigin is not null||store.Detail(r.GetProperty("current").GetGuid())!.Mission.SimulatorOrigin?.SimulatorId!="msfs2024")throw new IOException("Original unknown/frozen identities changed.");
        Console.WriteLine("Schema-14 original payloads, origins and complete database fingerprint unchanged after older-runtime refusal.");
    }
}
