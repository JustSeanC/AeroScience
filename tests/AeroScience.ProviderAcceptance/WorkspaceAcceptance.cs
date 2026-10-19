using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

public static class WorkspaceAcceptance
{
    public static async Task Run(string output,ScienceStore science,MissionStore missions,bool includeGuided=false)
    {
        var results=new List<object>();
        foreach(var mission in missions.List(100))
        {
            var product=science.Products(mission.Id,1).SingleOrDefault();if(product is null)continue;
            var data=science.ReadProduct(product.Id);var source=data.Source;
            if(source.Manifest.Acquisition is null&&!includeGuided)continue;
            var band=source.Manifest.Kind switch{RemoteKind.Thermal=>"temperature",RemoteKind.Multispectral=>"ndvi",RemoteKind.Hyperspectral=>data.Bands.First(b=>b.WavelengthNm is not null&&b.Values.Any(v=>v.HasValue)).Id,_=>"elevation"};
            var expected=ScientificSupport.Band(data,band);
            var plan=new MissionPlan(Guid.NewGuid(),"Synthetic acceptance footprint region",SurveyAreas:[new("Recorded footprint",product.Footprint.Boundary)]);
            var coverage=ScientificCoverage.Calculate(science,plan,mission.Id,new(product.InstrumentId,band));
            if(!coverage.Sources.Any(s=>s.ValidSupportSquareMetres>0))throw new IOException("Genuine saved values did not produce scientific support.");
            var artifacts=new List<ScientificArtifact>();
            foreach(var mode in new[]{"mission-samples","retained-source"})
                artifacts.AddRange(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,band,mode)));
            if(source.Manifest.Kind==RemoteKind.Multispectral)
                foreach(var view in new[]{"natural-color","false-color"})artifacts.AddRange(ScientificComposites.Build(science,missions,mission.Id,new(source.Id,view)));
            foreach(var artifact in artifacts)
            {
                var path=science.VerifiedArtifactPath(artifact);using var raster=new ScientificRaster(Path.GetDirectoryName(path)!,new("check",Path.GetFileName(path),"geotiff"));
                if(!ScientificRaster.GridCoverage(raster.Grid).All(p=>MissionPlanning.Position(p.Latitude,p.Longitude)))throw new IOException("Exported raster is not geographically resolvable.");
                var preview=ScientificArtifactPreview.Create(science,artifact);if(preview.Png.Length<100)throw new IOException("Genuine geographic raster preview is invalid.");
            }
            var package=Path.Combine(output,$"{mission.Id:N}-research.zip");await using(var stream=File.Create(package))await ResearchPackage.WriteAsync(missions,science,mission.Id,stream);
            results.Add(new{source,product,coverage,artifacts,package=Path.GetFileName(package),savedValidSamples=expected.Values.Count(v=>v.HasValue)});
            Console.WriteLine("Genuine retained numeric source → saved science support → native-grid composites → GeoTIFF → geographic preview → research ZIP: "+source.Manifest.Name);
        }
        if(results.Count<1)throw new IOException("No genuine automatic-provider mission exists for workspace acceptance.");
        await File.WriteAllTextAsync(Path.Combine(output,"workspace-acceptance.json"),JsonSerializer.Serialize(new{formatVersion=1,evidenceType="genuine-external-data-and-synthetic-flight-input",checkedUtc=DateTimeOffset.UtcNow,windowsMsfs="not tested",retainedSources=science.Sources(),results},WireJson.Options));
    }
}
