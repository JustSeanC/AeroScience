using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AeroScience.Missions;

public sealed class ScientificDownloadBudget(long maximum,Action<long>? progress=null,Action<long>? ensureCapacity=null)
{
    private long consumed,authorized;
    public long Bytes=>Interlocked.Read(ref consumed);
    public long Maximum=>maximum;
    public void EnsureCapacity(long bytes)
    {
        if(bytes<0||bytes>maximum-Bytes)throw new IOException("Scientific per-download limit reached. Select a smaller supported subset or change the per-download limit; other protections remain enabled.");
        ensureCapacity?.Invoke(checked(Bytes+bytes));authorized=Math.Max(authorized,Bytes+bytes);
    }
    public void Charge(long bytes)
    {
        if(bytes<0)throw new ArgumentOutOfRangeException(nameof(bytes));if(bytes>authorized-Bytes)EnsureCapacity(bytes);Interlocked.Add(ref consumed,bytes);progress?.Invoke(Bytes);
    }

}
// Provider endpoints are fixed and HTTPS-only. Catalog-supplied URLs cannot become
// arbitrary local/network fetches; redirects and cookies are disabled.
public sealed class ScientificHttp : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim pace=new(1);
    private DateTimeOffset last;
    private readonly object metricsGate=new();
    private readonly Dictionary<string,(long Requests,long Metadata,long Numeric,long Retries,long Hits,long Reused)> counters=[];
    private readonly Dictionary<string,(DateTimeOffset At,byte[] Bytes)> ranges=[];
    private readonly Dictionary<string,(DateTimeOffset At,byte[] Bytes)> metadata=[];
    private readonly Func<DateTimeOffset> cacheClock;
    private long cacheBytes,metadataBytes;
    private void Count(string host,long requests=0,long meta=0,long numeric=0,long retries=0,long hits=0,long reused=0)
    {lock(metricsGate){var c=counters.GetValueOrDefault(host);counters[host]=(c.Requests+requests,c.Metadata+meta,c.Numeric+numeric,c.Retries+retries,c.Hits+hits,c.Reused+reused);}}
    public object Diagnostics(){lock(metricsGate)return counters.Select(c=>new{providerHost=c.Key,requests=c.Value.Requests,metadataBytes=c.Value.Metadata,numericBytes=c.Value.Numeric,retries=c.Value.Retries,cacheHits=c.Value.Hits,reusedBytes=c.Value.Reused}).ToArray();}
    internal void Received(Uri uri,int bytes,bool range)=>Count(uri.Host,meta:range?0:bytes,numeric:range?bytes:0);
    internal byte[]? CachedRange(Uri uri,string? etag,long index)
    {
        if(!CacheEnabled||etag is null)return null;
        lock(metricsGate){if(ranges.TryGetValue(uri.GetLeftPart(UriPartial.Path)+"|"+etag+"|"+index,out var c)&&cacheClock()-c.At<TimeSpan.FromMinutes(10)){Count(uri.Host,hits:1,reused:c.Bytes.Length);return c.Bytes;}return null;}
    }
    internal void CacheRange(Uri uri,string? etag,long index,byte[] bytes)
    {
        if(!CacheEnabled||etag is null)return;lock(metricsGate){var key=uri.GetLeftPart(UriPartial.Path)+"|"+etag+"|"+index;if(ranges.Remove(key,out var old))cacheBytes-=old.Bytes.Length;while(cacheBytes+bytes.Length>67108864&&ranges.Count>0){var first=ranges.First();cacheBytes-=first.Value.Bytes.Length;ranges.Remove(first.Key);}ranges[key]=(cacheClock(),bytes);cacheBytes+=bytes.Length;}
    }

    public bool CacheEnabled {get;}
    public ScientificHttp(HttpMessageHandler? handler=null,bool cacheEnabled=true,Func<DateTimeOffset>? cacheClock=null)
    {
        CacheEnabled=cacheEnabled;this.cacheClock=cacheClock??(()=>DateTimeOffset.UtcNow);
        http=handler is null?new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,MaxResponseDrainSize=0,MaxConnectionsPerServer=2,ConnectTimeout=TimeSpan.FromSeconds(20)}):new(handler);
        http.Timeout=TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AeroScience/0.5.0 (+https://github.com/JustSeanC/AeroScience)");
    }
    public static void ValidateUrl(Uri uri)
    {
        if(uri.AbsoluteUri.Length>16384||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.Host is not
            ("earth-search.aws.element84.com" or "sentinel-cogs.s3.us-west-2.amazonaws.com" or
            "planetarycomputer.microsoft.com" or "landsateuwest.blob.core.windows.net" or
            "elevationeuwest.blob.core.windows.net" or "elevation.nationalmap.gov" or "cmr.earthdata.nasa.gov"))
            throw new IOException("Scientific provider returned an unsupported endpoint. No request was sent to it.");
    }
    public async Task<HttpResponseMessage> Send(Uri uri,long? start,long? end,string? etag,CancellationToken token)
    {
        ValidateUrl(uri);
        for(var attempt=0;attempt<3;attempt++)
        {
            await pace.WaitAsync(token);
            try {var delay=last.AddMilliseconds(180)-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay,token);last=DateTimeOffset.UtcNow;}
            finally{pace.Release();}
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if(start is {} from)request.Headers.Range=new RangeHeaderValue(from,end);
            if(etag is not null)request.Headers.TryAddWithoutValidation("If-Match",etag);
            Count(uri.Host,requests:1,retries:attempt>0?1:0);
            HttpResponseMessage response;
            try{response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);}
            catch(HttpRequestException) when(attempt<2){await Task.Delay(TimeSpan.FromSeconds(2*(attempt+1)),token);continue;}
            if((response.StatusCode==(HttpStatusCode)429||(int)response.StatusCode>=500)&&attempt<2)
            {
                var retry=response.Headers.RetryAfter?.Delta??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow)??TimeSpan.FromSeconds(2*(attempt+1));
                response.Dispose();
                if(retry>TimeSpan.FromSeconds(30))throw new IOException("Provider rate limit: retry later. The provider requested a long pause.");
                await Task.Delay(retry>TimeSpan.Zero?retry:TimeSpan.FromSeconds(1),token);continue;
            }
            return response;
        }
        throw new IOException("Scientific provider is unavailable. Retry later or use verified local sources.");
    }
    public async Task<byte[]> Bytes(Uri uri,int maximum,ScientificDownloadBudget budget,CancellationToken token)
    {
        budget.EnsureCapacity(1);budget.EnsureCapacity(Math.Min(65536,Math.Min(maximum,budget.Maximum-budget.Bytes)));
        using var response=await Send(uri,null,null,null,token);
        if(!response.IsSuccessStatusCode)throw new IOException($"Scientific provider request failed (HTTP {(int)response.StatusCode}). Retry later; local history remains available.");
        if(response.Content.Headers.ContentLength>maximum)throw new IOException("Provider response exceeds the supported bounded size.");
        using var stream=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[65536];
        var declared=response.Content.Headers.ContentLength;
        while(true)
        {
            if(declared is {} total&&output.Length==total)break;
            var remaining=Math.Min((long)maximum-output.Length,budget.Maximum-budget.Bytes);
            var count=(int)Math.Min(buffer.Length,remaining);
            if(declared is {} expected)count=(int)Math.Min(count,expected-output.Length);
            if(count<=0){budget.EnsureCapacity(1);count=1;}
            budget.EnsureCapacity(count);var n=await stream.ReadAsync(buffer.AsMemory(0,count),token);if(n==0)break;
            budget.Charge(n);Received(uri,n,false);if(output.Length+n>maximum)throw new IOException("Provider response exceeded its size bound.");output.Write(buffer,0,n);
        }
        if(declared is {} length&&output.Length!=length)throw new EndOfStreamException("Provider response ended before its declared size; received bytes remain accounted.");
        return output.ToArray();
    }
    public async Task<JsonDocument> Json(string uri,ScientificDownloadBudget budget,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();ValidateUrl(new(uri));
        // Authentication responses are never cached. Catalog/search responses have a short TTL.
        var cacheable=CacheEnabled&&!uri.Contains("/sas/",StringComparison.Ordinal);
        lock(metricsGate)if(cacheable&&metadata.TryGetValue(uri,out var c)&&cacheClock()-c.At<TimeSpan.FromMinutes(5)){Count(new Uri(uri).Host,hits:1,reused:c.Bytes.Length);return JsonDocument.Parse(c.Bytes);}
        var bytes=await Bytes(new(uri),8388608,budget,token);
        using(var parsed=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64})){}
        if(cacheable)lock(metricsGate){if(metadata.Remove(uri,out var old))metadataBytes-=old.Bytes.Length;while((metadata.Count>=32||metadataBytes+bytes.Length>16777216)&&metadata.Count>0){var first=metadata.First();metadataBytes-=first.Value.Bytes.Length;metadata.Remove(first.Key);}metadata[uri]=(cacheClock(),bytes);metadataBytes+=bytes.Length;}
        return JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});
    }
    public async Task<Uri> Authorize(Uri unsigned,ScientificDownloadBudget budget,CancellationToken token)
    {
        ValidateUrl(unsigned);
        if(unsigned.Host is not("landsateuwest.blob.core.windows.net" or "elevationeuwest.blob.core.windows.net"))return unsigned;
        using var signed=await Json("https://planetarycomputer.microsoft.com/api/sas/v1/sign?href="+Uri.EscapeDataString(unsigned.AbsoluteUri),budget,token);
        var uri=new Uri(signed.RootElement.GetProperty("href").GetString()!);ValidateUrl(uri);
        if(uri.GetLeftPart(UriPartial.Path)!=unsigned.GetLeftPart(UriPartial.Path))throw new IOException("Provider authorization changed the requested asset.");
        return uri; // Short-lived SAS exists in memory only; never written to manifests/logs.
    }
    public void Dispose(){http.Dispose();pace.Dispose();}
}

