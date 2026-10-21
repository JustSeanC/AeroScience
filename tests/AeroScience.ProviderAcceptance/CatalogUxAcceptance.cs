using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
internal static class CatalogUxAcceptance
{
    public static async Task Run(string output,ScientificCatalog catalog)
    {
        var checks=new List<object>();var failures=new List<string>();
        var area=new GeographicBounds(-119,33,-117,35);var from=DateTimeOffset.Parse("2024-12-01T00:00:00Z");var to=DateTimeOffset.Parse("2024-12-31T23:59:59Z");
        foreach(var provider in new[]{"landsat","sentinel-2"})foreach(var sort in new[]{"latest","oldest","cloud"}){
            var q=new ScientificCatalogQuery(provider,area,from,to,PageSize:3,Refresh:true,Sort:sort);
            try{var page=await catalog.Search(q);var okay=page.Status=="Available"&&page.Items.Length>0&&page.ServerOrdered;
                checks.Add(new{query=q,passed=okay,page});if(!okay)failures.Add(provider+" "+sort+": "+page.Status+" / "+page.Error);
                if(sort=="latest"&&page.NextCursor is not null){var next=await catalog.Search(q with{Cursor=page.NextCursor});var unique=!page.Items.Select(i=>i.Scene).Intersect(next.Items.Select(i=>i.Scene)).Any();checks.Add(new{provider,check="next-page",passed=next.Items.Length>0&&unique,page=next});if(next.Items.Length==0||!unique)failures.Add(provider+" continuation");}
            }catch(Exception e){checks.Add(new{provider,sort,passed=false,error=e.Message});failures.Add(provider+" "+sort);}
        }
        foreach(var (provider,filter) in new[]{("nasa-classic","f080611t01p00r13"),("nasa-ng","ang20140621t190550")})try{
            var q=new ScientificCatalogQuery(provider,provider=="nasa-classic"?area:new(-115.6,35.4,-115.4,35.6),SceneFilter:filter,PageSize:5,Refresh:true);
            var page=await catalog.Search(q);var okay=page.Status=="Available"&&page.Items.Any(i=>i.ObservationUtc?.Year<2024);checks.Add(new{query=q,passed=okay,page});if(!okay)failures.Add(provider+" historical default discovery");
        }catch(Exception e){checks.Add(new{provider,passed=false,error=e.Message});failures.Add(provider);}
        try{var states=await catalog.RegionNames(new());var counties=await catalog.RegionNames(new("35"));var okay=states.Items.Any(i=>i.Name=="New Mexico"&&i.Code=="35")&&counties.Items.Any(i=>i.Code=="013"&&i.Name.Contains("Ana"));checks.Add(new{check="authoritative-named-regions",passed=okay,states,counties});if(!okay)failures.Add("named regions");}catch(Exception e){checks.Add(new{check="authoritative-named-regions",passed=false,error=e.Message});failures.Add("named regions");}
        await File.WriteAllTextAsync(Path.Combine(output,"catalog-ux.json"),JsonSerializer.Serialize(new{checkedUtc=DateTimeOffset.UtcNow,scope="Real public catalog metadata only; no authorized NASA numeric import, physical or installed acceptance.",checks,failures},new JsonSerializerOptions(WireJson.Options){WriteIndented=true}));
        if(failures.Count>0)throw new IOException("Genuine catalog UX checks failed: "+string.Join(", ",failures));
        Console.WriteLine("Genuine public catalog ordering, continuation, historical NASA metadata and named Census selections passed. No numeric/installed acceptance claimed.");
    }
}
