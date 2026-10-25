using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed class ScientificAccessSettings(string directory,Func<bool?>? unmetered=null)
{
    public const string Privacy="Automatic scientific data access sends geographic search areas and your IP address to public scientific-data providers (USGS, Microsoft Planetary Computer, Element 84, NASA and US Census TIGERweb). Historical numeric datasets are downloaded directly to your PC. Mission notes and flight recordings are not uploaded. This permission is separate from online basemaps. You can choose offline-only before any scientific query.";
    public string ConfigurationDirectory => directory;
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
        if(p.NasaOriginalMaximumBytes is <1048576 or >21474836480)throw new ArgumentException("Choose a NASA original-scene cap between 1 MiB and 20 GiB. Automatic subset limits remain separate.");
        if(p.MaximumDownloadBytes is <1048576 or >536870912||p.ConcurrentDownloads is <1 or >8||p.LookbackDays is <1 or >3650||!double.IsFinite(p.MaximumCloudPercent)||p.MaximumCloudPercent is <0 or >100||p.SelectionPolicy is not("clear-recent" or "newest")||p.PreferredDateUtc>DateTimeOffset.UtcNow.AddDays(1)||p.LookaheadSeconds is <0 or >900||p.PrefetchHourlyBytes is <1048576 or >21474836480||p.PreflightTransferBytes is <1048576 or >21474836480||p.SourceModes?.Any(kv=>!Enum.IsDefined(kv.Key)||kv.Value is not("automatic" or "local" or "prefer-local")||kv.Key==RemoteKind.Hyperspectral&&kv.Value!="local")==true)
            throw new ArgumentException("Choose 1–512 MiB per download, 1–8 concurrent preparations, 1–3650 lookback days, 0–100% scene cloud, and a supported selection policy/date.");
    }
}

public sealed partial class AutomaticScientificData : IDisposable
{
    private long configurationRevision;
    public long ConfigurationRevision => Interlocked.Read(ref configurationRevision);
    private sealed record Job(string Key,RemoteKind Kind,GeographicBounds Area,Guid Ticket,double Priority,string Scope,bool Background=false,Coordinates? Point=null,Func<CancellationToken,Task>? LibraryWork=null,TaskCompletionSource? Completion=null);
    private readonly ScienceStore science;private readonly ScientificAccessSettings settings;private readonly ScientificHttp http;
    private readonly IScientificDataProvider[] providers;
    private readonly Dictionary<string,Job> queue=[];
    private readonly Dictionary<string,DateTimeOffset> retryAfter=[];
    private readonly HashSet<string> pausedScopes=[];
    public void PauseScope(string scope,bool value)
    {
        lock(gate)
        {
            if(value){pausedScopes.Add(scope);foreach(var pair in cancellations.Where(p=>scopes.GetValueOrDefault(p.Key)==scope)){preempted.Add(pair.Key);pair.Value.Cancel();}}
            else pausedScopes.Remove(scope);
        }
        Wake();
    }
    private readonly SemaphoreSlim wake=new(0,1);
    private bool steadyFastFlight;
    public void SetTrajectoryRegionHint(bool steadyFast){lock(gate)steadyFastFlight=steadyFast;}
    private bool paused,flightActive;
    public void FlightActive(bool value){lock(gate){if(value&&!flightActive){var keep=cancellations.Keys.FirstOrDefault(key=>scopes.GetValueOrDefault(key)?.StartsWith("library:")!=true);foreach(var p in cancellations.Where(x=>x.Key!=keep)){preempted.Add(p.Key);p.Value.Cancel();}}if(!value&&flightActive){foreach(var scope in scopes.Values.Where(s=>s.StartsWith("plan:")).Distinct())pausedScopes.Add(scope);foreach(var p in cancellations.Where(x=>scopes.GetValueOrDefault(x.Key)=="flight"||scopes.GetValueOrDefault(x.Key)?.StartsWith("plan:")==true)){preempted.Add(p.Key);p.Value.Cancel();}foreach(var job in queue.Values.Where(j=>j.Scope=="flight").ToArray())queue.Remove(job.Key);}flightActive=value;}Wake();}
    private string? flightResourceHold;
    public void FlightResourceHold(string? reason){lock(gate){if(reason is not null&&flightResourceHold is null)foreach(var p in cancellations.Where(x=>scopes.GetValueOrDefault(x.Key)?.StartsWith("library:")!=true)){preempted.Add(p.Key);p.Value.Cancel();}flightResourceHold=reason;}Wake();}
    public void ReprioritizeScope(string scope,IReadOnlyDictionary<string,double> priorities){lock(gate)foreach(var job in queue.Values.Where(j=>j.Scope==scope).ToArray())if(priorities.TryGetValue(job.Key,out var priority))queue[job.Key]=job with{Priority=priority};Wake();}
    public void LimitScopeWindow(string scope,IReadOnlySet<string> desired){lock(gate)foreach(var job in queue.Values.Where(j=>j.Scope==scope&&!desired.Contains(j.Key)).ToArray()){queue.Remove(job.Key);retryAfter.Remove(job.Key);if(states.TryGetValue(job.Key,out var state))states[job.Key]=state with{Status="Superseded",Message="Upcoming aircraft regions reprioritized; this unsent area remains in the full plan inventory."};}Wake();}
    public void RetainScopeKeys(string scope,IReadOnlySet<string> keys){lock(gate)foreach(var key in scopes.Where(p=>p.Value==scope&&!keys.Contains(p.Key)).Select(p=>p.Key).ToArray())Cancel(key);}

