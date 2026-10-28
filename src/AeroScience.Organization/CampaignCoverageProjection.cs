using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed class CampaignCoverageProjection(OrganizationDatabase db)
{
    public const int Version=1;
    public async Task Backfill(CancellationToken ct)
    {
        while(true){var ids=await db.Reviews.AsNoTracking().Where(r=>!db.CoverageIndexes.Any(i=>i.RootId==r.Id&&i.Version==Version&&i.ReviewRevision==r.Revision&&i.HeadSha256==r.HeadSha256)).OrderBy(r=>r.Id).Select(r=>r.Id).Take(5).ToArrayAsync(ct);if(ids.Length==0)return;
            foreach(var id in ids){await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);await Sync(id,ct);await tx.CommitAsync(ct);db.ChangeTracker.Clear();}}
    }
    public async Task Sync(Guid root,CancellationToken ct,bool force=false)
    {
        var source=await db.Reviews.AsNoTracking().SingleAsync(r=>r.Id==root,ct);var marker=await db.CoverageIndexes.FindAsync([root],ct);if(marker?.Version>Version)throw new InvalidOperationException("A newer coverage index cannot be downgraded.");if(!force&&marker?.Version==Version&&marker.ReviewRevision==source.Revision&&marker.HeadSha256==source.HeadSha256)return;
        if(marker is null){marker=new(){RootId=root};db.CoverageIndexes.Add(marker);}var priorSignature=marker.ApprovalSignature;var wasValid=marker.Valid;marker.Version=Version;marker.ReviewRevision=source.Revision;marker.HeadSha256=source.HeadSha256;marker.Valid=false;
        var p19=await db.CampaignProjections.AsNoTracking().SingleOrDefaultAsync(p=>p.RootId==root,ct);var cards=await db.CampaignContributions.AsNoTracking().Where(c=>c.RootId==root).OrderBy(c=>c.CardId).ToArrayAsync(ct);var signature=PreflightReadiness.Hash(cards.Select(c=>new{c.CardId,c.Valid,c.State,c.ApprovalSignature}));marker.ApprovalSignature=signature;
        var usable=p19 is {Valid:true,Version:1}&&p19.ReviewRevision==source.Revision&&p19.HeadSha256==source.HeadSha256&&cards.All(c=>c.Valid&&c.ReviewRevision==source.Revision&&c.SourceHeadSha256==source.HeadSha256);
        if(usable&&!force&&wasValid&&priorSignature==signature){marker.Valid=true;await db.SaveChangesAsync(ct);return;}
        var existing=await db.CoverageObservations.Where(o=>o.RootId==root).ToArrayAsync(ct);var next=new Dictionary<(Guid,string,string),CampaignCoverageObservation>();
        if(usable)try{
            var original=await OrganizationReviews.ReadDocument(db,root,ct);
            foreach(var card in cards.Where(c=>c.State==ResearchReviewState.Approved&&c.SummaryJson!="")){
                var summary=JsonSerializer.Deserialize<ApprovedResearchSummary>(card.SummaryJson,WireJson.Options)??throw new InvalidOperationException("Approved card summary unavailable.");var requirements=await db.ReviewRequirements.AsNoTracking().Where(q=>q.RootId==root&&q.CardId==card.CardId).ToArrayAsync(ct);
                foreach(var q in requirements){ct.ThrowIfCancellationRequested();var approved=await CampaignProgressProjection.ReadApprovedFinding(db,source,q,ct);var documents=await OrganizationReviews.Selected(db,source,approved.Finding.Evidence,card.CardId,ct);
                    SubmissionReviewEvidence.DescribeApproved(original,documents,approved.Definition,approved.Finding,ct,s=>{
                        var key=(card.CardId,q.Key,s.IdentitySha256);if(next.TryGetValue(key,out var duplicate)){if(duplicate.ContentSha256!=s.ContentSha256)throw new InvalidOperationException("Conflicting approved original identities cannot establish coverage.");return;}
                        var compatibility=PreflightReadiness.Hash(new{version=Version,card.OpportunityId,card.PublicationRevision,card.PublicationSha256,s.Variable,s.Instrument,s.Unit,s.Provenance,s.Category,s.SourceSemanticsSha256,altitudeReference="feet MSL; no AGL/FL conversion"});
                        next.Add(key,new(){RootId=root,CardId=card.CardId,RequirementKey=q.Key,IdentitySha256=s.IdentitySha256,ContentSha256=s.ContentSha256,ReferenceId=PreflightReadiness.Hash(new{root,card.CardId,q.Key,s.IdentitySha256}),CampaignId=card.CampaignId,OpportunityId=card.OpportunityId,PublicationRevision=card.PublicationRevision,PublicationSha256=card.PublicationSha256,CompatibilityId=compatibility,FlightId=s.FlightId,FlightReference="Flight "+PreflightReadiness.Hash(new{original.Manifest.OrganizationId,s.FlightId})[..12],PilotId=card.PilotId,Contributor=summary.Contributor,Variable=s.Variable,Name=s.Name,Instrument=s.Instrument,Unit=s.Unit,Provenance=s.Provenance,Category=s.Category,SourceSemanticsSha256=s.SourceSemanticsSha256,TimestampUtc=s.TimestampUtc,Date=DateOnly.FromDateTime(s.TimestampUtc.UtcDateTime),AltitudeFeetMsl=s.AltitudeFeetMsl,Latitude=s.Latitude,Longitude=s.Longitude,OriginalReferenceJson=JsonSerializer.Serialize(s.Original,WireJson.Options)});
                    });
                }
            }marker.Valid=true;
        }catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or JsonException){next.Clear();marker.Valid=false;}
        foreach(var old in existing){var key=(old.CardId,old.RequirementKey,old.IdentitySha256);if(next.Remove(key,out var value))db.Entry(old).CurrentValues.SetValues(value);else db.CoverageObservations.Remove(old);}db.CoverageObservations.AddRange(next.Values);await db.SaveChangesAsync(ct);
    }
}
