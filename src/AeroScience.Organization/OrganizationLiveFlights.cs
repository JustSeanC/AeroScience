using System.Security.Claims;
using System.Text.Json;
using AeroScience.Shared;
using AeroScience.Missions;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed class OrganizationLiveRuntime(TimeProvider clock)
{
    public DateTimeOffset UtcNow=>new(clock.GetUtcNow().Ticks/10*10,TimeSpan.Zero);
    public Guid InstanceId {get;}=Guid.NewGuid();
    internal readonly SemaphoreSlim Gate=new(1,1);
    internal readonly Dictionary<Guid,(LiveFlightUpdate Update,string Hash,DateTimeOffset Received)> Current=[];
    internal void Remove(Guid id)=>Current.Remove(id);
}
public sealed class OrganizationLiveFlights(OrganizationDatabase db,OrganizationMembership members,OrganizationDevices devices,
    OrganizationManagement history,OrganizationLiveRuntime runtime,OrganizationSettings settings)
{
    static T Read<T>(string text)=>JsonSerializer.Deserialize<T>(text,WireJson.Options)!;
    static Guid[] Claims(LiveConsentRecord r)=>Read<Guid[]>(r.ClaimsJson);
    static OrganizationLivePolicy View(LivePolicyRecord? r)=>new(1,r?.Revision??0,r?.Enabled??true,r is null?[]:Read<Guid[]>(r.RestrictedCampaignsJson));
    static LiveSharingSession Session(LiveConsentRecord r)=>new(r.Id,r.OrganizationId,r.PilotId,r.FlightId,r.Revision,r.StartedUtc,r.ExpiresUtc,r.PolicyRevision,OrganizationLiveProtocol.UpdateSeconds,r.EndedUtc is null?"Sharing":"Stopped");
    internal async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    async Task<MembershipRecord> Browser(ClaimsPrincipal actor,CancellationToken ct)
    {
        var member=await members.Current(actor,ct);
        if(member?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        var stamp=actor.FindFirstValue("AspNet.Identity.SecurityStamp");
        if(stamp is null||stamp!=await db.Users.Where(u=>u.Id==member.UserId).Select(u=>u.SecurityStamp).SingleAsync(ct))throw new OrganizationUnauthorized();
        return member;
    }
    public async Task<OrganizationLivePolicy> Policy(ClaimsPrincipal actor,CancellationToken ct){await Browser(actor,ct);return View(await db.LivePolicies.AsNoTracking().SingleOrDefaultAsync(ct));}
    public async Task<OrganizationLivePolicy> SetPolicy(LivePolicyCommand r,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(r.Version!=1||r.RequestId==Guid.Empty||r.ExpectedRevision<0||r.RestrictedCampaigns is null||r.RestrictedCampaigns.Length>64||r.RestrictedCampaigns.Any(x=>x==Guid.Empty)||r.RestrictedCampaigns.Distinct().Count()!=r.RestrictedCampaigns.Length)throw new ArgumentException("Choose up to 64 distinct published campaigns and the saved policy revision.");
        await runtime.Gate.WaitAsync(ct);try{return await history.Mutate(actor,r.RequestId,new{operation="live-policy",r},async(id,name)=>{
            await Lock(ct);await members.RequireManager(actor,true,ct);
            if(await db.Campaigns.CountAsync(c=>r.RestrictedCampaigns.Contains(c.Id)&&c.PublishedRevision!=null,ct)!=r.RestrictedCampaigns.Length)throw new ArgumentException("Choose only published campaigns in this organization.");
            var row=await db.LivePolicies.SingleOrDefaultAsync(ct);if((row?.Revision??0)!=r.ExpectedRevision)throw new OrganizationConflict("Live policy changed. Reload before saving.");
            if(row is null){row=new();db.LivePolicies.Add(row);}row.Revision++;row.Enabled=r.Enabled;row.RestrictedCampaignsJson=JsonSerializer.Serialize(r.RestrictedCampaigns);
            // Any policy change closes old consents, including changes which later relax a restriction.
            foreach(var consent in await db.LiveConsents.Where(x=>x.EndedUtc==null).Take(OrganizationLiveProtocol.MaximumFlights+1).ToArrayAsync(ct))End(consent,"policyChanged");
            var organization=(await members.Public(ct)).Id;history.Revision(new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("live-policy/v1:"+organization)).AsSpan(0,16)),row.Revision,"live-policy",View(row),id);
            history.Audit(id,name,"live-policy-changed",organization,row.Revision);return View(row);
        },ct,async()=>{await Lock(ct);await Browser(actor,ct);await members.RequireManager(actor,true,ct);});}finally{runtime.Gate.Release();}
    }
    async Task<OrganizationOpportunitySnapshot[]> Eligible(LiveConsentRecord row,OrganizationLivePolicy policy,CancellationToken ct)
    {
        if(!policy.Enabled||row.PolicyRevision!=policy.Revision)throw new OrganizationForbidden();
        // A pilot cannot bypass a sensitive-campaign restriction by omitting its claim tag.
        // Live identity/location remains pilot reported; do not pretend to attest a private flight archive.
        if(policy.RestrictedCampaigns.Length>0&&await db.Claims.AsNoTracking().AnyAsync(c=>c.UserId==row.PilotId&&c.State==OrganizationClaimState.Claimed&&c.EndedUtc==null&&db.Opportunities.Any(o=>o.Id==c.OpportunityId&&policy.RestrictedCampaigns.Contains(o.CampaignId)),ct))throw new OrganizationForbidden();
        var ids=Claims(row);if(ids.Length==0)return [];
        var claims=await db.Claims.AsNoTracking().Where(x=>ids.Contains(x.Id)&&x.UserId==row.PilotId&&x.State==OrganizationClaimState.Claimed&&x.EndedUtc==null).ToArrayAsync(ct);
        if(claims.Length!=ids.Length)throw new OrganizationForbidden();var result=new List<OrganizationOpportunitySnapshot>();
        foreach(var claim in claims){
            var opportunity=await db.Opportunities.AsNoTracking().SingleAsync(x=>x.Id==claim.OpportunityId,ct);
            if(opportunity.Suspended||policy.RestrictedCampaigns.Contains(opportunity.CampaignId))throw new OrganizationForbidden();
            var revision=await history.ReadRevision(claim.OpportunityId,claim.IssuedRevision,ct);
            if(revision.Sha256!=claim.IssuedSha256||revision.Kind!="opportunity")throw new OrganizationForbidden();
            result.Add(Read<OrganizationOpportunitySnapshot>(revision.SnapshotJson));
        }return result.ToArray();
    }
    internal void End(LiveConsentRecord row,string reason)
    {
        runtime.Remove(row.Id);if(row.EndedUtc is not null)return;
        row.EndedUtc=runtime.UtcNow;row.EndReason=reason;row.LastUpdateSha256="";
        OrganizationEvents.Add(db,row.OrganizationId,OrganizationEventType.LiveSharingStopped,"live-consent/v1",row.Id,row.Revision,row.PilotId,
            "live-stop:"+row.Id,OrganizationEventVisibility.ActiveMembers,"Pilot live sharing ended. This is not an evidence submission or research approval.");
        history.Audit(row.PilotId,"live-sharing","live-sharing-stopped",row.Id,row.Revision);
    }
    public async Task<LiveSharingSession> Start(LiveSharingStart r,HttpContext c,CancellationToken ct)
    {
        var now=runtime.UtcNow;
        if(r.Version!=1||r.ConsentVersion!=1||!r.ConfirmAllActiveMembers||r.RequestId==Guid.Empty||r.FlightId==Guid.Empty||r.ExpectedPolicyRevision<0||r.DisplayLabel is not {Length:>=1 and <=80}||string.IsNullOrWhiteSpace(r.DisplayLabel)||r.DisplayLabel.Any(char.IsControl)||r.ClaimIds is null||r.ClaimIds.Length>8||r.ClaimIds.Any(x=>x==Guid.Empty)||r.ClaimIds.Distinct().Count()!=r.ClaimIds.Length||r.FlightStartedUtc.Offset!=TimeSpan.Zero||r.FlightStartedUtc>now.AddSeconds(5)||r.FlightStartedUtc<now.AddDays(-2))throw new ArgumentException("Confirm the exact current flight, a public label, selected organization and disclosure to ALL active members.");
        await runtime.Gate.WaitAsync(ct);try{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var grant=await devices.Authorize(c,ct,requireLive:true);var org=await members.Public(ct);
            if(r.OrganizationId!=org.Id)throw new OrganizationForbidden();
            var prior=await db.LiveConsents.SingleOrDefaultAsync(x=>x.PilotId==grant.UserId&&x.RequestId==r.RequestId,ct);var hash=OrganizationMembership.Digest(JsonSerializer.Serialize(r,WireJson.Options));
            if(prior is not null){if(prior.RequestSha256!=hash||prior.GrantId!=grant.Id)throw new OrganizationConflict("That consent identity belongs to a different request.");await Validate(prior,ct);return Session(prior);}
            var policy=View(await db.LivePolicies.AsNoTracking().SingleOrDefaultAsync(ct));
            if(policy.Revision!=r.ExpectedPolicyRevision)throw new OrganizationConflict("Review the current organization live-sharing policy before consenting.");
            if(await db.LiveConsents.CountAsync(x=>x.EndedUtc==null&&x.InstanceId==runtime.InstanceId&&x.ExpiresUtc>now&&x.LastSeenUtc>now.AddSeconds(-120),ct)>=OrganizationLiveProtocol.MaximumFlights)throw new OrganizationConflict("Live Flights is at its 64-flight capacity. Your recording is unaffected.");
            if(await db.LiveConsents.AnyAsync(x=>x.PilotId==grant.UserId&&x.FlightId==r.FlightId&&x.EndedUtc==null&&x.InstanceId==runtime.InstanceId&&x.ExpiresUtc>now&&x.LastSeenUtc>now.AddSeconds(-120),ct))throw new OrganizationConflict("This pilot is already sharing this flight. Stop the existing session before starting another.");
            var row=new LiveConsentRecord{Id=Guid.NewGuid(),RequestId=r.RequestId,RequestSha256=hash,OrganizationId=org.Id,PilotId=grant.UserId!.Value,GrantId=grant.Id,FlightId=r.FlightId,InstanceId=runtime.InstanceId,PolicyRevision=policy.Revision,DisplayLabel=r.DisplayLabel.Trim(),ClaimsJson=JsonSerializer.Serialize(r.ClaimIds),FlightStartedUtc=r.FlightStartedUtc,StartedUtc=now,LastSeenUtc=now,ExpiresUtc=now.AddHours(8)<grant.CredentialExpiresUtc?now.AddHours(8):grant.CredentialExpiresUtc!.Value};
            await Eligible(row,policy,ct);db.LiveConsents.Add(row);
            OrganizationEvents.Add(db,org.Id,OrganizationEventType.LiveSharingStarted,"live-consent/v1",row.Id,1,row.PilotId,"live-start:"+row.Id,OrganizationEventVisibility.ActiveMembers,"Pilot explicitly shared a current flight with all active members. No scientific result is implied.");
            history.Audit(row.PilotId,"live-sharing","live-sharing-started",row.Id,1);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Session(row);
        }finally{runtime.Gate.Release();}
    }
    async Task Validate(LiveConsentRecord row,CancellationToken ct)=>await ValidateExternal(row,false,ct);
    internal async Task ValidateExternal(LiveConsentRecord row,bool ended,CancellationToken ct)
    {
        var now=runtime.UtcNow;
        if((ended?row.EndedUtc is null||row.EndReason is not("pilotStopped" or "flightEnded"):row.EndedUtc is not null)||row.InstanceId!=runtime.InstanceId||row.ExpiresUtc<=now||row.LastSeenUtc<=now.AddSeconds(-120))throw new OrganizationUnauthorized();
        var grant=await db.DeviceGrants.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==row.GrantId,ct);
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==row.PilotId,ct);
        var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(x=>x.UserId==row.PilotId,ct);
        if(settings.RequireAdministratorMfa&&member?.Role==OrganizationRoles.Administrator&&(user is null||!OrganizationAccountSecurity.Ready(user))||grant is null||grant.Revoked||grant.UserId!=row.PilotId||grant.Permission!=OrganizationDesktopPermission.ShareLiveFlight||grant.CredentialExpiresUtc<=now||user is null||user.SecurityStamp!=grant.SecurityStamp||member?.State!=OrganizationMembershipState.Active)throw new OrganizationUnauthorized();
        await Eligible(row,View(await db.LivePolicies.AsNoTracking().SingleOrDefaultAsync(ct)),ct);
    }
    public static void ValidateUpdate(LiveFlightUpdate r)
    {
        static bool Bounded(double? x,double min,double max)=>x is null||double.IsFinite(x.Value)&&x>=min&&x<=max;
        if(r.Version!=1||r.SessionId==Guid.Empty||r.ConsentRevision!=1||r.Sequence<=0||!Enum.IsDefined(r.State)||r.Source is {} source&&!Enum.IsDefined(source)||r.SourceUtc is {} at&&at.Offset!=TimeSpan.Zero||r.Aircraft is {Length:>120}||r.Aircraft?.Any(char.IsControl)==true||!Bounded(r.Latitude,-90,90)||!Bounded(r.Longitude,-180,180)||!Bounded(r.HeadingDegrees,0,360)||!Bounded(r.AltitudeMslFeet,-5000,200000)||!Bounded(r.IndicatedAirspeedKnots,0,10000)||r.Latitude.HasValue!=r.Longitude.HasValue||r.Cards is null||r.Cards.Length>8||r.Cards.Select(x=>x.ClaimId).Distinct().Count()!=r.Cards.Length||r.Cards.Any(x=>x.ClaimId==Guid.Empty||x.CardId==Guid.Empty||x.CardRevision<1||!Enum.IsDefined(x.Progress)||x.Outcome is {} outcome&&!Enum.IsDefined(outcome))||r.Science is {} science&&(science.Instruments is null||science.Instruments.Length>8||science.Phase is {} phase&&!Enum.IsDefined(phase)||science.Instruments.Any(x=>x.InstrumentId is not {Length:>=1 and <=80}||!ScienceCatalog.Definitions.Any(d=>d.Id==x.InstrumentId)||!Enum.IsDefined(x.State)||!Enum.IsDefined(x.SourceState)))||JsonSerializer.SerializeToUtf8Bytes(r,WireJson.Options).Length>OrganizationLiveProtocol.MaximumBytes)throw new ArgumentException("Live updates contain unsupported, invalid or excessive current-state fields. Raw science, notes and histories are not supported.");
        if(r.Latitude is not null&&(r.State!=LiveSourceState.Connected||r.SourceUtc is null||r.Source is null))throw new ArgumentException("A position requires an actual available source and original UTC time.");
    }
    public async Task<LiveUpdateReceipt> Update(LiveFlightUpdate r,HttpContext c,CancellationToken ct)
    {
        ValidateUpdate(r);await runtime.Gate.WaitAsync(ct);try{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var grant=await devices.Authorize(c,ct,requireLive:true);
            var row=await db.LiveConsents.SingleOrDefaultAsync(x=>x.Id==r.SessionId&&x.GrantId==grant.Id,ct)??throw new OrganizationNotFound();await Validate(row,ct);
            var ids=Claims(row);if(r.Cards.Any(x=>!ids.Contains(x.ClaimId)))throw new OrganizationForbidden();
            foreach(var card in r.Cards){var claim=await db.Claims.AsNoTracking().SingleAsync(x=>x.Id==card.ClaimId,ct);var issued=Read<OrganizationOpportunitySnapshot>((await history.ReadRevision(claim.OpportunityId,claim.IssuedRevision,ct)).SnapshotJson);
                if(card.CardId!=MissionStore.OrganizationLocalCardId(settings.Origin.GetLeftPart(UriPartial.Authority),row.OrganizationId,row.PilotId,claim.Id,claim.IssuedSha256)||card.CardRevision!=1||card.PointId is {} point&&issued.Definition.Procedure?.Points.Any(p=>p.Id==point)!=true)throw new OrganizationForbidden();}

            var now=runtime.UtcNow;var hash=OrganizationMembership.Digest(JsonSerializer.Serialize(r,WireJson.Options));
            if(row.LastSequence>0){
                if(r.Sequence==row.LastSequence&&hash==row.LastUpdateSha256)return new(row.Id,r.Sequence,true,row.LastSeenUtc);
                if(r.Sequence<=row.LastSequence)throw new OrganizationConflict("This live update was superseded. Do not replay old positions.");
                if(now-row.LastSeenUtc<TimeSpan.FromSeconds(OrganizationLiveProtocol.UpdateSeconds))return new(row.Id,row.LastSequence,false,row.LastSeenUtc,5);
            }
            // No interpolation or alternate signal substitution. Old/future inputs lose their numerical fields.
            if(r.SourceUtc is not {} at||at>now.AddSeconds(5)||now-at>TimeSpan.FromSeconds(15)||r.State!=LiveSourceState.Connected)r=r with{Latitude=null,Longitude=null,HeadingDegrees=null,AltitudeMslFeet=null,IndicatedAirspeedKnots=null,State=r.State==LiveSourceState.Connected?LiveSourceState.Stale:r.State};
            foreach(var card in r.Cards.Where(x=>x.AttemptId!=null&&x.Progress is LiveResearchProgress.AttemptRecorded or LiveResearchProgress.LocallyEvaluated or LiveResearchProgress.MissingEvidence)){
                var key="live-local:"+OrganizationMembership.Digest(row.PilotId+":"+row.FlightId+":"+card.ClaimId+":"+card.AttemptId+":"+card.Progress);
                if(!await db.Events.AnyAsync(e=>e.IdempotencyKey==key,ct))OrganizationEvents.Add(db,row.OrganizationId,card.Progress==LiveResearchProgress.LocallyEvaluated?OrganizationEventType.LocalEvaluationReported:OrganizationEventType.AttemptRecorded,"authenticated-pilot-local-report/v1",card.AttemptId!.Value,card.CardRevision,row.PilotId,key,OrganizationEventVisibility.ActiveMembers,"Pilot reported a local recorded/evaluated attempt. Not received evidence, reviewer approval, card completion or aircraft certification.",row.PilotId,card:card.CardId);
            }
            row.LastSeenUtc=now;row.LastSequence=r.Sequence;row.LastUpdateSha256=hash;await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);runtime.Current[row.Id]=(r,hash,now);return new(row.Id,r.Sequence,true,now);
        }finally{runtime.Gate.Release();}
    }
    public async Task<LiveSharingSession> Stop(LiveSharingStop r,HttpContext c,CancellationToken ct)
    {
        if(r.Version!=1||r.RequestId==Guid.Empty||r.ConsentRevision!=1||r.Reason is not("pilotStopped" or "flightEnded" or "disconnected"))throw new ArgumentException("Choose a supported stop reason.");
        await runtime.Gate.WaitAsync(ct);try{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var grant=await devices.Authorize(c,ct,requireLive:true);
            var row=await db.LiveConsents.SingleOrDefaultAsync(x=>x.Id==r.SessionId&&x.GrantId==grant.Id,ct)??throw new OrganizationNotFound();End(row,r.Reason);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return Session(row);
        }finally{runtime.Gate.Release();}
    }
    public async Task<LiveDashboard> Dashboard(ClaimsPrincipal actor,CancellationToken ct,HttpContext? desktop=null)
    {
        await runtime.Gate.WaitAsync(ct);try{
            await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);
            if(desktop is null)await Browser(actor,ct);else await devices.Authorize(desktop,ct,requireLive:true);
            var policy=View(await db.LivePolicies.AsNoTracking().SingleOrDefaultAsync(ct));var now=runtime.UtcNow;var result=new List<LiveFlightView>();
            foreach(var row in await db.LiveConsents.Where(x=>x.EndedUtc==null&&x.InstanceId==runtime.InstanceId).OrderBy(x=>x.StartedUtc).Take(65).ToArrayAsync(ct)){
                try{await Validate(row,ct);}catch(Exception e)when(e is OrganizationForbidden or OrganizationUnauthorized){End(row,"authorizationEnded");continue;}
                LiveFlightUpdate? current=null;DateTimeOffset? received=null;
                if(runtime.Current.TryGetValue(row.Id,out var entry)){received=entry.Received;if(now-entry.Received<TimeSpan.FromSeconds(30))current=entry.Update;else runtime.Remove(row.Id);}
                var cards=new List<LiveCardView>();
                foreach(var card in current?.Cards??[]){var claim=await db.Claims.AsNoTracking().SingleAsync(x=>x.Id==card.ClaimId,ct);var issued=Read<OrganizationOpportunitySnapshot>((await history.ReadRevision(claim.OpportunityId,claim.IssuedRevision,ct)).SnapshotJson);cards.Add(new(claim.Id,claim.OpportunityId,claim.IssuedRevision,issued.Name,card.CardId,card.CardRevision,card.PointId,card.AttemptId,card.Progress,"Pilot-reported local status; not submitted, reviewed or approved evidence.",issued.CampaignId,(await db.Campaigns.AsNoTracking().SingleAsync(c=>c.Id==issued.CampaignId,ct)).PublishedName,issued.Definition.Procedure?.Points.FirstOrDefault(p=>p.Id==card.PointId)?.Conditions.TestPoint?.Name,card.Active,card.Outcome));}
                result.Add(new(row.Id,row.FlightId,row.DisplayLabel,row.FlightStartedUtc,row.ExpiresUtc,received,received is null||now-received>TimeSpan.FromSeconds(15),current,cards.ToArray()));
            }await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
            return new(1,now,policy,result.ToArray(),64,15,30,"Read-only current state shared with ALL active members. Positions expire; no tracks or raw science are retained. Local progress is not organization approval.");
        }finally{runtime.Gate.Release();}
    }
    public async Task Sweep(CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct);try{await using var tx=await db.Database.BeginTransactionAsync(ct);await Lock(ct);var now=runtime.UtcNow;
            foreach(var id in runtime.Current.Where(x=>now-x.Value.Received>=TimeSpan.FromSeconds(30)).Select(x=>x.Key).ToArray())runtime.Remove(id);
            foreach(var row in await db.LiveConsents.Where(x=>x.EndedUtc==null).OrderBy(x=>x.StartedUtc).Take(128).ToArrayAsync(ct))try{await Validate(row,ct);}catch(Exception e)when(e is OrganizationForbidden or OrganizationUnauthorized){End(row,row.InstanceId!=runtime.InstanceId?"serverRestarted":"authorizationEnded");}
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }finally{runtime.Gate.Release();}
    }
}
