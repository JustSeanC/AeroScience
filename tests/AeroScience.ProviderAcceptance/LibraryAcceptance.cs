using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
internal static class LibraryAcceptance
{
    internal static async Task Run(string output,ScienceStore science,ScientificAccessSettings settings,AutomaticScientificData automatic)
    {
        var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);var results=new List<object>();var inspections=new List<object>();
        // Protected NASA browser handoff is checked separately by --nasa; it is
        // not an automatic numeric-download provider. Keep all four automatic adapters.
        foreach(var provider in ScientificCatalog.Providers.Where(p => p.Id is "usgs-3dep" or "copernicus" or "landsat" or "sentinel-2"))
        {
            var area=provider.Id=="copernicus"?new GeographicBounds(8.1,46.1,8.12,46.12):new(-75,40,-74.98,40.02);
            var from=DateTimeOffset.Parse("2024-09-03T00:00:00Z");var to=from.AddDays(1);
            var page=await catalog.Search(new(provider.Id,area,provider.SceneDates?from:null,provider.SceneDates?to:null,PageSize:20));
            var item=page.Items.OrderByDescending(i=>i.Scene.Contains("18TWK")).FirstOrDefault()??throw new IOException(provider.Id+" catalog unavailable: "+page.Error);
            var selection=new ScientificLibrarySelection(1,[area],[new(item.Provider,item.Scene,item.Revision,item.Bands.Select(b=>b.Id).ToArray())]);
            var preview=await jobs.Preview(selection);Console.WriteLine(provider.Id+" preview "+preview.Preview.Work.Length+" units / "+item.Scene);
            await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var until=DateTimeOffset.UtcNow.AddMinutes(13);LibraryDownloadJob current;
            do{await Task.Delay(500);current=science.LibraryJob(preview.Id);}while(current.State is "Queued" or "Preparing" or "Downloading" or "Verifying"&&DateTimeOffset.UtcNow<until);
            if(current.State!="Completed")throw new IOException(provider.Id+" Library job "+current.State+": "+current.Error);
            var source=science.Sources().Single(s=>s.Id==current.Items.Single().SourceId);var folder=Path.Combine(science.DirectoryPath,source.RelativeDirectory);
            var locations=Enumerable.Range(0,24*24).Select(i=>new Coordinates(area.South+(area.North-area.South)*(i/24+.5)/24,area.West+(area.East-area.West)*(i%24+.5)/24)).ToArray();
            var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Genuine retained source; no active flight",raster.Grid);}).ToArray();
            var derived=RemoteCalculations.Apply(source.Manifest.Kind,bands);var valid=derived.Values.SelectMany(v=>v).Count(v=>v.HasValue);
            if(valid==0)throw new IOException(provider.Id+" downloaded but no QA-valid numeric samples exist in this area.");
            var reused=await jobs.Preview(selection);await jobs.Approve(reused.Id,reused.Preview.SelectionHash);until=DateTimeOffset.UtcNow.AddSeconds(15);
            do{await Task.Delay(100);current=science.LibraryJob(reused.Id);}while(current.State is not("Completed" or "Failed")&&DateTimeOffset.UtcNow<until);
            if(current.State!="Completed"||current.DownloadedBytes!=0)throw new IOException("Exact retained reuse failed.");
            var inspectBand=source.Manifest.Kind==RemoteKind.Multispectral?"ndvi":source.Manifest.Bands.First(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).Id;
            inspections.Add(new{source,inspection=science.InspectSource(source.Id,new(area,inspectBand),s=>automatic.VerifyRetainedSource(s))});
            var evidence=new{provider=provider.Id,catalog=page,preview=preview.Preview,source,locations,bands,derived,valid,reuse=current,flight="No flight or science session was created. Numeric Library preparation only."};
            results.Add(evidence);await File.WriteAllTextAsync(Path.Combine(output,provider.Id+"-library.json"),JsonSerializer.Serialize(evidence,WireJson.Options));Console.WriteLine(provider.Id+" Library acquisition/reuse passed; "+valid+" valid derived values.");
        }
        await File.WriteAllTextAsync(Path.Combine(output,"source-inspections.json"),JsonSerializer.Serialize(new{inspections},WireJson.Options));
        await CumulativeReuse(output,science,settings,jobs,catalog);
        var regions=new[]{await catalog.Region(new("35")),await catalog.Region(new("35","013"))};
        await File.WriteAllTextAsync(Path.Combine(output,"regions.json"),JsonSerializer.Serialize(regions,WireJson.Options));Console.WriteLine("Genuine Census state/county boundaries retrieved without alteration.");
        var paging=new ScientificCatalogQuery("sentinel-2",new(-75,40,-74.98,40.02),DateTimeOffset.Parse("2024-09-03T00:00:00Z"),DateTimeOffset.Parse("2024-09-04T00:00:00Z"),PageSize:1,Refresh:true);
        var first=await catalog.Search(paging);if(first.NextCursor is null)throw new IOException("Expected actual Sentinel pagination continuation at this known overlapping-tile region.");
        var next=await catalog.Search(paging with{Cursor=first.NextCursor});if(next.Items.Length==0)throw new IOException("Genuine provider continuation returned no scene metadata.");
        await File.WriteAllTextAsync(Path.Combine(output,"pagination.json"),JsonSerializer.Serialize(new{first,next},WireJson.Options));Console.WriteLine("Genuine Sentinel next-link pagination passed.");
        if(science.Datasets().Count!=0||science.Products().Length!=0)throw new IOException("Library downloads unexpectedly produced flight observations.");
        await File.WriteAllTextAsync(Path.Combine(output,"library.json"),JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,results},WireJson.Options));
    }
    private static async Task CumulativeReuse(string output,ScienceStore science,ScientificAccessSettings settings,ScientificLibraryJobs jobs,ScientificCatalog catalog)
    {
        var area=new GeographicBounds(-75.04,40,-75.02,40.02);var left=new GeographicBounds(area.West,area.South,-75.03,area.North);var right=new GeographicBounds(-75.03,area.South,area.East,area.North);
        var item=(await catalog.Search(new("usgs-3dep",area))).Items.Single();var scene=new ScientificSceneSelection(item.Provider,item.Scene,item.Revision,item.Bands.Select(b=>b.Id).ToArray());
        var preview=await jobs.Preview(new(1,[left,right],[scene],MaximumTransferBytes:16777216));await jobs.Approve(preview.Id,preview.Preview.SelectionHash);
        async Task<LibraryDownloadJob> Complete(Guid id){var end=DateTimeOffset.UtcNow.AddMinutes(13);while(true){var current=science.LibraryJob(id);if(current.State is "Completed" or "Failed" or "Interrupted")return current;if(DateTimeOffset.UtcNow>end)throw new IOException("Cumulative genuine reuse exceeded its existing bounded job deadline.");await Task.Delay(100);}}
        var acquired=await Complete(preview.Id);if(acquired.State!="Completed"||acquired.DownloadedBytes<=0)throw new IOException("Genuine bounded cumulative-source acquisition did not complete: "+acquired.Error);
        var ids=acquired.Items.Select(i=>i.SourceId!.Value).Distinct().ToArray();if(ids.Length!=2)throw new IOException("This acceptance requires two independently retained genuine windows.");
        var count=science.CountSources();var before=settings.Get().Preferences;LibraryDownloadJob repeat;
        settings.Save(before with{OnlineEnabled=false,PrivacyAccepted=false});
        try
        {
            var combined=await jobs.Preview(new(1,[area],[scene],MaximumTransferBytes:16777216));
            if(combined.Preview.EstimatedTransferBytes!=0||combined.Preview.Work.Any(w=>w.ReuseSourceId is null)||combined.Preview.Work.Select(w=>w.ReuseSourceId!.Value).Distinct().Count()!=2)throw new IOException("Both genuine windows were not recognized as complete numeric union support.");
            await jobs.Approve(combined.Id,combined.Preview.SelectionHash);repeat=await Complete(combined.Id);
            if(repeat.State!="Completed"||repeat.DownloadedBytes!=0||science.CountSources()!=count)throw new IOException("Offline cumulative reuse transferred bytes or created duplicate originals.");
        }
        finally{settings.Save(before);}
        // Fresh read-only persistence handle, not falsely described as an installed restart test.
        using(var reopened=new ScienceStore(science.DirectoryPath))if(reopened.LibraryJob(repeat.Id).State!="Completed"||ids.Any(id=>reopened.Source(id) is null))throw new IOException("Offline retained job/source reopening failed.");
        var results=new List<object>();foreach(var id in ids)
        {
            var source=science.Source(id)!;var region=source.Manifest.Acquisition!.RequestedArea;var folder=Path.Combine(science.DirectoryPath,source.RelativeDirectory);
            var locations=Enumerable.Range(0,576).Select(i=>new Coordinates(region.South+(region.North-region.South)*(i/24+.5)/24,region.West+(region.East-region.West)*(i%24+.5)/24)).ToArray();
            var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Genuine retained native value; source inspection only, no flight",raster.Grid);}).ToArray();var derived=RemoteCalculations.Apply(source.Manifest.Kind,bands);var valid=derived.Values.SelectMany(v=>v).Count(v=>v.HasValue);
            if(valid!=576)throw new IOException("The genuine cumulative fixture does not have full checked sample support; no pass substituted.");
            results.Add(new{provider="usgs-3dep",source,locations,bands,derived,valid});
        }
        await File.WriteAllTextAsync(Path.Combine(output,"library-reuse.json"),JsonSerializer.Serialize(new{evidenceClass="Genuine bounded USGS originals and actual Library jobs; independent rasterio verification required",area,initial=acquired,repeat,observedInitialTransferBytes=acquired.DownloadedBytes,repeatedTransferBytes=repeat.DownloadedBytes,reopened="Fresh read-only persistence handle; deterministic full host/process recovery tested separately",results},WireJson.Options));
        Console.WriteLine("Genuine two-window numeric union: initial "+acquired.DownloadedBytes+" bytes, identical-area offline repeat "+repeat.DownloadedBytes+" bytes; no duplicated original or flight observation.");
    }
}
