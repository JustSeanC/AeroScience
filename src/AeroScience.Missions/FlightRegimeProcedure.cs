using AeroScience.Shared;
namespace AeroScience.Missions;

public static partial class FlightRegimeEvaluation
{
    public const string ProcedureTemplate="custom-flight-procedure",ProcedureVersion="flight-procedure-1";
    public static void ValidateProcedure(FlightTestProcedureDefinition? procedure)
    {
        if(procedure is not {Version:1,Points.Length:>=2 and <=4}||procedure.Extensions is {Count:>0}
            ||procedure.Points.Any(p=>p is null||p.Id==Guid.Empty||p.Extensions is {Count:>0}||p.Repetitions is <1 or >5)
            ||procedure.Points.Select(p=>p.Id).Distinct().Count()!=procedure.Points.Length)
            throw new ArgumentException("Use 2–4 distinct named test points, each requiring 1–5 separately marked attempts. Unsupported procedure fields or versions cannot be saved.");
        foreach(var point in procedure.Points){
            if(point.Conditions?.TestPoint is not {} definition||definition.Policy is not ("all" or "any"))throw new ArgumentException("Choose IAS and/or MSL with AND or OR for each point.");
            Validate(point.Conditions with{TestPoint=definition with{Policy="all"}});
        }
    }

    // A bounded composition of the proven Window, not another telemetry evaluator.
    // OR chooses one whole supported signal hold; alternating signals never form a hold.
    public sealed class PointWindow
    {
        readonly FlightRegimeDefinition rule;readonly Window combined;
        readonly List<(string Signal,Window Window)> branches=[];
        public PointWindow(FlightRegimeDefinition rule,DateTimeOffset start,DateTimeOffset end)
        {
            this.rule=rule;var all=rule with{TestPoint=rule.TestPoint! with{Policy="all"}};
            combined=new(all,start,end);
            if(RequiresAirspeed(rule))branches.Add(("indicated-airspeed/knots-IAS",new(all with{TestPoint=all.TestPoint! with{AltitudeRequired=false}},start,end)));
            if(RequiresAltitude(rule))branches.Add(("altitude/feet-MSL",new(all with{TestPoint=all.TestPoint! with{AirspeedRequired=false}},start,end)));
        }
        public void Add(TelemetryFrame frame){combined.Add(frame);foreach(var b in branches)b.Window.Add(frame);}
        public void Break(){combined.Break();foreach(var b in branches)b.Window.Break();}
        public FlightTestPointEvidence Finish(Guid pointId,ResearchEvidenceReference evidence,bool interrupted)
        {
            var conditions=branches.Select(b=>{var r=b.Window.Finish(interrupted);return new FlightTestConditionEvidence(b.Signal,r.Outcome,b.Window.LongestHold,r.Explanation);}).ToArray();
            var chosen=combined;var result=combined.Finish(interrupted);
            if(rule.TestPoint!.Policy=="any"){
                var pass=branches.FirstOrDefault(b=>b.Window.Finish(interrupted).Outcome==ResearchAttemptOutcome.Pass);
                chosen=pass.Window??branches.OrderByDescending(b=>b.Window.LongestHold).First().Window;
                var outcome=conditions.Any(c=>c.Outcome==ResearchAttemptOutcome.Pass)?ResearchAttemptOutcome.Pass
                    :conditions.All(c=>c.Outcome==ResearchAttemptOutcome.Fail)?ResearchAttemptOutcome.Fail:ResearchAttemptOutcome.InsufficientEvidence;
                result=(outcome,"OR requires one selected signal to sustain its entire hold on supported original evidence. Unsupported branches remain distinct from measured failures; signals cannot alternate to manufacture a hold.");
            }
            return new(pointId,result.Outcome,chosen.LongestHold,chosen.HoldStart,chosen.HoldEnd,evidence,conditions,result.Explanation);
        }
    }
}

