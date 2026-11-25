namespace AeroScience.XPlane;

internal sealed record XPlaneDataref(string Key, string Name, string ValueType, bool Required = false, int MaximumBytes = 0);
internal sealed record XPlaneResolvedDataref(XPlaneDataref Definition, long Id);
internal sealed record XPlaneDiscovery(IReadOnlyList<XPlaneResolvedDataref> Found, string[] Unavailable);

internal static class XPlaneDatarefs
{
    // Official catalog names/units: docs/research/v0.8.1-xplane-feasibility.md.
    internal const string MappingRevision = "xplane-web-v1-map1";
    internal static readonly IReadOnlyList<XPlaneDataref> All = Array.AsReadOnly<XPlaneDataref>([
        new("latitude", "sim/flightmodel/position/latitude", "double", true),
        new("longitude", "sim/flightmodel/position/longitude", "double", true),
        new("msl", "sim/flightmodel/position/elevation", "double", true),
        new("agl", "sim/flightmodel/position/y_agl", "float"),
        new("ias", "sim/flightmodel/position/indicated_airspeed", "float", true),
        new("tas", "sim/flightmodel/position/true_airspeed", "float", true),
        new("groundspeed", "sim/flightmodel/position/groundspeed", "float", true),
        new("heading", "sim/flightmodel/position/true_psi", "float", true),
        new("track", "sim/cockpit2/gauges/indicators/ground_track_true_pilot", "float", true),
        new("pitch", "sim/flightmodel/position/theta", "float", true),
        new("bank", "sim/flightmodel/position/phi", "float", true),
        new("vertical-speed", "sim/flightmodel/position/vh_ind", "float", true),
        new("mach", "sim/flightmodel/misc/machno", "float"),
        new("aircraft", "sim/aircraft/view/acf_descrip", "data", true, 260),
        new("registration", "sim/aircraft/view/acf_tailnum", "data", MaximumBytes: 40),
        new("aircraft-type", "sim/aircraft/view/acf_ICAO", "data", MaximumBytes: 40),
        new("aircraft-token", "sim/aircraft/view/acf_relative_path", "data", MaximumBytes: 4096),
        new("paused", "sim/time/paused", "int", true),
        new("running-seconds", "sim/time/total_running_time_sec", "float", true),
        new("flight-seconds", "sim/time/total_flight_time_sec", "float", true),
        new("replay", "sim/time/is_in_replay", "int", true),
        new("zulu-seconds", "sim/time/zulu_time_sec", "float"),
        new("local-day", "sim/time/local_date_days", "int"),
        new("zulu-day", "sim/time/zulu_date_days", "int"),
        new("requested-rate", "sim/time/sim_speed", "int"),
        new("actual-rate", "sim/time/sim_speed_actual", "float"),
        new("dilation-rate", "sim/time/sim_speed_actual_ogl", "float"),
        new("distance-rate", "sim/time/ground_speed_flt", "float"),
        new("build", "sim/version/xplane_internal_version", "int"),
        new("temperature", "sim/weather/aircraft/temperature_ambient_deg_c", "float"),
        new("pressure", "sim/weather/aircraft/barometer_current_pas", "float"),
        new("wind-speed", "sim/weather/aircraft/wind_now_speed_msc", "float"),
        new("wind-direction", "sim/weather/aircraft/wind_now_direction_degt", "float"),
        new("density", "sim/weather/rho", "float")
    ]);
    internal static bool KnownName(string name) => All.Any(d => d.Name == name);
    internal static double Feet(double metres) => metres / .3048;
    internal static double Knots(double metresPerSecond) => metresPerSecond * 3600 / 1852;
    internal static double FeetPerMinute(double metresPerSecond) => metresPerSecond * 60 / .3048;
    internal static double Heading(double degrees) => (degrees % 360 + 360) % 360;
    internal static double InHg(double pascals) => pascals / 3386.389;
    internal static double SlugsPerCubicFoot(double kgPerCubicMetre) => kgPerCubicMetre * .028316846592 / 14.5939029372064;
}
