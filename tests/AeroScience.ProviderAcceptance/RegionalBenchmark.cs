using System.Net;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

public static class RegionalBenchmark
{
    private sealed class Requests:DelegatingHandler
    {
        public int Count;
        public Requests():base(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,MaxResponseDrainSize=0,MaxConnectionsPerServer=2,ConnectTimeout=TimeSpan.FromSeconds(20)}){}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Interlocked.Increment(ref Count);return base.SendAsync(request,token);}
    }
    public static async Task Run(string output,string[] args)
    {
        var results=new List<object>();
        var cases=new[]{("usgs-3dep",RemoteKind.Terrain,new Coordinates(40.005,-74.995)),("landsat-c2-l2",RemoteKind.Thermal,new Coordinates(40.005,-74.995)),("sentinel-2-l2a",RemoteKind.Multispectral,new Coordinates(40.005,-74.995))};
        foreach(var (name,kind,point) in cases)
        foreach(var size in new[]{.02,.04,.06})
        {
            var folder=Path.Combine(output,name+"-"+size);Directory.CreateDirectory(folder);var library=Path.Combine(folder,"ScienceLibrary");Directory.CreateDirectory(library);
            PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{formatVersion=1,libraryId=Guid.NewGuid()});
            var science=new ScienceStore(library);science.Initialize();var settings=new ScientificAccessSettings(folder,()=>true);
            settings.Save(new(true,true,MaximumDownloadBytes:67108864,PreferredDateUtc:DateTimeOffset.Parse("2024-09-10T00:00:00Z"),LookbackDays:120));
            var requests=new Requests();using var http=new ScientificHttp(requests);using var automatic=new AutomaticScientificData(science,settings,http);
            var area=new GeographicBounds(-75,40,-75+size,40+size);var watch=System.Diagnostics.Stopwatch.StartNew();
            ScientificDataReadiness state=automatic.Prepare(area,[kind],true).Single();
            do{await Task.Delay(200);state=automatic.Snapshot().Single();}while(state.Status is "Queued" or "Downloading"&&watch.Elapsed<TimeSpan.FromMinutes(13));
            var firstState=state;var firstRequests=requests.Count;var firstSeconds=watch.Elapsed.TotalSeconds;var valid=0;long retained=0;
            if(state.SourceId is {} id)
            {
                var source=science.Sources().Single(s=>s.Id==id);retained=source.Files.Sum(f=>f.Bytes);
                var locations=Enumerable.Range(0,576).Select(i=>new Coordinates(area.South+size*(i/24+.5)/24,area.West+size*(i%24+.5)/24)).ToArray();
                var bands=source.Manifest.Bands.Select(b=>{using var r=new ScientificRaster(Path.Combine(library,source.RelativeDirectory),b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(r.Read).ToArray(),"Genuine benchmark samples; not flight observations",r.Grid);}).ToArray();
                valid=RemoteCalculations.Apply(kind,bands).Values.SelectMany(v=>v).Count(v=>v.HasValue);
            }
            var repeatWatch=System.Diagnostics.Stopwatch.StartNew();automatic.Prepare(area,[kind],true);do{await Task.Delay(100);state=automatic.Snapshot().Single();}while(state.Status is "Queued" or "Downloading"&&repeatWatch.Elapsed<TimeSpan.FromMinutes(13));
            var repeatRequests=requests.Count-firstRequests;var repeatSeconds=repeatWatch.Elapsed.TotalSeconds;
            // A neighboring small cell challenges scene metadata and validated range reuse.
            var adjacent=new GeographicBounds(area.East,40,area.East+.02,40.02);var before=requests.Count;var adjacentWatch=System.Diagnostics.Stopwatch.StartNew();automatic.Prepare(adjacent,[kind],true);
            ScientificDataReadiness next;do{await Task.Delay(200);next=automatic.Snapshot().Single(s=>s.Area==adjacent);}while(next.Status is "Queued" or "Downloading"&&adjacentWatch.Elapsed<TimeSpan.FromMinutes(13));
            var result=new{provider=name,size,networkBytes=firstState.DownloadedBytes,retainedBytes=retained,firstRequests,firstSeconds,validSamples=valid,totalSamples=576,repeatRequests,repeatSeconds,adjacentBytes=next.DownloadedBytes,adjacentRequests=requests.Count-before,adjacentSeconds=adjacentWatch.Elapsed.TotalSeconds,status=firstState.Status,message=firstState.Message,repeatStatus=state.Status,adjacentStatus=next.Status,adjacentMessage=next.Message,windowsMsfs="not tested"};results.Add(result);
            await File.WriteAllTextAsync(Path.Combine(output,"regional-benchmarks.json"),JsonSerializer.Serialize(results,WireJson.Options));Console.WriteLine(JsonSerializer.Serialize(result,WireJson.Options));science.Dispose();
        }
    }
}
