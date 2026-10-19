using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed class ScientificAccessSettings(string directory,Func<bool?>? unmetered=null)
{
    public const string Privacy="Automatic scientific data access sends geographic search areas and your IP address to public scientific-data providers (USGS, Microsoft Planetary Computer, Element 84 and NASA). Historical numeric datasets are downloaded directly to your PC. Mission notes and flight recordings are not uploaded. This permission is separate from online basemaps. You can choose offline-only before any scientific query.";
    private readonly string path=Path.Combine(directory,"scientific-access.json");private readonly object gate=new();
    public bool? IsUnmetered()=>unmetered?.Invoke();
    public ScientificAccessConfiguration Get()
    {
        lock(gate)
        {
            try
            {
                var p=File.Exists(path)?JsonSerializer.Deserialize<ScientificAccessPreferences>(File.ReadAllText(path),WireJson.Options)??throw new IOException("Missing scientific-access preferences."):new();
                Validate(p);return new(p,Privacy,NetworkCost:IsUnmetered() switch{true=>"Unmetered",false=>"Metered / constrained",_=>"Unknown; unmetered-only downloads pause"});
            }
            catch(Exception e)when(e is IOException or JsonException or UnauthorizedAccessException or ArgumentException){return new(new(),Privacy,"Scientific access settings need review. Online retrieval is disabled; verified local sources remain usable.","Unknown");}
        }
    }
    public ScientificAccessConfiguration Save(ScientificAccessPreferences p)
    {Validate(p);if(p.OnlineEnabled&&!p.PrivacyAccepted)throw new ArgumentException("Review the separate scientific-data privacy disclosure before enabling retrieval.");lock(gate)PreferenceStore.WriteAtomic(path,p);return Get();}
    private static void Validate(ScientificAccessPreferences p)
    {
        if(p.MaximumDownloadBytes is <1048576 or >536870912||p.ConcurrentDownloads is <1 or >2||p.LookbackDays is <1 or >3650||!double.IsFinite(p.MaximumCloudPercent)||p.MaximumCloudPercent is <0 or >100||p.SelectionPolicy is not("clear-recent" or "newest")||p.PreferredDateUtc>DateTimeOffset.UtcNow.AddDays(1)||p.LookaheadSeconds is <0 or >900||p.PrefetchHourlyBytes is <1048576 or >21474836480||p.SourceModes?.Any(kv=>!Enum.IsDefined(kv.Key)||kv.Value is not("automatic" or "local" or "prefer-local")||kv.Key==RemoteKind.Hyperspectral&&kv.Value!="local")==true)
            throw new ArgumentException("Choose 1–512 MiB per download, 1–2 concurrent preparations, 1–3650 lookback days, 0–100% scene cloud, and a supported selection policy/date.");
    }
}

