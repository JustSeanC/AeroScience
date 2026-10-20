using AeroScience.Shared;

namespace AeroScience.Missions;

public static class ScienceCatalog
{
    public static readonly InstrumentDefinition[] Definitions =
    [
        new("ambient-temperature", "Ambient thermometer", "Temperature reported by the simulator at the user aircraft.", "MSFS 2024 / SimConnect",
            [new("temperature", "Ambient temperature", "degC", "AMBIENT TEMPERATURE")]),
        new("ambient-pressure", "Ambient pressure", "Static ambient pressure reported by the simulator.", "MSFS 2024 / SimConnect",
            [new("pressure", "Ambient pressure", "inHg", "AMBIENT PRESSURE")]),
        new("ambient-wind", "Ambient wind", "Simulator wind speed and direction relative to true north.", "MSFS 2024 / SimConnect",
            [new("wind-speed", "Wind speed", "kt", "AMBIENT WIND VELOCITY"), new("wind-direction", "Wind direction", "degT", "AMBIENT WIND DIRECTION")]),
        new("ambient-density", "Ambient density", "Simulator ambient density; no independent atmospheric model is substituted.", "MSFS 2024 / SimConnect",
            [new("density", "Ambient density", "slug/ft3", "AMBIENT DENSITY")]),
        new("thermal-imager", "Thermal Imager", "Landsat Collection 2 surface temperature sampled along the simulated footprint; not ambient air temperature.", "Automatic Landsat C2 L2SP ST_B10 / QA_PIXEL / QA_RADSAT; advanced import also supported", []),
        new("multispectral-imager", "Multispectral Imager", "Natural color, infrared and NDVI from a real Sentinel-2 L2A scene.", "Automatic Sentinel-2 L2A reflectance COGs and SCL; advanced import also supported", []),
        new("hyperspectral-imager", "Hyperspectral Imager", "Real wavelength profiles from supported geographically referenced ENVI collections; limited source coverage.", "NASA catalog discovery only; automatic preparation unsupported. Advanced ENVI reflectance/radiance import", []),
        new("terrain-scanner", "Terrain Scanner (LiDAR / Elevation)", "DEM-derived elevations with an explicit source vertical datum. These are not airborne LiDAR returns.", "Automatic USGS 3DEP bare-earth / Copernicus DSM fallback; advanced DEM import also supported", [])
    ];
    public static readonly MissionTemplate[] Templates =
    [
        new(MissionTemplateKind.GeneralResearch, "General Research", "Record a flexible aircraft-agnostic research mission.", [], ["observation", "note"]),
        new(MissionTemplateKind.AtmosphericProfile, "Vertical Profile", "Observe simulator atmospheric conditions across altitude changes.",
            Definitions.Where(d => !RemoteAcquisition.Instruments.ContainsKey(d.Id)).Select(d => new InstrumentConfiguration(d.Id, true, 1000)).ToArray(), ["interesting-weather", "observation"]),
        new(MissionTemplateKind.GeographicSurvey, "Geographic Survey", "Observe a region along a planned route and mark survey activity.",
            [new("ambient-temperature", true, 2000), new("ambient-wind", true, 2000)], ["start-survey", "end-survey", "finding"]),
        new(MissionTemplateKind.ObservationFlight, "Observation Flight", "Visit points of interest and record research notes.",
            [new("ambient-temperature", true, 5000)], ["observation", "finding", "equipment-note"]),
        new(MissionTemplateKind.CloudStructureSurvey,"Cloud Structure Survey","Observe cloud entries, exits and local density along the actual aircraft path.",
            [new("ambient-temperature",true),new("ambient-wind",true),new("ambient-pressure",true)], ["interesting-weather","observation"]),
        new(MissionTemplateKind.AtmosphericTransect,"Atmospheric Transect","Compare local atmospheric observations along a geographic transect.",
            [new("ambient-temperature",true),new("ambient-wind",true),new("ambient-pressure",true),new("ambient-density",true)], ["interesting-weather","observation"])
    ];
    public static readonly string[] MarkerCategories = ["observation", "start-survey", "end-survey", "interesting-weather", "finding", "equipment-note", "note"];
    public static InstrumentConfiguration[] Normalize(IEnumerable<InstrumentConfiguration>? configurations)
    {
        var selected = (configurations ?? []).ToArray();
        if (selected.Select(c => c.InstrumentId).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw new ArgumentException("An instrument can be configured only once per mission.");
        foreach (var config in selected)
        {
            if (!Definitions.Any(d => d.Id == config.InstrumentId) || config.IntervalMs is < 1000 or > 60000)
                throw new ArgumentException("Select a known instrument and an acquisition interval of 1–60 seconds. The SDK source currently supplies at most 1 Hz.");
            if(config.Payload is {} payload)InstrumentProfiles.Validate(payload,config.InstrumentId);
        }
        return Definitions.Select(d => InstrumentCalibration.Freeze(selected.FirstOrDefault(c => c.InstrumentId == d.Id) ?? new(d.Id))).ToArray();
    }
    public static IScienceInstrument Instrument(string id) => new AtmosphericInstrument(
        Definitions.SingleOrDefault(d => d.Id == id) ?? throw new ArgumentException("Unknown instrument."));
    private sealed class AtmosphericInstrument(InstrumentDefinition definition) : IScienceInstrument
    {
        public InstrumentDefinition Definition => definition;
        public IReadOnlyList<MeasurementReading> Read(AtmosphericFrame source) => definition.Measurements.Select(m =>
        {
            var value = m.Id switch
            {
                "temperature" => source.TemperatureCelsius,
                "pressure" => source.PressureInHg,
                "wind-speed" => source.WindSpeedKnots,
                "wind-direction" => source.WindDirectionTrueDegrees,
                "density" => source.DensitySlugsPerCubicFoot,
                _ => null
            };
            if (value is { } v && !double.IsFinite(v)) value = null;
            return new MeasurementReading(m.Id, value, m.Unit, Quality:
                source.Quality != ObservationQuality.Valid ? source.Quality : value is null ? ObservationQuality.Unavailable : ObservationQuality.Valid);
        }).ToArray();
    }
}

public static class MissionPlanning
{
    public static bool Position(double latitude, double longitude) => double.IsFinite(latitude) && latitude is >= -90 and <= 90
        && double.IsFinite(longitude) && longitude is >= -180 and <= 180;
    public static string? Text(string? value, int maximum = 4000)
    {
        if (value is null) return null;
        if (value.Length > maximum || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"Text must be at most {maximum} characters without non-text control characters.");
        return value.Trim();
    }
    public static Guid StableWaypointId(Guid route, int index) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"AeroScience:{route:N}:{index}"))[..16]);
    public static MissionPlan Validate(MissionPlan plan)
    {
        var name = Text(plan.Name, 100);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the mission plan a name.");
        if (!Enum.IsDefined(plan.Template) || !Enum.IsDefined(plan.Mode)) throw new ArgumentException("Unknown template or science mode.");
        if (plan.FlightRules is not ("VFR" or "IFR") || plan.CruisingAltitudeFeet is { } cruise && (!double.IsFinite(cruise) || cruise is < -2000 or > 100000)) throw new ArgumentException("Choose VFR/IFR and an optional cruising altitude of -2,000 to 100,000 ft MSL.");
        if (new[] { plan.MinimumAltitudeFeet, plan.MaximumAltitudeFeet, plan.EstimatedDurationMinutes }.Any(v => v is { } n && !double.IsFinite(n))
            || plan.EstimatedDurationMinutes is <= 0 or > 43200
            || plan.MinimumAltitudeFeet > plan.MaximumAltitudeFeet) throw new ArgumentException("Use finite altitude limits and a positive mission duration (up to 30 days).");
        var waypoints = plan.Waypoints ?? [];
        var areas = plan.SurveyAreas ?? [];
        if (waypoints.Length > 1000 || areas.Length > 20 || areas.Sum(a => a.Vertices?.Length ?? 0) > 1000)
            throw new ArgumentException("Plans support up to 1,000 waypoints, 20 areas and 1,000 total polygon vertices.");
        waypoints = waypoints.Select((w, index) =>
        {
            if (!Position(w.Latitude, w.Longitude) || w.TargetAltitudeFeet is { } altitude && !double.IsFinite(altitude))
                throw new ArgumentException("Waypoints require valid WGS84 latitude/longitude and a finite optional altitude.");
            var ident = Text(w.NavigationIdentifier, 8)?.ToUpperInvariant();
            if (!string.IsNullOrEmpty(ident) && !System.Text.RegularExpressions.Regex.IsMatch(ident, "^[A-Z0-9]{2,8}$")) throw new ArgumentException("A published navigation identifier must be 2–8 letters/digits, or left empty for a custom waypoint.");
            return w with { Id = w.Id ?? StableWaypointId(plan.Id, index), Name = Text(w.Name, 100) ?? "Waypoint", Instructions = Text(w.Instructions), Objective = Text(w.Objective), NavigationIdentifier = string.IsNullOrEmpty(ident) ? null : ident };
        }).ToArray();
        if (waypoints.Select(w => w.Id).Distinct().Count() != waypoints.Length) throw new ArgumentException("Each waypoint must have a distinct stable identifier. Duplicate coordinates are permitted; duplicate identifiers are not.");
        areas = areas.Select(a =>
        {
            if (a.Vertices is null || a.Vertices.Length < 3 || a.Vertices.Any(p => !Position(p.Latitude, p.Longitude)))
                throw new ArgumentException("Survey polygons require at least three valid WGS84 vertices.");
            SurveyGeometry.Validate(a.Vertices);
            return a with { Name = Text(a.Name, 100) ?? "Survey area", Vertices = a.Vertices.ToArray() };
        }).ToArray();
        return plan with
        {
            Name = name,
            Description = Text(plan.Description),
            Objective = Text(plan.Objective),
            IntendedAircraft = Text(plan.IntendedAircraft, 200),
            Departure = Text(plan.Departure, 200),
            Destination = Text(plan.Destination, 200),
            PreflightNotes = Text(plan.PreflightNotes),
            Patterns = (plan.Patterns ?? []).Select(FlightPatterns.Validate).ToArray(),
            AtmosphericSettings = plan.AtmosphericSettings is {} settings ? AtmosphericAnalysis.Validate(settings) : null,
            SensorPlanning=plan.SensorPlanning is {} sensor?InstrumentProfiles.ValidateSettings(sensor):null,
            Waypoints = waypoints,
            SurveyAreas = areas,
            Instruments = InstrumentProfiles.Freeze(ScienceCatalog.Normalize(plan.Instruments ?? ScienceCatalog.Templates.Single(t => t.Id == plan.Template).Instruments)),
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }
}
