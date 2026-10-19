using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
public static class OfflineScientificAcceptance
{
    public static async Task Run(string originalLibrary,string output,ScienceStore science,MissionStore missions,ScientificAccessSettings settings)
    {
        using var original=new ScienceStore(originalLibrary);
        settings.Save(new(false,false,PreferredDateUtc:DateTimeOffset.Parse("2024-09-10T00:00:00Z"),LookbackDays:120));
        var results=new List<object>();
        using var automatic=new AutomaticScientificData(science,settings);
        foreach(var old in original.Sources())
        {
            var source=await science.ImportSourceAsync(old.Manifest,Path.Combine(originalLibrary,old.RelativeDirectory));
            var area=source.Manifest.Acquisition!.RequestedArea;automatic.Prepare(area,[source.Manifest.Kind],true);
            ScientificDataReadiness state;var until=DateTimeOffset.UtcNow.AddSeconds(30);
            do{await Task.Delay(50);state=automatic.Snapshot().Single(s=>s.Kind==source.Manifest.Kind&&s.Area==area);}while(state.Status is "Queued" or "Downloading"&&DateTimeOffset.UtcNow<until);
            if(state.Status is not("Ready" or "Partially usable")||state.DownloadedBytes!=0)throw new IOException("Genuine retained source could not be reused offline: "+state.Message);
            var locations=Enumerable.Range(0,576).Select(i=>new Coordinates(area.South+(area.North-area.South)*(i/24+.5)/24,area.West+(area.East-area.West)*(i%24+.5)/24)).ToArray();
            var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Actual retained public scientific source; not a new online download",raster.Grid);}).ToArray();
            var derived=RemoteCalculations.Apply(source.Manifest.Kind,bands);var values=derived.Values.First();var index=Array.FindIndex(values,v=>v.HasValue);if(index<0)throw new IOException("No valid scientific samples in known-good retained area.");
            using var research=new ResearchOperations(missions,science);var recorder=new MissionCoordinator(missions,1000,research:research);var id=RemoteAcquisition.Instruments.Single(p=>p.Value==source.Manifest.Kind).Key;
            var mission=recorder.Start(new("Genuine retained source / synthetic flight input",Instruments:[new(id,true)]));var now=DateTimeOffset.UtcNow;var point=locations[index];var frame=new TelemetryFrame(now,TelemetrySource.Simulated,"Synthetic offline acceptance position",point.Latitude,point.Longitude,1000,100,100,100,100,0,0,0,0,0);
            recorder.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"No MSFS hardware",now),frame));
            for(var n=0;n<200&&science.Products(mission.Id).Length==0;n++)await Task.Delay(50);
            var product=science.Products(mission.Id).Single();_=science.ReadProduct(product.Id);recorder.End(mission.Id);
            var result=new{provider=source.Manifest.Provider,scene=source.Manifest.SceneId,sourceObservedUtc=source.Manifest.ObservationUtc,originalFiles=source.Files,validSamples=derived.Values.SelectMany(v=>v).Count(v=>v.HasValue),networkBytes=0,product,readiness=state};results.Add(result);Console.WriteLine("Actual retained source sampled, published and reopened offline: "+source.Manifest.Name);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"offline-scientific-acceptance.json"),JsonSerializer.Serialize(new{formatVersion=1,checkedUtc=DateTimeOffset.UtcNow,evidenceType="genuine-retained-public-data-offline",networkAcceptance="not established by this run",windowsMsfs="not tested",flightInput="synthetic test position",results},WireJson.Options));
    }
}
