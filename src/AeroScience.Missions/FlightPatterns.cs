using AeroScience.Shared;
using GeographicLib;

namespace AeroScience.Missions;

public static class Geodesy
{
    public static double Distance(Coordinates a, Coordinates b)
    { Geodesic.WGS84.Inverse(a.Latitude, a.Longitude, b.Latitude, b.Longitude, out double metres); return metres; }
    public static Coordinates Direct(Coordinates start, double bearing, double metres)
    { Geodesic.WGS84.Direct(start.Latitude, start.Longitude, bearing, metres, out double lat, out double lon); return new(lat, lon); }
    public static double Bearing(Coordinates a, Coordinates b)
    { Geodesic.WGS84.Inverse(a.Latitude, a.Longitude, b.Latitude, b.Longitude, out double _, out double bearing, out double _); return bearing; }
    public static Coordinates Point(ResearchWaypoint w) => new(w.Latitude, w.Longitude);
}
public static class FlightPatterns
{
    public static PatternDefinition Validate(PatternDefinition d)
    {
        if (!Enum.IsDefined(d.Kind) || d.Center is null || !MissionPlanning.Position(d.Center.Latitude, d.Center.Longitude)
            || !double.IsFinite(d.HeadingDegrees) || new[] { d.LengthMetres, d.WidthMetres, d.SpacingMetres, d.RadiusMetres }.Any(x => !double.IsFinite(x) || x < 10 || x > 500000)
            || d.Legs is < 2 or > 100 || d.Orbits is < 1 or > 10 || d.TargetAltitudeFeet is { } alt && !double.IsFinite(alt))
            throw new ArgumentException("Use valid coordinates, dimensions of 10–500,000 metres, 2–100 legs and 1–10 orbits.");
        var extent = d.Kind is FlightPatternKind.Lawnmower or FlightPatternKind.CreepingLine ? (d.Legs - 1) * d.SpacingMetres / 2 : d.Kind == FlightPatternKind.ExpandingSquare ? Math.Ceiling(d.Legs / 2d) * d.SpacingMetres : 0;
        if (extent > 500000) throw new ArgumentException("Reduce spacing or legs: this local pattern layout supports extents up to 500 km. Long routes can be planned with separate patterns.");
        return d with { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id, HeadingDegrees = (d.HeadingDegrees % 360 + 360) % 360, Notes = MissionPlanning.Text(d.Notes) };
    }
    public static PatternPreview Generate(PatternDefinition input)
    {
        var d = Validate(input); var points = new List<Coordinates>(); var sign = d.Clockwise ? 1 : -1;
        Coordinates Offset(double along, double across) => Geodesy.Direct(Geodesy.Direct(d.Center, d.HeadingDegrees + 90, across), d.HeadingDegrees, along);
        void Add(double along, double across) => points.Add(Offset(along, across));
        switch (d.Kind)
        {
            case FlightPatternKind.Lawnmower:
            case FlightPatternKind.CreepingLine:
                for (var i = 0; i < d.Legs; i++)
                {
                    var cross = (i - (d.Legs - 1) / 2d) * d.SpacingMetres;
                    var first = i % 2 == 0 ? -d.LengthMetres / 2 : d.LengthMetres / 2;
                    if (d.Kind == FlightPatternKind.Lawnmower) { Add(first, cross); Add(-first, cross); }
                    else { Add(cross, first); Add(cross, -first); }
                }
                break;
            case FlightPatternKind.ExpandingSquare:
                points.Add(d.Center); var at = d.Center;
                for (var i = 0; i < d.Legs; i++) { at = Geodesy.Direct(at, d.HeadingDegrees + sign * 90 * i, d.SpacingMetres * (1 + i / 2)); points.Add(at); }
                break;
            case FlightPatternKind.Orbit:
                for (var i = 0; i <= 72 * d.Orbits; i++) points.Add(Geodesy.Direct(d.Center, d.HeadingDegrees + sign * i * 5, d.RadiusMetres)); break;
            case FlightPatternKind.Racetrack:
                for (var orbit = 0; orbit < d.Orbits; orbit++)
                {
                    // Tangent semicircles at the ends of a geodesic straight, sampled every 5 degrees.
                    var a = Offset(d.LengthMetres / 2, 0); var b = Offset(-d.LengthMetres / 2, 0);
                    for (var i = 0; i <= 36; i++) points.Add(Geodesy.Direct(a, d.HeadingDegrees + sign * (-90 + i * 5), d.RadiusMetres));
                    for (var i = 0; i <= 36; i++) points.Add(Geodesy.Direct(b, d.HeadingDegrees + sign * (90 + i * 5), d.RadiusMetres));
                }
                points.Add(points[0]); break;
            case FlightPatternKind.Box:
                Add(-d.LengthMetres / 2, -sign * d.WidthMetres / 2); Add(d.LengthMetres / 2, -sign * d.WidthMetres / 2);
                Add(d.LengthMetres / 2, sign * d.WidthMetres / 2); Add(-d.LengthMetres / 2, sign * d.WidthMetres / 2); points.Add(points[0]); break;
            case FlightPatternKind.RadialSpokes:
                for (var i = 0; i < d.Legs; i++) { points.Add(d.Center); points.Add(Geodesy.Direct(d.Center, d.HeadingDegrees + sign * i * 360d / d.Legs, d.RadiusMetres)); points.Add(d.Center); }
                break;
            case FlightPatternKind.StraightTransect: Add(-d.LengthMetres / 2, 0); Add(d.LengthMetres / 2, 0); break;
        }
        if (points.Count > 1000) throw new ArgumentException("This pattern exceeds 1,000 editable waypoints. Reduce its orbits or legs.");
        var distance = points.Zip(points.Skip(1), (a, b) => Geodesy.Distance(a, b)).Sum();
        return new(d, points.Select((p, i) => new ResearchWaypoint($"{d.Kind} {i + 1}", p.Latitude, p.Longitude, d.TargetAltitudeFeet, PatternId: d.Id, Id: MissionPlanning.StableWaypointId(d.Id, i))).ToArray(), distance,
            "WGS84 ellipsoid / GeographicLib. Curves use 5-degree samples. All generated points remain editable. No aircraft or autopilot commands.");
    }
}
