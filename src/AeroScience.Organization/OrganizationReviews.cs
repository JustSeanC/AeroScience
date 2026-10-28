using System.Security.Claims;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed class OrganizationReviews(OrganizationDatabase db,OrganizationMembership members,OrganizationManagement audit,OrganizationDevices devices,CampaignProgressProjection progress)
{
    const string Genesis="0000000000000000000000000000000000000000000000000000000000000000";
    static T Read<T>(string json)=>JsonSerializer.Deserialize<T>(json,WireJson.Options)??throw new InvalidOperationException("Review metadata unavailable.");
    async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    async Task<MembershipRecord> Active(ClaimsPrincipal actor,CancellationToken ct)
    {
        var m=await members.Current(actor,ct);if(m?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        var stamp=actor.FindFirstValue("AspNet.Identity.SecurityStamp");var current=await db.Users.AsNoTracking().Where(u=>u.Id==m.UserId).Select(u=>u.SecurityStamp).SingleOrDefaultAsync(ct);
        if(stamp is null||stamp!=current)throw new OrganizationUnauthorized();return m;
    }
    async Task<MembershipRecord> Reviewer(ClaimsPrincipal actor,Guid? campaign,CancellationToken ct)
    {
        var m=await Active(actor,ct);if(m.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager)return m;
        if(m.Role!=OrganizationRoles.Reviewer||campaign is {} c&&!await db.ReviewGrants.AsNoTracking().AnyAsync(g=>g.CampaignId==c&&g.ReviewerId==m.UserId&&g.Allowed,ct))throw new OrganizationForbidden();return m;
    }
    async Task<ResearchReviewCaseRecord> Case(Guid root,CancellationToken ct)=>await db.Reviews.AsNoTracking().SingleOrDefaultAsync(r=>r.Id==root,ct)??throw new OrganizationNotFound();
    Task<SubmissionDocument> Document(Guid receipt,CancellationToken ct)=>ReadDocument(db,receipt,ct);
    internal static async Task<SubmissionDocument> ReadDocument(OrganizationDatabase db,Guid receipt,CancellationToken ct)
    {
        var row=await db.Submissions.AsNoTracking().SingleOrDefaultAsync(s=>s.ReceiptId==receipt,ct)??throw new OrganizationNotFound();var d=Read<SubmissionDocument>(row.PayloadJson);
        if(PreflightReadiness.Hash(d)!=row.PayloadSha256||PreflightReadiness.Hash(d.Manifest)!=row.ManifestSha256)throw new InvalidOperationException("Received evidence failed its retained integrity check.");return d;
    }
    static ReviewRequirementDefinition Definition(ResearchReviewRequirementRecord r)=>Read<ReviewRequirementDefinition>(r.DefinitionJson);
    static ReviewRequirementView Requirement(ResearchReviewRequirementRecord r)=>new(Definition(r),r.State,r.FindingJson is null?null:Read<ReviewFinding>(r.FindingJson));
    static ResearchReviewRequest Request(ResearchReviewRequestRecord r)=>new(r.Id,Read<ReviewTarget>(r.TargetJson),r.Note,r.AuthorName,r.CreatedUtc,r.State);
    ResearchReviewEvent Append(ResearchReviewCaseRecord r,ResearchReviewAction action,Guid actor,string name,string role,ReviewTarget? target,string note,ReviewEvidenceReference[] evidence,Guid[] resolved,Guid? respond,Guid receipt,string payload,DateTimeOffset? at=null)
    {
        var e=new ResearchReviewEvent(1,Guid.NewGuid(),r.Id,checked(r.Revision+1),action,actor,name,role,at??OrganizationTime.UtcNow,target,note,evidence,resolved,respond,receipt,payload,1,r.HeadSha256,"");e=e with{Sha256=PreflightReadiness.Hash(e)};
        db.ReviewEvents.Add(new(){RootId=r.Id,Id=e.Id,Sequence=e.Sequence,PayloadJson=JsonSerializer.Serialize(e,WireJson.Options),Sha256=e.Sha256});r.Revision=e.Sequence;r.HeadSha256=e.Sha256;r.UpdatedUtc=OrganizationTime.UtcNow;
        audit.Audit(actor,name,"research-review-"+action.ToString().ToLowerInvariant(),r.Id,r.Revision);return e;
    }
    public async Task Received(SubmissionDocument d,SubmissionReceipt receipt,CancellationToken ct)
    {
        var link=d.Manifest.ReviewLink;ResearchReviewCaseRecord r;ResearchReviewRequestRecord? request=null;
        if(link is null){
            var binding=d.Manifest.Cards[0];var original=await audit.ReadRevision(binding.OpportunityId,binding.IssuedRevision,ct);var card=Read<OrganizationOpportunitySnapshot>(original.SnapshotJson);var program=await db.Programs.AsNoTracking().SingleAsync(p=>p.Id==card.ProgramId,ct);var campaign=Read<OrganizationCampaignView>((await audit.ReadRevision(card.CampaignId,card.CampaignRevision,ct)).SnapshotJson);var pilot=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==d.Manifest.PilotId,ct);var requirements=SubmissionReviewEvidence.Requirements(d);
            r=new(){Id=receipt.Id,PilotId=d.Manifest.PilotId,SubmissionId=d.Manifest.Id,ProgramId=card.ProgramId,CampaignId=card.CampaignId,PilotName=pilot.UserName!,ProgramName=program.Name,CampaignName=campaign.Name,Title=d.Manifest.Name,CardNames=string.Join(" · ",requirements.Select(q=>q.CardName).Distinct()),RootPayloadSha256=receipt.PayloadSha256,ReceivedUtc=receipt.ReceivedUtc,UpdatedUtc=receipt.ReceivedUtc,HeadSha256=Genesis};
            r.SearchName=r.Title.ToUpperInvariant();r.SearchPilot=r.PilotName.ToUpperInvariant();r.SearchCampaign=r.CampaignName.ToUpperInvariant();r.SearchProgram=r.ProgramName.ToUpperInvariant();r.Completeness=d.Manifest.Policies.Any(p=>p.Mode==SubmissionPolicyMode.AllowIncompleteSubmission)?"Received under Allow Incomplete Submission. Inspect every selected requirement and omitted/missing evidence.":"Received under minimum-evidence policy. Receipt is not human approval or proof of a successful experiment.";db.Reviews.Add(r);
            foreach(var q in requirements)db.ReviewRequirements.Add(new(){RootId=r.Id,CardId=q.CardId,Key=q.Key,DefinitionJson=JsonSerializer.Serialize(q,WireJson.Options)});
        }else{
            if(link.Version!=1||link.RootReceiptId==Guid.Empty||link.RequestEventId==Guid.Empty)throw new ArgumentException("Choose the original review and actual evidence request.");
            r=await db.Reviews.SingleOrDefaultAsync(x=>x.Id==link.RootReceiptId,ct)??throw new OrganizationNotFound();
            if(r.PilotId!=d.Manifest.PilotId||r.CampaignId!=d.Manifest.CampaignId||r.RootPayloadSha256!=link.RootPayloadSha256)throw new OrganizationForbidden();
            request=await db.ReviewRequests.SingleOrDefaultAsync(x=>x.Id==link.RequestEventId&&x.RootId==r.Id,ct)??throw new OrganizationNotFound();if(request.State==ResearchReviewRequestState.Resolved)throw new OrganizationConflict("This evidence request is resolved. No supplemental bytes were committed; review the current conversation.");
            var root=await Document(r.Id,ct);foreach(var b in d.Manifest.Cards)if(!root.Manifest.Cards.Any(c=>c.CardId==b.CardId&&c.ClaimId==b.ClaimId&&c.DefinitionSha256==b.DefinitionSha256&&c.IssuedRevision==b.IssuedRevision&&c.IssuedSha256==b.IssuedSha256))throw new OrganizationForbidden();
            if(!d.Manifest.Cards.Any(c=>c.CardId==request.CardId))throw new OrganizationConflict("The supplemental package does not include the requested original card.");
            var affected=await db.ReviewRequirements.Where(q=>q.RootId==r.Id&&d.Manifest.Cards.Select(c=>c.CardId).Contains(q.CardId)).ToArrayAsync(ct);foreach(var q in affected)q.State=ResearchReviewState.AwaitingReview;request.State=ResearchReviewRequestState.EvidenceReceived;r.State=ResearchReviewState.UpdatedSubmission;
        }
        r.PackageCount++;var e=Append(r,link is null?ResearchReviewAction.Received:ResearchReviewAction.SupplementalReceived,d.Manifest.PilotId,r.PilotName,"pilot",request is null?null:Read<ReviewTarget>(request.TargetJson),link is null?"Exact selected package received. Awaiting human review.":"Explicitly approved supplemental package received. Earlier evidence and decisions are unchanged; affected requirements need review.",[],[],request?.Id,receipt.Id,receipt.PayloadSha256,receipt.ReceivedUtc);
        db.ReviewPackages.Add(new(){ReceiptId=receipt.Id,RootId=r.Id,PilotId=r.PilotId,SubmissionId=d.Manifest.Id,RequestEventId=request?.Id,ReceivedUtc=receipt.ReceivedUtc,PayloadSha256=receipt.PayloadSha256,FlightId=d.Manifest.FlightId});
    }
    public async Task Backfill(CancellationToken ct)
    {
        while(true){var ids=await db.Submissions.AsNoTracking().Where(s=>!db.ReviewPackages.Any(p=>p.ReceiptId==s.ReceiptId)).OrderBy(s=>s.ReceivedUtc).ThenBy(s=>s.ReceiptId).Select(s=>s.ReceiptId).Take(10).ToArrayAsync(ct);if(ids.Length==0)return;
            foreach(var id in ids){ct.ThrowIfCancellationRequested();await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);if(!await db.ReviewPackages.AnyAsync(p=>p.ReceiptId==id,ct)){var row=await db.Submissions.AsNoTracking().SingleAsync(s=>s.ReceiptId==id,ct);await Received(await Document(id,ct),Read<SubmissionReceipt>(row.ReceiptJson),ct);await db.SaveChangesAsync(ct);}await tx.CommitAsync(ct);db.ChangeTracker.Clear();}
        }
    }
    async Task<ResearchReviewSummary> Summary(ResearchReviewCaseRecord r,CancellationToken ct)
    {
        var org=await members.Public(ct);var requests=await db.ReviewRequests.CountAsync(q=>q.RootId==r.Id&&q.State!=ResearchReviewRequestState.Resolved,ct);
        return new(1,r.Id,r.SubmissionId,org.Id,org.Name,r.PilotId,r.PilotName,r.ProgramId,r.ProgramName,r.CampaignId,r.CampaignName,r.Title,r.CardNames,r.RootPayloadSha256,r.Revision,r.State,r.ReceivedUtc,r.UpdatedUtc,requests,r.PackageCount,r.Completeness);
    }
    async Task<ResearchReviewSnapshot> Snapshot(ResearchReviewCaseRecord r,int requestOffset,CancellationToken ct)
    {
        if(requestOffset<0)throw new ArgumentException("Choose a valid request page.");var req=await db.ReviewRequirements.AsNoTracking().Where(q=>q.RootId==r.Id).OrderBy(q=>q.CardId).ThenBy(q=>q.Key).ToArrayAsync(ct);
        var requests=await db.ReviewRequests.AsNoTracking().Where(q=>q.RootId==r.Id).OrderByDescending(q=>q.CreatedUtc).ThenBy(q=>q.Id).Skip(requestOffset).Take(21).ToArrayAsync(ct);
        return new(await Summary(r,ct),req.Select(Requirement).ToArray(),new(1,requests.Take(20).Select(Request).ToArray(),requests.Length>20?checked(requestOffset+20):null));
    }
    public async Task<OrganizationPage<ResearchReviewSummary>> Queue(int offset,string? search,ResearchReviewState? state,Guid? campaign,ClaimsPrincipal actor,CancellationToken ct)
    {
        var user=await Reviewer(actor,campaign,ct);if(offset<0||search?.Length>100||state is {} st&&!Enum.IsDefined(st))throw new ArgumentException("Choose a valid review page, state and search of up to 100 characters.");var rows=db.Reviews.AsNoTracking();if(user.Role==OrganizationRoles.Reviewer)rows=rows.Where(r=>db.ReviewGrants.Any(g=>g.ReviewerId==user.UserId&&g.CampaignId==r.CampaignId&&g.Allowed));if(campaign is {} c)rows=rows.Where(r=>r.CampaignId==c);if(state is {} wanted)rows=rows.Where(r=>r.State==wanted);if(!string.IsNullOrWhiteSpace(search)){var prefix=search.Trim().ToUpperInvariant();rows=rows.Where(r=>r.SearchName.StartsWith(prefix)||r.SearchPilot.StartsWith(prefix)||r.SearchCampaign.StartsWith(prefix)||r.SearchProgram.StartsWith(prefix));}
        var page=await rows.OrderByDescending(r=>r.UpdatedUtc).ThenBy(r=>r.Id).Skip(offset).Take(21).ToArrayAsync(ct);var result=new List<ResearchReviewSummary>();foreach(var r in page.Take(20))result.Add(await Summary(r,ct));return new(1,result.ToArray(),page.Length>20?checked(offset+20):null);
    }
    public async Task<ResearchReviewSnapshot> Workspace(Guid root,int requestOffset,ClaimsPrincipal actor,CancellationToken ct)
    {var r=await Case(root,ct);await Reviewer(actor,r.CampaignId,ct);return await Snapshot(r,requestOffset,ct);}
    public async Task<ResearchReviewSnapshot> PilotSnapshot(Guid root,int requestOffset,HttpContext c,CancellationToken ct)
    {var g=await devices.Authorize(c,ct,requireSubmission:true);var r=await Case(root,ct);if(r.PilotId!=g.UserId)throw new OrganizationNotFound();return await Snapshot(r,requestOffset,ct);}
    public async Task<ResearchReviewSnapshot> PilotPortal(Guid root,int requestOffset,ClaimsPrincipal actor,CancellationToken ct)
    {var user=await Active(actor,ct);var r=await Case(root,ct);if(r.PilotId!=user.UserId)throw new OrganizationNotFound();return await Snapshot(r,requestOffset,ct);}
    public async Task<OrganizationPage<ResearchReviewPackage>> Packages(Guid root,int offset,ClaimsPrincipal actor,CancellationToken ct)
    {var r=await Case(root,ct);await Reviewer(actor,r.CampaignId,ct);if(offset<0)throw new ArgumentException("Choose a valid received-package page.");var rows=await db.ReviewPackages.AsNoTracking().Where(p=>p.RootId==root).OrderByDescending(p=>p.ReceivedUtc).ThenBy(p=>p.ReceiptId).Skip(offset).Take(21).ToArrayAsync(ct);var result=new List<ResearchReviewPackage>();foreach(var p in rows.Take(20)){var s=await db.Submissions.AsNoTracking().Where(s=>s.ReceiptId==p.ReceiptId).Select(s=>new{s.Name}).SingleAsync(ct);result.Add(new(p.ReceiptId,p.SubmissionId,s.Name,p.PayloadSha256,p.ReceivedUtc,p.FlightId,p.ReceiptId!=root,p.RequestEventId));}return new(1,result.ToArray(),rows.Length>20?checked(offset+20):null);}
    internal static async Task<SubmissionDocument[]> Selected(OrganizationDatabase db,ResearchReviewCaseRecord r,ReviewEvidenceReference[] evidence,Guid card,CancellationToken ct)
    {
        if(evidence.Length is <1 or >64||evidence.Any(e=>e is null||e.ReceiptId==Guid.Empty||e.Key is not {Length:>=1 and <=160})||evidence.Select(e=>(e.ReceiptId,e.Key)).Distinct().Count()!=evidence.Length||evidence.Select(e=>e.ReceiptId).Distinct().Count()>SubmissionReviewEvidence.MaximumPackages)throw new ArgumentException("Select 1–8 received packages or up to 64 exact supporting item references.");
        var list=new List<SubmissionDocument>();foreach(var group in evidence.GroupBy(e=>e.ReceiptId)){if(!await db.ReviewPackages.AsNoTracking().AnyAsync(p=>p.RootId==r.Id&&p.ReceiptId==group.Key,ct))throw new OrganizationNotFound();var d=await ReadDocument(db,group.Key,ct);if(!d.Manifest.Cards.Any(c=>c.CardId==card))throw new ArgumentException("That package does not contain this original card.");if(group.Any(e=>e.Key=="*")){if(group.Count()!=1)throw new ArgumentException("Choose the complete scoped card evidence or individual references, not both.");list.Add(d);continue;}
            foreach(var item in group)if(!d.Evidence.Any(i=>i.Reference.CardId==card&&i.Reference.Key==item.Key))throw new OrganizationNotFound();var kept=d.Evidence.Where(i=>i.Reference.CardId!=card||i.Data.Definition is not null||group.Any(e=>e.Key==i.Reference.Key)).ToArray();list.Add(d with{Manifest=d.Manifest with{Items=kept.Select(i=>i.Reference).ToArray()},Evidence=kept});
        }return list.ToArray();
    }
    public async Task<ResearchReviewEvidencePage> Evidence(Guid root,ResearchReviewSelection selection,int offset,ClaimsPrincipal actor,CancellationToken ct)
    {
        var r=await Case(root,ct);await Reviewer(actor,r.CampaignId,ct);if(offset<0||selection.ReceiptIds is not {Length:>=1 and <=8}||selection.ReceiptIds.Distinct().Count()!=selection.ReceiptIds.Length)throw new ArgumentException("Choose a bounded received evidence selection.");var documents=await Selected(db,r,selection.ReceiptIds.Select(id=>new ReviewEvidenceReference(id,"*")).ToArray(),selection.CardId,ct);var assessment=SubmissionReviewEvidence.Assess(await Document(root,ct),documents,selection.CardId,selection.RequirementKey,ct);
        var all=documents.SelectMany(d=>d.Evidence.Select(i=>(d,i))).Where(x=>x.i.Reference.CardId==selection.CardId).ToArray();var page=all.Skip(offset).Take(20).Select(x=>{var v=BoundedItem(x.i,0);return new ReceivedReviewEvidenceItem(selection.ReceiptIds[Array.IndexOf(documents,x.d)],v.Item,v.Total,v.Next);}).ToArray();return new(assessment,page,all.Length>offset+20?checked(offset+20):null);
    }
    static (SubmissionEvidenceItem Item,int Total,int? Next) BoundedItem(SubmissionEvidenceItem i,int offset)
    {
        var d=i.Data;int total=checked((int)i.Reference.Records);int? next=null;int pagingTotal=total;
        if(d.Telemetry is {} t){total=t.Samples.Length;pagingTotal=Math.Max(total,Math.Max(t.Attempts.Length,t.Breaks.Length));d=d with{Telemetry=t with{Samples=t.Samples.Skip(offset).Take(100).ToArray(),Attempts=t.Attempts.Skip(offset).Take(100).ToArray(),Breaks=t.Breaks.Skip(offset).Take(100).ToArray()}};}
        else if(d.Observations is {} o){total=o.Length;d=d with{Observations=o.Skip(offset).Take(100).ToArray()};}
        else if(d.Documentation is {} notes){total=notes.Length;d=d with{Documentation=notes.Skip(offset).Take(100).ToArray()};}
        if(offset+100<Math.Max(total,pagingTotal))next=checked(offset+100);return(i with{Data=d},total,next);
    }
    public async Task<ResearchReviewItem> Item(Guid root,Guid receipt,string key,int offset,ClaimsPrincipal actor,CancellationToken ct)
    {
        var r=await Case(root,ct);await Reviewer(actor,r.CampaignId,ct);if(offset<0||key.Length>160)throw new ArgumentException("Choose a valid evidence item page.");if(!await db.ReviewPackages.AnyAsync(p=>p.RootId==root&&p.ReceiptId==receipt,ct))throw new OrganizationNotFound();var item=(await Document(receipt,ct)).Evidence.SingleOrDefault(i=>i.Reference.Key==key)??throw new OrganizationNotFound();var view=BoundedItem(item,offset);return new(item.Reference,view.Item.Data,offset,view.Total,view.Next);
    }
    public async Task<ResearchReviewHistory> History(Guid root,int after,ClaimsPrincipal actor,bool pilot,CancellationToken ct)
    {var r=await Case(root,ct);if(pilot){var m=await Active(actor,ct);if(m.UserId!=r.PilotId)throw new OrganizationNotFound();}else await Reviewer(actor,r.CampaignId,ct);return await Events(r,after,ct);}
    public async Task<ResearchReviewHistory> PilotHistory(Guid root,int after,HttpContext c,CancellationToken ct)
    {var grant=await devices.Authorize(c,ct,requireSubmission:true);var r=await Case(root,ct);if(r.PilotId!=grant.UserId)throw new OrganizationNotFound();return await Events(r,after,ct);}
    async Task<ResearchReviewHistory> Events(ResearchReviewCaseRecord r,int after,CancellationToken ct)
    {
        if(after<0)throw new ArgumentException("Choose a valid history cursor.");var rows=await db.ReviewEvents.AsNoTracking().Where(e=>e.RootId==r.Id&&e.Sequence>after&&e.Sequence<=r.Revision).OrderBy(e=>e.Sequence).Take(21).ToArrayAsync(ct);var result=new List<ResearchReviewEvent>();string? previous=after==0?Genesis:await db.ReviewEvents.AsNoTracking().Where(e=>e.RootId==r.Id&&e.Sequence==after).Select(e=>e.Sha256).SingleOrDefaultAsync(ct);if(previous is null)throw new OrganizationNotFound();
        foreach(var row in rows.Take(20)){var e=Read<ResearchReviewEvent>(row.PayloadJson);if(e.Sequence!=row.Sequence||e.RootReceiptId!=r.Id||e.PreviousSha256!=previous||e.Sha256!=row.Sha256||PreflightReadiness.Hash(e with{Sha256=""})!=e.Sha256)throw new InvalidOperationException("Review history failed its event-chain integrity check.");result.Add(e);previous=e.Sha256;}if(rows.Length<=20&&previous!=r.HeadSha256)throw new InvalidOperationException("Review history does not reach its retained revision/head.");return new(1,result.ToArray(),rows.Length>20?result.Last().Sequence:null,r.HeadSha256);
    }
    public async Task<ResearchReviewCommandResult> Command(Guid root,ResearchReviewCommand command,ClaimsPrincipal actor,CancellationToken ct,Func<Task>? nativeAuthorization=null)
    {
        if(command.Version!=1||command.PolicyVersion!=1||command.RequestId==Guid.Empty||command.ExpectedRevision<1||!Enum.IsDefined(command.Action)||command.Note is null||command.Note.Length>2000||command.Note.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t'))||command.ResolveRequests?.Length>20)throw new ArgumentException("Use the current review policy, saved revision and readable notes of up to 2,000 characters.");
        var current=await Case(root,ct);var pilot=command.Action==ResearchReviewAction.PilotResponse;if(pilot){var m=await Active(actor,ct);if(m.UserId!=current.PilotId||!command.ExplicitSharing)throw new OrganizationForbidden();}else await Reviewer(actor,current.CampaignId,ct);
        if(command.Action is not(ResearchReviewAction.Open or ResearchReviewAction.Comment or ResearchReviewAction.RequestEvidence or ResearchReviewAction.PilotResponse or ResearchReviewAction.Approve or ResearchReviewAction.Reject))throw new ArgumentException("Choose a supported review action.");
        var refs=command.Evidence??[];if(refs.Length>64)throw new ArgumentException("Choose a bounded set of supporting references.");ResearchReviewAssessment? assessment=null;
        if(command.Action is ResearchReviewAction.RequestEvidence or ResearchReviewAction.Approve or ResearchReviewAction.Reject){if(command.Target is null)throw new ArgumentException("Choose the original card requirement.");var requirement=await db.ReviewRequirements.AsNoTracking().SingleOrDefaultAsync(q=>q.RootId==root&&q.CardId==command.Target.CardId&&q.Key==command.Target.RequirementKey,ct)??throw new OrganizationNotFound();
            if(command.Action==ResearchReviewAction.Approve){var selected=await Selected(db,current,refs,command.Target.CardId,ct);assessment=SubmissionReviewEvidence.Assess(await Document(root,ct),selected,command.Target.CardId,command.Target.RequirementKey,ct);if(!command.ConfirmApproval||!assessment.CanApprove)throw new OrganizationConflict("Approval requires a deliberate confirmation and sufficient supported received evidence. Missing, failed or unsupported requirements need more evidence or rejection; P18 has no waiver.");}
        }
        if(refs.Length>0&&command.Action!=ResearchReviewAction.Approve){if(command.Target is null)throw new ArgumentException("Choose the exact requirement for supporting evidence references.");_=await Selected(db,current,refs,command.Target.CardId,ct);}
        if(command.Action is ResearchReviewAction.RequestEvidence or ResearchReviewAction.Reject or ResearchReviewAction.Comment or ResearchReviewAction.PilotResponse&&string.IsNullOrWhiteSpace(command.Note))throw new ArgumentException("Explain the evidence request, rejection or comment.");
        return await audit.Mutate(actor,command.RequestId,new{operation="research-review",root,command},async(id,name)=>{
            var row=await db.Reviews.SingleAsync(r=>r.Id==root,ct);if(row.Revision!=command.ExpectedRevision)throw new OrganizationConflict("Another reviewer or pilot updated this review. Your text is unchanged; refresh the conversation before sending a new decision.");var member=await Active(actor,ct);
            ResearchReviewRequirementRecord? requirement=null;if(command.Target is {} target){requirement=await db.ReviewRequirements.SingleOrDefaultAsync(q=>q.RootId==root&&q.CardId==target.CardId&&q.Key==target.RequirementKey,ct)??throw new OrganizationNotFound();await ValidateTarget(row,target,ct);}
            var resolved=command.ResolveRequests??[];if(resolved.Distinct().Count()!=resolved.Length)throw new ArgumentException("Select each evidence request once.");
            var pending=requirement is null?[]:await db.ReviewRequests.Where(q=>q.RootId==root&&q.CardId==requirement.CardId&&q.RequirementKey==requirement.Key&&q.State!=ResearchReviewRequestState.Resolved).ToArrayAsync(ct);
            if(command.Action==ResearchReviewAction.Approve&&pending.Any(p=>!resolved.Contains(p.Id)))throw new OrganizationConflict("Explicitly address every outstanding request for this requirement before approval.");
            if(resolved.Length>0&&(command.Action is not(ResearchReviewAction.Approve or ResearchReviewAction.Reject)||string.IsNullOrWhiteSpace(command.Note)||resolved.Any(id=>!pending.Any(p=>p.Id==id))))throw new ArgumentException("Explain how the selected outstanding requests are resolved by this decision.");
            ResearchReviewRequestRecord? response=null;ReviewTarget? replyTarget=null;
            if(pilot){
                if(command.RespondTo is not {} requested)throw new ArgumentException("Choose the actual request or reviewer comment being answered.");
                response=await db.ReviewRequests.SingleOrDefaultAsync(q=>q.RootId==root&&q.Id==requested,ct);
                if(response is not null){if(response.State==ResearchReviewRequestState.Resolved)throw new OrganizationConflict("This evidence request is already resolved. Read its latest history.");replyTarget=Read<ReviewTarget>(response.TargetJson);response.State=ResearchReviewRequestState.Responded;}
                else{var original=await db.ReviewEvents.AsNoTracking().SingleOrDefaultAsync(e=>e.RootId==root&&e.Id==requested,ct)??throw new OrganizationNotFound();var message=Read<ResearchReviewEvent>(original.PayloadJson);if(message.Action!=ResearchReviewAction.Comment)throw new OrganizationForbidden();replyTarget=message.Target;}
                if(command.Target is not null&&command.Target!=replyTarget)throw new OrganizationForbidden();
                if(refs.Length>0||resolved.Length>0)throw new ArgumentException("Replies share the reviewed text only. Add evidence through an independently approved P17 supplemental package.");
            }
            var last=Append(row,command.Action,id,name,pilot?"pilot":member.Role,command.Target??replyTarget,command.Note.Trim(),refs,resolved,command.RespondTo,row.Id,row.RootPayloadSha256);
            if(command.Action==ResearchReviewAction.RequestEvidence){db.ReviewRequests.Add(new(){Id=last.Id,RootId=root,CardId=command.Target!.CardId,RequirementKey=command.Target.RequirementKey,TargetJson=JsonSerializer.Serialize(command.Target,WireJson.Options),Note=last.Note,AuthorId=id,AuthorName=name,CreatedUtc=last.TimestampUtc});requirement!.State=ResearchReviewState.MoreEvidenceRequested;}
            if(command.Action is ResearchReviewAction.Approve or ResearchReviewAction.Reject){requirement!.State=command.Action==ResearchReviewAction.Approve?ResearchReviewState.Approved:ResearchReviewState.Rejected;requirement.FindingJson=JsonSerializer.Serialize(new ReviewFinding(requirement.State,last.Id,last.Sequence,id,name,last.TimestampUtc,last.Note,refs,1),WireJson.Options);foreach(var q in pending.Where(q=>resolved.Contains(q.Id)))q.State=ResearchReviewRequestState.Resolved;}
            await db.SaveChangesAsync(ct);if(command.Action is ResearchReviewAction.RequestEvidence or ResearchReviewAction.Approve or ResearchReviewAction.Reject)await Recompute(row,ct);await db.SaveChangesAsync(ct);await progress.Sync(root,ct);return new ResearchReviewCommandResult(last,await Snapshot(row,0,ct));
        },ct,async()=>{await Lock(ct);if(nativeAuthorization is not null)await nativeAuthorization();if(pilot){var m=await Active(actor,ct);if(m.UserId!=current.PilotId)throw new OrganizationForbidden();}else await Reviewer(actor,current.CampaignId,ct);});
    }
    async Task ValidateTarget(ResearchReviewCaseRecord r,ReviewTarget target,CancellationToken ct)
    {
        if(target.RequirementKey.Length>100||target.EvidenceKey?.Length>160)throw new ArgumentException("Choose an exact supported requirement/received item.");if(target.AttemptId is null&&target.EvidenceKey is null)return;
        // Exact received-package identity keeps historic targeting bounded without scanning or limiting retained history.
        if(target.ReceiptId is not {} receipt||!await db.ReviewPackages.AsNoTracking().AnyAsync(p=>p.RootId==r.Id&&p.ReceiptId==receipt,ct))throw new ArgumentException("Choose the received package containing this attempt or evidence item.");
        var definition=Definition(await db.ReviewRequirements.AsNoTracking().SingleAsync(q=>q.RootId==r.Id&&q.CardId==target.CardId&&q.Key==target.RequirementKey,ct));var d=await Document(receipt,ct);if(!d.Evidence.Any(i=>i.Reference.CardId==target.CardId&&(target.EvidenceKey is null||i.Reference.Key==target.EvidenceKey)&&(target.AttemptId is null||i.Data.Result?.RecordedAttemptId==target.AttemptId&&(definition.PointId is null||i.Data.Result?.QuantitativePoint?.PointId==definition.PointId)||i.Data.Telemetry?.Attempts.Any(a=>a.Id==target.AttemptId&&(definition.PointId is null||a.TestPointId==definition.PointId))==true)))throw new OrganizationNotFound();
    }

    async Task Recompute(ResearchReviewCaseRecord r,CancellationToken ct)
    {
        var states=await db.ReviewRequirements.Where(q=>q.RootId==r.Id).Select(q=>q.State).ToArrayAsync(ct);r.State=states.All(s=>s==ResearchReviewState.Approved)?ResearchReviewState.Approved:states.Any(s=>s==ResearchReviewState.MoreEvidenceRequested)?ResearchReviewState.MoreEvidenceRequested:states.Any(s=>s==ResearchReviewState.Rejected)?ResearchReviewState.Rejected:ResearchReviewState.AwaitingReview;
    }
    public async Task<ResearchReviewCommandResult> PilotCommand(Guid root,ResearchReviewCommand command,HttpContext c,CancellationToken ct)
    {
        if(command.Action!=ResearchReviewAction.PilotResponse)throw new OrganizationForbidden();var grant=await devices.Authorize(c,ct,requireSubmission:true);var user=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==grant.UserId,ct);
        var actor=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,user.Id.ToString()),new Claim(ClaimTypes.Name,user.UserName!),new Claim("AspNet.Identity.SecurityStamp",user.SecurityStamp!)],"verified-submission-device"));return await Command(root,command,actor,ct,async()=>{var current=await devices.Authorize(c,ct,requireSubmission:true);if(current.Id!=grant.Id||current.UserId!=user.Id)throw new OrganizationForbidden();});
    }
    public async Task<OrganizationPage<CampaignReviewPermissionView>> Permissions(Guid campaign,int offset,ClaimsPrincipal actor,CancellationToken ct)
    {await members.RequireManager(actor,false,ct);await Active(actor,ct);if(offset<0)throw new ArgumentException("Choose a valid permission page.");var rows=await db.ReviewGrants.AsNoTracking().Where(g=>g.CampaignId==campaign).OrderBy(g=>g.ReviewerId).Skip(offset).Take(21).Join(db.Users,g=>g.ReviewerId,u=>u.Id,(g,u)=>new CampaignReviewPermissionView(g.CampaignId,g.ReviewerId,u.UserName!,g.Allowed,g.Revision)).ToArrayAsync(ct);return new(1,rows.Take(20).ToArray(),rows.Length>20?checked(offset+20):null);}
    public Task<CampaignReviewPermissionView> Permission(Guid campaign,CampaignReviewPermission command,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(command.Version!=1||command.RequestId==Guid.Empty||command.ExpectedRevision<0||command.ReviewerId==Guid.Empty)throw new ArgumentException("Choose a supported campaign reviewer permission.");
        return audit.Mutate(actor,command.RequestId,new{operation="campaign-review-access",campaign,command},async(id,name)=>{
            if(!await db.Campaigns.AnyAsync(c=>c.Id==campaign,ct))throw new OrganizationNotFound();var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==command.ReviewerId,ct);if(command.Allowed&&(member?.State!=OrganizationMembershipState.Active||member.Role!=OrganizationRoles.Reviewer))throw new OrganizationConflict("Choose an active member with the Reviewer role. This action cannot promote a pilot or grant membership-management powers.");var row=await db.ReviewGrants.SingleOrDefaultAsync(g=>g.CampaignId==campaign&&g.ReviewerId==command.ReviewerId,ct);if((row?.Revision??0)!=command.ExpectedRevision)throw new OrganizationConflict("Reviewer access changed. Reload before saving.");if(row is null){row=new(){CampaignId=campaign,ReviewerId=command.ReviewerId};db.ReviewGrants.Add(row);}row.Revision++;row.Allowed=command.Allowed;row.UpdatedBy=id;row.UpdatedUtc=OrganizationTime.UtcNow;audit.Audit(id,name,command.Allowed?"campaign-review-access-granted":"campaign-review-access-revoked",campaign,row.Revision);return new CampaignReviewPermissionView(campaign,row.ReviewerId,(await db.Users.SingleAsync(u=>u.Id==row.ReviewerId,ct)).UserName!,row.Allowed,row.Revision);
        },ct,async()=>{await Lock(ct);await members.RequireManager(actor,false,ct);await Active(actor,ct);});
    }
}
