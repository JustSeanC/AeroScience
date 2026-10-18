using AeroScience.Shared;

namespace AeroScience.Missions;

public static class SensorGeometry
{
    public static ObservationFootprint Footprint(TelemetryFrame frame, SensorMount? mount = null)
    {
        mount ??= new();
        if (!frame.IsValid || frame.AltitudeAglFeet is not { } agl || !double.IsFinite(agl) || agl is < 1 or > 200000)
            throw new InvalidOperationException("A current positive simulator altitude above ground is required. Ground intersection is unavailable.");
        if (new[] { mount.HorizontalFovDegrees, mount.VerticalFovDegrees, mount.ForwardDegrees, mount.RightDegrees }.Any(v => !double.IsFinite(v))
            || mount.HorizontalFovDegrees is <= 0 or >= 120 || mount.VerticalFovDegrees is <= 0 or >= 120)
            throw new ArgumentException("Use a finite fixed sensor mount and field of view below 120 degrees.");
        var height = agl * .3048;
        var h = Math.Tan(mount.HorizontalFovDegrees * Math.PI / 360);
        var v = Math.Tan(mount.VerticalFovDegrees * Math.PI / 360);
        // Body axes: forward, right, down. The existing SimConnect decoder
        // normalizes SDK angles to nose-up pitch and right-wing-down bank.
        (double X, double Y, double Z) Rotate(double x, double y, double z, double pitch, double roll)
        {
            pitch *= Math.PI / 180; roll *= Math.PI / 180;
            var yr = y * Math.Cos(roll) - z * Math.Sin(roll);
            var zr = y * Math.Sin(roll) + z * Math.Cos(roll);
            return (x * Math.Cos(pitch) + zr * Math.Sin(pitch), yr, -x * Math.Sin(pitch) + zr * Math.Cos(pitch));
        }
        Coordinates Ray(double x, double y)
        {
            var sensor = Rotate(x, y, 1, mount.ForwardDegrees, -mount.RightDegrees);
            var body = Rotate(sensor.X, sensor.Y, sensor.Z, frame.PitchDegrees, frame.BankDegrees);
            if (body.Z < .15) throw new InvalidOperationException("Sensor rays approach/point above the horizon; finite ground intersection is unavailable.");
            var forward = body.X * height / body.Z; var right = body.Y * height / body.Z;
            var distance = Math.Sqrt(forward * forward + right * right);
            if (distance > 100000) throw new InvalidOperationException("Footprint exceeds the supported 100 km local-plane range.");
            var bearing = frame.HeadingDegrees + Math.Atan2(right, forward) * 180 / Math.PI;
            return distance == 0 ? new(frame.Latitude, frame.Longitude) : Geodesy.Direct(new(frame.Latitude, frame.Longitude), bearing, distance);
        }
        return new(Ray(0, 0), [Ray(v, -h), Ray(v, h), Ray(-v, h), Ray(-v, -h)], height,
            "EPSG:4326", "Local horizontal plane at simulator surface beneath aircraft; not an external DEM datum",
            "Body/mount rotation, ray-plane intersection and WGS84 geodesic displacement",
            ["Approximate constant-height plane; off-aircraft relief, Earth curvature and occlusion are not ray-traced.",
             "External terrain datums are retained separately. No NAVD88/ellipsoid/MSFS vertical transformation is assumed."]);
    }
    public static Coordinates[] Samples(ObservationFootprint footprint, int size = 24)
    {
        if (size is < 1 or > 32) throw new ArgumentException("Acquisition subsets support 1–32 samples per side.");
        Coordinates Along(Coordinates a, Coordinates b, double t) => Geodesy.Direct(a, Geodesy.Bearing(a, b), Geodesy.Distance(a, b) * t);
        var result = new Coordinates[size * size]; var p = footprint.Boundary;
        for (var row = 0; row < size; row++)
        {
            var a = Along(p[0], p[3], (row + .5) / size); var b = Along(p[1], p[2], (row + .5) / size);
            for (var col = 0; col < size; col++) result[row * size + col] = Along(a, b, (col + .5) / size);
        }
        return result;
    }
}
