using AeroScience.Missions;
namespace AeroScience.Engine;
// Resolves the durable coordinator at startup even with no dashboard connected.
public sealed class ScientificLibraryWorker(ScientificLibraryJobs jobs):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    { _=jobs;try{await Task.Delay(Timeout.Infinite,stoppingToken);}catch(OperationCanceledException){} }
}
