using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed partial class ScientificHttp
{
    public static void ValidateNasaOriginal(Uri uri)
    {
        if(!ScientificCatalog.OfficialNasaLink(uri.AbsoluteUri)||uri.Host!="data.ornldaac.earthdata.nasa.gov"||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,@"^/(protected|public)/aviris/AVIRIS-(Classic|NG)_L2_Reflectance/data/[A-Za-z0-9_.-]+\.(hdr|bin|sha256)$"))
            throw ScientificFailure.Create(ScientificFailureKind.UnsupportedFormat,"Unsupported NASA original endpoint. Use the official browser fallback for other distributions.");
    }
    // Official ORNL responses: s3 original transfers (2026-10-21), head metadata
    // redirects (2026-10-24). The head route is authorized only for HEAD. Fail closed if its CDN
    // identity changes. Never forward the Earthdata token to this signed destination.
    public static void ValidateNasaRedirect(Uri original,Uri redirected,HttpMethod? method=null)
    {
        ValidateNasaOriginal(original);
        var relative=original.AbsolutePath[(original.AbsolutePath.IndexOf("/aviris/",StringComparison.Ordinal)+1)..];
        var bucket=original.AbsolutePath.StartsWith("/protected/",StringComparison.Ordinal)?"ornl-cumulus-prod-protected":"ornl-cumulus-prod-public";
        var prefix=method==HttpMethod.Head?"(?:s3|head)":"s3";
        if(redirected.Scheme!="https"||redirected.Port!=443||redirected.UserInfo.Length!=0||redirected.Fragment.Length!=0||redirected.AbsoluteUri.Length>16384||
            redirected.Host!="d3o6w55j8uz1ro.cloudfront.net"||!System.Text.RegularExpressions.Regex.IsMatch(redirected.AbsolutePath,@"^/"+prefix+@"-[a-f0-9]+/"+bucket+@"\.s3\.us-west-2\.amazonaws\.com/"+System.Text.RegularExpressions.Regex.Escape(relative)+"$"))
            throw ScientificFailure.Create(ScientificFailureKind.ProviderRedirect,"NASA redirected to an unsupported destination. No credential was forwarded; use official browser access or retry after a provider update.");
    }
    public async Task<HttpResponseMessage> SendNasa(Uri original,HttpMethod method,IEarthdataCredentials credentials,long? start,long? end,string? etag,CancellationToken token)
    {
        ValidateNasaOriginal(original);
        var bearer=credentials.ReadToken()??throw ScientificFailure.Create(ScientificFailureKind.Authorization,"Connect Earthdata on the companion PC before acquiring NASA originals.");
        EarthdataConnection.ValidateToken(bearer);
        try
        {
            var response=await SendCore(original,method,start,end,etag,token,bearer,ValidateNasaOriginal);
            if((int)response.StatusCode is >=300 and <400)
            {
                var location=response.Headers.Location;response.Dispose();
                if(location is null)throw new IOException("NASA access redirect is missing. Reconnect or use official browser access.");
                var destination=new Uri(original,location);
                if(destination.Host=="urs.earthdata.nasa.gov")throw ScientificFailure.Create(ScientificFailureKind.Authorization,"NASA sign-in is required: reconnect with a current User Token and approve ORNL DAAC on NASA's site, or use the official browser import fallback. No credential was forwarded to the login redirect.");
                ValidateNasaRedirect(original,destination,method);
                response=await SendCore(destination,method,start,end,etag,token,validate:u=>ValidateNasaRedirect(original,u,method));
            }
            if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {response.Dispose();throw ScientificFailure.Create(ScientificFailureKind.Authorization,"Earthdata access denied or expired. Reconnect with a current NASA User Token and approve ORNL DAAC access on NASA's site; browser import remains available.");}
            if(!response.IsSuccessStatusCode){var code=(int)response.StatusCode;response.Dispose();throw new IOException($"NASA original request failed (HTTP {code}). Retry later or use the official browser fallback.");}
            if(response.Content.Headers.ContentEncoding.Any(c=>!c.Equals("identity",StringComparison.OrdinalIgnoreCase)))
            {response.Dispose();throw new IOException("NASA returned encoded content; original numeric bytes were refused.");}
            return response;
        }
        catch(HttpRequestException){throw new IOException("NASA connection failed. Retry with retained partial data; no source was published.");}
    }
    public async Task<NasaDownloadFile> DescribeNasaFile(NasaAsset asset,IEarthdataCredentials credentials,CancellationToken token)
    {
        using var response=await SendNasa(new(asset.Url),HttpMethod.Head,credentials,null,null,null,token);
        var length=response.Content.Headers.ContentLength;
        if(length is null or <=0 or >21474836480)throw new IOException("NASA did not supply a supported exact original file length. Use browser acquisition; no size was invented.");
        if(asset.Role=="ENVI header"&&length>1048576||asset.Role=="Provider SHA-256"&&length>4096)throw new IOException("NASA metadata companion exceeds its safe size bound.");
        var etag=response.Headers.ETag is {IsWeak:false} tag?tag.ToString():null;
        return new(asset.Name,asset.Url,asset.Role,length.Value,etag,response.Headers.AcceptRanges.Contains("bytes"));
    }
    private sealed record FileCheckpoint(long Bytes,string Sha256,string? Etag,long Length);
    public async Task DownloadNasaFile(NasaDownloadFile file,string folder,IEarthdataCredentials credentials,ScientificDownloadBudget budget,CancellationToken token,Action<long>? progress=null)
    {
        ValidateNasaOriginal(new(file.Url));
        if(Path.GetFileName(file.Name)!=file.Name||file.Name.Contains('/')||file.Name.Contains('\\'))throw new IOException("Unsafe NASA filename.");
        var partial=Path.Combine(folder,file.Name+".part");var checkpoint=partial+".json";var finished=Path.Combine(folder,file.Name);
        foreach(var path in new[]{partial,checkpoint,finished})StorageManager.ValidateLocalPath(path);
        var fresh=await DescribeNasaFile(new(file.Name,file.Url,file.Role),credentials,token);
        if(fresh.Bytes!=file.Bytes||file.Etag is not null&&fresh.Etag!=file.Etag)throw new IOException("Approved NASA original changed. Create a fresh preview; incompatible bytes were not combined.");
        FileCheckpoint? saved=null;using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if(file.Ranges&&file.Etag is not null&&File.Exists(checkpoint))
        {
            if(new FileInfo(checkpoint).Length>4096)throw new IOException("NASA partial checkpoint is invalid; preserved for review.");
            try{saved=JsonSerializer.Deserialize<FileCheckpoint>(await File.ReadAllTextAsync(checkpoint,token),WireJson.Options);}catch(JsonException){}
            var source=File.Exists(finished)?finished:partial;
            if(saved is not null&&(saved.Bytes<0||saved.Bytes>file.Bytes||saved.Etag!=file.Etag||saved.Length!=file.Bytes||!File.Exists(source)||new FileInfo(source).Length<saved.Bytes))saved=null;
            if(saved is not null)
            {
                using var input=File.OpenRead(source);var prefix=new byte[1048576];long hashed=0;
                while(hashed<saved.Bytes){var n=await input.ReadAsync(prefix.AsMemory(0,(int)Math.Min(prefix.Length,saved.Bytes-hashed)),token);if(n==0)break;digest.AppendData(prefix,0,n);hashed+=n;}
                if(hashed!=saved.Bytes||saved.Bytes==file.Bytes&&input.Length!=file.Bytes||Convert.ToHexString(digest.GetCurrentHash()).ToLowerInvariant()!=saved.Sha256)saved=null;
            }
            if(saved?.Bytes==file.Bytes){if(!File.Exists(finished))File.Move(partial,finished);progress?.Invoke(file.Bytes);return;}
        }
        if(saved is null)digest.GetHashAndReset();
        var offset=saved?.Bytes??0;
        using var response=await SendNasa(new(file.Url),HttpMethod.Get,credentials,offset>0?offset:null,null,file.Etag,token);
        if(file.Etag is not null&&response.Headers.ETag?.ToString()!=file.Etag)throw new IOException("NASA original ETag changed; fresh preview required.");
        if(offset>0&&response.StatusCode==HttpStatusCode.OK){offset=0;digest.GetHashAndReset();} // Never append a full response.
        if(response.StatusCode==HttpStatusCode.PartialContent)
        {
            var range=response.Content.Headers.ContentRange;
            if(range?.Unit!="bytes"||range.From!=offset||range.To!=file.Bytes-1||range.Length!=file.Bytes)throw new IOException("NASA did not honor the exact original-file range. Partial bytes were not appended.");
        }
        else if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Unsupported NASA file response.");
        if(response.Content.Headers.ContentLength is {} expected&&expected!=file.Bytes-offset)throw new IOException("NASA original length changed; fresh preview required.");
        if(file.Bytes-offset>budget.Maximum-budget.Bytes)throw new IOException("The remaining NASA original exceeds this attempt's approved transfer allowance. Review the job budget; no limit was enlarged.");
        await using var output=new FileStream(partial,offset==0?FileMode.Create:FileMode.Open,FileAccess.ReadWrite,FileShare.None,1048576,FileOptions.Asynchronous);
        output.SetLength(offset);output.Position=offset;progress?.Invoke(offset);
        await using var inputStream=await response.Content.ReadAsStreamAsync(token);var buffer=new byte[1048576];var checkpointAt=offset;
        void SaveCheckpoint()
        {
            output.Flush(true);
            PreferenceStore.WriteAtomic(checkpoint,new FileCheckpoint(output.Position,Convert.ToHexString(digest.GetCurrentHash()).ToLowerInvariant(),file.Etag,file.Bytes));
            checkpointAt=output.Position;
        }
        try
        {
            while(output.Position<file.Bytes)
            {
                token.ThrowIfCancellationRequested();var count=(int)Math.Min(buffer.Length,file.Bytes-output.Position);budget.EnsureCapacity(count);
                var n=await inputStream.ReadAsync(buffer.AsMemory(0,count),token);if(n==0)throw new EndOfStreamException("NASA original download is incomplete. Resume or retry; no source was published.");
                budget.Charge(n);Received(new(file.Url),n,true);await output.WriteAsync(buffer.AsMemory(0,n),token);
                digest.AppendData(buffer,0,n);progress?.Invoke(output.Position);if(output.Position-checkpointAt>=8388608)SaveCheckpoint();
            }
        }
        finally
        {
            SaveCheckpoint();
        }
        await output.DisposeAsync();File.Move(partial,finished,true);
    }
}
