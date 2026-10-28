using AeroScience.Shared;
namespace AeroScience.Organization;

public static class OrganizationReviewApi
{
    public const string ResponsePath="/api/member/reviews/{root:guid}/respond";
    public static void Map(WebApplication app)
    {
        // Campaign/role authorization is performed freshly on EVERY service endpoint, never inferred from a menu.
        var api=app.MapGroup("/api/organization/reviews").RequireAuthorization();
        api.MapGet("",(int? offset,string? search,string? state,Guid? campaign,OrganizationReviews s,HttpContext c)=>s.Queue(offset??0,search,ParseState(state),campaign,c.User,c.RequestAborted));
        api.MapGet("/{root:guid}",(Guid root,int? requestOffset,OrganizationReviews s,HttpContext c)=>s.Workspace(root,requestOffset??0,c.User,c.RequestAborted));
        api.MapGet("/{root:guid}/packages",(Guid root,int? offset,OrganizationReviews s,HttpContext c)=>s.Packages(root,offset??0,c.User,c.RequestAborted));
        api.MapPost("/{root:guid}/evidence",(Guid root,ResearchReviewSelection r,int? offset,OrganizationReviews s,HttpContext c)=>s.Evidence(root,r,offset??0,c.User,c.RequestAborted));
        api.MapGet("/{root:guid}/item",(Guid root,Guid receipt,string key,int? offset,OrganizationReviews s,HttpContext c)=>s.Item(root,receipt,key,offset??0,c.User,c.RequestAborted));
        api.MapGet("/{root:guid}/history",(Guid root,int? after,OrganizationReviews s,HttpContext c)=>s.History(root,after??0,c.User,false,c.RequestAborted));
        api.MapPost("/{root:guid}/commands",(Guid root,ResearchReviewCommand r,OrganizationReviews s,HttpContext c)=>s.Command(root,r,c.User,c.RequestAborted)).RequireRateLimiting("public");
        api.MapGet("/permissions/{campaign:guid}",(Guid campaign,int? offset,OrganizationReviews s,HttpContext c)=>s.Permissions(campaign,offset??0,c.User,c.RequestAborted));
        api.MapPost("/permissions/{campaign:guid}",(Guid campaign,CampaignReviewPermission r,OrganizationReviews s,HttpContext c)=>s.Permission(campaign,r,c.User,c.RequestAborted));
        app.MapGet("/api/member/reviews/{root:guid}",(Guid root,int? requestOffset,OrganizationReviews s,HttpContext c)=>s.PilotSnapshot(root,requestOffset??0,c,c.RequestAborted));
        app.MapGet("/api/member/reviews/{root:guid}/history",(Guid root,int? after,OrganizationReviews s,HttpContext c)=>s.PilotHistory(root,after??0,c,c.RequestAborted));
        app.MapPost(ResponsePath,(Guid root,ResearchReviewCommand r,OrganizationReviews s,HttpContext c)=>s.PilotCommand(root,r,c,c.RequestAborted)).RequireRateLimiting("public");
        app.MapGet("/api/membership/reviews/{root:guid}",(Guid root,int? requestOffset,OrganizationReviews s,HttpContext c)=>s.PilotPortal(root,requestOffset??0,c.User,c.RequestAborted)).RequireAuthorization();
        app.MapGet("/api/membership/reviews/{root:guid}/history",(Guid root,int? after,OrganizationReviews s,HttpContext c)=>s.History(root,after??0,c.User,true,c.RequestAborted)).RequireAuthorization();
        app.MapPost("/api/membership/reviews/{root:guid}/respond",(Guid root,ResearchReviewCommand r,OrganizationReviews s,HttpContext c)=>r.Action==ResearchReviewAction.PilotResponse?s.Command(root,r,c.User,c.RequestAborted):throw new OrganizationForbidden()).RequireAuthorization();
    }
    static ResearchReviewState? ParseState(string? value)=>value switch{
        null or ""=>null,"awaitingReview"=>ResearchReviewState.AwaitingReview,"moreEvidenceRequested"=>ResearchReviewState.MoreEvidenceRequested,
        "updatedSubmission"=>ResearchReviewState.UpdatedSubmission,"approved"=>ResearchReviewState.Approved,"rejected"=>ResearchReviewState.Rejected,
        _=>throw new ArgumentException("Choose a supported review-state filter.")};
    public static bool IsNativeResponse(PathString path)=>path.StartsWithSegments("/api/member/reviews")&&path.Value?.EndsWith("/respond",StringComparison.Ordinal)==true;
}
