namespace AeroScience.Engine;

public sealed class AviationAcquisitionWorker(AviationReference aviation, ILogger<AviationAcquisitionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Run(aviation.ProcessDownloads, stoppingToken), Run(aviation.RefreshTfrIfDue, stoppingToken));

    // A streamed original can occupy its transfer loop for minutes. Small, consent-
    // gated TFR updates must not wait for that transfer. Both retain existing gates,
    // bounded requests and serialized database access on this single host.
    private async Task Run(Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await work(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { logger.LogWarning("Aviation storage is unavailable; originals and durable work were preserved."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
