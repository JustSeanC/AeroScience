using System.Runtime.CompilerServices;
using AeroScience.Shared;
using AeroScience.Telemetry;

// Browser-fixture control only; never compiled into a production application.
public sealed class BrowserTelemetryProvider(int intervalMs) : ITelemetryProvider
{
    private int remoteLocation;
    public IDisposable UseRemoteFixtureLocation()
    {
        if (Interlocked.Exchange(ref remoteLocation, 1) != 0) throw new InvalidOperationException("Remote browser fixtures cannot overlap.");
        return new Scope(() => Volatile.Write(ref remoteLocation, 0));
    }
    public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in new SimulatedTelemetryProvider(intervalMs, includeResearch: true).ReadAsync(cancellationToken))
        {
            // The test endpoint and periodic provider must describe the same location.
            // Otherwise a provider tick can take the first acquisition slot at the
            // default California position before the endpoint supplies its local packet.
            yield return Volatile.Read(ref remoteLocation) != 0 && update.Frame is {} frame
                ? update with { Frame = frame with { Latitude = 40, Longitude = -75, AltitudeMslFeet = 1000,
                    AltitudeAglFeet = 328.0839895, PitchDegrees = 0, BankDegrees = 0, HeadingDegrees = 0 } }
                : update;
        }
    }
    private sealed class Scope(Action release) : IDisposable { public void Dispose() => release(); }
}
