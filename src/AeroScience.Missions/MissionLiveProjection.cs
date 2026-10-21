using AeroScience.Shared;
namespace AeroScience.Missions;

// Incremental in-memory projection of archived facts. No raster/union/provider work and no recorder lock.
// Restart replays bounded pages; incomplete backlogs remain provisional. Final evaluation uses the full archive.
public sealed class MissionLiveProjection(MissionStore store,Guid mission,ScienceStore? science=null)
{
    readonly Dictionary<Guid,AcquisitionResult> acquisitions=[];
    readonly Dictionary<Guid,ScienceAcquisitionIntent> pending=[];
    readonly Queue<Guid> pendingOrder=[];
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
            if(pending.Count<4096){pending[intent.Id]=intent;pendingOrder.Enqueue(intent.Id);}else bounded=true;
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
        // Reconcile bounded original scalar receipts after asynchronous publication. Native
        // raster QA/coverage stays pending; no full cube is decoded during a live projection.
        var retryCount=Math.Min(64,pendingOrder.Count);
        if(science is not null)for(var retryIndex=0;retryIndex<retryCount;retryIndex++)
        {
            var key=pendingOrder.Dequeue();if(!pending.TryGetValue(key,out var intent))continue;
            if(acquisitions.Count>=200000){bounded=true;break;}
            if(RemoteAcquisition.Instruments.ContainsKey(intent.InstrumentId))
            {acquisitions[intent.Id]=new(intent.Id,intent.SessionId,intent.InstrumentId,intent.ExposureUtc,0,0,0,false,false,false,["intent:"+intent.Id],["Native source QA and valid-pixel support await archived reconciliation."]);pending.Remove(intent.Id);continue;}
            var rows=science.IntentObservations(intent);var expectedCount=ScienceCatalog.Definitions.Single(d=>d.Id==intent.InstrumentId).Measurements.Length;
            var packet=store.CollectionInput(mission,intent.InputReceiptId)?.Packet;var assessable=packet?.Atmosphere is not null||packet?.Frame?.Atmosphere is not null;
            var valid=rows.Count(r=>r.Quality==ObservationQuality.Valid&&double.IsFinite(r.Value));
            acquisitions[intent.Id]=new(intent.Id,intent.SessionId,intent.InstrumentId,intent.ExposureUtc,expectedCount,valid,expectedCount-valid,assessable&&rows.Length>0,rows.Length>0,false,["intent:"+intent.Id],[]);
            if(rows.Length>0)pending.Remove(intent.Id);else pendingOrder.Enqueue(intent.Id);
        }
        var asOf=at==default?flight.StartedUtc:at;
        var yield=acquisitions.Values.Select(a=>a.InstrumentId).Distinct().Select(i=>ScienceYieldEvaluation.Calculate(mission,null,i,asOf,acquisitions.Values)).ToArray();
        var sessionResults=acquisitions.Values.GroupBy(a=>a.SessionId).ToDictionary(g=>g.Key,g=>g.ToArray());
        var sessions=store.CollectionSessions(mission,32);var checkpoint=store.CollectionCheckpoint(mission);
        if(checkpoint?.CurrentSessionId is {} sid&&!sessions.Any(s=>s.Id==sid)&&store.CollectionSession(mission,sid) is {} open)sessions=[..sessions,open];
        var summaries=sessions.Select(s=>new SessionEvaluation(s,sessionResults.TryGetValue(s.Id,out var results)?results.Select(r=>r.InstrumentId).Distinct().Select(i=>ScienceYieldEvaluation.Calculate(mission,s.Id,i,asOf,results)).ToArray():[],[],[],["Bounded live session diagnostics; final archived reports retain complete events and grading evidence."])).ToArray();
        var input=new MissionEvaluationInput(1,mission,asOf,false,context,facts.ToArray(),yield,summaries,[],
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
