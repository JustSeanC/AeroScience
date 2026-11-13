using AeroScience.Missions;
using AeroScience.Shared;
namespace AeroScience.Engine;

public static class SurveyResultsEndpoints
{
    public static void MapSurveyResults(this WebApplication app)
    {
        app.MapGet("/api/missions/{id:guid}/survey-results",(Guid id,ScienceStore science,MissionStore missions,HttpContext c)=>SurveyResults.Catalog(science,missions,id,c.RequestAborted));
        app.MapGet("/api/missions/{id:guid}/survey-track",(Guid id,int? after,int? limit,MissionStore missions,HttpContext c)=>SurveyResults.Track(missions,id,after??0,limit??1024,c.RequestAborted));
        app.MapGet("/api/science/artifacts/{id:guid}/survey-manifest",(Guid id,ScienceStore science,MissionStore missions,ScientificAccessSettings settings,HttpContext c)=>
        {c.Response.RegisterForDispose(science.BeginAnalysis());return SurveyResults.Manifest(science,missions,id,settings.Get().Preferences,c.RequestAborted);});
        app.MapPost("/api/science/artifacts/{id:guid}/survey-page",(Guid id,SurveyViewQuery query,ScienceStore science,MissionStore missions,ScientificAccessSettings settings,HttpContext c)=>
        {c.Response.RegisterForDispose(science.BeginAnalysis());return SurveyResults.Page(science,missions,id,query,settings.Get().Preferences,c.RequestAborted);});
        app.MapPost("/api/science/artifacts/{id:guid}/survey-profile",(Guid id,SurveyProfileQuery query,ScienceStore science,MissionStore missions,ScientificAccessSettings settings,HttpContext c)=>
        {c.Response.RegisterForDispose(science.BeginAnalysis());return SurveyProfiles.Create(science,missions,id,query,settings.Get().Preferences,c.RequestAborted);});
        app.MapGet("/api/science/artifacts/{id:guid}/survey-geometry",(Guid id,string sha,long? cursor,int? limit,ScienceStore science,MissionStore missions,HttpContext c)=>
        {c.Response.RegisterForDispose(science.BeginAnalysis());return SurveyResults.Geometry(science,missions,id,sha,cursor??0,limit??8,c.RequestAborted);});
    }
}
