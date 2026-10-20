using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record TransectPass(int Number,string Direction,bool Completed,DateTimeOffset? StartUtc,DateTimeOffset? EndUtc,long Samples,
    double? MeanAltitudeFeet,double? MeanTemperatureCelsius,double? MeanWindSpeedKnots,string Quality,
    double? TargetAltitudeFeet=null,double? TargetGroundspeedKnots=null,double? MeanGroundspeedKnots=null,double? ToleranceMetres=null);
public static class TransectPasses
{
    public static TransectPass[] Recognize(PatternDefinition definition,IEnumerable<TelemetryFrame> samples,double? toleranceMetres=null,double maximumGapSeconds=5)
    {
        var pattern=FlightPatterns.Generate(definition);
        if(definition.Kind!=FlightPatternKind.StraightTransect)throw new ArgumentException("Choose a straight transect.");
        var a=Geodesy.Point(pattern.Waypoints[0]);var b=Geodesy.Point(pattern.Waypoints[1]);var length=Geodesy.Distance(a,b);
        var tolerance=toleranceMetres??Math.Clamp(length/20,.1,250);
        if(!double.IsFinite(tolerance)||tolerance<.1||tolerance>=length/4||!double.IsFinite(maximumGapSeconds)||maximumGapSeconds is <1 or >60)
            throw new ArgumentException("Endpoint tolerance must be 0.1 m to less than one quarter of transect length; gap tolerance 1–60 seconds.");
        var results=new List<TransectPass>();DateTimeOffset? started=null,atmosphereTime=null;TelemetryFrame? previous=null,candidate=null;
        long count=0,tempCount=0,windCount=0;double altitude=0,temp=0,wind=0,groundspeed=0,maxProgress=0;var valid=true;
        void Atmosphere(TelemetryFrame frame)
        {
            if(frame.Atmosphere is not{Quality:ObservationQuality.Valid} atmosphere||Math.Abs((atmosphere.TimestampUtc-frame.TimestampUtc).TotalSeconds)>3||atmosphereTime is {} old&&atmosphere.TimestampUtc<=old)return;
            atmosphereTime=atmosphere.TimestampUtc;
            if(atmosphere.TemperatureCelsius is {} t){temp+=t;tempCount++;}
            if(atmosphere.WindSpeedKnots is {} w){wind+=w;windCount++;}
        }
        foreach(var frame in samples)
        {
            if(results.Count>=definition.Passes)break;
            var reverse=results.Count%2==1;var from=reverse?b:a;var to=reverse?a:b;var p=new Coordinates(frame.Latitude,frame.Longitude);
            if(!frame.IsValid||frame.IsPaused){candidate=null;if(started is not null)valid=false;continue;}
            if(started is null)
            {
                if(candidate is null){if(Geodesy.Distance(p,from)<=tolerance)candidate=frame;continue;}
                var at=new Coordinates(candidate.Latitude,candidate.Longitude);var gap=(frame.TimestampUtc-candidate.TimestampUtc).TotalSeconds;
                var movement=Geodesy.Distance(at,p);var direction=Math.Abs((Geodesy.Bearing(at,p)-Geodesy.Bearing(from,to)+540)%360-180);
                // An endpoint is only an arm point. Start after observed movement toward the opposite endpoint.
                // Departing into a pilot-managed turn invalidates the arm, not a scientific reverse pass.
                if(gap<=0)continue;
                if(gap>maximumGapSeconds||direction>30||movement<Math.Min(10,tolerance/5))
                {candidate=Geodesy.Distance(p,from)<=tolerance?frame:null;continue;}
                started=candidate.TimestampUtc;previous=candidate;count=1;altitude=candidate.AltitudeMslFeet;groundspeed=candidate.GroundspeedKnots;
                temp=wind=0;tempCount=windCount=0;atmosphereTime=null;Atmosphere(candidate);maxProgress=0;valid=true;candidate=null;
            }
            var dt=(frame.TimestampUtc-previous!.TimestampUtc).TotalSeconds;if(dt<=0)continue;if(dt>maximumGapSeconds)valid=false;
            var distance=Geodesy.Distance(from,p);var bearing=Geodesy.Bearing(from,p)-Geodesy.Bearing(from,to);
            var progress=distance*Math.Cos(bearing*Math.PI/180);var cross=Math.Abs(distance*Math.Sin(bearing*Math.PI/180));
            if(cross>tolerance||progress<maxProgress-tolerance)valid=false;
            maxProgress=Math.Max(maxProgress,progress);count++;altitude+=frame.AltitudeMslFeet;groundspeed+=frame.GroundspeedKnots;Atmosphere(frame);previous=frame;
            if(Geodesy.Distance(p,to)<=tolerance)
            {
                var completed=valid&&count>=3&&maxProgress>=length-tolerance;
                results.Add(new(results.Count+1,reverse?"B → A":"A → B",completed,started,frame.TimestampUtc,count,altitude/count,tempCount>0?temp/tempCount:null,windCount>0?wind/windCount:null,
                    completed?"Continuous recorded traversal within stated tolerance; turnaround excluded":"Incomplete: direction, cross-track, telemetry continuity or intermediate evidence failed",MeanGroundspeedKnots:groundspeed/count));
                started=null;previous=null;candidate=null;
            }
        }
        while(results.Count<definition.Passes)results.Add(new(results.Count+1,results.Count%2==1?"B → A":"A → B",false,null,null,0,null,null,null,"No complete observed traversal. Planned passes are not flown evidence."));
        return results.Select((pass,i)=>pass with{TargetAltitudeFeet=definition.PassAltitudesFeet?[i]??definition.TargetAltitudeFeet,TargetGroundspeedKnots=definition.TargetGroundspeedKnots,ToleranceMetres=tolerance}).ToArray();
    }
}
