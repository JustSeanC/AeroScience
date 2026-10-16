using AeroScience.Missions;
using AeroScience.Telemetry;

namespace AeroScience.Engine;

public sealed class TelemetryWorker(ITelemetryProvider provider, MissionCoordinator coordinator,
    ILogger<TelemetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var update in provider.ReadAsync(stoppingToken))
            {
                var previous = coordinator.Snapshot().Connection;
                coordinator.Apply(update);
                if (previous.State != update.Connection.State || previous.Message != update.Connection.Message)
                    logger.LogInformation("Telemetry {State}: {Message}", update.Connection.State, update.Connection.Message);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        // Storage errors must stop the process rather than silently lose a recording.
        finally { coordinator.Interrupt(); }
    }
}
