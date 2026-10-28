using System.Security.Claims;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed partial class ProgramWorkflow
{
    static TestPhaseView PhaseView(TestPhaseSnapshot p,ProgramPolicyView policy,WorkflowRuleProgress[] rules,IReadOnlySet<Guid> authorized)
    {
        var pre=p.Definition.Prerequisites.All(authorized.Contains);var gaps=rules.Where(r=>!r.Fulfilled).Select(r=>r.CardName+(r.Requirement.RequirementKey is {} key?" / "+key:"")+": "+r.Status).ToList();if(!pre)gaps.Add("A prerequisite phase has no current authorization.");if(policy.Policy.AdministrativeHold)gaps.Add("The program is on administrative hold.");if(p.Held)gaps.Add("This phase is held/reopened; a manager must release it explicitly.");var ready=rules.All(r=>r.Fulfilled)&&pre&&!policy.Policy.AdministrativeHold;var released=policy.Policy.PhaseGatesEnabled&&ready&&!p.Held&&p.Released&&p.AuthorizedDefinitionRevision==p.DefinitionRevision&&p.AuthorizedRulesRevision==policy.RulesRevision;
        var status=!policy.Policy.PhaseGatesEnabled?"Phase gates disabled — ordinary research remains available":policy.Policy.AdministrativeHold||p.Held?"On Hold":released?"Authorized":!pre?"Waiting for Prerequisites":!ready?"More Evidence or Authorization Needed":p.Definition.Advancement==PhaseAdvancement.ManagerRelease?"Ready — Manager Release Required":"Ready — Automatic Policy Pending";
        return new(p.Id,p.Revision,p.DefinitionRevision,p.Definition,p.Held,ready,released,pre,status,rules,gaps.ToArray(),p.Action,p.Reason,p.Actor,p.TimestampUtc,p.Proof?.Sha256);
    }
    async Task<WorkflowRuleProgress[]> PhaseRules(Guid program,TestPhaseSnapshot phase,ProgramPolicyView policy,CancellationToken ct)
    {var result=new List<WorkflowRuleProgress>();foreach(var r in phase.Definition.Requirements)result.Add(await Rule(program,r,policy,phase.Id,phase.DefinitionRevision,ct));return result.ToArray();}
    async Task<PhaseAuthorizationProof> Proof(Guid program,TestPhaseSnapshot phase,ProgramPolicyView policy,CancellationToken ct)
    {
        var result=new List<PhaseSupport>();foreach(var rule in phase.Definition.Requirements){
            var exceptions=ForRule(Effective(program,policy,phase.Id,phase.DefinitionRevision),rule);var waiver=rule.AllowWaivers?await exceptions.Where(e=>e.Kind==WorkflowExceptionKind.RequirementWaiver).OrderBy(e=>e.Id).Take(1).Select(e=>new WorkflowWaiverReference(e.Id,e.Revision,e.SnapshotSha256)).ToArrayAsync(ct):[];if(waiver.Length>0){result.Add(new(rule,[],waiver));continue;}
            var all=ForRule(rule);var originalRows=await all.GroupBy(c=>c.PilotId).OrderBy(g=>g.Key).Take(rule.RequiredPilots).Select(g=>g.OrderBy(c=>c.RootId).ThenBy(c=>c.CardId).Select(c=>new{c.RootId,c.CardId,c.ClaimId,c.PilotId,c.ReviewRevision,c.SourceHeadSha256}).First()).ToArrayAsync(ct);var originals=originalRows.Select(c=>new WorkflowEvidenceReference(c.RootId,c.CardId,c.ClaimId,c.PilotId,c.ReviewRevision,c.SourceHeadSha256)).ToArray();var partials=Array.Empty<WorkflowEvidenceReference>();
            if(originals.Length<rule.RequiredPilots&&rule.AllowExceptions&&rule.CountAuthorizedPartialContributors){var approvedPilots=all.Select(c=>c.PilotId);var partialRows=await exceptions.Where(e=>e.Kind==WorkflowExceptionKind.ApprovedWithExceptions&&e.CountAsPartialContributor&&e.PilotId!=null&&!approvedPilots.Contains(e.PilotId.Value)).GroupBy(e=>e.PilotId).OrderBy(g=>g.Key).Take(rule.RequiredPilots-originals.Length).Select(g=>g.OrderBy(e=>e.Id).Select(e=>new{e.RootReceiptId,e.CardId,e.ClaimId,e.PilotId,e.SourceReviewRevision,e.SourceReviewHeadSha256,e.Id,e.Revision}).First()).ToArrayAsync(ct);partials=partialRows.Select(e=>new WorkflowEvidenceReference(e.RootReceiptId!.Value,e.CardId!.Value,e.ClaimId!.Value,e.PilotId!.Value,e.SourceReviewRevision!.Value,e.SourceReviewHeadSha256!,e.Id,e.Revision)).ToArray();}
            if(originals.Length+partials.Length<rule.RequiredPilots)throw new OrganizationConflict("The original supporting evidence changed before release. Recheck phase readiness.");result.Add(new(rule,originals.Concat(partials).ToArray(),[]));
        }
        var proof=new PhaseAuthorizationProof(1,phase.DefinitionRevision,policy.RulesRevision,OrganizationTime.UtcNow,result.ToArray(),phase.Definition.Prerequisites,"");return proof with{Sha256=Hash(proof)};
    }
    public async Task Reconcile(Guid program,CancellationToken ct)
    {
        if(Rebuilding)return;
        var owns=db.Database.CurrentTransaction is null;await using var tx=owns?await db.Database.BeginTransactionAsync(ct):null;if(owns)await Lock(ct);
        var policy=await Policy(program,ct);var phases=await Phases(program,ct);var authorized=new HashSet<Guid>();
        foreach(var original in phases){var p=original;var rules=await PhaseRules(program,p,policy,ct);var v=PhaseView(p,policy,rules,authorized);
            async Task Transition(bool released,string action,string reason,PhaseAuthorizationProof? proof){p=p with{Revision=p.Revision+1,Released=released,AuthorizedDefinitionRevision=released?p.DefinitionRevision:0,AuthorizedRulesRevision=released?policy.RulesRevision:0,ActorId=Guid.Empty,Actor="Automatic workflow policy",ActorRole="system-policy",TimestampUtc=OrganizationTime.UtcNow,Action=action,Reason=reason,Proof=proof};var row=await db.TestPhases.SingleAsync(r=>r.Id==p.Id,ct);await SavePhaseState(row,p,Guid.Empty,"Automatic workflow policy",ct);v=PhaseView(p,policy,rules,authorized);}
            if(p.Released&&!v.Authorized)await Transition(false,"readiness-lost","Current evidence, prerequisites, hold or policy no longer supports the previous release. Prior authorization and evidence references remain historical.",p.Proof);
            if(policy.Policy.PhaseGatesEnabled&&v.Ready&&!p.Held&&!p.Released&&p.Definition.Advancement==PhaseAdvancement.AutomaticAfterApprovals)await Transition(true,"automatic-release","Advanced under the explicitly saved automatic phase policy after current organizational requirements and prerequisites were satisfied. No scientific finding was approved automatically.",await Proof(program,p,policy,ct));
            if(v.Authorized)authorized.Add(v.Id);
        }
        if(tx is not null)await tx.CommitAsync(ct);gateCache.Clear();
    }
    public async Task Backfill(CancellationToken ct)
    {
        await BackfillReviews(ct);Rebuilding=false;
        int offset=0;while(true){var ids=await db.WorkflowPolicies.AsNoTracking().Where(p=>p.PhaseGatesEnabled).OrderBy(p=>p.ProgramId).Skip(offset).Take(5).Select(p=>p.ProgramId).ToArrayAsync(ct);if(ids.Length==0)return;foreach(var id in ids){await Reconcile(id,ct);db.ChangeTracker.Clear();}offset+=ids.Length;}
    }
    public async Task<TestPhaseSnapshot> PhaseCommand(Guid program,Guid phase,PhaseStateCommand command,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(command.Version!=1||command.RequestId==Guid.Empty||command.ExpectedRevision<1||!Enum.IsDefined(command.Action)||command.Action==PhaseCommandAction.Release&&!command.Confirm)throw new ArgumentException("Use the current phase revision and deliberately confirm a release.");ProgramWorkflowRules.Text(command.Reason,2000,"Decision explanation");
        return await history.Mutate(actor,command.RequestId,new{operation="phase-decision",program,phase,command},async(id,name)=>{
            var row=await db.TestPhases.SingleOrDefaultAsync(p=>p.Id==phase&&p.ProgramId==program,ct)??throw new OrganizationNotFound();if(row.Revision!=command.ExpectedRevision)throw new OrganizationConflict("The phase changed. Your explanation remains here; reload before deciding.");var original=await Phase(row,ct);if(!original.Definition.Enabled)throw new OrganizationConflict("This phase is disabled. Enable its reviewed definition before issuing a decision.");var policy=await Policy(program,ct);var views=await Evaluate(program,policy,await Phases(program,ct),ct);var state=views.Single(p=>p.Id==phase);var release=command.Action==PhaseCommandAction.Release;if(release&&(!policy.Policy.PhaseGatesEnabled||!state.Ready))throw new OrganizationConflict("Release needs enabled phase policy, satisfied current requirements/prerequisites and no program hold.");var member=await Active(actor,ct,true);var next=original with{Revision=original.Revision+1,Held=!release,Released=release,AuthorizedDefinitionRevision=release?original.DefinitionRevision:0,AuthorizedRulesRevision=release?policy.RulesRevision:0,ActorId=id,Actor=name,ActorRole=member.Role,TimestampUtc=OrganizationTime.UtcNow,Action=command.Action.ToString().ToLowerInvariant(),Reason=command.Reason.Trim(),Proof=release?await Proof(program,original,policy,ct):original.Proof};await SavePhaseState(row,next,id,name,ct);await Reconcile(program,ct);return next;
        },ct,async()=>{await Lock(ct);await Active(actor,ct,true);});
    }
    readonly Dictionary<Guid,(ProgramPolicyView Policy,TestPhaseView[] Phases)> gateCache=[];
    public async Task<string?> ClaimGate(Guid opportunity,int publication,CancellationToken ct)
    {
        var program=await db.Opportunities.AsNoTracking().Where(o=>o.Id==opportunity).Join(db.Campaigns,o=>o.CampaignId,c=>c.Id,(o,c)=>c.ProgramId).SingleAsync(ct);
        if(!gateCache.TryGetValue(program,out var context)){var policy=await Policy(program,ct);context=(policy,policy.Policy.PhaseGatesEnabled?await Evaluate(program,policy,await Phases(program,ct),ct):[]);gateCache[program]=context;}
        if(context.Policy.Policy.AdministrativeHold)return "Program on administrative hold — new assignments/claims are paused. Existing claims and local recording remain preserved.";
        if(!context.Policy.Policy.PhaseGatesEnabled)return null;var phase=context.Phases.SingleOrDefault(p=>p.Definition.Requirements.Any(r=>r.OpportunityId==opportunity));if(phase is null)return null;
        if(phase.Definition.Requirements.Any(r=>r.OpportunityId==opportunity&&r.PublicationRevision!=publication))return "This card has a newer publication than its phase policy. A manager must explicitly review/update the phase association; old exceptions do not authorize new criteria.";
        if(phase.Held)return "This test phase is on hold/reopened. A manager must release it before new assignments/claims.";
        if(!phase.PrerequisitesSatisfied)return "A prerequisite test phase has not been authorized. Open Test-Program Progression to see the missing approvals or manager release.";
        return null;
    }
    public Task<CampaignWorkflowView> Campaign(Guid campaign,int offset,ClaimsPrincipal actor,CancellationToken ct,Guid? pilot=null)=>Snapshot(actor,pilot,async(_,_)=>{
        if(offset<0)throw new ArgumentException("Choose a valid administrative progress page.");var row=await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==campaign&&c.PublishedRevision!=null,ct)??throw new OrganizationNotFound();var policy=await Policy(row.ProgramId,ct);var cards=await db.Opportunities.AsNoTracking().Where(o=>o.CampaignId==campaign&&o.PublishedRevision!=null).OrderBy(o=>o.PublishedName).ThenBy(o=>o.Id).Skip(offset).Take(21).ToArrayAsync(ct);var values=new List<WorkflowRuleProgress>();foreach(var card in cards.Take(20)){var published=await Card(card.Id,card.PublishedRevision!.Value,ct);values.Add(await Rule(row.ProgramId,new(card.Id,published.Revision,Hash(published),RequiredPilots:published.Policy.RequiredDistinctPilots,AllowWaivers:policy.Policy.AllowCampaignWaivers,AllowExceptions:policy.Policy.AllowCampaignExceptions,CountAuthorizedPartialContributors:policy.Policy.CountAuthorizedPartialContributors),policy,null,null,ct));}return new CampaignWorkflowView(1,row.ProgramId,campaign,OrganizationTime.UtcNow,policy,new(1,values.ToArray(),cards.Length>20?checked(offset+20):null),"P19 scientific approval counts stay unchanged. Only explicit whole-card campaign dispositions affect whole-card administrative fulfillment; requirement/phase dispositions keep their exact separate purposes. A waiver adds no pilot, measurement or coverage.");
    },ct);
    public Task<OrganizationPage<TestPhaseView>> PhasePage(Guid program,int offset,ClaimsPrincipal actor,CancellationToken ct,Guid? pilot=null)=>Snapshot(actor,pilot,async(_,_)=>{
        if(offset<0)throw new ArgumentException("Choose a valid phase page.");_=await Program(program,ct);var policy=await Policy(program,ct);var active=await Evaluate(program,policy,await Phases(program,ct),ct);var rows=await db.TestPhases.AsNoTracking().Where(p=>p.ProgramId==program).OrderBy(p=>p.Sequence).ThenBy(p=>p.Id).Skip(offset).Take(21).ToArrayAsync(ct);var list=new List<TestPhaseView>();foreach(var row in rows.Take(20)){var p=active.SingleOrDefault(p=>p.Id==row.Id);if(p is null){var original=await Phase(row,ct);p=PhaseView(original,policy,await PhaseRules(program,original,policy,ct),new HashSet<Guid>()) with{Authorized=false,Status="Disabled phase — historical decisions preserved"};}list.Add(p);}return new OrganizationPage<TestPhaseView>(1,list.ToArray(),rows.Length>20?checked(offset+20):null);
    },ct);
    public Task<TestPhaseSnapshot> PhaseRevision(Guid phase,int revision,ClaimsPrincipal actor,CancellationToken ct)=>Snapshot(actor,null,async(_,manage)=>{
        if(!manage)throw new OrganizationForbidden();var retained=await db.TestPhases.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==phase,ct)??throw new OrganizationNotFound();var r=await history.ReadRevision(phase,revision,ct);if(r.Kind!="test-phase")throw new OrganizationNotFound();return Read<TestPhaseSnapshot>(r.SnapshotJson);
    },ct);
}
