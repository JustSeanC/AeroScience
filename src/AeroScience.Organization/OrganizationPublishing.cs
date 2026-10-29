using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class OrganizationPublishing(OrganizationDatabase db)
{
    internal async Task RequireManager(ClaimsPrincipal user,bool administrator,CancellationToken ct)
    {
        var id=Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==id,ct);
        if(member?.State!=OrganizationMembershipState.Active||!(administrator?member.Role==OrganizationRoles.Administrator:member.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager))throw new OrganizationForbidden();
    }
    public async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    public async Task<ResearchPublishingPolicy> Policy(CancellationToken ct)
    {
        var row=await db.PublishingPolicies.AsNoTracking().SingleOrDefaultAsync(ct);
        return new(1,row?.Revision??0,(ResearchPublishingMode)(row?.Mode??0));
    }
    internal async Task<(int Revision,string Hash,Guid Author,string Name)> Target(string kind,Guid id,CancellationToken ct)
    {
        int revision;
        if(kind=="campaign")revision=(await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new OrganizationNotFound()).Revision;
        else if(kind=="opportunity")revision=(await db.Opportunities.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new OrganizationNotFound()).Revision;
        else if(kind=="research-template")revision=(await db.ResearchTemplates.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id&&!x.Proposal,ct)??throw new OrganizationNotFound()).Revision;
        else throw new ArgumentException("Choose a campaign, opportunity or research-template draft.");
        var r=await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x=>x.ResourceId==id&&x.Revision==revision&&x.Kind==kind,ct)??throw new OrganizationNotFound();
        if(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.SnapshotJson)))!=r.Sha256)throw new InvalidOperationException("Original draft integrity is unavailable.");
        using var json=JsonDocument.Parse(r.SnapshotJson);var value=json.RootElement;
        if(kind=="research-template")
        {var t=JsonSerializer.Deserialize<TemplateDocument>(r.SnapshotJson,WireJson.Options)!;if(t.Status==TemplateProposalStatus.Published)throw new OrganizationConflict("Create a new draft before publishing again.");return(revision,r.Sha256,t.AuthorId,t.Template.Name);}
        if(value.GetProperty("status").GetString()!="draft")throw new OrganizationConflict("Review a saved draft, not an already published revision.");
        return(revision,r.Sha256,value.GetProperty("authorId").GetGuid(),value.GetProperty("name").GetString()!);
    }
    internal async Task<bool> Current(PublicationReviewView review,CancellationToken ct)
    {
        try{var t=await Target(review.Kind,review.ResourceId,ct);return t.Revision==review.DraftRevision&&t.Hash==review.DraftSha256&&(await Policy(ct)).Revision==review.PolicyRevision;}
        catch(OrganizationConflict){return false;}catch(OrganizationNotFound){return false;}
    }
    internal async Task<PublicationAuthorship> Require(string kind,Guid id,OrganizationPublishRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        await RequireManager(actor,false,ct);var policy=await Policy(ct);var target=await Target(kind,id,ct);
        if(target.Revision!=request.ExpectedRevision||request.DraftSha256 is not null&&target.Hash!=request.DraftSha256)throw new OrganizationConflict("The reviewed draft changed. Reload its exact current revision and hash.");
        Guid? approver=null;
        if(policy.Mode==ResearchPublishingMode.Legacy&&kind!="opportunity")await RequireManager(actor,true,ct);
        if(policy.Mode==ResearchPublishingMode.AdministratorApproval)
        {
            if(request.ApprovalId is not {} approval||request.DraftSha256!=target.Hash)throw new OrganizationConflict("This organization requires independent administrator approval of the exact draft revision and hash.");
            var record=await db.PublicationReviews.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==approval,ct)??throw new OrganizationNotFound();
            var review=JsonSerializer.Deserialize<PublicationReviewView>(record.SnapshotJson,WireJson.Options)!;
            if(review.Kind!=kind||review.ResourceId!=id||review.Status!=PublicationReviewStatus.Approved||review.ApproverId is not {} a||a==target.Author||!await Current(review,ct))throw new OrganizationConflict("Approval is stale, belongs to another draft, or is not independent. Request a new publication review.");
            var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==a,ct);
            if(member?.State!=OrganizationMembershipState.Active||member.Role!=OrganizationRoles.Administrator)throw new OrganizationForbidden();
            approver=a;
        }
        return new(target.Author,approver,Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!),policy.Revision,policy.Mode,target.Revision,target.Hash,OrganizationTime.UtcNow);
    }
    internal void Record(string kind,Guid id,int revision,PublicationAuthorship authorization)
    {
        // Separate indexed authorization record: original author, independent approver and publisher.
        db.PublicationReviews.Add(new(){Id=Guid.NewGuid(),ResourceId=id,DraftRevision=revision,Revision=1,Kind="publication:"+kind,
            Status="Published",CreatedUtc=authorization.PublishedUtc,SnapshotJson=JsonSerializer.Serialize(authorization,WireJson.Options)});
    }
    internal static async Task<OrganizationResearchDefinition> Resolve(OrganizationDatabase db,OrganizationTemplateReference reference,CancellationToken ct)
    {
        if(!reference.Id.StartsWith("published:",StringComparison.Ordinal))return OrganizationTemplates.Resolve(reference);
        if(!Guid.TryParseExact(reference.Id[10..],"D",out var id)||reference.Version<1)throw new ArgumentException("Invalid published template identity.");
        var record=await db.Revisions.AsNoTracking().SingleOrDefaultAsync(x=>x.ResourceId==id&&x.Revision==reference.Version&&x.Kind=="research-template",ct)??throw new OrganizationNotFound();
        if(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(record.SnapshotJson)))!=record.Sha256)throw new InvalidOperationException("Published template integrity is unavailable.");
        var t=JsonSerializer.Deserialize<TemplateDocument>(record.SnapshotJson,WireJson.Options)!;
        if(t.Status!=TemplateProposalStatus.Published)throw new ArgumentException("Only an immutable published template can be used by a campaign.");
        var validated=AeroScience.Missions.PortableResearchTemplates.Read(JsonSerializer.Serialize(t.Template,WireJson.Options));
        return validated.Definition;
    }
}
