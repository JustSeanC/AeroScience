using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record PlanDataPreparation(int AreasQueued,bool LimitedCoverage,string Message,ScientificDataReadiness[] Readiness);
public sealed record ScientificPlanReadiness(Guid PlanId,string PlanName,int AreasQueued,bool LimitedCoverage,string Message,
    string Status="Preparing",int Ready=0,int Unavailable=0,int Awaiting=0,long DownloadedBytes=0,long? AdditionalTransferCeiling=null);

public static class ScientificPlanPreparation
{
    private static bool InPolygon(Coordinates p,Coordinates[] vertices)
    {
        var inside=false;for(int i=0,j=vertices.Length-1;i<vertices.Length;j=i++)
            if((vertices[i].Latitude>p.Latitude)!=(vertices[j].Latitude>p.Latitude)&&p.Longitude<(vertices[j].Longitude-vertices[i].Longitude)*(p.Latitude-vertices[i].Latitude)/(vertices[j].Latitude-vertices[i].Latitude)+vertices[i].Longitude)inside=!inside;
        return inside;
    }
    // Lazy, geodesic corridor traversal. The session advances bounded windows instead of stopping at eight cells.
    public static IEnumerable<GeographicBounds> Cells(MissionPlan plan,double step=.02)
    {
        var seen=new HashSet<GeographicBounds>();var inspected=0;
        GeographicBounds? Cell(Coordinates p){try{var a=new GeographicBounds(Math.Floor(p.Longitude/step)*step,Math.Floor(p.Latitude/step)*step,Math.Floor(p.Longitude/step)*step+step,Math.Floor(p.Latitude/step)*step+step);AutomaticScientificData.ValidateArea(a);return a;}catch(ArgumentException){return null;}}
        var points=(plan.Waypoints??[]).Select(w=>new Coordinates(w.Latitude,w.Longitude)).ToArray();
        foreach(var (a,b) in points.Zip(points.Skip(1)))
        {
            var distance=Geodesy.Distance(a,b);var bearing=Geodesy.Bearing(a,b);var steps=(int)Math.Min(20000,Math.Ceiling(distance/700));
            for(var i=0;i<=steps;i++)
            {if(++inspected>100000||seen.Count>=4096)yield break;var cell=Cell(Geodesy.Direct(a,bearing,steps==0?0:distance*i/steps));if(cell is not null&&seen.Add(cell))yield return cell;}
        }
        foreach(var p in points){var cell=Cell(p);if(cell is not null&&seen.Add(cell))yield return cell;}
        foreach(var survey in plan.SurveyAreas??[])
        {
            var vertices=survey.Vertices??[];if(vertices.Length<3)continue;
            foreach(var (a,b) in vertices.Zip(vertices.Skip(1).Append(vertices[0])))
            {var length=Geodesy.Distance(a,b);var bearing=Geodesy.Bearing(a,b);var count=(int)Math.Min(20000,Math.Ceiling(length/700));for(var n=0;n<=count;n++){if(++inspected>100000||seen.Count>=4096)yield break;var edgeCell=Cell(Geodesy.Direct(a,bearing,count==0?0:length*n/count));if(edgeCell is not null&&seen.Add(edgeCell))yield return edgeCell;}}
            var south=vertices.Min(p=>p.Latitude);var north=vertices.Max(p=>p.Latitude);var west=vertices.Min(p=>p.Longitude);var east=vertices.Max(p=>p.Longitude);
            for(var lat=Math.Floor(south/step)*step;lat<=north;lat+=step)
            for(var lon=Math.Floor(west/step)*step;lon<=east;lon+=step)
            {if(++inspected>100000||seen.Count>=4096)yield break;var center=new Coordinates(lat+step/2,lon+step/2);var cell=Cell(center);if(cell is not null&&(InPolygon(center,vertices)||cell.Corners.Any(c=>InPolygon(c,vertices))||vertices.Any(cell.Contains))&&seen.Add(cell))yield return cell;}
        }
    }
    public static RemoteKind[] Kinds(MissionPlan plan)=>(plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).Distinct().ToArray();
    public static PlanDataPreparation Prepare(AutomaticScientificData automatic,MissionPlan plan,bool retry=false)
    {
        var kinds=Kinds(plan);var cells=Cells(plan).Take(8).ToArray();foreach(var cell in cells)automatic.Prepare(cell,kinds,retry,background:!retry,scope:"plan:"+plan.Id);
        return new(cells.Length,true,"First bounded window queued; progressive preflight continues in the background, subject to consent and resource budgets.",automatic.Snapshot());
    }
}

