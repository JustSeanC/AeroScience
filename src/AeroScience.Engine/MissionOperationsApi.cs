using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public static class MissionOperationsApi
{
    static T Parse<T>(string value) where T:struct,Enum=>Enum.TryParse<T>(value,true,out var result)&&Enum.IsDefined(result)?result:throw new ArgumentException("Choose a supported timeline source/category.");
    public static void Map(WebApplication app)
    {
        // Existing host session, pairing and CSRF policy applies; no organization call or upload.
        app.MapGet("/api/missions/{id:guid}/operations",(Guid id,MissionStore store)=>store.RecordedOperations(id));
        app.MapGet("/api/missions/{id:guid}/operations/status",(Guid id,MissionStore store)=>store.OperationsStatus(id));
        app.MapGet("/api/missions/{id:guid}/timeline/reference",(Guid id,string source,string referenceId,MissionStore store,ScienceStore science)=>store.TimelineReference(id,Parse<MissionTimelineSource>(source),referenceId,science));
        app.MapGet("/api/missions/{id:guid}/timeline",(Guid id,DateTimeOffset? fromUtc,DateTimeOffset? toUtc,string? lane,Guid? cardId,Guid? sessionId,string? instrumentId,string? cursor,bool? recent,int? limit,MissionStore store,ScienceStore science,HttpContext context)=>store.MissionTimeline(id,new(fromUtc,toUtc,lane is null?null:Parse<MissionTimelineLane>(lane),cardId,sessionId,instrumentId,cursor,recent??false,limit??50),science,context.RequestAborted));
        app.MapGet("/api/missions/{id:guid}/timeline/evidence",(Guid id,string source,string referenceId,string sha256,long? afterSequence,MissionStore store,ScienceStore science,HttpContext context)=>store.InspectTimeline(id,new(Parse<MissionTimelineSource>(source),referenceId,sha256),science,afterSequence??0,context.RequestAborted));
    }
}
