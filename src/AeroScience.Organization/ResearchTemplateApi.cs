using AeroScience.Shared;
namespace AeroScience.Organization;

public static class ResearchTemplateApi
{
    public static void Map(WebApplication app)
    {
        // Browser cookie + existing strict Origin/antiforgery. No device grant is upgraded.
        var api=app.MapGroup("/api/research-templates").RequireAuthorization();
        api.MapGet("/policy",(OrganizationResearchTemplates s,HttpContext c)=>s.Policy(c.User,c.RequestAborted));
        api.MapPost("/policy",(ResearchPublishingPolicyCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.SetPolicy(r,c.User,c.RequestAborted));
        api.MapGet("/proposals",(bool? manage,int? offset,string? search,OrganizationResearchTemplates s,HttpContext c)=>s.List(true,manage??false,false,offset??0,search,c.User,c.RequestAborted));
        api.MapPost("/proposals",(TemplateProposalCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.Propose(null,r,c.User,c.RequestAborted));
        api.MapPut("/proposals/{id:guid}",(Guid id,TemplateProposalCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.Propose(id,r,c.User,c.RequestAborted));
        api.MapPost("/proposals/{id:guid}/decision",(Guid id,TemplateProposalDecision r,OrganizationResearchTemplates s,HttpContext c)=>s.Decide(id,r,c.User,c.RequestAborted));
        api.MapGet("/drafts",(int? offset,string? search,OrganizationResearchTemplates s,HttpContext c)=>s.List(false,true,false,offset??0,search,c.User,c.RequestAborted));
        api.MapPost("/drafts",(ResearchDraftCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.Draft(null,r,c.User,c.RequestAborted));
        api.MapPut("/drafts/{id:guid}",(Guid id,ResearchDraftCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.Draft(id,r,c.User,c.RequestAborted));
        api.MapPost("/drafts/{id:guid}/publish",(Guid id,OrganizationPublishRequest r,OrganizationResearchTemplates s,HttpContext c)=>s.Publish(id,r,c.User,c.RequestAborted));
        api.MapGet("/published",(int? offset,string? search,OrganizationResearchTemplates s,HttpContext c)=>s.List(false,false,true,offset??0,search,c.User,c.RequestAborted));
        api.MapGet("/{id:guid}/revisions/{revision:int}",(Guid id,int revision,OrganizationResearchTemplates s,HttpContext c)=>s.Revision(id,revision,c.User,c.RequestAborted));
        api.MapPost("/preview",async(TemplateFileRequest r,OrganizationMembership m,HttpContext c)=>{if((await m.Current(c.User,c.RequestAborted))?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();return AeroScience.Missions.PortableResearchTemplates.Read(r.Contents);});
        api.MapPost("/compose",async(TemplateFileRequest r,OrganizationMembership m,HttpContext c)=>{await m.RequireManager(c.User,false,c.RequestAborted);return AeroScience.Missions.PortableResearchTemplates.Revise(r.Contents);});
        api.MapGet("/publication-reviews",(int? offset,OrganizationResearchTemplates s,HttpContext c)=>s.Reviews(offset??0,c.User,c.RequestAborted));
        api.MapGet("/publication-context",(string kind,Guid id,OrganizationResearchTemplates s,HttpContext c)=>s.PublicationContext(kind,id,c.User,c.RequestAborted));
        api.MapGet("/publication-history",(string kind,Guid id,int? offset,OrganizationResearchTemplates s,HttpContext c)=>s.Publications(kind,id,offset??0,c.User,c.RequestAborted));
        api.MapPost("/publication-reviews",(PublicationReviewCommand r,OrganizationResearchTemplates s,HttpContext c)=>s.RequestReview(r,c.User,c.RequestAborted));
        api.MapPost("/publication-reviews/{id:guid}/decision",(Guid id,PublicationReviewDecision r,OrganizationResearchTemplates s,HttpContext c)=>s.DecideReview(id,r,c.User,c.RequestAborted));
    }
}
