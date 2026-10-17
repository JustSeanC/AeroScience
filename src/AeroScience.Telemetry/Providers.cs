using AeroScience.Shared;
namespace AeroScience.Telemetry;

public interface ITelemetryProvider
{
    IAsyncEnumerable<TelemetryUpdate> ReadAsync(CancellationToken cancellationToken);
}
