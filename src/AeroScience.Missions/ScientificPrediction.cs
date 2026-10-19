using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record PredictedScienceArea(GeographicBounds Area,double ArrivalSeconds,string Basis,Coordinates? SamplePoint=null);
public sealed record ScientificPredictionResult(string Status,string Message,PredictedScienceArea[] Areas,
    double LookaheadSeconds,double DistanceMetres,double TrackTrueDegrees,double TurnDegreesPerSecond,bool RouteConsistent,bool Limited);

// A bounded motion predictor, not navigation guidance or a calibrated sensor model.
public sealed class ScientificMotionPredictor
{
    private TelemetryFrame? previous;
    private double? lastTrack;
    private double filteredTurn;
    public void Reset(){previous=null;lastTrack=null;filteredTurn=0;}
    private static double Delta(double a,double b)=>(a-b+540)%360-180;
    public ScientificPredictionResult Predict(TelemetryFrame frame,DateTimeOffset now,RemoteKind[] kinds,
        ScientificAccessPreferences preferences,double preparationSeconds,MissionPlan? plan=null)
    {
        ScientificPredictionResult Empty(string status,string why)=>new(status,why,[],0,0,frame.GroundTrackDegrees,0,false,false);
        if(!frame.IsValid||frame.IsPaused||(now-frame.TimestampUtc).TotalSeconds is < -2 or >6)
        {Reset();return Empty("Suspended","Paused, stale or invalid telemetry; forward preparation is suspended.");}
        var before=previous;previous=frame;
        if(before is null)return Empty("Stabilizing","Waiting for a second fresh position after startup/reconnect.");
        var dt=(frame.TimestampUtc-before.TimestampUtc).TotalSeconds;
        if(dt<=0){previous=before;return Empty("Waiting","No new geographic movement packet.");}
        var at=new Coordinates(frame.Latitude,frame.Longitude);var old=new Coordinates(before.Latitude,before.Longitude);
        var distance=Geodesy.Distance(old,at);var reported=Math.Max(0,frame.GroundspeedKnots)*.514444444444;
        if(dt>15||frame.Aircraft!=before.Aircraft||distance>Math.Max(2000,Math.Max(reported,before.GroundspeedKnots*.514444444444)*dt*3+500))
        {lastTrack=null;filteredTurn=0;return Empty("Stabilizing","Telemetry gap, aircraft change or position jump detected; discarded the previous trajectory.");}
        var measured=distance/dt;
        if(reported<1.5&&measured>15&&distance>50)
        {lastTrack=null;filteredTurn=0;return Empty("Stabilizing","Position moved while reported groundspeed was stationary (possible slew/correction); forward prediction was discarded.");}
        // Movement wins over heading and track. A stationary slew/pause packet must not predict a fast flight.
        var speed=distance>=5?Math.Min(800,measured):0;
        var track=distance>=5?Geodesy.Bearing(old,at):frame.GroundTrackDegrees;
        track=(track+360)%360;
        if(lastTrack is {} last&&speed>3)
        {var rate=Math.Clamp(Delta(track,last)/dt,-6,6);filteredTurn=.55*filteredTurn+.45*rate;}
        else filteredTurn=0;
        lastTrack=track;
        var horizon=preferences.LookaheadSeconds>0?preferences.LookaheadSeconds:Math.Clamp(preparationSeconds*Math.Max(1,kinds.Count(k=>k!=RemoteKind.Hyperspectral))/preferences.ConcurrentDownloads*1.75+30,90,600);
        horizon=Math.Min(horizon,120000/Math.Max(1,speed));if(speed<1.5)horizon=0;
        var result=new Dictionary<GeographicBounds,PredictedScienceArea>();var limited=false;
        void Add(Coordinates p,double eta,string basis)
        {
            try{var cell=GeographicBounds.Around(p);AutomaticScientificData.ValidateArea(cell);
                if(result.TryGetValue(cell,out var known)){if(eta<known.ArrivalSeconds)result[cell]=new(cell,eta,basis,p);return;}
                if(result.Count>=128){limited=true;return;}result.Add(cell,new(cell,eta,basis,p));
            }catch(ArgumentException){limited=true;}
        }
        void Footprint(Coordinates p,double eta,double bearing)
        {
            Add(p,eta,"Observed flight corridor");
            foreach(var kind in kinds)
            {
                try
                {
                    var projected=frame with{Latitude=p.Latitude,Longitude=p.Longitude,HeadingDegrees=bearing};
                    var footprint=SensorGeometry.Footprint(projected,kind==RemoteKind.Terrain?new(30,3,Swath:true):new());
                    // Cover the model's polygon envelope, bounded independently of altitude/attitude.
                    var south=footprint.Boundary.Min(c=>c.Latitude);var north=footprint.Boundary.Max(c=>c.Latitude);
                    var west=footprint.Boundary.Min(c=>c.Longitude);var east=footprint.Boundary.Max(c=>c.Longitude);
                    if(east-west>1||north-south>1){limited=true;continue;}
                    var count=0;
                    for(var lat=Math.Floor(south/.02)*.02;lat<=north;lat+=.02)
                    for(var lon=Math.Floor(west/.02)*.02;lon<=east;lon+=.02)
                    {if(++count>32){limited=true;break;}Add(new(lat+.01,lon+.01),eta,"Approximate sensor footprint envelope");}
                }catch(InvalidOperationException){limited=true;}catch(ArgumentException){limited=true;}
            }
        }
        Footprint(at,0,frame.HeadingDegrees);
        var route=plan?.Waypoints??[];var routeIndex=-1;var nearest=double.MaxValue;
        // The nearest forward segment must agree with observed motion. No autopilot/LNAV assumption.
        if(speed>3&&Math.Abs(filteredTurn)<.5)
        for(var i=0;i+1<route.Length;i++)
        {
            var a=new Coordinates(route[i].Latitude,route[i].Longitude);var b=new Coordinates(route[i+1].Latitude,route[i+1].Longitude);
            var length=Geodesy.Distance(a,b);var bearing=Geodesy.Bearing(a,b);var angle=Delta(Geodesy.Bearing(a,at),bearing)*Math.PI/180;
            var along=Geodesy.Distance(a,at)*Math.Cos(angle);var cross=Math.Abs(Geodesy.Distance(a,at)*Math.Sin(angle));
            if(along>=-500&&along<=length+500&&cross<Math.Max(500,speed*8)&&cross<nearest&&Math.Abs(Delta(track,bearing))<25)
            {nearest=cross;routeIndex=i+1;}
        }
        var point=at;var heading=track;
        // At most 240 steps and 128 unique cells; speed controls spacing and horizon, never a fixed-cell distance.
        var step=Math.Clamp(700/Math.Max(speed,1),2,10);
        for(double seconds=step;seconds<=horizon+step/2;seconds+=step)
        {
            var targetRoute=routeIndex>=0&&routeIndex<route.Length&&seconds>20;
            if(targetRoute&&routeIndex<route.Length)
            {
                var target=new Coordinates(route[routeIndex].Latitude,route[routeIndex].Longitude);
                if(Geodesy.Distance(point,target)<speed*step){point=target;routeIndex++;}
                else{heading=Geodesy.Bearing(point,target);point=Geodesy.Direct(point,heading,speed*step);}
            }
            else
            {
                // Sustained turns arc around the aircraft instead of extending an endless straight corridor.
                heading+=filteredTurn*step;point=Geodesy.Direct(point,heading-filteredTurn*step/2,speed*step);
            }
            Footprint(point,seconds,heading);
            if(Math.Abs(filteredTurn)>.4)
            {var uncertainty=Math.Min(800,150+seconds*2);Add(Geodesy.Direct(point,heading-90,uncertainty),seconds,"Turn uncertainty");Add(Geodesy.Direct(point,heading+90,uncertainty),seconds,"Turn uncertainty");}
        }
        return new("Predicting",limited?"Bounded prediction is partial; footprint, latitude or queue limits may leave gaps.":"Observed ground movement drives lookahead. Footprints use the existing approximate plane model; continuous coverage is not guaranteed.",result.Values.OrderBy(c=>c.ArrivalSeconds).ToArray(),horizon,speed*horizon,track,filteredTurn,routeIndex>=0,limited);
    }
}
