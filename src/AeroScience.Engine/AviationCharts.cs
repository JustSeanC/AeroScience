using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed class AviationChartRenderer : IAviationChartRenderer
{
    private readonly string runtime = Path.Combine(AppContext.BaseDirectory, "ChartRuntime");
    private bool? healthy;
    public bool Available
    {
        get
        {
            if(!OperatingSystem.IsWindows()||System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture!=System.Runtime.InteropServices.Architecture.X64||!File.Exists(Path.Combine(runtime,"manifest.json")))return false;
            lock(this)
            {
                if(healthy is {} result)return result;
                try{AviationChartRaster.Initialize(runtime);healthy=true;}
                catch(Exception e)when(e is IOException or NotSupportedException or InvalidOperationException or System.Text.Json.JsonException or System.Runtime.InteropServices.ExternalException or DllNotFoundException or EntryPointNotFoundException){healthy=false;}
                return healthy.Value;
            }
        }
    }
    private void Initialize() { if (!Available) throw new InvalidOperationException("Verified Windows chart runtime unavailable. No replacement dataset or software was downloaded."); AviationChartRaster.Initialize(runtime); }
    public AviationChartRaster.Description Describe(string path, AviationChartDefinition definition, CancellationToken token)
    { Initialize(); var result = AviationChartRaster.Inspect(path,definition.Sha256,definition.MainBody,token); if(result.Width!=definition.Width||result.Height!=definition.Height)throw new InvalidDataException("Original chart dimensions changed."); return result; }
    public byte[] Tile(string path, AviationChartDefinition definition, int zoom, int x, int y, CancellationToken token)
    { Initialize(); return AviationChartRaster.Tile(path,definition.Sha256,definition.MainBody,zoom,x,y,token); }
}

