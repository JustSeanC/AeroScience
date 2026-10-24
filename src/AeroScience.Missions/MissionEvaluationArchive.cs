using AeroScience.Shared;
namespace AeroScience.Missions;

public static class MissionEvaluationArchive
{
    static ScienceCollectionSession[] Sessions(MissionStore store,Guid mission)
    {var result=new List<ScienceCollectionSession>();for(var offset=0;;offset+=500){var page=store.CollectionSessions(mission,500,offset);result.AddRange(page);if(page.Length<500)break;if(result.Count>=4096)throw new InvalidOperationException("More than 4096 sessions; evaluation remains unsupported.");}return result.ToArray();}
    static CollectionEvent[] Events(MissionStore store,Guid mission)
    {var result=new List<CollectionEvent>();long after=0;while(true){var page=store.CollectionEvents(mission,after,500);result.AddRange(page);if(result.Count>200000)throw new InvalidOperationException("Collection evidence exceeds bounded report workload.");if(page.Length<500)break;after=page[^1].Sequence;}return result.ToArray();}
    public static MissionEvaluationInput Build(MissionStore store,ScienceStore science,Guid mission,CancellationToken token=default,bool coverage=true)
    {
        var detail=store.Detail(mission)??throw new ArgumentException("Mission not found.");var research=store.Research(mission);var plan=research.PlanSnapshot;
        var archivedFrames=store.StreamSamples(mission).Take(2000001).ToArray();var frames=archivedFrames.Where(f=>f.TimestampUtc>=detail.Mission.StartedUtc&&(detail.Mission.EndedUtc is null||f.TimestampUtc<=detail.Mission.EndedUtc)).ToArray();if(archivedFrames.Length>2000000)throw new InvalidOperationException("Archive exceeds two million positions; no partial final grade generated.");
        var sessions=Sessions(store,mission);var events=Events(store,mission);var actual=ScientificEvaluationEvidence.Read(store,science,mission,token);
        var operating=store.StreamOperatingReports(mission).Take(1000001).ToArray();if(operating.Length>1000000)throw new InvalidOperationException("Operating evidence exceeds bounded report workload.");
        var context=store.CollectionSnapshot<MissionAssessmentContext>(mission);var at=detail.Mission.EndedUtc??frames.LastOrDefault()?.TimestampUtc??detail.Mission.StartedUtc;
        var completed=detail.Mission.Status!=MissionStatus.Recording;
        var limitations=new List<string>{"Pilot performance is not scientific accuracy. Original telemetry, source values and QA are unchanged.","Session diagnostics are not averaged to produce the overall grade. Frozen obligations retain omitted/never-started research.","Unknown gaps, manual pause reasons and unsupported operating evidence remain uncertain; archived source QA is distinct from pilot conduct."};
        var controls=events.Where(e=>e.Outcome==CollectionEventOutcome.Applied&&e.Action!=CollectionAction.Unknown).OrderBy(e=>e.InputUtc).ThenBy(e=>e.Sequence).ToArray();
        var controlAt=new Dictionary<DateTimeOffset,CollectionEvent?>();var controlIndex=0;CollectionEvent? lastControl=null;
        foreach(var frame in frames){while(controlIndex<controls.Length&&controls[controlIndex].InputUtc<=frame.TimestampUtc)lastControl=controls[controlIndex++];controlAt[frame.TimestampUtc]=lastControl;}
        // Exact ingress sequence disambiguates Pause/Resume issued at the same clock instant
        // as an already committed observation. Later controls cannot rewrite that observation.
        CollectionEvent? sequencedControl=null;bool continuityHeld=false;var continuityAt=new Dictionary<DateTimeOffset,bool>();
        foreach(var e in events.OrderBy(e=>e.Sequence))
        {
            if(e.Outcome!=CollectionEventOutcome.Applied)continue;
            var engineHold=e.Origin==CollectionEventOrigin.Engine&&e.Action==CollectionAction.Unknown&&e.ConfigurationIds is null&&!e.Reason.StartsWith("Committed exact ingress",StringComparison.Ordinal);
            if(engineHold)continuityHeld=true;
            if(e.Action==CollectionAction.RearmAutomation)continuityHeld=false;
            if(e.Action!=CollectionAction.Unknown||engineHold)sequencedControl=e;
            else if(e.Origin==CollectionEventOrigin.Engine&&e.Reason.StartsWith("Committed exact ingress",StringComparison.Ordinal))
                foreach(var link in e.Evidence){controlAt[link.ObservedUtc]=sequencedControl;continuityAt[link.ObservedUtc]=continuityHeld;}
        }
        CollectionEvent? StateAt(DateTimeOffset time)=>controlAt.GetValueOrDefault(time);
        bool AuthorizedPause(CollectionEvent? e)=>e is {Action:CollectionAction.PauseData}&&(e.Justification=="safety"||e.Origin is CollectionEventOrigin.RouteTrigger or CollectionEventOrigin.Boundary);
        var verifiedFrames=frames.Where(f=>!continuityAt.GetValueOrDefault(f.TimestampUtc)).ToArray();
        var criteria=new List<CriterionEvidence>();var instruments=(context?.Configurations.Select(c=>c.Settings.InstrumentId)??[]).Concat(research.Instruments.Where(c=>c.Enabled).Select(c=>c.InstrumentId)).Concat(sessions.SelectMany(s=>s.Configurations).Where(c=>c.Settings.Enabled).Select(c=>c.Settings.InstrumentId)).Concat(actual.Select(a=>a.InstrumentId)).Distinct().Order(StringComparer.Ordinal).ToArray();
        var yield=new List<ScienceYield>();
        foreach(var instrument in instruments)
        {
            token.ThrowIfCancellationRequested();yield.Add(Yield(null,instrument));foreach(var session in sessions)yield.Add(Yield(session.Id,instrument));
        }
        ScienceYield Yield(Guid? session,string instrument)
        {
            var y=ScienceYieldEvaluation.Calculate(mission,session,instrument,at,actual);
            if(!RemoteAcquisition.Instruments.ContainsKey(instrument))return y;
            ScienceMetric metric;
            if(!coverage)metric=new("valid-survey-coverage",AssessmentStatus.NotAssessed,null,null,"m²","Pending final native-source support union; never inferred from route or modeled footprint.",0,0,EvidenceOrigin.Derived,[]);
            else if(plan?.SurveyAreas is not {Length:>0})metric=new("valid-survey-coverage",AssessmentStatus.Unsupported,null,null,"m²","No survey polygon: a survey percentage is inapplicable.",0,0,EvidenceOrigin.Derived,[]);
            else try
            {
                var eligible=ScientificEvaluationEvidence.Intents(store,mission).Where(i=>i.State=="Published"&&i.ProductId.HasValue).Select(i=>i.ProductId!.Value).ToHashSet();
                var result=ScientificCoverage.Calculate(science,plan,mission,new(instrument),token,sessionId:session,eligibleProducts:eligible);
                metric=new("valid-survey-coverage",AssessmentStatus.Supported,result.ValidDataSquareMetres,result.SurveySquareMetres,"m²","scientific-coverage-1: native valid-pixel support union intersected with frozen survey polygons; see scientific limitations.",0,0,EvidenceOrigin.Derived,result.Methods);
            }
            catch(Exception e)when(e is IOException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {metric=new("valid-survey-coverage",AssessmentStatus.NotAssessed,null,null,"m²","Native union unavailable or outside supported geometry; no approximate success percentage substituted.",0,1,EvidenceOrigin.Derived,["Use the existing detailed coverage analysis to inspect available source support."]);}
            return y with{Metrics=[..y.Metrics,metric]};
        }
        var contiguous=verifiedFrames.Length==frames.Length&&frames.Length>=2&&frames.Zip(frames.Skip(1)).All(p=>(p.Second.TimestampUtc-p.First.TimestampUtc).TotalSeconds is >0 and <=5);
        var comparison=MissionAnalysis.Compare(plan,verifiedFrames,new());
        (DateTimeOffset? From,DateTimeOffset? To) PlannedWindow(string? key)
        {
            var actions=plan?.Collection?.Triggers.Where(t=>t.PlannedSessionKey==key).ToArray()??[];
            var start=actions.FirstOrDefault(t=>t.Action==CollectionAction.StartData);var end=actions.FirstOrDefault(t=>t.Action==CollectionAction.EndData&&t.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate);
            return(start is null?null:events.FirstOrDefault(e=>e.OccurrenceId==start.OccurrenceId)?.InputUtc,end is null?null:events.FirstOrDefault(e=>e.OccurrenceId==end.OccurrenceId&&e.Origin==CollectionEventOrigin.RouteTrigger)?.InputUtc);
        }
        var plannedWindows=(plan?.Collection?.Triggers??[]).Where(t=>t.Action==CollectionAction.StartData&&t.PlannedSessionKey is not null).Select(t=>t.PlannedSessionKey).Distinct().Select(PlannedWindow).ToArray();
        var allWindowsBounded=plannedWindows.Length>0&&plannedWindows.All(w=>w.From.HasValue&&w.To.HasValue&&w.To>=w.From);
        bool InScope(TelemetryFrame f,MissionObligation o)
        {
            if(o.Scope=="region"&&o.RegionIndex is {} index&&plan?.SurveyAreas?.ElementAtOrDefault(index) is {} area)return MissionAnalysis.Inside(new(f.Latitude,f.Longitude),area.Vertices);
            if(o.Scope=="planned-session")
            {
                var actions=plan?.Collection?.Triggers.Where(t=>t.PlannedSessionKey==o.PlannedSessionKey).ToArray()??[];
                var start=actions.FirstOrDefault(t=>t.Action==CollectionAction.StartData);
                var end=actions.FirstOrDefault(t=>t.Action==CollectionAction.EndData);
                var route=plan?.Waypoints??[];var si=Array.FindIndex(route,w=>w.Id==start?.TargetId);var ei=Array.FindIndex(route,w=>w.Id==end?.TargetId);
                var from=start is null?null:events.FirstOrDefault(e=>e.OccurrenceId==start.OccurrenceId)?.InputUtc;
                var to=end is null?null:events.FirstOrDefault(e=>e.OccurrenceId==end.OccurrenceId)?.InputUtc;
                var areaIndex=plan?.Collection?.SurveyAreas?.FirstOrDefault(a=>a.Id==start?.TargetId)?.PlanAreaIndex;
                var inside=areaIndex is null||MissionAnalysis.Inside(new(f.Latitude,f.Longitude),plan!.SurveyAreas![areaIndex.Value].Vertices);
                return inside&&from.HasValue&&f.TimestampUtc>=from&&(!to.HasValue||f.TimestampUtc<to);
            }
            return true;
        }
        foreach(var obligation in context?.Obligations??[])
        {
            token.ThrowIfCancellationRequested();double? low=null,high=null;double evidence=0;string[] classes=[];var reasons=new List<string>();var recommendations=new List<string>();var refs=new List<string>{"obligation:"+obligation.Id};var metrics=new List<ScienceMetric>();
            void Metric(string id,double numerator,double denominator,string unit,string method,long missing=0,long unassessed=0){if(denominator>0)metrics.Add(new(id,AssessmentStatus.Supported,numerator,denominator,unit,method,missing,unassessed,EvidenceOrigin.Derived,[]));}
            void Score(double value,string evidenceClass){low=high=value;evidence=1;classes=[evidenceClass];}
            if(obligation.CriterionId=="design")
            {
                var tests=new[]{!string.IsNullOrWhiteSpace(plan?.Objective??research.Objective),context!.Configurations.Length>0,
                    plan?.Template==MissionTemplateKind.GeographicSurvey?plan.SurveyAreas is {Length:>0}:plan?.Template==MissionTemplateKind.AtmosphericProfile?plan.MinimumAltitudeFeet<plan.MaximumAltitudeFeet:plan?.Waypoints is {Length:>0}||plan?.EstimatedDurationMinutes>0,
                    plan is not null&&(plan.Waypoints is {Length:>0}||plan.EstimatedDurationMinutes>0||plan.MinimumAltitudeFeet<plan.MaximumAltitudeFeet)};
                Metric("design-checks",tests.Count(v=>v),tests.Length,"frozen requirements","Explicit objective, instruments, template research work and route/profile/duration.");Score(100d*tests.Count(v=>v)/tests.Length,"frozen-plan");reasons.Add($"{tests.Count(v=>v)}/{tests.Length} frozen design criteria: objective, enabled instruments, template research geometry, bounded route/profile/duration.");recommendations.Add("Define missing objective/instruments and template-specific research work before flight.");
            }
            else if(obligation.CriterionId=="execution")
            {
                bool? observed=null;
                if(obligation.Scope=="waypoint"&&obligation.RegionIndex is {} index){observed=comparison.Waypoints.ElementAtOrDefault(index)?.Reached;reasons.Add("Ordered actual waypoint observations, 1000 m policy tolerance; geographic coverage is separate.");}
                else if(obligation.Scope=="altitude"&&obligation.TargetAltitudeFeet is {} altitude){observed=verifiedFrames.Any(f=>Math.Abs(f.AltitudeMslFeet-altitude)<=100);reasons.Add($"Actual altitude target {altitude.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)} ft MSL within 100 ft policy tolerance; no polygon required.");}
                else if(obligation.Scope=="region"){observed=verifiedFrames.Any(f=>InScope(f,obligation));reasons.Add("Actual visit to frozen study region, not proof of valid scientific pixels.");}
                else if(obligation.Scope=="planned-session"){observed=verifiedFrames.Any(f=>InScope(f,obligation));reasons.Add("Configured point/gate research window assessed from route observations; actual session End does not shorten it.");}
                else if(plan?.EstimatedDurationMinutes is {} duration&&frames.Length>0){Score(Math.Min(100,100*(frames[^1].TimestampUtc-detail.Mission.StartedUtc).TotalSeconds/(duration*60)),"flight-position");reasons.Add("Recorded time versus frozen planned duration; not geographic coverage.");}
                if(observed.HasValue)Metric("planned-target",observed.Value?1:0,1,"planned target","Observed ordered route, altitude or research-region target; not valid scientific coverage.");
                if(observed==true)Score(100,"flight-position");else if(observed==false&&contiguous&&completed)Score(0,"flight-position");
                recommendations.Add("Visit planned research regions and ordered route/altitude targets. Missing telemetry cannot prove an unobserved visit.");
            }
            else if(obligation.CriterionId=="discipline")
            {
                var config=context!.Configurations.First(c=>c.Settings.InstrumentId==obligation.InstrumentId).Settings;
                var interval=config.IntervalMs/1000d;
                long Slot(DateTimeOffset time)=>(long)Math.Floor((time-detail.Mission.StartedUtc).TotalSeconds/interval+1e-6);
                var scopedFrames=frames.Where(f=>InScope(f,obligation)&&!f.IsPaused&&(obligation.Scope!="region"||!allWindowsBounded||plannedWindows.Any(w=>f.TimestampUtc>=w.From&&f.TimestampUtc<w.To))).ToArray();
                var expected=MissionRubrics.RequiredSlots(scopedFrames.Where(f=>!AuthorizedPause(StateAt(f.TimestampUtc))).Select(f=>Slot(f.TimestampUtc)),obligation.TargetAmount);
                var matching=actual.Where(a=>a.InstrumentId==obligation.InstrumentId&&expected.Contains(Slot(a.ExposureUtc))).GroupBy(a=>Slot(a.ExposureUtc)).Select(g=>g.First()).ToArray();
                var denominator=Math.Max(expected.Count,obligation.TargetAmount??0);
                // Start/End/disable never change expected slots. No result/source success is needed for attempt credit.
                var unknownPaused=scopedFrames.Where(f=>continuityAt.GetValueOrDefault(f.TimestampUtc)||StateAt(f.TimestampUtc) is {To:CollectionPhase.Paused}&&!AuthorizedPause(StateAt(f.TimestampUtc))).Select(f=>Slot(f.TimestampUtc)).Distinct().ToHashSet();
                if(denominator>0)
                {
                    low=100*matching.Length/denominator;var matchedSlots=matching.Select(a=>Slot(a.ExposureUtc)).ToHashSet();var unresolved=contiguous?unknownPaused.Count(s=>expected.Contains(s)&&!matchedSlots.Contains(s)):denominator-matching.Length;high=Math.Min(100,low.Value+100*unresolved/denominator);evidence=1-unresolved/denominator;
                    Metric("expected-acquisition-work",matching.Length,denominator,"acquisition opportunities","First required frozen-cadence slots in this research scope. Later easy repetitions cannot replace earlier missed work. Actual intents, including source-failed attempts, are counted once per slot. Session boundaries do not reset work.",(long)Math.Max(0,denominator-matching.Length),(long)Math.Ceiling(unresolved));
                    classes=matching.Length>0?["collection-intent"]:contiguous&&completed?["collection-intent"]:[];
                    reasons.Add($"{matching.Length}/{denominator} frozen-cadence research opportunities have actual acquisition intents. Session count, source success and extra out-of-scope samples cannot change this denominator.");
                    if(!contiguous||unknownPaused.Count>0)reasons.Add("Telemetry gaps or manual pause rationale are not sufficient evidence of pilot fault; unresolved work remains uncertain, not perfect or failed.");
                }
                else if(contiguous&&completed&&obligation.Scope is "region" or "planned-session"){Score(0,"collection-intent");reasons.Add("The frozen research region was never visited/collected during a continuously observed flight.");}
                var window=PlannedWindow(obligation.PlannedSessionKey);
                var closedPlannedWindow=obligation.Scope=="region"?allWindowsBounded:obligation.Scope=="planned-session"&&window.From.HasValue&&window.To.HasValue&&window.To>=window.From;
                if(obligation.Scope=="region"&&allWindowsBounded)reasons.Add("Study-region opportunities are bounded by the explicitly configured and observed route collection windows, not by later bonus science sessions or a polygon exit.");
                if(denominator>0&&obligation.TargetAmount is null&&!closedPlannedWindow)
                {
                    low=0;high=100;evidence=0;classes=[];
                    reasons.Add("This frozen scope has no finite acquisition target or completed explicit route/gate End bound. Observed-opportunity counts are factual diagnostics, not proof that all intended research was completed. Final discipline credit remains unassessed.");
                    recommendations.Add("Before departure, define a bounded flight acquisition duration or explicit Start/End route window. Later sessions cannot retroactively create a smaller intended-work denominator.");
                }
                reasons.Add("Provider outages/cloud/nodata affect factual yield, not acquisition-attempt discipline. Frozen route/boundary pauses and explicit pilot-declared safety pauses exclude their observed time only; frozen target amounts and omitted regions remain required.");recommendations.Add("Complete frozen research work; ending/restarting sessions does not erase outstanding obligations.");
            }
            else if(obligation.CriterionId=="operating")
            {
                var reports=operating.Where(r=>r.Policy=="hybrid-realistic-1"&&r.Configuration.Settings.InstrumentId==obligation.InstrumentId&&r.Input.Frame is {} f&&InScope(f,obligation)).ToArray();
                var rules=reports.SelectMany(r=>r.Assessment.Rules.Where(rule=>rule.Rule.Id is "geometry.ground-intersection" or "planning.forward-overlap" or "planning.side-overlap" or "riegl.conditional-range" or "aviris-ng.nominal-line-rate")).ToArray();
                var known=reports.Where(r=>r.Input.Frame is {} f&&!continuityAt.GetValueOrDefault(f.TimestampUtc)).SelectMany(r=>r.Assessment.Rules.Where(rule=>rule.Rule.Id is "geometry.ground-intersection" or "planning.forward-overlap" or "planning.side-overlap" or "riegl.conditional-range" or "aviris-ng.nominal-line-rate")).Where(r=>r.Compliance is AssessmentStatus.Supported or AssessmentStatus.Caution or AssessmentStatus.Invalid).ToArray();
                if(rules.Length>0){Metric("operating-assessments",known.Count(r=>r.Compliance==AssessmentStatus.Supported),rules.Length,"applicable rule outcomes","P04 documented geometry/overlap/reference rules. Unknown compliance remains unassessed; source QA and measurement accuracy are separate.",unassessed:rules.Length-known.Length);low=100d*known.Count(r=>r.Compliance==AssessmentStatus.Supported)/rules.Length;high=low+100d*(rules.Length-known.Length)/rules.Length;evidence=(double)known.Length/rules.Length;classes=known.Length>0?["operating-assessment"]:[];}
                reasons.Add($"{known.Count(r=>r.Compliance==AssessmentStatus.Supported)}/{rules.Length} applicable P04 rule outcomes supported; {rules.Length-known.Length} unknown. Source QA and unavailable payload temperature are not pilot noncompliance.");recommendations.Add("Review documented geometry/overlap constraints and unknown operating evidence.");
            }
            criteria.Add(new(obligation.Id,low,high,evidence,classes,reasons.Count>0?reasons.ToArray():["Necessary evidence is missing or the frozen research target cannot be assessed."],recommendations.ToArray(),[..refs,..actual.Where(a=>a.InstrumentId==obligation.InstrumentId).Take(32).SelectMany(a=>a.Evidence)],metrics.ToArray()));
        }
        var sessionReports=sessions.Select(session=>
        {
            var localYield=yield.Where(y=>y.SessionId==session.Id).ToArray();
            var localFrames=frames.Where(f=>f.TimestampUtc>=session.StartedUtc&&(session.EndedUtc is null||f.TimestampUtc<=session.EndedUtc)).ToArray();
            var localCriteria=(context?.Obligations.Where(o=>o.InstrumentId is null||session.Configurations.Any(c=>c.Settings.InstrumentId==o.InstrumentId))??[]).Select(o=>
            {
                double? score=null;string reason;
                if(o.CriterionId=="operating")
                {
                    var rules=operating.Where(r=>r.Policy=="hybrid-realistic-1"&&r.Assessment.SessionId==session.Id&&r.Configuration.Settings.InstrumentId==o.InstrumentId&&r.Input.Frame is {} f&&InScope(f,o)).SelectMany(r=>r.Assessment.Rules).Where(r=>r.Compliance is AssessmentStatus.Supported or AssessmentStatus.Caution or AssessmentStatus.Invalid).ToArray();
                    if(rules.Length>0)score=100d*rules.Count(r=>r.Compliance==AssessmentStatus.Supported)/rules.Length;
                    reason=$"Session-local documented operating outcomes: {rules.Count(r=>r.Compliance==AssessmentStatus.Supported)}/{rules.Length} assessed rules. Unknown constraints and source QA are separate.";
                }
                else if(o.CriterionId=="discipline")
                {
                    var config=context!.Configurations.First(c=>c.Settings.InstrumentId==o.InstrumentId).Settings;
                    long Slot(DateTimeOffset time)=>(long)Math.Floor((time-detail.Mission.StartedUtc).TotalSeconds/(config.IntervalMs/1000d)+1e-6);
                    var slots=localFrames.Where(f=>InScope(f,o)&&StateAt(f.TimestampUtc)?.To==CollectionPhase.Collecting).Select(f=>Slot(f.TimestampUtc)).Distinct().ToHashSet();
                    var count=actual.Where(a=>a.SessionId==session.Id&&a.InstrumentId==o.InstrumentId&&slots.Contains(Slot(a.ExposureUtc))).Select(a=>Slot(a.ExposureUtc)).Distinct().Count();
                    if(slots.Count>0)score=100d*count/slots.Count;
                    reason=$"Session collection-window attempts {count}/{slots.Count}; this diagnostic does not replace or shrink flight-wide obligations.";
                }
                else reason="Design and full route obligations belong to the continuous flight; see the overall breakdown. This session's positions/events and yield remain independently reviewable.";
                return new GradingCriterionResult(o.Id,o.CriterionId,score.HasValue?AssessmentStatus.Supported:AssessmentStatus.NotAssessed,score,[reason],[]);
            }).ToArray();
            return new SessionEvaluation(session,localYield,localCriteria,events.Where(e=>e.SessionId==session.Id).ToArray(),["Independent session evidence scope; closed sessions cannot erase outstanding flight-wide research."]);
        }).ToArray();
        ScientificSourceEvidence[] sources=[];
        try
        {
            var used=actual.SelectMany(a=>a.Evidence).Where(e=>e.StartsWith("source:")).Select(e=>Guid.Parse(e[7..])).ToHashSet();
            sources=science.AllProducts(mission).Where(p=>used.Contains(p.SourceId)).DistinctBy(p=>p.SourceId).Select(p=>science.ReadProduct(p.Id).Source).Select(s=>new ScientificSourceEvidence(s.Id,s.Manifest.Provider,s.Manifest.Name,s.Manifest.SceneId,s.Manifest.ObservationUtc,s.ImportedUtc,s.Manifest.VerticalDatum,s.ManifestSha256,
                s.Manifest.Bands.Select(b=>new ScientificBandEvidence(b.Id,b.Unit,b.Scale,b.Offset,b.NoData,b.WavelengthNm,s.Grids.GetValueOrDefault(b.Id))).ToArray(),["Historical external observations, not measurements by the simulated aircraft. Native source QA and georeferencing remain authoritative."])).ToArray();
        }
        catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException){limitations.Add("Saved source snapshots unavailable at this evaluation; earlier report revisions remain retained.");}
        return new(1,mission,at,completed,context,criteria.ToArray(),yield.Where(y=>y.SessionId is null).ToArray(),sessionReports,
            ["telemetry-sha256:"+PreflightReadiness.Hash(archivedFrames),"collection-events-sha256:"+PreflightReadiness.Hash(events),"operating-sha256:"+PreflightReadiness.Hash(operating),"science-results-sha256:"+PreflightReadiness.Hash(actual)],limitations.ToArray(),sources,actual.Length==0?null:actual.Max(a=>a.ProcessedUtc??a.ExposureUtc),detail.Mission.Source,detail.Mission.Status==MissionStatus.Interrupted);
    }
}