// Seekable 64 KiB remote COG cache, bounded both on wire and in memory. Only used
// by the background science worker, never by the 1 Hz flight-recorder path.
public sealed class ScientificRangeStream : Stream
{
    private readonly ScientificHttp http;private readonly Uri original;private Uri current;
    private readonly ScientificDownloadBudget budget;private readonly CancellationToken token;
    private readonly ScientificRangeCheckpoint? checkpoint;
    private readonly Dictionary<long,byte[]> cache=[];private long length=-1,position;private string? etag;
    public int Requests {get;private set;}
    public ScientificRangeStream(ScientificHttp http,Uri original,ScientificDownloadBudget budget,CancellationToken token)
    {this.http=http;this.original=original;this.budget=budget;this.token=token;current=http.Authorize(original,budget,token).GetAwaiter().GetResult();Fetch(0);}
    internal ScientificRangeStream(ScientificHttp http,Uri original,ScientificDownloadBudget budget,CancellationToken token,ScientificRangeCheckpoint checkpoint)
    {this.http=http;this.original=original;this.budget=budget;this.token=token;this.checkpoint=checkpoint;current=http.Authorize(original,budget,token).GetAwaiter().GetResult();Fetch(0);}
    private byte[] Fetch(long index)
    {
        token.ThrowIfCancellationRequested();if(cache.TryGetValue(index,out var existing))return existing;
        // Each new stream first obtains a fresh exact range/ETag; only matching versions share blocks.
        if(index!=0&&http.CachedRange(original,etag,index) is {} shared){cache[index]=shared;return shared;}
        if(index!=0&&checkpoint?.Read(index) is {} retained){cache[index]=retained;return retained;}
        var start=index*65536;var end=length<0?start+65535:Math.Min(length-1,start+65535);
        for(var auth=0;auth<2;auth++)
        {
            if(++Requests>2048)throw new IOException("The remote asset requires too many requests. Select a smaller subset.");
            budget.EnsureCapacity(end-start+1);
            using var response=http.Send(current,start,end,etag,token).GetAwaiter().GetResult();
            if((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)&&auth==0&&original.Host.EndsWith(".blob.core.windows.net",StringComparison.Ordinal))
            {current=http.Authorize(original,budget,token).GetAwaiter().GetResult();continue;}
            var range=response.Content.Headers.ContentRange;
            if(response.Content.Headers.ContentEncoding.Any(c=>!c.Equals("identity",StringComparison.OrdinalIgnoreCase))||response.StatusCode!=HttpStatusCode.PartialContent||range?.Unit!="bytes"||range.From!=start||range.To!=end&&!(length<0&&range.To==range.Length-1)||range.Length is not >0||range.Length>17179869184||range.To> end||range.To<start)
                throw new IOException($"Provider did not honor the exact bounded HTTP byte range (HTTP {(int)response.StatusCode}; requested {start}–{end}, returned {range?.From}–{range?.To}/{range?.Length}). The asset was refused; retry later.");
            if(length>=0&&range.Length!=length||etag is not null&&response.Headers.ETag?.ToString()!=etag)throw new IOException("Scientific asset changed during retrieval. Retry as a new acquisition.");
            length=range.Length.Value;etag??=response.Headers.ETag?.ToString();
            if(index==0)checkpoint?.Establish(etag,length);
            var count=checked((int)(range.To!.Value-start+1));budget.EnsureCapacity(count);
            var bytes=new byte[count];using var stream=response.Content.ReadAsStream(token);var received=0;
            while(received<count){var n=stream.Read(bytes,received,count-received);if(n==0)throw new EndOfStreamException("Provider range ended early; actual received bytes remain accounted.");budget.Charge(n);http.Received(original,n,true);received+=n;}
            if(stream.ReadByte()!=-1)throw new IOException("Provider returned more bytes than the approved range.");
            checkpoint?.Save(index,bytes);http.CacheRange(original,etag,index,bytes);if(cache.Count>=256)cache.Remove(cache.Keys.First());cache[index]=bytes;return bytes;
        }
        throw new IOException("Scientific asset authorization expired. Retry when the provider is available.");
    }
    public override int Read(byte[] buffer,int offset,int count)
    {
        token.ThrowIfCancellationRequested();if(count<0||count>33554432)throw new IOException("Remote TIFF read exceeds the supported block size.");
        var total=0;while(count>0&&position<length){var bytes=Fetch(position/65536);var within=(int)(position%65536);var take=(int)Math.Min(count,bytes.Length-within);if(take<=0)throw new IOException("Truncated range.");Buffer.BlockCopy(bytes,within,buffer,offset,take);position+=take;offset+=take;count-=take;total+=take;}return total;
    }
    public override long Seek(long offset,SeekOrigin origin){var next=checked((origin==SeekOrigin.Begin?0:origin==SeekOrigin.Current?position:length)+offset);if(next<0||next>length)throw new IOException("Invalid remote TIFF offset.");return position=next;}
    public override long Position{get=>position;set=>Seek(value,SeekOrigin.Begin);}public override long Length=>length;
    public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
    public override void Flush(){}public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
}