public sealed class AutomaticScientificData : IDisposable
{
    private sealed record Job(string Key,RemoteKind Kind,GeographicBounds Area,Guid Ticket,double Priority,string Scope,bool Background=false,Coordinates? Point=null);
    private readonly ScienceStore science;private readonly ScientificAccessSettings settings;private readonly ScientificHttp http;
    private readonly IScientificDataProvider[] providers;
    private readonly Dictionary<string,Job> queue=[];
    private readonly SemaphoreSlim wake=new(0,1);
    private bool paused;
    private readonly HashSet<string> flightKeys=[],preempted=[];
    private DateTimeOffset lastPreemption;
    private double preparationSeconds=45;
    private readonly Queue<(DateTimeOffset At,long Bytes)> transfers=[];
    private long hourlyBytes,reservedTransferBytes;
    private readonly Dictionary<string,string> scopes=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(long Length,DateTime Modified)> verified=new();
    public double EstimatedPreparationSeconds {get{lock(gate)return preparationSeconds;}}
    public static string AreaKey(RemoteKind kind,GeographicBounds area)=>kind+":"+StacScientificProvider.Bounds(area);
    private void Wake(){if(wake.CurrentCount==0)try{wake.Release();}catch(SemaphoreFullException){}}
    private void TrimTransfers(){while(transfers.TryPeek(out var t)&&t.At<DateTimeOffset.UtcNow.AddHours(-1)){transfers.Dequeue();hourlyBytes-=t.Bytes;}}
    public ScientificQueueSummary QueueSummary(){lock(gate){TrimTransfers();return new(queue.Count,cancellations.Count,paused,hourlyBytes,settings.Get().Preferences.PrefetchHourlyBytes,preparationSeconds);}}
    public void Pause(bool value){lock(gate)paused=value;Wake();}
    public void ReprioritizeFlight(IReadOnlyDictionary<string,double> priorities)
    {
        lock(gate){flightKeys.Clear();flightKeys.UnionWith(priorities.Keys);foreach(var job in queue.Values.Where(j=>j.Scope=="flight").ToArray())
        {
            if(priorities.TryGetValue(job.Key,out var priority))queue[job.Key]=job with{Priority=priority};
            else{queue.Remove(job.Key);states[job.Key]=states[job.Key] with{Status="Superseded",Message="Observed course changed; unsent preparation was replaced. Retained sources and useful active downloads are preserved."};}
        }
        }
        Wake();
    }
    private readonly Dictionary<string,ScientificDataReadiness> states=[];private readonly Dictionary<string,Guid> tickets=[];private readonly Dictionary<string,CancellationTokenSource> cancellations=[];
    private readonly object gate=new();private readonly CancellationTokenSource stopping=new();private readonly Task worker;
    public AutomaticScientificData(ScienceStore science,ScientificAccessSettings settings,ScientificHttp? http=null,IScientificDataProvider[]? providers=null)
    {
        this.science=science;this.settings=settings;this.http=http??new();this.providers=providers??[new UsgsElevationProvider(this.http),new StacScientificProvider(this.http,"planetary-computer"),new StacScientificProvider(this.http,"earth-search")];
        worker=Task.Run(Run);
    }
    public ScientificDataReadiness[] Snapshot(){lock(gate)return states.Values.OrderByDescending(s=>s.UpdatedUtc).Take(256).ToArray();}
    private void Set(ScientificDataReadiness state){lock(gate)states[state.Key]=state with{UpdatedUtc=DateTimeOffset.UtcNow};}
    public void Cancel(string? key=null)
    {
        lock(gate)
        {
            foreach(var pair in cancellations.Where(p=>key is null||p.Key==key).ToArray())pair.Value.Cancel();
            foreach(var job in queue.Values.Where(j=>key is null||j.Key==key).ToArray())queue.Remove(job.Key);
            foreach(var s in states.Values.Where(s=>s.Status=="Queued"&&(key is null||key==s.Key)).ToArray())
                states[s.Key]=s with{Status="Unavailable",Message="Queued preparation cancelled; no download was started.",UpdatedUtc=DateTimeOffset.UtcNow};
        }
    }
    public void CancelScope(string scope)
    {lock(gate)foreach(var key in scopes.Where(p=>p.Value==scope).Select(p=>p.Key).ToArray())Cancel(key);}
    public ScientificAccessConfiguration Configure(ScientificAccessPreferences preferences)
    {var saved=settings.Save(preferences);Cancel();lock(gate){foreach(var key in states.Keys.Where(k=>!cancellations.ContainsKey(k)).ToArray())states.Remove(key);}return saved;}
    public ScientificDataReadiness[] Prepare(GeographicBounds area,IEnumerable<RemoteKind> kinds,bool retry=false,bool background=false,double priority=10000,string scope="manual",Coordinates? samplePoint=null)
    {
        ValidateArea(area);var p=settings.Get().Preferences;
        foreach(var kind in kinds.Distinct().Take(4))
        {
            if(!Enum.IsDefined(kind))throw new ArgumentException("Unknown scientific instrument.");
            var key=AreaKey(kind,area);
            lock(gate)
            {
                if(states.TryGetValue(key,out var previous)&&(!retry||previous.Status is "Downloading" or "Queued")&&DateTimeOffset.UtcNow-previous.UpdatedUtc<TimeSpan.FromMinutes(30)&&!(previous.Status=="Offline"&&p.OnlineEnabled)&&previous.Status!="Superseded"&&!(kind==RemoteKind.Hyperspectral&&samplePoint!=previous.SamplePoint))
                {if(queue.TryGetValue(key,out var queued)&&priority<queued.Priority)queue[key]=queued with{Priority=priority,Scope=scope};if(scope=="flight")scopes[key]=scope;continue;}
                if(states.Count>=256){var old=states.Values.Where(s=>s.Status is not("Queued" or "Downloading")).OrderBy(s=>s.UpdatedUtc).FirstOrDefault();if(old is not null){states.Remove(old.Key);tickets.Remove(old.Key);scopes.Remove(old.Key);}else continue;}
                string? reason=!p.OnlineEnabled||!p.PrivacyAccepted?"Scientific online access is disabled. Enable it separately from basemaps in Settings. Verified local sources still work.":background&&!p.BackgroundRetrieval?"Background retrieval is disabled. Prepare this area explicitly before flight.":p.UnmeteredOnly&&settings.IsUnmetered()!=true?"Waiting for a reliably detected unmetered connection. Change the unmetered-only control to proceed on this connection.":null;
                // Local hyperspectral checks run without online consent and never trigger NASA downloading.
                if(kind==RemoteKind.Hyperspectral)reason=null;
                Set(new(key,kind,"Queued","Checking retained sources before any external request. "+reason,area,EstimatedDownloadBytes:kind==RemoteKind.Hyperspectral?0:p.MaximumDownloadBytes,EstimatedStorageBytes:StorageBound(kind),SamplePoint:samplePoint));
                {
                    var ticket=Guid.NewGuid();tickets[key]=ticket;scopes[key]=scope;
                    if(queue.Count>=64)
                    {
                        var least=queue.Values.MaxBy(j=>j.Priority)!;
                        if(priority<least.Priority){queue.Remove(least.Key);states[least.Key]=states[least.Key] with{Status="Superseded",Message="Yielded to more urgent aircraft data; progressive preflight will revisit this area."};}
                        else{Set(states[key] with{Status="Superseded",Message="Waiting for priority queue capacity; progressive preparation will retry."});continue;}
                    }
                    queue[key]=new(key,kind,area,ticket,priority,scope,background,samplePoint);
                    PreemptDistantPreflight(scope,priority);Wake();
                }
            }
        }
        return Snapshot();
    }
    // Caller holds gate and has already inserted urgent work. Only irrelevant active
    // preflight can yield; normal flight downloads survive small heading changes.
    private void PreemptDistantPreflight(string scope,double priority)
    {
        if(scope!="flight"||priority>0||DateTimeOffset.UtcNow-lastPreemption<TimeSpan.FromSeconds(30)||cancellations.Count<settings.Get().Preferences.ConcurrentDownloads)return;
        var candidate=cancellations.FirstOrDefault(p=>scopes.GetValueOrDefault(p.Key)?.StartsWith("plan:",StringComparison.Ordinal)==true&&!flightKeys.Contains(p.Key)&&!p.Value.IsCancellationRequested&&states.GetValueOrDefault(p.Key)?.Status=="Downloading");
        if(candidate.Value is null)return;lastPreemption=DateTimeOffset.UtcNow;preempted.Add(candidate.Key);candidate.Value.Cancel();
    }
    public void RefreshLocalReadiness()
    {lock(gate)foreach(var key in states.Where(p=>p.Value.Status is not("Queued" or "Downloading")).Select(p=>p.Key).ToArray()){states.Remove(key);tickets.Remove(key);}}
    public static void ValidateArea(GeographicBounds a)
    {
        if(new[]{a.West,a.South,a.East,a.North}.Any(v=>!double.IsFinite(v))||a.West< -180||a.East>180||a.South< -80||a.North>84||a.East<=a.West||a.North<=a.South||a.East-a.West>.061||a.North-a.South>.061)
            throw new ArgumentException("Automatic preparation supports bounded areas up to 0.06° per side within 80°S–84°N; split large or dateline-crossing surveys. No global scenes will be downloaded.");
    }
    private static long StorageBound(RemoteKind kind)=>kind switch{RemoteKind.Multispectral=>176160768,RemoteKind.Thermal=>109051904,_=>37748736};
    private async Task Run()
    {
        var active=new List<Task>();
        try
        {
            while(!stopping.IsCancellationRequested)
            {
                active.RemoveAll(t=>t.IsCompleted);
                Job? job=null;
                lock(gate)if(!paused&&active.Count<settings.Get().Preferences.ConcurrentDownloads&&queue.Count>0)
                {job=queue.Values.MinBy(j=>j.Priority);queue.Remove(job!.Key);}
                if(job is not null){active.Add(Task.Run(()=>Execute(job),stopping.Token));continue;}
                await wake.WaitAsync(TimeSpan.FromMilliseconds(200),stopping.Token);
            }
        }
        catch(OperationCanceledException){}
        finally{try{await Task.WhenAll(active);}catch(OperationCanceledException){}}
    }
    private async Task Execute(Job job)
    {
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);cancel.CancelAfter(TimeSpan.FromMinutes(12));
        lock(gate)cancellations[job.Key]=cancel;
        ScientificDataReadiness state;lock(gate){if(!tickets.TryGetValue(job.Key,out var ticket)||ticket!=job.Ticket||!states.TryGetValue(job.Key,out state!)||state.Status!="Queued"){cancellations.Remove(job.Key);return;}}
        var token=cancel.Token;var started=DateTimeOffset.UtcNow;long lastCharged=0,transferAllowance=0;
        try
        {
            var p=settings.Get().Preferences;
            // Reuse verified sources before any network traffic. They remain retained originals,
            // not cleanup-eligible caches; actual sampling still applies per-pixel QA.
            var available=ScientificSourceSelection.Select(science.Availability(job.Point??job.Area.Center).Where(a=>a.FilesAvailable&&a.CoversPoint==true&&
                (job.Kind==RemoteKind.Hyperspectral||p.SourceModes?.GetValueOrDefault(job.Kind)=="local"||job.Area.Corners.All(point=>{using var raster=new ScientificRaster(Path.Combine(science.DirectoryPath,a.Source.RelativeDirectory),a.Source.Manifest.Bands[0]);return raster.Covers(new(point.Latitude+(job.Area.Center.Latitude-point.Latitude)*.000001,point.Longitude+(job.Area.Center.Longitude-point.Longitude)*.000001));}))),job.Kind,p);
            if(available is not null)
            {
                foreach(var file in available.Files)
                {
                    token.ThrowIfCancellationRequested();using var bytes=File.OpenRead(ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,available.RelativeDirectory),file.RelativePath));
                    var info=new FileInfo(bytes.Name);var identity=(info.Length,info.LastWriteTimeUtc);
                    if((!verified.TryGetValue(bytes.Name,out var cached)||cached!=identity)&&Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()!=file.Sha256)throw new IOException("Retained scientific source failed its integrity check. Restore/reimport it; no download replaced the original.");
                    verified[bytes.Name]=identity;
                }
                using var coverageRaster=new ScientificRaster(Path.Combine(science.DirectoryPath,available.RelativeDirectory),available.Manifest.Bands[0]);
                var full=job.Area.Corners.All(point=>coverageRaster.Covers(point));
                Set(state with{SourceCoverage=ScientificRaster.GridCoverage(coverageRaster.Grid),Status=!full?"Partially usable":job.Kind==RemoteKind.Hyperspectral?"Dataset Ready":available.Manifest.Acquisition?.UsablePixels==0?"Limited Coverage":"Ready",Message="Verified retained source available offline; actual per-pixel masks still apply.",Provider=available.Manifest.Provider,Dataset=available.Manifest.Name,ObservationUtc=available.Manifest.ObservationUtc,SourceId=available.Id,Quality=available.Manifest.Acquisition?.Quality});return;
            }
            if(job.Kind==RemoteKind.Hyperspectral)
            {
                var local=science.Availability().Where(a=>a.Source.Manifest.Kind==RemoteKind.Hyperspectral).ToArray();
                Set(state with{Status=local.Length==0?"Dataset Required":local.All(a=>!a.FilesAvailable)?"Dataset Invalid":"Dataset Outside Coverage",Message=local.Length==0?"Import a compatible hyperspectral dataset covering your research area. No account or automatic download is required.":"Review imported dataset coverage or import another compatible dataset. No automatic hyperspectral download is required."});return;
            }
            string? blocked=p.SourceModes?.GetValueOrDefault(job.Kind)=="local"?"My Datasets Only: no compatible local dataset covers this area. Import a dataset or change this instrument’s source mode; no provider download was attempted.":!p.OnlineEnabled||!p.PrivacyAccepted?"Scientific online access is disabled; retained local sources remain usable.":job.Background&&!p.BackgroundRetrieval?"Background retrieval is disabled.":p.UnmeteredOnly&&settings.IsUnmetered()!=true?"Waiting for a reliably detected unmetered connection.":null;
            if(blocked is not null){Set(state with{Status="Offline",Message=blocked});return;}
            lock(gate)
            {
                TrimTransfers();transferAllowance=Math.Min(p.MaximumDownloadBytes,p.PrefetchHourlyBytes-hourlyBytes-reservedTransferBytes);
                if(transferAllowance<65536)throw new IOException("Overall scientific transfer budget is reserved or exhausted. Wait for active work/the rolling hour or change the budget; no provider request was started.");
                reservedTransferBytes+=transferAllowance;
            }
            state=state with{Status="Downloading",Message="Discovering real public datasets…"};Set(state);
            var budget=new ScientificDownloadBudget(transferAllowance,bytes=>
            {
                lock(gate){TrimTransfers();var delta=bytes-lastCharged;lastCharged=bytes;transfers.Enqueue((DateTimeOffset.UtcNow,delta));hourlyBytes+=delta;reservedTransferBytes-=delta;if(hourlyBytes>p.PrefetchHourlyBytes)throw new IOException("Overall scientific transfer budget reached. Pause/resume after the rolling hour or change the budget in Settings. No originals were deleted.");}
                state=state with{DownloadedBytes=bytes};Set(state);
            });
            ScientificDiscovery? discovery=null;ScientificSource? source=null;var providerFailures=new List<string>();
            foreach(var provider in providers.Where(provider=>provider.Supports(job.Kind)))
            {
                token.ThrowIfCancellationRequested();
                try { discovery=await provider.Discover(job.Kind,job.Area,p,budget,token); }
                catch(Exception e)when(provider.Id=="usgs-3dep"&&e is IOException or HttpRequestException or JsonException){providerFailures.Add("USGS 3DEP discovery unavailable: "+(e is IOException?e.Message:"provider network/metadata error"));continue;}
                if(discovery is null)continue;
                state=state with{Provider=discovery.Manifest.Provider,Dataset=discovery.Manifest.Name,ObservationUtc=discovery.Manifest.ObservationUtc,Quality=discovery.Quality,Message="Retrieving bounded numeric bands; no observations recorded yet."};Set(state);
                try { source=await PrepareSource(discovery,budget,token); }
                catch(Exception e)when(provider.Id=="usgs-3dep"&&e is not StorageLimitException&&e is IOException or HttpRequestException){providerFailures.Add("USGS 3DEP numeric preparation unavailable: "+(e is IOException?e.Message:"provider network error"));continue;}
                if(source is not null)break;
            }
            if(source is null){Set(state with{Status="Unavailable",Message="No suitable numeric dataset covers this entire bounded area within the date/quality policy. "+string.Join(" ",providerFailures)+" Try a different area/date or retry the provider; advanced manual import remains available.",DownloadedBytes=budget.Bytes});return;}
            var usable=source.Manifest.Acquisition?.UsablePixels;
            Set(state with{Status=usable==0?"Limited Coverage":"Ready",Message=usable==0?"Numeric source prepared, but the bounded QA preview found no usable pixels. Flight observations remain unavailable until valid pixels are reached.":"Numeric source validated and retained in the Scientific Data Library. Historical external observations; per-pixel QA is enforced during flight sampling.",SourceId=source.Id,DownloadedBytes=budget.Bytes});
        }
        catch(OperationCanceledException){bool yielded;lock(gate)yielded=preempted.Contains(job.Key);Set(state with{Status=yielded?"Superseded":"Unavailable",Message=yielded?"Distant preflight yielded to urgent aircraft data. Partial staging is retained/accounted; progressive preparation can retry later.":"Preparation cancelled or timed out. No partial source was advertised as ready. Retained staging is visible in Storage Management."});}
        catch(Exception e)when(e is IOException or HttpRequestException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or KeyNotFoundException or FormatException or NotSupportedException)
        {Set(state with{Status="Unavailable",Message=e is HttpRequestException?"Scientific provider is unreachable. Retry later or use verified offline sources.":e is JsonException or KeyNotFoundException?"Provider metadata is incompatible; no scientific values were guessed.":e.Message});}
        finally{lock(gate){cancellations.Remove(job.Key);preempted.Remove(job.Key);if(transferAllowance>0)reservedTransferBytes-=Math.Max(0,transferAllowance-lastCharged);if(lastCharged>0)preparationSeconds=Math.Clamp(.7*preparationSeconds+.3*(DateTimeOffset.UtcNow-started).TotalSeconds,5,300);}Wake();}
    }
    private async Task<ScientificSource?> PrepareSource(ScientificDiscovery d,ScientificDownloadBudget budget,CancellationToken token)
    {
        science.Initialize();
        using var reservation=science.Management?.ReserveScience(StorageBound(d.Manifest.Kind));
        var relative="Downloads/"+Guid.NewGuid().ToString("N");var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);
        var names=d.Manifest.Bands.Select(b=>b.File).Append("source.json").ToArray();
        var ledger=science.BeginManagedWrite("Automatic numeric source preparation",names.Select(n=>relative+"/"+n));Directory.CreateDirectory(folder);
        var manifest=d.Manifest;
        if(d.NumericExport is {} export)
        {
            var bytes=await http.Bytes(new(export),8388608,budget,token);await File.WriteAllBytesAsync(Path.Combine(folder,"elevation.tif"),bytes,token);
            using var raster=new ScientificRaster(folder,manifest.Bands[0]);
            // USGS service extent is broad. Explicitly test real numeric coverage;
            // an all-nodata subset must fall back, never become a "Ready" source.
            var valid=0;for(var y=0;y<9;y++)for(var x=0;x<9;x++)if(raster.Read(new(d.Area.South+(d.Area.North-d.Area.South)*(y+.5)/9,d.Area.West+(d.Area.East-d.Area.West)*(x+.5)/9)) is {} value&&value is > -12000 and <10000)valid++;
            if(valid==0){File.Delete(Path.Combine(folder,"elevation.tif"));Directory.Delete(folder);File.Delete(ledger);return null;}
        }
        else foreach(var asset in d.Assets)
        {
            token.ThrowIfCancellationRequested();
            using var remote=new ScientificRangeStream(http,asset.Url,budget,token);using var raster=new ScientificRaster(remote,asset.Band);
            raster.CopySubset(d.Area,Path.Combine(folder,asset.Band.File),token);
        }
        var locations=Enumerable.Range(0,81).Select(i=>new Coordinates(d.Area.South+(d.Area.North-d.Area.South)*(i/9+.5)/9,d.Area.West+(d.Area.East-d.Area.West)*(i%9+.5)/9)).ToArray();
        var checkedBands=manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Readiness preview, not a flight observation",raster.Grid);}).ToArray();
        _=RemoteCalculations.Apply(manifest.Kind,checkedBands);
        var numeric=checkedBands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).ToArray();
        var usable=Enumerable.Range(0,81).Count(i=>numeric.Any(b=>b.Values[i].HasValue));
        var provenance=new AutomaticSourceProvenance(d.Manifest.Kind==RemoteKind.Terrain&&d.NumericExport is not null?"usgs-3dep-numeric":"stac-cog-native-subset",d.Catalog,d.Item,d.Assets.Select(a=>a.Url.AbsoluteUri).Concat(d.NumericExport is null?[]:new[]{d.NumericExport}).ToArray(),d.Area,DateTimeOffset.UtcNow,
            d.NumericExport is null?"Lossless native-grid, native-DN GeoTIFF crop via validated HTTP 206 ranges; no overview substitution or interpolation.":"Provider numeric F32 nearest-neighbor export in WGS84; composite native resolution and datum remain unspecified.",d.Quality,d.Resolution,budget.Bytes,d.Limitations??[],d.ProviderMetadataJson,81,usable);
        manifest=manifest with{Acquisition=provenance};
        PreferenceStore.WriteAtomic(Path.Combine(folder,"source.json"),manifest);
        var imported=await science.ImportSourceAsync(Path.Combine(folder,"source.json"),token);
        // ImportSourceAsync hashed and verified every copied numeric file. Remove only
        // this owned, duplicated preparation folder; the imported originals stay retained.
        foreach(var name in names)File.Delete(Path.Combine(folder,name));Directory.Delete(folder);File.Delete(ledger);
        science.Management?.RefreshScience();return imported;
    }
    public async Task DiscoverHyperspectralMetadata(GeographicBounds area,CancellationToken token)
    {
        ValidateArea(area);var p=settings.Get().Preferences;if(!p.OnlineEnabled||!p.PrivacyAccepted||p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new InvalidOperationException("Scientific discovery requires separate consent and an allowed network.");
        var key="NASA:"+StacScientificProvider.Bounds(area);await DiscoverHyperspectral(new(key,RemoteKind.Hyperspectral,area,Guid.NewGuid(),0,"manual"),new(key,RemoteKind.Hyperspectral,"Discovering","Optional NASA metadata only",area),new(Math.Min(1048576,p.MaximumDownloadBytes)),token);
    }
    private async Task DiscoverHyperspectral(Job job,ScientificDataReadiness state,ScientificDownloadBudget budget,CancellationToken token)
    {
        const string catalog="https://cmr.earthdata.nasa.gov/search/granules.json";
        var datasets=new[]{("EMITL2ARFL","001","EMIT L2A reflectance / NetCDF-4 with geolocation and masks"),("AVIRIS-Classic_L2_Reflectance_2154","1","AVIRIS-Classic L2 reflectance / ENVI header + binary"),("AVIRIS-NG_L2_Reflectance_2110","1","AVIRIS-NG L2 reflectance / campaign binary + header metadata")};
        var matches=new List<HyperspectralDiscovery>();
        foreach(var (shortName,version,format) in datasets)
        {
            var url=catalog+"?short_name="+shortName+"&version="+version+"&bounding_box="+StacScientificProvider.Bounds(job.Area)+"&page_size=2&sort_key=-start_date";
            using var response=await http.Json(url,budget,token);var entries=response.RootElement.GetProperty("feed").GetProperty("entry");
            foreach(var entry in entries.EnumerateArray().Take(2))
            {
                DateTimeOffset? observed=entry.TryGetProperty("time_start",out var date)?DateTimeOffset.Parse(date.GetString()!,System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime():null;
                matches.Add(new(shortName,entry.GetProperty("title").GetString()!,observed,format,"Individual NASA Earthdata authorization required for protected scientific files. No login or download is initiated.",url));
            }
        }
        var first=matches.OrderByDescending(m=>m.ObservationUtc).FirstOrDefault();
        Set(state with{Status=matches.Count>0?"Limited Coverage":"Unavailable",Provider="NASA CMR / LP DAAC / ORNL DAAC",Dataset=first?.Dataset??"EMIT / AVIRIS / AVIRIS-NG",ObservationUtc=first?.ObservationUtc,DownloadedBytes=budget.Bytes,Discoveries=matches.ToArray(),
            Authorization="Individual NASA Earthdata authorization is required for protected downloads; credential handling and automated spectral preparation are not supported in this release.",Message=matches.Count>0?"Genuine geographically matching hyperspectral catalog observations discovered (all archived dates searched). Optional NASA metadata search only. Import your own compatible ENVI dataset through the guided workflow; no automatic spectral download or account is required.":"No observations found in the three supported NASA catalog searches. Coverage is campaign-limited. Automatic hyperspectral preparation is unsupported; advanced ENVI import remains available."});
    }

    public void Dispose(){stopping.Cancel();Cancel();try{worker.GetAwaiter().GetResult();}catch(OperationCanceledException){}http.Dispose();stopping.Dispose();}
}
