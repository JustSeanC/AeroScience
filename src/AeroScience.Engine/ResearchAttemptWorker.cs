using AeroScience.Missions;
using Microsoft.Data.Sqlite;
namespace AeroScience.Engine;
// Reads original evidence asynchronously; the 1 Hz recorder never calls the detector.
public sealed class ResearchAttemptWorker(MissionStore store,ILogger<ResearchAttemptWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();var offset=0;
        while(!stoppingToken.IsCancellationRequested){
            try{
                var flights=store.PendingAutomaticFlights(offset);
                foreach(var flight in flights){stoppingToken.ThrowIfCancellationRequested();store.ProcessAutomaticAttempts(flight,stoppingToken);store.EvaluateAutomaticResults(flight,stoppingToken);}
                offset=flights.Length==16?offset+flights.Length:0;
            }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(SqliteException ex) when(ex.SqliteErrorCode is 5 or 6){logger.LogDebug("Automatic attempt work yielded to another local writer.");}
            catch(Exception ex){logger.LogWarning(ex,"Automatic attempt evidence processing is pending; flight and science controls remain independent.");}
            try{await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
