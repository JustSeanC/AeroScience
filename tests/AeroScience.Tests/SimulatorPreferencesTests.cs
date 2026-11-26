using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using Xunit;

namespace AeroScience.Tests;

public sealed class SimulatorPreferencesTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-p03-preferences",Guid.NewGuid().ToString("N"));
    [Fact] public void LegacyDefaultsDoNotWriteSettingsOrChangeOtherPreferences()
    {
        var store=new PreferenceStore(directory);
        Assert.Equal(SimulatorPreferences.Default,store.Simulator());Assert.False(Directory.Exists(directory));
        var original=JsonSerializer.SerializeToNode(new AppPreferences(directory,Path.Combine(directory,"library"),true,true,"192.168.1.4",5000,"retained-community",true,new(1,"dark","fr-FR")))!.AsObject();
        Assert.False(original.ContainsKey("Simulator"));original["UnknownOwnerSection"]=new JsonObject{["units"]="µm",["consent"]=false};
        Directory.CreateDirectory(directory);File.WriteAllText(store.SettingsPath,original.ToJsonString());var bytes=File.ReadAllBytes(store.SettingsPath);
        Assert.Equal(SimulatorPreferences.Default,store.Simulator());Assert.Equal(bytes,File.ReadAllBytes(store.SettingsPath));
        var choice=store.SetSimulator(new(0,"xplane12",9012));Assert.Equal(1,choice.Revision);
        Assert.Equal(choice,new PreferenceStore(directory).Simulator());
        var after=JsonNode.Parse(File.ReadAllText(store.SettingsPath))!.AsObject();after.Remove("Simulator");Assert.True(JsonNode.DeepEquals(original,after));
        Assert.False(Directory.Exists(Path.Combine(directory,"library")));
    }
    [Theory]
    [InlineData("{}")] [InlineData("{\"Version\":2,\"Id\":\"xplane12\",\"XPlanePort\":8086}")]
    [InlineData("{\"Version\":1,\"Id\":\"unknown\",\"XPlanePort\":8086}")]
    [InlineData("{\"Version\":1,\"Id\":\"XPlane12\",\"XPlanePort\":8086}")]
    [InlineData("{\"Version\":1,\"Id\":\"xplane12\",\"XPlanePort\":0}")]
    [InlineData("{\"Version\":1,\"Id\":\"xplane12\",\"XPlanePort\":65536}")]
    [InlineData("{\"Version\":1,\"Id\":\"xplane12\",\"XPlanePort\":8086,\"Revision\":-1}")]
    public void PresentInvalidSectionFailsClosedAndIsNotRepairedSilently(string section)
    {
        var store=new PreferenceStore(directory);var json=JsonSerializer.SerializeToNode(store.Load())!.AsObject();json["Simulator"]=JsonNode.Parse(section);
        Directory.CreateDirectory(directory);File.WriteAllText(store.SettingsPath,json.ToJsonString());var before=File.ReadAllBytes(store.SettingsPath);
        Assert.Throws<InvalidOperationException>(()=>store.Simulator());Assert.Throws<InvalidOperationException>(()=>store.SetSimulator(new(0,"msfs2024",8086)));
        Assert.Equal(before,File.ReadAllBytes(store.SettingsPath));
    }
    [Theory] [InlineData(null,8086)] [InlineData("msfs2024",0)] [InlineData("xplane12",65536)] [InlineData("http://127.0.0.1",8086)]
    public void InvalidUpdatesNeverCreateSettings(string? id,int port)
    {var store=new PreferenceStore(directory);Assert.Throws<ArgumentException>(()=>store.SetSimulator(new(0,id,port)));Assert.False(File.Exists(store.SettingsPath));}
    [Fact] public void UnknownSimulatorFieldsAndMalformedJsonCannotBeOverwritten()
    {
        var store=new PreferenceStore(directory);Directory.CreateDirectory(directory);
        foreach(var section in new[]{"{broken","{\"MissionDirectory\":\"unused\",\"ScienceLibraryDirectory\":\"unused\",\"Simulator\":{\"Version\":1,\"Id\":\"xplane12\",\"XPlanePort\":8086,\"Host\":\"remote\"}}"})
        {File.WriteAllText(store.SettingsPath,section);Assert.Throws<JsonException>(()=>store.SetSimulator(new(0,"xplane12",8086)));Assert.Equal(section,File.ReadAllText(store.SettingsPath));}
    }
    [Fact] public void ConcurrentRevisionsAndPresentationWritesRemainIndependentAndAtomic()
    {
        var stores=Enumerable.Range(0,8).Select(_=>new PreferenceStore(directory)).ToArray();var success=0;
        Parallel.ForEach(stores,s=>{try{s.SetSimulator(new(0,"xplane12",8086));Interlocked.Increment(ref success);}catch(InvalidOperationException){}});
        Assert.Equal(1,success);
        Parallel.Invoke(()=>stores[0].SetPresentation(new(0,"dark","de-DE")),()=>stores[1].SetSimulator(new(1,"msfs2024",65535)));
        Assert.Equal(new SimulatorPreferences(1,"msfs2024",65535,2),stores[0].Simulator());Assert.Equal("de-DE",stores[0].Presentation().Locale);
        Assert.Throws<InvalidOperationException>(()=>stores[2].SetSimulator(new(1,"xplane12",8086)));
        var bytes=File.ReadAllBytes(stores[0].SettingsPath);Assert.Equal(2,stores[0].SetSimulator(new(2,"msfs2024",65535)).Revision);Assert.Equal(bytes,File.ReadAllBytes(stores[0].SettingsPath));
    }
    [Fact] public void MaintenanceAndConfinedProfilesCannotBeBypassed()
    {
        var store=new PreferenceStore(directory);Directory.CreateDirectory(directory);File.WriteAllText(store.JournalPath,"protected");
        Assert.Throws<InvalidOperationException>(()=>store.SetSimulator(new(0,"xplane12",8086)));Assert.False(File.Exists(store.SettingsPath));
        var confined=new PreferenceStore(Path.Combine(directory,"outside"),Path.Combine(directory,"inside"));
        Assert.Throws<IOException>(()=>confined.SetSimulator(new(0,"xplane12",8086)));Assert.False(File.Exists(confined.SettingsPath));
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
