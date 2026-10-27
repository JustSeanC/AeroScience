using AeroScience.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed record ActiveOrganizationRole(bool Administrator):IAuthorizationRequirement;
public sealed class ActiveOrganizationRoleHandler(OrganizationMembership membership):AuthorizationHandler<ActiveOrganizationRole>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context,ActiveOrganizationRole requirement)
    {
        var m=await membership.Current(context.User,CancellationToken.None);
        if(m?.State==OrganizationMembershipState.Active&&(requirement.Administrator?m.Role==OrganizationRoles.Administrator:m.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager))context.Succeed(requirement);
    }
}
public static class OrganizationEnrollmentApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/public/organization",(OrganizationMembership m,CancellationToken ct)=>m.Public(ct)).RequireRateLimiting("public");
        app.MapPost("/api/enrollment",(OrganizationJoinRequest request,OrganizationMembership m,HttpContext c)=>m.Join(request,c.User,c.RequestAborted)).RequireRateLimiting("identity");
        app.MapGet("/api/membership",async(OrganizationMembership m,HttpContext c)=>new{organization=await m.Public(c.RequestAborted),membership=await m.Current(c.User,c.RequestAborted)}).RequireAuthorization();
        var api=app.MapGroup("/api/organization/members").RequireAuthorization(OrganizationApplication.ManagePolicy);
        api.MapGet("",(int? offset,OrganizationMembership m,CancellationToken ct)=>m.Members(offset??0,ct));
        api.MapGet("/invitations",(int? offset,OrganizationMembership m,CancellationToken ct)=>m.Invitations(offset??0,ct));
        api.MapPost("/invitations",(OrganizationInvitationRequest r,OrganizationMembership m,HttpContext c)=>m.Invite(r,c.User,c.RequestAborted));
        api.MapPost("/invitations/{id:guid}/revoke",(Guid id,OrganizationPublishRequest r,OrganizationMembership m,HttpContext c)=>{if(r.Version!=1)throw new ArgumentException("Unsupported request version.");return m.RevokeInvitation(id,r.RequestId,c.User,c.RequestAborted);});
        api.MapPut("/policy",(OrganizationPolicyRequest r,OrganizationMembership m,HttpContext c)=>m.Policy(r,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.PublishPolicy);
        api.MapPut("/{id:guid}",(Guid id,OrganizationMemberRequest r,OrganizationMembership m,HttpContext c)=>m.ChangeMember(id,r,c.User,c.RequestAborted));
        app.MapPost("/api/device/start",(OrganizationDeviceStart r,OrganizationDevices d,HttpContext c)=>d.Start(r,c.RequestAborted)).RequireRateLimiting("identity");
        app.MapPost("/api/device/poll",(OrganizationDevicePoll r,OrganizationDevices d,HttpContext c)=>d.Poll(r,c.RequestAborted)).RequireRateLimiting("device-poll");
        app.MapPost("/api/device/disconnect",(OrganizationDevices d,HttpContext c)=>d.Disconnect(c,c.RequestAborted)).RequireRateLimiting("identity");
        app.MapGet("/api/connection/approval/{id:guid}",(Guid id,OrganizationDevices d,HttpContext c)=>d.Review(id,c.User,c.RequestAborted)).RequireAuthorization();
        app.MapPost("/api/connection/approval",(OrganizationDeviceApproval r,OrganizationDevices d,HttpContext c)=>d.Approve(r,c.User,c.RequestAborted)).RequireAuthorization();
        app.MapGet("/api/member/published",async(int? offset,OrganizationDevices d,HttpContext c)=>{var grant=await d.Authorize(c,c.RequestAborted);return await d.Published(grant.UserId!.Value,offset??0,c.RequestAborted);});
        app.MapGet("/api/membership/published",(int? offset,OrganizationDevices d,HttpContext c)=>d.Published(OrganizationMembership.Actor(c.User),offset??0,c.RequestAborted)).RequireAuthorization();
    }
}
