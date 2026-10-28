using AeroScience.Shared;
namespace AeroScience.Engine;

public static class OrganizationWorkflowApi
{
    static string Query(HttpContext c)
    {
        string[] allowed=["offset","campaign","opportunity","publication","revision"];
        if(c.Request.Query.Any(q=>!allowed.Contains(q.Key)||q.Value.Count!=1||q.Value.ToString().Length>80))throw new ArgumentException("Choose a bounded workflow view and exact publication.");return QueryString.Create(c.Request.Query.Select(q=>new KeyValuePair<string,string?>(q.Key,q.Value.ToString()))).Value??"";
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/{id:guid}/progress/workflow/programs/{program:guid}",(Guid id,Guid program,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<ProgramWorkflowView>(id,$"/workflow/programs/{program}",c.RequestAborted));
        api.MapGet("/{id:guid}/progress/workflow/programs/{program:guid}/phases",(Guid id,Guid program,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<OrganizationPage<TestPhaseView>>(id,$"/workflow/programs/{program}/phases"+Query(c),c.RequestAborted));
        api.MapGet("/{id:guid}/progress/workflow/programs/{program:guid}/exceptions",(Guid id,Guid program,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<OrganizationPage<WorkflowExceptionView>>(id,$"/workflow/programs/{program}/exceptions"+Query(c),c.RequestAborted));
        api.MapGet("/{id:guid}/progress/workflow/campaigns/{campaign:guid}",(Guid id,Guid campaign,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<CampaignWorkflowView>(id,$"/workflow/campaigns/{campaign}"+Query(c),c.RequestAborted));
        api.MapGet("/{id:guid}/progress/workflow/cards/{card:guid}",(Guid id,Guid card,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<PublishedWorkflowCard>(id,$"/workflow/cards/{card}"+Query(c),c.RequestAborted));
        api.MapGet("/{id:guid}/progress/workflow/history/{resource:guid}",(Guid id,Guid resource,OrganizationConnections connections,HttpContext c)=>connections.CoverageRead<OrganizationPage<WorkflowHistoryView>>(id,$"/workflow/history/{resource}"+Query(c),c.RequestAborted));
    }
}
