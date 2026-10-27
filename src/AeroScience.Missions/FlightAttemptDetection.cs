using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
namespace AeroScience.Missions;

// Derived, constant-size checkpoints. No original measurement arrays are stored here.
public sealed record HoldWindowState(DateTimeOffset? Previous,DateTimeOffset? ContinuousStart,DateTimeOffset? HoldStart,
    TelemetrySource? Source,string? Aircraft,bool Incomplete,double Observed,double Longest,DateTimeOffset? LongestStart,DateTimeOffset? LongestEnd);
public sealed record PointWindowState(HoldWindowState Combined,HoldWindowState[] Branches);
public sealed record DetectionCandidate(Guid Id,long FirstSequence,DateTimeOffset StartUtc,int NearCount,int NearMask,bool Recognized,PointWindowState Window);
public sealed record DetectionCursor(long Sequence=0,DateTimeOffset? LastUtc=null,TelemetrySource? Source=null,string? Aircraft=null,
    bool WaitingExit=false,int ExitCount=0,bool ManualBlocked=false,DetectionCandidate? Candidate=null,Guid? LastAttempt=null,string Status="Monitoring",string Message="Armed for the selected point only.",long BreakEventId=0,long ClosedCount=0,string ClosureDigest="");
public sealed record DetectionEmission(Guid Id,long FirstSequence,DateTimeOffset StartUtc,bool Closed,DateTimeOffset? EndUtc,
    long? LastSequence,ResearchAttemptStatus? CompletionStatus,string Reason);

