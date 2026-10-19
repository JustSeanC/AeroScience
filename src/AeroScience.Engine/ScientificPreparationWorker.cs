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
    public static IEnumerable<GeographicBounds> Cells(MissionPlan plan)
    {
        var seen=new HashSet<GeographicBounds>();var inspected=0;
        GeographicBounds? Cell(Coordinates p){try{var a=GeographicBounds.Around(p);AutomaticScientificData.ValidateArea(a);return a;}catch(ArgumentException){return null;}}
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
            var south=vertices.Min(p=>p.Latitude);var north=vertices.Max(p=>p.Latitude);var west=vertices.Min(p=>p.Longitude);var east=vertices.Max(p=>p.Longitude);
            for(var lat=Math.Floor(south/.02)*.02;lat<=north;lat+=.02)
            for(var lon=Math.Floor(west/.02)*.02;lon<=east;lon+=.02)
            {if(++inspected>100000||seen.Count>=4096)yield break;var center=new Coordinates(lat+.01,lon+.01);var cell=Cell(center);if(cell is not null&&InPolygon(center,vertices)&&seen.Add(cell))yield return cell;}
        }
    }
    public static RemoteKind[] Kinds(MissionPlan plan)=>(plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).Distinct().ToArray();
    public static PlanDataPreparation Prepare(AutomaticScientificData automatic,MissionPlan plan,bool retry=false)
    {
        var kinds=Kinds(plan);var cells=Cells(plan).Take(8).ToArray();foreach(var cell in cells)automatic.Prepare(cell,kinds,retry,background:!retry,scope:"plan:"+plan.Id);
        return new(cells.Length,true,"First bounded window queued; progressive preflight continues in the background, subject to consent and resource budgets.",automatic.Snapshot());
    }
}

