using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record PlanDataPreparation(int AreasQueued,bool LimitedCoverage,string Message,ScientificDataReadiness[] Readiness);
public sealed record ScientificPlanReadiness(Guid PlanId,string PlanName,int AreasQueued,bool LimitedCoverage,string Message,
    string Status="Preparing",int Ready=0,int Unavailable=0,int Awaiting=0,long DownloadedBytes=0,long? AdditionalTransferCeiling=null,string Scope="entire",int SelectedUnits=0,int SelectedReady=0,int SelectedAwaiting=0,int SelectedUnavailable=0,bool PlanStale=false);

public static class ScientificPlanPreparation
{
    public static IEnumerable<GeographicBounds> Cells(MissionPlan plan,double step=.02)=>ScientificGeographicWork.Cells((plan.Waypoints??[]).Select(w=>new Coordinates(w.Latitude,w.Longitude)).ToArray(),(plan.SurveyAreas??[]).Select(s=>s.Vertices).ToArray(),step);
    public static RemoteKind[] Kinds(MissionPlan plan)=>(plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).Distinct().ToArray();
    public static PlanDataPreparation Prepare(AutomaticScientificData automatic,MissionPlan plan,bool retry=false)
    {
        var kinds=Kinds(plan);var cells=Cells(plan).Take(8).ToArray();foreach(var cell in cells)automatic.Prepare(cell,kinds,retry,background:!retry,scope:"plan:"+plan.Id);
        return new(cells.Length,true,"First bounded window queued; progressive preflight continues in the background, subject to consent and resource budgets.",automatic.Snapshot());
    }
}

public sealed record PreparationInstrument(string InstrumentId,string Name,string? Profile,string Status,int Total,int Ready,int Awaiting,int Unavailable,long DownloadedBytes,long ReusedBytes,string Eta,string[] Reasons,bool ActivelyDownloading=false);
public sealed record PreparationRegion(string Key,RemoteKind Kind,GeographicBounds Area,string Status="Queued",string Message="Waiting for preparation",long DownloadedBytes=0,long ReusedBytes=0,string Stage="Queued",DateTimeOffset? UpdatedUtc=null,long PreviousAttemptBytes=0,bool Selected=true,Guid? SourceId=null);
public sealed record PlanInstrumentInventory(Guid PlanId,DateTimeOffset? PlanRevision,PreparationInstrument[] Instruments);
public sealed record PreparationActivity(ScientificPlanReadiness[] Plans,PlanInstrumentInventory[] Inventories);
public sealed record MissionStartReadiness(Guid PlanId,string PlanRevision,DateTimeOffset? PlanUpdatedUtc,DateTimeOffset CheckedUtc,
    string Status,string Message,ScientificPlanReadiness? Preparation=null,PreparationInstrument[]? Instruments=null);
