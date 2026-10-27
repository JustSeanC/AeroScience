using System.Security.Claims;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class OpportunityRecord
{
    public Guid Id {get;set;}
    public Guid CampaignId {get;set;}
    public int Revision {get;set;}
    public int? PublishedRevision {get;set;}
    public string SnapshotJson {get;set;}="";
    public bool Suspended {get;set;}
    public int AvailabilityRevision {get;set;}=1;
    public DateTimeOffset CreatedUtc {get;set;}
}
public sealed class ClaimRecord
{
    public Guid Id {get;set;}
    public Guid OpportunityId {get;set;}
    public Guid UserId {get;set;}
    public int IssuedRevision {get;set;}
    public string IssuedSha256 {get;set;}="";
    public int Revision {get;set;}=1;
    public int Slot {get;set;}
    public OrganizationClaimState State {get;set;}
    public DateTimeOffset AssignedUtc {get;set;}
    public DateTimeOffset? AcceptedUtc {get;set;}
    public DateTimeOffset? EndedUtc {get;set;}
    public string? EndReason {get;set;}
}
public sealed class ClaimEventRecord
{
    public long Id {get;set;}
    public Guid ClaimId {get;set;}
    public int ClaimRevision {get;set;}
    public string Action {get;set;}="";
    public DateTimeOffset TimestampUtc {get;set;}
    public Guid ActorId {get;set;}
}