// Constant-size aggregate over explicit attempts. No automatic interval discovery.
internal sealed class FlightProcedureProgress(FlightTestProcedureDefinition definition)
{
    readonly int[] completed=new int[definition.Points.Length];
    readonly double[] holds=new double[definition.Points.Length];
    readonly bool[] failed=new bool[definition.Points.Length];
    readonly string[] reasons=definition.Points.Select(_=>"No qualifying marked attempt. Missing repetitions remain Insufficient Evidence.").ToArray();
    DateTimeOffset? lastQualifiedEnd;TelemetrySource? qualifiedSource;string? qualifiedAircraft;long count;
    public (bool Qualified,string Reason) Add(RecordedResearchAttempt attempt)
    {
        count++;var i=Array.FindIndex(definition.Points,p=>p.Id==attempt.TestPointId);if(i<0)return(false,"Unknown point identity cannot qualify.");
        var evidence=attempt.PointEvidence;if(evidence is null){reasons[i]="Open or interrupted attempt has no qualifying complete evidence.";return(false,reasons[i]);}
        holds[i]=Math.Max(holds[i],evidence.LongestHoldSeconds);failed[i]|=evidence.Outcome==ResearchAttemptOutcome.Fail;
        var compatible=evidence.Evidence.Source is not null&&!string.IsNullOrWhiteSpace(evidence.Aircraft)&&(qualifiedSource is null||qualifiedSource==evidence.Evidence.Source&&qualifiedAircraft==evidence.Aircraft);
        var eligible=compatible&&(!definition.Ordered||Enumerable.Range(0,i).All(j=>completed[j]>=definition.Points[j].Repetitions))
            &&(lastQualifiedEnd is null||attempt.StartUtc>lastQualifiedEnd);
        if(evidence.Outcome==ResearchAttemptOutcome.Pass&&attempt.Status==ResearchAttemptStatus.Recorded&&eligible&&completed[i]<definition.Points[i].Repetitions){
            completed[i]++;lastQualifiedEnd=attempt.EndUtc;qualifiedSource??=evidence.Evidence.Source;qualifiedAircraft??=evidence.Aircraft;reasons[i]="Distinct marked interval qualified in the required order. Earlier failed/interrupted attempts remain in history.";return(true,reasons[i]);
        }else reasons[i]=!compatible?"This point has missing or different original source/aircraft identity; it cannot be combined with prior qualifying repetitions.":!eligible?"This interval cannot qualify: required earlier points were incomplete or it overlaps an already counted interval. It is not retroactively qualified.":completed[i]>=definition.Points[i].Repetitions?"Required repetitions already complete; this extra attempt is preserved but is not counted again.":evidence.Explanation;
        return(false,reasons[i]);
    }
    public FlightTestProcedureSummary Summary(bool interrupted=false)
    {
        var points=definition.Points.Select((p,i)=>new FlightTestPointProgress(p.Id,p.Repetitions,completed[i],
            completed[i]>=p.Repetitions?ResearchAttemptOutcome.Pass:failed[i]?ResearchAttemptOutcome.Fail:ResearchAttemptOutcome.InsufficientEvidence,holds[i],reasons[i])).ToArray();
        var outcome=interrupted?ResearchAttemptOutcome.InsufficientEvidence:points.All(p=>p.Outcome==ResearchAttemptOutcome.Pass)?ResearchAttemptOutcome.Pass
            :points.Any(p=>p.Outcome==ResearchAttemptOutcome.InsufficientEvidence)?ResearchAttemptOutcome.InsufficientEvidence:ResearchAttemptOutcome.Fail;
        return new(1,definition.Ordered,outcome,points,count,points.FirstOrDefault(p=>p.CompletedRepetitions<p.RequiredRepetitions)?.PointId,
            "Only distinct explicitly marked, supported attempts qualify. All required points/repetitions must complete; ordered points cannot qualify early. This is not aircraft certification."+(interrupted?" The flight was interrupted; overall evidence remains insufficient.":""));
    }
}
