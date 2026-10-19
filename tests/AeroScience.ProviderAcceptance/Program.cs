using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

// Explicit opt-in network acceptance. These are real provider downloads, never
// replaced with synthetic rasters or mocked catalogs when a provider fails.
if(!args.Contains("--network"))throw new ArgumentException("Pass --network to authorize bounded real provider queries/downloads for public test areas.");
var output=Path.GetFullPath(args.SkipWhile(a=>a!="--output").Skip(1).FirstOrDefault()??Path.Combine(Path.GetTempPath(),"aeroscience-provider-acceptance-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);var library=Path.Combine(output,"ScienceLibrary");Directory.CreateDirectory(library);
PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{formatVersion=1,libraryId=Guid.NewGuid()});
var missions=new MissionStore(Path.Combine(output,"missions.db"));missions.Initialize();var science=new ScienceStore(library);science.Initialize();
var management=new StorageManagement(output,missions,science);science.Management=management;
var settings=new ScientificAccessSettings(output,()=>true);settings.Save(new(true,true,MaximumDownloadBytes:67108864,PreferredDateUtc:DateTimeOffset.Parse("2024-09-10T00:00:00Z"),LookbackDays:120));
if(args.Contains("--envi")){await GuidedEnviAcceptance.Run(args[Array.IndexOf(args,"--envi")+1],output,science,missions);return;}
using var auto=new AutomaticScientificData(science,settings);
var cases=new[]{("usgs-3dep",RemoteKind.Terrain,new Coordinates(40.005,-74.995)),("landsat-c2-l2",RemoteKind.Thermal,new Coordinates(40.005,-74.995)),("sentinel-2-l2a",RemoteKind.Multispectral,new Coordinates(40.005,-74.995)),("cop-dem-glo-30",RemoteKind.Terrain,new Coordinates(46.105,8.105))};
var results=new List<object>();
foreach(var (name,kind,point) in cases)
{
    if(args.Contains("--only")&&args[Array.IndexOf(args,"--only")+1]!=name)continue;
    Console.WriteLine("Preparing real provider "+name);var area=GeographicBounds.Around(point);var queued=auto.Prepare(area,[kind],true).First(s=>s.Kind==kind&&s.Area==area);
    ScientificDataReadiness state;var deadline=DateTimeOffset.UtcNow.AddMinutes(13);
    do{await Task.Delay(1000);state=auto.Snapshot().First(s=>s.Key==queued.Key);}while(state.Status is "Queued" or "Downloading"&&DateTimeOffset.UtcNow<deadline);
    Console.WriteLine(JsonSerializer.Serialize(state,WireJson.Options));
    if(state.Status!="Ready"||state.SourceId is null)throw new IOException(name+" genuine acceptance failed: "+state.Message);
    var source=science.Sources().Single(s=>s.Id==state.SourceId);var root=Path.Combine(library,source.RelativeDirectory);
    var locations=Enumerable.Range(0,24*24).Select(i=>new Coordinates(area.South+(area.North-area.South)*(i/24+.5)/24,area.West+(area.East-area.West)*(i%24+.5)/24)).ToArray();
    var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(root,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Nearest original pixel",raster.Grid);}).ToArray();
    var derived=RemoteCalculations.Apply(kind,bands);var valid=derived.Values.SelectMany(v=>v).Count(v=>v.HasValue);
    if(valid==0)throw new IOException(name+" downloaded but has no valid QA-screened scientific samples at the acceptance area.");
    using var research=new ResearchOperations(missions,science);var coordinator=new MissionCoordinator(missions,1000,research:research);
    var instrument=RemoteAcquisition.Instruments.Single(p=>p.Value==kind).Key;
    // A synthetic flight position drives the production mission/product pipeline.
    // It is explicitly labeled test input, never evidence of a Windows/MSFS flight.
    var mission=coordinator.Start(new("Genuine provider / synthetic position — "+name,Instruments:[new(instrument,true)]));
    var samplePoint=locations[Array.FindIndex(derived.Values.First(),v=>v.HasValue)];
    var now=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(now,TelemetrySource.Simulated,"Provider acceptance synthetic flight position",samplePoint.Latitude,samplePoint.Longitude,1000,100,100,100,100,0,0,0,0,0);
    coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Genuine external dataset / synthetic flight input",now),frame));
    for(var i=0;i<200&&science.Products(mission.Id).Length==0;i++)await Task.Delay(50);
    var product=science.Products(mission.Id).Single();var evidence=science.ReadProduct(product.Id);coordinator.End(mission.Id);
    if(evidence.Source.Id!=source.Id)throw new IOException("Published product did not retain the acquired source identity.");
    var result=new{workflow=name,state,source,publishedProduct=product,aircraft="No MSFS; WGS84 sample grid used for provider acceptance",locations,bands,derived,valid};results.Add(result);
    await File.WriteAllTextAsync(Path.Combine(output,name+"-acceptance.json"),JsonSerializer.Serialize(result,WireJson.Options));
    Console.WriteLine(name+" passed: "+valid+" valid derived samples, "+state.DownloadedBytes+" network bytes");
}
if(args.Contains("--hyperspectral"))
{
    var area=GeographicBounds.Around(new(34.25,-118.45));await auto.DiscoverHyperspectralMetadata(area,default);
    ScientificDataReadiness state;do{await Task.Delay(1000);state=auto.Snapshot().First(s=>s.Kind==RemoteKind.Hyperspectral);}while(state.Status is "Queued" or "Downloading");
    if(state.Status is not("Limited Coverage" or "Unavailable"))throw new IOException("Hyperspectral discovery must not advertise automatic preparation as ready.");
    await File.WriteAllTextAsync(Path.Combine(output,"hyperspectral-discovery.json"),JsonSerializer.Serialize(state,WireJson.Options));Console.WriteLine("Hyperspectral discovery (no preparation): "+state.Message);
}
await File.WriteAllTextAsync(Path.Combine(output,"provider-acceptance.json"),JsonSerializer.Serialize(new{formatVersion=1,checkedUtc=DateTimeOffset.UtcNow,windowsMsfs="not tested",results},WireJson.Options));
Console.WriteLine("Genuine source acceptance saved: "+output);
