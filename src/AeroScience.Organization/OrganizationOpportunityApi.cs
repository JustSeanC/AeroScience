using AeroScience.Shared;
namespace AeroScience.Organization;

public static class OrganizationOpportunityApi
{
    public static void Map(WebApplication app)
    {
        var manage=app.MapGroup("/api/organization/opportunities").RequireAuthorization(OrganizationApplication.ManagePolicy);
        manage.MapGet("",(Guid? campaign,int? offset,OrganizationOpportunities o,HttpContext c)=>o.Catalog(campaign,offset??0,c.User,true,c.RequestAborted));
        manage.MapPost("/campaigns/{campaign:guid}",(Guid campaign,OrganizationOpportunityRequest r,OrganizationOpportunities o,HttpContext c)=>o.Save(campaign,null,r,c.User,c.RequestAborted));
        manage.MapPut("/{id:guid}/campaigns/{campaign:guid}",(Guid id,Guid campaign,OrganizationOpportunityRequest r,OrganizationOpportunities o,HttpContext c)=>o.Save(campaign,id,r,c.User,c.RequestAborted));
        manage.MapPost("/{id:guid}/publish",(Guid id,OrganizationPublishRequest r,OrganizationOpportunities o,HttpContext c)=>o.Publish(id,r,c.User,c.RequestAborted));
        manage.MapPost("/{id:guid}/availability",(Guid id,OrganizationOpportunityAvailability r,OrganizationOpportunities o,HttpContext c)=>o.Availability(id,r,c.User,c.RequestAborted));
        manage.MapGet("/claims",(Guid? opportunity,int? offset,OrganizationOpportunities o,HttpContext c)=>o.Claims(opportunity,offset??0,c.User,true,c.RequestAborted));
        manage.MapPost("/claim-command",(OrganizationClaimCommand r,OrganizationOpportunities o,HttpContext c)=>o.Change(r,c,false,true,c.RequestAborted));
        manage.MapGet("/claims/{id:guid}/events",(Guid id,long? after,OrganizationOpportunities o,HttpContext c)=>o.Events(id,after??0,c.User,true,c.RequestAborted));
        var browser=app.MapGroup("/api/membership/research").RequireAuthorization();
        browser.MapGet("/opportunities",(int? offset,OrganizationOpportunities o,HttpContext c)=>o.Catalog(null,offset??0,c.User,false,c.RequestAborted));
        browser.MapGet("/claims",(int? offset,OrganizationOpportunities o,HttpContext c)=>o.Claims(null,offset??0,c.User,false,c.RequestAborted));
        browser.MapPost("/claim-command",(OrganizationClaimCommand r,OrganizationOpportunities o,HttpContext c)=>o.Change(r,c,false,false,c.RequestAborted));
        app.MapGet("/api/member/opportunities",async(int? offset,OrganizationDevices d,OrganizationOpportunities o,HttpContext c)=>{var grant=await d.Authorize(c,c.RequestAborted);return await o.Catalog(null,offset??0,Principal(grant.UserId!.Value),false,c.RequestAborted);});
        app.MapGet("/api/member/claims",async(int? offset,OrganizationDevices d,OrganizationOpportunities o,HttpContext c)=>{var grant=await d.Authorize(c,c.RequestAborted);return await o.Claims(null,offset??0,Principal(grant.UserId!.Value),false,c.RequestAborted);});
        app.MapGet("/api/member/claims/{id:guid}/original",async(Guid id,OrganizationDevices d,OrganizationOpportunities o,HttpContext c)=>{var grant=await d.Authorize(c,c.RequestAborted);return await o.Original(id,Principal(grant.UserId!.Value),c.RequestAborted);});
        app.MapPost("/api/member/claim-command",(OrganizationClaimCommand r,OrganizationOpportunities o,HttpContext c)=>o.Change(r,c,true,false,c.RequestAborted)).RequireRateLimiting("public");
    }
    static System.Security.Claims.ClaimsPrincipal Principal(Guid id)=>new(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.NameIdentifier,id.ToString())],"OrganizationPublishedRead"));
}
