using System.Net;
using System.Net.Http.Headers;
using AeroScience.Missions;
using AeroScience.Shared;
using Xunit;
namespace AeroScience.Tests;

public sealed class NasaTransferTests:IDisposable
{
    const string Url="https://data.ornldaac.earthdata.nasa.gov/protected/aviris/AVIRIS-Classic_L2_Reflectance/data/f080611t01p00r13_corr_v1h_img.bin";
    readonly string folder=Path.Combine(Path.GetTempPath(),"nasa-transfer-"+Guid.NewGuid().ToString("N"));
    readonly NasaDirectAcquisitionTests.Credentials credentials=new();
    public NasaTransferTests()=>Directory.CreateDirectory(folder);
    sealed class Handler:HttpMessageHandler
    {
        public byte[] Data=Enumerable.Range(0,3*1048576+17).Select(i=>(byte)(i%251)).ToArray();
        public string Etag="\"original-v1\"";public bool Fail,IgnoreRange,WrongRange;public long? Start;public int Requests;
        public Func<HttpRequestMessage,HttpResponseMessage>? Override;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;if(Override is not null)return Task.FromResult(Override(request));Assert.Equal(NasaDirectAcquisitionTests.Token,request.Headers.Authorization?.Parameter);
            Start=request.Headers.Range?.Ranges.Single().From;var from=request.Method==HttpMethod.Get&&!IgnoreRange?Start??0:0;
            var response=new HttpResponseMessage(from>0?HttpStatusCode.PartialContent:HttpStatusCode.OK){Content=request.Method==HttpMethod.Head?new ByteArrayContent([]):Fail?new StreamContent(new BrokenStream(Data)):new ByteArrayContent(Data[(int)from..])};
            response.Content.Headers.ContentLength=request.Method==HttpMethod.Head?Data.Length:Data.Length-from;response.Headers.ETag=new(Etag);response.Headers.AcceptRanges.Add("bytes");
            if(from>0)response.Content.Headers.ContentRange=new(WrongRange?from-1:from,Data.Length-1,Data.Length);return Task.FromResult(response);
        }
    }
    sealed class BrokenStream(byte[] data):MemoryStream(data,false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(Position>=1048576)throw new IOException("Explicit synthetic interrupted stream");return base.ReadAsync(buffer[..Math.Min(buffer.Length,1048576)],token);}
    }
    [Fact]public async Task InterruptedTransferResumesOnlyVerifiedPrefixAndCountsActualBytes()
    {
        var handler=new Handler();using var http=new ScientificHttp(handler,cacheEnabled:false);var file=await http.DescribeNasaFile(new(Path.GetFileName(new Uri(Url).AbsolutePath),Url,"Original numeric binary"),credentials,default);
        handler.Fail=true;var first=new ScientificDownloadBudget(10000000);await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,first,default));Assert.Equal(1048576,first.Bytes);
        handler.Fail=false;var rest=new ScientificDownloadBudget(10000000);await http.DownloadNasaFile(file,folder,credentials,rest,default);Assert.Equal(1048576,handler.Start);Assert.Equal(handler.Data.Length-first.Bytes,rest.Bytes);Assert.Equal(handler.Data,File.ReadAllBytes(Path.Combine(folder,file.Name)));
        var reused=new ScientificDownloadBudget(10000000);await http.DownloadNasaFile(file,folder,credentials,reused,default);Assert.Equal(0,reused.Bytes);
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task ChangedIdentityOrWrongContentRangeCannotAppendPartialOriginal(bool changed,bool wrongRange)
    {
        var handler=new Handler();using var http=new ScientificHttp(handler);var file=await http.DescribeNasaFile(new(Path.GetFileName(new Uri(Url).AbsolutePath),Url,"Original numeric binary"),credentials,default);handler.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,new(10000000),default));var prefix=File.ReadAllBytes(Path.Combine(folder,file.Name+".part"));handler.Fail=false;handler.WrongRange=wrongRange;if(changed)handler.Etag="\"revised-v2\"";
        await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,new(10000000),default));Assert.Equal(prefix,File.ReadAllBytes(Path.Combine(folder,file.Name+".part")));Assert.False(File.Exists(Path.Combine(folder,file.Name)));
    }
    [Fact]public async Task CrashTailBeyondDurableCheckpointIsDiscardedBeforeSafeResume()
    {
        var handler=new Handler();using var http=new ScientificHttp(handler);var file=await http.DescribeNasaFile(new(Path.GetFileName(new Uri(Url).AbsolutePath),Url,"Original numeric binary"),credentials,default);handler.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,new(10000000),default));
        // A killed process may leave bytes newer than its last durable checkpoint.
        using(var tail=new FileStream(Path.Combine(folder,file.Name+".part"),FileMode.Append))tail.Write(new byte[65536]);
        handler.Fail=false;var resumed=new ScientificDownloadBudget(10000000);await http.DownloadNasaFile(file,folder,credentials,resumed,default);
        Assert.Equal(1048576,handler.Start);Assert.Equal(handler.Data.Length-1048576,resumed.Bytes);Assert.Equal(handler.Data,File.ReadAllBytes(Path.Combine(folder,file.Name)));
    }
    [Fact]public async Task IgnoredRangeRestartsSafelyAndDiskTamperingCannotBeTreatedAsVerifiedPrefix()
    {
        var handler=new Handler();using var http=new ScientificHttp(handler);var file=await http.DescribeNasaFile(new(Path.GetFileName(new Uri(Url).AbsolutePath),Url,"Original numeric binary"),credentials,default);handler.Fail=true;
        await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,new(10000000),default));handler.Fail=false;handler.IgnoreRange=true;var budget=new ScientificDownloadBudget(10000000);
        await http.DownloadNasaFile(file,folder,credentials,budget,default);Assert.Equal(handler.Data.Length,budget.Bytes);Assert.Equal(handler.Data,File.ReadAllBytes(Path.Combine(folder,file.Name)));
        File.WriteAllBytes(Path.Combine(folder,file.Name),new byte[handler.Data.Length]);var repair=new ScientificDownloadBudget(10000000);await http.DownloadNasaFile(file,folder,credentials,repair,default);Assert.Equal(handler.Data.Length,repair.Bytes);Assert.Equal(handler.Data,File.ReadAllBytes(Path.Combine(folder,file.Name)));
    }
    [Fact]public async Task OfficialSignedRedirectNeverReceivesBearerAndUnsafeRedirectIsNotRequested()
    {
        var redirect=new Uri("https://d3o6w55j8uz1ro.cloudfront.net/s3-abcdef/ornl-cumulus-prod-protected.s3.us-west-2.amazonaws.com/aviris/AVIRIS-Classic_L2_Reflectance/data/f080611t01p00r13_corr_v1h_img.bin?Signature=synthetic-fixture");
        var seen=new List<(string Host,bool Bearer)>();var handler=new Handler{Override=request=>
        {
            seen.Add((request.RequestUri!.Host,request.Headers.Authorization is not null));
            if(request.RequestUri.Host=="data.ornldaac.earthdata.nasa.gov")return new(HttpStatusCode.Found){Headers={Location=redirect}};
            return new(HttpStatusCode.OK){Content=new ByteArrayContent([1,2,3])};
        }};
        using var http=new ScientificHttp(handler);using var response=await http.SendNasa(new(Url),HttpMethod.Get,credentials,null,null,null,default);
        Assert.Equal(new[]{("data.ornldaac.earthdata.nasa.gov",true),("d3o6w55j8uz1ro.cloudfront.net",false)},seen);
        handler.Override=_=>new(HttpStatusCode.Found){Headers={Location=new("https://evil.example/stolen")}};var before=handler.Requests;await Assert.ThrowsAsync<IOException>(()=>http.SendNasa(new(Url),HttpMethod.Get,credentials,null,null,null,default));Assert.Equal(before+1,handler.Requests);
    }
    [Theory][InlineData("Classic","f080611t01p00r13_corr_v1h_img.hdr")][InlineData("NG","ang20140621t190550_corr_v1g_img.bin")]
    public async Task OfficialHeadRedirectIsMethodBoundAndNeverReceivesEarthdataCredential(string family,string name)
    {
        var original=new Uri($"https://data.ornldaac.earthdata.nasa.gov/protected/aviris/AVIRIS-{family}_L2_Reflectance/data/{name}");
        var observed=new Uri($"https://d3o6w55j8uz1ro.cloudfront.net/head-d0f68fa49c8cba12794bb586349f2341/ornl-cumulus-prod-protected.s3.us-west-2.amazonaws.com/aviris/AVIRIS-{family}_L2_Reflectance/data/{name}?Signature=synthetic-test-only");
        ScientificHttp.ValidateNasaRedirect(original,observed,HttpMethod.Head);
        Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaRedirect(original,observed,HttpMethod.Get));
        Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaRedirect(original,new(observed.AbsoluteUri.Replace("prod-protected","prod-public")),HttpMethod.Head));
        Assert.Throws<IOException>(()=>ScientificHttp.ValidateNasaRedirect(original,new(observed.AbsoluteUri.Replace(name,"another.bin")),HttpMethod.Head));
        var seen=new List<(HttpMethod Method,bool Credential)>();var handler=new Handler{Override=request=>{
            seen.Add((request.Method,request.Headers.Authorization is not null));
            if(request.RequestUri!.Host==original.Host)return new(HttpStatusCode.SeeOther){Headers={Location=observed}};
            return new(HttpStatusCode.OK){Content=new ByteArrayContent([])};
        }};
        using var http=new ScientificHttp(handler);using var response=await http.SendNasa(original,HttpMethod.Head,credentials,null,null,null,default);
        Assert.Equal(new[]{(HttpMethod.Head,true),(HttpMethod.Head,false)},seen);
    }
    [Fact]public async Task BudgetAndCancellationCannotPublishIncompleteFile()
    {
        var handler=new Handler();using var http=new ScientificHttp(handler);var file=await http.DescribeNasaFile(new(Path.GetFileName(new Uri(Url).AbsolutePath),Url,"Original numeric binary"),credentials,default);
        await Assert.ThrowsAsync<IOException>(()=>http.DownloadNasaFile(file,folder,credentials,new(100000),default));Assert.False(File.Exists(Path.Combine(folder,file.Name)));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>http.DownloadNasaFile(file,folder,credentials,new(10000000),cancelled.Token));Assert.False(File.Exists(Path.Combine(folder,file.Name)));
    }
    [Fact]public async Task RateLimitsUseSharedBoundedRetriesAndLoginRedirectsNeverReceiveCredential()
    {
        var attempt=0;var handler=new Handler{Override=_=>++attempt<=2?new HttpResponseMessage(attempt==1?HttpStatusCode.TooManyRequests:HttpStatusCode.ServiceUnavailable){Headers={RetryAfter=new RetryConditionHeaderValue(TimeSpan.Zero)}}:new(HttpStatusCode.OK){Content=new ByteArrayContent([1])}};
        using var http=new ScientificHttp(handler);using var result=await http.SendNasa(new(Url),HttpMethod.Get,credentials,null,null,null,default);Assert.Equal(3,handler.Requests);
        handler.Override=_=>new(HttpStatusCode.SeeOther){Headers={Location=new("https://urs.earthdata.nasa.gov/oauth/authorize?code=synthetic")}};
        var before=handler.Requests;var denied=await Assert.ThrowsAsync<IOException>(()=>http.SendNasa(new(Url),HttpMethod.Get,credentials,null,null,null,default));Assert.Equal(before+1,handler.Requests);Assert.Contains("reconnect",denied.Message);Assert.DoesNotContain("code=",denied.Message);
    }
    public void Dispose()=>Directory.Delete(folder,true);
}
