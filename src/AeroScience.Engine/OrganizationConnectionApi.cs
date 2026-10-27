namespace AeroScience.Engine;

public static class OrganizationConnectionApi
{
    public static void MapOrganizationConnections(this WebApplication app)
    {
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
        api.MapPost("/{id:guid}/disconnect",(Guid id,OrganizationConnections connections,HttpContext c)=>connections.Disconnect(id,c.RequestAborted));
    }
}
