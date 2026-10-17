using AeroScience.Shared;
namespace AeroScience.Missions;

public static class MissionAnalysis
{
    public static int AdvanceWaypoint(ResearchWaypoint[] route, int expected, Coordinates position, double tolerance) =>
        expected < route.Length && Geodesy.Distance(position, Geodesy.Point(route[expected])) <= tolerance ? expected + 1 : expected;
    public static void Validate(AnalysisSettings s)
    {
        if (!double.IsFinite(s.WaypointToleranceMetres) || s.WaypointToleranceMetres is < 10 or > 50000 || !double.IsFinite(s.MaximumGapSeconds) || s.MaximumGapSeconds is < 1 or > 600
            || s.SwathWidthMetres is { } w && (!double.IsFinite(w) || w <= 0 || w > 50000)) throw new ArgumentException("Use a 10–50,000 m waypoint tolerance and 1–600 s maximum telemetry gap.");
    }
    private static double Delta(double a, double b) => (a - b + 540) % 360 - 180;
    private static bool Inside(Coordinates p, Coordinates[] vertices)
    {
        var inside = false; for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
        {
            var a = vertices[i]; var b = vertices[j]; var ax = Delta(a.Longitude, p.Longitude); var bx = Delta(b.Longitude, p.Longitude);
            if ((a.Latitude > p.Latitude) != (b.Latitude > p.Latitude) && 0 < (bx - ax) * (p.Latitude - a.Latitude) / (b.Latitude - a.Latitude) + ax) inside = !inside;
        }
        return inside;
    }
    private static double SegmentDistance(Coordinates p, Coordinates a, Coordinates b, double length, double bearing)
    {
        // Candidate fraction in a local tangent approximation, then measure its WGS84 position.
        // This is a route comparison, never navigation guidance. Long/polar limitations are reported.
        var cos = Math.Max(.001, Math.Cos(p.Latitude * Math.PI / 180));
        var ax = Delta(a.Longitude, p.Longitude) * cos; var ay = a.Latitude - p.Latitude;
        var bx = Delta(b.Longitude, p.Longitude) * cos; var by = b.Latitude - p.Latitude;
        var dx = bx - ax; var dy = by - ay; var t = dx * dx + dy * dy == 0 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / (dx * dx + dy * dy), 0, 1);
        return Geodesy.Distance(p, Geodesy.Direct(a, bearing, t * length));
    }
    public static PlanComparison Compare(MissionPlan? plan, IEnumerable<TelemetryFrame> samples, AnalysisSettings settings)
    {
        Validate(settings); var waypoints = plan?.Waypoints ?? []; var areas = plan?.SurveyAreas ?? [];
        var closest = Enumerable.Repeat(double.PositiveInfinity, waypoints.Length).ToArray(); var reached = new DateTimeOffset?[waypoints.Length]; var alt = new double?[waypoints.Length];
        var inside = new long[areas.Length]; var traversed = new double[areas.Length]; var deviations = new List<RouteDeviation>();
        var route = waypoints.Select(Geodesy.Point).ToArray(); var lengths = route.Zip(route.Skip(1), Geodesy.Distance).ToArray(); var bearings = route.Zip(route.Skip(1), Geodesy.Bearing).ToArray(); var planned = lengths.Sum();
        var segmentSamples = new long[lengths.Length]; var segmentGaps = new long[lengths.Length]; var segmentMax = new double[lengths.Length];
        double deviationSum = 0, deviationMax = 0; long deviationCount = 0;
        var distance = 0d; long gaps = 0; var expected = 0; TelemetryFrame? previous = null;
        foreach (var sample in samples)
        {
            if (!MissionPlanning.Position(sample.Latitude, sample.Longitude)) continue;
            var p = new Coordinates(sample.Latitude, sample.Longitude); var continuity = previous is not null && (sample.TimestampUtc - previous.TimestampUtc).TotalSeconds is > 0 && (sample.TimestampUtc - previous.TimestampUtc).TotalSeconds <= settings.MaximumGapSeconds;
            var last = previous is null ? p : new Coordinates(previous.Latitude, previous.Longitude);
            if (continuity) distance += Geodesy.Distance(last, p); else if (previous is not null) gaps++;
            for (var i = 0; i < route.Length; i++) closest[i] = Math.Min(closest[i], Geodesy.Distance(p, route[i]));
            var activeSegment = expected - 1;
            if (activeSegment >= 0 && activeSegment < lengths.Length)
            {
                segmentSamples[activeSegment]++;
                if (!continuity && previous is not null) segmentGaps[activeSegment]++;
                segmentMax[activeSegment] = Math.Max(segmentMax[activeSegment], SegmentDistance(p, route[activeSegment], route[activeSegment + 1], lengths[activeSegment], bearings[activeSegment]));
            }
            // Reaching means an observed, ordered point within tolerance. Never skip an unvisited waypoint.
            if (AdvanceWaypoint(waypoints, expected, p, settings.WaypointToleranceMetres) > expected)
            { reached[expected] = sample.TimestampUtc; alt[expected] = waypoints[expected].TargetAltitudeFeet is { } target ? sample.AltitudeMslFeet - target : null; expected++; }
            if (route.Length > 1)
            {
                var best = double.PositiveInfinity; var segment = 0;
                for (var i = 1; i < route.Length; i++) { var d = SegmentDistance(p, route[i - 1], route[i], lengths[i - 1], bearings[i - 1]); if (d < best) { best = d; segment = i - 1; } }
                deviationSum += best; deviationMax = Math.Max(deviationMax, best); deviationCount++;
                deviations.Add(new(sample.TimestampUtc, best, segment));
                if (deviations.Count > 2000) deviations = deviations.Where((_, i) => i % 2 == 0).ToList();
            }
            for (var i = 0; i < areas.Length; i++)
            {
                if (Inside(p, areas[i].Vertices)) inside[i]++;
                if (continuity)
                {
                    // Integrate observed path in 250 m subsegments, including boundary crossings.
                    var length = Geodesy.Distance(last, p); var steps = Math.Clamp((int)Math.Ceiling(length / 250), 1, 4000); var bearing = Geodesy.Bearing(last, p);
                    for (var j = 0; j < steps; j++) if (Inside(Geodesy.Direct(last, bearing, length * (j + .5) / steps), areas[i].Vertices)) traversed[i] += length / steps;
                }
            }
            previous = sample;
        }
        return new(settings, planned, distance, gaps, waypoints.Select((w, i) => new WaypointComparison(i, w.Name, reached[i] is not null, double.IsFinite(closest[i]) ? closest[i] : null, reached[i], alt[i])).ToArray(), deviations.ToArray(),
            areas.Select((a, i) => new SurveyComparison(a.Name, inside[i], traversed[i], "Recorded path traversal only. Scientific area coverage is not established: no implemented sensor footprint, overlap or acquisition-validity model. A swath entry alone does not establish coverage.")).ToArray(),
            ["WGS84 ellipsoidal distances. Route deviation uses a local projected candidate on each geodesic segment; interpret long segments and polar results cautiously.",
            "Waypoint matches require observed points in plan order and within the selected tolerance. Closest proximity alone does not establish intention. Narrow crossings between samples may be missed.",
            "Distances and area traversal exclude telemetry intervals exceeding the gap limit or with non-increasing timestamps. No missing track is interpolated.",
            "Area path lengths use 250 m midpoint integration and are approximate. These results are not pilot scores or certified navigation.",
            "Deviation display is bounded to 2,000 observed timestamps; full original telemetry remains available in exports. Aggregate deviation metrics use every valid recorded position, not the reduced display.",
            "A segment is observed within tolerance only when both endpoints are reached in order at different times, its recorded intermediate positions stay within tolerance and no excluded gap occurs. This establishes observed path adherence, not pilot intention or scientific coverage."],
            new(deviationCount, deviationCount > 0 ? deviationSum / deviationCount : null, deviationCount > 0 ? deviationMax : null),
            lengths.Select((_, i) => new SegmentComparison(i, waypoints[i].Name, waypoints[i + 1].Name, reached[i] is { } start && reached[i + 1] is { } end && end > start && segmentSamples[i] > 0 && segmentGaps[i] == 0 && segmentMax[i] <= settings.WaypointToleranceMetres, segmentSamples[i], segmentGaps[i], segmentSamples[i] > 0 ? segmentMax[i] : null)).ToArray());
    }
}
