using System.Text.Json;
using System.Threading.Channels;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record AnalysisJob(Guid Id,Guid MissionId,string Kind,string State,long Completed,long? Total,Guid[] Artifacts,string? Message=null);
public sealed class ScientificAnalysisJobs(MissionStore missions,ScienceStore science,StorageManagement? storage=null,ScientificAccessSettings? settings=null,ILogger<ScientificAnalysisJobs>? logger=null) : BackgroundService
{
    private sealed record Work(Guid Id,Guid Mission,ScientificCoverageRequest? Coverage,CompositeRequest? Composite,CancellationTokenSource Cancel,string? PerformanceReason=null,TerrainSurveyOptions? Terrain=null,Guid? Elevation=null,Guid? Lidar=null);
    private readonly Channel<Work> queue=Channel.CreateBounded<Work>(4);
    private readonly object gate=new();private readonly Dictionary<Guid,AnalysisJob> states=[];private readonly Dictionary<Guid,CancellationTokenSource> cancellations=[];
    public AnalysisJob Enqueue(Guid mission,ScientificCoverageRequest? coverage=null,CompositeRequest? composite=null,TerrainSurveyOptions? terrain=null)
    {
        if(terrain is not null)missions.RequireArchivedTerrainSurvey(mission);else ResearchExports.RequireCompleted(missions,mission);
        if(missions.ScienceDeleted(mission))throw new InvalidOperationException("Collected science was permanently deleted. Analysis and historical grades cannot be recreated from missing evidence.");if((coverage is null?0:1)+(composite is null?0:1)+(terrain is null?0:1)!=1)throw new ArgumentException("Choose one analysis operation.");
        if(terrain is not null)ContinuousTerrainSurveys.ValidateOptions(terrain);
        var id=Guid.NewGuid();var cancel=new CancellationTokenSource();var job=new AnalysisJob(id,mission,terrain is not null?"terrain-corridor":coverage is null?"composite":"coverage","Queued",0,null,[]);
        lock(gate)
        {
            foreach(var old in states.Values.Where(s=>s.State is "Completed" or "Failed" or "Cancelled").Take(Math.Max(0,states.Count-95)).ToArray())states.Remove(old.Id);
            if(states.Count>=100){cancel.Dispose();throw new InvalidOperationException("Analysis history is full. Restart AeroScience after current work completes.");}
            states.Add(id,job);cancellations.Add(id,cancel);
            if(!queue.Writer.TryWrite(new(id,mission,coverage,composite,cancel,Terrain:terrain))){states.Remove(id);cancellations.Remove(id);cancel.Dispose();throw new InvalidOperationException("Analysis queue is full. Wait for current work to finish.");}
        }
        return job;
    }
    public AnalysisJob EnqueuePerformance(Guid mission,string reason="End-flight archived evaluation")
    {
        ResearchExports.RequireCompleted(missions,mission);if(missions.ScienceDeleted(mission))throw new InvalidOperationException("Collected science was permanently deleted. Analysis and historical grades cannot be recreated from missing evidence.");
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>500)throw new ArgumentException("Explain the report calculation or revision.");
        var id=Guid.NewGuid();var cancel=new CancellationTokenSource();var job=new AnalysisJob(id,mission,"performance","Queued",0,null,[]);
        lock(gate)
        {
            foreach(var old in states.Values.Where(s=>s.State is "Completed" or "Failed" or "Cancelled").Take(Math.Max(0,states.Count-95)).ToArray())states.Remove(old.Id);
            if(states.Count>=100){cancel.Dispose();throw new InvalidOperationException("Analysis history is full.");}
            states.Add(id,job);cancellations.Add(id,cancel);
            if(!queue.Writer.TryWrite(new(id,mission,null,null,cancel,reason))){states.Remove(id);cancellations.Remove(id);cancel.Dispose();throw new InvalidOperationException("Analysis queue is full.");}
        }
        return job;
    }
    public AnalysisJob Get(Guid id){lock(gate)return states.GetValueOrDefault(id)??throw new ArgumentException("Analysis job not found.");}
    public TerrainElevationJob ControlTerrain(Guid id,TerrainElevationControl request)
    {
        lock(gate)
        {
            if(request.Action is "resume" or "retry"&&cancellations.ContainsKey(id))throw new InvalidOperationException("The previous terrain worker is still stopping. Refresh before resuming.");
            var receipt=science.ControlTerrainJob(id,request);var current=science.TerrainJob(id);
            if(current.State=="Queued"&&!cancellations.ContainsKey(id))
            {
                foreach(var old in states.Values.Where(s=>s.State is "Completed" or "Failed" or "Cancelled").Take(Math.Max(0,states.Count-95)).ToArray())states.Remove(old.Id);
                if(states.Count>=100&&!states.ContainsKey(id))return science.ChangeTerrainJob(id,j=>j with{State="Failed",Message="Analysis history is full; retry after current work finishes/restart."});
                var cancel=new CancellationTokenSource();states[id]=new(id,current.MissionId,"terrain-elevation","Queued",current.CompletedTiles,current.TotalTiles,[]);cancellations.Add(id,cancel);
                if(!queue.Writer.TryWrite(new(id,current.MissionId,null,null,cancel,Elevation:id)))
                {cancellations.Remove(id);cancel.Dispose();states.Remove(id);return science.ChangeTerrainJob(id,j=>j with{State="Failed",Message="Analysis queue is full. Retained sources/checkpoints are preserved; retry explicitly."});}
            }
            else if(current.State is "Pausing" or "Paused" or "Cancelled"&&cancellations.TryGetValue(id,out var active))active.Cancel();
            return receipt;
        }
    }
    public LidarProcessingJob ControlLidar(Guid id,TerrainElevationControl request)
    {
        EnsureLidarRecovered();lock(gate)
        {
            if(request.Action is "resume" or "retry"&&cancellations.ContainsKey(id))throw new InvalidOperationException("The previous historical-point worker is still stopping. Refresh before resuming.");
            var receipt=science.ControlLidarJob(id,request);var current=science.LidarJob(id);
            if(current.State=="Queued"&&!cancellations.ContainsKey(id))
            {
                foreach(var old in states.Values.Where(s=>s.State is "Completed" or "Failed" or "Cancelled").Take(Math.Max(0,states.Count-95)).ToArray())states.Remove(old.Id);
                if(states.Count>=100&&!states.ContainsKey(id))return science.ChangeLidarJob(id,j=>j with{State="Failed",Message="Analysis history is full; retry after current work finishes/restart."});
                var cancel=new CancellationTokenSource();states[id]=new(id,current.MissionId,"historical-lidar","Queued",current.CompletedTiles,current.TotalTiles,[]);cancellations.Add(id,cancel);
                if(!queue.Writer.TryWrite(new(id,current.MissionId,null,null,cancel,Lidar:id)))
                {cancellations.Remove(id);cancel.Dispose();states.Remove(id);return science.ChangeLidarJob(id,j=>j with{State="Failed",Message="Analysis queue is full. Retained sources/checkpoints are preserved; retry explicitly."});}
            }
            else if(current.State is "Pausing" or "Paused" or "Cancelled"&&cancellations.TryGetValue(id,out var active))active.Cancel();
            return receipt;
        }
    }
    public AnalysisJob Cancel(Guid id){lock(gate){if(cancellations.TryGetValue(id,out var source))source.Cancel();return Get(id);}}
    private void Update(Guid id,Func<AnalysisJob,AnalysisJob> change){lock(gate)states[id]=change(states[id]);}
    private void StopTerrain(Guid id,string? error=null)
    {
        try
        {
            var job=science.TerrainJob(id);if(job.State is "Queued" or "Running" or "Pausing")science.ChangeTerrainJob(id,j=>j with{State=error is not null?"Failed":j.State=="Pausing"?"Paused":"Interrupted",Message=error??"Worker stopped. Explicit resume verifies originals and completed checkpoints; no download resumes automatically."});
        }
        catch(ArgumentException){/* A permanent collected-science deletion removed this queued job. Never recreate it. */}
        catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException or JsonException){logger?.LogWarning(e,"Terrain job stop could not be persisted. Explicit recovery is required when the Library returns.");}
    }
    private void StopLidar(Guid id,string? error=null)
    {
        try
        {
            var job=science.LidarJob(id);if(job.State is "Queued" or "Running" or "Pausing")science.ChangeLidarJob(id,j=>j with{State=error is not null?"Failed":j.State=="Pausing"?"Paused":"Interrupted",Message=error??"Worker stopped. Explicit resume verifies originals and completed checkpoints; no download resumes automatically."});
        }
        catch(ArgumentException){/* A permanent collected-science deletion removed this queued job. Never recreate it. */}
        catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException or JsonException){logger?.LogWarning(e,"Historical point job stop could not be persisted. Explicit recovery is required when the Library returns.");}
    }
    private bool lidarRecovered;
    public void EnsureLidarRecovered()
    {
        lock(gate){if(lidarRecovered)return;science.RecoverLidarJobs();lidarRecovered=true;}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try{if(science.AvailabilityError() is null){science.RecoverTerrainJobs();EnsureLidarRecovered();}}
        catch(Exception e)when(e is IOException or Microsoft.Data.Sqlite.SqliteException or JsonException){logger?.LogWarning(e,"Terrain recovery unavailable. Existing recording operations remain independent; terrain commands require a usable Library.");}
        try{await foreach(var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,work.Cancel.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();Update(work.Id,s=>s with{State="Running"});
                var results=await Task.Run(()=>
                {
                    if(missions.ScienceDeleted(work.Mission))throw new InvalidOperationException("Collected science was deliberately deleted; queued analysis cannot recreate it.");
                    if(work.PerformanceReason is {} reason)
                    {
                        // Coordinate available Library migrations/clear operations using its existing
                        // analysis lease; offline stored-report review does not require this lease.
                        using var performanceLease=science.AvailabilityError() is null?science.BeginAnalysis():null;
                        var evidence=MissionEvaluationArchive.Build(missions,science,work.Mission,linked.Token);
                        missions.SavePerformanceReport(evidence,reason,bytes=>storage?.RequireFlightCapacity(bytes));return Array.Empty<ScientificArtifact>();
                    }
                    using var lease=science.BeginAnalysis();
                    if(work.Lidar is {} lidar)
                        return new[]{LidarProcessing.Process(science,missions,lidar,settings?.Get().Preferences,linked.Token,(n,total)=>Update(work.Id,s=>s with{Completed=n,Total=total})).GetAwaiter().GetResult()};
                    if(work.Elevation is {} elevation)
                        return new[]{TerrainElevations.Process(science,missions,elevation,settings?.Get().Preferences,linked.Token,(n,total)=>Update(work.Id,s=>s with{Completed=n,Total=total}))};
                    if(work.Terrain is {} terrain)
                        return new[]{ContinuousTerrainSurveys.Build(science,missions,work.Mission,terrain,linked.Token,n=>Update(work.Id,s=>s with{Completed=n}))};
                    if(work.Coverage is {} request)
                    {
                        var result=ScientificCoverage.Calculate(science,missions.Research(work.Mission).PlanSnapshot,work.Mission,request,linked.Token,n=>Update(work.Id,s=>s with{Completed=n}));
                        var bytes=JsonSerializer.SerializeToUtf8Bytes(result,WireJson.Options);var identity=ScientificComposites.Identity(new{request,result,algorithm="scientific-coverage-1"});
                        var summary=new{result.InstrumentId,result.SurveySquareMetres,result.GeometricSquareMetres,result.GeometricSurveyPercent,result.ValidDataSquareMetres,result.ValidDataSurveyPercent,result.Acquisitions,result.Methods};
                        return new[]{science.PublishArtifact(work.Mission,"coverage","json",identity,summary,Math.Max(1048576,bytes.LongLength+65536),path=>File.WriteAllBytes(path,bytes),linked.Token)};
                    }
                    return ScientificComposites.Build(science,missions,work.Mission,work.Composite!,linked.Token,(n,total)=>Update(work.Id,s=>s with{Completed=n,Total=total}));
                },linked.Token);
                Update(work.Id,s=>s with{State="Completed",Artifacts=results.Select(r=>r.Id).ToArray(),Message="Verified local analysis outputs published. Originals and recorder were not modified."});
            }
            catch(OperationCanceledException){if(work.Lidar is {} lidar)StopLidar(lidar);if(work.Elevation is {} e)StopTerrain(e);Update(work.Id,s=>s with{State="Cancelled",Message=work.Terrain is not null?"Cancelled before corridor publication. No complete terrain product is claimed.":"Cancelled. Already completed tiles remain individually identified; no complete operation is claimed."});}
            catch(Exception e)when(e is not OutOfMemoryException){if(work.Lidar is {} lidar)StopLidar(lidar,e.Message);if(work.Elevation is {} elevation)StopTerrain(elevation,e.Message);Update(work.Id,s=>s with{State="Failed",Message=e.Message});}
            finally{lock(gate)cancellations.Remove(work.Id);work.Cancel.Dispose();}
        }}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        finally{while(queue.Reader.TryRead(out var pending)){if(pending.Lidar is {} lidar)StopLidar(lidar);if(pending.Elevation is {} e)StopTerrain(e);Update(pending.Id,s=>s with{State="Cancelled",Message="Application stopped before this operation began."});lock(gate)cancellations.Remove(pending.Id);pending.Cancel.Dispose();}}
    }
}