public sealed class OrganizationOpportunities(OrganizationDatabase db,OrganizationManagement history,OrganizationMembership members,OrganizationDevices devices)
{
    const int PageSize=10;
    static void Command(int version,Guid id){if(version!=1||id==Guid.Empty)throw new ArgumentException("Use a supported command and a unique request identity.");}
    static string Text(string? value,int max,string label,bool required=true)
    {
        var text=value?.Trim()??"";if(text.Length>max||required&&text.Length==0||text.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))throw new ArgumentException($"{label} needs {(required?"1":"0")}–{max} readable characters.");return text;
    }
    static void Policy(OrganizationClaimPolicy? p)
    {
        if(p is null||!Enum.IsDefined(p.Participation)||p.MaximumActiveClaims is <1 or >1000||p.RequiredDistinctPilots<1||p.RequiredDistinctPilots>p.MaximumActiveClaims||p.Participation==OrganizationParticipation.Exclusive&&(p.MaximumActiveClaims!=1||p.RequiredDistinctPilots!=1)||p.EligibleRoles is not {Length:>=1 and <=4}||p.EligibleRoles.Distinct().Count()!=p.EligibleRoles.Length||p.EligibleRoles.Any(r=>!OrganizationRoles.All.Contains(r)))throw new ArgumentException("Choose eligible roles and capacity (1–1,000 concurrent claims). Exclusive cards require one pilot/slot; distinct-pilot requirements cannot exceed capacity.");
        _=Text(p.ReviewPolicy,1000,"Future review policy",false);
    }
    static OrganizationOpportunitySnapshot Snapshot(OpportunityRecord r)=>JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(r.SnapshotJson,WireJson.Options)!;
    async Task Lock(CancellationToken ct)
    {
        // Same lock order as membership/revocation, across every server process. No recorder lock is involved.
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    }
    async Task<MembershipRecord> Active(ClaimsPrincipal actor,CancellationToken ct)
    {
        var m=await members.Current(actor,ct);if(m?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();return m;
    }
    internal async Task<ClaimsPrincipal> Actor(HttpContext c,bool desktop,CancellationToken ct)
    {
        if(!desktop)return c.User;
        var grant=await devices.Authorize(c,ct,requireClaims:true);var user=await db.Users.AsNoTracking().SingleAsync(u=>u.Id==grant.UserId,ct);
        return new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,user.Id.ToString()),new(ClaimTypes.Name,user.UserName!)],"OrganizationClaimDevice"));
    }
    async Task Manager(ClaimsPrincipal actor,CancellationToken ct)=>await members.RequireManager(actor,false,ct);
    async Task<OrganizationCampaignView> Campaign(Guid id,int revision,CancellationToken ct)
    {
        var live=await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==id,ct)??throw new OrganizationNotFound();
        if(live.PublishedRevision!=revision)throw new OrganizationConflict("The campaign publication changed. Review its current published revision.");
        var row=await history.ReadRevision(id,revision,ct);var campaign=JsonSerializer.Deserialize<OrganizationCampaignView>(row.SnapshotJson,WireJson.Options)!;
        if(row.Kind!="campaign"||campaign.Status!=OrganizationPublication.Published)throw new OrganizationConflict("Choose an authorized published campaign, not a draft.");return campaign;
    }
    async Task<(OrganizationOpportunitySnapshot Card,string Hash)> Published(OpportunityRecord row,CancellationToken ct)
    {
        if(row.PublishedRevision is not {} revision)throw new OrganizationNotFound();
        var record=await history.ReadRevision(row.Id,revision,ct);if(record.Kind!="opportunity")throw new InvalidOperationException("Published opportunity integrity is unavailable.");
        return(JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(record.SnapshotJson,WireJson.Options)!,record.Sha256);
    }
    public async Task<OrganizationOpportunitySnapshot> Save(Guid campaignId,Guid? id,OrganizationOpportunityRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);Policy(request.Policy);
        string name=Text(request.Name,120,"Card name"),description=Text(request.Description,2000,"Description",false),objective=Text(request.Objective,2000,"Objective"),instructions=Text(request.Instructions,2000,"Instructions",false);
        return await history.Mutate(actor,request.RequestId,new{operation="opportunity-save",campaignId,id,request},async(user,author)=>{
            await Lock(ct);await Manager(actor,ct);var campaign=await Campaign(campaignId,request.CampaignRevision,ct);
            var row=id is {} key?await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==key&&o.CampaignId==campaignId,ct)??throw new OrganizationNotFound():new OpportunityRecord{Id=Guid.NewGuid(),CampaignId=campaignId,CreatedUtc=OrganizationTime.UtcNow};
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("The card has a newer revision. Reload before saving your draft.");
            row.Revision++;var card=new OrganizationOpportunitySnapshot(1,row.Id,(await members.Public(ct)).Id,campaign.ProgramId,campaignId,campaign.Revision,row.Revision,OrganizationPublication.Draft,name,description,objective,instructions,request.Policy,campaign.Definition,row.CreatedUtc,OrganizationTime.UtcNow,user,author);
            row.SnapshotJson=JsonSerializer.Serialize(card,WireJson.Options);if(id is null)db.Opportunities.Add(row);
            history.Revision(row.Id,row.Revision,"opportunity",card,user);history.Audit(user,author,"opportunity-draft-saved",row.Id,row.Revision);return card;
        },ct);
    }
    public async Task<OrganizationOpportunitySnapshot> Publish(Guid id,OrganizationPublishRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);
        return await history.Mutate(actor,request.RequestId,new{operation="opportunity-publish",id,request},async(user,author)=>{
            await Lock(ct);await Manager(actor,ct);var row=await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==id,ct)??throw new OrganizationNotFound();var draft=Snapshot(row);
            if(row.Revision!=request.ExpectedRevision||draft.Status!=OrganizationPublication.Draft)throw new OrganizationConflict("Review the current saved draft before publication.");
            var campaign=await Campaign(row.CampaignId,draft.CampaignRevision,ct);
            if(JsonSerializer.Serialize(campaign.Definition,WireJson.Options)!=JsonSerializer.Serialize(draft.Definition,WireJson.Options))throw new OrganizationConflict("The saved definition differs from the authorized campaign publication.");
            if(await db.Claims.CountAsync(c=>c.OpportunityId==id&&c.State!=OrganizationClaimState.Released,ct)>draft.Policy.MaximumActiveClaims)throw new OrganizationConflict("Current reservations/claims exceed this capacity. Explicitly release them or retain enough slots.");
            row.Revision++;row.PublishedRevision=row.Revision;row.AvailabilityRevision++;
            var published=draft with{Revision=row.Revision,Status=OrganizationPublication.Published,UpdatedUtc=OrganizationTime.UtcNow,AuthorId=user,AuthorName=author};row.SnapshotJson=JsonSerializer.Serialize(published,WireJson.Options);
            history.Revision(id,row.Revision,"opportunity",published,user);history.Audit(user,author,"opportunity-published",id,row.Revision);return published;
        },ct);
    }
    public Task<object> Availability(Guid id,OrganizationOpportunityAvailability request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);return history.Mutate<object>(actor,request.RequestId,new{operation="opportunity-availability",id,request},async(user,author)=>{
            await Lock(ct);await Manager(actor,ct);var row=await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==id,ct)??throw new OrganizationNotFound();if(row.PublishedRevision is null||row.AvailabilityRevision!=request.ExpectedAvailabilityRevision)throw new OrganizationConflict("Reload this published card before changing availability.");
            row.Suspended=request.Suspended;row.AvailabilityRevision++;history.Audit(user,author,request.Suspended?"opportunity-withdrawn":"opportunity-reopened",id,row.AvailabilityRevision);return new{row.Suspended,row.AvailabilityRevision};
        },ct);
    }
    static bool Eligible(MembershipRecord member,OrganizationOpportunitySnapshot card)=>member.State==OrganizationMembershipState.Active&&card.Policy.EligibleRoles.Contains(member.Role);
    public async Task<OrganizationPage<OrganizationOpportunityView>> Catalog(Guid? campaignId,int offset,ClaimsPrincipal actor,bool manager,CancellationToken ct)
    {
        if(offset<0)throw new ArgumentException("Choose a valid card page.");var member=await Active(actor,ct);if(manager)await Manager(actor,ct);
        var query=db.Opportunities.AsNoTracking().AsQueryable();if(campaignId is {} campaign)query=query.Where(o=>o.CampaignId==campaign);if(!manager)query=query.Where(o=>o.PublishedRevision!=null);
        var rows=await query.OrderBy(o=>o.CreatedUtc).ThenBy(o=>o.Id).Skip(offset).Take(PageSize+1).ToArrayAsync(ct);var result=new List<OrganizationOpportunityView>();
        foreach(var row in rows.Take(PageSize)){
            var (card,hash)=manager?(Snapshot(row),""):await Published(row,ct);
            if(manager){var original=await history.ReadRevision(row.Id,row.Revision,ct);hash=original.Sha256;}
            var held=await db.Claims.CountAsync(c=>c.OpportunityId==row.Id&&c.State!=OrganizationClaimState.Released,ct);var publishedCard=row.PublishedRevision is not null?(await Published(row,ct)).Card:null;var effective=publishedCard??card;var eligible=Eligible(member,effective);
            result.Add(new(card,hash,row.PublishedRevision,row.Suspended,row.AvailabilityRevision,held,Math.Max(0,effective.Policy.MaximumActiveClaims-held),eligible,row.Suspended?"Unavailable / Suspended":row.PublishedRevision is null?"Draft — not claimable":!eligible?"Not eligible for this card":held>=effective.Policy.MaximumActiveClaims?"All slots reserved or claimed":!effective.Policy.AllowVolunteers?"Manager assignment only":"Available",manager?publishedCard:null));
        }
        return new(1,result.ToArray(),rows.Length>PageSize?checked(offset+PageSize):null);
    }
    public async Task<OrganizationClaimView> Change(OrganizationClaimCommand request,HttpContext c,bool desktop,bool manager,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);if(!Enum.IsDefined(request.Action))throw new ArgumentException("Choose a supported claim action.");
        if(manager?request.Action is not(OrganizationClaimAction.Assign or OrganizationClaimAction.ManagerRelease):request.Action is not(OrganizationClaimAction.Claim or OrganizationClaimAction.Accept or OrganizationClaimAction.Release))throw new OrganizationForbidden();
        var actor=await Actor(c,desktop,ct);
        return await history.Mutate(actor,request.RequestId,new{operation="claim-command",request},async(user,author)=>{
            await Lock(ct);if(desktop){var current=await Actor(c,true,ct);if(OrganizationMembership.Actor(current)!=user)throw new OrganizationForbidden();}
            var member=await Active(actor,ct);if(manager)await Manager(actor,ct);
            if((await members.Public(ct)).Id!=request.OrganizationId)throw new OrganizationForbidden();
            var opportunity=await db.Opportunities.SingleOrDefaultAsync(o=>o.Id==request.OpportunityId,ct)??throw new OrganizationNotFound();
            ClaimRecord row;
            if(request.Action is OrganizationClaimAction.Assign or OrganizationClaimAction.Claim){
                var (card,hash)=await Published(opportunity,ct);
                if(opportunity.Suspended||card.Revision!=request.PublishedRevision)throw new OrganizationConflict("This card is unavailable or its publication changed. Refresh and review the current requirements.");
                var pilot=manager?request.PilotId??throw new ArgumentException("Choose an eligible active member."):user;
                if(!manager&&request.PilotId is not null&&request.PilotId!=user)throw new OrganizationForbidden();
                var target=pilot==user?member:await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==pilot,ct)??throw new OrganizationNotFound();
                if(!Eligible(target,card)||!manager&&!card.Policy.AllowVolunteers)throw new OrganizationForbidden();
                if(await db.Claims.AnyAsync(r=>r.OpportunityId==opportunity.Id&&r.UserId==pilot&&r.State!=OrganizationClaimState.Released,ct))throw new OrganizationConflict("This pilot already has a reservation or claim. Open My Assignments instead.");
                var used=await db.Claims.Where(r=>r.OpportunityId==opportunity.Id&&r.State!=OrganizationClaimState.Released).Select(r=>r.Slot).ToArrayAsync(ct);
                if(used.Length>=card.Policy.MaximumActiveClaims)throw new OrganizationConflict("This card is no longer available. Its slots are reserved or claimed; refresh the list.");
                var slot=Enumerable.Range(1,card.Policy.MaximumActiveClaims).First(i=>!used.Contains(i));
                row=new(){Id=Guid.NewGuid(),OpportunityId=opportunity.Id,UserId=pilot,IssuedRevision=card.Revision,IssuedSha256=hash,Slot=slot,State=manager?OrganizationClaimState.Reserved:OrganizationClaimState.Claimed,AssignedUtc=OrganizationTime.UtcNow,AcceptedUtc=manager?null:OrganizationTime.UtcNow};db.Claims.Add(row);
            }else{
                row=await db.Claims.SingleOrDefaultAsync(r=>r.Id==request.ClaimId&&r.OpportunityId==opportunity.Id,ct)??throw new OrganizationNotFound();
                if(!manager&&row.UserId!=user)throw new OrganizationForbidden();
                if(row.Revision!=request.ExpectedClaimRevision||row.IssuedRevision!=request.PublishedRevision||row.State==OrganizationClaimState.Released)throw new OrganizationConflict("This assignment changed or was released. Reload it; historical commands cannot be replayed.");
                if(request.Action==OrganizationClaimAction.Accept){
                    if(opportunity.Suspended||row.State!=OrganizationClaimState.Reserved)throw new OrganizationConflict("This reservation is unavailable or already accepted.");
                    var issued=await history.ReadRevision(row.OpportunityId,row.IssuedRevision,ct);var card=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(issued.SnapshotJson,WireJson.Options)!;
                    if(issued.Sha256!=row.IssuedSha256||!Eligible(member,card))throw new OrganizationForbidden();row.State=OrganizationClaimState.Claimed;row.AcceptedUtc=OrganizationTime.UtcNow;
                }else{row.State=OrganizationClaimState.Released;row.EndedUtc=OrganizationTime.UtcNow;row.EndReason=manager?"manager-released":"pilot-released";}
                row.Revision++;
            }
            db.ClaimEvents.Add(new(){ClaimId=row.Id,ClaimRevision=row.Revision,ActorId=user,Action=request.Action.ToString(),TimestampUtc=OrganizationTime.UtcNow});history.Audit(user,author,"claim-"+request.Action.ToString().ToLowerInvariant(),row.Id,row.Revision);
            return await ClaimView(row,manager,ct);
        },ct);
    }
    async Task<OrganizationClaimView> ClaimView(ClaimRecord row,bool manager,CancellationToken ct)
    {
        var source=await history.ReadRevision(row.OpportunityId,row.IssuedRevision,ct);if(source.Kind!="opportunity"||source.Sha256!=row.IssuedSha256)throw new InvalidOperationException("Issued claim source integrity is unavailable.");
        var issued=JsonSerializer.Deserialize<OrganizationOpportunitySnapshot>(source.SnapshotJson,WireJson.Options)!;var live=await db.Opportunities.AsNoTracking().SingleAsync(o=>o.Id==row.OpportunityId,ct);
        var member=await db.Memberships.AsNoTracking().SingleAsync(m=>m.UserId==row.UserId,ct);var availability=live.Suspended?"Unavailable / publication withdrawn":member.State!=OrganizationMembershipState.Active?"Unavailable / membership "+member.State:"Retained assignment — mission preparation requires P16";
        var name=manager?await db.Users.Where(u=>u.Id==row.UserId).Select(u=>u.UserName).SingleAsync(ct):null;
        return new(row.Id,row.UserId,name,issued.OrganizationId,row.OpportunityId,row.Revision,row.IssuedRevision,row.IssuedSha256,row.State,row.AssignedUtc,row.AcceptedUtc,row.EndedUtc,row.EndReason,issued,live.PublishedRevision>row.IssuedRevision,live.PublishedRevision,availability);
    }
    public async Task<OrganizationPage<OrganizationClaimView>> Claims(Guid? opportunity,int offset,ClaimsPrincipal actor,bool manager,CancellationToken ct)
    {
        if(offset<0)throw new ArgumentException("Choose a valid assignment page.");await Active(actor,ct);if(manager)await Manager(actor,ct);var user=OrganizationMembership.Actor(actor);
        var query=db.Claims.AsNoTracking().AsQueryable();if(!manager)query=query.Where(r=>r.UserId==user);if(opportunity is {} id)query=query.Where(r=>r.OpportunityId==id);
        var rows=await query.OrderByDescending(r=>r.AssignedUtc).ThenBy(r=>r.Id).Skip(offset).Take(PageSize+1).ToArrayAsync(ct);var result=new List<OrganizationClaimView>();foreach(var row in rows.Take(PageSize))result.Add(await ClaimView(row,manager,ct));return new(1,result.ToArray(),rows.Length>PageSize?checked(offset+PageSize):null);
    }
    public async Task<OrganizationClaimEventPage> Events(Guid claim,long after,ClaimsPrincipal actor,bool manager,CancellationToken ct)
    {
        await Active(actor,ct);if(manager)await Manager(actor,ct);var row=await db.Claims.AsNoTracking().SingleOrDefaultAsync(c=>c.Id==claim,ct)??throw new OrganizationNotFound();if(!manager&&row.UserId!=OrganizationMembership.Actor(actor))throw new OrganizationForbidden();
        if(after<0)throw new ArgumentException("Choose a valid event page.");var events=await db.ClaimEvents.AsNoTracking().Where(e=>e.ClaimId==claim&&e.Id>after).OrderBy(e=>e.Id).Take(21).Select(e=>new OrganizationClaimEventView(e.Id,e.ClaimId,e.ClaimRevision,e.Action,e.TimestampUtc,e.ActorId)).ToArrayAsync(ct);
        // Keyset event cursors use long IDs, returned by the last event; no retained-history ceiling.
        return new(1,events.Take(20).ToArray(),events.Length>20?events[19].Id:null);
    }
}
