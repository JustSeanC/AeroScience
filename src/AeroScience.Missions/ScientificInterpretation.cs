using AeroScience.Shared;
namespace AeroScience.Missions;

public sealed record DescriptiveTrend(string Measurement, string Explanation, long IndependentPackets, double? Correlation, double? SlopePerMetre, string Unit);
public static class ScientificInterpretation
{
    private sealed class Accumulator
    {
        public long N; public double MeanX, MeanY, XX, YY, XY, MinimumX = double.PositiveInfinity, MaximumX = double.NegativeInfinity; public DateTimeOffset? Last;
        public void Add(double x, double y, DateTimeOffset time)
        { if (Last is { } last && time <= last || !double.IsFinite(x) || !double.IsFinite(y)) return; Last = time; N++; var dx = x - MeanX; MeanX += dx / N; var dy = y - MeanY; MeanY += dy / N; XX += dx * (x - MeanX); YY += dy * (y - MeanY); XY += dx * (y - MeanY); MinimumX = Math.Min(x, MinimumX); MaximumX = Math.Max(x, MaximumX); }
    }
    public static DescriptiveTrend[] Describe(IEnumerable<TelemetryFrame> samples)
    {
        var temperature = new Accumulator(); var pressure = new Accumulator();
        foreach (var frame in samples)
        {
            if (!frame.IsValid || frame.IsPaused || frame.Atmosphere is not { Quality: ObservationQuality.Valid } atmosphere || Math.Abs((frame.TimestampUtc - atmosphere.TimestampUtc).TotalSeconds) > 3) continue;
            var altitude = frame.AltitudeMslFeet * .3048;
            if (atmosphere.TemperatureCelsius is { } t) temperature.Add(altitude, t, atmosphere.TimestampUtc);
            if (atmosphere.PressureInHg is { } p) pressure.Add(altitude, p, atmosphere.TimestampUtc);
        }
        DescriptiveTrend Result(string name, string unit, Accumulator a)
        {
            var correlation = a.XX > 0 && a.YY > 0 ? a.XY / Math.Sqrt(a.XX * a.YY) : (double?)null;
            var supported = a.N >= 6 && a.MaximumX - a.MinimumX >= 100 && correlation is { } r && Math.Abs(r) >= .7;
            return new(name, supported ? $"Across {a.N} distinct simulator packets, {name} generally {(correlation > 0 ? "increased" : "decreased")} with recorded altitude. This is a descriptive association across the flight; location and time also changed. It does not establish causation or a calibrated atmospheric sounding." : $"The saved {name} packets do not support a clear altitude trend under the stated minimum sample, altitude-span and correlation rules.", a.N, correlation, a.XX > 0 ? a.XY / a.XX : null, unit);
        }
        return [Result("temperature", "°C", temperature), Result("pressure", "inHg", pressure)];
    }
}