public sealed record PreparationInstrument(string InstrumentId,string Name,string? Profile,string Status,int Total,int Ready,int Awaiting,int Unavailable,long DownloadedBytes,long ReusedBytes,string Eta,string[] Reasons);
public sealed record PreparationRegion(string Key,RemoteKind Kind,GeographicBounds Area,string Status="Queued",string Message="Waiting for preparation",long DownloadedBytes=0,long ReusedBytes=0,string Stage="Queued",DateTimeOffset? UpdatedUtc=null,long PreviousAttemptBytes=0);
public sealed class ScientificPlanData
{
    private sealed record SavedSession(MissionPlan Plan,bool Paused,bool Cancelled,bool Explicit,PreparationRegion[] Regions);
    private sealed class Session(SavedSession saved)
    {
        public MissionPlan Plan=saved.Plan;
        public bool Paused=saved.Paused,Cancelled=saved.Cancelled,Explicit=saved.Explicit,Preempted;
        public readonly Dictionary<string,PreparationRegion> Regions=saved.Regions.ToDictionary(r=>r.Key);
    }
    public string? Warning {get;private set;}
    private readonly AutomaticScientificData automatic;private readonly ScientificAccessSettings settings;
    private readonly Dictionary<Guid,Session> sessions=[];private readonly object gate=new();private long configurationRevision;private string Path => System.IO.Path.Combine(settings.ConfigurationDirectory,"preparation-sessions.json");
    public ScientificPlanData(AutomaticScientificData automatic,ScientificAccessSettings settings)
    {
        this.automatic=automatic;this.settings=settings;configurationRevision=automatic.ConfigurationRevision;
        if(File.Exists(Path))try
        {
            var saved=System.Text.Json.JsonSerializer.Deserialize<SavedSession[]>(File.ReadAllText(Path),WireJson.Options)??[];
            foreach(var item in saved.Take(16))
            {
                // Ready originals must be reverified, not trusted from a prior process.
                var restored=item with{Regions=item.Regions.Select(r=>r with{Status="Queued",Message="Rechecking retained data after restart"}).ToArray()};
                sessions[item.Plan.Id]=new(restored);
                automatic.PauseScope("plan:"+item.Plan.Id,item.Paused||item.Cancelled);
            }
        }
        catch(Exception error)when(error is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {Warning="Saved preparation sessions could not be read. Their file was preserved. Prepare a saved plan again to recover its session and reverify retained originals. "+error.Message;sessions.Clear();}
    }
    private void Save()=>PreferenceStore.WriteAtomic(Path,sessions.Values.Select(s=>new SavedSession(s.Plan,s.Paused,s.Cancelled,s.Explicit,s.Regions.Values.ToArray())).ToArray());
    public PlanDataPreparation Prepare(MissionPlan plan,bool retry=false)
    {
        lock(gate)
        {
            if(Warning is not null){if(File.Exists(Path))File.Copy(Path,Path+".preserved-"+Guid.NewGuid().ToString("N"));Warning=null;}
            if(!sessions.TryGetValue(plan.Id,out var s)||s.Plan.UpdatedUtc!=plan.UpdatedUtc||s.Cancelled)
            {
                if(sessions.Count>=16&&!sessions.ContainsKey(plan.Id))
                {
                    var retired=sessions.FirstOrDefault(pair=>pair.Value.Cancelled);
                    if(retired.Value is null)throw new InvalidOperationException("16 preparation sessions are retained. Cancel an old session before preparing another.");
                    sessions.Remove(retired.Key); // Session metadata only; original sources and science are preserved.
                }
                if(s is not null)automatic.CancelScope("plan:"+plan.Id);
                var kinds=ScientificPlanPreparation.Kinds(plan);
                var regions=ScientificPlanPreparation.Cells(plan,.04).SelectMany(area=>kinds.Select(k=>new PreparationRegion(AutomaticScientificData.AreaKey(k,area),k,area))).ToArray();
                sessions[plan.Id]=s=new(new(plan,false,false,retry,regions));
            }
            s.Explicit|=retry;s.Paused=false;s.Cancelled=false;
            if(retry)foreach(var r in s.Regions.Values.Where(r=>r.Status is not("Ready" or "Dataset Ready")).ToArray())s.Regions[r.Key]=r with{Status="Queued"};
            Pump(s);Save();return new(s.Regions.Values.Select(r=>r.Area).Distinct().Count(),false,"Preparation requested explicitly; valid-pixel coverage remains independent of source readiness.",automatic.Snapshot());
        }
    }
    public void Control(Guid id,string action)
    {
        lock(gate)
        {
            if(!sessions.TryGetValue(id,out var s))throw new ArgumentException("This preparation session is no longer active. Restart data preparation for this saved mission; retained originals will be reused.");
            if(action=="pause")s.Paused=true;
            else if(action=="resume"){s.Paused=false;s.Cancelled=false;s.Explicit=true;foreach(var r in s.Regions.Values.Where(r=>r.Status is "Cancelled" or "Offline").ToArray())s.Regions[r.Key]=r with{Status="Queued"};}
            else if(action=="cancel"){s.Cancelled=true;automatic.CancelScope("plan:"+id);foreach(var r in s.Regions.Values.Where(r=>!Terminal(r.Status)).ToArray())s.Regions[r.Key]=r with{Status="Cancelled"};}
            else throw new ArgumentException("Choose pause, resume or cancel.");
            automatic.PauseScope("plan:"+id,s.Paused||s.Cancelled||s.Preempted);Save();
        }
    }
    private static bool Ready(string state)=>state is "Ready" or "Dataset Ready";
    private static bool Terminal(string state)=>Ready(state)||state is "Unavailable" or "Limited Coverage" or "Partially usable" or "Dataset Required" or "Dataset Invalid" or "Dataset Outside Coverage" or "Failed" or "Cancelled";
    private void Pump(Session s)
    {
        var scope="plan:"+s.Plan.Id;automatic.PauseScope(scope,s.Paused||s.Cancelled||s.Preempted);
        var states=automatic.Snapshot().ToDictionary(r=>r.Key);
        foreach(var region in s.Regions.Values.ToArray())
            if(states.TryGetValue(region.Key,out var row))
                s.Regions[region.Key]=region with{Status=row.Status,Message=row.Message,DownloadedBytes=row.Scope==scope?row.DownloadedBytes:0,PreviousAttemptBytes=region.PreviousAttemptBytes+(row.Scope==scope&&row.DownloadedBytes<region.DownloadedBytes?region.DownloadedBytes:0),ReusedBytes=row.ReusedBytes,Stage=row.Stage,UpdatedUtc=row.UpdatedUtc};
        if(s.Paused||s.Cancelled||s.Preempted||!s.Explicit&&!settings.Get().Preferences.BackgroundRetrieval)return;
        var active=s.Regions.Values.Count(r=>states.TryGetValue(r.Key,out var row)&&row.Scope==scope&&row.Status is "Queued" or "Downloading" or "Waiting for download allowance");
        foreach(var region in s.Regions.Values.Where(r=>!Terminal(r.Status)).Take(24).ToArray())
        {
            if(active>=24)break;
            if(states.TryGetValue(region.Key,out var row)&&row.Scope==scope&&row.Status is not("Offline" or "Superseded"))continue;
            automatic.Prepare(region.Area,[region.Kind],background:!s.Explicit,priority:10000,scope:scope);active++;
        }
    }
    public void Tick(bool flightActive=false)
    {
        automatic.FlightActive(flightActive);
        lock(gate)
        {
            if(configurationRevision!=automatic.ConfigurationRevision)
            {
                configurationRevision=automatic.ConfigurationRevision;
                foreach(var session in sessions.Values.Where(s=>!s.Cancelled))
                    foreach(var row in session.Regions.Values.ToArray())session.Regions[row.Key]=row with{Status="Queued",Message="Rechecking retained data under the updated source preferences"};
            }
            foreach(var s in sessions.Values){s.Preempted=flightActive;Pump(s);}if(sessions.Count>0)Save();
        }
    }
    public PreparationRegion[] Regions(Guid id,int offset=0){lock(gate)return sessions.TryGetValue(id,out var s)?s.Regions.Values.Skip(Math.Max(0,offset)).Take(100).ToArray():[];}
    public PreparationInstrument[] Instruments(Guid id)
    {
        lock(gate)
        {
            if(!sessions.TryGetValue(id,out var s))return [];
            return (s.Plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>
            {
                var rows=s.Regions.Values.Where(r=>r.Kind==RemoteAcquisition.Instruments[c.InstrumentId]).ToArray();var ready=automatic.AvailabilityError is null?rows.Count(r=>Ready(r.Status)):0;var unavailable=automatic.AvailabilityError is null?rows.Count(r=>Terminal(r.Status)&&!Ready(r.Status)):rows.Length;
                var status=rows.Length==0?"Unavailable":automatic.AvailabilityError is not null?"Storage unavailable":s.Cancelled?"Cancelled":s.Paused?"Paused":s.Preempted?"Waiting for active flight":ready==rows.Length&&rows.Length>0?s.Regions.Values.Select(r=>r.Area).Distinct().Count()>=4096?"Partially Ready":"Ready":ready>0?"Partially Ready":rows.Any(r=>r.Status=="Downloading"&&r.Stage is "Validating" or "Saving")?"Validating / Processing":rows.Any(r=>r.Status=="Downloading"&&r.Stage=="Downloading"&&r.DownloadedBytes>0&&DateTimeOffset.UtcNow-r.UpdatedUtc<TimeSpan.FromSeconds(6))?"Downloading":rows.Any(r=>r.Status=="Downloading")?"Finding Source":rows.Any(r=>r.Status=="Offline")?"Waiting for Network":unavailable==rows.Length&&rows.Length>0?"Unavailable":"Queued";
                return new PreparationInstrument(c.InstrumentId,ScienceCatalog.Definitions.Single(d=>d.Id==c.InstrumentId).Name,c.Payload?.Profile.Name,status,rows.Length,ready,rows.Length-ready-unavailable,unavailable,rows.Sum(r=>r.DownloadedBytes+r.PreviousAttemptBytes),rows.Sum(r=>r.ReusedBytes),"Time remaining unknown",rows.Where(r=>!Ready(r.Status)).Select(r=>r.Message).Distinct().Take(3).ToArray());
            }).ToArray();
        }
    }
    public ScientificPlanReadiness[] Snapshot()
    {
        lock(gate)return sessions.Values.Select(s=>{var instruments=Instruments(s.Plan.Id);var ready=instruments.Sum(i=>i.Ready);var awaiting=instruments.Sum(i=>i.Awaiting);var unavailable=instruments.Sum(i=>i.Unavailable);return new ScientificPlanReadiness(s.Plan.Id,s.Plan.Name,s.Regions.Values.Select(r=>r.Area).Distinct().Count(),unavailable>0||s.Regions.Values.Select(r=>r.Area).Distinct().Count()>=4096,"Logical instrument × region counts, not provider attempts. At most 4096 distinct regions are reviewed; split larger plans. Ready sources do not establish cloud-free pixel coverage.",s.Cancelled?"Cancelled":s.Paused?"Paused":s.Preempted?"Waiting for active flight":awaiting==0?(s.Regions.Count==0?"No supported remote-data regions":unavailable>0||s.Regions.Values.Select(r=>r.Area).Distinct().Count()>=4096?"Partially Ready":"Ready"):"Preparing",ready,unavailable,awaiting,instruments.Sum(i=>i.DownloadedBytes));}).ToArray();
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
public sealed class ScientificPreparationWorker(ScientificFlightPrediction prediction,ScientificPlanData plans,MissionCoordinator missions,MissionStore store,ILogger<ScientificPreparationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var state=missions.Snapshot();plans.Tick(state.ActiveMission is not null);
                if(state.Connection.State!=ConnectionState.Connected||state.Research?.IntentionallyPaused==true||state.Telemetry is not{IsValid:true,IsPaused:false} frame||DateTimeOffset.UtcNow-frame.TimestampUtc>TimeSpan.FromSeconds(6)){prediction.Reset();continue;}
                var kinds=state.Research?.Instruments.Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).ToArray()??[];
                var plan=state.ActiveMission is {} mission?store.Research(mission.Id).PlanSnapshot:null;
                prediction.Observe(frame,kinds,plan);
            }
            catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Scientific preparation unavailable: {Message}. Flight recording continues independently.",error.Message);}
        }
    }
}
