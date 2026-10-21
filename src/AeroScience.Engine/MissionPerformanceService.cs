using AeroScience.Shared;
using AeroScience.Missions;
namespace AeroScience.Engine;

// Reuses the existing bounded analysis queue. Reads never create/revise final reports.
public sealed class MissionPerformanceService(MissionStore missions,ScientificAnalysisJobs analysis,ILogger<MissionPerformanceService> logger):BackgroundService
{
    readonly HashSet<Guid> attempted=[];
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,Guid> jobs=new();
    int archiveOffset;
    MissionLiveProjection? live;Guid? liveId;MissionPerformanceReport? projected;
    public object Read(Guid id)
    {
        var mission=missions.Detail(id)?.Mission??throw new ArgumentException("Mission not found.");
        var latest=missions.LatestPerformanceReport(id);var reports=latest is null?Array.Empty<MissionPerformanceReport>():[latest];var revisions=missions.PerformanceRevisions(id);
        if(mission.Status!=MissionStatus.Recording)
        {
            AnalysisJob? job=null;if(jobs.TryGetValue(id,out var jobId)){try{job=analysis.Get(jobId);}catch(ArgumentException){jobs.TryRemove(id,out _);}}
            var legacy=reports.Length==0&&missions.CollectionSnapshot<MissionAssessmentContext>(id) is null;
            return new{state=reports.Length>0?"Available":legacy?"Not Graded":job?.State??"Pending",message=legacy?"Legacy archive has no frozen rubric or obligations. Existing scientific results remain available; no historical grading evidence is invented.":job?.Message,reports,revisions,projection=(MissionPerformanceReport?)null};
        }
        var projection=Volatile.Read(ref projected);
        if(projection?.MissionId!=id){var context=missions.CollectionSnapshot<MissionAssessmentContext>(id);projection=MissionGrading.Report(new(1,id,mission.StartedUtc,false,context,[],[],[],[],["Live evidence projection pending the next bounded background update."]));}
        return new{state="Provisional",reports,revisions,projection};
    }
    public AnalysisJob Recompute(Guid mission,string reason)
    {var job=analysis.EnqueuePerformance(mission,reason);jobs[mission]=job.Id;return job;}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recent=missions.List(20);
                var active=recent.FirstOrDefault(m=>m.Status==MissionStatus.Recording);
                if(active is not null){if(liveId!=active.Id){liveId=active.Id;live=new(missions,active.Id);}Volatile.Write(ref projected,live!.Update());}
                else {live=null;liveId=null;Volatile.Write(ref projected,null);}
                var page=archiveOffset==0?recent:missions.List(20,archiveOffset);archiveOffset=page.Count<20?0:archiveOffset+20;
                foreach(var mission in page.Where(m=>m.Status!=MissionStatus.Recording))
                {
                    if(attempted.Contains(mission.Id)||missions.CollectionSnapshot<MissionAssessmentContext>(mission.Id) is null||missions.LatestPerformanceReport(mission.Id) is not null)continue;
                    attempted.Add(mission.Id);
                    try{Recompute(mission.Id,"End-flight archived evaluation");}catch(InvalidOperationException){attempted.Remove(mission.Id);break;}
                }
            }
            catch(Exception e)when(e is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException){logger.LogWarning("Archived performance evaluation unavailable: {Kind}",e.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
