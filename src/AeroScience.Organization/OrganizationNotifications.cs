using System.Security.Claims;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
namespace AeroScience.Organization;

public sealed record DiscordDestinationView(Guid Id,int Revision,string Label,string MaskedIdentifier,bool Enabled,int Categories,string Notice,bool AuthorizationCurrent=true);
public sealed record DiscordSettingsCommand(Guid RequestId,int ExpectedRevision,string Label,bool Enabled,int Categories,string? Webhook=null,bool Delete=false);
public sealed record DiscordAction(Guid RequestId,int ExpectedRevision,Guid? EventId=null,bool ConfirmExternalDisclosure=false,string? MessageSha256=null);
public sealed record DiscordConsentCommand(Guid RequestId,Guid SessionId,int ExpectedRevision,int DestinationRevision,bool AllowExternalAnnouncements);
public sealed record DiscordConsentView(Guid SessionId,string DisplayLabel,Guid FlightId,int Revision,bool Allowed,DateTimeOffset ExpiresUtc);
public sealed record DiscordDeliveryView(Guid Id,Guid EventId,int DestinationRevision,bool Test,NotificationState State,int Attempts,DateTimeOffset CreatedUtc,DateTimeOffset? FinishedUtc,string Reason,string MessageId);
public sealed record DiscordAnnouncement(Guid EventId,string Category,string Message,string MessageSha256);
public sealed record DiscordAnnouncementPage(DiscordAnnouncement[] Items,long? NextBefore);
public sealed class DiscordNotificationRuntime { public Guid InstanceId {get;}=Guid.NewGuid(); }

