using System.Security.Cryptography;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Durable orchestration only. Actual work runs in AutomaticScientificData's existing
// priority queue, shares its numeric reader/importer, and yields to active flights.
public sealed partial class ScientificLibraryJobs:IDisposable
{
    private readonly ScienceStore science;private readonly ScientificCatalog catalog;private readonly AutomaticScientificData automatic;
    private readonly ScientificAccessSettings settings;private readonly CancellationTokenSource stopping=new();private readonly SemaphoreSlim commands=new(1);private readonly Task worker;
    public string? Warning {get;private set;}
    private string? initialized;private readonly object recoveryGate=new();
    private void EnsureRecovered(){lock(recoveryGate){if(science.AvailabilityError() is {} unavailable){initialized=null;throw new IOException(unavailable);}var identity=science.DirectoryPath+":"+science.LibraryId;if(initialized==identity)return;science.RecoverLibraryJobs();initialized=identity;}}
    private readonly IEarthdataCredentials earthdata;private readonly IScientificSwathImport? swathImport;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,byte> activeNasa=[];
    public ScientificLibraryJobs(ScienceStore science,ScientificCatalog catalog,AutomaticScientificData automatic,ScientificAccessSettings settings,IEarthdataCredentials? earthdata=null,IScientificSwathImport? swathImport=null)
    {this.swathImport=swathImport;this.science=science;this.catalog=catalog;this.automatic=automatic;this.settings=settings;this.earthdata=earthdata??new UnavailableEarthdataCredentials();try{EnsureRecovered();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){Warning=error.Message;}worker=Task.Run(Run);}
    private string SettingsHash(bool nasa=false)=>nasa?ScientificCatalog.Hash(new{general=SettingsHash(),settings.Get().Preferences.NasaOriginalMaximumBytes}):ScientificCatalog.Hash(new{settings.Get().Preferences.OnlineEnabled,settings.Get().Preferences.PrivacyAccepted,settings.Get().Preferences.MaximumDownloadBytes,settings.Get().Preferences.PreflightTransferBytes,settings.Get().Preferences.UnmeteredOnly,settings.Get().Preferences.BackgroundRetrieval});
    public static GeographicBounds[] Expand(ScientificLibrarySelection selection)
    {
        if(selection.Version!=1||selection.Areas is null||selection.Scenes is null||selection.Areas.Length is <1 or >256||selection.Scenes.Length is <1 or >16||selection.Areas.Any(a=>a is null)||selection.Scenes.Any(s=>s is null))throw new ArgumentException("Choose version 1, 1–256 areas and 1–16 exact scenes.");
        var cells=new HashSet<GeographicBounds>();
        foreach(var area in selection.Areas)
        {
            ScientificCatalog.ValidateBounds(area);
            var width=(int)Math.Ceiling((area.East-area.West)/.02-1e-8);var height=(int)Math.Ceiling((area.North-area.South)/.02-1e-8);
            if((long)width*height>100000)throw new ArgumentException("Selection exceeds 100,000 geometry candidates. Use smaller regions, each no more than 4,096 work units.");
            for(var y=0;y<height;y++)for(var x=0;x<width;x++)
            {
                // The final cell must retain the pilot's exact endpoint. Repeated
                // floating additions can otherwise leave a real unreviewed sliver.
                var cell=new GeographicBounds(area.West+x*.02,area.South+y*.02,x==width-1?area.East:area.West+(x+1)*.02,y==height-1?area.North:area.South+(y+1)*.02);cells.Add(cell);
                if(cells.Count>4096)throw new ArgumentException("Selection exceeds 4,096 work units. Divide this region into narrower explicit bounding boxes, polygons or fewer scenes.");
            }
        }
        if(selection.Polygon is {} polygon)
        {
            if(polygon.Length is <3 or >500||polygon.Any(p=>!double.IsFinite(p.Latitude)||!double.IsFinite(p.Longitude)))throw new ArgumentException("Supply 3–500 finite polygon vertices.");
            SurveyGeometry.Validate(polygon);
            var polygonBounds=new GeographicBounds(polygon.Min(p=>p.Longitude),polygon.Min(p=>p.Latitude),polygon.Max(p=>p.Longitude),polygon.Max(p=>p.Latitude));ScientificCatalog.ValidateBounds(polygonBounds);
            // Existing geodesic polygon traversal includes narrow boundary cells. Actual
            // numeric windows remain rectangular and their small edge overfetch is disclosed.
            var intersecting=ScientificGeographicWork.Cells([], [polygon],strict:true).ToArray();
            cells.RemoveWhere(c=>!intersecting.Any(a=>a.West<c.East&&a.East>c.West&&a.South<c.North&&a.North>c.South));
        }
        if(selection.RegionGeometry is {} geometry)
        {
            var rings=ScientificCatalog.RegionRings(geometry);
            cells.RemoveWhere(c=>!c.Corners.Append(c.Center).Any(p=>StacScientificProvider.Contains(geometry,p))&&!rings.SelectMany(r=>r).Any(c.Contains));
        }
        return cells.OrderBy(a=>a.South).ThenBy(a=>a.West).ToArray();
    }
    private static bool Compatible(ScientificSceneSelection scene,ScientificSource s)=>s.Manifest.Kind==ScientificCatalog.Provider(scene.Provider).Kind&&
        ScientificCatalog.RetainedRevision(s,scene.Provider)==scene.Revision&&s.Manifest.Acquisition is {} a&&
        (scene.Provider=="usgs-3dep"?a.Workflow=="usgs-3dep-numeric":s.Manifest.SceneId==scene.Scene)&&
        s.Manifest.Bands.Select(b=>b.Id).Order().SequenceEqual(scene.Bands.Order());
    private ScientificSource? Reusable(ScientificSceneSelection scene,GeographicBounds area,IEnumerable<ScientificSource>? sources=null)=>(sources??science.StreamSources(new(Kind:ScientificCatalog.Provider(scene.Provider).Kind,Area:area,Scene:scene.Provider=="usgs-3dep"?null:scene.Scene))).FirstOrDefault(s=>Compatible(scene,s)&&science.InspectAvailability(s).FilesAvailable&&ScientificSourceSelection.CoversArea(s,area));
    private bool Verify(ScientificSource source)
    {
        foreach(var file in source.Files)
        {var path=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.RelativePath);using var stream=File.OpenRead(path);if(stream.Length!=file.Bytes||Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=file.Sha256)return false;}return true;
    }
    public async Task<LibraryDownloadJob> Preview(ScientificLibrarySelection selection,CancellationToken token=default)
    {
        EnsureRecovered();using var sourceRead=science.BeginAnalysis();
        var cells=Expand(selection);var p=settings.Get().Preferences;
        if(cells.Length==0)throw new ArgumentException("No supported cells intersect this polygon.");
        if(selection.Scenes.Select(s=>s.Provider+"/"+s.Scene).Distinct().Count()!=selection.Scenes.Length)throw new ArgumentException("Select each exact scene only once.");
        var limit=selection.MaximumTransferBytes??Math.Min(p.PreflightTransferBytes??1073741824,1073741824);
        if(limit<1048576||limit> (p.PreflightTransferBytes??21474836480)||limit>21474836480)throw new ArgumentException("Choose a positive Library job budget within the configured preflight allowance (maximum 20 GiB).");
        var products=new List<ScientificCatalogItem>();var work=new List<LibraryWorkUnit>();long reuse=0;var reusedIds=new HashSet<Guid>();var covered=new HashSet<GeographicBounds>();var checkedSources=new HashSet<Guid>();var reuseContext=new ScientificReuseContext();var reuseNotes=new HashSet<string>();
        bool Verified(ScientificSource source)=>!checkedSources.Add(source.Id)||Verify(source);
        foreach(var selected in selection.Scenes)
        {
            var recipe=ScientificCatalog.Provider(selected.Provider);
            var partitions=new Dictionary<GeographicBounds,ScientificReusePlan>();
            foreach(var cell in cells){var single=Reusable(selected,cell);partitions[cell]=single is not null?new([new(cell,single)],[],[],[]):ScientificSourceSelection.ReuseArea(science,cell,s=>Compatible(selected,s),Verified,token,reuseContext);}
            foreach(var note in partitions.Values.SelectMany(r=>r.Limitations))reuseNotes.Add(note);
            ScientificCatalogItem item;
            if(partitions.Values.All(r=>r.Missing.Length==0))
            {
                var source=partitions.Values.First().Reused[0].Source;var m=source.Manifest;var provenance=m.Acquisition!;
                item=new(selected.Provider,recipe.Product,selected.Scene,selected.Revision,m.Kind,cells[0],null,m.ObservationUtc,m.PublicationUtc,
                    m.Bands.Select(b=>new ScientificCatalogBand(b.Id,b.Unit,b.Scale,b.Offset,b.NoData,null,b.Format,null)).ToArray(),source.Grids.Values.First().Crs,m.VerticalDatum,provenance.Quality,provenance.NativeResolution,m.License,m.Citation,"Verified retained reuse; no provider query",["Retained local evidence, not newly verified provider availability."]);
            }
            else item=(await catalog.Resolve(selected,cells[0],new(Math.Min(p.MaximumDownloadBytes,8388608)),token,requireCoverage:false)).Item;
            products.Add(item);
            foreach(var cell in cells)
            {
                if(item.Geometry is {} geometry&&!cell.Corners.Append(cell.Center).All(point=>StacScientificProvider.Contains(geometry,point)))continue;
                if(selected.Provider=="usgs-3dep"&&(cell.West< -170||cell.East> -64||cell.South<17||cell.North>72))continue;
                if(work.Count>=4096)throw new ArgumentException("Exact scenes × intersecting geographic cells exceed 4,096 work units. Narrow the region or scene selection.");
                var partition=partitions[cell];
                if(partition.Uncertain)throw new InvalidOperationException("Retained partial coverage could not be scientifically verified within this bounded review. Choose smaller windows or inspect the source; uncertain coverage was not labeled missing or downloaded again.");
                if(partition.QualityLimited.Length>0)throw new InvalidOperationException("Some retained pixels are nodata or quality-excluded. Re-downloading the same source is not a remedy; choose another scientifically suitable source or smaller area.");
                foreach(var piece in partition.Reused){if(!Verified(piece.Source))throw new IOException("An exact retained source changed; restore/reimport it. No original was replaced.");if(reusedIds.Add(piece.Source.Id))reuse+=piece.Source.Files.Sum(f=>f.Bytes);work.Add(new(work.Count,selected,piece.Area,piece.Source.Id,AutomaticScientificData.StorageBound(recipe.Kind)));}
                foreach(var gap in partition.Missing)work.Add(new(work.Count,selected,gap,null,AutomaticScientificData.StorageBound(recipe.Kind)));
                if(work.Count>4096)throw new ArgumentException("The reviewed reuse/gap partition exceeds 4,096 work units. Choose smaller areas; this is not a Library capacity limit.");
                covered.Add(cell);
            }
        }
        if(work.Count==0)throw new ArgumentException("None of these bounded cells fit the selected scene footprints. Select cells wholly inside a scene; partial edge cells are not claimed as supported.");
        var storage=science.Management?.Snapshot();
        var uncovered=cells.Length-covered.Count;var temp=work.All(w=>w.ReuseSourceId is not null)?1048576:work.Max(w=>w.StorageAllowanceBytes)+p.MaximumDownloadBytes;
        using(var reservation=science.Management?.ReserveScience(temp)){}
        var canonical=selection with{Areas=selection.Areas.OrderBy(a=>a.West).ThenBy(a=>a.South).ToArray(),Scenes=selection.Scenes.OrderBy(s=>s.Provider).ThenBy(s=>s.Scene).Select(s=>s with{Bands=s.Bands.Order().ToArray()}).ToArray(),MaximumTransferBytes=limit};
        var now=DateTimeOffset.UtcNow;var hash=ScientificCatalog.Hash(new{canonical,work,settings=SettingsHash(),limit,temp});
        var preview=new LibraryDownloadPreview(1,Guid.NewGuid(),canonical,hash,SettingsHash(),now,now.AddMinutes(30),work.ToArray(),products.ToArray(),limit,p.MaximumDownloadBytes,temp,reuse,
            work.All(w=>w.ReuseSourceId is not null)?0:null,null,
            ["Download size and final retained size are Unknown until bounded numeric reads complete. Allowances are limits, not estimates.","Polygon edges may include adjacent numeric pixels within the displayed rectangular work windows; no entire original scene is requested.","Historical datasets are retained scientific originals, not airborne observations or automatically disposable caches.","Retries and interrupted-read allowances count against the approved total. Background work yields to active flight acquisition.","Mandatory numeric/QA band recipes cannot be reduced without a compatible reader; catalog footprints do not guarantee valid pixels.","Retained originals are counted once even when several approved windows reuse them. Gap windows use the provider's existing bounded numeric subsetting, not full-scene claims. Native resolution and per-observation source provenance remain separate.",..reuseNotes],uncovered,storage?.Science.FreeBytes,storage?.Science.LimitBytes,storage?.Science.UsedBytes,automatic.QueueSummary().PreflightAvailableBytes);
        return science.SaveLibraryPreview(preview);
    }
    public async Task<LibraryDownloadJob> Approve(Guid id,string hash,CancellationToken token=default)
    {
        await commands.WaitAsync(token);try
        {
            EnsureRecovered();using var sourceRead=science.BeginAnalysis();var job=science.LibraryJob(id);
            if(job.State!="Awaiting Approval"||job.Preview.SelectionHash!=hash||job.Preview.ExpiresUtc<DateTimeOffset.UtcNow||job.Preview.SettingsHash!=SettingsHash(job.Preview.Nasa is not null)||job.Preview.UncoveredCells>0)throw new InvalidOperationException("Preview expired, changed, incomplete or already approved. Resolve uncovered cells and create a fresh preview.");
            return science.SetLibraryState(id,"Queued",approve:true);
        }finally{commands.Release();}
    }
    public async Task<LibraryDownloadJob> Control(Guid id,string action,CancellationToken token=default)
    {
        await commands.WaitAsync(token);try
        {
            var job=science.LibraryJob(id);if(job.State=="Completed")throw new InvalidOperationException("Completed work does not restart; verified sources remain retained.");
            if(action is not("pause" or "resume" or "cancel" or "retry"))throw new ArgumentException("Choose pause, resume, cancel or retry.");
            if(action is "resume" or "retry")
            {
                if(job.ApprovalId is null||job.State=="Cancelled"||job.Preview.SettingsHash!=SettingsHash(job.Preview.Nasa is not null))throw new InvalidOperationException("This work needs a new preview and approval.");
                await automatic.WaitForLibraryScope("library:"+id,token);job=science.LibraryJob(id);
                foreach(var item in job.Items.Where(i=>i.State!="Completed"))science.SetLibraryItem(id,item.Index,"Queued");
                automatic.PauseScope("library:"+id,false);return science.SetLibraryState(id,"Queued");
            }
            var result=science.SetLibraryState(id,action=="pause"?"Paused":"Cancelled");automatic.CancelScope("library:"+id);await automatic.WaitForLibraryScope("library:"+id,token);return science.LibraryJob(result.Id);
        }finally{commands.Release();}
    }
    private async Task Run()
    {
        while(!stopping.IsCancellationRequested)
        {
            try
            {
                if(science.AvailabilityError() is {} unavailable){Warning=unavailable;lock(recoveryGate)initialized=null;await Task.Delay(500,stopping.Token);continue;}
                EnsureRecovered();Warning=null;
                var job=science.NextQueuedLibraryJob();
                if(job is not null&&automatic.LibraryAllowed)
                {
                    var item=job.Items.FirstOrDefault(i=>i.State!="Completed");
                    if(item is null){science.SetLibraryState(job.Id,"Completed",runningOnly:true);continue;}
                    var work=job.Preview.Work[item.Index];var key=job.Id+"/"+item.Index;
                    await automatic.ScheduleLibrary(key,ScientificCatalog.Provider(work.Scene.Provider).Kind,work.Area,token=>Execute(job,work,item,token));
                }
            }
            catch(OperationCanceledException)when(stopping.IsCancellationRequested){break;}
            catch(OperationCanceledException){} // paused/preempted work remains explicit and recoverable
            catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){Warning=e.Message;}
            try{await Task.Delay(250,stopping.Token);}catch(OperationCanceledException){break;}
        }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,ScientificTransferProgress> progress=[];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,(double Rate,DateTimeOffset At)> recentRates=[];
    public LibraryDownloadJob Project(LibraryDownloadJob job)
    {
        if(progress.TryGetValue(job.Id,out var measured)){
            var result=measured.Snapshot(job.State);
            if(job.State=="Completed"&&measured.LastReliableRate is {} rate)recentRates[job.Preview.Products[0].Provider]=(rate,measured.LastMeasuredAt);
            return job with{Progress=result};
        }
        if(job.State=="Awaiting Approval"&&job.Preview.EstimatedTransferBytes is {} known){
            var history=recentRates.GetValueOrDefault(job.Preview.Products[0].Provider);var recent=history.Rate>0&&DateTimeOffset.UtcNow-history.At<TimeSpan.FromMinutes(30);
            return job with{Progress=new("Preview",known,0,0,recent?history.Rate:null,recent?known/history.Rate*.75:null,recent?known/history.Rate*1.5:null,DateTimeOffset.UtcNow,recent?"Approximate download duration from a comparable same-provider transfer measured in this process within 30 minutes. Excludes verification/import and future queue constraints.":"Time estimate available after download starts. No assumed internet speed.",TotalFiles:job.Preview.Nasa?.Files.Length??0)};
        }
        return job;
    }
    private async Task Execute(LibraryDownloadJob job,LibraryWorkUnit work,LibraryDownloadItem previous,CancellationToken token)
    {
        if(job.Preview.Nasa is not null)activeNasa.TryAdd(job.Id,0);
        ScientificDownloadBudget? budget=null;ScientificTransferLedger.Lease? transfer=null;var initial=previous.DownloadedBytes;
        if(progress.Count>128)foreach(var id in progress.Keys.Where(id=>id!=job.Id).Take(64))progress.TryRemove(id,out _);
        var measured=new ScientificTransferProgress();progress[job.Id]=measured;measured.Begin(job.Preview.EstimatedTransferBytes,job.Preview.EstimatedTransferBytes==0?0:job.Preview.Nasa?.Files.Length??0);
        try
        {
            using var operation=science.BeginAnalysis();
            var current=science.LibraryJob(job.Id);if(current.State!="Queued"||current.ApprovalId is null)return;
            if(current.Preview.SettingsHash!=SettingsHash(current.Preview.Nasa is not null))throw new InvalidOperationException("Scientific access limits/consent changed; create a new preview before further downloading.");
            if(science.SetLibraryState(job.Id,"Preparing",runningOnly:true).State!="Preparing")return;
            science.SetLibraryItem(job.Id,work.Index,"Preparing");
            var source=job.Preview.Nasa is {} bundle?await ReusableNasa(bundle,token):work.ReuseSourceId is {} reviewedId?science.Source(reviewedId):Reusable(work.Scene,work.Area);
            if(work.ReuseSourceId is not null&&(source is null||job.Preview.Nasa is null&&(!Compatible(work.Scene,source)||!ScientificSourceSelection.CoversArea(source,work.Area))))throw new InvalidOperationException("The reviewed retained source is no longer available/compatible. Create a fresh preview; no unapproved replacement was downloaded.");
            if(source is not null){if(!Verify(source))throw new IOException("Retained source failed verification; originals were preserved.");}
            else
            {
                var p=settings.Get().Preferences;
                if(!p.OnlineEnabled||!p.PrivacyAccepted||p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new InvalidOperationException("Scientific retrieval is offline or waiting for an unmetered connection. Resume after resolving it.");
                transfer=automatic.AcquireLibraryTransfer();
                budget=new(job.Preview.PerItemTransferLimit,bytes=>{measured.Network(bytes);transfer.Charge(bytes);science.LibraryBudget(job.Id,work.Index,initial+bytes,initial+bytes);},total=>{transfer.EnsureTotal(total);science.LibraryBudget(job.Id,work.Index,initial+(budget?.Bytes??0),initial+total);});
                if(job.Preview.Nasa is {} nasa)source=await AcquireNasa(job,nasa,budget,token);
                else
                {
                    var exact=await catalog.Resolve(work.Scene,work.Area,budget,token);
                    science.SetLibraryState(job.Id,"Downloading",runningOnly:true);science.SetLibraryItem(job.Id,work.Index,"Downloading");measured.Stage("Downloading");
                    source=await automatic.PrepareSource(exact.Discovery,budget,token,stage=>{measured.Stage(stage=="Saving"?"Importing":stage=="Validating"?"Verifying":"Downloading");science.SetLibraryItem(job.Id,work.Index,stage is "Validating" or "Saving"?"Verifying":"Downloading");},"Downloads/Library/"+job.Id.ToString("N")+"/"+work.Index);
                }
                if(source is null)throw new IOException("Provider export contained no supported numeric coverage. No source was published as usable.");
            }
            token.ThrowIfCancellationRequested();science.SetLibraryItem(job.Id,work.Index,"Completed",source:source.Id,verifiedBytes:source.Files.Sum(f=>f.Bytes));
            current=science.LibraryJob(job.Id);science.SetLibraryState(job.Id,current.Items.All(i=>i.State=="Completed")?"Completed":"Queued",runningOnly:true);if(current.Items.All(i=>i.State=="Completed")&&measured.LastReliableRate is {} rate)recentRates[job.Preview.Products[0].Provider]=(rate,measured.LastMeasuredAt);
        }
        catch(ScientificAllowanceWaitException error)
        {science.SetLibraryItem(job.Id,work.Index,"Interrupted",error.Message);science.SetLibraryState(job.Id,"Interrupted",error.Message,runningOnly:true);}
        catch(OperationCanceledException)
        {
            science.SetLibraryItem(job.Id,work.Index,"Interrupted",error:"Paused, preempted or interrupted; verified originals and safe partial ranges are preserved.");
            science.SetLibraryState(job.Id,"Interrupted","Resume explicitly; active flight acquisition has priority.",runningOnly:true);
        }
        catch(Exception e)when(e is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            var error=e is HttpRequestException?"Provider network unavailable. Retry when connectivity returns.":e is System.Text.Json.JsonException?"Provider metadata is incompatible; no scientific values were guessed.":e.Message;
            science.SetLibraryItem(job.Id,work.Index,"Failed",error);science.SetLibraryState(job.Id,"Failed",error,runningOnly:true);
        }
        finally{activeNasa.TryRemove(job.Id,out _);transfer?.Dispose();if(budget is not null)science.LibraryBudget(job.Id,work.Index,initial+budget.Bytes,initial+budget.Bytes,finish:true);science.Management?.RefreshScience();}
    }
    public void Dispose(){stopping.Cancel();foreach(var job in science.AvailabilityError() is null?science.LibraryJobs():[])automatic.CancelScope("library:"+job.Id);try{worker.GetAwaiter().GetResult();}catch(OperationCanceledException){}stopping.Dispose();commands.Dispose();}
}
