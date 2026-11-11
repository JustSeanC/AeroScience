using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
using NetTopologySuite.Geometries;
using Geometry = NetTopologySuite.Geometries.Geometry;
using Coordinates = AeroScience.Shared.Coordinates;

namespace AeroScience.Missions;

// Bounded sweeps of the ORIGINAL sensor-plane model. No source reads, DEM
// reconstruction, acquisition scheduling, calibration, grading or yield writes.
internal sealed class TerrainSurveyGeometryBuilder(TerrainSurveyOptions options, CancellationToken token)
{
    readonly GeometryFactory factory = new();
    readonly List<Geometry> pieces = [];
    readonly List<TerrainSurveyLeg> legs = [];
    ScientificLocalGeometry? local;
    Coordinates? origin;
    TerrainSurveyInput? previous, first;
    int chunkIndex, chunkSteps;
    long inputs, totalLegs, poses, gaps;
    int peakLegs, peakVertices;

    public static void Validate(TerrainSurveyOptions o)
    {
        static bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        if (!Range(o.MaximumGapSeconds, .1, 10) || !Range(o.MaximumPositionAgeSeconds, .1, 6)
            || !Range(o.MaximumLegMetres, 1, 10000) || !Range(o.MaximumLatitudeDegrees, 1, 85)
            || !Range(o.InterpolationStepMetres, 1, 100) || !Range(o.InterpolationAngleDegrees, .1, 5)
            || !Range(o.MaximumRelativeHeightStep, .005, .1) || o.MaximumStepsPerLeg is < 1 or > 256
            || !Range(o.ChunkRadiusMetres, 100, 80000) || o.MaximumLegsPerChunk is < 1 or > 128
            || o.MaximumVerticesPerChunk is < 16 or > 50000 || o.MaximumArchiveRows is < 1 or > 2000000
            || o.MaximumArchiveBytes is < 1024 or > 1073741824 || o.MaximumOutputBytes is < 1024 or > 134217728
            || o.MaximumModeledPoses is < 2 or > 4000000 || o.MaximumChunks is < 1 or > 50000)
            throw new ArgumentException("Choose finite, bounded terrain survey continuity, interpolation and processing limits.");
    }
    static Coordinates Point(TelemetryFrame f) => new(f.Latitude, f.Longitude);
    static double Angle(double a, double b) => (b - a + 540) % 360 - 180;
    string? PoseProblem(TerrainSurveyInput input)
    {
        if (input.Frame is not { IsValid: true, IsPaused: false } f || input.Reference is null || input.SessionId is null || input.Configuration is null) return "Missing/invalid authoritative pose, session or configuration";
        if (input.Configuration.Settings is not { InstrumentId: "terrain-scanner", Enabled: true }) return "Terrain Scanner inactive";
        if (!SensorGeometry.SupportsAgl(f.AltitudeAglFeet)) return "AGL outside original sensor-plane bounds; MSL is not substituted";
        if (Math.Abs(f.Latitude) > options.MaximumLatitudeDegrees || Math.Abs(f.PitchDegrees) > 90 || Math.Abs(f.BankDegrees) > 180 || f.HeadingDegrees is < 0 or >= 360 || f.GroundTrackDegrees is < 0 or >= 360 || f.GroundspeedKnots < 0) return "Unsupported latitude, attitude or motion values";
        try { if (input.Configuration.Settings.Payload is {} payload) InstrumentProfiles.Validate(payload, "terrain-scanner"); }
        catch (ArgumentException) { return "Frozen scanner profile/geometry version is unsupported or invalid"; }
        return null;
    }
    string? PairProblem(TelemetryFrame a, TelemetryFrame b, double distance)
    {
        var dt = (b.TimestampUtc - a.TimestampUtc).TotalSeconds;
        if (dt <= 0) return "Non-increasing pose timestamps";
        if (dt > options.MaximumGapSeconds) return "Telemetry timing gap exceeds modeled continuity limit";
        if (a.Source != b.Source || a.Aircraft != b.Aircraft) return "Aircraft or telemetry source changed";
        // The existing geographic controller uses the same conservative motion test.
        // This is a discontinuity detector, never a physical aircraft speed limit.
        if (distance > options.MaximumLegMetres || distance > Math.Max(300, Math.Max(a.GroundspeedKnots, b.GroundspeedKnots) * .514444 * dt * 3 + 100)) return "Position jump/teleport or unsupported leg length";
        return null;
    }
    int Steps(TelemetryFrame a, TelemetryFrame b, double distance) => Math.Max(1, (int)Math.Ceiling(new[] {
        distance / options.InterpolationStepMetres,
        Math.Abs(Angle(a.HeadingDegrees, b.HeadingDegrees)) / options.InterpolationAngleDegrees,
        Math.Abs(b.PitchDegrees-a.PitchDegrees) / options.InterpolationAngleDegrees,
        Math.Abs(b.BankDegrees-a.BankDegrees) / options.InterpolationAngleDegrees,
        Math.Abs(b.AltitudeAglFeet!.Value-a.AltitudeAglFeet!.Value) / (Math.Min(a.AltitudeAglFeet.Value,b.AltitudeAglFeet.Value)*options.MaximumRelativeHeightStep)
    }.Max()));
    TelemetryFrame Interpolate(TelemetryFrame a, TelemetryFrame b, double fraction, double distance)
    {
        var p = Geodesy.Direct(Point(a), Geodesy.Bearing(Point(a), Point(b)), distance * fraction);
        double Lerp(double x, double y) => x + (y - x) * fraction;
        return a with { TimestampUtc = a.TimestampUtc.AddTicks((long)((b.TimestampUtc-a.TimestampUtc).Ticks*fraction)), Latitude = p.Latitude, Longitude = p.Longitude,
            AltitudeAglFeet = Lerp(a.AltitudeAglFeet!.Value,b.AltitudeAglFeet!.Value), AltitudeMslFeet = Lerp(a.AltitudeMslFeet,b.AltitudeMslFeet),
            HeadingDegrees = (a.HeadingDegrees+Angle(a.HeadingDegrees,b.HeadingDegrees)*fraction+360)%360,
            PitchDegrees = Lerp(a.PitchDegrees,b.PitchDegrees), BankDegrees = Lerp(a.BankDegrees,b.BankDegrees) };
    }
    void ResetRegion(TerrainSurveyInput start)
    { first = start; origin = Point(start.Frame!); local = new(origin); pieces.Clear(); legs.Clear(); chunkSteps = 0; }
    Geometry Sweep(TerrainSurveyInput a, TerrainSurveyInput b, double distance, int steps)
    {
        var values = new List<Geometry>(steps * 9 + 1); Coordinate[]? before = null;
        for (var i = 0; i <= steps; i++)
        {
            token.ThrowIfCancellationRequested(); if (++poses > options.MaximumModeledPoses) throw new TerrainSurveyBudgetException("Modeled pose budget exceeded.");
            var f = i == 0 ? a.Frame! : i == steps ? b.Frame! : Interpolate(a.Frame!,b.Frame!,i/(double)steps,distance);
            var footprint = SensorGeometry.ForInstrument(f, a.Configuration!.Settings);
            if (footprint.Boundary.Any(p=>Math.Abs(p.Latitude)>options.MaximumLatitudeDegrees || Geodesy.Distance(origin!,p)>options.ChunkRadiusMetres)) throw new InvalidOperationException("Footprint outside the selected regional/latitude bound");
            var boundary = footprint.Boundary.Select(local!.Project).ToArray();
            values.Add(local!.Polygon(footprint.Boundary));
            if (before is not null)
            {
                // Loft corresponding sensor edges with triangles. A convex hull of a
                // turning leg would falsely fill the inside of the turn. Each bounded
                // step is explicitly a linear boundary approximation, not observed motion.
                for (var edge = 0; edge < 4; edge++)
                {
                    var next = (edge + 1) % 4;
                    Triangle(before[edge], before[next], boundary[next]);
                    Triangle(before[edge], boundary[next], boundary[edge]);
                }
            }
            before = boundary;
        }
        void Triangle(Coordinate a0, Coordinate b0, Coordinate c0)
        {
            var p = factory.CreatePolygon([a0.Copy(),b0.Copy(),c0.Copy(),a0.Copy()]);
            if (p.Area > 1e-10) values.Add(p);
        }
        var union = ScientificLocalGeometry.Union(values);
        if (!union.IsValid || union.IsEmpty) throw new InvalidOperationException("Invalid swept boundary topology");
        return union;
    }
    TerrainSurveyRecord? Flush()
    {
        if (legs.Count == 0) return null;
        token.ThrowIfCancellationRequested(); var union = ScientificLocalGeometry.Union(pieces); union.Normalize();
        if (!union.IsValid || union.IsEmpty || union.NumPoints > options.MaximumVerticesPerChunk) throw new TerrainSurveyBudgetException("Chunk topology/vertex bound exceeded; no partial product published.");
        var geometry = JsonSerializer.Deserialize<TerrainSurveyGeometry>(JsonSerializer.Serialize(local!.GeoJson(union),WireJson.Options),WireJson.Options)!;
        if (geometry.Coordinates.Length == 0) throw new InvalidDataException("Swept geometry has no supported geographic polygon representation.");
        // RFC 7946 right-hand winding; rotate each ring to a stable starting vertex.
        foreach (var polygon in geometry.Coordinates) for (var ring = 0; ring < polygon.Length; ring++)
        {
            var points = polygon[ring]; double signed = 0;
            for (var i = 0; i < points.Length - 1; i++) signed += (points[i][0]-points[0][0])*(points[i+1][1]-points[0][1])-(points[i+1][0]-points[0][0])*(points[i][1]-points[0][1]);
            if ((signed > 0) != (ring == 0)) Array.Reverse(points);
            var min = Enumerable.Range(0,points.Length-1).MinBy(i=>(points[i][0],points[i][1]));
            polygon[ring] = Enumerable.Range(0,points.Length).Select(i=>points[(i+min)%(points.Length-1)]).ToArray();
        }
        var vertices = geometry.Coordinates.Sum(p=>p.Sum(r=>r.Length));
        if (vertices > options.MaximumVerticesPerChunk) throw new TerrainSurveyBudgetException("Geographic export vertex bound exceeded.");
        var windows = geometry.Coordinates.Select((p,i)=>new TerrainSurveyWindow(p[0].Min(c=>c[0]),p[0].Min(c=>c[1]),p[0].Max(c=>c[0]),p[0].Max(c=>c[1]),i)).ToArray();
        if (windows.Length > 128) throw new TerrainSurveyBudgetException("Source-window count exceeds the bounded chunk contract.");
        if (++chunkIndex > options.MaximumChunks) throw new TerrainSurveyBudgetException("Chunk budget exceeded.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(geometry,WireJson.Options)));
        peakLegs = Math.Max(peakLegs,legs.Count); peakVertices = Math.Max(peakVertices,vertices);
        var result = new TerrainSurveyChunk(chunkIndex,first!.SessionId!.Value,first.Configuration!,first.Configuration!.Settings.Payload ?? new(InstrumentProfiles.Generic("terrain-scanner")),first.Configuration!.Settings.Payload is null,
            legs[0].From.ObservedUtc,legs[^1].To.ObservedUtc,legs.ToArray(),geometry,windows,union.Area,vertices,hash);
        pieces.Clear(); legs.Clear(); chunkSteps = 0; return new(1,"chunk",Chunk:result);
    }
    TerrainSurveyRecord Exclude(string reason, TerrainSurveyInput? before, TerrainSurveyInput? after)
    { gaps++; return new(1,"gap",Gap:new(reason,before?.Reference?.ObservedUtc,after?.Reference?.ObservedUtc,after?.SessionId??before?.SessionId,after?.Reference?.CollectionEventSequence,after?.Reference?.ReceiptId)); }
    public IEnumerable<TerrainSurveyRecord> Build(IEnumerable<TerrainSurveyInput> source)
    {
        foreach (var input in source)
        {
            token.ThrowIfCancellationRequested(); if (++inputs > options.MaximumArchiveRows) throw new TerrainSurveyBudgetException("Input record budget exceeded.");
            if (input.Gap is {} gap)
            {
                if (Flush() is {} chunk) yield return chunk;
                gaps++; yield return new(1,"gap",Gap:gap); previous = null; continue;
            }
            if (PoseProblem(input) is {} problem)
            {
                if (Flush() is {} chunk) yield return chunk;
                yield return Exclude(problem,previous,input); previous = null; continue;
            }
            if (previous is not {} a) { previous = input; ResetRegion(input); continue; }
            var distance = Geodesy.Distance(Point(a.Frame!),Point(input.Frame!));
            var reason = a.SessionId != input.SessionId ? "Scientific session changed" : a.Configuration!.Id != input.Configuration!.Id ? "Frozen instrument configuration changed" : PairProblem(a.Frame!,input.Frame!,distance);
            var steps = reason is null ? Steps(a.Frame!,input.Frame!,distance) : 0;
            if (reason is null && steps > options.MaximumStepsPerLeg) reason = "Required interpolation exceeds the supported per-leg fidelity bound";
            if (reason is not null)
            {
                if (Flush() is {} chunk) yield return chunk;
                yield return Exclude(reason,a,input); previous = input; ResetRegion(input); continue;
            }
            // Bound regional unions and temporary interpolation work independently of flight length.
            if (legs.Count >= options.MaximumLegsPerChunk || chunkSteps + steps > 256 || Geodesy.Distance(origin!,Point(input.Frame!)) > options.ChunkRadiusMetres / 2)
            { if (Flush() is {} chunk) yield return chunk; ResetRegion(a); }
            Geometry? sweep = null; string? failed = null;
            try { sweep = Sweep(a,input,distance,steps); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NetTopologySuite.Geometries.TopologyException) { failed = "Unsupported ground intersection/sweep: " + e.Message; }
            if (failed is not null)
            {
                if (Flush() is {} chunk) yield return chunk;
                yield return Exclude(failed,a,input); previous = input; ResetRegion(input); continue;
            }
            pieces.Add(sweep!); legs.Add(new(a.Reference!,input.Reference!,steps,distance)); chunkSteps += steps; totalLegs++; previous = input;
        }
        if (Flush() is {} last) yield return last;
        if (totalLegs == 0) yield return Exclude("No adjacent collection-active poses support a continuous corridor",previous,null);
    }
    public TerrainSurveySummary Summary(long acquisitions) => new(chunkIndex>0?"Modeled":"Unavailable",inputs,chunkIndex,totalLegs,poses,gaps,acquisitions,peakLegs,peakVertices);
}

internal sealed class TerrainSurveyBudgetException(string message) : IOException(message);
