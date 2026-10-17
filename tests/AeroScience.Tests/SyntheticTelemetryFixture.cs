using System.Runtime.CompilerServices;
using AeroScience.Shared;
namespace AeroScience.Telemetry;

// Test-only deterministic provider; never referenced by production projects.
public sealed class SimulatedTelemetryProvider(int intervalMs = 1000) : ITelemetryProvider
{
    // Synthetic ER-2 cruise over Southern California. This is not scientific observation data.
    public static TelemetryFrame Generate(DateTimeOffset startUtc, double elapsedSeconds)
    {
        const double latitude = 34.9;
        const double speed = 410;
        var heading = (75 + elapsedSeconds / 120) % 360;
        var distanceNm = elapsedSeconds * speed / 3600;
        var angle = 75 * Math.PI / 180;
        var lat = latitude + distanceNm * Math.Cos(angle) / 60;
        var lon = -117.9 + distanceNm * Math.Sin(angle) / (60 * Math.Cos(latitude * Math.PI / 180));
        // Keep long-running demos within valid coordinates.
        lat = 34.9 + (lat - 34.9) % 10;
        lon = ((lon + 180) % 360 + 360) % 360 - 180;
        return new(startUtc.AddSeconds(elapsedSeconds), TelemetrySource.Simulated, "NASA ER-2 · test trajectory",
            lat, lon, 60000 + 250 * Math.Sin(elapsedSeconds / 90), 57000 + 250 * Math.Sin(elapsedSeconds / 90),
            205, 410, speed, heading, heading, 1.2, 2 * Math.Sin(elapsedSeconds / 60),
            250.0 / 90 * 60 * Math.Cos(elapsedSeconds / 90), MachNumber: .82);
    }

    public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var connection = new AeroScience.Shared.ConnectionInfo(ConnectionState.Connected, TelemetrySource.Simulated,
            "Synthetic test telemetry — no simulator connection", start);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        do { yield return new(connection, Generate(start, clock.Elapsed.TotalSeconds)); }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
