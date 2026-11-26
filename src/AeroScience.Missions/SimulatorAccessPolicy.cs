using AeroScience.Shared;

namespace AeroScience.Missions;

// The compiled host mapping and actual confirmation evidence decide eligibility.
// Provider readiness flags, UI settings and a live socket cannot grant permission.
public sealed class SimulatorAccessPolicy
{
    public string SimulatorId { get; }
    public bool XPlane => SimulatorId == SimulatorPreferences.XPlane12;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Guid msfsEpoch=Guid.NewGuid();
    private int eligible;
    private TelemetryUpdate? qualified;
    public SimulatorAccessPolicy(string simulatorId,Func<DateTimeOffset>? utcNow=null)
    {
        if (!SimulatorPreferences.Known(simulatorId)) throw new ArgumentException("Unsupported simulator identity.");
        SimulatorId=simulatorId;this.utcNow=utcNow??(()=>DateTimeOffset.UtcNow);eligible=XPlane?0:1;
    }
    public bool RecordingAllowed => Volatile.Read(ref eligible)==1 && (!XPlane || qualified?.Frame is {} frame && Fresh(frame.TimestampUtc));
    public bool ScienceAllowed => RecordingAllowed;
    public string ReasonKey => XPlane?"simulator.incomplete":"simulator.sourceMismatch";
    public string Reason => XPlane?"X-Plane recording requires current, confirmed aircraft data from the supported local adapter. Reconnect and review source diagnostics.":PresentationText.Text(ReasonKey);
    public SimulatorAvailability Availability => new(1,SimulatorId,RecordingAllowed,ScienceAllowed,ReasonKey);
    private bool Fresh(DateTimeOffset at) => (utcNow()-at).TotalSeconds is >= 0 and <= 2;

    public void Observe(TelemetryUpdate update)
    {
        if(XPlane)
        {
            qualified=QualifyXPlane(update);Volatile.Write(ref eligible,qualified is null?0:1);return;
        }
        if(update.Connection.Origin is {} origin && origin.SimulatorId!=SimulatorId
            || update.SampleContext is {} context && (!context.Capabilities.ProductionRecordingReady||!context.Capabilities.ScienceIntegrationReady))
            Volatile.Write(ref eligible,0);
        else if(update.Connection.State==ConnectionState.Connected&&update.Frame is {IsValid:true})Volatile.Write(ref eligible,1);
    }

    public TelemetryUpdate Qualify(TelemetryUpdate update)
    {
        if(XPlane)return qualified??update with{Frame=null,Atmosphere=null,Environment=null,Guidance=null};
        if(update.Connection.Source!=TelemetrySource.Simulator)return update;
        var origin=update.Connection.Origin??new SimulatorOrigin(1,SimulatorId,null,"aeroscience.simconnect",
            typeof(SimulatorAccessPolicy).Assembly.GetName().Version?.ToString()??"unknown",null,msfsEpoch,"simconnect-standard-v1");
        origin=origin with{TimestampBasis=SimulatorProvenance.HostReceiptBasis,Limitations=origin.Limitations??["Simulator-reported measurements; not independent real-world observations.","MSFS simulator build and complete simulator date are unknown unless explicitly reported.","Connection epoch identifies this AeroScience adapter instance; MSFS simulator-session continuity is not attested."]};
        SimulatorProvenance.Validate(origin);
        return update with{Connection=update.Connection with{Origin=origin},
            Frame=update.Frame is {} frame?frame with{SimulatorOrigin=origin}:null,
            Atmosphere=update.Atmosphere is {} atmosphere?atmosphere with{SimulatorOrigin=origin}:null};
    }

    public void RequireRecording(){if(!RecordingAllowed)throw new InvalidOperationException(Reason);}
    public void RequireScience(){if(!ScienceAllowed)throw new InvalidOperationException(Reason);}

