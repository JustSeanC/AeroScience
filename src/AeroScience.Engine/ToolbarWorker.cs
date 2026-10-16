using System.Runtime.InteropServices;
using AeroScience.Missions;
using AeroScience.SimConnect;

namespace AeroScience.Engine;

public sealed class ToolbarWorker(ToolbarCommandHandler handler, EngineOptions options, ILogger<ToolbarWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableToolbarBridge || !OperatingSystem.IsWindows())
        {
            logger.LogInformation("MSFS toolbar bridge {State}", options.EnableToolbarBridge ? "requires Windows x64" : "disabled in configuration");
            return;
        }
        string? lastError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var transport = new CommBusTransport();
                logger.LogInformation("MSFS toolbar CommBus connected (SU5+); waiting for panel requests");
                lastError = null;
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var command in transport.Poll()) transport.Reply(handler.Handle(command));
                    await Task.Delay(100, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or COMException or IOException or PlatformNotSupportedException)
            {
                var message = e is DllNotFoundException ? "Official x64 SimConnect runtime not found beside the executable."
                    : e is EntryPointNotFoundException ? "Runtime lacks MSFS 2024 SU5+ CommBus exports; update the official SDK runtime."
                    : $"{e.GetType().Name}: {e.Message}";
                if (lastError != message) logger.LogWarning("Toolbar unavailable: {Reason}. Retrying in 5 seconds.", message);
                lastError = message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
