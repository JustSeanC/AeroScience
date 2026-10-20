using System.Diagnostics;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
public static class ConcurrencyAcceptance
{
    public static async Task Run(string output)
    {
        var results=new List<object>();
        foreach(var slots in new[]{1,2,4,8})
        {
            var folder=Path.Combine(output,"slots-"+slots);Directory.CreateDirectory(folder);var library=Path.Combine(folder,"ScienceLibrary");Directory.CreateDirectory(library);
            PreferenceStore.WriteAtomic(Path.Combine(library,StorageManager.LibraryManifest),new{formatVersion=1,libraryId=Guid.NewGuid()});
            using var science=new ScienceStore(library);science.Initialize();var settings=new ScientificAccessSettings(folder,()=>true);
            settings.Save(new(true,true,ConcurrentDownloads:slots,PreferredDateUtc:DateTimeOffset.Parse("2024-09-10T00:00:00Z"),LookbackDays:120,PreflightTransferBytes:536870912));
            using var automatic=new AutomaticScientificData(science,settings);var watch=Stopwatch.StartNew();using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;long peak=0;
            var jobs=new List<string>();
            foreach(var (kind,area) in new[]{(RemoteKind.Terrain,new GeographicBounds(-75,40,-74.98,40.02)),(RemoteKind.Thermal,new(-75,40,-74.98,40.02)),(RemoteKind.Multispectral,new(-75,40,-74.98,40.02)),(RemoteKind.Terrain,new(-74.96,40,-74.94,40.02)),(RemoteKind.Thermal,new(-74.96,40,-74.94,40.02)),(RemoteKind.Multispectral,new(-74.96,40,-74.94,40.02)),(RemoteKind.Terrain,new(-74.92,40,-74.90,40.02)),(RemoteKind.Thermal,new(-74.92,40,-74.90,40.02))})
            {automatic.Prepare(area,[kind],true,scope:"plan:concurrency-acceptance");jobs.Add(AutomaticScientificData.AreaKey(kind,area));}
            ScientificDataReadiness[] states;
            do{await Task.Delay(250);process.Refresh();peak=Math.Max(peak,process.WorkingSet64);states=automatic.Snapshot().Where(s=>jobs.Contains(s.Key)).ToArray();}while(states.Any(s=>s.Status is "Queued" or "Downloading")&&watch.Elapsed<TimeSpan.FromMinutes(12));
            var valid=new Dictionary<string,int>();foreach(var source in science.Sources())
            {var area=source.Manifest.Acquisition!.RequestedArea;var locations=Enumerable.Range(0,81).Select(i=>new Coordinates(area.South+(area.North-area.South)*(i/9+.5)/9,area.West+(area.East-area.West)*(i%9+.5)/9)).ToArray();var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(Path.Combine(library,source.RelativeDirectory),b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Genuine provider benchmark, not aircraft collection",raster.Grid);}).ToArray();valid[source.Id.ToString()]=RemoteCalculations.Apply(source.Manifest.Kind,bands).Values.SelectMany(v=>v).Count(v=>v.HasValue);}
            var result=new{slots,seconds=watch.Elapsed.TotalSeconds,cpuSeconds=(process.TotalProcessorTime-cpu).TotalSeconds,peakWorkingSetBytes=peak,retainedBytes=science.Sources().Sum(s=>s.Files.Sum(f=>f.Bytes)),networkBytes=states.Sum(s=>s.DownloadedBytes),completed=states.Count(s=>s.Status=="Ready"),requested=jobs.Count,validSamples=valid.Values.Sum(),allPassed=states.Length==jobs.Count&&states.All(s=>s.Status=="Ready")&&valid.Values.All(n=>n>0),states,network=automatic.NetworkDiagnostics(),windowsMsfs="not tested"};
            results.Add(result);await File.WriteAllTextAsync(Path.Combine(output,"concurrency-acceptance.json"),JsonSerializer.Serialize(results,WireJson.Options));Console.WriteLine($"{slots} slots: {result.completed}/{jobs.Count} ready; {result.seconds:0.0}s; {result.networkBytes} bytes; {result.peakWorkingSetBytes/1048576} MiB peak; pass={result.allPassed}");
        }
    }
}
