using System.Security.Cryptography;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Durable orchestration only. Actual work runs in AutomaticScientificData's existing
// priority queue, shares its numeric reader/importer, and yields to active flights.
public sealed class ScientificLibraryJobs:IDisposable
{
    private readonly ScienceStore science;private readonly ScientificCatalog catalog;private readonly AutomaticScientificData automatic;
    private readonly ScientificAccessSettings settings;private readonly CancellationTokenSource stopping=new();private readonly SemaphoreSlim commands=new(1);private readonly Task worker;
    public string? Warning {get;private set;}
    private string? initialized;
    public ScientificLibraryJobs(ScienceStore science,ScientificCatalog catalog,AutomaticScientificData automatic,ScientificAccessSettings settings)
    {this.science=science;this.catalog=catalog;this.automatic=automatic;this.settings=settings;worker=Task.Run(Run);}
    private string SettingsHash()=>ScientificCatalog.Hash(new{settings.Get().Preferences.OnlineEnabled,settings.Get().Preferences.PrivacyAccepted,settings.Get().Preferences.MaximumDownloadBytes,settings.Get().Preferences.PreflightTransferBytes,settings.Get().Preferences.UnmeteredOnly,settings.Get().Preferences.BackgroundRetrieval});
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
                var cell=new GeographicBounds(area.West+x*.02,area.South+y*.02,Math.Min(area.East,area.West+(x+1)*.02),Math.Min(area.North,area.South+(y+1)*.02));cells.Add(cell);
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
    private ScientificSource? Reusable(ScientificSceneSelection scene,GeographicBounds area,ScientificSource[]? sources=null)=>(sources??science.Sources().ToArray()).FirstOrDefault(s=>
        s.Manifest.Acquisition?.CatalogRevision==scene.Revision&&s.Manifest.Acquisition is {} a&&
        (scene.Provider=="usgs-3dep"?a.Workflow=="usgs-3dep-numeric":s.Manifest.SceneId==scene.Scene)&&
        s.Manifest.Bands.Select(b=>b.Id).Order().SequenceEqual(scene.Bands.Order())&&ScientificSourceSelection.CoversArea(s,area));
    private bool Verify(ScientificSource source)
    {
        foreach(var file in source.Files)
        {var path=ScientificRaster.SafeFile(Path.Combine(science.DirectoryPath,source.RelativeDirectory),file.RelativePath);using var stream=File.OpenRead(path);if(stream.Length!=file.Bytes||Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()!=file.Sha256)return false;}return true;
    }
    public async Task<LibraryDownloadJob> Preview(ScientificLibrarySelection selection,CancellationToken token=default)
    {
        var cells=Expand(selection);var p=settings.Get().Preferences;
        if(cells.Length==0)throw new ArgumentException("No supported cells intersect this polygon.");
        if(selection.Scenes.Select(s=>s.Provider+"/"+s.Scene).Distinct().Count()!=selection.Scenes.Length)throw new ArgumentException("Select each exact scene only once.");
        var limit=selection.MaximumTransferBytes??Math.Min(p.PreflightTransferBytes??1073741824,1073741824);
        if(limit<1048576||limit> (p.PreflightTransferBytes??21474836480)||limit>21474836480)throw new ArgumentException("Choose a positive Library job budget within the configured preflight allowance (maximum 20 GiB).");
        var products=new List<ScientificCatalogItem>();var work=new List<LibraryWorkUnit>();long reuse=0;var reusedIds=new HashSet<Guid>();var covered=new HashSet<GeographicBounds>();var retainedSnapshot=science.Sources().ToArray();var checkedSources=new HashSet<Guid>();
        foreach(var selected in selection.Scenes)
        {
            var recipe=ScientificCatalog.Provider(selected.Provider);
            var allLocal=cells.Select(c=>Reusable(selected,c,retainedSnapshot)).ToArray();
            ScientificCatalogItem item;
            if(allLocal.All(s=>s is not null))
            {
                var source=allLocal[0]!;var m=source.Manifest;var provenance=m.Acquisition!;
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
                var retained=Reusable(selected,cell,retainedSnapshot);if(retained is not null&&checkedSources.Add(retained.Id)&&!Verify(retained))throw new IOException("An exact retained source changed; restore/reimport it. No original was replaced.");
                if(retained is not null&&reusedIds.Add(retained.Id))reuse+=retained.Files.Sum(f=>f.Bytes);
                work.Add(new(work.Count,selected,cell,retained?.Id,AutomaticScientificData.StorageBound(recipe.Kind)));covered.Add(cell);
            }
        }
        if(work.Count==0)throw new ArgumentException("None of these bounded cells fit the selected scene footprints. Select cells wholly inside a scene; partial edge cells are not claimed as supported.");
        var storage=science.Management?.Snapshot();
        var uncovered=cells.Length-covered.Count;var temp=work.Max(w=>w.StorageAllowanceBytes)+p.MaximumDownloadBytes;
        using(var reservation=science.Management?.ReserveScience(temp)){}
        var canonical=selection with{Areas=selection.Areas.OrderBy(a=>a.West).ThenBy(a=>a.South).ToArray(),Scenes=selection.Scenes.OrderBy(s=>s.Provider).ThenBy(s=>s.Scene).Select(s=>s with{Bands=s.Bands.Order().ToArray()}).ToArray(),MaximumTransferBytes=limit};
        var now=DateTimeOffset.UtcNow;var hash=ScientificCatalog.Hash(new{canonical,work,settings=SettingsHash(),limit,temp});
        var preview=new LibraryDownloadPreview(1,Guid.NewGuid(),canonical,hash,SettingsHash(),now,now.AddMinutes(30),work.ToArray(),products.ToArray(),limit,p.MaximumDownloadBytes,temp,reuse,
            work.All(w=>w.ReuseSourceId is not null)?0:null,null,
            ["Download size and final retained size are Unknown until bounded numeric reads complete. Allowances are limits, not estimates.","Polygon edges may include adjacent numeric pixels within the displayed rectangular work windows; no entire original scene is requested.","Historical datasets are retained scientific originals, not airborne observations or automatically disposable caches.","Retries and interrupted-read allowances count against the approved total. Background work yields to active flight acquisition.","Mandatory numeric/QA band recipes cannot be reduced without a compatible reader; catalog footprints do not guarantee valid pixels."],uncovered,storage?.Science.FreeBytes,storage?.Science.LimitBytes,storage?.Science.UsedBytes,automatic.QueueSummary().PreflightAvailableBytes);
        return science.SaveLibraryPreview(preview);
    }
    public async Task<LibraryDownloadJob> Approve(Guid id,string hash,CancellationToken token=default)
    {
        await commands.WaitAsync(token);try
        {
            var job=science.LibraryJob(id);
            if(job.State!="Awaiting Approval"||job.Preview.SelectionHash!=hash||job.Preview.ExpiresUtc<DateTimeOffset.UtcNow||job.Preview.SettingsHash!=SettingsHash()||job.Preview.UncoveredCells>0)throw new InvalidOperationException("Preview expired, changed, incomplete or already approved. Resolve uncovered cells and create a fresh preview.");
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
                if(job.ApprovalId is null||job.State=="Cancelled"||job.Preview.SettingsHash!=SettingsHash())throw new InvalidOperationException("This work needs a new preview and approval.");
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
                if(science.AvailabilityError() is {} unavailable){Warning=unavailable;initialized=null;await Task.Delay(500,stopping.Token);continue;}
                if(initialized!=science.DirectoryPath){science.RecoverLibraryJobs();initialized=science.DirectoryPath;}Warning=null;
                var job=science.NextQueuedLibraryJob();
                if(job is not null&&automatic.LibraryAllowed)
                {
                    var item=job.Items.FirstOrDefault(i=>i.State!="Completed");
                    if(item is null){science.SetLibraryState(job.Id,"Completed");continue;}
                    var work=job.Preview.Work[item.Index];var key=job.Id+"/"+item.Index;
                    await automatic.ScheduleLibrary(key,ScientificCatalog.Provider(work.Scene.Provider).Kind,work.Area,token=>Execute(job,work,item,token));
                }
            }
            catch(OperationCanceledException)when(stopping.IsCancellationRequested){break;}
            catch(OperationCanceledException){} // paused/preempted work remains explicit and recoverable
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){Warning=e.Message;}
            try{await Task.Delay(250,stopping.Token);}catch(OperationCanceledException){break;}
        }
    }
    private async Task Execute(LibraryDownloadJob job,LibraryWorkUnit work,LibraryDownloadItem previous,CancellationToken token)
    {
        ScientificDownloadBudget? budget=null;ScientificTransferLedger.Lease? transfer=null;var initial=previous.DownloadedBytes;
        try
        {
            using var operation=science.BeginAnalysis();
            var current=science.LibraryJob(job.Id);if(current.State!="Queued"||current.ApprovalId is null)return;
            if(current.Preview.SettingsHash!=SettingsHash())throw new InvalidOperationException("Scientific access limits/consent changed; create a new preview before further downloading.");
            science.SetLibraryState(job.Id,"Preparing");science.SetLibraryItem(job.Id,work.Index,"Preparing");
            var source=Reusable(work.Scene,work.Area);
            if(source is not null){if(!Verify(source))throw new IOException("Retained source failed verification; originals were preserved.");}
            else
            {
                var p=settings.Get().Preferences;
                if(!p.OnlineEnabled||!p.PrivacyAccepted||p.UnmeteredOnly&&settings.IsUnmetered()!=true)throw new InvalidOperationException("Scientific retrieval is offline or waiting for an unmetered connection. Resume after resolving it.");
                transfer=automatic.AcquireLibraryTransfer();
                budget=new(job.Preview.PerItemTransferLimit,bytes=>{transfer.Charge(bytes);science.LibraryBudget(job.Id,work.Index,initial+bytes,initial+bytes);},total=>{transfer.EnsureTotal(total);science.LibraryBudget(job.Id,work.Index,initial+(budget?.Bytes??0),initial+total);});
                var exact=await catalog.Resolve(work.Scene,work.Area,budget,token);
                science.SetLibraryState(job.Id,"Downloading");science.SetLibraryItem(job.Id,work.Index,"Downloading");
                source=await automatic.PrepareSource(exact.Discovery,budget,token,stage=>science.SetLibraryItem(job.Id,work.Index,stage is "Validating" or "Saving"?"Verifying":"Downloading"),"Downloads/Library/"+job.Id.ToString("N")+"/"+work.Index);
                if(source is null)throw new IOException("Provider export contained no supported numeric coverage. No source was published as usable.");
            }
            token.ThrowIfCancellationRequested();science.SetLibraryItem(job.Id,work.Index,"Completed",source:source.Id,verifiedBytes:source.Files.Sum(f=>f.Bytes));
            current=science.LibraryJob(job.Id);if(current.State is "Preparing" or "Downloading" or "Verifying")science.SetLibraryState(job.Id,current.Items.All(i=>i.State=="Completed")?"Completed":"Queued");
        }
        catch(ScientificAllowanceWaitException error)
        {science.SetLibraryItem(job.Id,work.Index,"Interrupted",error.Message);if(science.LibraryJob(job.Id).State is not("Paused" or "Cancelled"))science.SetLibraryState(job.Id,"Interrupted",error.Message);}
        catch(OperationCanceledException)
        {
            science.SetLibraryItem(job.Id,work.Index,"Interrupted",error:"Paused, preempted or interrupted; verified originals and safe partial ranges are preserved.");
            var current=science.LibraryJob(job.Id);if(current.State is not("Paused" or "Cancelled"))science.SetLibraryState(job.Id,"Interrupted","Resume explicitly; active flight acquisition has priority.");
        }
        catch(Exception e)when(e is IOException or HttpRequestException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            var error=e is HttpRequestException?"Provider network unavailable. Retry when connectivity returns.":e is System.Text.Json.JsonException?"Provider metadata is incompatible; no scientific values were guessed.":e.Message;
            science.SetLibraryItem(job.Id,work.Index,"Failed",error);if(science.LibraryJob(job.Id).State is not("Paused" or "Cancelled"))science.SetLibraryState(job.Id,"Failed",error);
        }
        finally{transfer?.Dispose();if(budget is not null)science.LibraryBudget(job.Id,work.Index,initial+budget.Bytes,initial+budget.Bytes,finish:true);science.Management?.RefreshScience();}
    }
    public void Dispose(){stopping.Cancel();foreach(var job in science.AvailabilityError() is null?science.LibraryJobs():[])automatic.CancelScope("library:"+job.Id);try{worker.GetAwaiter().GetResult();}catch(OperationCanceledException){}stopping.Dispose();commands.Dispose();}
}
