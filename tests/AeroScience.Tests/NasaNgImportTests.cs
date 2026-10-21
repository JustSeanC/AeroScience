using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
// Independent NG family cases; synthetic numeric values, never a genuine NASA pass.
public sealed class NasaNgImportTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"nasa-ng-import-"+Guid.NewGuid().ToString("N"));
    [Theory][InlineData(425,"bil",false,false)][InlineData(425,"bip",true,true)][InlineData(432,"bsq",true,false)]
    public async Task NgIndependentLayoutsRetainSpectralAlignmentQualityUnitsAndOfflineSource(int bands,string layout,bool endian,bool float64)
    {
        var item=NasaFixtureFiles.Item("ng");var header=NasaFixtureFiles.Cube(Path.Combine(root,"originals"),item,bands,layout,endian,float64);
        var i=NasaGuidedImport.Inspect(header,item);Assert.Equal(bands,i.BandCount);Assert.Equal(item.ObservationUtc,i.ObservationUtc);Assert.Equal("reflectance",i.Unit);Assert.Single(i.Manifest!.OriginalMetadataFiles!);
        var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();Guid id;
        using(var science=new ScienceStore(storage.ScienceLibraryDirectory)){science.Initialize();var source=await science.ImportSourceAsync(i.Manifest,i.Root,reuseExisting:true);id=source.Id;Assert.Equal(id,(await science.ImportSourceAsync(i.Manifest,i.Root,reuseExisting:true)).Id);}
        using(var reopened=new ScienceStore(storage.ScienceLibraryDirectory))
        {
            reopened.Initialize();var source=Assert.Single(reopened.Sources());Assert.Equal(id,source.Id);Assert.Equal("G2663871321-ORNL_CLOUD",source.Manifest.SceneId);
            var folder=Path.Combine(reopened.DirectoryPath,source.RelativeDirectory);
            foreach(var b in new[]{0,1,5,100,bands-1})
            {var band=source.Manifest.Bands[b];Assert.Equal(400+5*b,band.WavelengthNm);Assert.Equal(b,band.Channel);using var raster=new ScientificRaster(folder,band);var value=raster.Read(ScientificRaster.GridPoint(raster.Grid,1.5,.5));if(b==5)Assert.Null(value);else Assert.Equal(float64?b*.001+.01:(double)(float)(b*.001+.01),value);}
            Assert.Contains("AVIRIS-NG",source.Manifest.Notes);Assert.True(reopened.Availability().Single().FilesAvailable);
        }
    }
    [Fact]public void ClassicIsNotNgAndWrongRevisionFilesCannotBeRelabeled()
    {
        var ng=NasaFixtureFiles.Item("ng");var classic=NasaFixtureFiles.Item("classic");var h=NasaFixtureFiles.Cube(root,ng,224);
        Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,ng));Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,classic));
        h=NasaFixtureFiles.Cube(root,ng,425);File.Delete(Path.ChangeExtension(h,".bin")+".sha256");Assert.Throws<InvalidDataException>(()=>NasaGuidedImport.Inspect(h,ng));
    }
    [Fact]public void NgChecksumCancellationAndTruncationLeaveOriginalsUnpublished()
    {
        var item=NasaFixtureFiles.Item("ng");var h=NasaFixtureFiles.Cube(root,item,425);using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>NasaGuidedImport.Inspect(h,item,token:cancel.Token));
        var file=Path.ChangeExtension(h,".bin");using(var stream=File.OpenWrite(file))stream.SetLength(stream.Length-4);
        Assert.Null(NasaGuidedImport.Inspect(h,item).Manifest);Assert.True(File.Exists(h));
    }
    [Fact]public void AuxiliaryWaterVaporIsDownloadOnlyAndCannotOpenNativeImport()
    {
        var collection=NasaCatalogTests.Metadata("ng","collections")["feed"]!["entry"]![0]!;var entry=NasaCatalogTests.Metadata("ng","granules")["feed"]!["entry"]![1]!;
        var item=ScientificCatalog.DescribeNasa("nasa-ng",System.Text.Json.JsonSerializer.SerializeToElement(collection),System.Text.Json.JsonSerializer.SerializeToElement(entry));Assert.StartsWith("Download only",item.Nasa!.Capability);
        var storage=new StorageManager(new PreferenceStore(root)).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();using var session=new ScientificImportSession(science);Assert.Throws<ArgumentException>(()=>session.BeginNasa(item));
    }
    [Fact]public async Task ChangedOriginalCannotBypassChecksumBetweenInspectionAndPublication()
    {
        var item=NasaFixtureFiles.Item("ng");var h=NasaFixtureFiles.Cube(Path.Combine(root,"originals"),item,425);var original=NasaGuidedImport.Inspect(h,item);
        var binary=Path.ChangeExtension(h,".bin");using(var stream=File.OpenWrite(binary))stream.WriteByte(123);
        var storage=new StorageManager(new PreferenceStore(Path.Combine(root,"settings"))).Initialize();using var science=new ScienceStore(storage.ScienceLibraryDirectory);science.Initialize();
        await Assert.ThrowsAsync<InvalidDataException>(()=>science.ImportSourceAsync(original.Manifest!,original.Root,verifyStaged:staged=>NasaGuidedImport.Inspect(Path.Combine(staged,Path.GetFileName(h)),item)));
        Assert.Empty(science.Sources());Assert.True(File.Exists(h));Assert.True(File.Exists(binary));
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
