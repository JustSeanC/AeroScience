using System.Security.Claims;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class OrganizationResearchTemplates(OrganizationDatabase db,OrganizationManagement history,OrganizationMembership members,OrganizationPublishing publishing)
{
    const int Page=20;
    static readonly Guid PolicyIdentity=Guid.Parse("24000000-0000-4000-8000-000000000001");
    static Guid Actor(ClaimsPrincipal user)=>Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    static TemplateDocument View(ResearchTemplateRecord row)=>JsonSerializer.Deserialize<TemplateDocument>(row.SnapshotJson,WireJson.Options)!;
    static void Command(int version,Guid request,int revision){if(version!=1||request==Guid.Empty||revision<0)throw new ArgumentException("Use the supported command version, a unique request identity and the saved revision.");}
    async Task Authorize(ClaimsPrincipal user,bool manager,bool admin,CancellationToken ct)
    {
        await publishing.Lock(ct);
        if(manager||admin)await members.RequireManager(user,admin,ct);
        else if((await members.Current(user,ct))?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
    }
    async Task ReadAccess(ClaimsPrincipal user,bool manager,CancellationToken ct)
    {if(manager)await members.RequireManager(user,false,ct);else if((await members.Current(user,ct))?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();}
    void Save(ResearchTemplateRecord row,TemplateDocument value,Guid actor,string action,string? actorName=null)
    {
        row.Revision=value.Revision;row.Name=value.Template.Name;row.Status=value.Status.ToString();row.PublishedRevision=value.PublishedRevision;row.SnapshotJson=JsonSerializer.Serialize(value,WireJson.Options);
        history.Revision(row.Id,row.Revision,row.Proposal?"template-proposal":"research-template",value,actor);history.Audit(actor,actorName??value.AuthorName,action,row.Id,row.Revision);
    }
    public async Task<ResearchPublishingPolicy> Policy(ClaimsPrincipal user,CancellationToken ct){await ReadAccess(user,false,ct);return await publishing.Policy(ct);}
    public Task<ResearchPublishingPolicy> SetPolicy(ResearchPublishingPolicyCommand command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);if(!Enum.IsDefined(command.Mode))throw new ArgumentException("Unknown publishing policy.");
        return history.Mutate(user,command.RequestId,new{operation="publishing-policy",command},async(actor,name)=>{
            var current=await publishing.Policy(ct);if(current.Revision!=command.ExpectedRevision)throw new OrganizationConflict("Publishing policy changed. Review its current version.");
            var row=await db.PublishingPolicies.SingleOrDefaultAsync(ct);if(row is null){row=new();db.PublishingPolicies.Add(row);}row.Revision=current.Revision+1;row.Mode=(int)command.Mode;
            var result=new ResearchPublishingPolicy(1,row.Revision,command.Mode);history.Revision(PolicyIdentity,row.Revision,"publishing-policy",result,actor);history.Audit(actor,name,"publishing-policy-changed",PolicyIdentity,row.Revision);return result;
        },ct,()=>Authorize(user,true,true,ct));
    }
    public Task<TemplateDocument> Propose(Guid? id,TemplateProposalCommand command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);var original=PortableResearchTemplates.Read(command.Contents);
        var portable=command.ProposedName is null?original:PortableResearchTemplates.Create(command.ProposedName,original.Description,original.Definition,original.Attribution);
        var purpose=PortableResearchTemplates.Text(command.Purpose,2000,"Proposed purpose",true);var message=PortableResearchTemplates.Text(command.Message,2000,"Proposal message");
        if(!command.ConfirmDefinitionOnly)throw new ArgumentException("Review and explicitly confirm this definition-only proposal.");
        return history.Mutate(user,command.RequestId,new{operation="template-proposal",id,command},async(actor,name)=>{
            if(command.OrganizationId!=(await members.Public(ct)).Id)throw new OrganizationForbidden();
            var row=id is {} existing?await db.ResearchTemplates.SingleOrDefaultAsync(x=>x.Id==existing&&x.Proposal&&x.OwnerId==actor,ct)??throw new OrganizationNotFound():new ResearchTemplateRecord{Id=Guid.NewGuid(),Proposal=true,OwnerId=actor,CreatedUtc=OrganizationTime.UtcNow};
            if(row.Revision!=command.ExpectedRevision)throw new OrganizationConflict("The proposal changed. Review its current saved revision.");
            if(id is not null&&View(row).Status is not(TemplateProposalStatus.ChangesRequested or TemplateProposalStatus.Submitted))throw new OrganizationConflict("This proposal has already been decided. Make an independent new proposal instead.");
            var value=new TemplateDocument(row.Id,row.Revision+1,portable,actor,name,row.CreatedUtc,OrganizationTime.UtcNow,TemplateProposalStatus.Submitted,purpose,message,OriginalTemplateSha256:original.Sha256);
            if(id is null)db.ResearchTemplates.Add(row);Save(row,value,actor,"template-proposed");return value;
        },ct,()=>Authorize(user,false,false,ct));
    }
    public async Task<OrganizationPage<TemplateDocument>> List(bool proposals,bool manage,bool published,int offset,string? search,ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,manage,ct);if(offset<0)throw new ArgumentException("Invalid template page.");var actor=Actor(user);
        var mayManage=(await members.Current(user,ct))?.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager;
        var query=db.ResearchTemplates.AsNoTracking().Where(x=>x.Proposal==proposals);
        if(proposals&&!manage)query=query.Where(x=>x.OwnerId==actor);
        if(!proposals&&!manage&&!published)throw new OrganizationForbidden();
        if(published)query=query.Where(x=>x.PublishedRevision!=null);
        var text=PortableResearchTemplates.Text(search,100,"Search");if(text.Length>0)query=query.Where(x=>EF.Functions.ILike(x.Name,"%"+text+"%"));
        var rows=await query.OrderBy(x=>x.CreatedUtc).ThenBy(x=>x.Id).Skip(offset).Take(Page+1).ToArrayAsync(ct);var values=new List<TemplateDocument>();
        foreach(var row in rows.Take(Page))
        {
            if(published){var r=await history.ReadRevision(row.Id,row.PublishedRevision!.Value,ct);var t=JsonSerializer.Deserialize<TemplateDocument>(r.SnapshotJson,WireJson.Options)!;values.Add(t with{Message="",Explanation="",Purpose="",ProposalId=null,DraftId=null,OriginalTemplateSha256=null,ProgramId=mayManage?t.ProgramId:null,CampaignId=mayManage?t.CampaignId:null});}
            else values.Add(View(row));
        }
        return new(1,values.ToArray(),rows.Length>Page?checked(offset+Page):null);
    }
    public async Task<DefinitionRevision> Revision(Guid id,int revision,ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,false,ct);var row=await db.ResearchTemplates.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new OrganizationNotFound();
        if(!row.Proposal||row.OwnerId!=Actor(user))await members.RequireManager(user,false,ct);
        var r=await history.ReadRevision(id,revision,ct);if(r.Kind!=(row.Proposal?"template-proposal":"research-template"))throw new OrganizationNotFound();return r;
    }
    public Task<TemplateDocument> Decide(Guid id,TemplateProposalDecision command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);
        if(command.Status is not(TemplateProposalStatus.ChangesRequested or TemplateProposalStatus.AcceptedForDrafting or TemplateProposalStatus.Rejected))throw new ArgumentException("Choose Request Changes, Accept for Drafting or Reject.");
        var explanation=PortableResearchTemplates.Text(command.Explanation,2000,"Explanation",command.Status!=TemplateProposalStatus.AcceptedForDrafting);
        return history.Mutate(user,command.RequestId,new{operation="template-proposal-decision",id,command},async(actor,name)=>{
            var row=await db.ResearchTemplates.SingleOrDefaultAsync(x=>x.Id==id&&x.Proposal,ct)??throw new OrganizationNotFound();var old=View(row);
            if(row.Revision!=command.ExpectedRevision||old.Status is not(TemplateProposalStatus.Submitted or TemplateProposalStatus.ChangesRequested))throw new OrganizationConflict("This proposal has a newer decision. Reload before deciding.");
            Guid? draft=null;
            if(command.Status==TemplateProposalStatus.AcceptedForDrafting)
            {
                var record=new ResearchTemplateRecord{Id=Guid.NewGuid(),OwnerId=actor,CreatedUtc=OrganizationTime.UtcNow};db.ResearchTemplates.Add(record);draft=record.Id;
                var value=new TemplateDocument(record.Id,1,old.Template,actor,name,record.CreatedUtc,record.CreatedUtc,TemplateProposalStatus.AcceptedForDrafting,old.Purpose,ProposalId:id);
                Save(record,value,actor,"research-draft-created-from-proposal");
            }
            var result=old with{Revision=old.Revision+1,Status=command.Status,Explanation=explanation,DraftId=draft,UpdatedUtc=OrganizationTime.UtcNow};Save(row,result,actor,"template-proposal-"+command.Status,name);return result;
        },ct,()=>Authorize(user,true,false,ct));
    }
    public Task<TemplateDocument> Draft(Guid? id,ResearchDraftCommand command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);var portable=PortableResearchTemplates.Read(command.Contents);
        return history.Mutate(user,command.RequestId,new{operation="research-draft",id,command},async(actor,name)=>{
            if(command.ProgramId is {} p&&!await db.Programs.AnyAsync(x=>x.Id==p,ct))throw new OrganizationNotFound();
            if(command.CampaignId is {} c&&!await db.Campaigns.AnyAsync(x=>x.Id==c&&x.ProgramId==command.ProgramId,ct))throw new ArgumentException("Select the campaign's actual parent program.");
            var row=id is {} key?await db.ResearchTemplates.SingleOrDefaultAsync(x=>x.Id==key&&!x.Proposal,ct)??throw new OrganizationNotFound():new ResearchTemplateRecord{Id=Guid.NewGuid(),OwnerId=actor,CreatedUtc=OrganizationTime.UtcNow};
            if(row.Revision!=command.ExpectedRevision)throw new OrganizationConflict("A newer draft exists. Reload instead of overwriting it.");
            var prior=id is null?null:View(row);var result=new TemplateDocument(row.Id,row.Revision+1,portable,actor,name,row.CreatedUtc,OrganizationTime.UtcNow,TemplateProposalStatus.AcceptedForDrafting,
                Purpose:prior?.Purpose??"",ProposalId:prior?.ProposalId,PublishedRevision:row.PublishedRevision,ProgramId:command.ProgramId,CampaignId:command.CampaignId);
            if(id is null)db.ResearchTemplates.Add(row);Save(row,result,actor,"research-draft-saved");return result;
        },ct,()=>Authorize(user,true,false,ct));
    }
    public Task<TemplateDocument> Publish(Guid id,OrganizationPublishRequest command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);
        return history.Mutate(user,command.RequestId,new{operation="research-template-publish",id,command},async(actor,name)=>{
            var authorization=await publishing.Require("research-template",id,command,user,ct);
            var row=await db.ResearchTemplates.SingleAsync(x=>x.Id==id&&!x.Proposal,ct);var old=View(row);PortableResearchTemplates.ValidateDefinition(old.Template.Definition);
            var result=old with{Revision=old.Revision+1,Status=TemplateProposalStatus.Published,PublishedRevision=old.Revision+1,UpdatedUtc=OrganizationTime.UtcNow,Publication=authorization};
            Save(row,result,actor,"research-template-published");publishing.Record("research-template",id,result.Revision,authorization);
            if(old.ProposalId is {} proposal){var original=await db.ResearchTemplates.SingleAsync(x=>x.Id==proposal&&x.Proposal,ct);var value=View(original);Save(original,value with{Revision=value.Revision+1,Status=TemplateProposalStatus.Published,PublishedRevision=result.Revision,UpdatedUtc=OrganizationTime.UtcNow},actor,"template-proposal-published",name);}
            return result;
        },ct,()=>Authorize(user,true,false,ct));
    }
    public Task<PublicationReviewView> RequestReview(PublicationReviewCommand command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.DraftRevision);
        return history.Mutate(user,command.RequestId,new{operation="publication-review",command},async(actor,name)=>{
            var policy=await publishing.Policy(ct);var target=await publishing.Target(command.Kind,command.ResourceId,ct);
            if(policy.Mode!=ResearchPublishingMode.AdministratorApproval||policy.Revision!=command.PolicyRevision||target.Revision!=command.DraftRevision||target.Hash!=command.DraftSha256)throw new OrganizationConflict("Review the current policy and exact saved draft before requesting approval.");
            var id=Guid.NewGuid();var value=new PublicationReviewView(id,1,command.Kind,command.ResourceId,target.Revision,target.Hash,policy.Revision,target.Name,target.Author,actor,PublicationReviewStatus.Pending,null,"",OrganizationTime.UtcNow,true);
            db.PublicationReviews.Add(new(){Id=id,ResourceId=command.ResourceId,Kind=command.Kind,DraftRevision=target.Revision,Revision=1,Status="Pending",SnapshotJson=JsonSerializer.Serialize(value,WireJson.Options),CreatedUtc=value.UpdatedUtc});
            history.Revision(id,1,"publication-review",value,actor);history.Audit(actor,name,"publication-review-requested",id,1);return value;
        },ct,()=>Authorize(user,true,false,ct));
    }
    public async Task<OrganizationPage<PublicationReviewView>> Reviews(int offset,ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,true,ct);if(offset<0)throw new ArgumentException("Invalid review page.");var rows=await db.PublicationReviews.AsNoTracking().Where(x=>!x.Kind.StartsWith("publication:")).OrderByDescending(x=>x.CreatedUtc).ThenBy(x=>x.Id).Skip(offset).Take(Page+1).ToArrayAsync(ct);var items=new List<PublicationReviewView>();
        foreach(var r in rows.Take(Page)){var v=JsonSerializer.Deserialize<PublicationReviewView>(r.SnapshotJson,WireJson.Options)!;items.Add(v with{Current=await publishing.Current(v,ct)});}return new(1,items.ToArray(),rows.Length>Page?checked(offset+Page):null);
    }
    public async Task<object> PublicationContext(string kind,Guid id,ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,true,ct);var policy=await publishing.Policy(ct);var target=await publishing.Target(kind,id,ct);
        var rows=await db.PublicationReviews.AsNoTracking().Where(x=>x.ResourceId==id&&x.Kind==kind&&x.DraftRevision==target.Revision&&x.Status=="Approved").OrderByDescending(x=>x.CreatedUtc).Take(20).ToArrayAsync(ct);
        PublicationReviewView? approval=null;foreach(var row in rows){var value=JsonSerializer.Deserialize<PublicationReviewView>(row.SnapshotJson,WireJson.Options)!;if(await publishing.Current(value,ct)){approval=value;break;}}
        return new{policy,draftRevision=target.Revision,draftSha256=target.Hash,authorId=target.Author,name=target.Name,approval};
    }
    public async Task<OrganizationPage<ResearchPublicationRecord>> Publications(string kind,Guid id,int offset,ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,true,ct);if(offset<0||kind is not("campaign" or "opportunity" or "research-template"))throw new ArgumentException("Choose a valid publication history page.");
        var rows=await db.PublicationReviews.AsNoTracking().Where(x=>x.ResourceId==id&&x.Kind=="publication:"+kind).OrderByDescending(x=>x.CreatedUtc).ThenBy(x=>x.Id).Skip(offset).Take(Page+1).ToArrayAsync(ct);
        return new(1,rows.Take(Page).Select(r=>new ResearchPublicationRecord(r.Id,kind,id,r.DraftRevision,JsonSerializer.Deserialize<PublicationAuthorship>(r.SnapshotJson,WireJson.Options)!)).ToArray(),rows.Length>Page?checked(offset+Page):null);
    }
    public Task<PublicationReviewView> DecideReview(Guid id,PublicationReviewDecision command,ClaimsPrincipal user,CancellationToken ct)
    {
        Command(command.Version,command.RequestId,command.ExpectedRevision);if(command.Status is not(PublicationReviewStatus.Approved or PublicationReviewStatus.Rejected))throw new ArgumentException("Choose approve or reject.");
        var explanation=PortableResearchTemplates.Text(command.Explanation,2000,"Publication decision explanation",command.Status==PublicationReviewStatus.Rejected);
        return history.Mutate(user,command.RequestId,new{operation="publication-decision",id,command},async(actor,name)=>{
            var row=await db.PublicationReviews.SingleOrDefaultAsync(x=>x.Id==id&&!x.Kind.StartsWith("publication:"),ct)??throw new OrganizationNotFound();var old=JsonSerializer.Deserialize<PublicationReviewView>(row.SnapshotJson,WireJson.Options)!;
            if(old.Revision!=command.ExpectedRevision||old.Status!=PublicationReviewStatus.Pending||!await publishing.Current(old,ct))throw new OrganizationConflict("This review or draft changed. Reload the current exact revision.");
            if(old.AuthorId==actor)throw new OrganizationForbidden();
            var result=old with{Revision=old.Revision+1,Status=command.Status,ApproverId=actor,Explanation=explanation,UpdatedUtc=OrganizationTime.UtcNow};row.Revision=result.Revision;row.Status=result.Status.ToString();row.SnapshotJson=JsonSerializer.Serialize(result,WireJson.Options);
            history.Revision(id,result.Revision,"publication-review",result,actor);history.Audit(actor,name,"publication-review-"+command.Status,id,result.Revision);return result;
        },ct,()=>Authorize(user,true,true,ct));
    }
    public async Task<OrganizationTemplate[]> Catalog(ClaimsPrincipal user,CancellationToken ct)
    {
        await ReadAccess(user,true,ct);
        // Built-ins stay small. Published personal-derived templates use a separate paged selector.
        return OrganizationTemplates.All();
    }
}
