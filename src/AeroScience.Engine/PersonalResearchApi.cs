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
        app.MapPost("/api/research/cards/{id:guid}/comparison",(Guid id,ResearchComparisonRequest request,MissionStore store,HttpContext context)=>store.CompareResearch(id,request,context.RequestAborted));
        app.MapGet("/api/research/recorded-attempts/{id:guid}",(Guid id,MissionStore store)=>store.RecordedResearchAttempt(id));
        app.MapPost("/api/research/cards", (SaveResearchCardRequest request, MissionStore store) => store.SaveResearchCard(request));
        app.MapPost("/api/research/cards/{id:guid}/plan", (Guid id, ResearchCardReference request, MissionStore store) =>
        {
            if (request.Id != id) throw new ArgumentException("The selected card identity changed.");
            return store.CreateResearchPlan(id, request.Revision);
        });
        app.MapGet("/api/research/attempts", (Guid? cardId, Guid? flightId, int? offset, MissionStore store) => store.ResearchAttempts(cardId, flightId, offset ?? 0));
        app.MapPost("/api/research/attempts", (ResearchAttemptRequest request, MissionStore store, HttpContext context) => store.EvaluateResearchAttempt(request, context.RequestAborted));
        app.MapGet("/api/missions/{id:guid}/research-operations",(Guid id,MissionCoordinator coordinator)=>coordinator.PersonalResearch(id));
        app.MapPost("/api/research/control",(ResearchCardControl request,MissionCoordinator coordinator)=>coordinator.ControlPersonalResearch(request));
        app.MapGet("/api/missions/{id:guid}/research-attempts",(Guid id,int? offset,MissionStore store)=>store.RecordedResearchAttempts(id,offset??0));
        app.MapPost("/api/research/recorded-attempts/{id:guid}/evaluate",(Guid id,ResearchEvaluationCommand request,MissionStore store,HttpContext context)=>store.EvaluateRecordedResearchAttempt(id,request.RequestId,context.RequestAborted));
    }
}
public sealed record ResearchEvaluationCommand(Guid RequestId);
