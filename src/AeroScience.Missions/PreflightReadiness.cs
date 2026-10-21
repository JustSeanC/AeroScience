using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

// A snapshot of local facts, not a provider search or a claim of valid future pixels.
public sealed record ReadinessEnvironment(SourceAvailability[] Sources, ScientificAccessPreferences Access,
    bool FlightStorageAvailable, bool ScienceStorageAvailable, long? FlightFreeBytes, long? ScienceFreeBytes,
    long? FlightRemainingBytes, long? ScienceRemainingBytes, string[] Limitations);

public static class PreflightReadiness
{
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, WireJson.Options))));
    public static ReadinessAssessment Evaluate(MissionPlan draft, ReadinessEnvironment environment, DateTimeOffset now)
    {
        if ((draft.Waypoints?.Length??0)>1000 || (draft.SurveyAreas?.Length??0)>20 || (draft.Instruments?.Length??0)>8)
            throw new ArgumentException("Readiness supports up to 1000 waypoints, 20 survey areas and eight instruments.");
        var checks=new List<ReadinessCheck>();
        void Add(string id,string category,ReadinessStatus status,string reason,string action="",params string[] inputs) =>
            checks.Add(new(id,status,[reason],string.IsNullOrEmpty(action)?[]:[action],category,inputs));
        var route=draft.Waypoints??[];var areas=draft.SurveyAreas??[];
        Add("design.objective","Mission design",string.IsNullOrWhiteSpace(draft.Objective)?ReadinessStatus.Caution:ReadinessStatus.Ready,
            string.IsNullOrWhiteSpace(draft.Objective)?"No research objective is specified.":"An explicit research objective is present; feasibility is assessed by the checks below.","Describe the intended research outcome.","template="+draft.Template,"objective="+(draft.Objective??"Missing"));
        try { _=MissionPlanning.Validate(draft with{Name=string.IsNullOrWhiteSpace(draft.Name)?"Unsaved draft":draft.Name});
            Add("design.validation","Mission design",ReadinessStatus.Ready,"Plan fields and geographic references satisfy current structural validation."); }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or NotSupportedException)
        { Add("design.validation","Mission design",ReadinessStatus.Caution,e.Message,"Correct the reported plan fields; incomplete drafts may still be retained."); }
        Add("design.route","Mission design",route.Length>=2?ReadinessStatus.Ready:ReadinessStatus.Caution,
            $"{route.Length} ordered waypoints; repeated coordinates represent distinct planned occurrences, not automatically completed work.","Add ordered departure/research/return points where appropriate.","waypoints="+route.Length);
        var geographic=draft.Template==MissionTemplateKind.GeographicSurvey;
        foreach(var area in areas)
            if(area.Vertices.Length>1000)throw new ArgumentException("Bounded readiness supports 1000 vertices per area.");
        var validArea=areas.Length>0&&areas.All(a=>a.Vertices.Length>=3&&a.Vertices.All(p=>MissionPlanning.Position(p.Latitude,p.Longitude)));
        Add("design.survey","Mission design",geographic&&!validArea?ReadinessStatus.Caution:ReadinessStatus.Ready,
            geographic? $"{areas.Length} planned research regions; source-valid coverage is not inferred from polygons.":"This template does not require polygon overlap.",geographic?"Define a bounded, valid survey polygon.":"","regions="+areas.Length);
        var outside=route.Where(w=>w.TargetAltitudeFeet is {} altitude&&(altitude<draft.MinimumAltitudeFeet||altitude>draft.MaximumAltitudeFeet)).ToArray();
        if(outside.Length>0)Add("design.altitude-targets","Mission design",ReadinessStatus.Caution,"Waypoint altitude targets contradict the stated mission MSL range.","Reconcile waypoint targets with the research altitude profile.","waypoints="+string.Join(",",outside.Take(20).Select(w=>w.Name))+"; total="+outside.Length);
        Add("design.altitude","Mission design",draft.MinimumAltitudeFeet>draft.MaximumAltitudeFeet?ReadinessStatus.Caution:
            draft.MinimumAltitudeFeet is null&&draft.MaximumAltitudeFeet is null&&draft.CruisingAltitudeFeet is null&&route.All(w=>w.TargetAltitudeFeet is null)?ReadinessStatus.NotYetAssessed:ReadinessStatus.Ready,
            "Planned MSL limits and waypoint targets are not AGL or terrain-clearance guarantees.","Resolve reversed limits and specify the research altitude profile.",
            $"minimumFtMSL={draft.MinimumAltitudeFeet}; maximumFtMSL={draft.MaximumAltitudeFeet}; cruiseFtMSL={draft.CruisingAltitudeFeet}");
        InstrumentConfiguration[] configurations=[];
        try { configurations=ScienceCatalog.Normalize(draft.Instruments).Where(c=>c.Enabled).ToArray(); }
        catch(ArgumentException e){Add("instruments.validation","Instruments",ReadinessStatus.Unavailable,e.Message,"Choose supported configurations and a 1–60 second cadence.");}
        Add("instruments.selection","Instruments",configurations.Length==0?ReadinessStatus.Caution:ReadinessStatus.Ready,$"{configurations.Length} instruments enabled.","Select instruments suited to the research objective.");
        foreach(var config in configurations)
        {
            var calibration=InstrumentCalibration.Describe(config.InstrumentId);
            Add("calibration."+config.InstrumentId,"Instruments",config.Calibration?.Status==CalibrationInformationStatus.UserDefined?ReadinessStatus.Caution:ReadinessStatus.NotYetAssessed,
                "Calibration information: "+(config.Calibration??calibration.Reference).Status+". "+string.Join(" ",(config.Calibration??calibration.Reference).Limitations),
                "Review documented reference limitations; no physical accuracy certificate is inferred.","intervalMs="+config.IntervalMs);
            if(!RemoteAcquisition.Instruments.TryGetValue(config.InstrumentId,out var kind))continue;
            // Reuse P04's pure rules with explicitly absent actual telemetry. Planned geometry
            // is evaluated by the existing sensor assistant, never forged as a measured packet.
            var effective=new EffectiveInstrumentConfiguration(Guid.NewGuid(),1,now,config,config.Calibration);
            var operating=InstrumentOperatingAssessment.Evaluate(Guid.NewGuid(),null,effective,draft.Mode,
                new(new(ConnectionState.Disconnected,TelemetrySource.Simulator,"Preflight: no actual flight observation",now),null),draft,evaluatedUtc:now);
            Add("operating."+config.InstrumentId,"Instruments",ReadinessStatus.NotYetAssessed,
                "P04 operating policy: "+string.Join(" ",operating.Assessment.Rules.Where(r=>r.Parameter is "payload operating temperature" or "AGL / ray-plane intersection").SelectMany(r=>r.Reasons)),
                "Review AGL, orientation, mounting assumptions and documented limits; actual compliance requires flight evidence.");
            var sources=environment.Sources.Where(s=>s.Source.Manifest.Kind==kind && !(environment.Access.ExcludedSourceIds??[]).Contains(s.Source.Id)).ToArray();
            var points=areas.SelectMany(a=>a.Vertices).Concat(route.Select(w=>new Coordinates(w.Latitude,w.Longitude))).Take(2048).ToArray();
            var candidates=sources.Where(s=>s.FilesAvailable).ToArray();
            var covered=points.Length>0&&points.All(p=>candidates.Any(s=>ScientificSourceSelection.CoversArea(s.Source,new(p.Longitude,p.Latitude,p.Longitude,p.Latitude))));
            Add("source."+config.InstrumentId,"Scientific sources",!environment.ScienceStorageAvailable?ReadinessStatus.Unavailable:
                covered?ReadinessStatus.Caution:environment.Access.OnlineEnabled&&environment.Access.PrivacyAccepted?ReadinessStatus.NotYetAssessed:ReadinessStatus.Unavailable,
                $"{candidates.Length} accessible local source candidates; planned boundary coverage {(covered?"matches candidate extents":"is not established")}. Extents/catalogs do not prove QA-valid pixels. Online consent: {environment.Access.OnlineEnabled&&environment.Access.PrivacyAccepted}.",
                "Use the separate explicit preparation workflow, then review source dates, masks and actual coverage.",
                "sources="+string.Join(",",candidates.Take(12).Select(s=>$"{s.Source.Id}:{s.Source.Manifest.SceneId}:{s.Source.Manifest.ObservationUtc:O}")));
        }
        if(draft.SensorPlanning is {} planning)
        {
            try { var config=configurations.FirstOrDefault(c=>c.InstrumentId==planning.InstrumentId)??throw new ArgumentException("Planned sensor is not enabled.");
                var assistant=InstrumentProfiles.Plan(new(config,planning.AltitudeAglMetres,planning.GroundspeedMetresPerSecond,planning.SideOverlap,planning.ForwardOverlap));
                Add("instruments.geometry","Instruments",ReadinessStatus.Caution,JsonSerializer.Serialize(assistant,WireJson.Options),"Review modeled swath/spacing versus saved pattern track spacing. Flat-plane geometry is not valid pixel coverage.");
                Add("instruments.cadence","Instruments",assistant.RecommendedIntervalSeconds is null?ReadinessStatus.NotYetAssessed:config.IntervalMs/1000d>assistant.RecommendedIntervalSeconds?ReadinessStatus.Caution:ReadinessStatus.Ready,
                    "Configured receipt interval versus modeled forward-overlap recommendation, not continuous detector sampling.","Adjust cadence or speed if the modeled target is missed.",$"intervalSeconds={config.IntervalMs/1000d}; recommendedSeconds={assistant.RecommendedIntervalSeconds}");
                var spacing=(draft.Patterns??[]).Where(p=>p.Kind is FlightPatternKind.Lawnmower or FlightPatternKind.CreepingLine).Select(p=>p.SpacingMetres).ToArray();
                Add("instruments.track-spacing","Instruments",spacing.Length==0?ReadinessStatus.NotYetAssessed:spacing.Any(s=>s>assistant.TrackSpacingMetres)?ReadinessStatus.Caution:ReadinessStatus.Ready,
                    "Saved parallel-track spacing versus modeled side-overlap target; no valid-data coverage guarantee.","Review track spacing and actual source resolution.",$"recommendedMetres={assistant.TrackSpacingMetres}; savedMetres={string.Join(",",spacing)}"); }
            catch(Exception e)when(e is ArgumentException or InvalidOperationException){Add("instruments.geometry","Instruments",ReadinessStatus.Unavailable,e.Message,"Correct planned AGL, speed and supported sensor geometry.");}
        }
        else Add("instruments.geometry","Instruments",ReadinessStatus.NotYetAssessed,"No planned AGL/groundspeed/overlap model is supplied.","Use the existing sensor planning assistant when geographic overlap is relevant.");
        foreach(var (id,available,free,remaining) in new[]{("flight",environment.FlightStorageAvailable,environment.FlightFreeBytes,environment.FlightRemainingBytes),("science",environment.ScienceStorageAvailable,environment.ScienceFreeBytes,environment.ScienceRemainingBytes)})
            Add("storage."+id,"Storage",!available||free<67108864||remaining<=0?ReadinessStatus.Unavailable:free is null?ReadinessStatus.NotYetAssessed:ReadinessStatus.Caution,
                "Current capacity is checked; future workload/transfer size is unknown, so successful completion is not guaranteed.","Review quota/free space and the separate acquisition budget preview.","available="+available,"freeBytes="+free,"remainingQuotaBytes="+remaining);
        if(draft.Collection is {} collection)
        {
            try{CollectionPlanning.Validate(draft,collection);Add("automation.references","Collection automation",ReadinessStatus.Ready,$"{collection.Triggers.Length} explicit actions; geometry, stable targets and occurrence identities validate.");}
            catch(Exception e)when(e is ArgumentException or InvalidOperationException or NotSupportedException){Add("automation.references","Collection automation",ReadinessStatus.Unavailable,e.Message,"Refresh/review automation targets or use manual collection.");}
            var pointActions=collection.Triggers.Where(t=>t.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate).ToArray();
            foreach(var trigger in pointActions.Where(t=>t.Pass>1))
            {
                var target=route.FirstOrDefault(w=>w.Id==trigger.TargetId);
                var occurrences=target is null?0:route.Count(w=>MissionPlanning.Position(w.Latitude,w.Longitude)&&Geodesy.Distance(new(w.Latitude,w.Longitude),new(target.Latitude,target.Longitude))<=trigger.ToleranceMetres);
                if(occurrences<trigger.Pass)Add("automation.reachability."+trigger.Id,"Collection automation",ReadinessStatus.Caution,$"Configured pass {trigger.Pass} has only {occurrences} explicit nearby route occurrences; additional gate/route crossings are unverified.","Add the intended return/repeated pass or review this as a manual/conditional action.","trigger="+trigger.Id);
            }
            foreach(var group in pointActions.Where(t=>t.PlannedSessionKey is not null&&t.Pass==1).GroupBy(t=>t.PlannedSessionKey))
            {
                var start=group.FirstOrDefault(t=>t.Action==CollectionAction.StartData);var end=group.FirstOrDefault(t=>t.Action==CollectionAction.EndData);
                var startIndex=start is null?-1:Array.FindIndex(route,w=>w.Id==start.TargetId);var endIndex=end is null?-1:Array.FindIndex(route,w=>w.Id==end.TargetId);
                if(startIndex>=0&&endIndex>=0&&endIndex<=startIndex)Add("automation.order."+start!.Id,"Collection automation",ReadinessStatus.Caution,"Configured End is at or before Start in the planned waypoint order for session "+group.Key,"Move the explicit End after Start or document the intended repeated/directional crossing; execution is not assumed.","startIndex="+startIndex,"endIndex="+endIndex);
            }
            var starts=collection.Triggers.Count(t=>t.Action==CollectionAction.StartData);var ends=collection.Triggers.Count(t=>t.Action==CollectionAction.EndData);
            Add("automation.sessions","Collection automation",ReadinessStatus.Caution,
                $"{starts} configured Starts and {ends} optional explicit Ends. Last waypoint and polygon exit never imply End. Route order, repeated passes, entry direction and actual reachability require pilot review; boundary re-entry resumes the same session.",
                "Review each planned session key/action in flight order. Manual End suspends automatic Starts until explicit rearm; manual Start remains available.");
            var missingStarts=collection.Triggers.Where(t=>t.PlannedSessionKey is not null).GroupBy(t=>t.PlannedSessionKey).Where(g=>!g.Any(t=>t.Action==CollectionAction.StartData)&&g.Any(t=>t.Action==CollectionAction.EndData)).ToArray();
            if(missingStarts.Length>0)Add("automation.sequence.missing-starts","Collection automation",ReadinessStatus.Caution,$"{missingStarts.Length} planned session keys have End without a configured Start.","Add an explicit Start or document intended manual Starts; inspect the referenced action IDs.",missingStarts.Select(g=>"endTrigger="+g.First().Id).ToArray());
        }
        else Add("automation.manual","Collection automation",ReadinessStatus.Ready,"Manual collection selected. Start/End science and End Flight remain independent actions.");
        var meaningful=!string.IsNullOrWhiteSpace(draft.Objective)&&configurations.Length>0&&(route.Length>0||validArea||draft.EstimatedDurationMinutes>0||draft.MinimumAltitudeFeet<draft.MaximumAltitudeFeet);
        Add("grading.obligations","Grading",draft.Mode==ScienceMode.Universal?ReadinessStatus.Ready:meaningful?ReadinessStatus.Caution:ReadinessStatus.NotYetAssessed,
            draft.Mode==ScienceMode.Universal?"Universal missions expose factual yield without Realistic pilot grades.":meaningful?"Research inputs can define frozen obligations. Actual grade eligibility still requires applicable archived evidence.":"Insufficient planned research obligations for a meaningful grade.","Specify objective, instruments and bounded research work before departure.");
        if(draft.Mode==ScienceMode.Realistic)
        {
            var numericBound=draft.EstimatedDurationMinutes>0&&draft.SurveyAreas is not {Length:>0}&&!(draft.Collection?.Triggers.Any(t=>t.Action==CollectionAction.StartData&&t.PlannedSessionKey is not null)??false);
            Add("grading.work-bound","Grading",numericBound?ReadinessStatus.Caution:ReadinessStatus.NotYetAssessed,
                numericBound?"Declared flight acquisition duration freezes the first required cadence opportunities. Easy later repetitions cannot replace earlier missed work.":"Research scopes need finite intended work or a completed explicit Start/End route window for final acquisition-discipline credit. Observed samples alone cannot prove completeness.",
                "Define the intended acquisition duration for a flight-wide study, or explicit Start/End gates for planned sessions. Unbounded scopes remain honestly unassessed; incomplete drafts can still be saved.");
        }
        var result=new ReadinessAssessment(1,Hash(draft),now,checks.ToArray(),["Readiness is advisory and expires after 60 seconds. No save, recording, collection or download occurs.",..environment.Limitations],Hash(environment),now.AddSeconds(60));
        EvidenceContracts.Validate(result);return result;
    }
}
