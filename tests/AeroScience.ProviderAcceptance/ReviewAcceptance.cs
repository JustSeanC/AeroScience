using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
// Genuine retained originals + explicitly synthetic scheduled flight evidence.
// This is not an online-provider rerun or Windows/MSFS acceptance.
internal static class ReviewAcceptance
{
    public static async Task Run(string retainedRoot,string output)
    {
        using var originals=new ScienceStore(Path.Combine(retainedRoot,"ScienceLibrary"));var results=new List<object>();
        foreach(var original in originals.Sources().GroupBy(s=>s.Manifest.Kind).Select(g=>g.First()))
        {
            var folder=Path.Combine(output,original.Manifest.Kind.ToString());var paths=new StorageManager(new PreferenceStore(folder)).Initialize();
            var missions=new MissionStore(Path.Combine(paths.MissionDirectory,"missions.db"));missions.Initialize();using var science=new ScienceStore(paths.ScienceLibraryDirectory);science.Initialize();missions.ScienceLibrary=science;
            var input=Path.Combine(originals.DirectoryPath,original.RelativeDirectory);var area=original.Manifest.Acquisition!.RequestedArea;
            Coordinates? point=null;
            for(var n=0;n<81;n++)
            {
                var candidate=new Coordinates(area.South+(area.North-area.South)*(n/9+.5)/9,area.West+(area.East-area.West)*(n%9+.5)/9);
                var bands=original.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(input,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,[raster.Read(candidate)],"Candidate actual source pixel",raster.Grid);}).ToArray();
                if(RemoteCalculations.Apply(original.Manifest.Kind,bands).Values.Any(v=>v.Any(x=>x.HasValue))){point=candidate;break;}
            }
            if(point is null)throw new IOException("No valid genuine source pixel for review acceptance.");
            var instrument=RemoteAcquisition.Instruments.Single(p=>p.Value==original.Manifest.Kind).Key;
            using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research);
            var mission=coordinator.Start(new("Genuine source / synthetic outrun — "+original.Manifest.Kind,Instruments:[new(instrument,true)]));var utc=DateTimeOffset.UtcNow;
            var frame=new TelemetryFrame(utc,TelemetrySource.Simulated,"Synthetic scheduled position — not MSFS",point.Latitude,point.Longitude,1000,100,100,100,100,0,0,0,0,0);
            coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Explicit synthetic outrun fixture",utc),frame));
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));while(missions.Recovery(mission.Id).Length==0)await Task.Delay(10,timeout.Token);coordinator.End(mission.Id);
            var slot=missions.Recovery(mission.Id).Single();if(slot.State!="Pending"||!missions.VerifyRecoveryEvidence(slot))throw new IOException("Recoverable exact archived evidence not established.");
            var imported=await science.ImportSourceAsync(original.Manifest,input);using var remote=new RemoteAcquisition(missions,science);
            var product=remote.Reconstruct(slot,default);var repeated=remote.Reconstruct(slot,default);if(product.Id!=repeated.Id||science.Products(mission.Id).Length!=1)throw new IOException("Recovery was not idempotent.");
            missions.UpdateRecovery(slot with{State="Recovered",ProductId=product.Id,DerivedCompleted=true});
            var data=science.ReadProduct(product.Id);var map=science.MapPage(new(Missions:[mission.Id]));var measurement=original.Manifest.Kind switch{RemoteKind.Terrain=>"elevation",RemoteKind.Thermal=>"temperature",_=>"ndvi"};
            var layer=ScientificMapLayer.Create(data,measurement);var artifacts=ScientificComposites.Build(science,missions,mission.Id,new(imported.Id,measurement));
            var report=new{kind=original.Manifest.Kind,source=data.Source,product,data.SampleLocations,data.Bands,data.Derived,review=missions.ScientificReview(mission.Id),summary=ScientificResearchSummary.Build(missions,science,mission.Id),mapCount=map.Products.Length,mapLayer=layer,artifacts,idempotent=product.Id==repeated.Id,originalFlight=missions.StreamSamples(mission.Id).Single()};
            await File.WriteAllTextAsync(Path.Combine(folder,"review-acceptance.json"),JsonSerializer.Serialize(report,WireJson.Options));results.Add(new{kind=original.Manifest.Kind,product.Sha256,product.FlightPacketUtc,product.SourceObservationUtc,product.ProcessedUtc,report.idempotent,mapCount=report.mapCount,sourceFiles=original.Files});Console.WriteLine(original.Manifest.Kind+": genuine retained source reconstructed once, mapped, summarized and composited.");
        }
        await File.WriteAllTextAsync(Path.Combine(output,"review-acceptance.json"),JsonSerializer.Serialize(new{formatVersion=1,evidenceType="genuine-retained-data-scientific-review",checkedUtc=DateTimeOffset.UtcNow,network="No new provider requests; retained benchmark originals",flightInput="synthetic scheduled position; not MSFS",windowsMsfs="not tested",results},WireJson.Options));
    }
}
