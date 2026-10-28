using AeroScience.Shared;
namespace AeroScience.Organization;

public static class ProgramWorkflowApi
{
    public static void Map(WebApplication app)
    {
        foreach(var native in new[]{false,true}){
            var api=app.MapGroup((native?"/api/member":"/api/membership")+"/research-progress/workflow");if(!native)api.RequireAuthorization();
            async Task<Guid?> Pilot(OrganizationDevices d,HttpContext c)=>native?(await d.Authorize(c,c.RequestAborted)).UserId:null;
            api.MapGet("/programs/{program:guid}",async(Guid program,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.View(program,c.User,c.RequestAborted,await Pilot(d,c)));
            api.MapGet("/programs/{program:guid}/phases",async(Guid program,int? offset,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.PhasePage(program,offset??0,c.User,c.RequestAborted,await Pilot(d,c)));
            api.MapGet("/campaigns/{campaign:guid}",async(Guid campaign,int? offset,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.Campaign(campaign,offset??0,c.User,c.RequestAborted,await Pilot(d,c)));
            api.MapGet("/cards/{card:guid}",async(Guid card,int revision,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.PublishedCard(card,revision,c.User,c.RequestAborted,await Pilot(d,c)));
            api.MapGet("/programs/{program:guid}/exceptions",async(Guid program,Guid? campaign,Guid? opportunity,int? publication,int? offset,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.Exceptions(program,campaign,opportunity,publication,offset??0,c.User,c.RequestAborted,await Pilot(d,c)));
            api.MapGet("/history/{id:guid}",async(Guid id,int? offset,ProgramWorkflow w,OrganizationDevices d,HttpContext c)=>await w.History(id,offset??0,c.User,c.RequestAborted,await Pilot(d,c)));
            if(!native){
                api.MapGet("/phases/{phase:guid}/revisions/{revision:int}",(Guid phase,int revision,ProgramWorkflow w,HttpContext c)=>w.PhaseRevision(phase,revision,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapGet("/review-context/{root:guid}/{card:guid}",(Guid root,Guid card,int revision,ProgramWorkflow w,HttpContext c)=>w.ReviewContext(root,card,revision,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/programs/{program:guid}/exception-preview",(Guid program,WorkflowExceptionPreviewRequest request,ProgramWorkflow w,HttpContext c)=>w.PreviewException(program,request,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/programs/{program:guid}/policy",(Guid program,ProgramPolicyCommand command,ProgramWorkflow w,HttpContext c)=>w.SavePolicy(program,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/programs/{program:guid}/phases",(Guid program,PhaseSaveCommand command,ProgramWorkflow w,HttpContext c)=>w.SavePhase(program,null,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPut("/programs/{program:guid}/phases/{phase:guid}",(Guid program,Guid phase,PhaseSaveCommand command,ProgramWorkflow w,HttpContext c)=>w.SavePhase(program,phase,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/programs/{program:guid}/phases/{phase:guid}/commands",(Guid program,Guid phase,PhaseStateCommand command,ProgramWorkflow w,HttpContext c)=>w.PhaseCommand(program,phase,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapGet("/programs/{program:guid}/sources",(Guid program,Guid opportunity,int publication,int? offset,ProgramWorkflow w,HttpContext c)=>w.Sources(program,opportunity,publication,offset??0,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/programs/{program:guid}/exceptions",(Guid program,WorkflowExceptionCommand command,ProgramWorkflow w,HttpContext c)=>w.AuthorizeException(program,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
                api.MapPost("/exceptions/{id:guid}/revoke",(Guid id,ExceptionRevokeCommand command,ProgramWorkflow w,HttpContext c)=>w.RevokeException(id,command,c.User,c.RequestAborted)).RequireAuthorization(OrganizationApplication.ManagePolicy);
            }
        }
    }
}
