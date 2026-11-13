using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public static class LidarEndpoints
{
    public static void MapLidarEndpoints(this WebApplication app)
    {
        app.MapPost("/api/science/lidar/discovery",(LidarDiscoveryRequest request,LidarCatalog catalog,HttpContext c)=>catalog.Discover(request,c.RequestAborted));
        app.MapPost("/api/science/library/lidar/preview",async(LidarDownloadRequest request,ScientificLibraryJobs jobs,HttpContext c)=>jobs.Project(await jobs.PreviewLidar(request,c.RequestAborted)));
        app.MapGet("/api/science/lidar/import",(LidarImportSession session,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(session.Snapshot()):Results.StatusCode(403));
        app.MapPost("/api/science/lidar/import",(LidarImportCommand request,LidarImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);session.Import(request);return Results.Accepted();});
        app.MapPost("/api/science/lidar/import/cancel",(LidarImportSession session,HttpContext c)=>{if(!AccessControl.IsLoopback(c))return Results.StatusCode(403);session.Cancel();return Results.Ok();});
        app.MapGet("/api/science/artifacts/{id:guid}/lidar-review",(Guid id,int? afterChunk,long? afterSource,long? throughSource,string? search,ScienceStore science,MissionStore missions,HttpContext c)=>LidarProcessing.Review(science,missions,id,afterChunk??0,afterSource??0,throughSource,search,c.RequestAborted));
        app.MapPost("/api/missions/{id:guid}/lidar/jobs",(Guid id,LidarProcessingRequest request,ScienceStore science,MissionStore missions,ScientificAccessSettings settings,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(LidarProcessing.Create(science,missions,id,request,settings.Get().Preferences,c.RequestAborted)):Results.StatusCode(403));
        app.MapGet("/api/missions/{id:guid}/lidar/jobs",(Guid id,int? offset,ScienceStore science,ScientificAnalysisJobs jobs)=>{jobs.EnsureLidarRecovered();return science.LidarJobs(id,offset??0);});
        app.MapGet("/api/lidar/jobs/{id:guid}",(Guid id,ScienceStore science,ScientificAnalysisJobs jobs)=>{jobs.EnsureLidarRecovered();return science.LidarJob(id);});
        app.MapGet("/api/lidar/jobs/{id:guid}/tiles",(Guid id,int? offset,ScienceStore science)=>science.LidarCheckpoints(id).Skip(Math.Max(0,offset??0)).Take(100));
        app.MapPost("/api/lidar/jobs/{id:guid}/control",(Guid id,TerrainElevationControl request,ScientificAnalysisJobs jobs,HttpContext c)=>AccessControl.IsLoopback(c)?Results.Ok(jobs.ControlLidar(id,request)):Results.StatusCode(403));
        app.MapGet("/api/science/artifacts/{id:guid}/lidar-points",(Guid id,int? offset,int? limit,ScienceStore science,MissionStore missions,ScientificAccessSettings settings,HttpContext c)=>{
            if(offset is <0||limit is <1 or >100)throw new ArgumentException("Inspect pages of at most 100 retained original points.");c.Response.RegisterForDispose(science.BeginAnalysis());var artifact=science.Artifact(id);_=LidarProcessing.RequireCurrent(science,missions,artifact,settings.Get().Preferences,c.RequestAborted);var tile=LidarProcessing.ReadTile(science,artifact);var start=offset??0;var size=limit??30;
            return new{tile.Source.Id,tile.Source.Manifest.Name,tile.OriginalFile,tile.OriginalSha256,tile.Source.Manifest.PointCloud,tile.GeometryArtifactId,tile.GeometrySha256,tile.Algorithm,tile.EvidenceClass,total=tile.Points.Length,offset=start,points=tile.Points.Skip(start).Take(size),nextOffset=start+size<tile.Points.Length?(int?)(start+size):null};});
    }
}
