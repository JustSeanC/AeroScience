using System.Runtime.InteropServices;
using AeroScience.Missions;
using AeroScience.SimConnect;

namespace AeroScience.Engine;

public sealed class ToolbarWorker(ToolbarCommandHandler handler, EngineOptions options, ILogger<ToolbarWorker> logger,ToolbarBridgeStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableToolbarBridge || !OperatingSystem.IsWindows())
        {
            status.Connection(options.EnableToolbarBridge?"Windows required":"Disabled","Enable the native toolbar bridge on Windows with the official x64 runtime. Telemetry connection alone does not verify CommBus.");
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
                lastError = null;status.Connection("Connected","SimConnect CommBus subscribed; waiting for native panel requests.");
                while (!stoppingToken.IsCancellationRequested)
                {
                    foreach (var command in transport.Poll())
                    {
                        try
                        {
                            using var value=System.Text.Json.JsonDocument.Parse(command);
                            status.Requested(value.RootElement.TryGetProperty("op",out var operation)?operation.GetString():"unknown");
                            transport.Reply(handler.Handle(command));status.Replied();
                        }
                        catch(Exception error)when(error is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
                        { logger.LogWarning("Toolbar command/reply refused: {Reason}. Bridge remains connected; check package and engine versions.",error.Message); }
                    }
                    await Task.Delay(100, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or COMException or IOException or PlatformNotSupportedException)
            {
                var message = e is DllNotFoundException ? (File.Exists(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"))
                    ? "Windows could not load SimConnect or a dependency. Check official runtime prerequisites in application Settings."
                    : "Official x64 SimConnect runtime not found beside the executable.")
                    : e is EntryPointNotFoundException ? "Runtime lacks MSFS 2024 SU5+ CommBus exports; update the official SDK runtime."
                    : $"{e.GetType().Name}: {e.Message}";
                if (lastError != message) logger.LogWarning("Toolbar unavailable: {Reason}. Retrying in 5 seconds.", message);
                lastError = message;status.Connection("Retrying",message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