public sealed class OrganizationNotifications(OrganizationDatabase db,OrganizationMembership members,OrganizationManagement history,
    IDataProtectionProvider protection,OrganizationLiveFlights live,DiscordNotificationRuntime runtime,TimeProvider clock,IDiscordTransport transport)
{
    DateTimeOffset Now=>new(clock.GetUtcNow().Ticks/10*10,TimeSpan.Zero);
    static DiscordDestinationView View(DiscordDestinationRecord? d)=>new(d?.PublicId??Guid.Empty,d?.Revision??0,d?.Label??"",d?.MaskedIdentifier??"",d?.Enabled??false,(int)(d?.Categories??DiscordCategories.None),"Discord channels may be visible outside this organization. Sharing with members is not external consent. No coordinates, raw science, notes or credentials are sent. Delivered means Discord acknowledged the post; it is not scientific certification.");
    IDataProtector Protector(DiscordDestinationRecord d)=>protection.CreateProtector("AeroScience/Discord/v1",d.OrganizationId.ToString(),d.PublicId.ToString());
    async Task Lock(CancellationToken ct)=>await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
    async Task<MembershipRecord> Member(ClaimsPrincipal actor,CancellationToken ct)
    {
        var m=await members.Current(actor,ct);if(m?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        if(actor.FindFirstValue("AspNet.Identity.SecurityStamp")!=await db.Users.Where(u=>u.Id==m.UserId).Select(u=>u.SecurityStamp).SingleAsync(ct))throw new OrganizationUnauthorized();return m;
    }
    async Task Admin(ClaimsPrincipal actor,bool sensitive,CancellationToken ct)
    {
        var m=await Member(actor,ct);if(m.Role!=OrganizationRoles.Administrator)throw new OrganizationForbidden();
        if(sensitive&&(!OrganizationAccountSecurity.Recent(actor)||!await db.Users.AnyAsync(u=>u.Id==m.UserId&&u.TwoFactorEnabled&&u.RecoveryAcknowledgedUtc!=null,ct)))throw new OrganizationForbidden();
    }
    async Task<bool> Authorized(Guid actor,string stamp,CancellationToken ct)=>await db.Memberships.AnyAsync(m=>m.UserId==actor&&m.State==OrganizationMembershipState.Active&&m.Role==OrganizationRoles.Administrator,ct)&&await db.Users.AnyAsync(u=>u.Id==actor&&u.SecurityStamp==stamp&&u.TwoFactorEnabled&&u.RecoveryAcknowledgedUtc!=null,ct);
    public async Task<DiscordDestinationView> Settings(ClaimsPrincipal actor,CancellationToken ct){await Admin(actor,false,ct);var d=await db.DiscordDestinations.AsNoTracking().SingleOrDefaultAsync(ct);return View(d) with{AuthorizationCurrent=d is null||d.Deleted||await Authorized(d.AuthorizerId,d.AuthorizerStamp,ct)};}
    public async Task<DiscordDestinationView> Save(DiscordSettingsCommand r,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(r.RequestId==Guid.Empty||r.ExpectedRevision<0||r.Label is null||r.Label.Trim().Length is <1 or >80||r.Label.Any(char.IsControl)||(r.Categories&~7)!=0)throw new ArgumentException("Choose a readable destination name, supported categories and saved revision.");
        var parsed=r.Webhook is null?null:DiscordWebhookAddress.Parse(r.Webhook);
        // Hash-only command input; neither receipts nor audit/revision snapshots retain a secret.
        return await history.Mutate(actor,r.RequestId,new{operation="discord-settings",r.ExpectedRevision,r.Label,r.Enabled,r.Categories,r.Delete,credentialHash=r.Webhook is null?null:OrganizationMembership.Digest(r.Webhook)},async(id,name)=>{
            var d=await db.DiscordDestinations.SingleOrDefaultAsync(ct);if((d?.Revision??0)!=r.ExpectedRevision)throw new OrganizationConflict("Notifications changed. Reload before saving.");
            if(d is null){d=new(){PublicId=Guid.NewGuid(),OrganizationId=(await members.Public(ct)).Id};db.DiscordDestinations.Add(d);}
            if(!r.Delete&&(d.Deleted||d.ProtectedCredential.Length==0)&&parsed is null)throw new ArgumentException("Enter a Discord incoming webhook to create this destination.");
            d.Revision++;d.UpdatedUtc=Now;d.Label=r.Delete?"":r.Label.Trim();d.Enabled=!r.Delete&&r.Enabled;d.Categories=r.Delete?DiscordCategories.None:(DiscordCategories)r.Categories;d.Deleted=r.Delete;
            d.AuthorizerId=id;d.AuthorizerStamp=actor.FindFirstValue("AspNet.Identity.SecurityStamp")!;
            if(parsed is not null){d.ProtectedCredential=Protector(d).Protect(r.Webhook!);d.MaskedIdentifier=parsed.MaskedIdentifier;}
            if(r.Delete){d.ProtectedCredential="";d.MaskedIdentifier="";}
            d.EventCursor=await db.Events.MaxAsync(e=>(long?)e.Sequence,ct)??0;
            foreach(var pending in await db.DiscordDeliveries.Where(q=>q.State==NotificationState.Pending).Take(501).ToArrayAsync(ct)){pending.State=NotificationState.Disabled;pending.Reason="Destination changed or deleted; this intent will not be sent.";pending.FinishedUtc=Now;}
            var view=View(d);history.Revision(d.PublicId,d.Revision,"discord-settings",view,id);history.Audit(id,name,r.Delete?"discord-destination-deleted":"discord-settings-saved",d.PublicId,d.Revision);return view;
        },ct,async()=>{await Lock(ct);await Admin(actor,true,ct);});
    }
    static DiscordCategories Category(OrganizationEventType t)=>t switch{OrganizationEventType.LiveSharingStarted or OrganizationEventType.LiveSharingStopped=>DiscordCategories.Flights,OrganizationEventType.ApprovedCardResult=>DiscordCategories.ApprovedResearch,OrganizationEventType.DocumentedExceptionAccepted=>DiscordCategories.AcceptedExceptions,_=>DiscordCategories.None};
    async Task<DiscordDestinationRecord> Destination(int revision,CancellationToken ct)
    {
        var d=await db.DiscordDestinations.SingleOrDefaultAsync(ct);if(d is null||d.Deleted||!d.Enabled||d.Revision!=revision||!await Authorized(d.AuthorizerId,d.AuthorizerStamp,ct))throw new OrganizationConflict("This destination is disabled, changed or needs renewed administrator authorization. Verify Account Security and save current notification settings before approving.");return d;
    }
    async Task Capacity(CancellationToken ct){if(await db.DiscordDeliveries.CountAsync(q=>q.State==NotificationState.Pending||q.State==NotificationState.Sending,ct)>=500)throw new OrganizationConflict("The notification queue is full. Resolve pending deliveries before scheduling more.");}
    async Task<DiscordDeliveryRecord> Schedule(DiscordDestinationRecord d,Guid eventId,bool test,CancellationToken ct,DiscordConsentRecord? consent=null)
    {
        var old=await db.DiscordDeliveries.SingleOrDefaultAsync(q=>q.EventId==eventId&&q.DestinationId==d.PublicId&&q.DestinationRevision==d.Revision,ct);if(old is not null)return old;await Capacity(ct);
        var row=new DiscordDeliveryRecord{Id=Guid.NewGuid(),EventId=eventId,DestinationId=d.PublicId,DestinationRevision=d.Revision,Test=test,State=NotificationState.Pending,CreatedUtc=Now,DueUtc=Now,ConsentSessionId=consent?.SessionId,ConsentRevision=consent?.Revision};db.DiscordDeliveries.Add(row);return row;
    }
    static DiscordDeliveryView Delivery(DiscordDeliveryRecord r)=>new(r.Id,r.EventId,r.DestinationRevision,r.Test,r.State,r.Attempts,r.CreatedUtc,r.FinishedUtc,r.Reason,r.MessageId);
    public async Task<DiscordDeliveryView> Test(DiscordAction r,ClaimsPrincipal actor,CancellationToken ct)=>await history.Mutate(actor,r.RequestId,new{operation="discord-explicit-test",r},async(id,name)=>{
        var d=await Destination(r.ExpectedRevision,ct);var row=await Schedule(d,r.RequestId,true,ct);history.Audit(id,name,"discord-test-requested",row.Id,d.Revision);return Delivery(row);
    },ct,async()=>{if(r.RequestId==Guid.Empty)throw new ArgumentException("Use a new action identity.");await Lock(ct);await Admin(actor,true,ct);});
    public async Task<object> History(long before,ClaimsPrincipal actor,CancellationToken ct)
    {
        await Admin(actor,false,ct);if(before<0)throw new ArgumentException("Choose a valid notification page.");var rows=await db.DiscordDeliveries.AsNoTracking().Where(q=>before==0||q.Sequence<before).OrderByDescending(q=>q.Sequence).Take(21).ToArrayAsync(ct);
        return new{items=rows.Take(20).Select(Delivery).ToArray(),nextBefore=rows.Length>20?(long?)rows[19].Sequence:null};
    }
    public async Task<object> Cancel(Guid id,DiscordAction r,ClaimsPrincipal actor,CancellationToken ct)=>await history.Mutate(actor,r.RequestId,new{operation="discord-cancel",id,r},async(user,name)=>{
        var q=await db.DiscordDeliveries.SingleOrDefaultAsync(x=>x.Id==id,ct)??throw new OrganizationNotFound();if(q.State==NotificationState.Sending)throw new OrganizationConflict("Delivery has started. Its acknowledgment must be resolved; cancellation cannot recall a Discord post.");
        if(q.State==NotificationState.Delivered)throw new OrganizationConflict("Discord already received this notification. Delete it in Discord if required.");var uncertain=q.State==NotificationState.Ambiguous;q.State=uncertain?NotificationState.Ambiguous:NotificationState.Disabled;q.FinishedUtc=Now;q.Reason=uncertain?"Administrator closed this uncertain delivery. Discord may have received it; no automatic retry.":"Cancelled by an administrator. No automatic retry.";history.Audit(user,name,"discord-delivery-cancelled",q.Id);return (object)Delivery(q);
    },ct,async()=>{await Lock(ct);await Admin(actor,true,ct);});
    public async Task<object> Pilot(ClaimsPrincipal actor,CancellationToken ct)
    {
        var member=await Member(actor,ct);var d=await db.DiscordDestinations.AsNoTracking().SingleOrDefaultAsync(ct);
        var sessions=await db.LiveConsents.AsNoTracking().Where(s=>s.PilotId==member.UserId&&s.EndedUtc==null&&s.ExpiresUtc>Now).OrderByDescending(s=>s.StartedUtc).Take(64).ToArrayAsync(ct);
        var ids=sessions.Select(s=>s.Id).ToArray();var consents=await db.DiscordConsents.AsNoTracking().Where(c=>ids.Contains(c.SessionId)).ToArrayAsync(ct);
        return new{destination=d is {Enabled:true,Deleted:false}&&(d.Categories&DiscordCategories.Flights)!=0&&await Authorized(d.AuthorizerId,d.AuthorizerStamp,ct)?View(d):null,flights=sessions.Select(s=>{var c=consents.SingleOrDefault(c=>c.SessionId==s.Id);return new DiscordConsentView(s.Id,s.DisplayLabel,s.FlightId,c?.Revision??0,c?.Allowed==true&&c.DestinationRevision==d?.Revision,s.ExpiresUtc);}).ToArray()};
    }
    public async Task<DiscordConsentView> Consent(DiscordConsentCommand r,ClaimsPrincipal actor,CancellationToken ct)=>await history.Mutate<DiscordConsentView>(actor,r.RequestId,new{operation="discord-pilot-consent",r},async(id,name)=>{
        var session=await db.LiveConsents.SingleOrDefaultAsync(s=>s.Id==r.SessionId&&s.PilotId==id,ct)??throw new OrganizationNotFound();
        var c=await db.DiscordConsents.SingleOrDefaultAsync(c=>c.SessionId==r.SessionId,ct);if((c?.Revision??0)!=r.ExpectedRevision)throw new OrganizationConflict("Your announcement consent changed. Reload before choosing again.");
        DiscordDestinationRecord? d=null;if(r.AllowExternalAnnouncements){d=await Destination(r.DestinationRevision,ct);if((d.Categories&DiscordCategories.Flights)==0)throw new OrganizationForbidden();await live.ValidateExternal(session,false,ct);}
        if(c is null){c=new(){SessionId=session.Id,PilotId=id};db.DiscordConsents.Add(c);}c.Revision++;c.Allowed=r.AllowExternalAnnouncements;c.UpdatedUtc=Now;
        if(d is not null){c.DestinationId=d.PublicId;c.DestinationRevision=d.Revision;var start=await db.Events.SingleAsync(e=>e.SourceId==session.Id&&e.Type==OrganizationEventType.LiveSharingStarted,ct);await Schedule(d,start.Id,false,ct,c);}
        if(!c.Allowed)foreach(var q in await db.DiscordDeliveries.Where(q=>q.ConsentSessionId==session.Id&&q.State==NotificationState.Pending).Take(500).ToArrayAsync(ct)){q.State=NotificationState.Disabled;q.Reason="Pilot revoked external announcement consent.";q.FinishedUtc=Now;}
        history.Revision(session.Id,c.Revision,"discord-flight-consent",new{session.Id,session.FlightId,c.PilotId,c.DestinationId,c.DestinationRevision,c.Revision,c.Allowed,c.UpdatedUtc},id);history.Audit(id,name,c.Allowed?"discord-flight-consent-granted":"discord-flight-consent-revoked",session.Id,c.Revision);return new(session.Id,session.DisplayLabel,session.FlightId,c.Revision,c.Allowed,session.ExpiresUtc);
    },ct,async()=>{if(r.RequestId==Guid.Empty)throw new ArgumentException("Use a new consent identity.");await Lock(ct);await Member(actor,ct);});
    async Task<bool> CurrentAuthority(OrganizationEventRecord e,CancellationToken ct)
    {
        if(e.SubjectId is {} subject&&!await db.Memberships.AnyAsync(m=>m.UserId==subject&&m.State==OrganizationMembershipState.Active,ct))return false;
        var livePolicy=await db.LivePolicies.AsNoTracking().SingleOrDefaultAsync(ct);
        if(e.CampaignId is {} campaign&&livePolicy is not null&&JsonSerializer.Deserialize<Guid[]>(livePolicy.RestrictedCampaignsJson)!.Contains(campaign))return false;
        if(e.Type==OrganizationEventType.ApprovedCardResult){var c=await db.CampaignContributions.AsNoTracking().SingleOrDefaultAsync(c=>c.RootId==e.SourceId&&c.CardId==e.CardId,ct);return c is {Valid:true}&&c.RequiredFindings>0&&c.ApprovedFindings==c.RequiredFindings&&c.State==ResearchReviewState.Approved&&!await db.Events.AnyAsync(n=>n.SourceId==e.SourceId&&n.CardId==e.CardId&&n.Sequence>e.Sequence&&(n.Type==OrganizationEventType.ApprovedCardResult||n.Type==OrganizationEventType.ApprovedResultSuperseded),ct);}
        if(e.Type==OrganizationEventType.DocumentedExceptionAccepted)return await db.WorkflowExceptions.AnyAsync(x=>x.Id==e.SourceId&&x.Revision==e.SourceRevision&&x.Active,ct);
        return false;
    }
    async Task<string?> Content(DiscordDestinationRecord d,DiscordDeliveryRecord q,CancellationToken ct)
    {
        if(q.Test)return "AeroScience — Test notification. This administrator-requested message contains no flight or scientific evidence.";
        var e=await db.Events.AsNoTracking().SingleOrDefaultAsync(e=>e.Id==q.EventId&&e.OrganizationId==d.OrganizationId,ct);if(e is null||(d.Categories&Category(e.Type))==0)return null;
        if(Category(e.Type)==DiscordCategories.Flights){
            var c=await db.DiscordConsents.AsNoTracking().SingleOrDefaultAsync(c=>c.SessionId==e.SourceId,ct);if(c is null||!c.Allowed||c.DestinationId!=d.PublicId||c.DestinationRevision!=d.Revision||c.Revision!=q.ConsentRevision)return null;
            var s=await db.LiveConsents.AsNoTracking().SingleOrDefaultAsync(s=>s.Id==c.SessionId,ct);if(s is null)return null;
            try{await live.ValidateExternal(s,e.Type==OrganizationEventType.LiveSharingStopped,ct);}catch(Exception ex)when(ex is OrganizationForbidden or OrganizationUnauthorized){return null;}
            return "AeroScience — "+DiscordWebhookPolicy.PlainText(s.DisplayLabel)+ (e.Type==OrganizationEventType.LiveSharingStarted?" started sharing a research flight.":" ended research-flight sharing.")+" Event UTC: "+e.TimestampUtc.ToString("O")+". Live sharing is not a scientific result. No location is disclosed.";
        }
        var disclosure=await db.DiscordDisclosures.AsNoTracking().SingleOrDefaultAsync(x=>x.EventId==e.Id&&x.DestinationId==d.PublicId&&x.DestinationRevision==d.Revision,ct);
        if(disclosure is null||!await Authorized(disclosure.AuthorizerId,disclosure.AuthorizerStamp,ct)||!await CurrentAuthority(e,ct))return null;
        var message=Announcement(e);return disclosure.MessageSha256==OrganizationMembership.Digest(message)?message:null;
    }
    static string Announcement(OrganizationEventRecord e)=>"AeroScience — "+(e.Type==OrganizationEventType.ApprovedCardResult?"A research-card contribution received reviewer approval. Original measured outcomes remain unchanged; this is not aircraft certification.":"A documented administrative exception was accepted. This does not turn missing or failed scientific evidence into a Pass.")+" Card: "+(e.CardId?.ToString()??"not supplied")+". Event UTC: "+e.TimestampUtc.ToString("O")+". Details remain in the authorized organization portal.";
    public async Task<DiscordAnnouncementPage> Announcements(ClaimsPrincipal actor,CancellationToken ct,long before=0)
    {
        await Admin(actor,false,ct);if(before<0)throw new ArgumentException("Choose a valid announcement page.");var rows=await db.Events.AsNoTracking().Where(e=>(before==0||e.Sequence<before)&&(e.Type==OrganizationEventType.ApprovedCardResult||e.Type==OrganizationEventType.DocumentedExceptionAccepted)).OrderByDescending(e=>e.Sequence).Take(51).ToArrayAsync(ct);var result=new List<DiscordAnnouncement>();foreach(var e in rows.Take(50))if(await CurrentAuthority(e,ct))result.Add(new(e.Id,e.Type.ToString(),Announcement(e),OrganizationMembership.Digest(Announcement(e))));return new(result.ToArray(),rows.Length>50?rows[49].Sequence:null);
    }
    public async Task<DiscordDeliveryView> Disclose(DiscordAction r,ClaimsPrincipal actor,CancellationToken ct)=>await history.Mutate(actor,r.RequestId,new{operation="discord-explicit-disclosure",r},async(id,name)=>{
        if(!r.ConfirmExternalDisclosure||r.EventId is null)throw new ArgumentException("Review this exact announcement and explicitly approve disclosure outside the organization.");
        var d=await Destination(r.ExpectedRevision,ct);var e=await db.Events.SingleOrDefaultAsync(e=>e.Id==r.EventId&&e.OrganizationId==d.OrganizationId,ct)??throw new OrganizationNotFound();if((d.Categories&Category(e.Type))==0||!await CurrentAuthority(e,ct))throw new OrganizationForbidden();
        if(r.MessageSha256!=OrganizationMembership.Digest(Announcement(e)))throw new OrganizationConflict("The announcement preview changed. Review the current exact message before authorizing external disclosure.");
        if(!await db.DiscordDisclosures.AnyAsync(x=>x.EventId==e.Id&&x.DestinationId==d.PublicId&&x.DestinationRevision==d.Revision,ct))db.DiscordDisclosures.Add(new(){EventId=e.Id,DestinationId=d.PublicId,DestinationRevision=d.Revision,AuthorizerId=id,AuthorizerStamp=actor.FindFirstValue("AspNet.Identity.SecurityStamp")!,MessageSha256=r.MessageSha256,ApprovedUtc=Now});
        var q=await Schedule(d,e.Id,false,ct);history.Audit(id,name,"discord-external-disclosure-approved",e.Id,d.Revision);return Delivery(q);
    },ct,async()=>{if(r.RequestId==Guid.Empty)throw new ArgumentException("Use a new action identity.");await Lock(ct);await Admin(actor,true,ct);});
    public async Task Tick(CancellationToken ct)
    {
        // Scheduling and durable reservation are short database transactions; HTTP is outside all membership/live locks.
        Guid? send=null;string? content=null;string? credential=null;
        await using(var tx=await db.Database.BeginTransactionAsync(ct)){
            await Lock(ct);
            // Single organization-server instance, like live presence. A process lost after dispatch has unknown delivery, never automatic replay.
            foreach(var abandoned in await db.DiscordDeliveries.Where(q=>q.State==NotificationState.Sending&&(q.SenderInstance!=runtime.InstanceId||q.DueUtc<=Now.AddSeconds(-30))).Take(500).ToArrayAsync(ct)){abandoned.State=NotificationState.Ambiguous;abandoned.Reason="Server restarted during delivery. Discord may have received it; no automatic retry.";abandoned.FinishedUtc=Now;}
            var d=await db.DiscordDestinations.SingleOrDefaultAsync(ct);
            if(d is {Enabled:true,Deleted:false}&&await Authorized(d.AuthorizerId,d.AuthorizerStamp,ct)){
                var count=await db.DiscordDeliveries.CountAsync(q=>q.State==NotificationState.Pending||q.State==NotificationState.Sending,ct);
                if(count<500){var events=await db.Events.AsNoTracking().Where(e=>e.Sequence>d.EventCursor).OrderBy(e=>e.Sequence).Take(Math.Min(100,500-count)).ToArrayAsync(ct);foreach(var e in events){
                    if(e.Type==OrganizationEventType.LiveSharingStopped&&(d.Categories&DiscordCategories.Flights)!=0){var c=await db.DiscordConsents.SingleOrDefaultAsync(c=>c.SessionId==e.SourceId&&c.Allowed&&c.DestinationId==d.PublicId&&c.DestinationRevision==d.Revision,ct);if(c is not null)await Schedule(d,e.Id,false,ct,c);}
                    d.EventCursor=e.Sequence;
                }}
            }
            // Make newly scheduled intents visible to the indexed selection in this same transaction.
            await db.SaveChangesAsync(ct);
            var q=await db.DiscordDeliveries.Where(q=>q.State==NotificationState.Pending&&q.DueUtc<=Now&&(d==null||d.NextSendUtc<=Now)).OrderBy(q=>q.Sequence).FirstOrDefaultAsync(ct);
            if(q is not null){
                if(d is {Enabled:true,Deleted:false}&&d.PublicId==q.DestinationId&&d.Revision==q.DestinationRevision&&await Authorized(d.AuthorizerId,d.AuthorizerStamp,ct))content=await Content(d,q,ct);
                if(content is null){q.State=NotificationState.Disabled;q.Reason="Destination, consent, membership or authoritative result no longer permits disclosure.";q.FinishedUtc=Now;}
                else{try{credential=Protector(d!).Unprotect(d!.ProtectedCredential);DiscordWebhookAddress.Parse(credential);q.State=NotificationState.Sending;q.DueUtc=Now;q.SenderInstance=runtime.InstanceId;q.Attempts++;d!.NextSendUtc=Now.AddSeconds(2);q.Reason="Sending; acknowledgment not yet received.";send=q.Id;}catch(Exception ex)when(ex is System.Security.Cryptography.CryptographicException or ArgumentException){q.State=NotificationState.Failed;q.Reason="Protected destination cannot be opened. An administrator must replace the webhook.";q.FinishedUtc=Now;}}
            }
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
        }
        if(send is null)return;
        DiscordResponse response;try{response=await transport.Send(DiscordWebhookAddress.Parse(credential!),DiscordWebhookPolicy.Message(content!),ct);}catch{response=new(DiscordAcknowledgment.Ambiguous);}finally{credential=null;}
        db.ChangeTracker.Clear();await using(var tx=await db.Database.BeginTransactionAsync(CancellationToken.None)){
            await Lock(CancellationToken.None);var q=await db.DiscordDeliveries.SingleAsync(q=>q.Id==send,CancellationToken.None);
            if(q.State!=NotificationState.Sending||q.SenderInstance!=runtime.InstanceId)return;
            q.FinishedUtc=Now;
            var destination=await db.DiscordDestinations.SingleOrDefaultAsync(CancellationToken.None);
            if(response.State==DiscordAcknowledgment.RateLimited&&destination is not null)destination.NextSendUtc=Now.AddSeconds(response.RetryAfterSeconds??60);
            switch(response.State){case DiscordAcknowledgment.Delivered:q.State=NotificationState.Delivered;q.MessageId=response.MessageId!;q.Reason="Discord acknowledged this message. Not scientific approval.";break;
                case DiscordAcknowledgment.RateLimited:if(q.Attempts<DiscordWebhookPolicy.MaximumAttempts){q.State=NotificationState.Pending;q.DueUtc=Now.AddSeconds(Math.Max(response.RetryAfterSeconds??60,Math.Pow(2,q.Attempts)));q.FinishedUtc=null;q.Reason="Discord rate limit: waiting before a bounded retry.";}else{q.State=NotificationState.Failed;q.Reason="Discord rate limit retry budget exhausted. No further automatic attempts.";}break;
                case DiscordAcknowledgment.Rejected:q.State=NotificationState.Failed;if(destination is not null)destination.Enabled=false;q.Reason="Discord rejected the request. Destination disabled; check or replace the webhook. No automatic retry.";break;
                default:q.State=NotificationState.Ambiguous;q.Reason="No definitive acknowledgment. Discord may have received it; inspect the channel. No automatic retry.";break;}
            await db.SaveChangesAsync(CancellationToken.None);await tx.CommitAsync(CancellationToken.None);
        }
    }
}
