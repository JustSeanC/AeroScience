using System.Text.Json;
using System.Security.Cryptography;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
// Explicit synthetic numeric cubes exercise real source verification, retention and sampling.
// Official recorded metadata does not turn these fixtures into genuine NASA numeric acceptance.
public sealed class NasaClassicImportTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"nasa-classic-import-"+Guid.NewGuid().ToString("N"));
    internal static ScientificCatalogItem Item(string family)=>NasaFixtureFiles.Item(family);
    internal static string Cube(string root,ScientificCatalogItem item,int bands=224,string interleave="bil",bool bigEndian=false)=>NasaFixtureFiles.Cube(root,item,bands,interleave,bigEndian);
    [Theory][InlineData("bsq",false)][InlineData("bil",false)][InlineData("bip",true)]
    public async Task ClassicOriginalCompanionsPreserveNumericValuesMasksAndOfflineIdentity(string interleave,bool bigEndian)
    {
        var item=Item("classic");var header=Cube(Path.Combine(root,"originals"),item,interleave:interleave,bigEndian:bigEndian);var original=Directory.GetFiles(Path.GetDirectoryName(header)!).ToDictionary(p=>Path.GetFileName(p)!,p=>File.ReadAllBytes(p));
        var i=NasaGuidedImport.Inspect(header,item);Assert.Equal(224,i.BandCount);Assert.Equal(item.ObservationUtc,i.Manifest!.ObservationUtc);Assert.Equal(item.Scene,i.Manifest.SceneId);
        var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();Guid id;
        using(var science=new ScienceStore(storage.ScienceLibraryDirectory)){science.Initialize();var source=await science.ImportSourceAsync(i.Manifest,i.Root,reuseExisting:true);id=source.Id;Assert.Equal(id,(await science.ImportSourceAsync(i.Manifest,i.Root,reuseExisting:true)).Id);}
        using(var science=new ScienceStore(storage.ScienceLibraryDirectory)){science.Initialize();var source=Assert.Single(science.Sources());Assert.Equal(id,source.Id);var folder=Path.Combine(science.DirectoryPath,source.RelativeDirectory);var band=source.Manifest.Bands[100];using var raster=new ScientificRaster(folder,band);var point=ScientificRaster.GridPoint(raster.Grid,.5,.5);Assert.Equal((double)(float).1,raster.Read(point));using var bad=new ScientificRaster(folder,source.Manifest.Bands[5]);Assert.Null(bad.Read(point));Assert.Equal(900,band.WavelengthNm);Assert.Contains("NASA official-browser",source.Manifest.Notes);}
        foreach(var pair in original)Assert.Equal(pair.Value,File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(header)!,pair.Key!)));
    }
    [Theory][InlineData("bands = 224","bands = 223")][InlineData("data type = 4","data type = 2")][InlineData("data units = reflectance","data units = radiance")][InlineData("byte order = 0","byte order = 2")][InlineData("wavelength units = Nanometers","wavelength units = Unknown")]
    public void ClassicRejectsUnsupportedOriginalMetadata(string from,string to)
    {var item=Item("classic");var h=Cube(root,item);File.WriteAllText(h,File.ReadAllText(h).Replace(from,to));Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,item));}
    [Fact]public void MissingBinaryWrongSceneDateGeoreferenceAndChecksumAreRejected()
    {
        var item=Item("classic");var h=Cube(root,item);var binary=Path.ChangeExtension(h,".bin");var digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant();
        File.WriteAllText(binary+".sha256",digest+"  "+Path.GetFileName(binary));Assert.Single(NasaGuidedImport.Inspect(h,item).Manifest!.OriginalMetadataFiles!);
        File.WriteAllText(binary+".sha256",new string('0',64)+"  "+Path.GetFileName(binary));Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,item));File.Delete(binary+".sha256");
        var original=File.ReadAllText(h);File.AppendAllText(h,"acquisition time = 2026-01-01T00:00:00Z\n");Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,item));File.WriteAllText(h,original);
        File.WriteAllText(h,original.Replace("WGS-84","NAD27"));Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,item));File.WriteAllText(h,original);
        var wrong=h.Replace("_corr_","_h2o_");File.Copy(h,wrong);Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(wrong,item));File.Delete(wrong);
        File.Delete(binary);Assert.Null(NasaGuidedImport.Inspect(h,item).Manifest);
    }
    [Fact]public void ArchivesAndReparsePointsAreNotImportedOrExtracted()
    {
        Directory.CreateDirectory(root);var archive=Path.Combine(root,"original.zip");File.WriteAllText(archive,"not scientific data");Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(archive,Item("classic")));
        if(!OperatingSystem.IsWindows()){var h=Cube(Path.Combine(root,"target"),Item("classic"));var link=Path.Combine(root,Path.GetFileName(h));File.CreateSymbolicLink(link,h);Assert.Throws<IOException>(()=>NasaGuidedImport.Inspect(link,Item("classic")));}
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