public sealed class ScientificPlanData(AutomaticScientificData automatic,ScientificAccessSettings settings)
{
    private sealed class Session(MissionPlan plan)
    {
        public readonly MissionPlan Plan=plan;
        public readonly IEnumerator<GeographicBounds> Cells=ScientificPlanPreparation.Cells(plan).GetEnumerator();
        public readonly Dictionary<string,GeographicBounds> Pending=[];
        public readonly HashSet<string> Counted=[];
        public int Areas,Ready,Unavailable;public long Bytes;public bool Exhausted,Paused,Cancelled;
    }
    private readonly Dictionary<Guid,Session> sessions=[];private readonly object gate=new();
    public PlanDataPreparation Prepare(MissionPlan plan,bool retry=false)
    {
        lock(gate)
        {
            if(!sessions.TryGetValue(plan.Id,out var s)||s.Plan.UpdatedUtc!=plan.UpdatedUtc||s.Cancelled)
            {if(sessions.Count>=16){var first=sessions.First();first.Value.Cells.Dispose();sessions.Remove(first.Key);}if(s is not null)s.Cells.Dispose();sessions[plan.Id]=s=new(plan);}
            s.Paused=false;Pump(s,explicitAction:retry);return new(s.Areas,!s.Exhausted,"Progressive preflight started. It pauses at network/storage limits; live aircraft work has higher priority.",automatic.Snapshot());
        }
    }
    public void Control(Guid id,string action)
    {
        lock(gate){if(!sessions.TryGetValue(id,out var s))throw new ArgumentException("No preparation session exists for this plan.");
            if(action=="pause")s.Paused=true;else if(action=="resume"){s.Paused=false;s.Cancelled=false;}else if(action=="cancel"){s.Cancelled=true;s.Paused=true;automatic.CancelScope("plan:"+s.Plan.Id);}else throw new ArgumentException("Choose pause, resume or cancel.");}
    }
    private void Pump(Session s,bool explicitAction=false)
    {
        var states=automatic.Snapshot().ToDictionary(r=>r.Key);
        foreach(var item in s.Pending.ToArray())
        {
            if(!states.TryGetValue(item.Key,out var state)||state.Status=="Superseded")continue;
            if(state.Status is "Queued" or "Downloading")continue;
            if(state.Status=="Offline")continue;
            if(s.Counted.Add(item.Key)){if(state.Status is "Ready" or "Dataset Ready")s.Ready++;else s.Unavailable++;s.Bytes+=state.DownloadedBytes;}
            s.Pending.Remove(item.Key);
        }
        if(s.Paused||s.Cancelled||(!explicitAction&&!settings.Get().Preferences.BackgroundRetrieval))return;
        var budget=automatic.QueueSummary();if(budget.HourlyBytes>=budget.HourlyLimit)return;
        var kinds=ScientificPlanPreparation.Kinds(s.Plan);if(kinds.Length==0){s.Exhausted=true;return;}
        var allowance=Math.Max(0,24-s.Pending.Count);
        foreach(var pending in s.Pending.Take(allowance).ToArray())
            if(!states.TryGetValue(pending.Key,out var state)||state.Status is "Superseded" or "Offline")automatic.Prepare(pending.Value,kinds,background:!explicitAction,priority:10000+s.Areas,scope:"plan:"+s.Plan.Id);
        while(!s.Exhausted&&s.Pending.Count+kinds.Length<=24&&automatic.QueueSummary().Queued<48)
        {
            if(!s.Cells.MoveNext()){s.Exhausted=true;break;}
            var area=s.Cells.Current;s.Areas++;
            automatic.Prepare(area,kinds,background:!explicitAction,priority:10000+s.Areas,scope:"plan:"+s.Plan.Id);
            foreach(var kind in kinds){var key=AutomaticScientificData.AreaKey(kind,area);if(!s.Counted.Contains(key))s.Pending[key]=area;}
        }
    }
    public void Tick(){lock(gate)foreach(var s in sessions.Values)Pump(s);}
    public ScientificPlanReadiness[] Snapshot()
    {
        lock(gate)return sessions.Values.Select(s=>new ScientificPlanReadiness(s.Plan.Id,s.Plan.Name,s.Areas,!s.Exhausted||s.Unavailable>0,
            "Progressive bounded batches; additional unsurveyed areas may remain. Limits: 4096 distinct areas / 100000 geometry checks per session, hourly transfer and library capacity. Counts describe preparation, not scientific coverage scoring.",
            s.Cancelled?"Cancelled":s.Paused?"Paused":s.Exhausted&&s.Pending.Count==0?"Complete":"Preparing",s.Ready,s.Unavailable,s.Pending.Count,s.Bytes,
            s.Pending.Count*settings.Get().Preferences.MaximumDownloadBytes)).ToArray();
    }
}

public sealed class ScientificFlightPrediction(AutomaticScientificData automatic,ScientificAccessSettings settings)
{
    private readonly ScientificMotionPredictor predictor=new();private ScientificPredictionResult? last;private RemoteKind[] kinds=[];private readonly object gate=new();
    public void Reset(){lock(gate){predictor.Reset();last=null;kinds=[];}automatic.ReprioritizeFlight(new Dictionary<string,double>());}
    public void Observe(TelemetryFrame frame,RemoteKind[] enabled,MissionPlan? plan)
    {
        lock(gate)
        {
            if(enabled.Length==0){predictor.Reset();last=null;kinds=[];automatic.ReprioritizeFlight(new Dictionary<string,double>());return;}
            kinds=enabled;last=predictor.Predict(frame,DateTimeOffset.UtcNow,enabled,settings.Get().Preferences,automatic.EstimatedPreparationSeconds,plan);
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
                plans.Tick();var state=missions.Snapshot();
                if(state.Connection.State!=ConnectionState.Connected||state.Research?.IntentionallyPaused==true||state.Telemetry is not{IsValid:true,IsPaused:false} frame||DateTimeOffset.UtcNow-frame.TimestampUtc>TimeSpan.FromSeconds(6)){prediction.Reset();continue;}
                var kinds=state.Research?.Instruments.Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).ToArray()??[];
                var plan=state.ActiveMission is {} mission?store.Research(mission.Id).PlanSnapshot:null;
                prediction.Observe(frame,kinds,plan);
            }
            catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Scientific preparation unavailable: {Message}. Flight recording continues independently.",error.Message);}
        }
    }
}