public sealed class ScientificPlanData
{
    private sealed record SavedSession(MissionPlan Plan,bool Paused,bool Cancelled,bool Explicit,PreparationRegion[] Regions,PreflightScope? Scope=null,bool AfterFlight=false,bool InFlight=false,bool PlanStale=false,int Version=1);
    private sealed class Session(SavedSession saved)
    {
        public MissionPlan Plan=saved.Plan;public PreflightScope Scope=saved.Scope??new();
        public bool Paused=saved.Paused,Cancelled=saved.Cancelled,Explicit=saved.Explicit,Preempted,AfterFlight=saved.AfterFlight||saved.InFlight,InFlight;
        public Coordinates? Aircraft;public string? FlightHold;public bool PlanStale=saved.PlanStale;
        public readonly Dictionary<string,PreparationRegion> Regions=saved.Regions.ToDictionary(r=>r.Key);
    }
    public string? Warning {get;private set;}
    private readonly AutomaticScientificData automatic;private readonly ScientificAccessSettings settings;
    private readonly Dictionary<Guid,Session> sessions=[];private readonly object gate=new();private long configurationRevision;private bool wasFlight;
    private string Path => System.IO.Path.Combine(settings.ConfigurationDirectory,"preparation-sessions.json");
    public ScientificPlanData(AutomaticScientificData automatic,ScientificAccessSettings settings,MissionStore? missions=null)
    {
        this.automatic=automatic;this.settings=settings;configurationRevision=automatic.ConfigurationRevision;
        if(File.Exists(Path))try{
            var saved=System.Text.Json.JsonSerializer.Deserialize<SavedSession[]>(File.ReadAllText(Path),WireJson.Options)??[];
            foreach(var item in saved.Take(16)){
                if(item.Version!=1)throw new IOException("Unsupported preparation-session format; review with a compatible version.");
                var restored=item with{Regions=item.Regions.Select(r=>r with{Status="Queued",Message="Rechecking retained data after restart"}).ToArray()};
                sessions[item.Plan.Id]=new(restored);if(missions is not null&&missions.Plan(item.Plan.Id) is {} current&&ScientificPreparationScope.Revision(current)!=ScientificPreparationScope.Revision(item.Plan))sessions[item.Plan.Id].PlanStale=true;automatic.PauseScope("plan:"+item.Plan.Id,item.Paused||item.Cancelled||item.AfterFlight||item.InFlight||sessions[item.Plan.Id].PlanStale);
            }
        }catch(Exception error)when(error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {Warning="Saved preparation sessions could not be read. Their file was preserved. Prepare a saved plan again to recover and reverify retained originals. "+error.Message;sessions.Clear();}
    }
    private void Save()=>PreferenceStore.WriteAtomic(Path,sessions.Values.Select(s=>new SavedSession(s.Plan,s.Paused,s.Cancelled,s.Explicit,s.Regions.Values.ToArray(),s.Scope,s.AfterFlight,s.InFlight,s.PlanStale)).ToArray());
    public PlanDataPreparation Prepare(MissionPlan plan,bool retry=false,PreflightScope? scope=null)
    {
        var selection=ScientificPreparationScope.Preview(plan,scope);
        lock(gate){
            if(Warning is not null){if(File.Exists(Path))File.Copy(Path,Path+".preserved-"+Guid.NewGuid().ToString("N"));Warning=null;}
            sessions.TryGetValue(plan.Id,out var s);
            if(s?.InFlight==true)throw new InvalidOperationException("The active flight uses its frozen plan. Change the next preflight scope after End Flight; in-flight prediction continues independently.");
            if(sessions.Count>=16&&s is null){var retired=sessions.FirstOrDefault(p=>p.Value.Cancelled);if(retired.Value is null)throw new InvalidOperationException("16 preparation sessions are retained. Cancel an old session first.");sessions.Remove(retired.Key);}
            var selected=selection.Areas.ToHashSet();var kinds=ScientificPlanPreparation.Kinds(plan);
            var all=ScientificPlanPreparation.Cells(plan,.04).Distinct().Take(4096).ToArray();
            var rows=all.SelectMany(area=>kinds.Select(kind=>{
                var key=AutomaticScientificData.AreaKey(kind,area);var old=s?.Regions.GetValueOrDefault(key);
                return (old??new PreparationRegion(key,kind,area)) with{Selected=selected.Contains(area)};
            })).ToArray();
            automatic.RetainScopeKeys("plan:"+plan.Id,rows.Where(r=>r.Selected).Select(r=>r.Key).ToHashSet());
            sessions[plan.Id]=s=new(new(plan,false,false,retry,rows,selection.Selection));
            if(retry)foreach(var r in s.Regions.Values.Where(r=>r.Selected&&!Ready(r.Status)).ToArray())s.Regions[r.Key]=r with{Status="Queued"};
            if(retry)automatic.RetryScope("plan:"+plan.Id,rows.Where(r=>r.Selected).Select(r=>r.Key).ToHashSet());Pump(s);Save();return new(selection.Areas.Length,false,$"{selection.Selection.Mode}: {selection.InstrumentUnits} selected instrument-region units of {selection.FullInstrumentUnits} planned. Scope does not reduce grading obligations. Source readiness is separate from valid pixels.",automatic.Snapshot());
        }
    }
    public void PlanChanged(MissionPlan plan){lock(gate){if(!sessions.TryGetValue(plan.Id,out var s)||ScientificPreparationScope.Revision(plan)==ScientificPreparationScope.Revision(s.Plan))return;s.PlanStale=true;if(!s.InFlight)automatic.PauseScope("plan:"+plan.Id,true);Save();}}
    public string[] SourceDeletionDependencies(ScientificSource source){lock(gate)return sessions.Values.Where(s=>!s.Paused&&!s.Cancelled&&!s.AfterFlight&&s.Regions.Values.Any(r=>r.Kind==source.Manifest.Kind&&(r.SourceId==source.Id||r.SourceId is null&&(!Terminal(r.Status)||Ready(r.Status))&&ScientificSourceSelection.CoversArea(source,r.Area)))).Select(s=>"Active preparation for "+s.Plan.Name+" uses or can reuse this source. Pause/cancel its preparation before deletion.").ToArray();}
    public void SourcesRemoved(Guid[] ids){lock(gate){foreach(var s in sessions.Values)foreach(var r in s.Regions.Values.Where(r=>r.SourceId is {} id&&ids.Contains(id)).ToArray())s.Regions[r.Key]=r with{Status="Dataset Required",Message="The owner deleted this external source. Review preparation scope or import compatible data to use it again.",SourceId=null,ReusedBytes=0};Save();}}
    public bool AutomaticFlightAllowed(Guid id){lock(gate)return !sessions.TryGetValue(id,out var s)||!s.Paused&&!s.Cancelled;}
    public void Control(Guid id,string action)
    {
        lock(gate){
            if(!sessions.TryGetValue(id,out var s))throw new ArgumentException("Preparation session no longer exists. Review this saved plan again; originals remain retained.");
            if(action=="pause")s.Paused=true;
            else if(action is "resume" or "continue"){
                if(s.PlanStale&&!s.InFlight)throw new InvalidOperationException("The saved plan changed. Review and approve the current preparation scope instead of resuming an old revision.");
                s.Paused=false;s.Cancelled=false;s.AfterFlight=false;s.Explicit=true;automatic.RetryScope("plan:"+id);
                foreach(var r in s.Regions.Values.Where(r=>r.Status is "Cancelled" or "Offline" or "Superseded").ToArray())s.Regions[r.Key]=r with{Status="Queued"};
            }else if(action=="cancel"){
                s.Cancelled=true;automatic.CancelScope("plan:"+id);foreach(var r in s.Regions.Values.Where(r=>!Terminal(r.Status)).ToArray())s.Regions[r.Key]=r with{Status="Cancelled"};
            }else throw new ArgumentException("Choose pause, resume, continue or cancel.");
            automatic.PauseScope("plan:"+id,s.Paused||s.Cancelled||s.Preempted||s.AfterFlight||s.PlanStale&&!s.InFlight);Save();
        }
    }
    private static bool Ready(string state)=>state is "Ready" or "Dataset Ready";
    private static bool Terminal(string state)=>Ready(state)||state is "Unavailable" or "Limited Coverage" or "Partially usable" or "Dataset Required" or "Dataset Invalid" or "Dataset Outside Coverage" or "Failed" or "Cancelled";
    private void Pump(Session s)
    {
        var scope="plan:"+s.Plan.Id;automatic.PauseScope(scope,s.Paused||s.Cancelled||s.Preempted||s.AfterFlight||s.PlanStale&&!s.InFlight);
        var states=automatic.Snapshot().ToDictionary(r=>r.Key);
        foreach(var region in s.Regions.Values.ToArray())if(states.TryGetValue(region.Key,out var row))
            s.Regions[region.Key]=region with{Status=row.Status,Message=row.Message,DownloadedBytes=row.Scope==scope?row.DownloadedBytes:0,PreviousAttemptBytes=region.PreviousAttemptBytes+(row.Scope==scope&&row.DownloadedBytes<region.DownloadedBytes?region.DownloadedBytes:0),ReusedBytes=row.ReusedBytes,Stage=row.Stage,UpdatedUtc=row.UpdatedUtc,SourceId=row.SourceId};
        if(s.Paused||s.Cancelled||s.Preempted||s.AfterFlight||s.PlanStale&&!s.InFlight||s.InFlight&&!settings.Get().Preferences.BackgroundRetrieval||!s.Explicit&&!settings.Get().Preferences.BackgroundRetrieval)return;
        var candidates=s.Regions.Values.Where(r=>s.InFlight||r.Selected).ToArray();
        var areas=s.Regions.Values.Select(r=>r.Area).Distinct().ToArray();var near=s.Aircraft is {} position?Array.IndexOf(areas,areas.MinBy(a=>Geodesy.Distance(position,a.Center))!):0;
        double Priority(PreparationRegion r){var i=Array.IndexOf(areas,r.Area);return s.InFlight?1000+(i>=near?i-near:10000+near-i):10000+i;}
        automatic.ReprioritizeScope(scope,candidates.ToDictionary(r=>r.Key,Priority));
        if(s.InFlight){automatic.LimitScopeWindow(scope,candidates.Where(r=>!Terminal(r.Status)).OrderBy(Priority).Take(24).Select(r=>r.Key).ToHashSet());states=automatic.Snapshot().ToDictionary(r=>r.Key);}
        var active=candidates.Count(r=>states.TryGetValue(r.Key,out var row)&&row.Status is "Queued" or "Downloading" or "Waiting for download allowance" or "Waiting for Flight Priority");
        foreach(var region in candidates.Where(r=>!Terminal(r.Status)).OrderBy(Priority)){
            if(active>=24)break;
            if(states.TryGetValue(region.Key,out var row)&&row.Status is not("Offline" or "Superseded"))continue;
            automatic.Prepare(region.Area,[region.Kind],background:s.InFlight||!s.Explicit,priority:Priority(region),scope:scope);active++;
        }
    }
    public void Tick(bool flightActive=false,Guid? activePlanId=null,TelemetryFrame? frame=null,string? flightHold=null,MissionPlan? frozenPlan=null)
    {
        automatic.FlightActive(flightActive);automatic.FlightResourceHold(flightHold);
        lock(gate){
            if(configurationRevision!=automatic.ConfigurationRevision){configurationRevision=automatic.ConfigurationRevision;foreach(var session in sessions.Values.Where(s=>!s.Cancelled))foreach(var row in session.Regions.Values.ToArray())session.Regions[row.Key]=row with{Status="Queued",Message="Rechecking retained data after scientific sources/preferences changed"};}
            foreach(var s in sessions.Values){
                if(wasFlight&&!flightActive&&(s.InFlight||s.Preempted)){s.AfterFlight=true;automatic.PauseScope("plan:"+s.Plan.Id,true);}
                s.InFlight=flightActive&&s.Plan.Id==activePlanId&&(frozenPlan is null||ScientificPreparationScope.Revision(s.Plan)==ScientificPreparationScope.Revision(frozenPlan));
                if(s.InFlight)s.AfterFlight=false;
                s.Preempted=flightActive&&(!s.InFlight||flightHold is not null);
                s.FlightHold=flightHold;s.Aircraft=frame is {IsValid:true,IsPaused:false}?new(frame.Latitude,frame.Longitude):null;Pump(s);
            }
            wasFlight=flightActive;if(sessions.Count>0)Save();
        }
    }
    public PreparationRegion[] Regions(Guid id,int offset=0){lock(gate)return sessions.TryGetValue(id,out var s)?s.Regions.Values.Skip(Math.Max(0,offset)).Take(100).ToArray():[];}
    public PreparationInstrument[] Instruments(Guid id)
    {
        lock(gate){if(!sessions.TryGetValue(id,out var s))return [];
            return (s.Plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>{
                var rows=s.Regions.Values.Where(r=>r.Kind==RemoteAcquisition.Instruments[c.InstrumentId]).ToArray();var ready=automatic.AvailabilityError is null?rows.Count(r=>Ready(r.Status)):0;var unavailable=automatic.AvailabilityError is null?rows.Count(r=>Terminal(r.Status)&&!Ready(r.Status)):rows.Length;
                var status=automatic.AvailabilityError is not null?"Storage unavailable":s.Cancelled?"Cancelled":s.Paused?"Paused by User":s.PlanStale&&!s.InFlight?"Plan revision changed — review scope":s.AfterFlight?"Awaiting future-flight approval":s.Preempted?"Waiting for Flight Priority":ready==rows.Length&&rows.Length>0?"Ready":ready>0?"Partially Ready":rows.Any(r=>r.Status=="Downloading"&&r.Stage is "Validating" or "Saving")?"Validating / Processing":rows.Any(r=>r.Status=="Downloading"&&r.Stage=="Downloading"&&r.DownloadedBytes>0)?"Downloading":rows.Any(r=>r.Status=="Downloading")?"Finding Source":rows.Any(r=>r.Status=="Offline")?"Waiting for Network":rows.Length==unavailable?"Unavailable":"Queued";
                var transferring=!s.Paused&&!s.Cancelled&&!s.Preempted&&!s.AfterFlight&&rows.Any(r=>r.Status=="Downloading"&&r.Stage=="Downloading"&&r.DownloadedBytes>0&&DateTimeOffset.UtcNow-r.UpdatedUtc<TimeSpan.FromSeconds(6));
                return new PreparationInstrument(c.InstrumentId,ScienceCatalog.Definitions.Single(d=>d.Id==c.InstrumentId).Name,c.Payload?.Profile.Name,status,rows.Length,ready,rows.Length-ready-unavailable,unavailable,rows.Sum(r=>r.DownloadedBytes+r.PreviousAttemptBytes),rows.Sum(r=>r.ReusedBytes),"Time remaining unknown",rows.Where(r=>!Ready(r.Status)).Select(r=>r.Message).Distinct().Take(3).ToArray(),transferring);
            }).ToArray();
        }
    }
    public PreparationActivity Activity(){lock(gate)return new(Snapshot(),sessions.Values.Select(s=>new PlanInstrumentInventory(s.Plan.Id,s.Plan.UpdatedUtc,Instruments(s.Plan.Id))).ToArray());}
    // Read-only full-plan projection. Never schedule preparation or derive denominators from recent jobs.
    public MissionStartReadiness StartReadiness(MissionPlan plan)
    {
        var revision=ScientificPreparationScope.Revision(plan);var now=DateTimeOffset.UtcNow;
        MissionStartReadiness Unknown(string status,string message)=>new(plan.Id,revision,plan.UpdatedUtc,now,status,message);
        lock(gate){
            if(ScientificPlanPreparation.Kinds(plan).Length==0)return Unknown("NotRequired","No external-data instruments are planned. Normal science-session and recording requirements still apply.");
            if(Warning is not null)return Unknown("Unavailable",Warning);
            if(automatic.AvailabilityError is {} error)return Unknown("Unavailable",error);
            if(!sessions.TryGetValue(plan.Id,out var session))return Unknown("NotAssessed","No scientific preparation assessment exists for this plan. Ready and outstanding counts are Unknown.");
            if(session.PlanStale||ScientificPreparationScope.Revision(session.Plan)!=revision)return Unknown("Stale","Preparation belongs to an older plan revision. Its counts are not current; review preparation for this saved plan.");
            var projection=Snapshot().Single(p=>p.PlanId==plan.Id);var instruments=Instruments(plan.Id);
            return new(plan.Id,revision,plan.UpdatedUtc,now,projection.Awaiting==0&&projection.Unavailable==0&&instruments.Sum(i=>i.Total)>0?"Ready":"Incomplete",
                "Current complete planned instrument-region inventory. Prepared source support does not guarantee valid measurements at every pixel.",projection,instruments);
        }
    }
    public ScientificPlanReadiness[] Snapshot()
    {
        lock(gate)return sessions.Values.Select(s=>{
            var instruments=Instruments(s.Plan.Id);var ready=instruments.Sum(i=>i.Ready);var awaiting=instruments.Sum(i=>i.Awaiting);var unavailable=instruments.Sum(i=>i.Unavailable);var chosen=s.Regions.Values.Where(r=>r.Selected).ToArray();var chosenReady=chosen.Count(r=>Ready(r.Status));var chosenUnavailable=chosen.Count(r=>Terminal(r.Status)&&!Ready(r.Status));var chosenAwaiting=chosen.Length-chosenReady-chosenUnavailable;
            return new ScientificPlanReadiness(s.Plan.Id,s.Plan.Name,s.Regions.Values.Select(r=>r.Area).Distinct().Count(),unavailable>0||s.Regions.Count>=4096,(s.PlanStale?"Plan changed: this inventory belongs to the previously approved revision; review scope again. ":"")+"Complete logical instrument × region inventory, separate from the recent queue window. Scope: "+s.Scope.Mode+". "+s.Regions.Values.Count(r=>r.Selected)+" selected preflight units. Incomplete external data does not prevent departure. During flight upcoming data is attempted under consent, budgets and flight priority; late data never creates past observations.",s.Cancelled?"Cancelled":s.Paused?"Paused":s.PlanStale&&!s.InFlight?"Plan revision changed — review scope":s.AfterFlight?"Awaiting future-flight approval":s.Preempted?"Waiting for Flight Priority":!s.InFlight&&chosen.Length<s.Regions.Count&&chosenAwaiting==0?(chosenUnavailable>0?"Selected preflight scope limited/unavailable":"Selected preflight scope ready"):awaiting==0?(s.Regions.Count==0?"No supported remote-data regions":unavailable>0?"Partially Ready":"Ready"):"Preparing",ready,unavailable,awaiting,instruments.Sum(i=>i.DownloadedBytes),Scope:s.Scope.Mode,SelectedUnits:s.Regions.Values.Count(r=>r.Selected),SelectedReady:chosenReady,SelectedAwaiting:chosenAwaiting,SelectedUnavailable:chosenUnavailable,PlanStale:s.PlanStale&&!s.InFlight);
        }).ToArray();
    }
}

public sealed class ScientificFlightPrediction(AutomaticScientificData automatic,ScientificAccessSettings settings)
{
    private readonly ScientificMotionPredictor predictor=new();private ScientificPredictionResult? last;private RemoteKind[] kinds=[];private readonly object gate=new();
    public void Reset(){lock(gate){predictor.Reset();last=null;kinds=[];automatic.SetTrajectoryRegionHint(false);}automatic.ReprioritizeFlight(new Dictionary<string,double>());}
    public void Observe(TelemetryFrame frame,RemoteKind[] enabled,MissionPlan? plan)
    {
        lock(gate)
        {
            if(enabled.Length==0){predictor.Reset();last=null;kinds=[];automatic.SetTrajectoryRegionHint(false);automatic.ReprioritizeFlight(new Dictionary<string,double>());return;}
            kinds=enabled;last=predictor.Predict(frame,DateTimeOffset.UtcNow,enabled,settings.Get().Preferences,automatic.EstimatedPreparationSeconds,plan);
            var observedKnots=last.LookaheadSeconds>0?last.DistanceMetres/last.LookaheadSeconds/.514444444:0;
            automatic.SetTrajectoryRegionHint(last.Status=="Predicting"&&observedKnots>=160&&Math.Abs(last.TurnDegreesPerSecond)<.5);
            if(last.Status=="Waiting")return;
            var priorities=last.Areas.SelectMany(a=>enabled.Select(k=>(Key:AutomaticScientificData.AreaKey(k,a.Area),Priority:a.ArrivalSeconds-automatic.EstimatedPreparationSeconds))).ToDictionary(x=>x.Key,x=>x.Priority);
            automatic.ReprioritizeFlight(priorities);
            foreach(var a in last.Areas)automatic.Prepare(a.Area,enabled,background:true,priority:a.ArrivalSeconds-automatic.EstimatedPreparationSeconds,scope:"flight",samplePoint:a.SamplePoint);
        }
    }
    public ScientificPredictionSnapshot Snapshot()
    {
        lock(gate)
        {
            var states=automatic.Snapshot().ToDictionary(r=>r.Key);var relevant=new List<ScientificDataReadiness>();double? ready=null;var gap=false;
            foreach(var group in (last?.Areas??[]).GroupBy(a=>a.ArrivalSeconds).OrderBy(g=>g.Key))
            {
                var all=true;foreach(var area in group){var rows=kinds.Select(k=>states.GetValueOrDefault(AutomaticScientificData.AreaKey(k,area.Area))).ToArray();relevant.AddRange(rows.OfType<ScientificDataReadiness>());if(rows.Length==0||rows.Any(r=>r?.Status is not("Ready" or "Dataset Ready")))all=false;}
                if(!all)gap=true;if(!gap)ready=group.Key;
            }
            return new(last?.Status??"Waiting",last?.Message??"Enable an instrument and wait for fresh moving aircraft telemetry.",last?.LookaheadSeconds??0,last?.DistanceMetres??0,last?.RouteConsistent??false,last?.Limited??false,ready,relevant.Take(128).ToArray(),automatic.QueueSummary());
        }
    }
}
public sealed class ScientificPreparationWorker(ScientificFlightPrediction prediction,ScientificPlanData plans,MissionCoordinator missions,MissionStore store,ResearchOperations research,ILogger<ScientificPreparationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var state=missions.Snapshot();var plan=state.ActiveMission is {} mission?store.Research(mission.Id).PlanSnapshot:null;
                var memory=GC.GetGCMemoryInfo();var pressure=state.ActiveMission is null?null:plan is not null&&!plans.AutomaticFlightAllowed(plan.Id)?"Paused by User — this mission’s scientific preparation is paused/cancelled.":research.BackgroundDownloadHold??(state.Telemetry is not {IsValid:true,IsPaused:false} fresh||DateTimeOffset.UtcNow-fresh.TimestampUtc>TimeSpan.FromSeconds(6)?"Waiting for fresh active-flight telemetry.":memory.TotalAvailableMemoryBytes>0&&memory.MemoryLoadBytes>memory.TotalAvailableMemoryBytes*.85?"Host memory pressure; background scientific preparation is temporarily throttled.":null);
                plans.Tick(state.ActiveMission is not null,plan?.Id,state.Telemetry,pressure,plan);
                if(state.Connection.State!=ConnectionState.Connected||state.Research?.IntentionallyPaused==true||state.Telemetry is not{IsValid:true,IsPaused:false} frame||DateTimeOffset.UtcNow-frame.TimestampUtc>TimeSpan.FromSeconds(6)){prediction.Reset();continue;}
                var kinds=state.Research?.Instruments.Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).ToArray()??[];
                if(state.ActiveMission is null||pressure is not null||plan is not null&&!plans.AutomaticFlightAllowed(plan.Id)){prediction.Reset();continue;}
                prediction.Observe(frame,kinds,plan);
            }
            catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Scientific preparation unavailable: {Message}. Flight recording continues independently.",error.Message);}
        }
    }
}
