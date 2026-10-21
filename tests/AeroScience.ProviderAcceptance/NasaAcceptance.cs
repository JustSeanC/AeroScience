using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
internal static class NasaAcceptance
{
    public static async Task Run(string output,ScienceStore science,ScientificAccessSettings settings,AutomaticScientificData automatic)
    {
        var catalog=new ScientificCatalog(automatic,science,settings);var results=new List<object>();
        foreach(var (id,area) in new[]{("nasa-classic",new GeographicBounds(-118.8,34,-118.1,34.6)),("nasa-ng",new GeographicBounds(-115.6,35.4,-115.4,35.6)),("nasa-emit",new GeographicBounds(73,21,74.5,23))})
        {
            var q=new ScientificCatalogQuery(id,area,PageSize:1,Refresh:true);
            var first=await catalog.Search(q);if(first.Status!="Available"||first.Items.Length!=1||first.NextCursor is null)throw new IOException(id+" genuine search failed: "+first.Error);
            var next=await catalog.Search(q with{Cursor=first.NextCursor});if(next.Status is not("Available" or "No match"))throw new IOException("NASA pagination failed.");
            var item=first.Items[0];if(item.Nasa is null||item.Geometry is null||item.ObservationUtc is null||item.Bands.Length!=0)throw new IOException("NASA metadata/capability invariant failed.");
            var empty=await catalog.Search(q with{Area=new(-140,0,-139.99,.01),SceneFilter="AEROSCIENCE_NO_SUCH_FLIGHTLINE_20261021"});if(empty.Status!="No match")throw new IOException("Genuine empty NASA search not established.");
            var cached=await catalog.Search(q with{CachedOnly=true});if(!cached.Cached||cached.AsOfUtc!=first.AsOfUtc)throw new IOException("Offline cached NASA metadata failed.");
            results.Add(new{provider=id,query=q,first,next,empty,cachedAsOf=cached.AsOfUtc});Console.WriteLine(id+" genuine catalog: "+item.Scene+" "+item.ObservationUtc);
        }
        var report=new{checkedUtc=DateTimeOffset.UtcNow,source="Official NASA CMR via production ScientificCatalog/ScientificHttp",results,numericAcceptance="Not established by catalog discovery. Original AVIRIS files and independent values required.",missionCount=0};
        await File.WriteAllTextAsync(Path.Combine(output,"nasa-catalog-acceptance.json"),JsonSerializer.Serialize(report,WireJson.Options));
    }
}
