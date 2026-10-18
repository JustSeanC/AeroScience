using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public sealed record PlanDataPreparation(int AreasQueued,bool LimitedCoverage,string Message,ScientificDataReadiness[] Readiness);
public static class ScientificPlanPreparation
{
    // A corridor/survey is split into small cells, never its continent-sized bbox.
    // At most eight cells are queued per action; larger plans disclose partial readiness.
    private static bool InPolygon(Coordinates p,Coordinates[] vertices)
    {
        var inside=false;for(int i=0,j=vertices.Length-1;i<vertices.Length;j=i++)
            if((vertices[i].Latitude>p.Latitude)!=(vertices[j].Latitude>p.Latitude)&&p.Longitude<(vertices[j].Longitude-vertices[i].Longitude)*(p.Latitude-vertices[i].Latitude)/(vertices[j].Latitude-vertices[i].Latitude)+vertices[i].Longitude)inside=!inside;
        return inside;
    }
    public static PlanDataPreparation Prepare(AutomaticScientificData automatic,MissionPlan plan,bool retry=false)
    {
        var kinds=(plan.Instruments??[]).Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).Distinct().ToArray();
        if(kinds.Length==0)return new(0,false,"Enable a remote-sensing instrument in this plan first.",automatic.Snapshot());
        var points=(plan.Waypoints??[]).Select(w=>new Coordinates(w.Latitude,w.Longitude)).ToArray();
        var cells=new HashSet<GeographicBounds>();var limited=false;
        void Add(Coordinates point)
        {
            if(cells.Count>=8){limited=true;return;}
            try{var cell=GeographicBounds.Around(point);AutomaticScientificData.ValidateArea(cell);cells.Add(cell);}catch(ArgumentException){limited=true;}
        }
        foreach(var point in points)Add(point);
        foreach(var segment in points.Zip(points.Skip(1)))
        {
            var dx=segment.Second.Longitude-segment.First.Longitude;var dy=segment.Second.Latitude-segment.First.Latitude;
            var steps=(int)Math.Min(10000,Math.Ceiling(Math.Max(Math.Abs(dx),Math.Abs(dy))/.01));
            for(var i=1;i<steps;i++){if(cells.Count>=8){limited=true;break;}Add(new(segment.First.Latitude+dy*i/steps,segment.First.Longitude+dx*i/steps));}
        }
        foreach(var area in plan.SurveyAreas??[])
        {
            var vertices=area.Vertices??[];if(vertices.Length<3)continue;
            foreach(var vertex in vertices)Add(vertex);
            var inspected=0;
            var south=vertices.Min(p=>p.Latitude);var north=vertices.Max(p=>p.Latitude);var west=vertices.Min(p=>p.Longitude);var east=vertices.Max(p=>p.Longitude);
            for(var lat=Math.Floor(south/.02)*.02;lat<=north;lat+=.02)
            {
                if(cells.Count>=8||inspected>=256){limited=true;break;}
                for(var lon=Math.Floor(west/.02)*.02;lon<=east;lon+=.02)
                {if(cells.Count>=8||++inspected>256){limited=true;break;}var center=new Coordinates(lat+.01,lon+.01);if(InPolygon(center,vertices))Add(center);}
            }
        }
        foreach(var cell in cells)automatic.Prepare(cell,kinds,retry,background:!retry);
        return new(cells.Count,limited,limited?"Limited Coverage: the first eight small route/survey cells were queued. Large plans require staged preparation; this does not establish complete corridor coverage.":cells.Count==0?"Add a route waypoint or survey polygon to prepare data before takeoff.":"Bounded route/survey cells queued. Inspect each instrument's actual source date, coverage, QA and preparation state before takeoff.",automatic.Snapshot());
    }
}
public sealed class ScientificPreparationWorker(AutomaticScientificData automatic,ScientificAccessSettings settings,MissionCoordinator missions):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(5));
        while(await timer.WaitForNextTickAsync(stoppingToken))
        {
            var p=settings.Get().Preferences;if(!p.OnlineEnabled||!p.BackgroundRetrieval)continue;
            var state=missions.Snapshot();if(state.Connection.State!=ConnectionState.Connected||state.Research?.IntentionallyPaused==true||state.Telemetry is not{IsValid:true,IsPaused:false} frame||DateTimeOffset.UtcNow-frame.TimestampUtc>TimeSpan.FromSeconds(6))continue;
            var kinds=state.Research?.Instruments.Where(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)).Select(c=>RemoteAcquisition.Instruments[c.InstrumentId]).ToArray()??[];
            try{automatic.Prepare(GeographicBounds.Around(new(frame.Latitude,frame.Longitude)),kinds,background:true);}catch(ArgumentException){/* Polar/dateline unsupported area remains explicitly unavailable. */}
        }
    }
}

public sealed record ScientificPlanReadiness(Guid PlanId,string PlanName,int AreasQueued,bool LimitedCoverage,string Message);
public sealed class ScientificPlanData(AutomaticScientificData automatic)
{
    private readonly Dictionary<Guid,ScientificPlanReadiness> plans=[];private readonly object gate=new();
    public PlanDataPreparation Prepare(MissionPlan plan,bool retry=false)
    {
        var result=ScientificPlanPreparation.Prepare(automatic,plan,retry);
        lock(gate){if(plans.Count>=64)plans.Remove(plans.Keys.First());plans[plan.Id]=new(plan.Id,plan.Name,result.AreasQueued,result.LimitedCoverage,result.Message);}
        return result;
    }
    public ScientificPlanReadiness[] Snapshot(){lock(gate)return plans.Values.ToArray();}
}
