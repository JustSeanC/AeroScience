namespace AeroScience.Shared;

public sealed record ScienceUnitPreferences(string Temperature = "celsius", string Pressure = "inHg", string WindSpeed = "knots",
    string Density = "kg/m3", string Altitude = "feet", string Distance = "nauticalMiles",string? PlanningProfile=null);
public sealed record UnitConversion(string Id, string Label, double Scale, double Offset = 0, int Decimals = 2);
public static class ScienceUnits
{
    // Each conversion maps the canonical stored SDK unit to a display unit. Raw records are never rewritten.
    public static readonly IReadOnlyDictionary<string, UnitConversion[]> Options = new Dictionary<string, UnitConversion[]>
    {
        ["temperature"] = [new("celsius", "°C", 1), new("fahrenheit", "°F", 1.8, 32), new("kelvin", "K", 1, 273.15)],
        ["pressure"] = [new("inHg", "inHg", 1), new("hPa", "hPa / mbar", 33.86389), new("kPa", "kPa", 3.386389)],
        ["windSpeed"] = [new("knots", "kt", 1), new("metresPerSecond", "m/s", 1852d / 3600), new("kilometresPerHour", "km/h", 1.852)],
        ["windDirection"] = [new("degreesTrue", "° true", 1, 0, 0)],
        ["density"] = [new("kg/m3", "kg/m³", 14.59390293720636 / Math.Pow(.3048, 3), 0, 3), new("slug/ft3", "slug/ft³", 1, 0, 6)],
        ["altitude"] = [new("feet", "ft MSL", 1, 0, 0), new("metres", "m MSL", .3048, 0, 0)],
        ["distance"] = [new("metres", "m", 1), new("nauticalMiles", "NM", 1d / 1852), new("kilometres", "km", .001), new("miles", "mi", 1d / 1609.344)]
    };
    public static ScienceUnitPreferences Aviation => new();
    public static ScienceUnitPreferences Scientific => new(Pressure: "hPa", WindSpeed: "metresPerSecond", Altitude: "metres", Distance: "kilometres");
    public static ScienceUnitPreferences Validate(ScienceUnitPreferences preferences)
    {
        foreach (var (category, value) in new[]{("temperature",preferences.Temperature),("pressure",preferences.Pressure),("windSpeed",preferences.WindSpeed),
            ("density",preferences.Density),("altitude",preferences.Altitude),("distance",preferences.Distance)})
            if (!Options[category].Any(option => option.Id == value)) throw new ArgumentException("Choose a supported " + category + " unit.");
        if(preferences.PlanningProfile is not(null or "aviation" or "metric"))throw new ArgumentException("Choose Aviation or Metric for planning units.");
        return preferences;
    }
    public static UnitConversion Conversion(string measurement, ScienceUnitPreferences preferences)
    {
        var original = measurement.EndsWith(".user-adjusted", StringComparison.Ordinal) ? measurement[..^14] : measurement;
        var (category, id) = original switch
        {
            "temperature" => ("temperature", preferences.Temperature),
            "pressure" => ("pressure", preferences.Pressure),
            "wind-speed" => ("windSpeed", preferences.WindSpeed),
            "wind-direction" => ("windDirection", "degreesTrue"),
            "density" => ("density", preferences.Density),
            "altitude" => ("altitude", preferences.Altitude),
            "distance" => ("distance", preferences.Distance),
            _ => throw new ArgumentException("Unknown measurement category.")
        };
        return Options[category].Single(c => c.Id == id);
    }
    public static MeasurementReading Display(MeasurementReading reading, ScienceUnitPreferences preferences)
    {
        var conversion = Conversion(reading.MeasurementId, preferences);
        var number = reading.Value * conversion.Scale + conversion.Offset;
        return reading with { DisplayValue = number is { } value && double.IsFinite(value) ? value : null, DisplayUnit = conversion.Label };
    }
}
