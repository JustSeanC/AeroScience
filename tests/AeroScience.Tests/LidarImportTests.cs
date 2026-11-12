using System.Net;
using System.Security.Cryptography;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;
[Collection(ScientificIoCollection.Name)]
public sealed class LidarImportTests:IDisposable
{
    readonly string root=Path.Combine(Path.GetTempPath(),"lidar-import-"+Guid.NewGuid().ToString("N"));readonly ScienceStore science;readonly MissionStore missions;readonly MissionCoordinator coordinator;readonly ScientificAccessSettings settings;
    static string Original=>Path.Combine(AppContext.BaseDirectory,"LidarOriginals","ia-fullstate-root.laz");
    static LidarImportMetadata Meta=>new("Genuine historical EPT LAZ test original","IA_FullState/0-0-0-0","US Government Public Domain","https://registry.opendata.aws/usgs-lidar/ ; genuine delivered EPT root, sparse subset");
    public LidarImportTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();missions=new(Path.Combine(p.MissionDirectory,"missions.db"));missions.Initialize();coordinator=new(missions,1000);settings=new(root,()=>true);settings.Save(new(true,true));}
    [Fact]public async Task GenuineLocalImportReusesOriginalsOfflineNeverBecomesRasterAndDeletionProtectsUserOriginal()
    {
        var a=await LidarImports.Import(science,Original,Meta);Assert.Equal(34923,a.Manifest.PointCloud!.Originals[0].Header.PointCount);Assert.Empty(a.Grids);Assert.Null(a.Manifest.ObservationUtc);Assert.Empty(science.Products());Assert.Empty(science.Datasets());
        Assert.Equal("68c4b0ed247501a15d020d9a758d0345904f38785235732bd8e3386a3fca71c1",Assert.Single(a.Files).Sha256);
        Assert.Equal(a.Id,(await LidarImports.Import(science,Original,Meta)).Id);Assert.Single(science.Sources());
        Assert.Null(ScientificSourceSelection.Select([new(a,true,true,null)],RemoteKind.Terrain));Assert.False(ScientificSourceSelection.CoversArea(a,a.Manifest.PointCloud.Domain));Assert.Throws<InvalidOperationException>(()=>TerrainElevationSources.Bind(science,new(a.Id,a.ManifestSha256),new()));
        Assert.Throws<InvalidOperationException>(()=>LidarImports.Bind(science,a.Id,a.ManifestSha256,false));Assert.Equal(a.Id,LidarImports.Bind(science,a.Id,a.ManifestSha256,true,new(false,false)).Id);
        using(var active=science.BeginAnalysis())Assert.Equal(0,science.PreviewSourceDeletion(new([a.Id]),missions).EligibleSources);
        var preview=science.PreviewSourceDeletion(new([a.Id]),missions);Assert.Equal(1,preview.EligibleSources);Assert.Equal("Completed",science.DeleteExternalSources(new(preview.Id,preview.SelectionHash),missions,coordinator.EnterMaintenance).State);Assert.Empty(science.Sources());Assert.True(File.Exists(Original));Assert.Empty(science.Products());
    }
    sealed class Handler:HttpMessageHandler
    {
        public int Numeric;public bool Fail;public bool Huge;public int Metadata;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var u=request.RequestUri!;byte[] data;
            if(u.AbsolutePath.EndsWith("ept.json",StringComparison.Ordinal)){Metadata++;data=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"LidarOriginals","ia-fullstate-ept.json"));}
            else if(u.AbsolutePath.Contains("ept-hierarchy",StringComparison.Ordinal))data=System.Text.Encoding.UTF8.GetBytes("{\"0-0-0-0\":34923}");
            else{Numeric++;if(Fail)throw new HttpRequestException("Synthetic network interruption");data=Huge?new byte[2097152]:File.ReadAllBytes(Original);}
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)});
        }
    }
    async Task<LidarDownloadRequest> Request(AutomaticScientificData automatic,long cap=4194304)
    {var source=await new LidarCatalog(automatic,science,settings).Verify("IA_FullState",new(1048576));return new(source.Project,source.MetadataSha256,new(-93.601,41.599,-93.599,41.601),cap);}
    static async Task<LibraryDownloadJob> Terminal(ScienceStore science,Guid id)
    {var timer=System.Diagnostics.Stopwatch.StartNew();while(true){var j=science.LibraryJob(id);if(j.State is "Completed" or "Failed" or "Interrupted")return j;if(timer.Elapsed>TimeSpan.FromSeconds(30))throw new TimeoutException(j.State+" "+j.Error);await Task.Delay(25);}}
    [Fact]public void PointIndexSupportsDegenerateAndConservativeUtmDomainsWithoutCallingThemCoverage()
    {
        LidarCoordinates.ValidateDomain(new(-75,40,-75,40));var p=LidarCoordinates.Geographic("EPSG:32618",500000,4427757.218738374);Assert.Equal(40,p.Latitude,8);Assert.Equal(-75,p.Longitude,8);
        var h=new LidarLasHeader(2,1,28,1,false,[.01,.01,.01],[0,0,0],[500000,4427757,100,500100,4427857,200],"EPSG:32618","Unknown","unknown",null,[],false,[],[]);var domain=LidarCoordinates.Domain(h);LidarCoordinates.ValidateDomain(domain);Assert.True(domain.East-domain.West>15);
    }
    [Fact]public async Task EptUsesExistingApprovalQueueBudgetAndRetainedNodeReuse()
    {
        var handler=new Handler();using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await jobs.PreviewLidar(await Request(automatic));Assert.Equal("Awaiting Approval",preview.State);Assert.Equal(0,handler.Numeric);Assert.Null(preview.Preview.EstimatedTransferBytes);Assert.Equal(34923,preview.Preview.Lidar!.CandidatePoints);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Approve(preview.Id,"different selection"));await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var done=await Terminal(science,preview.Id);
        Assert.True(done.State=="Completed",done.Error);Assert.Equal(1,handler.Numeric);Assert.True(done.DownloadedBytes>=new FileInfo(Original).Length);var source=Assert.Single(science.Sources());Assert.Equal("IA_FullState",source.Manifest.PointCloud!.EptProject);Assert.Null(source.Manifest.ObservationUtc);Assert.Empty(science.Products());
        var next=await jobs.PreviewLidar(await Request(automatic));Assert.Equal(0,next.Preview.EstimatedTransferBytes);Assert.Equal(source.Id,Assert.Single(next.Preview.Work).ReuseSourceId);await jobs.Approve(next.Id,next.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,next.Id)).State);Assert.Equal(1,handler.Numeric);
    }
    [Theory][InlineData(false)][InlineData(true)]public async Task FailedOrOverBudgetPublicRetrievalPublishesNoSourceAndRetainsActualCharges(bool oversized)
    {
        var handler=new Handler{Fail=!oversized,Huge=oversized};using var automatic=new AutomaticScientificData(science,settings,new(handler));using var jobs=new ScientificLibraryJobs(science,new(automatic,science,settings),automatic,settings);
        var p=await jobs.PreviewLidar(await Request(automatic,1048576));await jobs.Approve(p.Id,p.Preview.SelectionHash);var failed=await Terminal(science,p.Id);Assert.Equal("Failed",failed.State);Assert.Empty(science.Sources());Assert.True(failed.DownloadedBytes>0);Assert.Empty(science.Products());
    }
    public void Dispose(){science.Dispose();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
