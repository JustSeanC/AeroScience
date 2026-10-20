using AeroScience.Shared;

namespace AeroScience.Missions;

/// <summary>Versioned reference configurations. These describe geometry, never the resolution or calibration of an external scene.</summary>
public static class InstrumentProfiles
{
    private const string Nasa = "https://avirisng.jpl.nasa.gov/specifications.html";
    private const string Mica = "https://support.micasense.com/hc/en-us/articles/4410824602903-RedEdge-P-Integration-Guide";
    private const string Flir = "https://www.flir.com/products/a655sc/";
    private const string Riegl = "https://www.riegl.com/fileadmin/media/Products/03_Airborne_Scanning/RIEGL_VQ-1560II-S_Datasheet_2024-03-22.pdf";
    public static readonly InstrumentProfile[] Catalog =
    [
        new("nasa.aviris-ng.nominal", 1, "NASA AVIRIS-NG · nominal 100 lines/s", "hyperspectral-imager", SensorGeometryKind.Pushbroom,
            36, .0572957795, 600, null, [ScientificCapability.HyperspectralRadiance, ScientificCapability.HyperspectralReflectance], [Nasa, "https://avirisng.jpl.nasa.gov/aviris-ng.html"],
            ["380–2510 nm, nominal 5 nm sampling. Actual source wavelengths and bad-band metadata control available spectra.",
             "36 ±2° cross-track FOV; 600 resolved elements. The reference page's IFOV text is ambiguous; IFOV remains unspecified. The thin along-track geometry uses the documented nominal 1 mrad spatial sampling, not a verified optical IFOV.",
             "100 lines/s is a selected nominal configuration within the documented 10–100 range. Sparse saved acquisitions do not establish continuous pushbroom coverage."], LineRateHz: 100, MinimumOperatingCelsius: -40, MaximumOperatingCelsius: 50),
        new("micasense.rededge-p.multispectral", 1, "MicaSense RedEdge-P · multispectral", "multispectral-imager", SensorGeometryKind.FrameCamera,
            49.6, 38.3, 1456, 1088, [ScientificCapability.MultispectralReflectance], [Mica],
            ["Five multispectral bands; separate 2464×2056 panchromatic imager has 44.5°×37.7° FOV and 10 mm lens.",
             "Nominal camera GSD does not increase Sentinel-2 resolution or reproduce RedEdge spectral responses; red-edge/panchromatic observations require genuine compatible source bands."],
            [new("blue",475,32),new("green",560,27),new("red",668,16),new("red-edge",717,12),new("nir",842,57),new("panchromatic",634.5,463)], PixelPitchMicrometres:3.45,FocalLengthMillimetres:5.5),
        Thermal("15",15,11), Thermal("25",25,19), Thermal("45",45,34),
        new("riegl.vq-1560-ii-s.nominal",1,"RIEGL VQ-1560 II-S · maximum effective rate", "terrain-scanner",SensorGeometryKind.ScanningLidar,
            58,16,null,null,[ScientificCapability.Elevation,ScientificCapability.SurfaceModel,ScientificCapability.LidarPoints],[Riegl],
            ["58° effective FOV from two 60° scan channels; ±8° forward/backward edge look. Rectangular planning envelope approximates the documented interleaved scan pattern.",
             "Total scan rate 40–600 lines/s; PRR 2×270 kHz to 2×2000 kHz; effective measurements up to 2×1.33 MHz. Effective rate is not PRR and is not a measured return count.",
             "100 m minimum range; example 20% reflectance operating heights: 3900 m AGL at 540 kHz and 1700 m AGL at 4 MHz. Range depends on reflectance, visibility, incidence and settings; no universal pass/fail altitude is inferred.",
             "Theoretical density assumes a uniform level straight swath at the selected maximum rate. DEM values remain DEM values; no laser returns or point clouds are synthesized."],
            MeasurementRateHz:2660000,MinimumOperatingCelsius:-5,MaximumOperatingCelsius:35)
    ];
    private static InstrumentProfile Thermal(string lens,double h,double v) => new($"flir.a655sc.lens-{lens}",1,$"FLIR A655sc · {lens}° lens", "thermal-imager", SensorGeometryKind.FrameCamera,
        h,v,640,480,[ScientificCapability.SurfaceTemperature],[Flir],
        ["7.5–14 µm microbolometer, 640×480. This geometry does not confer FLIR detector accuracy, temperature calibration or spatial resolution on Landsat surface-temperature observations."],MinimumOperatingCelsius:-15,MaximumOperatingCelsius:50);
    public static InstrumentProfile Generic(string instrument) => instrument switch
    {
        "thermal-imager" => new("generic.thermal",1,"Generic thermal camera",instrument,SensorGeometryKind.FrameCamera,20,20,null,null,[ScientificCapability.SurfaceTemperature],[],["Generic user-adjustable geometry; detector resolution and calibration unspecified."]),
        "multispectral-imager" => new("generic.multispectral",1,"Generic multispectral camera",instrument,SensorGeometryKind.FrameCamera,20,20,null,null,[ScientificCapability.MultispectralReflectance],[],["Generic geometry; only retained source bands are available."]),
        "hyperspectral-imager" => new("generic.hyperspectral",1,"Generic hyperspectral camera",instrument,SensorGeometryKind.FrameCamera,20,20,null,null,[ScientificCapability.HyperspectralRadiance,ScientificCapability.HyperspectralReflectance],[],["Generic geometry; actual source wavelengths and bad bands remain authoritative."]),
        "terrain-scanner" => new("generic.terrain",1,"Generic terrain scanner",instrument,SensorGeometryKind.ScanningLidar,30,3,null,null,[ScientificCapability.Elevation,ScientificCapability.SurfaceModel,ScientificCapability.LidarPoints],[],["Generic envelope; theoretical density unavailable without a supported effective measurement rate."]),
        _ => throw new ArgumentException("This instrument does not have an optical/scanner payload profile.")
    };
    public static PayloadConfiguration Validate(PayloadConfiguration payload,string instrument)
    {
        var p=payload.Profile;
        if(p is null || p.InstrumentId!=instrument || p.Version<1 || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length>150 || string.IsNullOrWhiteSpace(p.Name)
            || p.Name.Length>200 || !Enum.IsDefined(p.Geometry) || p.CompatibleCapabilities is null || p.CompatibleCapabilities.Length>10
            || p.CompatibleCapabilities.Any(c=>!Enum.IsDefined(c)) || p.References is null || p.Limitations is null
            || p.References.Length>20 || p.Limitations.Length>30 || p.References.Concat(p.Limitations).Any(s=>s is null||s.Length>4000)
            || p.Channels is {Length:>1024} || (p.Channels??[]).Any(c=>c is null||string.IsNullOrWhiteSpace(c.Id)||c.Id.Length>150
                ||new[]{c.CentreNanometres,c.WidthNanometres}.Any(v=>v is {} n&&(!double.IsFinite(n)||n<=0||n>1000000)))
            || new[]{p.HorizontalFovDegrees,p.VerticalFovDegrees,payload.ForwardDegrees,payload.RightDegrees}.Any(v=>!double.IsFinite(v))
            || p.HorizontalFovDegrees is <=0 or >=120 || p.VerticalFovDegrees is <=0 or >=120 || Math.Abs(payload.ForwardDegrees)>60 || Math.Abs(payload.RightDegrees)>60
            || new[]{p.CrossTrackPixels,p.AlongTrackPixels}.Any(v=>v is <=0 or >100000)
            || new[]{p.IfovMilliradians,p.LineRateHz,p.MeasurementRateHz,p.PixelPitchMicrometres,p.FocalLengthMillimetres}.Any(v=>v is {} n&&(!double.IsFinite(n)||n<=0||n>100000000))
            || new[]{p.MinimumOperatingCelsius,p.MaximumOperatingCelsius}.Any(v=>v is {} n&&!double.IsFinite(n))
            || p.MinimumOperatingCelsius>p.MaximumOperatingCelsius || payload.GeometryAlgorithm!="sensor-plane-2")
            throw new ArgumentException("The payload requires compatible, finite documented parameters and bounded optical geometry.");
        if(!payload.Custom)
        {
            var reference=Catalog.FirstOrDefault(x=>x.Id==p.Id&&x.Version==p.Version) ?? (p.Id.StartsWith("generic.",StringComparison.Ordinal)?Generic(instrument):null);
            // Compare serialized content rather than record-array reference equality. Custom edits must identify themselves.
            if(reference is null || System.Text.Json.JsonSerializer.Serialize(reference,WireJson.Options)!=System.Text.Json.JsonSerializer.Serialize(p,WireJson.Options))
                throw new ArgumentException("Modified or unknown reference profiles must be explicitly identified as custom configurations.");
        }
        return payload;
    }
    public static InstrumentConfiguration[] Freeze(IEnumerable<InstrumentConfiguration> configurations) => configurations.Select(c=>RemoteAcquisition.Instruments.ContainsKey(c.InstrumentId)
        ? c with {Payload=Validate(c.Payload??new(Generic(c.InstrumentId)),c.InstrumentId)} : c).Select(InstrumentCalibration.Freeze).ToArray();
    public static SensorMount Mount(InstrumentConfiguration c) => c.Payload is {} p
        ? new(p.Profile.HorizontalFovDegrees,p.Profile.VerticalFovDegrees,p.ForwardDegrees,p.RightDegrees,p.Profile.Geometry!=SensorGeometryKind.FrameCamera)
        : c.InstrumentId=="terrain-scanner" ? new(30,3,Swath:true) : new();
    public static SensorPlanningResult Plan(SensorPlanningRequest r)
    {
        var payload=Validate(r.Instrument.Payload??new(Generic(r.Instrument.InstrumentId)),r.Instrument.InstrumentId);
        if(new[]{r.AltitudeAglMetres,r.GroundspeedMetresPerSecond,r.SideOverlap,r.ForwardOverlap}.Any(v=>!double.IsFinite(v))
            || r.AltitudeAglMetres is <1 or >60000 || r.GroundspeedMetresPerSecond is <=0 or >2000 || r.SideOverlap is <0 or >=.99 || r.ForwardOverlap is <0 or >=.99)
            throw new ArgumentException("Use positive AGL altitude/groundspeed and overlap from 0 to less than 99%.");
        var p=payload.Profile;
        var swath=2*r.AltitudeAglMetres*Math.Tan(p.HorizontalFovDegrees*Math.PI/360);
        var along=2*r.AltitudeAglMetres*Math.Tan(p.VerticalFovDegrees*Math.PI/360);
        var spacing=swath*(1-r.SideOverlap);
        PatternPreview? preview=null;
        if(r.Pattern is {} pattern)
        {
            pattern=pattern with{SpacingMetres=spacing};
            if(pattern.Kind is FlightPatternKind.Lawnmower or FlightPatternKind.CreepingLine)
                pattern=pattern.FitMode=="polygon"&&pattern.ClippingPolygon is {Length:>=3} polygon
                    ?FlightPatterns.FitSurvey(pattern,new("Selected survey",polygon),"polygon")
                    :pattern with{Legs=Math.Max(2,(int)Math.Ceiling(pattern.WidthMetres/spacing)+1)};
            preview=FlightPatterns.Generate(pattern);
        }
        var warnings=new List<string>(p.Limitations)
        {"Nadir level-flight flat-ground planning baseline. Aircraft attitude, mount tilt, relief, occlusion, source resolution and actual QA may reduce useful coverage. Heading is degrees true.",
         "Route duration excludes turns, climb, transit, acceleration and pauses. Geometric overlap is not valid scientific coverage."};
        if(payload.ForwardDegrees!=0||payload.RightDegrees!=0)warnings.Add("A tilted mount is selected; the simple nadir spacing baseline requires separate footprint review.");
        var interval=p.Geometry==SensorGeometryKind.FrameCamera?along*(1-r.ForwardOverlap)/r.GroundspeedMetresPerSecond:(double?)null;
        if(interval is {} i&&i<r.Instrument.IntervalMs/1000d)warnings.Add("The requested forward overlap requires acquisitions faster than the configured saved-acquisition interval.");
        var swaths=new List<Coordinates[]>();
        if(preview is {} route&&route.Definition.Kind is FlightPatternKind.Lawnmower or FlightPatternKind.CreepingLine)
        {
            var segments=route.Segments?.Where(s=>s.Role=="scan").ToArray()??Enumerable.Range(0,route.Waypoints.Length/2).Select(i=>new PatternRouteSegment(2*i,2*i+1,"scan")).ToArray();
            foreach(var segment in segments)
            {
                var a=Geodesy.Point(route.Waypoints[segment.Start]);var b=Geodesy.Point(route.Waypoints[segment.End]);var bearing=Geodesy.Bearing(a,b);
                swaths.Add([Geodesy.Direct(a,bearing-90,swath/2),Geodesy.Direct(b,bearing-90,swath/2),Geodesy.Direct(b,bearing+90,swath/2),Geodesy.Direct(a,bearing+90,swath/2)]);
            }
        }
        return new(payload,swath,along,spacing,p.CrossTrackPixels is {} pixels?swath/pixels:null,
            p.Geometry==SensorGeometryKind.Pushbroom ? p.LineRateHz is {} hz?r.GroundspeedMetresPerSecond/hz:null : p.AlongTrackPixels is {} rows?along/rows:null,
            interval,p.MeasurementRateHz/(swath*r.GroundspeedMetresPerSecond),preview,preview?.DistanceMetres/r.GroundspeedMetresPerSecond,warnings.ToArray(),
            "sensor-planning-1: swath=2 H tan(HFOV/2); spacing=swath(1-side overlap); frame interval=along footprint(1-forward overlap)/groundspeed; pushbroom along step=groundspeed/line rate; ideal scanner density=effective measurement rate/(swath×groundspeed)",swaths.ToArray());
    }
    public static SensorPlanningSettings ValidateSettings(SensorPlanningSettings s)
    {
        if(!RemoteAcquisition.Instruments.ContainsKey(s.InstrumentId)||new[]{s.AltitudeAglMetres,s.GroundspeedMetresPerSecond,s.SideOverlap,s.ForwardOverlap}.Any(v=>!double.IsFinite(v))
            ||s.AltitudeAglMetres is <1 or >60000||s.GroundspeedMetresPerSecond is <=0 or >2000||s.SideOverlap is <0 or >=.99||s.ForwardOverlap is <0 or >=.99)
            throw new ArgumentException("Sensor planning requires a supported instrument, positive AGL/groundspeed and overlap below 99%.");return s;
    }
}