public sealed partial class AviationReference
{
    private readonly IAviationChartRenderer chartRaster = chartRenderer ?? new AviationChartRenderer();
    private readonly AviationChartDefinition[] knownCharts = (chartDefinitions ?? AviationChartSheets.Reviewed).ToArray();
    private readonly SemaphoreSlim chartWork = new(1);
    private int pendingChartTiles;
    private readonly Dictionary<string,AviationChartRaster.Description> chartDescriptions = new(StringComparer.Ordinal);
    private const long ChartCacheBytes = 128L * 1024 * 1024;
    public async Task<AviationChartPage> ChartSheets(int offset = 0, CancellationToken token = default)
    {
        if(offset<0||offset>10000||offset%50!=0)throw new ArgumentException("Use bounded 50-sheet pages.");
        // Existing durable-job page is authoritative. Reads never search FAA or download originals.
        (AviationDownloadJob Job,AviationRetainedFile File)[] originals;
        AviationCatalogItem[] advertised;
        lock(gate){if(Preferences().StoreId is null)return new(1,offset,null,false,[],"No retained aviation chart originals.",[AviationChartSheets.Limitations]);using var store=Open(Preferences());originals=store.ChartFiles(offset);advertised=(store.Catalog("vfr")?.Items??[]).Concat(store.Catalog("ifr")?.Items??[]).ToArray();}
        var sheets = new List<AviationChartSheet>();
        foreach (var (job,file) in originals)
        {
            token.ThrowIfCancellationRequested(); var definition=knownCharts.SingleOrDefault(d=>d.Sha256==file.Sha256&&d.FileName==file.Name); var id=definition?.Id??file.Sha256[..24]+"-"+AviationChartDefinition.FileIdentity(file.Name)+"-unreviewed";
            var state=definition is null?"Main body not reviewed":!chartRaster.Available?"Runtime unavailable":"Not yet rendered";
            var explanation=definition?.Evidence??"This exact raster/edition has no reviewed main-body clip. Original retained; no geographic placement was guessed.";
            AviationChartRaster.Description? description=null;
            if(definition is not null && chartRaster.Available)
            {
                var key=job.Id.ToString("N")+definition.Id;
                lock(chartDescriptions)chartDescriptions.TryGetValue(key,out description);
                if(description is null && !FlightActive && await chartWork.WaitAsync(0,token))
                {
                    try
                    {
                        var path=ChartOriginal(job,file);
                        description=await Task.Run(()=>chartRaster.Describe(path,definition,token),token);
                        lock(chartDescriptions){if(chartDescriptions.Count>=64)chartDescriptions.Clear();chartDescriptions[key]=description;}
                    }
                    catch(Exception e)when(e is IOException or InvalidDataException or NotSupportedException or InvalidOperationException or System.Text.Json.JsonException or System.Runtime.InteropServices.ExternalException)
                    {state="Verification unavailable";explanation="Reconnect the original aviation drive or reinstall the matching verified chart runtime. Original files were preserved.";}
                    finally{chartWork.Release();}
                }
                if(description is not null)state="Reviewed interior available";
                else if(FlightActive)explanation+=" New chart initialization waits for flight priority; already initialized charts remain usable.";
            }
            var item=job.Preview.Item;var newer=advertised.Where(a=>a.Product==item.Product&&a.OriginalFileName==item.OriginalFileName&&string.CompareOrdinal(a.Edition,item.Edition)>0).OrderByDescending(a=>a.Edition).FirstOrDefault();
            var currency=item.ExpiresUtc<=clock.GetUtcNow()?"Expired according to retained publisher UTC metadata":item.EffectiveUtc>clock.GetUtcNow()?"Future effective edition according to retained publisher UTC metadata":newer is not null?"Newer edition label advertised: "+newer.Edition+"; its exact effective status is not inferred":"Current operational validity Unknown";
            sheets.Add(new(id,job.Id,file.Name,item.Product.ToString(),item.Edition,file.Sha256,state,explanation,
                description?.MainBody.Select(p=>new Coordinates(p.Latitude,p.Longitude)).ToArray()??[],description?.Crs,item.EffectiveUtc,item.ExpiresUtc,currency));
        }
        return new(1,offset,originals.Length==50?offset+50:null,chartRaster.Available,sheets.ToArray(),
            "Retained aviation originals only. Choose an exact sheet/edition; overlapping sheets remain independent. Supersession/current operational validity is unverified. Bounded 50-sheet pages; unreviewed insets remain explicit.",
            [AviationChartSheets.Limitations,"Runtime/data/source integrity and full chart acceptance are separate. No science or flight evidence is created."]);
    }
    private string ChartOriginal(AviationDownloadJob job,AviationRetainedFile file)
    {
        if(job.State!="Completed"||!job.Approved||job.OriginalSha256 is null||job.Files?.Contains(file)!=true)throw new InvalidOperationException("Chart source is not a verified approved original.");
        var path=Path.Combine(JobDirectory(job.Id),"files",file.Name);StorageManager.ValidateLocalPath(path);
        if(new FileInfo(path).Length!=file.Bytes)throw new IOException("Retained chart file changed.");
        return path;
    }
    public async Task<byte[]> ChartTile(Guid jobId,string sheetId,int zoom,int x,int y,CancellationToken token=default)
    {
        if(zoom is <3 or >15||x<0||y<0||x>=1<<zoom||y>=1<<zoom)throw new ArgumentException("Choose bounded chart tile coordinates.");
        var definition=knownCharts.SingleOrDefault(d=>d.Id==sheetId)??throw new ArgumentException("Unreviewed chart sheet identity.");
        AviationDownloadJob job;lock(gate){using var store=Open(Preferences());job=store.Job(jobId);}
        var file=job.Files?.SingleOrDefault(f=>f.Sha256==definition.Sha256&&f.Name==definition.FileName)??throw new InvalidOperationException("Selected chart source does not match this retained job.");
        var original=ChartOriginal(job,file);var cache=Path.Combine(directory,"ChartTiles-v1",jobId.ToString("N"),definition.Id,zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),$"{x}-{y}.png");
        StorageManager.ValidateLocalPath(cache);
        var receipt=cache+".sha256";StorageManager.ValidateLocalPath(receipt);
        if(File.Exists(cache)&&File.Exists(receipt))
        {
            var info=new FileInfo(cache);var identity=new FileInfo(receipt);
            if(info.Length is >0 and <=1048576&&identity.Length==64)
            {var bytes=await File.ReadAllBytesAsync(cache,token);var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();if(await File.ReadAllTextAsync(receipt,token)==hash)return bytes;}
            throw new IOException("Derived chart cache integrity differs. Originals are preserved; remove only this regenerable chart cache before retrying.");
        }
        if(Interlocked.Increment(ref pendingChartTiles)>16){Interlocked.Decrement(ref pendingChartTiles);throw new InvalidOperationException("Chart request window is full. Retry the map; recording/science workers are independent.");}
        var entered=false;
        try
        {
            if(!await chartWork.WaitAsync(TimeSpan.FromSeconds(3),token))throw new InvalidOperationException("Chart renderer busy. Retry this tile; flight/science workers are independent.");
            entered=true;
            if(File.Exists(cache)&&File.Exists(receipt))
            {
                var check=new FileInfo(cache);if(check.Length is >0 and <=1048576 && new FileInfo(receipt).Length==64)
                {var cached=await File.ReadAllBytesAsync(cache,token);if(await File.ReadAllTextAsync(receipt,token)==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cached)).ToLowerInvariant())return cached;}
                throw new IOException("Derived chart cache changed. Original sources remain protected.");
            }
            var key=jobId.ToString("N")+definition.Id;
            lock(chartDescriptions)if(FlightActive&&!chartDescriptions.ContainsKey(key))throw new InvalidOperationException("New chart initialization is waiting for flight priority. Previously initialized charts remain available.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(8));
            var bytes=await Task.Run(()=>chartRaster.Tile(original,definition,zoom,x,y,deadline.Token),deadline.Token);
            if(bytes.Length is <8 or >1048576)throw new InvalidDataException("Chart tile exceeds its bound.");
            if(disk.FreeBytes(directory)>StorageManager.MinimumFreeBytes+bytes.Length)
            {
                var root=Path.Combine(directory,"ChartTiles-v1");StorageManager.ValidateLocalPath(root);Directory.CreateDirectory(root);
                var old=Directory.EnumerateFiles(root,"*.png",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,MaxRecursionDepth=5,IgnoreInaccessible=false}).Take(4097).ToArray();
                if(old.Length>4096)throw new IOException("Derived chart cache inventory exceeded its bound. Originals are unchanged.");
                long size=0;foreach(var p in old){StorageManager.ValidateLocalPath(p);size=checked(size+new FileInfo(p).Length);}
                var remaining=old.Length;
                foreach(var p in old.OrderBy(File.GetLastWriteTimeUtc)){if(size+bytes.Length<=ChartCacheBytes&&remaining<4096)break;var length=new FileInfo(p).Length;File.Delete(p);var sidecar=p+".sha256";StorageManager.ValidateLocalPath(sidecar);if(File.Exists(sidecar))File.Delete(sidecar);size-=length;remaining--;}
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);var stage=cache+"."+Guid.NewGuid().ToString("N")+".tmp";
                var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();var receiptStage=stage+".sha256";
                try
                {
                    await File.WriteAllBytesAsync(stage,bytes,token);await File.WriteAllTextAsync(receiptStage,hash,token);
                    // Only incomplete derived cache files owned by this exact tile can be replaced.
                    File.Move(stage,cache,true);File.Move(receiptStage,receipt,true);
                }
                finally{if(File.Exists(stage))File.Delete(stage);if(File.Exists(receiptStage))File.Delete(receiptStage);}
            }
            return bytes;
        }
        finally{if(entered)chartWork.Release();Interlocked.Decrement(ref pendingChartTiles);}
    }
}
