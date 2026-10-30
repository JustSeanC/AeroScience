using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

// Only authoritative domain transitions call Add, inside their existing database transaction.
// No endpoint accepts caller-authored organization events, notes, values or locations.
public sealed class OrganizationEvents(OrganizationDatabase db,OrganizationMembership members)
{
    public static void Add(OrganizationDatabase db,Guid organization,OrganizationEventType type,string authority,Guid source,
        int? revision,Guid actor,string idempotency,OrganizationEventVisibility visibility,string meaning,
        Guid? subject=null,Guid? campaign=null,Guid? card=null,string? requirement=null,DateTimeOffset? at=null)
    {
        if(organization==Guid.Empty||source==Guid.Empty||actor==Guid.Empty||idempotency.Length>160||authority.Length>80||meaning.Length>300||requirement?.Length>120)throw new InvalidOperationException("Unsupported bounded organization event metadata.");
        var id=new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("AeroScience/organization-event/v1\0"+organization+"\0"+idempotency)).AsSpan(0,16));
        if(db.Events.Local.Any(e=>e.Id==id))return;
        db.Events.Add(new(){Id=id,OrganizationId=organization,Type=type,Authority=authority,SourceId=source,SourceRevision=revision,
            ActorId=actor,SubjectId=subject,CampaignId=campaign,CardId=card,RequirementKey=requirement,IdempotencyKey=idempotency,
            TimestampUtc=at??OrganizationTime.UtcNow,Visibility=visibility,Meaning=meaning});
    }
    public async Task<OrganizationEventPage> Page(long after,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(after<0)throw new ArgumentException("Choose a valid event cursor.");
        var member=await members.Current(actor,ct);if(member?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        if(actor.FindFirstValue("AspNet.Identity.SecurityStamp")!=await db.Users.Where(u=>u.Id==member.UserId).Select(u=>u.SecurityStamp).SingleAsync(ct))throw new OrganizationUnauthorized();
        var manager=member.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager;
        var query=db.Events.AsNoTracking().Where(e=>e.Sequence>after&&(e.Visibility==OrganizationEventVisibility.ActiveMembers||e.ActorId==member.UserId||e.SubjectId==member.UserId||e.Visibility==OrganizationEventVisibility.AuthorizedReviewers&&(manager||member.Role==OrganizationRoles.Reviewer&&e.CampaignId!=null&&db.ReviewGrants.Any(g=>g.CampaignId==e.CampaignId&&g.ReviewerId==member.UserId&&g.Allowed))));
        var rows=await query.OrderBy(e=>e.Sequence).Take(51).ToArrayAsync(ct);
        return new(1,rows.Take(50).Select(e=>new OrganizationEventView(e.Id,1,e.OrganizationId,e.Type,e.Authority,e.SourceId,e.SourceRevision,manager||e.ActorId==member.UserId||e.SubjectId==member.UserId?e.ActorId:Guid.Empty,e.TimestampUtc,e.Visibility,e.Meaning,e.CardId,e.RequirementKey)).ToArray(),rows.Length>50?rows[49].Sequence:null);
    }
}
