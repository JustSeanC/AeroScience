using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

// Offline opt-in genuine retained-public-source validation. Never edits prior receipts/originals.
public static class SurveyAcceptance
{
    public static async Task Run(string originalLibrary,string output)
    {
        Directory.CreateDirectory(output);var library=Path.Combine(output,"ScienceLibrary");Directory.CreateDirectory(library);
        PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{formatVersion=1,libraryId=Guid.NewGuid()});
        var missions=new MissionStore(Path.Combine(output,"missions.db"));missions.Initialize();using var science=new ScienceStore(library);science.Initialize();missions.ScienceLibrary=science;
        var settings=new ScientificAccessSettings(output,()=>true);settings.Save(new(false,false));using var automatic=new AutomaticScientificData(science,settings);
        var options=new JsonSerializerOptions(WireJson.Options){PropertyNameCaseInsensitive=true};var numerics=new List<object>();var summaries=new List<object>();
        foreach(var metadata in Directory.EnumerateFiles(Path.Combine(originalLibrary,"Sources"),"source.json",SearchOption.AllDirectories).Order()){
            var old=JsonSerializer.Deserialize<ScientificSource>(await File.ReadAllTextAsync(metadata),options)??throw new IOException("Retained source identity missing.");
            if(old.Manifest.Kind is not(RemoteKind.Terrain or RemoteKind.Thermal))continue;if(old.Files.Sum(f=>f.Bytes)>67108864)throw new IOException("Retained source exceeds the bounded 64 MiB workload; select a smaller genuine input.");
            var root=Path.GetDirectoryName(metadata)!;
            foreach(var file in old.Files){var path=ScientificRaster.SafeFile(root,file.RelativePath);using var input=File.OpenRead(path);if(input.Length!=file.Bytes||Convert.ToHexStringLower(SHA256.HashData(input))!=file.Sha256)throw new IOException("Original source hash differs; no genuine acceptance recorded.");}
            var source=await science.ImportSourceAsync(old.Manifest,root,reuseExisting:true);var area=source.Manifest.Acquisition?.RequestedArea??throw new IOException("This workload requires retained official provider provenance and its exact authorized area.");
            automatic.Configure(new(false,false,PreferredDateUtc:source.Manifest.ObservationUtc?.AddSeconds(1),LookbackDays:3650,MaximumCloudPercent:100));
            automatic.Prepare(area,[source.Manifest.Kind],true);var deadline=DateTimeOffset.UtcNow.AddSeconds(30);ScientificDataReadiness state;
            do{await Task.Delay(25);state=automatic.Snapshot().First(s=>s.Kind==source.Manifest.Kind&&s.Area==area);}while(state.Status is "Queued" or "Downloading"&&DateTimeOffset.UtcNow<deadline);
            if(state.Status is not("Ready" or "Partially usable")||state.DownloadedBytes!=0)throw new IOException("Existing offline source reuse did not complete without a transfer: "+state.Message);
            var points=Enumerable.Range(0,576).Select(i=>new Coordinates(area.South+(area.North-area.South)*(i/24+.5)/24,area.West+(area.East-area.West)*(i%24+.5)/24)).ToArray();
            var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,points.Select(raster.Read).ToArray(),"Fresh reads from verified retained genuine original bytes",raster.Grid);}).ToArray();var derived=RemoteCalculations.Apply(source.Manifest.Kind,bands);
            var measurement=source.Manifest.Kind==RemoteKind.Terrain?SurveyMeasurement.TerrainElevation:SurveyMeasurement.LandSurfaceTemperature;
            var card=missions.SaveResearchCard(new("Genuine retained survey / synthetic flight context",null,Survey:new(1,[measurement])));var plan=missions.CreateResearchPlan(card.Id,1);var at=DateTimeOffset.UtcNow.AddSeconds(-10);
            var frame=new TelemetryFrame(at,TelemetrySource.Simulated,"Synthetic position, genuine public source; no MSFS device acceptance",area.Center.Latitude,area.Center.Longitude,3000,1000,100,100,100,0,0,0,0,0);
            var flight=missions.Create("Retained genuine source survey",frame,at,plan:plan);missions.Append(flight,frame);missions.Finish(flight,MissionStatus.Completed,at.AddSeconds(2),"Bounded genuine-source adapter verification");
            var dataset=science.EnsureDataset(missions.Detail(flight)!.Mission,new(SurveyResearch.Instrument(measurement),true),missions.Research(flight));var id=Guid.NewGuid();
            var product=new RemoteProduct(id,dataset.Id,science.LibraryId,flight,SurveyResearch.Instrument(measurement),source.Id,at.AddSeconds(1),DateTimeOffset.UtcNow,source.Manifest.ObservationUtc,new(area.Center,area.Corners,100,"EPSG:4326","Synthetic aircraft context; original source datum retained","Bounded original numeric workload",[]),$"Products/{flight:N}/{id:N}.json",0,"",24,24,source.Manifest.Kind==RemoteKind.Terrain?["elevation"]:["temperature"],"Actual retained public source, synthetic flight context","Original numeric QA retained",[]);
            product=science.Publish(new(1,product,source,points,bands,derived,["Genuine original numeric reads; not a new provider or installed flight acceptance"]));
            var result=missions.EvaluateResearchAttempt(new(Guid.NewGuid(),flight,at,at.AddSeconds(2),CardId:card.Id),science:science);var review=missions.ReviewSurveyEvidence(result,science);
            if(!review.MatchesSavedEvidence)throw new IOException("Fresh genuine-source result failed independent retained-evidence reopening.");
            if(source.Manifest.Kind==RemoteKind.Terrain&&SurveyResearch.TerrainDatum(source.Manifest.VerticalDatum) is null){if(result.Outcome!=ResearchAttemptOutcome.InsufficientEvidence)throw new IOException("Unknown original datum falsely qualified.");}
            else if(result.Outcome!=ResearchAttemptOutcome.CollectionComplete)throw new IOException("Source-qualified survey did not meet its original sample/location requirements: "+result.Explanation);
            numerics.Add(new{workflow=source.Manifest.Acquisition!.Workflow,source,locations=points,bands,derived,valid=derived.Values.Sum(v=>v.Count(x=>x.HasValue))});summaries.Add(new{source.Id,source.Manifest.Name,source.Manifest.VerticalDatum,originalFiles=source.Files,readiness=state,observedTransferBytes=0,result,review});
            Console.WriteLine(source.Manifest.Name+": "+result.Outcome+" / "+result.Survey!.Objectives[0].ValidSamples+" unique qualified samples; transfer bytes 0");
        }
        if(summaries.Count<3)throw new IOException("Expected retained genuine USGS, Copernicus and Landsat inputs; no incomplete workload is marked successful.");
        await File.WriteAllTextAsync(Path.Combine(output,"provider-acceptance.json"),JsonSerializer.Serialize(new{evidenceClass="Genuine retained public originals; offline numeric comparison workload only, not a new network-provider pass",results=numerics},WireJson.Options));
        var files=Directory.EnumerateFiles("src/AeroScience.Missions","*.cs").Concat(Directory.EnumerateFiles("src/AeroScience.Shared","*.cs")).Order().ToDictionary(f=>f,f=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));
        await File.WriteAllTextAsync(Path.Combine(output,"survey-acceptance.json"),JsonSerializer.Serialize(new{version=1,evidenceClass="Genuine retained original sources + real store/application assessment; synthetic aircraft positions, no installed MSFS",newProviderNetworkAcceptance=false,sourceFiles=files,results=summaries},WireJson.Options));
    }
}
