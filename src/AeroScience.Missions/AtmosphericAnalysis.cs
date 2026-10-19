using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record AtmosphericProfilePoint(DateTimeOffset Utc,double AltitudeMetres,double? TemperatureCelsius,double? PressureInHg,
    double? DensitySlugsPerCubicFoot,double? WindKnots,double? WindDirectionTrue,EnvironmentFrame? Environment,
    double? GroundAltitudeMetres,double? ClearanceMetres,double? GroundSlopeDegrees,double? LapseKelvinPerKilometre,bool? Inversion,bool BreakBefore,double DistanceMetres,
    DateTimeOffset? AtmosphereUtc=null,DateTimeOffset? GroundUtc=null);
public sealed record CloudEncounter(DateTimeOffset EntryUtc,DateTimeOffset? ExitUtc,double EntryAltitudeMetres,double? ExitAltitudeMetres,bool EntryObserved,bool ExitObserved,
    DateTimeOffset? EntryAircraftPacketUtc=null,DateTimeOffset? ExitAircraftPacketUtc=null);
public sealed record AtmosphericBin(string Direction,double MinimumAltitudeMetres,double MaximumAltitudeMetres,long Samples,double? TemperatureMeanCelsius,double? PressureMeanInHg,double? DensityMeanSlugsPerCubicFoot,double? WindMeanKnots);
public sealed record AtmosphericAnalysisResult(AtmosphericProfilePoint[] Profile,CloudEncounter[] Clouds,long TotalSamples,long Gaps,bool Downsampled,string[] Methods,AtmosphericBin[]? AltitudeBins=null);
public static class AtmosphericAnalysis
{
    private sealed class Mean {public double Sum;public long Count;public void Add(double? v){if(v is {} n&&double.IsFinite(n)){Sum+=n;Count++;}}public double? Value=>Count>0?Sum/Count:null;}
    private sealed class Bin {public long Count;public Mean Temperature=new(),Pressure=new(),Density=new(),Wind=new();}
    public static AtmosphericMissionSettings Validate(AtmosphericMissionSettings s)
    {
        if(s.Direction is not("both" or "ascent" or "descent")||!double.IsFinite(s.AltitudeIntervalMetres)||s.AltitudeIntervalMetres is <10 or >5000
            ||!double.IsFinite(s.MaximumGapSeconds)||s.MaximumGapSeconds is <1 or >600
            ||new[]{s.MinimumAltitudeFeet,s.MaximumAltitudeFeet}.Any(v=>v is {} n&&!double.IsFinite(n))||s.MinimumAltitudeFeet>s.MaximumAltitudeFeet)
            throw new ArgumentException("Choose ascent/descent/both, a 10–5,000 m altitude interval and a 1–600 second gap threshold.");return s;
    }
    public static AtmosphericAnalysisResult Calculate(IEnumerable<TelemetryFrame> frames,AtmosphericMissionSettings? settings=null,int maximumPoints=2000,CancellationToken token=default)
    {
        var s=Validate(settings??new());if(maximumPoints is <100 or >10000)throw new ArgumentOutOfRangeException(nameof(maximumPoints));
        // Bounded multiresolution retention: deterministic thinning of the chart only. Counts/cloud transitions use every actual sample.
        var chart=new List<AtmosphericProfilePoint>();var clouds=new List<CloudEncounter>();CloudEncounter? cloud=null;
        var bins=new Dictionary<(string Direction,int Index),Bin>();DateTimeOffset? lastBinned=null;
        TelemetryFrame? previous=null;long count=0,gaps=0;int stride=1;double distance=0;
        bool chartBreak=false;
        EnvironmentReading? Ground(TelemetryFrame f)=>f.Environment?.Readings.FirstOrDefault(r=>r.Id=="ground-altitude"&&r.Quality==ObservationQuality.Valid&&r.Value is {} v&&double.IsFinite(v)&&(f.TimestampUtc-r.TimestampUtc).TotalSeconds is >=0 and <=3);
        foreach(var f in frames)
        {
            token.ThrowIfCancellationRequested();count++;
            var dt=previous is null?0:(f.TimestampUtc-previous.TimestampUtc).TotalSeconds;
            var broken=previous is null||dt<=0||dt>s.MaximumGapSeconds||f.IsPaused||previous.IsPaused||f.Aircraft!=previous.Aircraft;
            chartBreak|=broken;
            var segment=previous is null?0:Geodesy.Distance(new(previous.Latitude,previous.Longitude),new(f.Latitude,f.Longitude));
            if(broken&&previous is not null){gaps++;if(cloud is not null){clouds.Add(cloud);cloud=null;}}
            if(!broken)distance+=segment;
            var ground=Ground(f);var priorGround=previous is null?null:Ground(previous);
            var slope=!broken&&ground is {Value:{} g}&&priorGround is {Value:{} pg}&&ground.TimestampUtc!=priorGround.TimestampUtc&&segment>=1?(double?)(Math.Atan2(g-pg,segment)*180/Math.PI):null;
            var da=previous is null?0:(f.AltitudeMslFeet-previous.AltitudeMslFeet)*.3048;
            bool Fresh(AtmosphericFrame? a)=>a is {Quality:ObservationQuality.Valid}&&(f.TimestampUtc-a.TimestampUtc).TotalSeconds is >=0 and <=3;
            var a=Fresh(f.Atmosphere)?f.Atmosphere:null;var old=previous?.Atmosphere;
            var direction=da>.1?"ascent":da<-.1?"descent":"level";
            if(!broken&&a is not null&&a.TimestampUtc!=lastBinned&&(s.Direction=="both"||s.Direction==direction)
                &&(s.MinimumAltitudeFeet is null||f.AltitudeMslFeet>=s.MinimumAltitudeFeet)&&(s.MaximumAltitudeFeet is null||f.AltitudeMslFeet<=s.MaximumAltitudeFeet))
            {
                var key=(direction,(int)Math.Floor(f.AltitudeMslFeet*.3048/s.AltitudeIntervalMetres));
                if(!bins.TryGetValue(key,out var bin))bins.Add(key,bin=new());bin.Count++;bin.Temperature.Add(a.TemperatureCelsius);bin.Pressure.Add(a.PressureInHg);bin.Density.Add(a.DensitySlugsPerCubicFoot);bin.Wind.Add(a.WindSpeedKnots);lastBinned=a.TimestampUtc;
                if(bins.Count>20000)throw new InvalidOperationException("Atmospheric altitude bins exceed the bounded analysis limit.");
            }
            var lapse=!broken&&Math.Abs(da)>=1&&a?.TemperatureCelsius is {} t&&old is {Quality:ObservationQuality.Valid,TemperatureCelsius:{} before}
                &&previous is not null&&(previous.TimestampUtc-old.TimestampUtc).TotalSeconds is >=0 and <=3&&old.TimestampUtc!=a.TimestampUtc
                ?(double?)(-(t-before)/da*1000):null;
            var cloudReading=f.Environment?.Readings.FirstOrDefault(r=>r.Id=="in-cloud"&&r.Quality==ObservationQuality.Valid&&(f.TimestampUtc-r.TimestampUtc).TotalSeconds is >=0 and <=3);
            var inCloud=cloudReading?.Value;
            var priorCloud=previous?.Environment?.Readings.FirstOrDefault(r=>r.Id=="in-cloud"&&r.Quality==ObservationQuality.Valid&&(previous.TimestampUtc-r.TimestampUtc).TotalSeconds is >=0 and <=3)?.Value;
            if(inCloud==1&&cloud is null)cloud=new(cloudReading!.TimestampUtc,null,f.AltitudeMslFeet*.3048,null,!broken&&priorCloud==0,false,f.TimestampUtc);
            else if(inCloud==0&&cloud is not null){clouds.Add(cloud with{ExitUtc=cloudReading!.TimestampUtc,ExitAltitudeMetres=f.AltitudeMslFeet*.3048,ExitObserved=!broken&&priorCloud==1,ExitAircraftPacketUtc=f.TimestampUtc});cloud=null;}
            else if(inCloud is null&&cloud is not null){clouds.Add(cloud);cloud=null;}
            if(clouds.Count>10000)throw new InvalidOperationException("Cloud analysis exceeds the bounded transition limit; select a shorter mission interval.");
            if((count-1)%stride==0)
                chart.Add(new(f.TimestampUtc,f.AltitudeMslFeet*.3048,a?.TemperatureCelsius,a?.PressureInHg,a?.DensitySlugsPerCubicFoot,a?.WindSpeedKnots,a?.WindDirectionTrueDegrees,
                    f.Environment,ground?.Value,f.AltitudeAglFeet*.3048,slope,lapse,lapse is {} l?l<0:null,chartBreak,distance,a?.TimestampUtc,ground?.TimestampUtc));
            if((count-1)%stride==0)chartBreak=false;
            if(chart.Count>maximumPoints)
            {
                var thinned=new List<AtmosphericProfilePoint>();bool gap=false;
                for(var n=0;n<chart.Count;n++){gap|=chart[n].BreakBefore;if(n%2==0){thinned.Add(chart[n]with{BreakBefore=gap});gap=false;}}
                chart=thinned;chartBreak|=gap;stride=checked(stride*2);
            }
            previous=f;
        }
        if(cloud is not null)clouds.Add(cloud);
        return new(chart.ToArray(),clouds.ToArray(),count,gaps,stride>1,
            ["atmospheric-profile-1: actual timestamped simulator observations; chart thinning is disclosed and never creates observations.",
             "Lapse = −ΔT/Δaltitude ×1000, using distinct valid atmosphere packets and at least 1 m vertical separation; negative lapse flags a local candidate inversion, not a validated regional layer.",
             "Horizontal movement, changing simulator weather and source timing confound vertical interpretation. Cloud entries/exits are confirmed only across contiguous valid 0→1/1→0 observations. Unknown boundaries remain open.",
             "Ground slope = atan2(Δsimulator surface altitude, WGS84 along-track distance). Clearance is the independent simulator AGL variable. The unspecified simulator vertical datum prevents absolute comparison with external DEM datums.",
             "The profile is a sampled aircraft path and local weather cross section, not a reconstructed 3D cloud volume. Historical records missing auxiliary values remain unavailable.",
             "Raw flight environment context is independent of optional Science Collection pause. It is not a backfilled instrument acquisition. Auxiliary receipt timestamps remain separate from the aircraft packet used for location and altitude; no temporal interpolation was applied.",
             "Altitude bins use the selected actual ascent/descent direction and interval, with distinct valid atmosphere receipt times; means exclude missing values and do not interpolate vertical layers."],
            bins.OrderBy(b=>b.Key.Direction).ThenBy(b=>b.Key.Index).Select(b=>new AtmosphericBin(b.Key.Direction,b.Key.Index*s.AltitudeIntervalMetres,(b.Key.Index+1)*s.AltitudeIntervalMetres,b.Value.Count,b.Value.Temperature.Value,b.Value.Pressure.Value,b.Value.Density.Value,b.Value.Wind.Value)).ToArray());
    }
    public static string[] SensorCautions(TelemetryFrame frame,PayloadConfiguration? payload)
    {
        var result=new List<string>();var env=frame.Environment?.Readings??[];
        double? Value(string id)=>env.FirstOrDefault(r=>r.Id==id&&r.Quality==ObservationQuality.Valid&&(frame.TimestampUtc-r.TimestampUtc).TotalSeconds is >=0 and <=3)?.Value;
        if(Value("in-cloud")==1||Value("cloud-density")>0)result.Add("Aircraft-local cloud may affect the modeled optical path. Archived scene QA and its independent observation time remain authoritative; no source pixels were rejected from this weather flag.");
        if(Value("precipitation")>0)result.Add("Simulator precipitation is present. No undocumented time-rate conversion or numerical quality penalty was applied.");
        if(Value("visibility") is {} v)result.Add($"Simulator ambient particle visibility: {v:0} m. This is not a calibrated sensor transmission model.");
        var t=frame.Atmosphere?.TemperatureCelsius;var p=payload?.Profile;
        if(t is {} temperature&&(temperature<p?.MinimumOperatingCelsius||temperature>p?.MaximumOperatingCelsius))result.Add("Simulator ambient temperature is outside the profile's documented operating-temperature envelope; retained source values remain unchanged.");
        return result.ToArray();
    }
}
