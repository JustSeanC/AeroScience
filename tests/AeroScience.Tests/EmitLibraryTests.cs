using System.Runtime.InteropServices;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class EmitLibraryTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"emit-library-synthetic-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;private readonly MissionStore missions;private readonly EmitLibrary emit;
    private static ScientificHdfReader Reader()=>new(new(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"../../..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet")),["exec","--runtimeconfig",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.runtimeconfig.json"),"--depsfile",Path.Combine(AppContext.BaseDirectory,"AeroScience.Tests.deps.json"),typeof(EngineApplication).Assembly.Location]));
    public EmitLibraryTests(){Directory.CreateDirectory(root);var library=Path.Combine(root,"Library");Directory.CreateDirectory(library);File.WriteAllText(Path.Combine(library,"library.json"),JsonSerializer.Serialize(new{formatVersion=1,libraryId=Guid.NewGuid()}));science=new(library);science.Initialize();missions=new(Path.Combine(root,"missions.db"));missions.Initialize();emit=new(science,new(Reader()),new(missions,1000));}
    private string[] Files()=>Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"EmitApplicationFixtures","standard"),"*.nc");
    private async Task<Guid> Import(){await emit.SelectLocal(Files());var r=emit.Snapshot().Review!;emit.Import(new(r.Id,r.SelectionHash,true));for(var n=0;n<300&&emit.Snapshot().Stage=="Importing";n++)await Task.Delay(50);Assert.True(emit.Snapshot().Stage=="Ready",emit.Snapshot().Message);return emit.Snapshot().SourceId!.Value;}
    [Fact]public async Task LocalReviewRequiresExactApprovalPublishesSwathInExistingInventoryAndReusesOffline()
    {
        await emit.SelectLocal(Files());var r=emit.Snapshot().Review!;Assert.Empty(science.Sources());Assert.Equal(3,r.FileNames.Length);Assert.Equal(285,r.Definition.Bands);
        Assert.Throws<InvalidOperationException>(()=>emit.Import(new(r.Id,"changed",true)));Assert.Throws<InvalidOperationException>(()=>emit.Import(new(r.Id,r.SelectionHash,false)));
        emit.Import(new(r.Id,r.SelectionHash,true));for(var n=0;n<300&&emit.Snapshot().Stage=="Importing";n++)await Task.Delay(50);Assert.Equal("Ready",emit.Snapshot().Stage);var id=emit.Snapshot().SourceId!.Value;
        var source=science.Source(id)!;Assert.Equal("emit-netcdf",source.Manifest.Swath!.Format);Assert.Empty(source.Grids);Assert.Equal(3,source.Files.Length);Assert.Equal(1,science.CountSources(new(Area:source.Manifest.Swath.Domain)));Assert.Null(science.InspectAvailability(source,new(37.99,-119.99)).CoversPoint);Assert.False(ScientificSourceSelection.CoversArea(source,source.Manifest.Swath.Domain));
        Assert.Empty(missions.List());emit.Cancel();Assert.Equal(id,await Import());Assert.Single(science.Sources());
        science.Dispose();using var reopened=new ScienceStore(science.DirectoryPath);reopened.Initialize();using var offline=new EmitLibrary(reopened,new(Reader()),new(missions,1000));var values=await offline.Spectrum(id,new(0,0),default);Assert.Equal(.125f,values.Pixels[0].UsableReflectance[0]);Assert.Equal(285,values.Pixels[0].Reflectance.Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(science.DirectoryPath,"Downloads"),"*.nc",SearchOption.AllDirectories));
    }
    [Fact]public async Task SpectrumQualityGeographicComparisonAndPortableExportsPreserveNativeEvidence()
    {
        var id=await Import();var a=await emit.Spectrum(id,new(0,0),default);Assert.Null(a.Pixels[0].UsableReflectance[9]);var cloudy=await emit.Spectrum(id,new(0,1),default);Assert.All(cloudy.Pixels[0].UsableReflectance,v=>Assert.Null(v));
        var map=await emit.Map(id,new(0,0,2,4),default);Assert.Equal(8,map.Cells.Length);Assert.Equal(0,map.Cells[0].SourceRow);Assert.Equal(0,map.Cells[0].SourceColumn);Assert.Null(map.Cells[2].SourceRow);
        var report=JsonSerializer.Serialize(await emit.Export(id,new(0,0,0,5),default),WireJson.Options);Assert.Contains("wavelengthNm",report);Assert.Contains("originalReflectanceBytes",report);Assert.DoesNotContain(root,report);Assert.DoesNotContain("token",report,StringComparison.OrdinalIgnoreCase);
        var compare=JsonSerializer.Serialize(await emit.Compare(id,new(new(0,0,0,5),new(0,2,0,5)),default),WireJson.Options);Assert.Contains("First minus second",compare);Assert.Contains("difference",compare);
        await Assert.ThrowsAsync<ArgumentException>(()=>emit.Compare(id,new(new(0,0),new(0,2,1,5)),default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>emit.Map(id,new(0,0,9,9),default));
    }
    [Fact]public async Task CorruptionCancelAndPermanentDeletionCannotReuseHiddenOriginals()
    {
        await emit.SelectLocal(Files());emit.Cancel();Assert.Empty(science.Sources());var id=await Import();var source=science.Source(id)!;var original=Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Files[0].RelativePath);var bytes=File.ReadAllBytes(original);File.WriteAllBytes(original,[1,2,3]);await Assert.ThrowsAsync<ScientificHdfException>(()=>emit.Spectrum(id,new(0,0),default));File.WriteAllBytes(original,bytes);
        var preview=science.PreviewSourceDeletion(new(SourceIds:[id]),missions,()=>null);
        var deleted=science.DeleteExternalSources(new(preview.Id,preview.SelectionHash),missions,()=>new ActionLease(),()=>null);
        Assert.Null(science.Source(id));Assert.False(File.Exists(original));Assert.Empty(Directory.GetFiles(science.DirectoryPath,"*.nc",SearchOption.AllDirectories));await Assert.ThrowsAsync<ArgumentException>(()=>emit.Spectrum(id,new(0,0),default));
    }
    [Fact]public async Task AuthoritativeOriginalChecksumsAllowLocalReuseButDifferentContentAndCatalogIdentityDoNot()
    {
        var id=await Import();var source=science.Source(id)!;
        using var collection=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata","nasa-emit-collections.json")));
        using var granules=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ProviderMetadata","nasa-emit-granules.json")));
        var item=ScientificCatalog.DescribeNasa("nasa-emit",collection.RootElement.GetProperty("feed").GetProperty("entry")[0],granules.RootElement.GetProperty("feed").GetProperty("entry")[0]) with{Bounds=source.Manifest.Swath!.Domain};
        var mask=item with{Provider="nasa-emit-mask",Nasa=item.Nasa! with{Flightline="EMIT_L2A_MASK_003_20220810T034103"}};
        var files=source.Files.Select(f=>new NasaDownloadFile(f.RelativePath,"https://data.lpdaac.earthdatacloud.nasa.gov/unused","Original NetCDF-4",f.Bytes,null,false,Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(Path.Combine(science.DirectoryPath,source.RelativeDirectory,f.RelativePath)))))).ToArray();
        var bundle=new NasaDownloadBundle(item,[mask],files,"synthetic");Assert.Equal(id,(await emit.FindReusable(bundle,default))!.Id);
        Assert.Null(await emit.FindReusable(bundle with{Files=files.Select(f=>f with{Sha512=null}).ToArray()},default));
        Assert.Null(await emit.FindReusable(bundle with{Files=files.Select(f=>f with{Sha512=new string('0',128)}).ToArray()},default));
        await Assert.ThrowsAsync<EmitProductException>(()=>emit.FindReusable(bundle with{Selection=item with{Bounds=new(73,21,75,23)}},default));
        Assert.Single(science.Sources());Assert.Empty(missions.List());
    }
    private sealed class ActionLease:IDisposable{public void Dispose(){}}
    public void Dispose(){emit.Dispose();science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
}
