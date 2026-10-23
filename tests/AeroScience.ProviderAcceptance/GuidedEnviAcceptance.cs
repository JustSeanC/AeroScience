using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;

internal static class GuidedEnviAcceptance
{
    // This terminal harness branch relinquishes its initial Library owner before
    // acquiring a fresh offline owner. Production ownership rules are unchanged.
    public static async Task Run(string header,string output,ScienceStore science,MissionStore missions,bool workspace=false)
    {
        var inspection=GuidedHyperspectralImport.Inspect(header,new(Provider:"NASA AVIRIS / NV5 Cuprite reflectance tutorial",Source:"https://www.nv5geospatialsoftware.com/docs/HyperspectralAnalysisTutorial.html",License:"Publicly downloadable NV5 tutorial dataset; consult original usage terms. Scientific files are not redistributed by AeroScience."));
        var manifest=inspection.Manifest??throw new IOException("Genuine ENVI example is not compatible.");
        var source=await science.ImportSourceAsync(manifest,inspection.Root,reuseExisting:true);Console.WriteLine("Imported genuine ENVI: "+source.Manifest.Name+" / "+source.Manifest.Bands.Length+" bands");
        var g=source.Grids.Values.First();var center=ScientificRaster.GridPoint(g,g.Width/2d,g.Height/2d);
        var fileRoot=Path.Combine(science.DirectoryPath,source.RelativeDirectory);
        var spectrum=new List<object>();
        foreach(var band in source.Manifest.Bands){using var raster=new ScientificRaster(fileRoot,band);spectrum.Add(new{band.Channel,band.WavelengthNm,band.Unit,band.BadBand,Value=raster.Read(center)});}
        RemoteProduct product;RemoteProductData data;
        using(var research=new ResearchOperations(missions,science))
        {
        var coordinator=new MissionCoordinator(missions,1000,research:research);
        var mission=coordinator.Start(new("Genuine AVIRIS ENVI / synthetic test aircraft",Instruments:[new("hyperspectral-imager",true)]));
        var now=DateTimeOffset.UtcNow;var frame=new TelemetryFrame(now,TelemetrySource.Simulated,"Synthetic test position — not MSFS",center.Latitude,center.Longitude,1000,100,100,100,100,0,0,0,0,0);
        coordinator.Apply(new(new(ConnectionState.Connected,TelemetrySource.Simulated,"Genuine ENVI / synthetic flight position",now),frame));
        for(var i=0;i<400&&science.Products(mission.Id).Length==0;i++)await Task.Delay(50);
        product=science.Products(mission.Id).Single();data=science.ReadProduct(product.Id);coordinator.End(mission.Id);
        if(!data.Bands.Any(b=>b.Values.Any(v=>v.HasValue)))throw new IOException("No genuine spectral samples were recorded.");
        }
        // Reopen the stored library without network access and revalidate the retained source/product.
        science.Dispose();
        using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();missions.ScienceLibrary=reopened;
        reopened.Management=new StorageManagement(output,missions,reopened);
        var retained=reopened.Availability(center).Single(s=>s.Source.Id==source.Id);if(!retained.FilesAvailable||retained.CoversPoint!=true)throw new IOException("Offline source reuse failed.");
        var recovered=reopened.ReadProduct(product.Id);
        if(recovered.Product.Sha256!=product.Sha256||!JsonSerializer.Serialize(recovered,WireJson.Options).Equals(JsonSerializer.Serialize(data,WireJson.Options),StringComparison.Ordinal))throw new IOException("Offline product evidence changed.");
        var again=await reopened.ImportSourceAsync(manifest,inspection.Root,reuseExisting:true);if(again.Id!=source.Id)throw new IOException("Guided duplicate import failed.");
        foreach(var file in source.Files){using var original=File.OpenRead(Path.Combine(inspection.Root,file.RelativePath));if(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant()!=file.Sha256)throw new IOException("External original changed.");}
        var result=new{formatVersion=1,evidenceType="genuine-downloaded-envi",checkedUtc=DateTimeOffset.UtcNow,windowsMsfs="not tested",flightInput="synthetic test position; not MSFS",inspection=inspection with{Header=Path.GetFileName(inspection.Header),Root="[external original folder]"},source,center,spectrum,publishedProduct=product,validSamples=data.Bands.Sum(b=>b.Values.Count(v=>v.HasValue)),offlineReopen=true,duplicateReused=true,externalOriginalsPreserved=true};
        await File.WriteAllTextAsync(Path.Combine(output,"guided-envi-acceptance.json"),JsonSerializer.Serialize(result,WireJson.Options));Console.WriteLine("Genuine guided ENVI import, mission product and offline reuse passed.");
        if(workspace)await WorkspaceAcceptance.Run(output,reopened,missions,true);
    }
}
