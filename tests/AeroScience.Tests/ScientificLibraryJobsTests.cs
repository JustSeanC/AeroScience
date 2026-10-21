using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AeroScience.Tests;

[Collection(ScientificIoCollection.Name)]
public sealed class ScientificLibraryJobsTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"library-jobs-"+Guid.NewGuid().ToString("N"));
    private readonly ScienceStore science;private readonly ScientificAccessSettings settings;private readonly string raster;
    public ScientificLibraryJobsTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();settings=new(root,()=>true);settings.Save(new(true,true));raster=Path.Combine(root,"numeric.tif");RemoteFixtureFiles.GeoTiff(raster,123.25f);}
    private sealed class Handler(string raster,Func<bool>? fail=null,Action? requested=null):HttpMessageHandler
    {
        public int Numeric;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(!r.RequestUri!.AbsolutePath.Contains("exportImage"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"pixelType\":\"F32\",\"bandCount\":1,\"description\":\"SYNTHETIC service fixture; no genuine provider acceptance\"}")});
            Numeric++;requested?.Invoke();if(fail?.Invoke()==true)throw new HttpRequestException("Synthetic outage");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(File.ReadAllBytes(raster))});
        }
    }
    private async Task<LibraryDownloadJob> Preview(ScientificCatalog catalog,ScientificLibraryJobs jobs,GeographicBounds? bounds=null)
    {
        var area=bounds??new(-75.01,39.99,-74.99,40.01);var item=Assert.Single((await catalog.Search(new("usgs-3dep",area))).Items);
        return await jobs.Preview(new(1,[area],[new(item.Provider,item.Scene,item.Revision,item.Bands.Select(b=>b.Id).ToArray())]));
    }
    private static async Task<LibraryDownloadJob> Terminal(ScienceStore science,Guid id)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();while(true){var j=science.LibraryJob(id);if(j.State is "Completed" or "Failed" or "Interrupted")return j;if(watch.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException(JsonSerializer.Serialize(j,WireJson.Options));await Task.Delay(25);}
    }
    [Fact]public async Task RealPersistedJobRequiresApprovalPublishesVerifiedSourceWithoutMissionAndReusesExactly()
    {
        var handler=new Handler(raster);using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);Assert.Equal("Awaiting Approval",preview.State);Assert.Equal(0,handler.Numeric);Assert.Null(preview.Preview.EstimatedTransferBytes);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Approve(preview.Id,"changed"));
        await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var complete=await Terminal(science,preview.Id);Assert.Equal("Completed",complete.State);Assert.True(complete.DownloadedBytes>0);var source=Assert.Single(science.Sources());Assert.NotNull(source.Manifest.Acquisition!.CatalogRevision);Assert.Equal(source.Id,Assert.Single(complete.Items).SourceId);Assert.Empty(science.Datasets());Assert.Empty(science.Products());
        using(var numeric=new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),source.Manifest.Bands[0]))Assert.Equal(123.25,numeric.Read(new(40,-75)));
        var reused=await Preview(catalog,jobs);Assert.True(reused.Preview.ReusableBytes>0);Assert.Equal(0,reused.Preview.EstimatedTransferBytes);var before=handler.Numeric;await jobs.Approve(reused.Id,reused.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,reused.Id)).State);Assert.Equal(before,handler.Numeric);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Control(reused.Id,"retry"));
    }
    [Fact]public async Task PauseCancelAndResumeAreDurableAndFlightPriorityDefersWork()
    {
        var handler=new Handler(raster);using var automatic=new AutomaticScientificData(science,settings,new(handler));automatic.FlightActive(true);var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);await jobs.Approve(preview.Id,preview.Preview.SelectionHash);await Task.Delay(400);Assert.Equal(0,handler.Numeric);
        await jobs.Control(preview.Id,"pause");Assert.Equal("Paused",science.LibraryJob(preview.Id).State);await jobs.Control(preview.Id,"resume");Assert.Equal("Queued",science.LibraryJob(preview.Id).State);
        automatic.FlightActive(false);Assert.Equal("Completed",(await Terminal(science,preview.Id)).State);
        automatic.FlightActive(true);var cancelled=await Preview(catalog,jobs);await jobs.Approve(cancelled.Id,cancelled.Preview.SelectionHash);await jobs.Control(cancelled.Id,"cancel");Assert.Equal("Cancelled",science.LibraryJob(cancelled.Id).State);await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Control(cancelled.Id,"resume"));Assert.Single(science.Sources());
    }
    [Fact]public async Task RestartRecoversApprovalsAndChargesUncertainReservationsWithoutInventingBytes()
    {
        using var automatic=new AutomaticScientificData(science,settings,new(new Handler(raster)));automatic.FlightActive(true);var catalog=new ScientificCatalog(automatic,science,settings);Guid id;string hash;
        using(var first=new ScientificLibraryJobs(science,catalog,automatic,settings))
        {
            var preview=await Preview(catalog,first);id=preview.Id;hash=preview.Preview.SelectionHash;await first.Approve(id,hash);
        }
        science.SetLibraryState(id,"Downloading");science.SetLibraryItem(id,0,"Downloading");science.LibraryBudget(id,0,0,65536);
        science.RecoverLibraryJobs();var recovered=science.LibraryJob(id);Assert.Equal("Interrupted",recovered.State);Assert.Equal(0,recovered.DownloadedBytes);Assert.Equal(65536,recovered.UncertainBytes);Assert.NotNull(recovered.ApprovalId);Assert.Equal(hash,recovered.Preview.SelectionHash);
        using(var second=new ScientificLibraryJobs(science,catalog,automatic,settings)){await second.Control(id,"resume");automatic.FlightActive(false);Assert.Equal("Completed",(await Terminal(science,id)).State);}
    }
    [Fact]public async Task ChangedConsentAndScopeCannotSpendOldApprovalAndBudgetsPersistAcrossRetries()
    {
        using var automatic=new AutomaticScientificData(science,settings,new(new Handler(raster)));automatic.FlightActive(true);var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);science.LibraryBudget(preview.Id,0,0,100);science.LibraryBudget(preview.Id,0,100,100);science.LibraryBudget(preview.Id,0,100,100,finish:true);Assert.Equal(100,science.LibraryJob(preview.Id).DownloadedBytes);
        Assert.Throws<IOException>(()=>science.LibraryBudget(preview.Id,0,100,preview.Preview.MaximumTransferBytes+1));settings.Save(new());await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Approve(preview.Id,preview.Preview.SelectionHash));
    }
    [Fact]public async Task RetainedExactSourceCanBeReviewedAndReusedWithConsentDisabled()
    {
        var handler=new Handler(raster);using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var initial=await Preview(catalog,jobs);await jobs.Approve(initial.Id,initial.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,initial.Id)).State);var calls=handler.Numeric;
        settings.Save(new());var offline=await jobs.Preview(initial.Preview.Selection);Assert.Equal(0,offline.Preview.EstimatedTransferBytes);await jobs.Approve(offline.Id,offline.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,offline.Id)).State);Assert.Equal(calls,handler.Numeric);
    }
    [Fact]public async Task ActivePauseCancelsNetworkAndCanResumeWithoutPublishingPartialSource()
    {
        var handler=new InterruptibleHandler(raster);using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);await jobs.Approve(preview.Id,preview.Preview.SelectionHash);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));await jobs.Control(preview.Id,"pause");
        // Pause acknowledgement includes quiescence; an immediate Resume cannot be
        // overwritten by the preceding attempt's cancellation completion.
        Assert.Equal("Interrupted",science.LibraryJob(preview.Id).Items[0].State);
        Assert.Equal("Paused",science.LibraryJob(preview.Id).State);Assert.Empty(science.Sources());Assert.Equal(0,science.LibraryJob(preview.Id).Items[0].ReservedBytes);
        handler.Block=false;await jobs.Control(preview.Id,"resume");Assert.Equal("Completed",(await Terminal(science,preview.Id)).State);Assert.Single(science.Sources());
    }
    [Fact]public async Task FlightActivationPreemptsActiveLibraryTransferAndReleasesItsAllowances()
    {
        var handler=new InterruptibleHandler(raster);using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);await jobs.Approve(preview.Id,preview.Preview.SelectionHash);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));automatic.FlightActive(true);
        Assert.Equal("Interrupted",(await Terminal(science,preview.Id)).State);Assert.Equal(0,automatic.QueueSummary().PreflightReservedBytes);Assert.Empty(science.Sources());
        handler.Block=false;automatic.FlightActive(false);await jobs.Control(preview.Id,"resume");Assert.Equal("Completed",(await Terminal(science,preview.Id)).State);
    }
    [Fact]public async Task ServiceOutageRetainsFailedWorkForExplicitRetry()
    {
        var handler=new InterruptibleHandler(raster){Block=false,Unavailable=true};using var automatic=new AutomaticScientificData(science,settings,new(handler));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var preview=await Preview(catalog,jobs);await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var failed=await Terminal(science,preview.Id);Assert.Equal("Failed",failed.State);Assert.Contains("503",failed.Error);Assert.Empty(science.Sources());
        handler.Unavailable=false;await jobs.Control(preview.Id,"retry");var completed=await Terminal(science,preview.Id);Assert.Equal("Completed",completed.State);Assert.Equal(2,completed.Items[0].Attempts);
    }
    private sealed class InterruptibleHandler(string raster):HttpMessageHandler
    {
        public bool Block=true,Unavailable;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(!request.RequestUri!.AbsolutePath.Contains("exportImage"))return new(HttpStatusCode.OK){Content=new StringContent("{\"pixelType\":\"F32\",\"bandCount\":1,\"description\":\"SYNTHETIC retry fixture\"}")};
            Started.TrySetResult();if(Block)await Task.Delay(Timeout.Infinite,token);
            if(Unavailable){var r=new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);r.Headers.RetryAfter=new RetryConditionHeaderValue(TimeSpan.Zero);return r;}
            return new(HttpStatusCode.OK){Content=new ByteArrayContent(File.ReadAllBytes(raster))};
        }
    }
    [Fact]public async Task MissingLibraryAndCapacityErrorsCannotCreateEmptyReplacementOrDeleteSources()
    {
        using var automatic=new AutomaticScientificData(science,settings,new(new Handler(raster)));var catalog=new ScientificCatalog(automatic,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,automatic,settings);
        var marker=Path.Combine(science.DirectoryPath,StorageManager.LibraryManifest);File.Move(marker,marker+".held");Assert.NotNull(science.AvailabilityError());Assert.True(File.Exists(science.DatabasePath));File.Move(marker+".held",marker);
        var missions=new MissionStore(Path.Combine(root,"capacity-missions.db"));missions.Initialize();var capacity=new FailingCapacity();science.Management=new StorageManagement(root,missions,science,capacity);
        await Assert.ThrowsAsync<StorageLimitException>(()=>Preview(catalog,jobs));Assert.Empty(science.LibraryJobs());Assert.Empty(science.Sources());capacity.PermissionError=true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>Preview(catalog,jobs));Assert.True(File.Exists(science.DatabasePath));
    }
    private sealed class FailingCapacity:IStorageCapacity {public bool PermissionError;public long FreeBytes(string directory)=>PermissionError?throw new UnauthorizedAccessException("Synthetic permission failure"):0;}
    [Fact]public void BoundedWorkNeverSilentlyTruncatesAndMigrationPreservesExistingSources()
    {
        var scene=new ScientificSceneSelection("usgs-3dep","3dep-composite","revision",["elevation"]);
        Assert.Equal(4,ScientificLibraryJobs.Expand(new(1,[new(-75,40,-74.96,40.04)],[scene])).Length);
        Assert.Throws<ArgumentException>(()=>ScientificLibraryJobs.Expand(new(1,[new(-75,40,-70,45)],[scene])));
        Assert.Throws<ArgumentException>(()=>ScientificLibraryJobs.Expand(new(1,[new(179,40,-179,41)],[scene])));
        using(var db=new SqliteConnection("Data Source="+science.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="DROP TABLE library_job_items; DROP TABLE library_jobs; PRAGMA user_version=2";cmd.ExecuteNonQuery();}
        science.Initialize();using(var db=new SqliteConnection("Data Source="+science.DatabasePath)){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";Assert.Equal(3L,cmd.ExecuteScalar());cmd.CommandText="PRAGMA integrity_check";Assert.Equal("ok",cmd.ExecuteScalar());}Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(science.DirectoryPath,"Metadata","Backups")));
    }
    [Fact]public void PersistedRangesRequireFreshStrongIdentityAndRejectCorruptBlocks()
    {
        var bytes=Enumerable.Range(0,150000).Select(i=>(byte)i).ToArray();var handler=new RangeHandler(bytes);using var http=new ScientificHttp(handler,cacheEnabled:false);var uri=new Uri("https://sentinel-cogs.s3.us-west-2.amazonaws.com/fixture.tif");
        using(var stream=new ScientificRangeStream(http,uri,new(1000000),default,new(science,"Downloads/range-test"))){stream.Position=70000;stream.ReadByte();}Assert.Equal(2,handler.Count);
        using(var stream=new ScientificRangeStream(http,uri,new(1000000),default,new(science,"Downloads/range-test"))){stream.Position=70000;Assert.Equal(bytes[70000],stream.ReadByte());}Assert.Equal(3,handler.Count);
        File.WriteAllBytes(Path.Combine(science.DirectoryPath,"Downloads/range-test/1.bin"),new byte[65536]);
        using(var stream=new ScientificRangeStream(http,uri,new(1000000),default,new(science,"Downloads/range-test"))){stream.Position=70000;Assert.Equal(bytes[70000],stream.ReadByte());}Assert.Equal(5,handler.Count);
        handler.Etag="\"different\"";Assert.Throws<IOException>(()=>new ScientificRangeStream(http,uri,new(1000000),default,new(science,"Downloads/range-test")));Assert.Equal(6,handler.Count);
    }
    [Fact]public async Task CogBodyCancellationAfterHeadersRetainsActualPartialByteAccounting()
    {
        var handler=new StallingBodyHandler();using var http=new ScientificHttp(handler,cacheEnabled:false);using var cancel=new CancellationTokenSource();var budget=new ScientificDownloadBudget(1048576);
        var reading=Task.Run(()=>{using var stream=new ScientificRangeStream(http,new("https://sentinel-cogs.s3.us-west-2.amazonaws.com/fixture.tif"),budget,cancel.Token);});
        await handler.Body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reading.WaitAsync(TimeSpan.FromSeconds(5)));Assert.Equal(32,budget.Bytes);
    }
    private sealed class StallingBodyHandler:HttpMessageHandler
    {
        public readonly StallingBody Body=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new StreamContent(Body)};response.Content.Headers.ContentRange=new(0,65535,150000);response.Headers.ETag=new("\"fixture-v1\"");return Task.FromResult(response);}
    }
    private sealed class StallingBody:Stream
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);private bool first=true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(first){first=false;buffer.Span[..32].Clear();return 32;}Started.TrySetResult();await Task.Delay(Timeout.Infinite,token);return 0;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException("A bounded remote read must honor its cancellation token.");
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    private sealed class RangeHandler(byte[] bytes):HttpMessageHandler
    {
        public string Etag="\"one\"";public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken t){Count++;var start=r.Headers.Range!.Ranges.Single().From!.Value;var end=Math.Min(bytes.Length-1,r.Headers.Range.Ranges.Single().To!.Value);var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(bytes[(int)start..((int)end+1)])};response.Content.Headers.ContentRange=new(start,end,bytes.Length);response.Headers.ETag=new(Etag);return Task.FromResult(response);}
    }
    public void Dispose(){science.Dispose();SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