// Recognition only; numeric assessment remains the proven FlightRegimeEvaluation windows.
public sealed class FlightAttemptDetection(Guid session,FlightTestProcedurePoint point,DetectionCursor initial)
{
    public const string Version="quantitative-detection-1";
    public DetectionCursor State {get;private set;}=initial;
    readonly List<DetectionEmission> emissions=[];
    public DetectionEmission[] Drain(){var result=emissions.ToArray();emissions.Clear();return result;}
    public static Guid Identity(string text){var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(text));return new Guid(bytes.AsSpan(0,16));}
    FlightRegimeEvaluation.PointWindow Window(DetectionCandidate candidate){var w=new FlightRegimeEvaluation.PointWindow(point.Conditions,candidate.StartUtc,candidate.StartUtc.AddHours(2));w.Restore(candidate.Window);return w;}
    (int Near,bool Outside,bool Supported) Classify(TelemetryFrame frame)
    {
        var rule=point.Conditions;var p=rule.TestPoint!;var selected=(p.AirspeedRequired?1:0)|(p.AltitudeRequired?2:0);int supported=0,near=0,outside=0;
        if(p.AirspeedRequired&&FlightRegimeEvaluation.SupportedSample(frame,rule with{TestPoint=p with{AltitudeRequired=false,Policy="all"}})){supported|=1;var d=Math.Abs(frame.IndicatedAirspeedKnots-rule.AirspeedKnots);if(d<=2*rule.AirspeedToleranceKnots)near|=1;if(d>3*rule.AirspeedToleranceKnots)outside|=1;}
        if(p.AltitudeRequired&&FlightRegimeEvaluation.SupportedSample(frame,rule with{TestPoint=p with{AirspeedRequired=false,Policy="all"}})){supported|=2;var d=Math.Abs(frame.AltitudeMslFeet-rule.AltitudeMslFeet);if(d<=2*rule.AltitudeToleranceFeet)near|=2;if(d>3*rule.AltitudeToleranceFeet)outside|=2;}
        return(p.Policy=="any"?near:near==selected?selected:0,
            supported==selected&&(p.Policy=="any"?outside==selected:outside!=0),p.Policy=="any"?supported!=0:supported==selected);
    }
    void Close(DateTimeOffset at,long sequence,bool interrupted,string reason)
    {
        if(State.Candidate is {Recognized:true} candidate){
            var emission=new DetectionEmission(candidate.Id,candidate.FirstSequence,candidate.StartUtc,true,at<candidate.StartUtc?candidate.StartUtc:at,sequence,
                interrupted?ResearchAttemptStatus.Interrupted:ResearchAttemptStatus.Recorded,reason);
            emissions.Add(emission);State=State with{ClosedCount=checked(State.ClosedCount+1),ClosureDigest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(State.ClosureDigest+System.Text.Json.JsonSerializer.Serialize(emission,WireJson.Options)))).ToLowerInvariant()};
            State=State with{LastAttempt=candidate.Id,Status=interrupted?"Insufficient Evidence":"Attempt Complete",Message=reason};
        }
        State=State with{Candidate=null,WaitingExit=true};
    }
    public void Interrupt(DateTimeOffset at,string reason)
    {Close(at,State.Sequence,true,reason);State=State with{ExitCount=0};}
    public void Manual(bool blocked,DateTimeOffset at)
    {
        if(blocked&&!State.ManualBlocked)Interrupt(at,"Manual attempt takes priority; the overlapping automatic candidate cannot earn duplicate credit.");
        if(blocked!=State.ManualBlocked)State=State with{ManualBlocked=blocked,WaitingExit=true,ExitCount=0};
    }
    public void Add(long sequence,TelemetryFrame frame)
    {
        if(sequence<=State.Sequence)throw new InvalidOperationException("Detection requires increasing original sequence identities.");
        var at=frame.TimestampUtc;var previous=State.LastUtc;var shape=Classify(frame);
        var broken=!shape.Supported||at.Offset!=TimeSpan.Zero||previous is {} prior&&((at-prior).TotalSeconds is <=0 or >FlightRegimeEvaluation.MaximumGapSeconds||State.Source!=frame.Source||State.Aircraft!=frame.Aircraft);
        if(broken){Interrupt(previous??at,"Original required readings, UTC continuity, source or aircraft changed or became unavailable.");State=State with{Sequence=sequence,LastUtc=previous is {} last&&at<=last?last:at,Source=frame.Source,Aircraft=frame.Aircraft};return;}
        State=State with{Sequence=sequence,LastUtc=at,Source=frame.Source,Aircraft=frame.Aircraft};
        if(State.ManualBlocked)return;
        if(State.WaitingExit){var count=shape.Outside?State.ExitCount+1:0;State=State with{ExitCount=count,WaitingExit=count<3,Message=count<3?"Waiting for three supported readings outside the 3-tolerance exit band before another attempt.":"Measured separation established; monitoring for a new approach."};return;}
        var candidate=State.Candidate;
        if(candidate is null){
            if(shape.Near==0)return;
            var w=new FlightRegimeEvaluation.PointWindow(point.Conditions,at,at.AddHours(2));w.Add(frame);
            var id=Identity($"{Version}/{session:D}/{point.Id:D}/{sequence}");
            State=State with{Candidate=new(id,sequence,at,1,shape.Near,false,w.Snapshot()),ExitCount=0};return;
        }
        var window=Window(candidate);window.Add(frame);
        if(!candidate.Recognized){
            var mask=candidate.NearMask&shape.Near;
            if(mask==0){State=State with{Candidate=null};if(shape.Near!=0){var w=new FlightRegimeEvaluation.PointWindow(point.Conditions,at,at.AddHours(2));w.Add(frame);State=State with{Candidate=new(Identity($"{Version}/{session:D}/{point.Id:D}/{sequence}"),sequence,at,1,shape.Near,false,w.Snapshot())};}return;}
            candidate=candidate with{NearCount=candidate.NearCount+1,NearMask=mask,Recognized=true};
            emissions.Add(new(candidate.Id,candidate.FirstSequence,candidate.StartUtc,false,null,null,null,"Two consecutive supported near-target readings identified an armed attempt."));
            State=State with{Status="Attempt Detected",LastAttempt=candidate.Id,Message="A detected attempt is not a pass. Original evidence is being assessed."};
        }
        var exits=shape.Outside?State.ExitCount+1:0;candidate=candidate with{Window=window.Snapshot()};State=State with{Candidate=candidate,ExitCount=exits};
        var result=window.Finish(point.Id,new(0,0,0,"","",frame.Source),false,at);
        if(result.Outcome==ResearchAttemptOutcome.Pass)Close(at,sequence,false,"Required original continuous hold completed; another repetition requires a measured exit and re-entry.");
        else if(exits>=2)Close(at,sequence,false,"Two supported readings outside the 3-tolerance exit band ended this identifiable attempt; insufficient durations remain insufficient.");
        else if((at-candidate.StartUtc).TotalSeconds>=Math.Max(5,2*point.Conditions.HoldSeconds))Close(at,sequence,false,"The bounded recognition interval ended without a qualifying hold; sufficient original evidence can establish a measured failure.");
    }
    public void Skip(long sequence,string reason){Interrupt(State.LastUtc??DateTimeOffset.UnixEpoch,reason);State=State with{Sequence=sequence};}
    public void AcknowledgeBreak(long id)=>State=State with{BreakEventId=id};
    public void Stop(DateTimeOffset at,string reason)=>Interrupt(at,reason);
}
