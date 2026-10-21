using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Backend-only, bounded decisions from witnessed packets. No client/map polling and no
// interpolation of scientific observations. WGS84 geodesy and existing polygon tests.
public sealed class GeographicCollectionEngine(MissionPlan mission,ScienceCollectionController controller)
{
    readonly CollectionPlan plan=mission.Collection??throw new ArgumentException("No collection plan.");
    readonly Dictionary<string,bool> visitsInside=[];
    readonly Dictionary<string,int> passes=[];
    readonly Dictionary<Guid,Region> regions=[];
    CollectionInputReceipt? previous;
    bool disconnected,hasBaseline,resetByPilot;
    bool? unionInside;
    sealed class Region
    {
        public bool? Inside,Candidate;public DateTimeOffset Since;
        public CollectionInputReceipt[] Evidence=[];
    }
    sealed record Decision(double Fraction,CollectionTrigger Trigger,CollectionInputReceipt[] Inputs,bool Initial=false);
    EvidenceReference Policy=>new("collection-plan",plan.Version,EvidenceContracts.ContentHash(plan));
    static Coordinates Point(TelemetryFrame f)=>new(f.Latitude,f.Longitude);
    static Guid Key(string value)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    public void Rearm(){previous=null;regions.Clear();visitsInside.Clear();unionInside=null;hasBaseline=false;disconnected=false;resetByPilot=true;}
    public void Observe(TelemetryUpdate update,DateTimeOffset evaluatedUtc)
    {
        if(update.Connection.State!=ConnectionState.Connected)
        {
            if(hasBaseline&&!disconnected)Hold("Telemetry disconnected; automation requires fresh evidence and explicit rearm.",[],evaluatedUtc);
            disconnected=true;previous=null;return;
        }
        if(update.Frame is not {} frame)return; // auxiliary packets never advance geometry
        var current=new CollectionInputReceipt("geographic:"+Guid.NewGuid().ToString("N"),update);
        var before=previous?.Packet.Frame;
        if(before is not null&&frame.TimestampUtc==before.TimestampUtc&&frame.Latitude==before.Latitude&&frame.Longitude==before.Longitude&&frame.IsPaused==before.IsPaused)return;
        if(!frame.IsValid||frame.IsPaused||!MissionPlanning.Position(frame.Latitude,frame.Longitude)||Math.Abs(frame.Latitude)>80||
            (evaluatedUtc-frame.TimestampUtc).TotalSeconds is var age&&(age< -2||age>plan.Continuity.MaximumPositionAgeSeconds))
        {Hold("Paused, stale or unsupported position evidence; no geographic crossing was inferred.",frame.IsValid?[current]:[],frame.IsValid?frame.TimestampUtc:evaluatedUtc);return;}
        var delta=before is null?0:(frame.TimestampUtc-before.TimestampUtc).TotalSeconds;
        if(before is not null&&(delta<=0||delta>plan.Continuity.MaximumGapSeconds||
            Geodesy.Distance(Point(before),Point(frame))>Math.Max(300,Math.Max(before.GroundspeedKnots,frame.GroundspeedKnots)*.514444*delta*3+100)))
        {Hold("Position order, gap or displacement is discontinuous (including slew/jumps); rearm to establish a new baseline.",[previous!,current],frame.TimestampUtc);return;}
        if(disconnected||controller.State.ContinuityHold){previous=current;hasBaseline=true;return;}
        var decisions=new List<Decision>();
        var inputs=previous is null?new[]{current}:new[]{previous,current};
        foreach(var group in plan.Triggers.Where(t=>t.Kind is CollectionTriggerKind.Waypoint or CollectionTriggerKind.DirectionalGate)
            .GroupBy(t=>(t.Kind,t.TargetId,t.DirectionTrueDegrees)))
        {
            var representative=group.First();var target=Geodesy.Point(mission.Waypoints!.Single(w=>w.Id==representative.TargetId));
            var groupId=$"{representative.Kind}:{representative.TargetId}:{representative.DirectionTrueDegrees}";double? crossing=null;
            if(representative.Kind==CollectionTriggerKind.Waypoint)
            {
                var distance=Geodesy.Distance(Point(frame),target);var inside=visitsInside.GetValueOrDefault(groupId);
                if(distance<=representative.ToleranceMetres&&!inside&&(!resetByPilot||hasBaseline))crossing=1;
                visitsInside[groupId]=inside?distance<=representative.ToleranceMetres+Math.Max(2,representative.ToleranceMetres*.1):distance<=representative.ToleranceMetres;
            }
            else if(before is not null)
            {
                (double Along,double Across) Project(TelemetryFrame f){var distance=Geodesy.Distance(target,Point(f));var angle=(Geodesy.Bearing(target,Point(f))-representative.DirectionTrueDegrees!.Value)*Math.PI/180;return(distance*Math.Cos(angle),distance*Math.Sin(angle));}
                var a=Project(before);var b=Project(frame);
                if(a.Along< -0.01&&b.Along>=0){var fraction=-a.Along/(b.Along-a.Along);if(Math.Abs(a.Across+(b.Across-a.Across)*fraction)<=representative.ToleranceMetres)crossing=fraction;}
            }
            if(crossing is {} at)
            {
                var pass=passes.GetValueOrDefault(groupId)+1;passes[groupId]=pass;
                foreach(var trigger in group.Where(t=>t.Pass==pass))decisions.Add(new(at,trigger,inputs));
            }
        }
        foreach(var identity in plan.SurveyAreas??[])
        {
            var polygon=mission.SurveyAreas![identity.PlanAreaIndex].Vertices;var point=Point(frame);
            var raw=MissionAnalysis.Inside(point,polygon);
            var edge=Enumerable.Range(0,polygon.Length).Min(i=>MissionAnalysis.SegmentDistance(point,polygon[i],polygon[(i+1)%polygon.Length],
                Geodesy.Distance(polygon[i],polygon[(i+1)%polygon.Length]),Geodesy.Bearing(polygon[i],polygon[(i+1)%polygon.Length])));
            var hysteresis=plan.Boundary?.HysteresisMetres??5;var dwell=plan.Boundary?.DwellSeconds??0;
            if(!regions.TryGetValue(identity.Id,out var region))
            {
                regions[identity.Id]=region=new();
                if(edge>hysteresis&&(!raw||!plan.Triggers.Any(t=>t.TargetId==identity.Id&&t.Action==CollectionAction.StartData&&t.InitialInside==InitialInsidePolicy.StartWhenInitiallyInside)))
                {region.Inside=raw;region.Candidate=raw;continue;}
            }
            if(edge<=hysteresis){region.Candidate=null;continue;} // grazing does not count
            if(region.Candidate!=raw){region.Candidate=raw;region.Since=frame.TimestampUtc;region.Evidence=inputs;}
            if(region.Inside==raw||(frame.TimestampUtc-region.Since).TotalSeconds<dwell)continue;
            var initial=region.Inside is null;region.Inside=raw;
            var evidence=region.Evidence.Concat(new[]{current}).DistinctBy(i=>i.Id).ToArray();
            foreach(var group in plan.Triggers.Where(t=>t.TargetId==identity.Id&&t.Kind==(raw?CollectionTriggerKind.AreaEntry:CollectionTriggerKind.AreaExit)).GroupBy(t=>t.Kind))
            {
                var groupId=$"area:{identity.Id}:{raw}";
                if(initial&&!raw)continue;
                if(initial&&!group.Any(t=>t.Action==CollectionAction.StartData&&t.InitialInside==InitialInsidePolicy.StartWhenInitiallyInside))continue;
                var pass=passes.GetValueOrDefault(groupId)+1;passes[groupId]=pass;
                foreach(var trigger in group.Where(t=>t.Pass==pass&&(!initial||t.Action==CollectionAction.StartData&&t.InitialInside==InitialInsidePolicy.StartWhenInitiallyInside)))
                    decisions.Add(new(1,trigger,evidence,initial));
            }
        }
        // Crossing fraction orders separate gates; ties give End/Pause precedence.
        var endedAt=new HashSet<double>();var pausedAt=new HashSet<double>();
        foreach(var decision in decisions.OrderBy(d=>d.Fraction).ThenBy(d=>Priority(d.Trigger.Action)).ThenBy(d=>d.Trigger.Id))
        {
            var trigger=decision.Trigger;var state=controller.State;
            if((state.FiredOccurrenceIds??[]).Contains(trigger.OccurrenceId)||(state.SuppressedOccurrenceIds??[]).Contains(trigger.OccurrenceId))continue;
            if((state.FiredOccurrenceIds?.Length??0)+(state.SuppressedOccurrenceIds?.Length??0)>=EvidenceContracts.MaximumOccurrences-2)
            {Hold("Automatic receipt budget reached; retained history preserved. Use manual collection.",[current],frame.TimestampUtc);break;}
            var suppression=trigger.PlannedSessionKey is {} target&&trigger.Action!=CollectionAction.StartData&&controller.Session?.PlannedSessionKey!=target?"Action targets a different planned science session; it was not redirected.":null;
            if(endedAt.Contains(decision.Fraction)&&trigger.Action is CollectionAction.StartData or CollectionAction.ResumeData)suppression="Coincident End takes priority; a later distinct Start remains possible.";
            if(pausedAt.Contains(decision.Fraction)&&trigger.Action==CollectionAction.ResumeData)suppression="Coincident Pause takes priority over Resume.";
            var holds=state.Holds??[];
            if(trigger.Action==CollectionAction.PauseData&&state.CurrentSessionId is {} session)holds=[..holds,new(CollectionHoldKind.Route,"Explicit route pause",session,trigger.Id)];
            if(trigger.Action==CollectionAction.ResumeData)holds=holds.Where(h=>h.Kind!=CollectionHoldKind.Route).ToArray();
            var result=controller.Control(new(1,state.MissionId,Guid.NewGuid(),state.Revision,trigger.Action,state.CurrentSessionId),frame.TimestampUtc,
                CollectionEventOrigin.RouteTrigger,trigger.OccurrenceId,decision.Inputs,trigger.PlannedSessionKey,holds,Policy,suppression);
            if(result.Event.Outcome==CollectionEventOutcome.Applied&&trigger.Action==CollectionAction.EndData)endedAt.Add(decision.Fraction);
            if(result.Event.Outcome==CollectionEventOutcome.Applied&&trigger.Action==CollectionAction.PauseData)pausedAt.Add(decision.Fraction);
        }
        ApplyBoundary(current);
        previous=current;hasBaseline=true;resetByPilot=false;
    }
    static int Priority(CollectionAction action)=>action switch{CollectionAction.EndData=>0,CollectionAction.PauseData=>1,CollectionAction.StartData=>2,_=>3};
    void ApplyBoundary(CollectionInputReceipt current)
    {
        if(plan.Boundary is not {} boundary)return;
        var memberships=boundary.SurveyAreaIds.Select(id=>regions.GetValueOrDefault(id)?.Inside).ToArray();
        bool? inside=memberships.Any(v=>v==true)?true:memberships.All(v=>v==false)?false:null;
        if(inside is null)return;var was=unionInside;unionInside=inside;if(was is null||was==inside)return;
        var state=controller.State;if(state.CurrentSessionId is not {} session)return;
        var action=inside.Value?CollectionAction.ResumeData:CollectionAction.PauseData;
        if(inside.Value?!boundary.ResumeOnEntry:!boundary.PauseOnExit)return;
        if((state.FiredOccurrenceIds?.Length??0)+(state.SuppressedOccurrenceIds?.Length??0)>=EvidenceContracts.MaximumOccurrences-2)
        {Hold("Automatic receipt budget reached; use manual collection.",[current],current.Packet.Frame!.TimestampUtc);return;}
        var cause=Key("boundary-policy:"+plan.MissionPlanId);
        if(inside.Value&&!(state.Holds??[]).Any(h=>h.Kind==CollectionHoldKind.Boundary&&h.CauseId==cause&&h.SessionId==session))return;
        var holds=(state.Holds??[]).Where(h=>h.Kind!=CollectionHoldKind.Boundary||h.CauseId!=cause).ToArray();
        if(!inside.Value)holds=[..holds,new(CollectionHoldKind.Boundary,"Outside all configured research areas",session,cause)];
        var inputs=boundary.SurveyAreaIds.SelectMany(id=>regions.GetValueOrDefault(id)?.Evidence??[]).Concat(new[]{current}).DistinctBy(i=>i.Id).Take(63).ToArray();
        var occurrence=Key($"boundary:{session}:{action}:{current.Packet.Frame!.TimestampUtc:O}");
        controller.Control(new(1,state.MissionId,Guid.NewGuid(),state.Revision,action,session),current.Packet.Frame.TimestampUtc,
            CollectionEventOrigin.Boundary,occurrence,inputs,automaticHolds:holds,policy:Policy);
    }
    void Hold(string reason,CollectionInputReceipt[] inputs,DateTimeOffset at)
    {
        controller.ContinuityHold(reason,inputs,at);previous=null;regions.Clear();visitsInside.Clear();unionInside=null;hasBaseline=false;resetByPilot=true;
    }
}
