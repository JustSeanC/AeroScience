using AeroScience.Shared;
namespace AeroScience.Engine;

public static class OrganizationConnectionApi
{
    public static void MapOrganizationConnections(this WebApplication app)
    {
        app.MapSubmissions();
        var api=app.MapGroup("/api/organizations");
        api.AddEndpointFilter(async(context,next)=>{
            if(!AccessControl.IsLoopback(context.HttpContext))return Results.Json(new{error="Manage organization connections on the companion PC. Personal Research remains available here."},statusCode:403);
            return await next(context);
        });
        api.MapGet("",async(OrganizationConnections connections,IOrganizationCredentials vault,HttpContext c)=>new{vaultAvailable=vault.Available,connections=await connections.List(c.RequestAborted)});
        api.MapPost("/review",(OrganizationConnectRequest r,OrganizationConnections connections,HttpContext c)=>connections.Review(r.Address,c.RequestAborted));
        api.MapPost("/begin",(OrganizationConnectApproval r,OrganizationConnections connections,HttpContext c)=>connections.Begin(r,c.RequestAborted));
        api.MapPost("/finish",(OrganizationConnectionFinish r,OrganizationConnections connections,HttpContext c)=>connections.Finish(r.PendingId,c.RequestAborted));
        api.MapPost("/pending/{id:guid}/open",async(Guid id,OrganizationConnections connections,HttpContext c)=>{
            var desktop=c.RequestServices.GetService<IDesktopActions>();if(desktop is null)return Results.Conflict(new{error="Open the approval link in your browser, or use the Windows companion to launch it."});
            desktop.OpenOrganizationApproval(await connections.ApprovalUri(id,c.RequestAborted));return Results.Ok(new{opened=true});
        });
        api.MapGet("/{id:guid}",(Guid id,int? offset,OrganizationConnections connections,HttpContext c)=>connections.Status(id,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/opportunities",(Guid id,int? offset,OrganizationConnections connections,HttpContext c)=>connections.Research(id,false,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/claims",(Guid id,int? offset,OrganizationConnections connections,HttpContext c)=>connections.Research(id,true,offset??0,c.RequestAborted));
        api.MapPost("/{id:guid}/claim-command",(Guid id,OrganizationClaimCommand r,OrganizationConnections connections,HttpContext c)=>connections.ClaimCommand(id,r,c.RequestAborted));
        api.MapPost("/{id:guid}/claims/{claim:guid}/mission-preview",async(Guid id,Guid claim,OrganizationMissionPreviewRequest r,OrganizationConnections connections,AeroScience.Missions.MissionStore store,PreflightReadinessService readiness,HttpContext c)=>{var preview=await connections.PreviewMission(id,claim,r,store,c.RequestAborted);return new{preview,readiness=readiness.Assess(preview.Suggested)};});
        api.MapPost("/{id:guid}/claims/{claim:guid}/prepare-mission",async(Guid id,Guid claim,OrganizationMissionSave r,OrganizationConnections connections,AeroScience.Missions.MissionStore store,ScientificPlanData preparation,HttpContext c)=>{var saved=await connections.PrepareMission(id,claim,r,store,c.RequestAborted);preparation.PlanChanged(saved.Plan);return saved;});
        api.MapGet("/{id:guid}/progress/programs/{program:guid}",(Guid id,Guid program,int? offset,OrganizationConnections connections,HttpContext c)=>connections.ProgramProgress(id,program,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}",(Guid id,Guid campaign,OrganizationConnections connections,HttpContext c)=>connections.CampaignProgress(id,campaign,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}/cards",(Guid id,Guid campaign,int? offset,string? search,string? sort,string? state,OrganizationConnections connections,HttpContext c)=>connections.CampaignCards(id,campaign,offset??0,search,sort,state,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/cards/{card:guid}",(Guid id,Guid card,int? revision,int? offset,OrganizationConnections connections,HttpContext c)=>connections.CampaignCard(id,card,revision,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}/approved",(Guid id,Guid campaign,Guid? opportunity,int? revision,int? offset,string? search,string? sort,OrganizationConnections connections,HttpContext c)=>connections.ApprovedResults(id,campaign,opportunity,revision,offset??0,search,sort,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/approved/{root:guid}/{card:guid}",(Guid id,Guid root,Guid card,int? offset,OrganizationConnections connections,HttpContext c)=>connections.ApprovedResult(id,root,card,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/approved/{root:guid}/{card:guid}/locations",(Guid id,Guid root,Guid card,int? offset,OrganizationConnections connections,HttpContext c)=>connections.ApprovedLocations(id,root,card,offset??0,c.RequestAborted));
        api.MapGet("/{id:guid}/progress/campaigns/{campaign:guid}/own",(Guid id,Guid campaign,int? offset,OrganizationConnections connections,HttpContext c)=>connections.CampaignOwn(id,campaign,offset??0,c.RequestAborted));
        OrganizationCoverageApi.Map(api);OrganizationWorkflowApi.Map(api);
        api.MapPost("/{id:guid}/disconnect",(Guid id,OrganizationConnections connections,HttpContext c)=>connections.Disconnect(id,c.RequestAborted));
        app.MapGet("/api/research/organization-cards",(int? offset,AeroScience.Missions.MissionStore store)=>store.OrganizationResearchCards(offset??0));
        app.MapGet("/api/research/cards/{id:guid}/organization",(Guid id,int? offset,AeroScience.Missions.MissionStore store)=>new{assignment=store.ResearchCard(id).OrganizationAssignment,check=store.LatestOrganizationClaimCheck(id),history=store.OrganizationPreparationHistory(id,offset??0)});
        app.MapPost("/api/research/cards/{id:guid}/organization/reconcile",async(Guid id,OrganizationClaimReconcile r,OrganizationConnections connections,AeroScience.Missions.MissionStore store,HttpContext c)=>{
            if(!AccessControl.IsLoopback(c))return Results.Json(new{error="Refresh organization eligibility on the companion PC. Local flight evidence remains available here."},statusCode:403);
            return Results.Ok(await connections.ReconcileMission(id,r,store,c.RequestAborted));
        });
    }
}
