using System.Runtime.CompilerServices;
using AeroScience.Shared;
using AeroScience.Telemetry;

// Browser-fixture control only; never compiled into a production application.
public sealed class BrowserTelemetryProvider(int intervalMs) : ITelemetryProvider
{
    private int remoteLocation;
    private SensorTelemetryFixture? sensorFixture;
    public SensorTelemetryFixture? SensorFixture { get=>Volatile.Read(ref sensorFixture);set=>Volatile.Write(ref sensorFixture,value); }
    public IDisposable UseRemoteFixtureLocation()
    {
        if (Interlocked.Exchange(ref remoteLocation, 1) != 0) throw new InvalidOperationException("Remote browser fixtures cannot overlap.");
        return new Scope(() => Volatile.Write(ref remoteLocation, 0));
    }
    public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in new SimulatedTelemetryProvider(intervalMs, includeResearch: true).ReadAsync(cancellationToken))
        {
            if(SensorFixture is {} fixture && update.Frame is {} original)
            {
                var at=DateTimeOffset.UtcNow.AddSeconds(-fixture.AgeSeconds);
                var sensorFrame=original with{TimestampUtc=at,Latitude=fixture.Latitude,Longitude=fixture.Longitude,AltitudeMslFeet=fixture.AltitudeMslFeet,AltitudeAglFeet=fixture.AltitudeAglFeet,
                    PitchDegrees=fixture.PitchDegrees,BankDegrees=fixture.BankDegrees,GroundspeedKnots=fixture.GroundspeedKnots,IsPaused=fixture.Paused};
                yield return update with{Frame=sensorFrame,Atmosphere=fixture.ResearchAtmosphere?new(at,fixture.TemperatureCelsius,fixture.PressureInHg,fixture.WindKnots,fixture.WindDirection,fixture.Density,fixture.Paused?ObservationQuality.Paused:ObservationQuality.Valid):update.Atmosphere is {} air?air with{TimestampUtc=at}:null,
                    Connection=update.Connection with{State=fixture.Disconnected?ConnectionState.Disconnected:ConnectionState.Connected}};
                continue;
            }
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
// Explicit synthetic test inputs; the production assessment, controller and source workers run unchanged.
public sealed record SensorTelemetryFixture(double? AltitudeAglFeet=1000,double PitchDegrees=0,double BankDegrees=0,
    double GroundspeedKnots=120,int AgeSeconds=0,bool Disconnected=false,bool Paused=false,double AltitudeMslFeet=1000,
    bool ResearchAtmosphere=false,double? TemperatureCelsius=12,double? PressureInHg=29.9,double? WindKnots=8,double? WindDirection=270,double? Density=.0023,
    double Latitude=40,double Longitude=-75);
