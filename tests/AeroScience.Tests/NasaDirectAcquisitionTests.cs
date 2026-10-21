using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

// Recorded official CMR metadata + explicit synthetic numeric originals. These tests
// exercise real jobs/SQLite/transport/import, not genuine protected NASA acceptance.
public sealed class NasaDirectAcquisitionTests:IDisposable
{
    internal const string Token="synthetic_earthdata_user_token_for_tests";
    internal sealed class Credentials:IEarthdataCredentials
    {public bool Available=>true;public string? Value=Token;public string? ReadToken()=>Value;public void Clear()=>Value=null;}
    readonly string root=Path.Combine(Path.GetTempPath(),"nasa-direct-"+Guid.NewGuid().ToString("N"));
    readonly ScienceStore science;readonly ScientificAccessSettings settings;
    public NasaDirectAcquisitionTests(){var p=new StorageManager(new PreferenceStore(root)).Initialize();science=new(p.ScienceLibraryDirectory);science.Initialize();settings=new(Path.Combine(root,"config"),()=>true);settings.Save(new(true,true));}
    internal sealed class Handler(string folder,string family):HttpMessageHandler
    {
        public int Numeric;public bool Denied,Block,BadChecksum;public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> AuthHosts=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var uri=request.RequestUri!;
            if(uri.Host=="cmr.earthdata.nasa.gov")
            {
                Assert.Null(request.Headers.Authorization);
                var type=uri.AbsolutePath.Contains("collections")?"collections":family=="ng"?"companions":"granules";
                var json=NasaCatalogTests.Metadata(family,type);var query=System.Web.HttpUtility.ParseQueryString(uri.Query);
                if(query["concept_id"] is {} id)json["feed"]!["entry"]=new JsonArray(json["feed"]!["entry"]!.AsArray().Where(e=>e!["id"]!.GetValue<string>()==id).Select(e=>e!.DeepClone()).ToArray());
                return new(HttpStatusCode.OK){Content=new StringContent(json.ToJsonString())};
            }
            Assert.Equal("data.ornldaac.earthdata.nasa.gov",uri.Host);Assert.Equal(Token,request.Headers.Authorization?.Parameter);AuthHosts.Add(uri.Host);
            if(Denied)return new(HttpStatusCode.Unauthorized);
            var name=Path.GetFileName(uri.AbsolutePath);var path=Path.Combine(folder,name);
            var data=!File.Exists(path)&&name.EndsWith(".hdr.sha256",StringComparison.Ordinal)?System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path[..^7]))).ToLowerInvariant()+"  "+name[..^7]):File.ReadAllBytes(path);
            if(BadChecksum&&name.EndsWith(".sha256",StringComparison.Ordinal))data=System.Text.Encoding.ASCII.GetBytes(new string('0',64)+"  "+name[..^7]);
            var result=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(request.Method==HttpMethod.Head?[]:data)};
            result.Content.Headers.ContentLength=data.Length;result.Headers.ETag=new("\"synthetic-original-v1\"");result.Headers.AcceptRanges.Add("bytes");
            if(request.Method==HttpMethod.Get&&name.EndsWith(".bin",StringComparison.Ordinal)){Numeric++;Started.TrySetResult();if(Block)await Task.Delay(Timeout.Infinite,token);}
            return result;
        }
    }
    private static async Task<LibraryDownloadJob> Terminal(ScienceStore science,Guid id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(true){var j=science.LibraryJob(id);if(j.State is "Completed" or "Failed" or "Interrupted")return j;await Task.Delay(25,timeout.Token);}
    }
    [Theory][InlineData("classic",224)][InlineData("ng",425)]
    public async Task ExactOriginalPairUsesApprovedDurableJobAndAutomaticImporterThenReusesOffline(string family,int bands)
    {
        var item=NasaFixtureFiles.Item(family);var folder=Path.Combine(root,"originals");NasaFixtureFiles.Cube(folder,item,bands);
        var handler=new Handler(folder,family);var vault=new Credentials();using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,auto,settings,vault);
        var request=new NasaDownloadRequest(new(item.Provider,item.Scene,item.Revision),10485760);var preview=await jobs.PreviewNasa(request);
        Assert.Equal("Awaiting Approval",preview.State);Assert.Equal(0,handler.Numeric);Assert.Single(preview.Preview.Work);Assert.NotNull(preview.Preview.Nasa);Assert.Equal(family=="ng"?4:2,preview.Preview.Nasa.Files.Length);
        if(family=="ng")Assert.Single(preview.Preview.Nasa.Companions);
        Assert.DoesNotContain(Token,JsonSerializer.Serialize(preview,WireJson.Options));await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Approve(preview.Id,"wrong"));
        await jobs.Approve(preview.Id,preview.Preview.SelectionHash);var completed=await Terminal(science,preview.Id);Assert.Equal("Completed",completed.State);
        var source=Assert.Single(science.Sources());Assert.Equal(bands,source.Manifest.Bands.Length);Assert.Equal(item.Scene,source.Manifest.SceneId);Assert.Equal("nasa-earthdata-original-envi",source.Manifest.Acquisition!.Workflow);
        Assert.True(completed.DownloadedBytes>0);Assert.All(source.Files,f=>Assert.Equal(64,f.Sha256.Length));Assert.Empty(science.Products());Assert.Empty(science.Datasets());
        using(var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,source.RelativeDirectory),source.Manifest.Bands[2]))Assert.Equal(410,raster.NativeWavelengthNm);
        var original=File.ReadAllBytes(Path.Combine(folder,source.Manifest.Bands[0].File));Assert.Equal(original,File.ReadAllBytes(Path.Combine(science.DirectoryPath,source.RelativeDirectory,source.Manifest.Bands[0].File)));
        Assert.DoesNotContain(Token,JsonSerializer.Serialize(source,WireJson.Options));Assert.DoesNotContain("Signature=",JsonSerializer.Serialize(source,WireJson.Options));
        vault.Clear();settings.Save(new());var reused=await jobs.PreviewNasa(request);Assert.Equal(0,reused.Preview.EstimatedTransferBytes);var before=handler.Numeric;
        await jobs.Approve(reused.Id,reused.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,reused.Id)).State);Assert.Equal(before,handler.Numeric);Assert.Single(science.Sources());
    }
    [Fact]public async Task ActivePauseAndRestartKeepApprovedIntentAndFlightPriorityWithoutMissionCreation()
    {
        var item=NasaFixtureFiles.Item("classic");var folder=Path.Combine(root,"originals");NasaFixtureFiles.Cube(folder,item);
        var handler=new Handler(folder,"classic"){Block=true};using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);var vault=new Credentials();Guid id;
        using(var jobs=new ScientificLibraryJobs(science,catalog,auto,settings,vault))
        {
            var preview=await jobs.PreviewNasa(new(new(item.Provider,item.Scene,item.Revision),10485760));id=preview.Id;auto.FlightActive(true);await jobs.Approve(id,preview.Preview.SelectionHash);await Task.Delay(350);Assert.Equal(0,handler.Numeric);
            auto.FlightActive(false);await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));await jobs.Control(id,"pause");Assert.Equal("Paused",science.LibraryJob(id).State);Assert.Empty(science.Sources());
        }
        science.RecoverLibraryJobs();handler.Block=false;using(var recovered=new ScientificLibraryJobs(science,catalog,auto,settings,vault)){await recovered.Control(id,"resume");Assert.Equal("Completed",(await Terminal(science,id)).State);}
        Assert.Single(science.Sources());Assert.Empty(science.Datasets());
    }
    [Fact]public async Task AuthorizationCapsChangesAndProviderChecksumFailuresNeverPublishSource()
    {
        var item=NasaFixtureFiles.Item("ng");var folder=Path.Combine(root,"originals");NasaFixtureFiles.Cube(folder,item,425);
        var handler=new Handler(folder,"ng");var vault=new Credentials();using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,auto,settings,vault);
        var request=new NasaDownloadRequest(new(item.Provider,item.Scene,item.Revision),10485760);
        vault.Clear();await Assert.ThrowsAsync<IOException>(()=>jobs.PreviewNasa(request));vault.Value=Token;handler.Denied=true;var denial=await Assert.ThrowsAsync<IOException>(()=>jobs.PreviewNasa(request));Assert.Contains("Earthdata",denial.Message);Assert.DoesNotContain(Token,denial.Message);
        handler.Denied=false;await Assert.ThrowsAsync<ArgumentException>(()=>jobs.PreviewNasa(request with{MaximumTransferBytes=21474836480}));
        var preview=await jobs.PreviewNasa(request);settings.Save(settings.Get().Preferences with{NasaOriginalMaximumBytes=2097152});await Assert.ThrowsAsync<InvalidOperationException>(()=>jobs.Approve(preview.Id,preview.Preview.SelectionHash));settings.Save(new(true,true));
        var checksum=await jobs.PreviewNasa(request);handler.BadChecksum=true;await jobs.Approve(checksum.Id,checksum.Preview.SelectionHash);var failed=await Terminal(science,checksum.Id);Assert.Equal("Failed",failed.State);Assert.Contains("SHA-256",failed.Error);Assert.Empty(science.Sources());
        handler.BadChecksum=false;var replacement=await jobs.PreviewNasa(request);await jobs.Approve(replacement.Id,replacement.Preview.SelectionHash);Assert.Equal("Completed",(await Terminal(science,replacement.Id)).State);
    }
    [Theory][InlineData("https://evil.test/data.bin")][InlineData("https://data.ornldaac.earthdata.nasa.gov/protected/other/file.bin")][InlineData("https://data.ornldaac.earthdata.nasa.gov/protected/aviris/AVIRIS-NG_L2_Reflectance/data/file.bin?token=secret")]
    public void ArbitraryAndCredentialBearingUrlsNeverBecomeNasaFetches(string value)=>Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaOriginal(new(value)));
    [Theory][InlineData("classic",224)][InlineData("ng",425)]
    public async Task UnsupportedOriginalHeaderFailsBeforeLargeBinaryIsRequested(string family,int bands)
    {
        var item=NasaFixtureFiles.Item(family);var folder=Path.Combine(root,"originals");var header=NasaFixtureFiles.Cube(folder,item,bands);
        File.WriteAllText(header,File.ReadAllText(header).Replace("bands = "+bands,"bands = "+(bands-1),StringComparison.Ordinal));
        var handler=new Handler(folder,family);using var auto=new AutomaticScientificData(science,settings,new(handler,cacheEnabled:false));var catalog=new ScientificCatalog(auto,science,settings);using var jobs=new ScientificLibraryJobs(science,catalog,auto,settings,new Credentials());
        var job=await jobs.PreviewNasa(new(new(item.Provider,item.Scene,item.Revision),10485760));await jobs.Approve(job.Id,job.Preview.SelectionHash);Assert.Equal("Failed",(await Terminal(science,job.Id)).State);Assert.Equal(0,handler.Numeric);Assert.Empty(science.Sources());
    }
    [Fact]public void UserTokenAndNoCredentialsStatusNeverExposeSecretsOrInventAuthorization()
    {
        Assert.Throws<ArgumentException>(()=>EarthdataConnection.ValidateToken("not a token\r\nAuthorization: secret"));var vault=new Credentials();var status=EarthdataConnection.Status(vault);Assert.True(status.Connected);Assert.DoesNotContain(Token,JsonSerializer.Serialize(status));vault.Clear();Assert.False(EarthdataConnection.Status(vault).Connected);
        var old=JsonSerializer.Deserialize<ScientificAccessPreferences>("{}",WireJson.Options)!;Assert.Equal(67108864,old.MaximumDownloadBytes);Assert.Equal(1073741824,old.NasaOriginalMaximumBytes);
    }
    public void Dispose(){science.Dispose();Directory.Delete(root,true);}
}
