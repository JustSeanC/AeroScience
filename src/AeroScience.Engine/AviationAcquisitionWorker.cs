namespace AeroScience.Engine;

public sealed class AviationAcquisitionWorker(AviationReference aviation, ILogger<AviationAcquisitionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await aviation.ProcessDownloads(stoppingToken);await aviation.RefreshTfrIfDue(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            { logger.LogWarning("Aviation storage is unavailable; originals and durable work were preserved."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
