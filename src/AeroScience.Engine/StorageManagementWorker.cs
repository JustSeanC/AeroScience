using AeroScience.Missions;

namespace AeroScience.Engine;

public sealed class StorageManagementWorker(StorageManagement management, ScientificCache cache,
    MissionCoordinator missions, ILogger<StorageManagementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        management.AutomaticCleanup = cache.ScheduleAutomatic;
        try
        {
            do
            {
                if(missions.Snapshot().ActiveMission is null)
                {
                    try { await Task.Run(management.RefreshScience, token); if(management.Preferences.AutomaticCacheCleanup)cache.ScheduleAutomatic(); }
                    catch(Exception error) when(error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Storage management: {Message}",error.Message);}
                }
                await Task.Delay(TimeSpan.FromMinutes(2),token);
            }while(!token.IsCancellationRequested);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
    }
}
