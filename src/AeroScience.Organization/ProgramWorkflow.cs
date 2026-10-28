using System.Data;
using System.Security.Claims;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed partial class ProgramWorkflow(OrganizationDatabase db,OrganizationManagement history,OrganizationMembership members)
{
    static T Read<T>(string json)=>JsonSerializer.Deserialize<T>(json,WireJson.Options)??throw new InvalidOperationException("Workflow history is unavailable.");
    static string Hash(object value)=>PreflightReadiness.Hash(value);
    async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    async Task<MembershipRecord> Active(ClaimsPrincipal actor,CancellationToken ct,bool manager=false)
    {
        var m=await members.Current(actor,ct);if(m?.State!=OrganizationMembershipState.Active||manager&&m.Role is not(OrganizationRoles.Administrator or OrganizationRoles.Manager))throw new OrganizationForbidden();var stamp=await db.Users.AsNoTracking().Where(u=>u.Id==m.UserId).Select(u=>u.SecurityStamp).SingleAsync(ct);if(stamp is null||stamp!=actor.FindFirstValue("AspNet.Identity.SecurityStamp"))throw new OrganizationUnauthorized();return m;
    }
    async Task<T> Snapshot<T>(ClaimsPrincipal actor,Guid? pilot,Func<Guid,bool,Task<T>> run,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);Guid id;bool manage=false;
        if(pilot is {} native){var m=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==native&&m.State==OrganizationMembershipState.Active,ct)??throw new OrganizationForbidden();id=m.UserId;}
        else{var m=await Active(actor,ct);id=m.UserId;manage=m.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager;}
        var value=await run(id,manage);await tx.CommitAsync(ct);return value;
    }
    IQueryable<CampaignContributionRecord> Live()=>CampaignProgressProjection.Current(db).Where(c=>c.Valid);
    IQueryable<CampaignContributionRecord> Approved()=>Live().Where(c=>c.State==ResearchReviewState.Approved&&c.SummaryJson!=""&&c.AcceptancePolicyVersion==1);
    async Task<ProgramRecord> Program(Guid id,CancellationToken ct)=>await db.Programs.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,ct)??throw new OrganizationNotFound();
    async Task<OrganizationOpportunitySnapshot> Card(Guid id,int revision,CancellationToken ct)
    {
        var original=await history.ReadRevision(id,revision,ct);var c=Read<OrganizationOpportunitySnapshot>(original.SnapshotJson);if(original.Kind!="opportunity"||c.Id!=id||c.Status!=OrganizationPublication.Published||c.Revision!=revision||Hash(c)!=original.Sha256)throw new OrganizationNotFound();return c;
    }
    static ReviewRequirementDefinition[] Requirements(OrganizationOpportunitySnapshot c)=>SubmissionReviewEvidence.Requirements(MissionStore.PublishedResearchCard(c,c.Id,c.CreatedUtc),c.Revision,Hash(c.Definition));
    public Task<PublishedWorkflowCard> PublishedCard(Guid id,int revision,ClaimsPrincipal actor,CancellationToken ct,Guid? pilot=null)=>Snapshot(actor,pilot,async(_,_)=>{var c=await Card(id,revision,ct);return new PublishedWorkflowCard(c,Requirements(c));},ct);
    async Task<T> Original<T>(Guid id,int revision,string kind,string sha,CancellationToken ct)
    {var r=await history.ReadRevision(id,revision,ct);if(r.Kind!=kind||r.Sha256!=sha)throw new InvalidOperationException("Workflow revision identity failed integrity verification.");return Read<T>(r.SnapshotJson);}
    async Task<ProgramPolicyView> Policy(Guid program,CancellationToken ct)
    {
        var r=await db.WorkflowPolicies.AsNoTracking().SingleOrDefaultAsync(p=>p.ProgramId==program,ct);if(r is null)return new(Guid.Empty,program,0,0,new(),Guid.Empty,"Not configured","none",DateTimeOffset.UnixEpoch,"Ordinary research: no phase gates or exception credit.");var p=await Original<ProgramPolicyView>(r.Id,r.Revision,"workflow-policy",r.SnapshotSha256,ct);if(p.ProgramId!=program||p.RulesRevision!=r.RulesRevision||p.Policy.PhaseGatesEnabled!=r.PhaseGatesEnabled||p.Policy.AdministrativeHold!=r.AdministrativeHold)throw new InvalidOperationException("Workflow policy index differs from its original revision.");return p;
    }
    async Task<TestPhaseSnapshot> Phase(TestPhaseRecord row,CancellationToken ct)
    {var p=await Original<TestPhaseSnapshot>(row.Id,row.Revision,"test-phase",row.SnapshotSha256,ct);if(p.Id!=row.Id||p.ProgramId!=row.ProgramId||p.DefinitionRevision!=row.DefinitionRevision||p.Definition.Enabled!=row.Enabled||p.Definition.Sequence!=row.Sequence||p.Held!=row.Held||p.Released!=row.Released)throw new InvalidOperationException("Phase index differs from its original revision.");ProgramWorkflowRules.Validate(p.Definition);return p;}
    async Task<TestPhaseSnapshot[]> Phases(Guid program,CancellationToken ct)
    {var rows=await db.TestPhases.AsNoTracking().Where(p=>p.ProgramId==program&&p.Enabled).OrderBy(p=>p.Sequence).ThenBy(p=>p.Id).Take(17).ToArrayAsync(ct);if(rows.Length>16)throw new InvalidOperationException("Too many active phases for this workflow version.");var result=new List<TestPhaseSnapshot>();foreach(var row in rows)result.Add(await Phase(row,ct));ProgramWorkflowRules.Graph(result);return result.ToArray();}
    internal IQueryable<WorkflowExceptionRecord> Effective(Guid program,ProgramPolicyView policy,Guid? phase,int? definitionRevision)
    {
        var live=Live();return db.WorkflowExceptions.AsNoTracking().Where(e=>e.ProgramId==program&&e.Active&&e.RulesRevision==policy.RulesRevision&&e.PhaseId==phase&&e.PhaseDefinitionRevision==definitionRevision&&
            (e.RootReceiptId==null||live.Any(c=>c.RootId==e.RootReceiptId&&c.CardId==e.CardId&&c.PilotId==e.PilotId&&c.ClaimId==e.ClaimId&&c.PublicationSha256==e.PublicationSha256&&c.ApprovalSignature==e.SourceSignature&&c.State==e.SourceState)));
    }
    IQueryable<WorkflowExceptionRecord> ForRule(IQueryable<WorkflowExceptionRecord> exceptions,PhaseRequirement rule)=>exceptions.Where(e=>e.OpportunityId==rule.OpportunityId&&e.PublicationRevision==rule.PublicationRevision&&e.PublicationSha256==rule.PublicationSha256&&e.RequirementKey==(rule.RequirementKey??""));
    IQueryable<CampaignContributionRecord> ForRule(PhaseRequirement rule)
    {
        var q=rule.RequirementKey is null?Approved():from c in Live() join a in db.WorkflowRequirementApprovals.AsNoTracking() on new{c.RootId,c.CardId} equals new{a.RootId,a.CardId} join i in db.WorkflowReviewIndexes.AsNoTracking() on c.RootId equals i.RootId where a.RequirementKey==rule.RequirementKey&&i.Version==1&&i.Valid&&i.ReviewRevision==c.ReviewRevision&&i.HeadSha256==c.SourceHeadSha256 select c;
        return q.Where(c=>c.OpportunityId==rule.OpportunityId&&c.PublicationRevision==rule.PublicationRevision&&c.PublicationSha256==rule.PublicationSha256);
    }
    async Task<WorkflowRuleProgress> Rule(Guid program,PhaseRequirement rule,ProgramPolicyView policy,Guid? phase,int? definitionRevision,CancellationToken ct)
    {
        var card=await Card(rule.OpportunityId,rule.PublicationRevision,ct);if(card.ProgramId!=program||Hash(card)!=rule.PublicationSha256)throw new InvalidOperationException("Progression requirement no longer matches its immutable publication.");var approved=ForRule(rule).Select(c=>c.PilotId);var exceptions=ForRule(Effective(program,policy,phase,definitionRevision),rule);var partial=exceptions.Where(e=>rule.AllowExceptions&&rule.CountAuthorizedPartialContributors&&e.Kind==WorkflowExceptionKind.ApprovedWithExceptions&&e.CountAsPartialContributor&&e.PilotId!=null).Select(e=>e.PilotId!.Value);
        var original=await approved.Distinct().LongCountAsync(ct);var authorized=await partial.Distinct().Where(id=>!approved.Contains(id)).LongCountAsync(ct);var total=await approved.Union(partial).LongCountAsync(ct);var waived=await exceptions.LongCountAsync(e=>rule.AllowWaivers&&e.Kind==WorkflowExceptionKind.RequirementWaiver,ct);var accepted=await exceptions.LongCountAsync(e=>rule.AllowExceptions&&e.Kind==WorkflowExceptionKind.ApprovedWithExceptions,ct);var fulfilled=waived>0||total>=rule.RequiredPilots;
        var live=await db.Opportunities.AsNoTracking().SingleAsync(o=>o.Id==card.Id,ct);var pending=await Live().Where(c=>c.OpportunityId==card.Id&&c.PublicationRevision==card.Revision&&c.PublicationSha256==rule.PublicationSha256&&c.State!=ResearchReviewState.Approved&&c.State!=ResearchReviewState.Rejected).LongCountAsync(ct);
        var campaign=await db.Campaigns.AsNoTracking().SingleAsync(c=>c.Id==card.CampaignId,ct);var status=waived>0?"Fulfilled by manager waiver — no measurement or additional pilot created":total>=rule.RequiredPilots?authorized>0?"Fulfilled with approved exceptions — original outcomes unchanged":"Fulfilled by approved scientific contributions":$"{original} of {rule.RequiredPilots} approved scientific contributors; {authorized} separately authorized partial contributor(s). More work needed.";
        return new(rule,card.Name,card.CampaignId,campaign.PublishedName,original,authorized,total,waived,accepted,fulfilled,status,live.PublishedRevision!=card.Revision,pending,rule.RequirementKey is {} key?Requirements(card).Single(q=>q.Key==key).Name:"Entire published card");
    }
    async Task<TestPhaseView[]> Evaluate(Guid program,ProgramPolicyView policy,TestPhaseSnapshot[] phases,CancellationToken ct)
    {var result=new List<TestPhaseView>();var authorized=new HashSet<Guid>();foreach(var p in phases){var view=PhaseView(p,policy,await PhaseRules(program,p,policy,ct),authorized);result.Add(view);if(view.Authorized)authorized.Add(view.Id);}return result.ToArray();}
    public Task<ProgramWorkflowView> View(Guid program,ClaimsPrincipal actor,CancellationToken ct,Guid? pilot=null)=>Snapshot(actor,pilot,async(_,manage)=>{var p=await Program(program,ct);var policy=await Policy(program,ct);var phases=await Evaluate(program,policy,await Phases(program,ct),ct);return new ProgramWorkflowView(1,(await members.Public(ct)).Id,program,p.Name,OrganizationTime.UtcNow,manage,policy,phases.FirstOrDefault(p=>!p.Authorized)?.Id,phases,ProgramWorkflowRules.Limitations);},ct);
    public async Task<ProgramPolicyView> SavePolicy(Guid program,ProgramPolicyCommand command,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(command.Version!=1||command.RequestId==Guid.Empty||command.ExpectedRevision<0||command.Policy is null||command.Policy.Version!=1||command.Policy.CountAuthorizedPartialContributors&&!command.Policy.AllowCampaignExceptions)throw new ArgumentException("Choose a versioned workflow policy; partial contributor credit requires the explicit exception policy.");ProgramWorkflowRules.Text(command.Reason,2000,"Policy explanation");
        return await history.Mutate(actor,command.RequestId,new{operation="workflow-policy",program,command},async(id,name)=>{
            _=await Program(program,ct);var old=await Policy(program,ct);if(old.Revision!=command.ExpectedRevision)throw new OrganizationConflict("The workflow policy changed. Your draft remains here; reload before saving.");var row=await db.WorkflowPolicies.SingleOrDefaultAsync(p=>p.ProgramId==program,ct);if(row is null){row=new(){Id=Guid.NewGuid(),ProgramId=program};db.WorkflowPolicies.Add(row);}var rulesChanged=old.Revision==0||(old.Policy with{AdministrativeHold=false})!=(command.Policy with{AdministrativeHold=false});row.Revision++;if(rulesChanged)row.RulesRevision++;row.PhaseGatesEnabled=command.Policy.PhaseGatesEnabled;row.AdministrativeHold=command.Policy.AdministrativeHold;var member=await Active(actor,ct,true);var value=new ProgramPolicyView(row.Id,program,row.Revision,row.RulesRevision,command.Policy,id,name,member.Role,OrganizationTime.UtcNow,command.Reason.Trim());row.SnapshotJson=JsonSerializer.Serialize(value,WireJson.Options);row.SnapshotSha256=Hash(value);history.Revision(row.Id,row.Revision,"workflow-policy",value,id);history.Audit(id,name,"workflow-policy-changed",row.Id,row.Revision);await db.SaveChangesAsync(ct);await Reconcile(program,ct);return value;
        },ct,async()=>{await Lock(ct);await Active(actor,ct,true);});
    }
    async Task SavePhaseState(TestPhaseRecord row,TestPhaseSnapshot next,Guid actor,string name,CancellationToken ct)
    {
        row.Revision=next.Revision;row.DefinitionRevision=next.DefinitionRevision;row.Sequence=next.Definition.Sequence;row.Enabled=next.Definition.Enabled;row.Held=next.Held;row.Released=next.Released;row.SnapshotJson=JsonSerializer.Serialize(next,WireJson.Options);row.SnapshotSha256=Hash(next);history.Revision(row.Id,row.Revision,"test-phase",next,actor);history.Audit(actor,name,"phase-"+next.Action,row.Id,row.Revision);await db.SaveChangesAsync(ct);
    }
    public async Task<TestPhaseSnapshot> SavePhase(Guid program,Guid? phase,PhaseSaveCommand command,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(command.Version!=1||command.RequestId==Guid.Empty||command.ExpectedRevision<0||phase is null&&command.ExpectedRevision!=0||phase is not null&&command.ExpectedRevision==0)throw new ArgumentException("Use the exact phase revision and a new command identity.");ProgramWorkflowRules.Validate(command.Definition);ProgramWorkflowRules.Text(command.Reason,2000,"Phase explanation");
        return await history.Mutate(actor,command.RequestId,new{operation="save-test-phase",program,phase,command},async(id,name)=>{
            _=await Program(program,ct);TestPhaseRecord row;TestPhaseSnapshot? prior=null;if(phase is {} key){row=await db.TestPhases.SingleOrDefaultAsync(p=>p.Id==key&&p.ProgramId==program,ct)??throw new OrganizationNotFound();prior=await Phase(row,ct);if(row.Revision!=command.ExpectedRevision)throw new OrganizationConflict("This phase changed. Keep your draft and reload its current revision.");}else{row=new(){Id=Guid.NewGuid(),ProgramId=program};db.TestPhases.Add(row);}
            foreach(var requirement in command.Definition.Requirements){var card=await Card(requirement.OpportunityId,requirement.PublicationRevision,ct);if(card.ProgramId!=program||Hash(card)!=requirement.PublicationSha256||requirement.RequiredPilots<card.Policy.RequiredDistinctPilots||requirement.RequirementKey is {} requirementKey&&!Requirements(card).Any(q=>q.Key==requirementKey))throw new ArgumentException("Use this program's exact published card/requirement and at least its published independent-pilot count.");}
            var member=await Active(actor,ct,true);var next=new TestPhaseSnapshot(row.Id,program,row.Revision+1,(prior?.DefinitionRevision??0)+1,command.Definition,prior?.Held??false,false,0,0,id,name,member.Role,OrganizationTime.UtcNow,"definition-saved",command.Reason.Trim(),null);var all=(await Phases(program,ct)).Where(p=>p.Id!=row.Id).Append(next);ProgramWorkflowRules.Graph(all);
            var links=await db.PhaseCards.Where(p=>p.PhaseId==row.Id).ToArrayAsync(ct);var wanted=command.Definition.Requirements.Select(r=>r.OpportunityId).ToHashSet();db.PhaseCards.RemoveRange(links.Where(l=>!wanted.Contains(l.OpportunityId)));foreach(var opportunity in wanted){var link=links.SingleOrDefault(l=>l.OpportunityId==opportunity);if(link is null){link=new(){PhaseId=row.Id,OpportunityId=opportunity};db.PhaseCards.Add(link);}link.PublicationRevision=command.Definition.Requirements.First(r=>r.OpportunityId==opportunity).PublicationRevision;}
            await SavePhaseState(row,next,id,name,ct);await Reconcile(program,ct);return await Phase(await db.TestPhases.AsNoTracking().SingleAsync(p=>p.Id==row.Id,ct),ct);
        },ct,async()=>{await Lock(ct);await Active(actor,ct,true);});
    }
}
