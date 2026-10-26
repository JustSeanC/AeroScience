using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Tests;

// Synthetic originals and saved products only. Never production or genuine-source acceptance.
public static class SurveyFixture
{
    public static async Task<ScientificSource> Source(ScienceStore science,string folder,RemoteKind kind,string datum="NAVD88",bool cloudy=false)
    {
        var path=RemoteFixtureFiles.Create(folder,kind,cloudy);var manifest=ScienceStore.ReadSourceManifest(path);
        if(kind==RemoteKind.Thermal){RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"QA_RADSAT.tif"),0);RemoteFixtureFiles.GeoTiff(Path.Combine(folder,"ST_QA.tif"),100);
            manifest=manifest with{SceneId="LC08_L2SP_014032_20240903_20240911_02_T1",ObservationUtc=DateTimeOffset.Parse("2024-09-03T15:00:00Z"),Bands=[..manifest.Bands,new("QA_RADSAT","QA_RADSAT.tif","geotiff"),new("ST_QA","ST_QA.tif","geotiff",Unit:"K",Scale:.01)]};}
        else manifest=manifest with{VerticalDatum=datum};
        return await science.ImportSourceAsync(manifest,folder);
    }
    public static RemoteProduct Publish(ScienceStore science,MissionStore missions,Guid flight,ScientificSource source,DateTimeOffset at,int variant=0,Func<RemoteProductData,RemoteProductData>? change=null)
    {
        var instrument=source.Manifest.Kind==RemoteKind.Terrain?"terrain-scanner":"thermal-imager";var mission=missions.Detail(flight)!.Mission;var dataset=science.EnsureDataset(mission,new(instrument,true),missions.Research(flight));
        Coordinates[] points=[new(40.001,-75.004),new(40.001,-75.001),new(40.001,-74.998)];var folder=Path.Combine(science.DirectoryPath,source.RelativeDirectory);
        var bands=source.Manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,points.Select(raster.Read).ToArray(),"Synthetic original numeric sampling",raster.Grid);}).ToArray();var derived=RemoteCalculations.Apply(source.Manifest.Kind,bands);
        var id=Guid.NewGuid();var footprint=new ObservationFootprint(points[1],points,100,"EPSG:4326","Synthetic fixture plane","Not a real flight",[]);
        var product=new RemoteProduct(id,dataset.Id,science.LibraryId,flight,instrument,source.Id,at.AddSeconds(variant),DateTimeOffset.UtcNow,source.Manifest.ObservationUtc,footprint,$"Products/{flight:N}/{id:N}.json",0,"",3,1,source.Manifest.Kind==RemoteKind.Terrain?["elevation"]:["temperature"],"SYNTHETIC test product","QA retained",[]);
        var data=new RemoteProductData(1,product,source,points,bands,derived,["Synthetic fixture; not genuine provider evidence"]);return science.Publish(change?.Invoke(data)??data);
    }
}
