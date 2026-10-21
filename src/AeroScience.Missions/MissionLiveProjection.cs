using AeroScience.Shared;
namespace AeroScience.Missions;

// Incremental in-memory projection of archived facts. No raster/union/provider work and no recorder lock.
// Restart replays bounded pages; incomplete backlogs remain provisional. Final evaluation uses the full archive.
public sealed class MissionLiveProjection(MissionStore store,Guid mission)
{
    readonly MissionAssessmentContext? context=store.CollectionSnapshot<MissionAssessmentContext>(mission);
    readonly MissionResearch research=store.Research(mission);
    readonly Mission flight=store.Detail(mission)!.Mission;
    readonly Dictionary<Guid,HashSet<long>> expected=[];
    readonly Dictionary<Guid,HashSet<long>> attempted=[];
    readonly HashSet<Guid> reached=[];
    readonly Dictionary<long,TelemetryFrame> positions=[];
    long after;int intentOffset,expectedWaypoint;DateTimeOffset at;bool bounded;
    public MissionPerformanceReport Update()
    {
        var page=store.EvaluationPositions(mission,after);var route=research.PlanSnapshot?.Waypoints??[];
        foreach(var (sequence,frame) in page)
        {
            after=sequence;if(frame.TimestampUtc<flight.StartedUtc)continue;at=frame.TimestampUtc;var tick=frame.TimestampUtc.ToUnixTimeSeconds();positions[tick]=frame;
            if(positions.Count>200000){positions.Remove(positions.Keys.Min());bounded=true;}
            if(expectedWaypoint<route.Length&&Geodesy.Distance(new(frame.Latitude,frame.Longitude),Geodesy.Point(route[expectedWaypoint]))<=1000)expectedWaypoint++;
            foreach(var obligation in context?.Obligations??[])
            {
                if(obligation.CriterionId=="execution")
                {
                    if(obligation.Scope=="waypoint"&&obligation.RegionIndex<expectedWaypoint||obligation.Scope=="region"&&InScope(frame,obligation)||obligation.Scope=="altitude"&&obligation.TargetAltitudeFeet is {} altitude&&Math.Abs(frame.AltitudeMslFeet-altitude)<=100)reached.Add(obligation.Id);
                }
                if(obligation.CriterionId!="discipline"||!InScope(frame,obligation)||frame.IsPaused)continue;
                var interval=Configuration(obligation).IntervalMs/1000d;var slots=expected.GetValueOrDefault(obligation.Id)??(expected[obligation.Id]=[]);if(slots.Count<200000)slots.Add(Slot(frame.TimestampUtc,interval));else bounded=true;
            }
        }
        // At most one bounded page per tick. Intents remain actual attempts even if their source fails.
        var intents=store.ScienceIntents(mission,limit:500,offset:intentOffset);intentOffset+=intents.Length;
        foreach(var intent in intents.Select(i=>i.Intent))
        {
            TelemetryFrame? frame=null;var time=intent.ExposureUtc.ToUnixTimeSeconds();for(var i=0;i<=6;i++)if(positions.TryGetValue(time-i,out frame))break;
            foreach(var obligation in context?.Obligations.Where(o=>o.CriterionId=="discipline"&&o.InstrumentId==intent.InstrumentId)??[])
            {
                if(frame is null||!InScope(frame,obligation))continue;var slots=attempted.GetValueOrDefault(obligation.Id)??(attempted[obligation.Id]=[]);if(slots.Count<200000)slots.Add(Slot(intent.ExposureUtc,Configuration(obligation).IntervalMs/1000d));else bounded=true;
            }
        }
        var facts=new List<CriterionEvidence>();
        foreach(var obligation in context?.Obligations??[])
        {
            double? lower=null,upper=null;double evidence=0;string[] classes=[];var reasons=new List<string>();
            if(obligation.CriterionId=="design")
            {
                var p=research.PlanSnapshot;var tests=new[]{!string.IsNullOrWhiteSpace(p?.Objective??research.Objective),context!.Configurations.Length>0,
                    p?.Template==MissionTemplateKind.GeographicSurvey?p.SurveyAreas is {Length:>0}:p?.Template==MissionTemplateKind.AtmosphericProfile?p.MinimumAltitudeFeet<p.MaximumAltitudeFeet:p?.Waypoints is {Length:>0}||p?.EstimatedDurationMinutes>0,
                    p is not null&&(p.Waypoints is {Length:>0}||p.EstimatedDurationMinutes>0||p.MinimumAltitudeFeet<p.MaximumAltitudeFeet)};
                lower=upper=100d*tests.Count(t=>t)/tests.Length;evidence=1;classes=["frozen-plan"];reasons.Add($"{tests.Count(t=>t)}/{tests.Length} frozen design criteria supported.");
            }
            else if(obligation.CriterionId=="execution")
            {
                if(reached.Contains(obligation.Id)){lower=upper=100;evidence=1;classes=["flight-position"];}
                else if(obligation.Scope=="flight"&&research.PlanSnapshot?.EstimatedDurationMinutes is {} duration&&at!=default){lower=Math.Min(100,100*(at-flight.StartedUtc).TotalSeconds/(duration*60));upper=100;evidence=lower.Value/100;classes=["flight-position"];}
                reasons.Add("Incremental ordered actual route/region/altitude evidence. Uncompleted future targets remain uncertain, not observed failures.");
            }
            else if(obligation.CriterionId=="discipline")
            {
                var required=MissionRubrics.RequiredSlots(expected.GetValueOrDefault(obligation.Id,[]),obligation.TargetAmount);var denominator=Math.Max(required.Count,obligation.TargetAmount??0);var completed=attempted.GetValueOrDefault(obligation.Id,[]).Count(s=>required.Contains(s));
                if(denominator>0){lower=100*completed/denominator;upper=100;evidence=completed/denominator;classes=completed>0?["collection-intent"]:[];}
                if(obligation.TargetAmount is null){lower=0;upper=100;evidence=0;classes=[];reasons.Add("Intended work is not numerically bounded; explicit route-window completion is assessed from archived trigger evidence, never guessed live.");}
                reasons.Add($"{completed}/{denominator} current frozen-cadence research slots contain actual attempts. Future work, source quality and final evidence reconciliation remain separate.");
            }
            else reasons.Add("Operating and exact scientific-support evaluation remains pending final archived assessment; no current rule outcome is guessed.");
            facts.Add(new(obligation.Id,lower,upper,evidence,classes,reasons.ToArray(),[],["obligation:"+obligation.Id]));
        }
        var input=new MissionEvaluationInput(1,mission,at==default?flight.StartedUtc:at,false,context,facts.ToArray(),[],[],[],
            ["Bounded incremental live projection: up to 1000 positions and 500 intents per update. No raster/coverage/archive scan on dashboard refresh. Future and unsupported evidence stays uncertain.",..(bounded?new[]{"Live memory bound reached; projection is partial. Full archived evaluation is required."}:Array.Empty<string>())],FlightSource:flight.Source);
        return MissionGrading.Report(input,reason:"Incremental provisional evidence; End Flight determines final eligibility");
    }
    InstrumentConfiguration Configuration(MissionObligation o)=>context!.Configurations.Single(c=>c.Settings.InstrumentId==o.InstrumentId).Settings;
    long Slot(DateTimeOffset time,double seconds)=>(long)Math.Floor((time-flight.StartedUtc).TotalSeconds/seconds+1e-6);
    bool InScope(TelemetryFrame frame,MissionObligation obligation)
    {
        if(obligation.Scope=="region"&&obligation.RegionIndex is {} index&&research.PlanSnapshot?.SurveyAreas?.ElementAtOrDefault(index) is {} area)return MissionAnalysis.Inside(new(frame.Latitude,frame.Longitude),area.Vertices);
        // Point/gate windows need archived ordered crossing evidence. Do not guess them live.
        return obligation.Scope!="planned-session";
    }
}
