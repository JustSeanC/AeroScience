using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

// Local original verification only. Authentication/download happens through the
// existing Windows assistant; metadata alone never proves authorized NASA origin.
internal static class NasaOriginalAcceptance
{
    public static void Run(string folder,string family,string output)
    {
        if(family is not("classic" or "ng"))throw new ArgumentException("Choose classic or ng independently. EMIT native import is unsupported.");
        folder=Path.GetFullPath(folder);
        var sourceFile=ScientificRaster.SafeFile(folder,"source.json");
        var source=JsonSerializer.Deserialize<ScientificSource>(File.ReadAllText(sourceFile),new JsonSerializerOptions(WireJson.Options){PropertyNameCaseInsensitive=true})??throw new IOException("Missing retained source identity.");
        using var notes=JsonDocument.Parse(source.Manifest.Notes??throw new IOException("NASA catalog provenance is missing."));
        var catalog=notes.RootElement.GetProperty("catalog").Deserialize<ScientificCatalogItem>(WireJson.Options)??throw new IOException("Missing NASA catalog identity.");
        if(catalog.Provider!="nasa-"+family)throw new IOException("Selected family and retained source differ.");
        foreach(var file in source.Files)
        {
            var path=ScientificRaster.SafeFile(folder,file.RelativePath);using var stream=File.OpenRead(path);
            if(stream.Length!=file.Bytes||!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Retained original changed.");
        }
        var header=source.Manifest.Bands[0].HeaderFile??throw new IOException("Original ENVI header is missing.");
        var inspection=NasaGuidedImport.Inspect(ScientificRaster.SafeFile(folder,header),catalog);
        if(inspection.Manifest is null)throw new IOException("The retained original is not a supported complete NASA distribution.");
        if(!inspection.Manifest.Bands.SequenceEqual(source.Manifest.Bands))throw new IOException("Retained spectral metadata differs from the verified original header.");
        var g=source.Grids.Values.First();
        // Choose pixel centers, including the middle sample, so independent CRS
        // transforms cannot ambiguously floor a point on an exact pixel boundary.
        var points=new[]{ScientificRaster.GridPoint(g,.5,.5),ScientificRaster.GridPoint(g,Math.Floor(g.Width/2d)+.5,Math.Floor(g.Height/2d)+.5),ScientificRaster.GridPoint(g,g.Width-.5,g.Height-.5)};
        var samples=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new{b.Id,b.Channel,b.WavelengthNm,b.Unit,b.Scale,b.Offset,b.BadBand,values=points.Select(raster.Read).ToArray()};}).ToArray();
        if(!samples.Any(b=>b.values.Any(v=>v.HasValue)))throw new IOException("No numeric original sample is available at the checked positions; acceptance is not established.");
        var report=new{formatVersion=1,evidenceType="owner-supplied-retained-nasa-source",family,checkedUtc=DateTimeOffset.UtcNow,source,points,samples,
            authorization="Not established by this local numerical check. Retain the actual authorized job/browser receipts separately.",
            windowsMsfs="not tested",flightObservationsCreated=0};
        File.WriteAllText(Path.Combine(output,"nasa-original-samples.json"),JsonSerializer.Serialize(report,WireJson.Options));
        Console.WriteLine(family+": verified retained source metadata/hashes and sampled "+samples.Length+" original channels; independent comparison and owner access evidence remain required.");
    }
}