    private readonly HashSet<string> flightKeys=[],urgentFlightKeys=[],preempted=[];
    private DateTimeOffset lastPreemption;
    private double preparationSeconds=45;
    private readonly ScientificTransferLedger flightTransfers=new();
    private readonly ScientificTransferLedger preflightTransfers=new(rolling:false);
    private readonly Dictionary<string,string> scopes=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(long Length,DateTime Modified,string Sha)> verified=new();
    internal ScientificHttp Http => http;
    internal IScientificDataProvider Provider(string id)=>providers.Single(p=>p.Id==id);
    public string? AvailabilityError => science.AvailabilityError();
    public object NetworkDiagnostics()=>http.Diagnostics();
    public double EstimatedPreparationSeconds {get{lock(gate)return preparationSeconds;}}
    public static string AreaKey(RemoteKind kind,GeographicBounds area)=>kind+":"+StacScientificProvider.Bounds(area);
    private void Wake(){if(wake.CurrentCount==0)try{wake.Release();}catch(SemaphoreFullException){}}
    public ScientificQueueSummary QueueSummary()
    {
        var p=settings.Get().Preferences;var f=flightTransfers.Snapshot(p.PrefetchHourlyBytes);var pre=preflightTransfers.Snapshot(p.PreflightTransferBytes);
        lock(gate)return new(queue.Count,cancellations.Count,paused,f.Spent,p.PrefetchHourlyBytes,preparationSeconds,f.Reserved,f.Available,pre.Spent,pre.Reserved,p.PreflightTransferBytes,pre.Available,flightTransfers.TotalBytes+preflightTransfers.TotalBytes,flightResourceHold,flightActive?1:p.ConcurrentDownloads);
    }
    public void Pause(bool value){lock(gate)paused=value;Wake();}
    public void ReprioritizeFlight(IReadOnlyDictionary<string,double> priorities)
    {
        lock(gate){flightKeys.Clear();flightKeys.UnionWith(priorities.Keys);urgentFlightKeys.Clear();urgentFlightKeys.UnionWith(priorities.Where(p=>p.Value<=0).Select(p=>p.Key));foreach(var job in queue.Values.Where(j=>j.Scope=="flight").ToArray())
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
            foreach(var job in queue.Values.Where(j=>key is null||j.Key==key).ToArray()){queue.Remove(job.Key);retryAfter.Remove(job.Key);job.Completion?.TrySetCanceled();}
            foreach(var s in states.Values.Where(s=>s.Status is ("Queued" or "Waiting for download allowance")&&(key is null||key==s.Key)).ToArray())
                states[s.Key]=s with{Status="Unavailable",Message="Queued preparation cancelled; no download was started.",UpdatedUtc=DateTimeOffset.UtcNow};
        }
    }
    public void CancelScope(string scope)
    {lock(gate)foreach(var key in scopes.Where(p=>p.Value==scope).Select(p=>p.Key).ToArray())Cancel(key);}
    public ScientificAccessConfiguration Configure(ScientificAccessPreferences preferences)
    {var saved=settings.Save(preferences);Interlocked.Increment(ref configurationRevision);Cancel();lock(gate){foreach(var key in states.Keys.Where(k=>!cancellations.ContainsKey(k)).ToArray())states.Remove(key);}return saved;}
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
                {if(queue.TryGetValue(key,out var queued)&&priority<queued.Priority)queue[key]=queued with{Priority=priority};if(scope=="flight"&&queue.TryGetValue(key,out var held)&&pausedScopes.Contains(held.Scope)){queue[key]=held with{Priority=priority,Scope=scope};scopes[key]=scope;states[key]=previous with{Scope=scope};}continue;}
                if(states.Count>=256){var old=states.Values.Where(s=>s.Status is not("Queued" or "Downloading" or "Waiting for download allowance")).OrderBy(s=>s.UpdatedUtc).FirstOrDefault();if(old is not null){states.Remove(old.Key);tickets.Remove(old.Key);scopes.Remove(old.Key);retryAfter.Remove(old.Key);}else continue;}
                string? reason=!p.OnlineEnabled||!p.PrivacyAccepted?"Scientific online access is disabled. Enable it separately from basemaps in Settings. Verified local sources still work.":background&&!p.BackgroundRetrieval?"Background retrieval is disabled. Prepare this area explicitly before flight.":p.UnmeteredOnly&&settings.IsUnmetered()!=true?"Waiting for a reliably detected unmetered connection. Change the unmetered-only control to proceed on this connection.":null;
                // Local hyperspectral checks run without online consent and never trigger NASA downloading.
                if(kind==RemoteKind.Hyperspectral)reason=null;
                Set(new(key,kind,"Queued","Checking retained sources before any external request. "+reason,area,EstimatedDownloadBytes:kind==RemoteKind.Hyperspectral?0:null,EstimatedStorageBytes:StorageBound(kind),SamplePoint:samplePoint,Scope:scope));
                {
                    var ticket=Guid.NewGuid();tickets[key]=ticket;scopes[key]=scope;
                    if(queue.Count>=64)
                    {
                        var least=queue.Values.MaxBy(j=>j.Priority)!;
                        if(priority<least.Priority){queue.Remove(least.Key);least.Completion?.TrySetCanceled();if(states.TryGetValue(least.Key,out var displaced))states[least.Key]=displaced with{Status="Superseded",Message="Yielded to more urgent aircraft data; progressive preflight will revisit this area."};}
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
        var candidate=cancellations.FirstOrDefault(p=>scopes.GetValueOrDefault(p.Key)?.StartsWith("plan:",StringComparison.Ordinal)==true&&!urgentFlightKeys.Contains(p.Key)&&!urgentFlightKeys.Any(key=>states.TryGetValue(key,out var wanted)&&states.TryGetValue(p.Key,out var current)&&wanted.Kind==current.Kind&&wanted.Area.West<current.Area.East&&wanted.Area.East>current.Area.West&&wanted.Area.South<current.Area.North&&wanted.Area.North>current.Area.South)&&!p.Value.IsCancellationRequested&&states.GetValueOrDefault(p.Key)?.Status=="Downloading");
        if(candidate.Value is null)return;lastPreemption=DateTimeOffset.UtcNow;preempted.Add(candidate.Key);candidate.Value.Cancel();
    }
    public void RetryScope(string scope,IReadOnlySet<string>? explicitlyRequestedKeys=null){lock(gate)foreach(var key in states.Where(p=>(p.Value.Scope==scope||explicitlyRequestedKeys?.Contains(p.Key)==true)&&p.Value.Status is not("Ready" or "Dataset Ready" or "Downloading" or "Queued")).Select(p=>p.Key).ToArray()){states.Remove(key);tickets.Remove(key);retryAfter.Remove(key);}}
    public void ForgetDeletedSources(){http.ClearNumericCache();verified.Clear();RefreshLocalReadiness();}
    public void RefreshLocalReadiness()
    {Interlocked.Increment(ref configurationRevision);lock(gate)foreach(var key in states.Where(p=>p.Value.Status is not("Queued" or "Downloading" or "Waiting for download allowance")).Select(p=>p.Key).ToArray()){states.Remove(key);tickets.Remove(key);}}
    public bool VerifyRetainedSource(ScientificSource source,CancellationToken token=default)
    {
        foreach(var file in source.Files)
        {
            token.ThrowIfCancellationRequested();using var bytes=File.OpenRead(ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.RelativePath));var info=new FileInfo(bytes.Name);var identity=(bytes.Length,info.LastWriteTimeUtc,file.Sha256);
            if(verified.TryGetValue(bytes.Name,out var cached)&&cached==identity)continue;
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);var block=new byte[65536];int read;
            while((read=bytes.Read(block))>0){token.ThrowIfCancellationRequested();hash.AppendData(block,0,read);}
            if(bytes.Length!=file.Bytes||Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()!=file.Sha256)return false;verified[bytes.Name]=identity;
        }
        return true;
    }
    public ScientificReusePlan LocalCoverage(GeographicBounds area,RemoteKind kind,ScientificReuseContext? context=null,CancellationToken token=default)
    {
        using var originals=science.BeginAnalysis();var p=settings.Get().Preferences;var single=ScientificSourceSelection.Select(science.Availability(area.Center,kind,area).Where(a=>ScientificSourceSelection.CoversArea(a.Source,area)),kind,p);
        if(single is not null){if(!VerifyRetainedSource(single,token))throw new IOException("Retained original failed verification.");return new([new(area,single)],[],single.Manifest.Acquisition?.UsablePixels==0?[area]:[],["Single-source file/extent reuse; per-pixel QA still applies."]);}
        return ScientificSourceSelection.ReuseArea(science,area,s=>ScientificSourceSelection.Select([new(s,true,true,null)],kind,p) is not null,s=>VerifyRetainedSource(s,token),token,context);
    }
    public static void ValidateArea(GeographicBounds a)
    {
        if(new[]{a.West,a.South,a.East,a.North}.Any(v=>!double.IsFinite(v))||a.West< -180||a.East>180||a.South< -80||a.North>84||a.East<=a.West||a.North<=a.South||a.East-a.West>.061||a.North-a.South>.061)
            throw new ArgumentException("Automatic preparation supports bounded areas up to 0.06° per side within 80°S–84°N; split large or dateline-crossing surveys. No global scenes will be downloaded.");
    }
    internal static long StorageBound(RemoteKind kind)=>kind switch{RemoteKind.Multispectral=>176160768,RemoteKind.Thermal=>109051904,_=>37748736};
    private async Task Run()
    {
        var active=new List<Task>();
        try
        {
            while(!stopping.IsCancellationRequested)
            {
                active.RemoveAll(t=>t.IsCompleted);
                Job? job=null;
                lock(gate)if(!paused&&active.Count<(flightActive?1:settings.Get().Preferences.ConcurrentDownloads)&&queue.Count>0)
                {job=queue.Values.Where(j=>!pausedScopes.Contains(j.Scope)).Where(j=>!(flightActive&&(j.Scope.StartsWith("library:")||flightResourceHold is not null))).Where(j=>!retryAfter.TryGetValue(j.Key,out var after)||after<=DateTimeOffset.UtcNow).MinBy(j=>j.Priority);if(job is not null)queue.Remove(job.Key);}
                if(job is not null){active.Add(Task.Run(()=>Execute(job),stopping.Token));continue;}
                await wake.WaitAsync(TimeSpan.FromMilliseconds(200),stopping.Token);
            }
        }
        catch(OperationCanceledException){}
        finally{try{await Task.WhenAll(active);}catch(OperationCanceledException){}}
    }
    internal Task ScheduleLibrary(string key,RemoteKind kind,GeographicBounds area,Func<CancellationToken,Task> action)
    {
        lock(gate)
        {
            if(queue.Count>=64)throw new IOException("Scientific preparation queue is busy; resume this job later.");
            var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var scope="library:"+key.Split('/')[0];
            queue[key]=new(key,kind,area,Guid.NewGuid(),100000,scope,LibraryWork:action,Completion:completion);scopes[key]=scope;Wake();return completion.Task;
        }
    }
    internal async Task WaitForLibraryScope(string scope,CancellationToken token)
    {
        while(true)
        {
            token.ThrowIfCancellationRequested();lock(gate)if(!queue.Values.Any(j=>j.Scope==scope)&&!cancellations.Keys.Any(key=>scopes.GetValueOrDefault(key)==scope))return;
            await Task.Delay(25,token);
        }
    }
    internal ScientificTransferLedger.Lease AcquireLibraryTransfer()=>preflightTransfers.Acquire(()=>settings.Get().Preferences.PreflightTransferBytes);
    internal bool LibraryAllowed {get{lock(gate)return !flightActive&&!paused&&settings.Get().Preferences.BackgroundRetrieval;}}
    private async Task Execute(Job job)
    {
        if(job.LibraryWork is not null)
        {
            if(!LibraryAllowed){job.Completion!.TrySetCanceled();return;}
            using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);lifetime.CancelAfter(TimeSpan.FromMinutes(12));lock(gate)cancellations[job.Key]=lifetime;
            try{await job.LibraryWork(lifetime.Token);job.Completion!.TrySetResult();}
            catch(OperationCanceledException){job.Completion!.TrySetCanceled();}
            catch(Exception error){job.Completion!.TrySetException(error);}
            finally{lock(gate){cancellations.Remove(job.Key);scopes.Remove(job.Key);preempted.Remove(job.Key);}Wake();}return;
        }
        var revision = ConfigurationRevision;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);cancel.CancelAfter(TimeSpan.FromMinutes(12));
        lock(gate)cancellations[job.Key]=cancel;
        ScientificDataReadiness state;lock(gate){if(!tickets.TryGetValue(job.Key,out var ticket)||ticket!=job.Ticket||!states.TryGetValue(job.Key,out state!)||state.Status is not("Queued" or "Waiting for download allowance")){cancellations.Remove(job.Key);return;}}
        var token=cancel.Token;var started=DateTimeOffset.UtcNow;ScientificTransferLedger.Lease? transfer=null;
        ScientificDiagnostics.Record("start",job.Key,job.Scope,job.Kind);
        try
        {
            using var sourceLease=science.BeginAnalysis();
            var p=settings.Get().Preferences;
            // Reuse verified sources before any network traffic. They remain retained originals,
            // not cleanup-eligible caches; actual sampling still applies per-pixel QA.
            var available=ScientificSourceSelection.Select(science.Availability(job.Point??job.Area.Center,job.Kind).Where(a=>a.FilesAvailable&&a.CoversPoint==true&&
                (job.Kind==RemoteKind.Hyperspectral||p.SourceModes?.GetValueOrDefault(job.Kind)=="local"||ScientificSourceSelection.CoversArea(a.Source,job.Area))),job.Kind,p);
            if(available is not null)
            {
                if(!VerifyRetainedSource(available,token))throw new IOException("Retained scientific source failed its integrity check. Restore/reimport it; no download replaced the original.");
                using var coverageRaster=new ScientificRaster(Path.Combine(science.DirectoryPath,available.RelativeDirectory),available.Manifest.Bands[0]);
                var full=ScientificSourceSelection.CoversArea(available,job.Area);
                ScientificDiagnostics.Record("source-reused",job.Key,job.Scope,job.Kind,available.Manifest.Provider,result:available.Id.ToString());
                Set(state with{SourceCoverage=ScientificRaster.GridCoverage(coverageRaster.Grid),Status=!full?"Partially usable":job.Kind==RemoteKind.Hyperspectral?"Dataset Ready":available.Manifest.Acquisition?.UsablePixels==0?"Limited Coverage":"Ready",Message="Verified retained source available offline; actual per-pixel masks still apply.",ReusedBytes=available.Files.Sum(f=>f.Bytes),Stage="Ready",Provider=available.Manifest.Provider,Dataset=available.Manifest.Name,ObservationUtc=available.Manifest.ObservationUtc,SourceId=available.Id,Quality=available.Manifest.Acquisition?.Quality});return;
            }

            // Hyperspectral prediction assesses the actual sample point above;
            // never replace its native flightline support with generic whole-cell union.
            var union=job.Kind==RemoteKind.Hyperspectral?new ScientificReusePlan([],[job.Area],[],[]):ScientificSourceSelection.ReuseArea(science,job.Area,source=>ScientificSourceSelection.Select([new(source,true,true,null)],job.Kind,p) is not null,part=>VerifyRetainedSource(part,token),token);
            var retained=union.Reused.Select(r=>r.Source).DistinctBy(s=>s.Id).ToList();
            if(union.Missing.Length==0)
            {Set(state with{Status=job.Kind==RemoteKind.Hyperspectral?"Dataset Ready":"Ready",Stage="Ready",Message="Several verified compatible retained windows jointly provide numeric support. Native grids, QA and per-observation source identity remain independent; no common-resolution mosaic or airborne measurement was created.",SourceIds=retained.Select(s=>s.Id).ToArray(),ReusedBytes=retained.Sum(s=>s.Files.Sum(f=>f.Bytes))});return;}
            if(union.Uncertain||union.QualityLimited.Length>0)
            {Set(state with{Status="Partially usable",Stage="Review coverage",Message="Retained partial support requires review: "+string.Join(" ",union.Limitations)+(union.QualityLimited.Length>0?" Some original pixels are nodata or quality-excluded; re-downloading that same source would not make them valid.":" No unexamined footprint was treated as valid coverage."),SourceIds=retained.Select(s=>s.Id).ToArray(),ReusedBytes=retained.Sum(s=>s.Files.Sum(f=>f.Bytes))});return;}
            if(job.Kind==RemoteKind.Hyperspectral)
            {
                var local=science.Availability(kind:RemoteKind.Hyperspectral).Take(1).ToArray();
                Set(state with{Status=local.Length==0?"Dataset Required":local.All(a=>!a.FilesAvailable)?"Dataset Invalid":"Dataset Outside Coverage",Message=local.Length==0?"Import a compatible hyperspectral dataset covering your research area. No account or automatic download is required.":"Review imported dataset coverage or import another compatible dataset. No automatic hyperspectral download is required."});return;
            }
            string? blocked=p.SourceModes?.GetValueOrDefault(job.Kind)=="local"?"My Datasets Only: no compatible local dataset covers this area. Import a dataset or change this instrument’s source mode; no provider download was attempted.":!p.OnlineEnabled||!p.PrivacyAccepted?"Scientific online access is disabled; retained local sources remain usable.":job.Background&&!p.BackgroundRetrieval?"Background retrieval is disabled.":p.UnmeteredOnly&&settings.IsUnmetered()!=true?"Waiting for a reliably detected unmetered connection.":null;
            if(blocked is not null){Set(state with{Status="Offline",Message=blocked});return;}
            bool inFlight;lock(gate)inFlight=flightActive;
            var ledger=job.Scope.StartsWith("plan:",StringComparison.Ordinal)&&!inFlight?preflightTransfers:flightTransfers;
            transfer=ledger.Acquire(()=>job.Scope.StartsWith("plan:",StringComparison.Ordinal)&&!inFlight?settings.Get().Preferences.PreflightTransferBytes:settings.Get().Preferences.PrefetchHourlyBytes);
            state=state with{Status="Downloading",Stage="Finding source",Message="Finding a suitable scientific source."};Set(state);
            var budget=new ScientificDownloadBudget(p.MaximumDownloadBytes,bytes=>
            {
                transfer.Charge(bytes);
                state=state with{DownloadedBytes=bytes,Seconds=(DateTimeOffset.UtcNow-started).TotalSeconds};Set(state);
            },transfer.EnsureTotal);
            ScientificDiscovery? discovery=null;ScientificSource? source=null;var providerFailures=new List<string>();
            var needed=retained.Count==0?new[]{job.Area}:union.Missing;
            foreach(var required in needed)
            {
            source=null;discovery=null;
            foreach(var provider in providers.Where(provider=>provider.Supports(job.Kind)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var requested=required;
                    // Optical source-block costs dominate tiny adjacent requests. Retain a bounded
                    // native-grid regional crop when steady motion/preflight justifies it.
                    if(retained.Count==0&&(job.Kind is RemoteKind.Thermal or RemoteKind.Multispectral)&&(job.Scope.StartsWith("plan:")||steadyFastFlight)&&requested.East-requested.West<.039)
                    {
                        var west=Math.Floor(requested.Center.Longitude/.04)*.04;var south=Math.Floor(requested.Center.Latitude/.04)*.04;requested=new(west,south,west+.04,south+.04);
                    }
                    if(retained.Count>0)
                    {
                        var anchor=retained[0];var a=anchor.Manifest.Acquisition!;
                        if(a.Workflow=="usgs-3dep-numeric")
                        {if(provider.Id!="usgs-3dep")continue;discovery=await provider.Discover(job.Kind,required,p,budget,token);}
                        else if(a.Workflow=="stac-cog-native-subset"&&provider is StacScientificProvider stac&&a.ProviderMetadataJson is {} metadata)
                        {using var original=System.Text.Json.JsonDocument.Parse(metadata);if(!required.Corners.Append(required.Center).All(point=>StacScientificProvider.Contains(original.RootElement.GetProperty("geometry"),point)))continue;discovery=stac.Describe(original.RootElement,job.Kind,required);}
                        else continue;
                        var id=a.Workflow=="usgs-3dep-numeric"?"usgs-3dep":job.Kind==RemoteKind.Terrain?"copernicus":job.Kind==RemoteKind.Thermal?"landsat":"sentinel-2";
                        if(discovery is not null){System.Text.Json.JsonElement? feature=id=="usgs-3dep"?null:System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(discovery.ProviderMetadataJson!);var sourceRevision=ScientificCatalog.Describe(ScientificCatalog.Provider(id),discovery,feature).Revision;if(sourceRevision!=ScientificCatalog.RetainedRevision(anchor,id))throw new InvalidOperationException("The missing-window source revision changed; retained data was preserved. Review the exact source before continuing.");discovery=discovery with{CatalogRevision=sourceRevision};}
                    }
                    else
                    {
                        discovery=await provider.Discover(job.Kind,requested,p,budget,token);
                        // A larger crop can cross a real scene edge. Retry the exact required region.
                        if(discovery is null&&requested!=required)discovery=await provider.Discover(job.Kind,required,p,budget,token);
                    }
                }
                catch(Exception e)when(provider.Id=="usgs-3dep"&&e is IOException or HttpRequestException or JsonException){providerFailures.Add("USGS 3DEP discovery unavailable: "+(e is IOException?e.Message:"provider network/metadata error"));continue;}
                if(discovery is null)continue;
                state=state with{Provider=discovery.Manifest.Provider,Dataset=discovery.Manifest.Name,ObservationUtc=discovery.Manifest.ObservationUtc,Quality=discovery.Quality,Stage="Downloading",Message="Retrieving numeric scientific bands."};Set(state);
                try { source=await PrepareSource(discovery,budget,token,stage=>{state=state with{Stage=stage};Set(state);}); }
                catch(Exception e)when(provider.Id=="usgs-3dep"&&e is not StorageLimitException&&e is IOException or HttpRequestException){providerFailures.Add("USGS 3DEP numeric preparation unavailable: "+(e is IOException?e.Message:"provider network error"));continue;}
                if(source is not null)break;
            }
            if(source is null){Set(state with{Status="Unavailable",Message="No suitable numeric dataset covers this entire bounded area within the date/quality policy. "+string.Join(" ",providerFailures)+" Try a different area/date or retry the provider; advanced manual import remains available.",DownloadedBytes=budget.Bytes});return;}
            retained.Add(source);
            }
            var originals=retained.DistinctBy(s=>s.Id).ToArray();var usable=originals.Any(s=>s.Manifest.Acquisition?.UsablePixels==0)?0:1;
            Set(state with{Status=usable==0?"Limited Coverage":"Ready",Message=usable==0?"Numeric sources prepared, but the bounded QA preview found excluded pixels. Actual per-pixel masks remain authoritative.":"Verified numeric sources retained. Existing compatible windows were reused; only required gaps were acquired. Each observation retains its actual source/date/grid; no past airborne observation was created.",SourceId=originals.Length==1?originals[0].Id:null,SourceIds=originals.Length>1?originals.Select(s=>s.Id).ToArray():null,SourceCoverage=originals.Length==1?ScientificRaster.GridCoverage(originals[0].Grids.Values.First()):null,ReusedBytes=union.Reused.Select(r=>r.Source).DistinctBy(s=>s.Id).Sum(s=>s.Files.Sum(f=>f.Bytes)),DownloadedBytes=budget.Bytes,Stage="Ready",Seconds=(DateTimeOffset.UtcNow-started).TotalSeconds});
        }
        catch(ScientificAllowanceWaitException e)
        {
            Set(state with{Status="Waiting for download allowance",Stage="Waiting",Message=e.Message});
            lock(gate){if(!stopping.IsCancellationRequested&&!cancel.IsCancellationRequested){queue[job.Key]=job;retryAfter[job.Key]=DateTimeOffset.UtcNow.AddSeconds(5);}}
        }
        catch(OperationCanceledException){bool yielded;lock(gate)yielded=preempted.Contains(job.Key);Set(state with{Status=yielded?"Superseded":"Unavailable",Message=yielded?"Preparation paused or preempted. Partial staging remains accounted; Resume can reuse verified originals and retry bounded work.":"Preparation cancelled or timed out. No partial source was advertised as ready. Retained staging is visible in Storage Management."});}
        catch(Exception e)when(e is IOException or HttpRequestException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or KeyNotFoundException or FormatException or NotSupportedException)
        {Set(state with{Status="Unavailable",Message=e is HttpRequestException?"Scientific provider is unreachable. Retry later or use verified offline sources.":e is JsonException or KeyNotFoundException?"Provider metadata is incompatible; no scientific values were guessed.":e.Message});}
        finally
        {
            if(transfer is not null)ScientificDiagnostics.Record("allowance-released",job.Key,job.Scope,job.Kind,bytes:transfer.HeldBytes,result:"Unused reservation released; actual transferred bytes remain charged.");
            transfer?.Dispose();
            lock(gate){if(preempted.Contains(job.Key)&&flightActive&&flightResourceHold is not null&&!pausedScopes.Contains(job.Scope)&&job.Scope!="flight"){queue[job.Key]=job;Set(state with{Status="Queued",Message="Waiting for Flight Priority: "+flightResourceHold});}cancellations.Remove(job.Key);preempted.Remove(job.Key);if(revision!=ConfigurationRevision&&tickets.GetValueOrDefault(job.Key)==job.Ticket){states.Remove(job.Key);tickets.Remove(job.Key);}if(state.DownloadedBytes>0)preparationSeconds=Math.Clamp(.7*preparationSeconds+.3*(DateTimeOffset.UtcNow-started).TotalSeconds,5,300);}
            ScientificDiagnostics.Record("finish",job.Key,job.Scope,job.Kind,state.Provider,state.DownloadedBytes,Snapshot().FirstOrDefault(x=>x.Key==job.Key)?.Status);
            Wake();
        }
    }
    internal async Task<ScientificSource?> PrepareSource(ScientificDiscovery d,ScientificDownloadBudget budget,CancellationToken token,Action<string>? stage=null,string? checkpointRoot=null)
    {
        using var originals=science.BeginAnalysis();
        var descriptor=ScientificCatalog.DiscoveryRevision(d);var manifest=ScientificCatalog.Hash(d.Manifest with{Acquisition=null});
        // Bounded striped locks coalesce identical in-flight originals across
        // preflight, prediction and Library jobs without retaining one lock per source.
        var key=ScientificCatalog.Hash(new{manifest,d.Area,descriptor});var serial=sourcePreparations[Convert.ToInt32(key[..2],16)%sourcePreparations.Length];
        await serial.WaitAsync(token);
        try
        {
            if(descriptor is not null)foreach(var source in science.StreamSources(new(Kind:d.Manifest.Kind,Area:d.Area)))
            {
                token.ThrowIfCancellationRequested();
                if(source.Manifest.Acquisition?.RequestedArea!=d.Area||ScientificCatalog.RetainedRevision(source)!=descriptor||ScientificCatalog.Hash(source.Manifest with{Acquisition=null})!=manifest||!science.InspectAvailability(source).FilesAvailable)continue;
                foreach(var file in source.Files){using var bytes=File.OpenRead(ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.RelativePath));if(bytes.Length!=file.Bytes||Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant()!=file.Sha256)throw new IOException("Exact retained source changed; no replacement transfer was permitted.");}
                stage?.Invoke("Validating");return source;
            }
            return await PrepareSourceCore(d,budget,token,stage,checkpointRoot);
        }
        finally{serial.Release();}
    }
    private readonly SemaphoreSlim[] sourcePreparations=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1)).ToArray();
    private async Task<ScientificSource?> PrepareSourceCore(ScientificDiscovery d,ScientificDownloadBudget budget,CancellationToken token,Action<string>? stage,string? checkpointRoot)
    {
        science.Initialize();
        if(await RecoverPreparedSource(d,token,stage,checkpointRoot) is {} recovered)return recovered;
        using var reservation=science.Management?.ReserveScience(StorageBound(d.Manifest.Kind)+(checkpointRoot is null?0:budget.Maximum));
        var relative="Downloads/"+Guid.NewGuid().ToString("N");var folder=StorageManager.ResolveDataset(science.DirectoryPath,science.LibraryId,relative);
        var names=d.Manifest.Bands.Select(b=>b.File).Append("source.json").ToArray();
        var ledger=science.BeginManagedWrite("Automatic numeric source preparation",names.Select(n=>relative+"/"+n));Directory.CreateDirectory(folder);
        var checkpoints=new List<ScientificRangeCheckpoint>();
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
            var checkpoint=checkpointRoot is null?null:new ScientificRangeCheckpoint(science,checkpointRoot+"/"+asset.Band.Id);
            if(checkpoint is not null)checkpoints.Add(checkpoint);
            using var remote=checkpoint is null?new ScientificRangeStream(http,asset.Url,budget,token):new ScientificRangeStream(http,asset.Url,budget,token,checkpoint);using var raster=new ScientificRaster(remote,asset.Band);
            raster.CopySubset(d.Area,Path.Combine(folder,asset.Band.File),token);
        }
        stage?.Invoke("Validating");
        var locations=Enumerable.Range(0,81).Select(i=>new Coordinates(d.Area.South+(d.Area.North-d.Area.South)*(i/9+.5)/9,d.Area.West+(d.Area.East-d.Area.West)*(i%9+.5)/9)).ToArray();
        var checkedBands=manifest.Bands.Select(b=>{using var raster=new ScientificRaster(folder,b);return new RemoteBandValues(b.Id,b.Unit,b.WavelengthNm,locations.Select(raster.Read).ToArray(),"Readiness preview, not a flight observation",raster.Grid);}).ToArray();
        _=RemoteCalculations.Apply(manifest.Kind,checkedBands);
        var numeric=checkedBands.Where(b=>b.Id is not("QA_PIXEL" or "QA_RADSAT" or "SCL")).ToArray();
        var usable=Enumerable.Range(0,81).Count(i=>numeric.Any(b=>b.Values[i].HasValue));
        var provenance=new AutomaticSourceProvenance(d.Manifest.Kind==RemoteKind.Terrain&&d.NumericExport is not null?"usgs-3dep-numeric":"stac-cog-native-subset",d.Catalog,d.Item,d.Assets.Select(a=>a.Url.AbsoluteUri).Concat(d.NumericExport is null?[]:new[]{d.NumericExport}).ToArray(),d.Area,DateTimeOffset.UtcNow,
            d.NumericExport is null?"Lossless native-grid, native-DN GeoTIFF crop via validated HTTP 206 ranges; no overview substitution or interpolation.":"Provider numeric F32 nearest-neighbor export in WGS84; composite native resolution and datum remain unspecified.",d.Quality,d.Resolution,budget.Bytes,d.Limitations??[],d.ProviderMetadataJson,81,usable,d.CatalogRevision);
        manifest=manifest with{Acquisition=provenance};
        PreferenceStore.WriteAtomic(Path.Combine(folder,"source.json"),manifest);
        stage?.Invoke("Saving");
        var imported=await science.ImportSourceAsync(Path.Combine(folder,"source.json"),token);
        // ImportSourceAsync hashed and verified every copied numeric file. Remove only
        // this owned, duplicated preparation folder; the imported originals stay retained.
        foreach(var name in names)File.Delete(Path.Combine(folder,name));Directory.Delete(folder);File.Delete(ledger);
        foreach(var checkpoint in checkpoints)checkpoint.Complete();
        return imported;
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
