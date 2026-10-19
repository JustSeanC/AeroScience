using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AeroScience.Missions;

public sealed class ScientificDownloadBudget(long maximum,Action<long>? progress=null)
{
    private long consumed;
    public long Bytes=>Interlocked.Read(ref consumed);
    public long Maximum=>maximum;
    public void Charge(long bytes)
    {
        if(bytes<0||Interlocked.Add(ref consumed,bytes)>maximum)throw new IOException("Scientific download limit reached. Select a smaller area or raise the per-download limit in Settings; no observations were fabricated.");
        progress?.Invoke(Bytes);
    }
}
// Provider endpoints are fixed and HTTPS-only. Catalog-supplied URLs cannot become
// arbitrary local/network fetches; redirects and cookies are disabled.
public sealed class ScientificHttp : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim pace=new(1);
    private DateTimeOffset last;
    public ScientificHttp(HttpMessageHandler? handler=null)
    {
        http=handler is null?new(new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,MaxResponseDrainSize=0,MaxConnectionsPerServer=2,ConnectTimeout=TimeSpan.FromSeconds(20)}):new(handler);
        http.Timeout=TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AeroScience/0.3.1 (+https://github.com/JustSeanC/AeroScience)");
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
        using var response=await Send(uri,null,null,null,token);
        if(!response.IsSuccessStatusCode)throw new IOException($"Scientific provider request failed (HTTP {(int)response.StatusCode}). Retry later; local history remains available.");
        if(response.Content.Headers.ContentLength>maximum)throw new IOException("Provider response exceeds the supported bounded size.");
        using var stream=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[65536];
        while(true){var n=await stream.ReadAsync(buffer,token);if(n==0)break;budget.Charge(n);if(output.Length+n>maximum)throw new IOException("Provider response exceeded its size bound.");output.Write(buffer,0,n);}
        return output.ToArray();
    }
    public async Task<JsonDocument> Json(string uri,ScientificDownloadBudget budget,CancellationToken token)=>JsonDocument.Parse(await Bytes(new(uri),8388608,budget,token),new JsonDocumentOptions{MaxDepth=64});
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
    private readonly Dictionary<long,byte[]> cache=[];private long length=-1,position;private string? etag;
    public int Requests {get;private set;}
    public ScientificRangeStream(ScientificHttp http,Uri original,ScientificDownloadBudget budget,CancellationToken token)
    {this.http=http;this.original=original;this.budget=budget;this.token=token;current=http.Authorize(original,budget,token).GetAwaiter().GetResult();Fetch(0);}
    private byte[] Fetch(long index)
    {
        token.ThrowIfCancellationRequested();if(cache.TryGetValue(index,out var existing))return existing;
        var start=index*65536;var end=length<0?start+65535:Math.Min(length-1,start+65535);
        for(var auth=0;auth<2;auth++)
        {
            if(++Requests>2048)throw new IOException("The remote asset requires too many requests. Select a smaller subset.");
            using var response=http.Send(current,start,end,etag,token).GetAwaiter().GetResult();
            if((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)&&auth==0&&original.Host.EndsWith(".blob.core.windows.net",StringComparison.Ordinal))
            {current=http.Authorize(original,budget,token).GetAwaiter().GetResult();continue;}
            var range=response.Content.Headers.ContentRange;
            if(response.Content.Headers.ContentEncoding.Any(c=>!c.Equals("identity",StringComparison.OrdinalIgnoreCase))||response.StatusCode!=HttpStatusCode.PartialContent||range?.Unit!="bytes"||range.From!=start||range.To!=end&&!(length<0&&range.To==range.Length-1)||range.Length is not >0||range.Length>17179869184||range.To> end||range.To<start)
                throw new IOException($"Provider did not honor the exact bounded HTTP byte range (HTTP {(int)response.StatusCode}; requested {start}–{end}, returned {range?.From}–{range?.To}/{range?.Length}). The asset was refused; retry later.");
            if(length>=0&&range.Length!=length||etag is not null&&response.Headers.ETag?.ToString()!=etag)throw new IOException("Scientific asset changed during retrieval. Retry as a new acquisition.");
            length=range.Length.Value;etag??=response.Headers.ETag?.ToString();
            var count=checked((int)(range.To!.Value-start+1));budget.Charge(count);
            var bytes=new byte[count];using var stream=response.Content.ReadAsStream(token);stream.ReadExactly(bytes);
            if(stream.ReadByte()!=-1)throw new IOException("Provider returned more bytes than the approved range.");
            if(cache.Count>=256)cache.Remove(cache.Keys.First());cache[index]=bytes;return bytes;
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
