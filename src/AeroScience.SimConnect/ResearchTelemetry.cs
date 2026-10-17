using System.Runtime.InteropServices;
using AeroScience.Shared;

namespace AeroScience.SimConnect;

// Auxiliary definitions are independent of the proven 360-byte aircraft definition.
public static class ResearchTelemetry
{
    public static readonly (string Name, string Unit)[] AtmosphericVariables =
    [
        ("AMBIENT TEMPERATURE", "celsius"), ("AMBIENT PRESSURE", "inHg"),
        ("AMBIENT WIND VELOCITY", "knots"), ("AMBIENT WIND DIRECTION", "degrees"),
        ("AMBIENT DENSITY", "slugs per cubic feet")
    ];
    public static readonly (string Name, string Unit)[] GuidanceVariables =
    [
        ("AUTOPILOT AVAILABLE", "bool"), ("AUTOPILOT MASTER", "bool"),
        ("AUTOPILOT HEADING LOCK", "bool"), ("AUTOPILOT NAV1 LOCK", "bool"),
        ("AUTOPILOT APPROACH HOLD", "bool"), ("AUTOPILOT APPROACH CAPTURED", "bool"),
        ("AUTOPILOT APPROACH IS LOCALIZER", "bool"), ("AUTOPILOT WING LEVELER", "bool"),
        ("AUTOPILOT BANK HOLD", "bool"), ("AUTOPILOT ALTITUDE LOCK", "bool"),
        ("AUTOPILOT VERTICAL HOLD", "bool"), ("AUTOPILOT FLIGHT LEVEL CHANGE", "bool"),
        ("AUTOPILOT GLIDESLOPE ACTIVE", "bool"), ("AUTOPILOT GLIDESLOPE ARM", "bool"),
        ("AUTOPILOT PITCH HOLD", "bool"), ("AUTOPILOT AIRSPEED HOLD", "bool"),
        ("AUTOPILOT MACH HOLD", "bool"), ("AUTOPILOT ALTITUDE ARM", "bool"),
        ("AUTOPILOT HEADING SLOT INDEX", "number"), ("AUTOPILOT ALTITUDE SLOT INDEX", "number"),
        ("AUTOPILOT HEADING LOCK DIR:0", "degrees"), ("AUTOPILOT HEADING LOCK DIR:1", "degrees"),
        ("AUTOPILOT HEADING LOCK DIR:2", "degrees"), ("AUTOPILOT HEADING LOCK DIR:3", "degrees"),
        ("AUTOPILOT ALTITUDE LOCK VAR:0", "feet"), ("AUTOPILOT ALTITUDE LOCK VAR:1", "feet"),
        ("AUTOPILOT ALTITUDE LOCK VAR:2", "feet"), ("AUTOPILOT ALTITUDE LOCK VAR:3", "feet"),
        ("AUTOPILOT VERTICAL HOLD VAR", "feet per minute"),
        ("AUTOPILOT AIRSPEED HOLD VAR", "knots"), ("AUTOPILOT MACH HOLD VAR", "number")
    ];
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AtmosphericData { public double Temperature, Pressure, WindSpeed, WindDirection, Density; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GuidanceData
    {
        public double Available, Master, HeadingLock, NavLock, ApproachHold, ApproachCaptured, IsLocalizer,
            WingLeveler, BankHold, AltitudeLock, VerticalHold, FlightLevelChange, GlideslopeActive, GlideslopeArm,
            PitchHold, AirspeedHold, MachHold, AltitudeArm, HeadingSlot, AltitudeSlot,
            Heading0, Heading1, Heading2, Heading3, Altitude0, Altitude1, Altitude2, Altitude3, VerticalSpeed, Ias, Mach;
    }
    public static AtmosphericFrame DecodeAtmosphere(AtmosphericData raw, DateTimeOffset timestamp)
    {
        static double? Value(double value, bool supported) => double.IsFinite(value) && supported ? value : null;
        var temperature = Value(raw.Temperature, raw.Temperature >= -273.15);
        var pressure = Value(raw.Pressure, raw.Pressure > 0);
        var wind = Value(raw.WindSpeed, raw.WindSpeed >= 0);
        var direction = Value(raw.WindDirection, raw.WindDirection is >= 0 and <= 360);
        var density = Value(raw.Density, raw.Density > 0);
        return new(timestamp.ToUniversalTime(), temperature, pressure, wind, direction, density,
            temperature is null && pressure is null && wind is null && direction is null && density is null
                ? ObservationQuality.Unavailable : ObservationQuality.Valid);
    }
    private static bool? Flag(double value) => value == 0 ? false : value == 1 ? true : null;
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static double? Selected(double index, params double[] slots) =>
        double.IsFinite(index) && index == Math.Truncate(index) && index is >= 0 and <= 3 ? Finite(slots[(int)index]) : null;
    private static GuidanceMode Mode(params (string Name, bool? Enabled, double? Target, string? Unit)[] candidates)
    {
        if (candidates.Any(c => c.Enabled is null)) return new(null, Status: "Standard mode indicators unavailable");
        var active = candidates.Where(c => c.Enabled == true).DistinctBy(c => c.Name).ToArray();
        return active.Length switch
        {
            0 => new(null, Status: "No standard active mode reported"),
            1 => new(active[0].Name, active[0].Target, active[0].Unit),
            _ => new(null, Status: "Conflicting active indicators; mode unavailable")
        };
    }
    public static FlightGuidance DecodeGuidance(GuidanceData r, DateTimeOffset timestamp)
    {
        var unavailable = new GuidanceMode(null, Status: "Unavailable");
        if (Flag(r.Available) != true || Flag(r.Master) is null)
            return new(timestamp.ToUniversalTime(), AutopilotEngagement.Unavailable, unavailable, unavailable, unavailable, ObservationQuality.Unavailable);
        if (Flag(r.Master) == false)
            return new(timestamp.ToUniversalTime(), AutopilotEngagement.Disengaged,
                new(null, Status: "AP disengaged"), new(null, Status: "AP disengaged"), new(null, Status: "Inactive"));
        // LOC is a classification of a captured NAV approach, not a guess from a selected target.
        var nav = Flag(r.NavLock);
        var navName = nav == true && Flag(r.ApproachHold) == true && Flag(r.ApproachCaptured) == true && Flag(r.IsLocalizer) == true ? "LOC" : "NAV";
        var lateral = Mode(("HDG", Flag(r.HeadingLock), Selected(r.HeadingSlot, r.Heading0, r.Heading1, r.Heading2, r.Heading3), "deg"),
            (navName, nav, null, null), ("ROLL", Flag(r.WingLeveler), null, null), ("ROLL", Flag(r.BankHold), null, null));
        var vertical = Mode(("ALT", Flag(r.AltitudeLock), Selected(r.AltitudeSlot, r.Altitude0, r.Altitude1, r.Altitude2, r.Altitude3), "ft"),
            ("VS", Flag(r.VerticalHold), Finite(r.VerticalSpeed), "ft/min"),
            ("FLC", Flag(r.FlightLevelChange), null, null), ("GS", Flag(r.GlideslopeActive), null, null),
            ("PITCH", Flag(r.PitchHold), null, null));
        var armed = Flag(r.GlideslopeArm) == true && Flag(r.GlideslopeActive) == false ? "GS (SDK arm indication)"
            : Flag(r.AltitudeArm) == true && Flag(r.AltitudeLock) == false ? "ALT" : null;
        vertical = vertical with { Armed = armed };
        var speed = Mode(("IAS", Flag(r.AirspeedHold), r.Ias >= 0 ? Finite(r.Ias) : null, "kt"),
            ("MACH", Flag(r.MachHold), r.Mach >= 0 ? Finite(r.Mach) : null, "mach"));
        return new(timestamp.ToUniversalTime(), AutopilotEngagement.Engaged, lateral, vertical, speed);
    }
}
