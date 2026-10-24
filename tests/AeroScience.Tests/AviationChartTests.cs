using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AeroScience.Engine;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;

namespace AeroScience.Tests;

// Synthetic HTTP/raster adapter, actual approved jobs, archive checks, store, service and cache.
// Native color/CRS correctness is tested separately on Windows with genuine FAA originals.
public sealed class AviationChartTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"aeroscience-chart-tests",Guid.NewGuid().ToString("N"));
    private static readonly byte[] Tiff=[73,73,42,0,0,0,0,0];
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private sealed class Renderer : IAviationChartRenderer
    {
        public bool Available{get;set;}=true; public int Descriptions,Tiles;
        public static readonly byte[] Png=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=");
        public AviationChartRaster.Description Describe(string path,AviationChartDefinition d,CancellationToken token)
        {token.ThrowIfCancellationRequested();Assert.Equal(Tiff,File.ReadAllBytes(path));Descriptions++;return new(d.Width,d.Height,1,"Synthetic test CRS",[new(-71,42),new(-70,42),new(-70,41),new(-71,41),new(-71,42)]);}
        public byte[] Tile(string path,AviationChartDefinition d,int z,int x,int y,CancellationToken token)
        {token.ThrowIfCancellationRequested();Assert.Equal(Tiff,File.ReadAllBytes(path));Tiles++;return Png;}
    }
    private sealed class Disk:IStorageCapacity {public long FreeBytes(string path)=>100L*1024*1024*1024;}
    private sealed class Transport(int files=2):HttpMessageHandler
    {
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.Host=="www.faa.gov")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<a href='https://aeronav.faa.gov/visual/09-03-2026/sectional-files/Synthetic.zip'>Synthetic original</a>")});
            Downloads++;using var data=new MemoryStream();using(var zip=new ZipArchive(data,ZipArchiveMode.Create,true))
                for(var i=0;i<files;i++){var entry=zip.CreateEntry($"sheet-{i:D2}.tif");using var output=entry.Open();output.Write(Tiff);}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data.ToArray())});
        }
    }
    private static AviationChartDefinition Definition=>new("sheet-00.tif",Digest(Tiff),10,10,[new(1,1),new(9,1),new(9,9),new(1,9),new(1,1)],"Synthetic interior test definition, not an FAA source");
    private AviationReference Service(Transport t,Renderer r,Func<bool>? flight=null)=>new(root,root,new HttpClient(t),capacity:new Disk(),unmetered:()=>true,flightActive:flight,chartRenderer:r,chartDefinitions:[Definition]);
    private static async Task<AviationDownloadJob> Acquire(AviationReference service)
    {
        service.Configure(true);await service.Refresh("vfr");var p=await service.PreviewDownload("vfr",service.Page("vfr").Charts[0].Id,1048576,1048576);
        service.DownloadControl(p.Id,p.Revision,"approve",p.Preview.SelectionHash);await service.ProcessDownloads();return Assert.Single(service.DownloadJobs());
    }
    [Fact]public async Task ApprovedOriginalToChartInventoryTileCacheAndOfflineReuseCreatesNoScience()
    {
        var t=new Transport();var renderer=new Renderer();Guid id;string sha;
        using(var service=Service(t,renderer))
        {
            var job=await Acquire(service);id=job.Id;sha=job.OriginalSha256!;Assert.Equal("Completed",job.State);
            var page=await service.ChartSheets(researchRendering:true);Assert.Equal(2,page.Sheets.Length);var sheet=Assert.Single(page.Sheets,s=>s.State=="Reviewed interior available");
            Assert.Equal("Main body not reviewed",page.Sheets.Single(s=>s.Id!=sheet.Id).State);Assert.Equal(5,sheet.MainBody.Length);Assert.Equal(1,renderer.Descriptions);
            await service.ChartSheets(researchRendering:true);Assert.Equal(1,renderer.Descriptions);
            Assert.Equal(Renderer.Png,await service.ChartTile(id,sheet.Id,5,9,12,researchRendering:true));Assert.Equal(Renderer.Png,await service.ChartTile(id,sheet.Id,5,9,12,researchRendering:true));Assert.Equal(1,renderer.Tiles);
            service.Configure(false);Assert.Equal(Renderer.Png,await service.ChartTile(id,sheet.Id,5,9,12,researchRendering:true));Assert.Equal(1,t.Downloads);
            Assert.Equal(sha,Assert.Single(service.DownloadJobs()).OriginalSha256);
        }
        using(var reopened=Service(t,new Renderer()))
        {
            Assert.Equal(Renderer.Png,await reopened.ChartTile(id,Definition.Id,5,9,12,researchRendering:true));Assert.Equal(1,t.Downloads);
            await reopened.VerifyDownload(id);
        }
        Assert.False(File.Exists(Path.Combine(root,"missions.db")));Assert.False(Directory.Exists(Path.Combine(root,"ScienceLibrary")));
    }
    [Fact]public async Task AllRasterMembersArePagedWithoutDroppingUnreviewedInsets()
    {
        var t=new Transport(51);using var service=Service(t,new Renderer());await Acquire(service);
        var first=await service.ChartSheets(researchRendering:true);Assert.Equal(50,first.Sheets.Length);Assert.Equal(50,first.NextOffset);
        var last=await service.ChartSheets(50,researchRendering:true);Assert.Single(last.Sheets);Assert.Null(last.NextOffset);
        Assert.Equal(51,first.Sheets.Concat(last.Sheets).Select(s=>s.Id).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ChartSheets(1,researchRendering:true));Assert.Equal(1,t.Downloads);
    }
    [Fact]public async Task UnknownRuntimeAndUnreviewedSourcesNeverBecomePositiveCoverage()
    {
        var t=new Transport();using var service=Service(t,new Renderer{Available=false});await Acquire(service);
        var page=await service.ChartSheets(researchRendering:true);Assert.False(page.RuntimeAvailable);Assert.All(page.Sheets,s=>Assert.Empty(s.MainBody));
        Assert.Equal("Runtime unavailable",page.Sheets.Single(s=>s.Name==Definition.FileName).State);
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ChartTile(page.Sheets[0].JobId,"../unreviewed",5,1,1,researchRendering:true));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.ChartTile(page.Sheets[0].JobId,Definition.Id,20,1,1,researchRendering:true));
    }
    [Fact]public async Task InitialChartWorkYieldsToFlightWhileWarmedChartsRemainBounded()
    {
        var active=false;var t=new Transport();var r=new Renderer();using var service=Service(t,r,()=>active);var job=await Acquire(service);active=true;
        var held=await service.ChartSheets(researchRendering:true);Assert.All(held.Sheets,s=>Assert.Empty(s.MainBody));Assert.Equal(0,r.Descriptions);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChartTile(job.Id,Definition.Id,5,9,12,researchRendering:true));
        active=false;await service.ChartSheets(researchRendering:true);active=true;Assert.Equal(Renderer.Png,await service.ChartTile(job.Id,Definition.Id,5,9,12,researchRendering:true));
        Assert.Equal(1,r.Descriptions);Assert.Equal(1,r.Tiles);
    }
    [Fact]public async Task CorruptDerivedCacheIsNeverPresentedAsAVerifiedTile()
    {
        var t=new Transport();using var service=Service(t,new Renderer());var job=await Acquire(service);await service.ChartSheets(researchRendering:true);await service.ChartTile(job.Id,Definition.Id,5,9,12,researchRendering:true);
        var cache=Directory.GetFiles(Path.Combine(root,"AviationReference","ChartTiles-v1"),"*.png",SearchOption.AllDirectories).Single();
        var bad=File.ReadAllBytes(cache);bad[^1]^=1;File.WriteAllBytes(cache,bad);
        await Assert.ThrowsAsync<IOException>(()=>service.ChartTile(job.Id,Definition.Id,5,9,12,researchRendering:true));await service.VerifyDownload(job.Id);
    }
    [Fact]public async Task MissingAviationDriveDoesNotCreateAnEmptyChartStore()
    {
        var t=new Transport();using var service=Service(t,new Renderer());await Acquire(service);var folder=Path.Combine(root,"AviationReference");Directory.Move(folder,folder+"-disconnected");
        await Assert.ThrowsAsync<IOException>(()=>service.ChartSheets(researchRendering:true));Assert.False(Directory.Exists(folder));
    }
    [Fact]public async Task SimultaneousTileRequestsShareOneVerifiedRenderingAndCancellationRemainsEffective()
    {
        var t=new Transport();var r=new Renderer();using var service=Service(t,r);var job=await Acquire(service);await service.ChartSheets(researchRendering:true);
        var tiles=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>service.ChartTile(job.Id,Definition.Id,5,9,12,researchRendering:true)));
        Assert.All(tiles,b=>Assert.Equal(Renderer.Png,b));Assert.Equal(1,r.Tiles);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.ChartTile(job.Id,Definition.Id,5,10,12,cancelled.Token,researchRendering:true));
        Assert.Equal(1,r.Tiles);Assert.Equal(1,t.Downloads);
    }
    [Fact]public async Task NormalApplicationDefersRenderingAndPreservesOriginalDownloads()
    {
        var t=new Transport();var r=new Renderer();using var service=Service(t,r);var job=await Acquire(service);
        var page=await service.ChartSheets();Assert.False(page.RuntimeAvailable);Assert.All(page.Sheets,s=>{Assert.Equal("Rendering deferred by owner",s.State);Assert.Empty(s.MainBody);});
        await Assert.ThrowsAsync<NotSupportedException>(()=>service.ChartTile(job.Id,Definition.Id,5,9,12));Assert.Equal(0,r.Descriptions);Assert.Equal(0,r.Tiles);await service.VerifyDownload(job.Id);Assert.Equal(1,t.Downloads);
    }
    public void Dispose(){if(Directory.Exists(root))Directory.Delete(root,true);}
}
