using AeroScience.Missions;
using AeroScience.Shared;

namespace AeroScience.Engine;

public static class PersonalResearchApi
{
    public static void MapPersonalResearchEndpoints(this WebApplication app)
    {
        // Existing host session, paired-display and CSRF middleware protects these routes.
        app.MapGet("/api/research/starters",()=>MissionStore.ResearchStarters());
        app.MapPost("/api/research/starters/prepare",(ResearchStarterRequest request,MissionStore store)=>store.PrepareResearchStarter(request));
        app.MapGet("/api/research/cards", (int? offset, MissionStore store) => store.ResearchCards(offset ?? 0));
        app.MapGet("/api/research/cards/{id:guid}", (Guid id, int? revision, MissionStore store) => store.ResearchCard(id, revision));
        app.MapGet("/api/research/cards/{id:guid}/flights", (Guid id, int? offset, MissionStore store) => store.ResearchFlights(id, offset ?? 0));
        app.MapGet("/api/research/cards/{id:guid}/overview",(Guid id,MissionStore store)=>store.ResearchOverview(id));
        app.MapGet("/api/research/cards/{id:guid}/history",(Guid id,int? offset,MissionStore store)=>store.ResearchHistory(id,offset??0));
        app.MapPost("/api/research/cards/{id:guid}/comparison",(Guid id,ResearchComparisonRequest request,MissionStore store,ScienceStore science,HttpContext context)=>store.CompareResearch(id,request,context.RequestAborted,science));
        app.MapGet("/api/research/recorded-attempts/{id:guid}",(Guid id,MissionStore store)=>store.RecordedResearchAttempt(id));
        app.MapPost("/api/research/cards", (SaveResearchCardRequest request, MissionStore store) => store.SaveResearchCard(request));
        app.MapPost("/api/research/cards/{id:guid}/plan", (Guid id, ResearchCardReference request, MissionStore store) =>
        {
            if (request.Id != id) throw new ArgumentException("The selected card identity changed.");
            return store.CreateResearchPlan(id, request.Revision);
        });
        app.MapGet("/api/research/attempts", (Guid? cardId, Guid? flightId, int? offset, MissionStore store) => store.ResearchAttempts(cardId, flightId, offset ?? 0));
        app.MapPost("/api/research/attempts", (ResearchAttemptRequest request, MissionStore store, ScienceStore science,HttpContext context) => store.EvaluateResearchAttempt(request, context.RequestAborted,science));
        app.MapGet("/api/research/attempts/{id:guid}/atmospheric",(Guid id,MissionStore store,ScienceStore science,HttpContext context)=>{var result=store.ResearchAttempt(id);return new{result,review=store.ReviewAtmosphericEvidence(result,science,context.RequestAborted)};});
        app.MapGet("/api/research/attempts/{id:guid}/survey",(Guid id,MissionStore store,ScienceStore science,HttpContext context)=>{var result=store.ResearchAttempt(id);return new{result,review=store.ReviewSurveyEvidence(result,science,context.RequestAborted)};});
        app.MapGet("/api/research/plans/{id:guid}/survey-sources",(Guid id,MissionStore store,ScienceStore science,MissionCoordinator coordinator,ScientificAccessSettings settings)=>{
            var plan=store.Plan(id)??throw new ArgumentException("Select a saved plan.");var reference=MissionStore.SelectedResearchCards(plan).FirstOrDefault(r=>store.ResearchCard(r.Id,r.Revision).Survey is not null)??throw new ArgumentException("Select a survey card.");
            var card=store.ResearchCard(reference.Id,reference.Revision);var frame=coordinator.Snapshot().Telemetry;var position=frame is{IsValid:true,IsPaused:false}&&(DateTimeOffset.UtcNow-frame.TimestampUtc).TotalSeconds is >=0 and <=6?new Coordinates(frame.Latitude,frame.Longitude):null;var preferences=settings.Get().Preferences;
            return new{scope="Current aircraft point only; bounded local candidates, not verified full-pixel or mission-area coverage. No provider request or download was made.",position,measurements=card.Survey!.Measurements.Select(m=>{
                if(m==SurveyMeasurement.AirTemperature)return new{measurement=m,status="LocalOnly",message="Uses simulator air temperature; no external dataset or online consent is required.",sources=Array.Empty<object>()};
                var kind=m==SurveyMeasurement.TerrainElevation?RemoteKind.Terrain:RemoteKind.Thermal;
                var candidates=position is null?[]:science.Availability(position,kind).Where(a=>preferences.ExcludedSourceIds?.Contains(a.Source.Id)!=true).Take(5).ToArray();
                return new{measurement=m,status=position is null?"LocationUnknown":candidates.Any(c=>c.FilesAvailable)?"LocalCandidate":preferences.OnlineEnabled&&preferences.PrivacyAccepted?"MayPrepare":"ConsentRequired",message=position is null?"Connect an aircraft or review the saved route/area with existing plan readiness.":candidates.Any(c=>c.FilesAvailable)?"Retained candidates can be reused. Actual selected-source datum, scene date, native pixels and QA must still qualify. Review existing source-selection settings if needed.":preferences.OnlineEnabled&&preferences.PrivacyAccepted?"No retained candidate was found here. Existing approved preparation can attempt acquisition within current budgets; provider coverage/availability is unverified.":"No local candidate was found here. Review Allow Online Access in Settings before any provider query; local-only air temperature still works.",sources=candidates.Select(c=>(object)new{c.Source.Id,c.Source.Manifest.Name,c.Source.Manifest.Provider,c.Source.Manifest.VerticalDatum,c.Source.Manifest.ObservationUtc,c.FilesAvailable,qualifiedDatum=m!=SurveyMeasurement.TerrainElevation||SurveyResearch.TerrainDatum(c.Source.Manifest.VerticalDatum) is not null}).ToArray()};
            }).ToArray()};
        });
        app.MapGet("/api/missions/{id:guid}/research-operations",(Guid id,MissionCoordinator coordinator)=>coordinator.PersonalResearch(id));
        app.MapPost("/api/research/control",(ResearchCardControl request,MissionCoordinator coordinator)=>coordinator.ControlPersonalResearch(request));
        app.MapGet("/api/missions/{id:guid}/research-attempts",(Guid id,int? offset,MissionStore store)=>store.RecordedResearchAttempts(id,offset??0));
        app.MapPost("/api/research/recorded-attempts/{id:guid}/evaluate",(Guid id,ResearchEvaluationCommand request,MissionStore store,ScienceStore science,HttpContext context)=>store.EvaluateRecordedResearchAttempt(id,request.RequestId,context.RequestAborted,science));
    }
}
public sealed record ResearchEvaluationCommand(Guid RequestId);
