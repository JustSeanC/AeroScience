using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

public static class RegionalWorkspaceAcceptance
{
    public static async Task Run(string output,AutomaticScientificData automatic,ScienceStore science,MissionStore missions)
    {
        var first=science.Sources().Single(s=>s.Manifest.Kind==RemoteKind.Terrain&&s.Manifest.Provider.Contains("Copernicus",StringComparison.OrdinalIgnoreCase));
        var secondPoint=new Coordinates(46.105,8.125);var area=GeographicBounds.Around(secondPoint);var queued=automatic.Prepare(area,[RemoteKind.Terrain],true).Single(s=>s.Area==area&&s.Kind==RemoteKind.Terrain);
        ScientificDataReadiness state;var deadline=DateTimeOffset.UtcNow.AddMinutes(3);
        do{await Task.Delay(200);state=automatic.Snapshot().Single(s=>s.Key==queued.Key);}while(state.Status is "Queued" or "Downloading"&&DateTimeOffset.UtcNow<deadline);
        if(state.Status is not("Ready" or "Partially usable")||state.SourceId is null)throw new IOException("Adjacent genuine terrain region unavailable: "+state.Message);
        var second=science.Sources().Single(s=>s.Id==state.SourceId);
        if(first.Id==second.Id||!ScientificComposites.Compatible(first,second))throw new IOException("Expected two genuinely compatible retained windows of the same original Copernicus scene.");
        using var research=new ResearchOperations(missions,science);var recorder=new MissionCoordinator(missions,1000,research:research);var mission=recorder.Start(new("Genuine regional mosaic / synthetic flight input",Instruments:[new("terrain-scanner",true)]));
        var start=DateTimeOffset.UtcNow;
        foreach(var (point,index) in new[]{new Coordinates(46.105,8.105),secondPoint}.Select((p,i)=>(p,i)))
        {
            var now=start.AddMilliseconds(index*1100);var frame=new TelemetryFrame(now,TelemetrySource.Simulated,"Synthetic regional-mosaic acceptance position",point.Latitude,point.Longitude,1000,100,100,100,100,0,0,0,0,0);
            recorder.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Genuine sources; synthetic input, no MSFS",now),frame));
            var until=DateTimeOffset.UtcNow.AddSeconds(20);while(science.Products(mission.Id).Length<index+1&&DateTimeOffset.UtcNow<until)await Task.Delay(20);
        }
        recorder.End(mission.Id);
        var products=science.Products(mission.Id);if(products.Length!=2||products.Select(p=>p.SourceId).Distinct().Count()!=2)throw new IOException("Regional mission did not retain acquisitions from both actual source windows.");
        await File.WriteAllTextAsync(Path.Combine(output,"regional-workspace-acceptance.json"),JsonSerializer.Serialize(new{formatVersion=1,evidenceType="genuine-two-region-copernicus-source-reuse",checkedUtc=DateTimeOffset.UtcNow,windowsMsfs="not tested",flightInput="synthetic test positions",firstSource=first,secondSource=second,missionId=mission.Id,additionalNetworkBytes=state.DownloadedBytes,additionalReusedBytes=state.ReusedBytes,products},WireJson.Options));
        Console.WriteLine("Two genuine same-scene regional windows retained; extra network bytes "+state.DownloadedBytes+", reused "+state.ReusedBytes+" bytes.");
    }
}