    public void RequireInstruments(IEnumerable<InstrumentConfiguration> configurations)
        =>RequireInstruments(SimulatorId,configurations);
    private static void RequireInstruments(string simulatorId,IEnumerable<InstrumentConfiguration> configurations)
    {
        if(simulatorId==SimulatorPreferences.XPlane12&&configurations.Any(c=>c.Enabled&&RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)))
            throw new InvalidOperationException("X-Plane ground-intersection and scanner geometry are not qualified in this checkpoint. Disable remote sensing instruments; existing external sources and archived Results remain available.");
    }
    public void RequirePlanScience(MissionPlan? plan)
        =>RequirePlanScience(SimulatorId,plan);
    private static void RequirePlanScience(string simulatorId,MissionPlan? plan)
    {
        if(simulatorId==SimulatorPreferences.XPlane12&&plan?.Template==MissionTemplateKind.CloudStructureSurvey)
            throw new InvalidOperationException("Cloud Structure Survey requires aircraft-local cloud channels unavailable from this X-Plane mapping. Recording and offline planning remain available; no cloud evidence or completion credit can be inferred from temperature or flight position.");
    }
    internal static void RequireCollectionConfiguration(SimulatorOrigin? origin,MissionPlan? plan,IEnumerable<InstrumentConfiguration> configurations)
    {
        if(origin is null)return;SimulatorProvenance.Validate(origin);
        RequireInstruments(origin.SimulatorId,configurations);RequirePlanScience(origin.SimulatorId,plan);
    }
    internal static void RequireCard(SimulatorOrigin? origin,PersonalResearchCard card)
    {
        if(origin?.SimulatorId==SimulatorPreferences.XPlane12&&(card.Survey is not null||card.ExperimentalSensor is not null
            ||card.ObservationFlight?.SelectedInstruments.Any(RemoteAcquisition.Instruments.ContainsKey)==true))
            throw new InvalidOperationException("This research card requires unqualified X-Plane remote-sensing geometry. Keep its issued revision and select supported atmospheric or flight research; no substituted evidence or credit was created.");
    }

    private TelemetryUpdate? QualifyXPlane(TelemetryUpdate update)
    {
        if(update.Connection is not {State:ConnectionState.Connected,Source:TelemetrySource.Simulator,Origin:{} origin}
            || update.Frame is not {IsValid:true,Source:TelemetrySource.Simulator} frame || update.SampleContext is not {} context)return null;
        try{SimulatorProvenance.Validate(origin);SimulatorProvenance.Validate(context);}catch(ArgumentException){return null;}
        if(origin.SimulatorId!=SimulatorId||origin.AdapterId!="aeroscience.xplane.web"||origin.AdapterVersion!="p02-v1"
            ||origin.ApiVersion!="v1"||origin.MappingRevision!="xplane-web-v1-map1"||context.ConnectionEpoch!=origin.ConnectionEpoch
            ||frame.TimestampUtc!=context.HostReceivedUtc||!Fresh(frame.TimestampUtc)||!Fresh(context.ConfirmationCompletedUtc)
            ||context.CoreSnapshotSpanMilliseconds is not (>=0 and <=500)||context.Clock is not {} clock||clock.Replay||clock.Year is not null
            ||clock.Paused!=frame.IsPaused||frame.IndicatedAirspeedKnots<0||frame.TrueAirspeedKnots<0||frame.GroundspeedKnots<0
            ||frame.HeadingDegrees is <0 or >=360||frame.GroundTrackDegrees is <0 or >=360
            ||frame.PitchDegrees is <-90 or >90||frame.BankDegrees is <-180 or >180
            ||Core.Any(key=>!Confirmed(key,context))||!CoreValuesMatch(frame,clock,context))return null;
        origin=origin with{TimestampBasis=SimulatorProvenance.HostReceiptBasis,Limitations=[
            "X-Plane aircraft-local simulator reports; not independent real-world measurements.",
            "Bounded non-atomic confirmation composite; individual field times and simulator clock components are retained.",
            "Candidate attitude/loading conventions await genuine installed X-Plane validation in P06.",
            "Geometry-dependent collection and unqualified environmental channels are unavailable.",
            "A new epoch, aircraft, replay, time-rate or clock discontinuity requires a separate recording."]};
        var atmosphere=update.Atmosphere;
        // Only the five documented aircraft-local channels are eligible. No MSFS
        // environment enrichment, MSL-minus-AGL ground estimate or source substitution.
        if(atmosphere is not null)
        {
            var valid=atmosphere.Quality==ObservationQuality.Valid&&QualifiedRate(context)
                &&Fresh(atmosphere.TimestampUtc)&&Math.Abs((atmosphere.TimestampUtc-frame.TimestampUtc).TotalMilliseconds)<=500;
            double? Value(string key,double? value) => valid&&Confirmed(key,context)&&value is {} n&&double.IsFinite(n)&&AtmosphereMatches(key,n,context)
                &&(key switch{"temperature"=>n>=-273.15,"pressure" or "density"=>n>0,"wind-speed"=>n>=0,"wind-direction"=>n is >=0 and <360,_=>false})?n:null;
            atmosphere=atmosphere with{TemperatureCelsius=Value("temperature",atmosphere.TemperatureCelsius),PressureInHg=Value("pressure",atmosphere.PressureInHg),
                WindSpeedKnots=Value("wind-speed",atmosphere.WindSpeedKnots),WindDirectionTrueDegrees=Value("wind-direction",atmosphere.WindDirectionTrueDegrees),
                DensitySlugsPerCubicFoot=Value("density",atmosphere.DensitySlugsPerCubicFoot),SimulatorOrigin=origin,SimulatorContext=context};
            if(frame.IsPaused||new[]{atmosphere.TemperatureCelsius,atmosphere.PressureInHg,atmosphere.WindSpeedKnots,atmosphere.WindDirectionTrueDegrees,atmosphere.DensitySlugsPerCubicFoot}.All(v=>v is null))
                atmosphere=atmosphere with{Quality=frame.IsPaused?ObservationQuality.Paused:ObservationQuality.Unavailable};
        }
        return update with{Connection=update.Connection with{Origin=origin},Frame=frame with{SimulatorOrigin=origin,SimulatorContext=context,Atmosphere=atmosphere,Environment=null,Guidance=null,
            AltitudeAglFeet=Confirmed("agl",context)&&frame.AltitudeAglFeet is >=0&&Matches(context,"agl","m",frame.AltitudeAglFeet.Value,v=>v/.3048)?frame.AltitudeAglFeet:null,
            MachNumber=Confirmed("mach",context)&&frame.MachNumber is >=0&&Matches(context,"mach","1",frame.MachNumber.Value)?frame.MachNumber:null},
            Atmosphere=atmosphere,Environment=null,Guidance=null};
    }

    public static bool NormalRate(SimulatorClock? clock) => clock is {RequestedRate:1,ActualRate:{} actual,DilationAwareRate:{} dilation,DistanceRate:{} distance}
        &&Math.Abs(actual-1)<.01&&Math.Abs(dilation-1)<.01&&Math.Abs(distance-1)<.01;
    public static bool QualifiedRate(SimulatorSampleContext context)=>NormalRate(context.Clock)
        &&new[]{"requested-rate","actual-rate","dilation-rate","distance-rate"}.All(key=>Confirmed(key,context))
        &&Matches(context,"requested-rate","1",context.Clock!.RequestedRate!.Value)
        &&Matches(context,"actual-rate","1",context.Clock.ActualRate!.Value)&&Matches(context,"dilation-rate","1",context.Clock.DilationAwareRate!.Value)
        &&Matches(context,"distance-rate","1",context.Clock.DistanceRate!.Value);

    private static bool Matches(SimulatorSampleContext context,string key,string unit,double normalized,Func<double,double>? convert=null)
    {
        var source=context.ReportedValues?.SingleOrDefault(v=>v.Channel==key);
        if(source is null||source.Unit!=unit||!Datarefs.TryGetValue(key,out var name)||source.Dataref!=name
            ||context.Fields.SingleOrDefault(f=>f.Dataref==name)?.LastConfirmedUtc!=source.ConfirmedUtc)return false;
        var value=convert?.Invoke(source.Value)??source.Value;
        return double.IsFinite(value)&&Math.Abs(value-normalized)<=Math.Max(1e-9,Math.Abs(value)*1e-12);
    }
    private static bool CoreValuesMatch(TelemetryFrame f,SimulatorClock clock,SimulatorSampleContext c) =>
        Matches(c,"latitude","deg",f.Latitude)&&Matches(c,"longitude","deg",f.Longitude)&&Matches(c,"msl","m",f.AltitudeMslFeet,v=>v/.3048)
        &&Matches(c,"ias","kt",f.IndicatedAirspeedKnots)&&Matches(c,"tas","m/s",f.TrueAirspeedKnots,v=>v*3600/1852)
        &&Matches(c,"groundspeed","m/s",f.GroundspeedKnots,v=>v*3600/1852)&&Matches(c,"vertical-speed","m/s",f.VerticalSpeedFeetPerMinute,v=>v*60/.3048)
        &&Matches(c,"heading","degT",f.HeadingDegrees,v=>(v%360+360)%360)&&Matches(c,"track","degT",f.GroundTrackDegrees,v=>(v%360+360)%360)
        &&Matches(c,"pitch","deg",f.PitchDegrees)&&Matches(c,"bank","deg",f.BankDegrees)
        &&Matches(c,"running-seconds","s",clock.RunningSeconds)&&Matches(c,"flight-seconds","s",clock.FlightSeconds)
        &&Matches(c,"paused","1",clock.Paused?1:0)&&Matches(c,"replay","1",clock.Replay?1:0);
    private static bool AtmosphereMatches(string key,double normalized,SimulatorSampleContext context)=>key switch
    {
        "temperature"=>Matches(context,key,"degC",normalized),"pressure"=>Matches(context,key,"Pa",normalized,v=>v/3386.389),
        "wind-speed"=>Matches(context,key,"m/s",normalized,v=>v*3600/1852),"wind-direction"=>Matches(context,key,"degT",normalized,v=>(v%360+360)%360),
        "density"=>Matches(context,key,"kg/m3",normalized,v=>v*.028316846592/14.5939029372064),_=>false
    };
    public static bool QualifiedObservation(ScienceObservation observation)
    {
        if(observation.SimulatorOrigin is not {Version:1,SimulatorId:SimulatorPreferences.XPlane12} origin
            ||observation.SimulatorContext is not {} context||context.ConnectionEpoch!=origin.ConnectionEpoch
            ||context.Version!=1||context.CoreSnapshotSpanMilliseconds is not (>=0 and <=500)
            ||context.Clock is not {Paused:false,Replay:false}||!QualifiedRate(context)
            ||Core.Any(key=>!Confirmed(key,context))||!Confirmed(observation.MeasurementId,context)
            ||Math.Abs((observation.TimestampUtc-context.HostReceivedUtc).TotalMilliseconds)>500)return false;
        var unit=observation.MeasurementId switch{"temperature"=>"degC","pressure"=>"inHg","wind-speed"=>"kt","wind-direction"=>"degT","density"=>"slug/ft3",_=>null};
        return unit is not null&&observation.Unit==unit&&AtmosphereMatches(observation.MeasurementId,observation.Value,context);
    }

    private static bool Confirmed(string key,SimulatorSampleContext context) => context.Capabilities.AvailableChannels.Contains(key)
        &&Datarefs.TryGetValue(key,out var name)&&context.Fields.SingleOrDefault(f=>f.Dataref==name) is {Available:true,LastConfirmedUtc:{} at} field
        &&at>=context.ConfirmationStartedUtc&&at<=context.ConfirmationCompletedUtc
        &&field.LastReceivedUtc>=context.ConfirmationStartedUtc&&field.LastReceivedUtc<=context.ConfirmationCompletedUtc
        &&Math.Abs((at-context.HostReceivedUtc).TotalMilliseconds)<=500;
    // Exact P01/P02 mapping names. Tests compare these to the actual provider map.
    internal static readonly Dictionary<string,string> Datarefs=new()
    {
        ["latitude"]="sim/flightmodel/position/latitude",["longitude"]="sim/flightmodel/position/longitude",["msl"]="sim/flightmodel/position/elevation",
        ["agl"]="sim/flightmodel/position/y_agl",["mach"]="sim/flightmodel/misc/machno",
        ["ias"]="sim/flightmodel/position/indicated_airspeed",["tas"]="sim/flightmodel/position/true_airspeed",["groundspeed"]="sim/flightmodel/position/groundspeed",
        ["heading"]="sim/flightmodel/position/true_psi",["track"]="sim/cockpit2/gauges/indicators/ground_track_true_pilot",
        ["pitch"]="sim/flightmodel/position/theta",["bank"]="sim/flightmodel/position/phi",["vertical-speed"]="sim/flightmodel/position/vh_ind",
        ["aircraft"]="sim/aircraft/view/acf_descrip",["paused"]="sim/time/paused",["running-seconds"]="sim/time/total_running_time_sec",
        ["flight-seconds"]="sim/time/total_flight_time_sec",["replay"]="sim/time/is_in_replay",
        ["requested-rate"]="sim/time/sim_speed",["actual-rate"]="sim/time/sim_speed_actual",["dilation-rate"]="sim/time/sim_speed_actual_ogl",["distance-rate"]="sim/time/ground_speed_flt",
        ["temperature"]="sim/weather/aircraft/temperature_ambient_deg_c",["pressure"]="sim/weather/aircraft/barometer_current_pas",
        ["wind-speed"]="sim/weather/aircraft/wind_now_speed_msc",["wind-direction"]="sim/weather/aircraft/wind_now_direction_degt",["density"]="sim/weather/rho"
    };
    private static readonly string[] Core=["latitude","longitude","msl","ias","tas","groundspeed","heading","track","pitch","bank","vertical-speed","aircraft","paused","running-seconds","flight-seconds","replay"];
}
