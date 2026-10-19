using System.Text.Json;
using System.Threading.Channels;
using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public sealed record AnalysisJob(Guid Id,Guid MissionId,string Kind,string State,long Completed,long? Total,Guid[] Artifacts,string? Message=null);
public sealed class ScientificAnalysisJobs(MissionStore missions,ScienceStore science) : BackgroundService
{
    private sealed record Work(Guid Id,Guid Mission,ScientificCoverageRequest? Coverage,CompositeRequest? Composite,CancellationTokenSource Cancel);
    private readonly Channel<Work> queue=Channel.CreateBounded<Work>(4);
    private readonly object gate=new();private readonly Dictionary<Guid,AnalysisJob> states=[];private readonly Dictionary<Guid,CancellationTokenSource> cancellations=[];
    public AnalysisJob Enqueue(Guid mission,ScientificCoverageRequest? coverage=null,CompositeRequest? composite=null)
    {
        ResearchExports.RequireCompleted(missions,mission);if((coverage is null)==(composite is null))throw new ArgumentException("Choose one analysis operation.");
        var id=Guid.NewGuid();var cancel=new CancellationTokenSource();var job=new AnalysisJob(id,mission,coverage is null?"composite":"coverage","Queued",0,null,[]);
        lock(gate)
        {
            foreach(var old in states.Values.Where(s=>s.State is "Completed" or "Failed" or "Cancelled").Take(Math.Max(0,states.Count-95)).ToArray())states.Remove(old.Id);
            if(states.Count>=100){cancel.Dispose();throw new InvalidOperationException("Analysis history is full. Restart AeroScience after current work completes.");}
            states.Add(id,job);cancellations.Add(id,cancel);
            if(!queue.Writer.TryWrite(new(id,mission,coverage,composite,cancel))){states.Remove(id);cancellations.Remove(id);cancel.Dispose();throw new InvalidOperationException("Analysis queue is full. Wait for current work to finish.");}
        }
        return job;
    }
    public AnalysisJob Get(Guid id){lock(gate)return states.GetValueOrDefault(id)??throw new ArgumentException("Analysis job not found.");}
    public AnalysisJob Cancel(Guid id){lock(gate){if(cancellations.TryGetValue(id,out var source))source.Cancel();return Get(id);}}
    private void Update(Guid id,Func<AnalysisJob,AnalysisJob> change){lock(gate)states[id]=change(states[id]);}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try{await foreach(var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,work.Cancel.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();Update(work.Id,s=>s with{State="Running"});
                var results=await Task.Run(()=>
                {
                    using var lease=science.BeginAnalysis();
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
            catch(OperationCanceledException){Update(work.Id,s=>s with{State="Cancelled",Message="Cancelled. Already completed tiles remain individually identified; no complete operation is claimed."});}
            catch(Exception e)when(e is not OutOfMemoryException){Update(work.Id,s=>s with{State="Failed",Message=e.Message});}
            finally{lock(gate)cancellations.Remove(work.Id);work.Cancel.Dispose();}
        }}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}
        finally{while(queue.Reader.TryRead(out var pending)){Update(pending.Id,s=>s with{State="Cancelled",Message="Application stopped before this operation began."});lock(gate)cancellations.Remove(pending.Id);pending.Cancel.Dispose();}}
    }
}
