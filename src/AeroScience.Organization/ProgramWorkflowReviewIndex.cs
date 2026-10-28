using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed partial class ProgramWorkflow
{
    internal bool Rebuilding {get;set;}
    public async Task IndexReview(Guid root,CancellationToken ct,bool force=false)
    {
        var source=await db.Reviews.AsNoTracking().SingleAsync(r=>r.Id==root,ct);var row=await db.WorkflowReviewIndexes.FindAsync([root],ct);if(row?.Version>1)throw new InvalidOperationException("A newer workflow review interpretation cannot be downgraded.");if(!force&&row is {Version:1}&&row.ReviewRevision==source.Revision&&row.HeadSha256==source.HeadSha256)return;
        if(row is null){row=new(){RootId=root};db.WorkflowReviewIndexes.Add(row);}row.Version=1;row.ReviewRevision=source.Revision;row.HeadSha256=source.HeadSha256;row.Valid=false;var prior=await db.WorkflowRequirementApprovals.Where(a=>a.RootId==root).ToArrayAsync(ct);var next=new Dictionary<(Guid,string),WorkflowRequirementApproval>();
        try{
            var marker=await db.CampaignProjections.AsNoTracking().SingleAsync(p=>p.RootId==root,ct);if(!marker.Valid||marker.ReviewRevision!=source.Revision||marker.HeadSha256!=source.HeadSha256)throw new InvalidOperationException("Original review projection is unavailable.");var original=await OrganizationReviews.ReadDocument(db,root,ct);
            var cards=await Live().Where(c=>c.RootId==root).Select(c=>c.CardId).ToArrayAsync(ct);var approved=await db.ReviewRequirements.AsNoTracking().Where(q=>q.RootId==root&&cards.Contains(q.CardId)&&q.State==ResearchReviewState.Approved).ToArrayAsync(ct);
            foreach(var q in approved){try{var finding=await CampaignProgressProjection.ReadApprovedFinding(db,source,q,ct);var documents=await OrganizationReviews.Selected(db,source,finding.Finding.Evidence,q.CardId,ct);_=SubmissionReviewEvidence.DescribeApproved(original,documents,finding.Definition,finding.Finding,ct);next[(q.CardId,q.Key)]=new(){RootId=root,CardId=q.CardId,RequirementKey=q.Key,FindingId=finding.Finding.EventId,FindingSha256=PreflightReadiness.Hash(finding.Finding)};}catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException){/* This exact requirement receives no credit; other independent findings remain separate. */}}
            row.Valid=true;
        }catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or System.Text.Json.JsonException){next.Clear();}
        foreach(var p in prior){if(next.Remove((p.CardId,p.RequirementKey),out var n))db.Entry(p).CurrentValues.SetValues(n);else db.WorkflowRequirementApprovals.Remove(p);}db.WorkflowRequirementApprovals.AddRange(next.Values);await db.SaveChangesAsync(ct);
    }
    async Task BackfillReviews(CancellationToken ct)
    {
        while(true){var roots=await db.Reviews.AsNoTracking().Where(r=>!db.WorkflowReviewIndexes.Any(i=>i.RootId==r.Id&&i.Version==1&&i.ReviewRevision==r.Revision&&i.HeadSha256==r.HeadSha256)).OrderBy(r=>r.Id).Take(5).Select(r=>r.Id).ToArrayAsync(ct);if(roots.Length==0)return;foreach(var root in roots){await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);await IndexReview(root,ct);await tx.CommitAsync(ct);db.ChangeTracker.Clear();}}
    }
}
