using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

/// <summary>Bounded pure assessments. No acquisition veto, provider/raster I/O or aircraft restriction.</summary>
public static class InstrumentOperatingAssessment
{
    public static InstrumentOperatingReport Evaluate(Guid mission, Guid? session, EffectiveInstrumentConfiguration config,
        ScienceMode mode, TelemetryUpdate input, MissionPlan? plan = null, RemoteInstrumentStatus? remote = null,
        double? actualIntervalSeconds = null, DateTimeOffset? evaluatedUtc = null)
    {
        var frame = input.Frame; var settings = config.Settings; var payload = settings.Payload;
        var packet = new CollectionInputReceipt("assessment:" + Guid.NewGuid().ToString("N"), input);
        var evidence = MissionStore.DescribeCollectionInput(packet);
        var rules = new List<OperatingRuleAssessment>();
        void Add(string id, string parameter, string unit, double? value, AssessmentStatus compliance,
            AssessmentStatus validity, AssessmentStatus qa, string reason, params string[] affected)
        {
            // Hash rule content/basis, independent of the actual observation and outcome.
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("operating-1:"+id+":"+parameter+":"+unit)));
            rules.Add(new(new(id, 1, hash), parameter, unit, value, compliance, validity, qa, [reason], affected));
        }
        var age = frame is null ? (double?)null : ((evaluatedUtc??DateTimeOffset.UtcNow)-frame.TimestampUtc).TotalSeconds;
        var fresh = frame is { IsValid: true, IsPaused: false } && input.Connection.State == ConnectionState.Connected && age is >= -2 and <= 6;
        var atmosphere = input.Atmosphere ?? frame?.Atmosphere;
        if (!RemoteAcquisition.Instruments.ContainsKey(settings.InstrumentId))
        {
            foreach (var reading in atmosphere is null ? [] : ScienceCatalog.Instrument(settings.InstrumentId).Read(atmosphere))
            {
                var good = reading.Quality == ObservationQuality.Valid && reading.Value is not null;
                Add("simulator."+reading.MeasurementId, reading.MeasurementId, reading.Unit, reading.Value,
                    AssessmentStatus.Unknown, good ? AssessmentStatus.Supported : AssessmentStatus.Unavailable,
                    good ? AssessmentStatus.Supported : AssessmentStatus.Unavailable,
                    "Actual simulator channel availability; no undocumented physical operating envelope is imposed. " + ScienceCatalog.Definitions.Single(d=>d.Id==settings.InstrumentId).DataSource);
                if (InstrumentCalibration.Derive(settings, reading, atmosphere!.TimestampUtc) is {} derived)
                    Add("calibration."+reading.MeasurementId, "user-defined adjustment", reading.Unit, derived.Value,
                        AssessmentStatus.Unknown, derived.Value is null ? AssessmentStatus.Invalid : AssessmentStatus.Supported,
                        good ? AssessmentStatus.Supported : AssessmentStatus.Unavailable,
                        "User affine applicability/physical scalar checks only; original measurements remain unchanged.", derived.MeasurementId);
            }
            if (atmosphere is null) Add("simulator.missing", "atmospheric packet", "1", null, AssessmentStatus.Unknown,
                AssessmentStatus.NotAssessed, AssessmentStatus.Missing, "No independent atmospheric packet is present; no measurement was inferred.");
        }
        else
        {
            ObservationFootprint? footprint = null;
            string geometryReason = "Current aircraft position and positive AGL are required; absent evidence is not an observed geometry failure.";
            var geometry = AssessmentStatus.NotAssessed;
            if (fresh)
            {
                try { footprint = SensorGeometry.ForInstrument(frame!, settings); geometry = AssessmentStatus.Supported; geometryReason = footprint.Method+". "+string.Join(" ",footprint.Limitations); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                { geometry = frame!.AltitudeAglFeet is null ? AssessmentStatus.NotAssessed : AssessmentStatus.Invalid; geometryReason = e.Message; }
            }
            Add("geometry.ground-intersection", "AGL / ray-plane intersection", "m", frame?.AltitudeAglFeet*.3048,
                geometry == AssessmentStatus.Invalid ? AssessmentStatus.Caution : geometry == AssessmentStatus.Supported ? AssessmentStatus.Supported : AssessmentStatus.Unknown,
                geometry, AssessmentStatus.NotAssessed, geometryReason, "footprint", "geographic-product");
            Add("aircraft.msl", "altitude MSL", "ft", frame?.AltitudeMslFeet, AssessmentStatus.Unknown, AssessmentStatus.NotAssessed,
                AssessmentStatus.NotAssessed, "MSL is recorded, not substituted for AGL or used as an undocumented sensor limit.");
            foreach (var (name, value) in new[] { ("pitch", frame?.PitchDegrees), ("bank", frame?.BankDegrees),
                         ("mount-forward", payload?.ForwardDegrees), ("mount-right", payload?.RightDegrees) })
                Add("orientation."+name, name, "deg", value, AssessmentStatus.Unknown, geometry, AssessmentStatus.NotAssessed,
                    "Actual aircraft/mount orientation enters the documented sensor-plane-2 calculation. No universal attitude cutoff or aircraft whitelist is invented.", "footprint");
            var speed = frame?.GroundspeedKnots * .5144444444444445;
            var interval = actualIntervalSeconds ?? settings.IntervalMs/1000d;
            var spacing = speed * interval;
            Add("cadence.spacing", "along-track sample spacing", "m", spacing,
                fresh && actualIntervalSeconds > settings.IntervalMs/1000d * 1.5 ? AssessmentStatus.Caution : AssessmentStatus.Unknown,
                fresh && spacing is not null ? AssessmentStatus.Supported : AssessmentStatus.NotAssessed, AssessmentStatus.NotAssessed,
                $"Groundspeed × {(actualIntervalSeconds is null ? "configured" : "actual receipt")} interval ({interval:0.###} s). Saved packet spacing is not continuous detector sampling or valid coverage.");
            if (footprint is not null && plan?.SensorPlanning is {} planning && planning.InstrumentId == settings.InstrumentId)
            {
                var b = footprint.Boundary;
                var along = (Geodesy.Distance(b[0],b[3])+Geodesy.Distance(b[1],b[2]))/2;
                var across = (Geodesy.Distance(b[0],b[1])+Geodesy.Distance(b[3],b[2]))/2;
                var overlap = along>0 && spacing is {} distance ? 1-distance/along : (double?)null;
                Add("planning.forward-overlap", "modeled forward overlap", "1", overlap,
                    overlap is null ? AssessmentStatus.Unknown : overlap >= planning.ForwardOverlap ? AssessmentStatus.Supported : AssessmentStatus.Caution,
                    AssessmentStatus.Supported, AssessmentStatus.NotAssessed, "Actual modeled plane footprint and packet spacing versus the saved user overlap target; not valid source-pixel coverage.");
                var track = plan.Patterns?.Where(p=>p.Kind is FlightPatternKind.Lawnmower or FlightPatternKind.CreepingLine).Select(p=>p.SpacingMetres).DefaultIfEmpty().Max();
                if (track > 0) Add("planning.side-overlap", "modeled side overlap", "1", 1-track/across,
                    1-track/across >= planning.SideOverlap ? AssessmentStatus.Supported : AssessmentStatus.Caution,
                    AssessmentStatus.Supported, AssessmentStatus.NotAssessed, "Saved parallel-track spacing versus current modeled swath. Relief, masks and original source support may reduce usable coverage.");
            }
            else Add("planning.overlap", "survey overlap target", "1", null, AssessmentStatus.NotAssessed, AssessmentStatus.NotAssessed,
                AssessmentStatus.NotAssessed, "No applicable saved sensor/overlap target; no completeness percentage is invented.");
            if (payload?.Profile is {} profile)
            {
                Add("environment.payload-temperature", "payload operating temperature", "degC", null, AssessmentStatus.Unknown,
                    AssessmentStatus.NotAssessed, AssessmentStatus.NotAssessed,
                    $"Documented profile range {profile.MinimumOperatingCelsius?.ToString()??"Unknown"} to {profile.MaximumOperatingCelsius?.ToString()??"Unknown"} degC. Payload internal temperature is unavailable; simulator ambient is not a proxy. References: "+string.Join(" ",profile.References));
                if (profile.Id.StartsWith("riegl.",StringComparison.Ordinal))
                {
                    var range=footprint is null?(double?)null:footprint.HeightAbovePlaneMetres/SensorGeometry.Direction(InstrumentProfiles.Mount(settings),frame!.PitchDegrees,frame.BankDegrees,0,0).Z;
                    Add("riegl.conditional-range", "modeled center-ray slant range", "m", range,
                        range is null ? AssessmentStatus.Unknown : range<100 ? AssessmentStatus.Caution : AssessmentStatus.Unknown,
                        geometry, AssessmentStatus.NotAssessed,
                        "RIEGL documents 100 m minimum range; operating-height examples depend on reflectance, PRR, incidence and visibility. No universal maximum AGL pass/fail or measured laser returns. "+string.Join(" ",profile.References));
                }
                if(profile.Id.StartsWith("nasa.aviris-ng",StringComparison.Ordinal))
                    Add("aviris-ng.nominal-line-rate","configured nominal line rate","Hz",profile.LineRateHz,
                        profile.LineRateHz is null?AssessmentStatus.Unknown:profile.LineRateHz is >=10 and <=100?AssessmentStatus.Supported:AssessmentStatus.Caution,
                        AssessmentStatus.NotAssessed,AssessmentStatus.NotAssessed,
                        "NASA documents 10–100 lines/s. This is configured modeled sampling, not measured aircraft detector operation or continuous saved imagery. "+string.Join(" ",profile.References));
            }
            var qa = remote?.Quality == ObservationQuality.Valid && frame is not null && remote.LastSuccessfulUtc is {} last && Math.Abs((frame.TimestampUtc-last).TotalSeconds)<=6 ? AssessmentStatus.Supported
                : remote?.Quality == ObservationQuality.Unavailable ? AssessmentStatus.Unavailable : AssessmentStatus.Unknown;
            Add("source.availability", "last source acquisition", "1", remote?.LastSuccessfulUtc is null ? null : 1,
                AssessmentStatus.Unknown, AssessmentStatus.NotAssessed, qa,
                "Cached independent source status: "+(remote?.Message??"Not yet assessed")+". Last success is not a guarantee of future coverage or every pixel's validity.");
        }
        var at = evidence.ObservedUtc;
        var assessment = new OperatingAssessment(1, mission, session, config.Id, at, rules.ToArray(), [evidence]);
        EvidenceContracts.Validate(assessment);
        return new(mode, config, assessment, input);
    }
}
