using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class DeviceGrantRecord
{
    public Guid Id {get;set;}
    public string ClientName {get;set;}="";
    public string DeviceHash {get;set;}="";
    public string Challenge {get;set;}="";
    public string UserCode {get;set;}="";
    public DateTimeOffset ExpiresUtc {get;set;}
    public DateTimeOffset NextPollUtc {get;set;}
    public Guid? UserId {get;set;}
    public string? SecurityStamp {get;set;}
    public string? CredentialHash {get;set;}
    public DateTimeOffset? CredentialExpiresUtc {get;set;}
    public bool Revoked {get;set;}
    public OrganizationDesktopPermission Permission {get;set;}
}

// Device-authorization-shaped, Identity-backed first-party approval; no OAuth/OIDC claim.
// No callback listener, browser bearer, password handoff, redirect or token in a URL.
public sealed class OrganizationDevices(OrganizationDatabase db,OrganizationMembership membership,OrganizationManagement history,UserManager<OrganizationUser> users,OrganizationSettings settings)
{
    public async Task<OrganizationDevicePending> Start(OrganizationDeviceStart request,CancellationToken ct)
    {
        if(!Enum.IsDefined(request.Permission)||request.OrganizationId!=(await membership.Public(ct)).Id||request.ClientName is not {Length:>=1 and <=80}||request.ClientName.Any(char.IsControl)||request.Challenge is not {Length:43}||request.Challenge.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('-' or '_')))throw new ArgumentException("Confirm this organization's identity and a supported desktop request.");
        var secret=OrganizationMembership.Secret();var code=Convert.ToHexString(RandomNumberGenerator.GetBytes(5));code=code[..5]+"-"+code[5..];
        var row=new DeviceGrantRecord{Permission=request.Permission,Id=Guid.NewGuid(),ClientName=request.ClientName,DeviceHash=OrganizationMembership.Digest(secret),UserCode=code,Challenge=request.Challenge,ExpiresUtc=OrganizationTime.UtcNow.AddMinutes(10)};
        db.DeviceGrants.Add(row);await db.SaveChangesAsync(ct);
        return new(row.Id,secret,code,settings.Origin.GetLeftPart(UriPartial.Authority)+"/#connect="+row.Id,row.ExpiresUtc,5,row.Permission);
    }
    public async Task<object> Review(Guid id,ClaimsPrincipal actor,CancellationToken ct)
    {
        if((await membership.Current(actor,ct))?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        var row=await db.DeviceGrants.AsNoTracking().SingleOrDefaultAsync(g=>g.Id==id,ct);
        if(row is null||row.Revoked||row.ExpiresUtc<=OrganizationTime.UtcNow||row.UserId is not null)throw new OrganizationConflict("This desktop request is expired, unavailable or already handled. Start again in AeroScience.");
        return new{organization=await membership.Public(ct),row.ClientName,row.UserCode,row.ExpiresUtc,permission=row.Permission,scope=row.Permission==OrganizationDesktopPermission.SubmitEvidence?"Submit only your explicitly reviewed and approved research packages; read your receipts. Selected locations, timestamps, original numeric evidence and notes may be shared. Drafts grant no upload permission. No claim management, organization administration or simulator control.":row.Permission==OrganizationDesktopPermission.ManageOwnClaims?"Read published opportunities; claim, accept and release your own assignments. No management, flight preparation, uploads or simulator control.":"Read published programs and campaigns only. No administration, flights, locations or scientific data transfer."};
    }
    public async Task<object> Approve(OrganizationDeviceApproval request,ClaimsPrincipal actor,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
        var current=await membership.Current(actor,ct);if(current?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        if(!request.ConfirmCode||request.OrganizationId!=(await membership.Public(ct)).Id)throw new ArgumentException("Compare the code with AeroScience and confirm this organization before approving.");
        var row=await db.DeviceGrants.SingleOrDefaultAsync(g=>g.Id==request.Id,ct)??throw new OrganizationNotFound();
        if(row.Revoked||row.ExpiresUtc<=OrganizationTime.UtcNow||row.UserId is not null)throw new OrganizationConflict("This connection request is no longer available. Start again in AeroScience.");
        if(request.Approve&&row.Permission==OrganizationDesktopPermission.ManageOwnClaims&&!request.ConfirmClaimManagement)throw new ArgumentException("Explicitly authorize management of your own claims, or deny this connection.");
        if(request.Approve&&row.Permission==OrganizationDesktopPermission.SubmitEvidence&&!request.ConfirmSubmission)throw new ArgumentException("Explicitly authorize selective evidence submission, or deny this connection. Older grants cannot upload evidence.");
        var user=await users.GetUserAsync(actor)??throw new OrganizationForbidden();if(actor.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType)!=await users.GetSecurityStampAsync(user))throw new OrganizationForbidden();row.UserId=user.Id;row.SecurityStamp=await users.GetSecurityStampAsync(user);row.Revoked=!request.Approve;
        history.Audit(user.Id,user.UserName!,request.Approve?row.Permission==OrganizationDesktopPermission.SubmitEvidence?"desktop-approved-submit-evidence":row.Permission==OrganizationDesktopPermission.ManageOwnClaims?"desktop-approved-manage-own-claims":"desktop-approved-read-published":"desktop-denied",row.Id);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new{message=request.Approve?"Desktop connection approved. Return to AeroScience and select Finish Connection.":"Desktop connection denied."};
    }
    public async Task<OrganizationDeviceToken> Poll(OrganizationDevicePoll request,CancellationToken ct)
    {
        if(request.DeviceSecret is not {Length:64}||request.Verifier is not {Length:64}||request.Verifier.Any(c=>!char.IsAsciiHexDigit(c)))throw new OrganizationForbidden();
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
        var row=await db.DeviceGrants.SingleOrDefaultAsync(g=>g.Id==request.Id,ct);
        if(row is null||!OrganizationMembership.Matches(request.DeviceSecret,row.DeviceHash)||row.Challenge!=WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request.Verifier)))||request.OrganizationId!=(await membership.Public(ct)).Id)throw new OrganizationForbidden();
        if(row.Revoked||row.ExpiresUtc<=OrganizationTime.UtcNow)return new("expiredOrRevoked");
        if(row.NextPollUtc>OrganizationTime.UtcNow)return new("slowDown");
        row.NextPollUtc=OrganizationTime.UtcNow.AddSeconds(5);
        if(row.UserId is null){await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new("pending");}
        var user=await users.FindByIdAsync(row.UserId.ToString()!);var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==row.UserId,ct);
        if(user is null||member?.State!=OrganizationMembershipState.Active||row.SecurityStamp!=await users.GetSecurityStampAsync(user))return new("expiredOrRevoked");
        // Idempotent delivery after a lost reply; no raw bearer is stored, including receipts.
        var bearer=OrganizationMembership.Digest((row.Permission switch{OrganizationDesktopPermission.ReadPublished=>"AeroScience/read-published/v1\0",OrganizationDesktopPermission.ManageOwnClaims=>"AeroScience/manage-own-claims/v1\0",OrganizationDesktopPermission.SubmitEvidence=>"AeroScience/submit-evidence/v1\0",_=>throw new OrganizationForbidden()})+row.Id+"\0"+request.DeviceSecret+"\0"+request.Verifier);
        row.CredentialHash??=OrganizationMembership.Digest(bearer);row.CredentialExpiresUtc??=OrganizationTime.UtcNow.AddHours(24);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new("approved",row.Id,bearer,row.CredentialExpiresUtc,row.Permission);
    }
    internal async Task<DeviceGrantRecord> Authorize(HttpContext c,CancellationToken ct,bool requireClaims=false,bool requireSubmission=false)
    {
        var header=c.Request.Headers.Authorization.ToString();
        if(!header.StartsWith("Bearer ",StringComparison.Ordinal)||header.Length!=71)throw new OrganizationUnauthorized();
        var credential=header[7..];if(credential.Any(ch=>!char.IsAsciiHexDigit(ch)))throw new OrganizationUnauthorized();
        var hash=OrganizationMembership.Digest(credential);var row=await db.DeviceGrants.AsNoTracking().SingleOrDefaultAsync(g=>g.CredentialHash==hash,ct);
        if(row is null||row.Revoked||row.CredentialExpiresUtc<=OrganizationTime.UtcNow||row.UserId is null)throw new OrganizationUnauthorized();
        if(requireClaims&&row.Permission!=OrganizationDesktopPermission.ManageOwnClaims)throw new OrganizationForbidden();
        if(requireSubmission&&row.Permission!=OrganizationDesktopPermission.SubmitEvidence)throw new OrganizationForbidden();
        var user=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.Id==row.UserId,ct);var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==row.UserId,ct);
        if(user is null||member?.State!=OrganizationMembershipState.Active||row.SecurityStamp!=user.SecurityStamp)throw new OrganizationUnauthorized();return row;
    }
    public async Task<object> Disconnect(HttpContext c,CancellationToken ct)
    {
        var row=await Authorize(c,ct);await db.DeviceGrants.Where(g=>g.Id==row.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true),ct);history.Audit(row.UserId!.Value,"desktop","desktop-disconnected",row.Id);await db.SaveChangesAsync(ct);return new{message="Desktop authorization revoked."};
    }
    public async Task<OrganizationMemberInformation> Published(Guid userId,int offset,CancellationToken ct)
    {
        if(offset<0)throw new ArgumentException("Choose a valid published campaign page.");
        var member=await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==userId,ct);
        if(member?.State!=OrganizationMembershipState.Active)throw new OrganizationForbidden();
        var campaigns=await db.Campaigns.AsNoTracking().Where(c=>c.PublishedRevision!=null).OrderBy(c=>c.CreatedUtc).ThenBy(c=>c.Id).Skip(offset).Take(21).ToArrayAsync(ct);
        var result=new List<OrganizationPublishedCampaign>();var manager=new OrganizationManagement(db);
        foreach(var c in campaigns.Take(20)){
            var row=await manager.ReadRevision(c.Id,c.PublishedRevision!.Value,ct);var snapshot=System.Text.Json.JsonSerializer.Deserialize<OrganizationCampaignView>(row.SnapshotJson,WireJson.Options)!;
            // Pin the exact program revision, independent of wall-clock order across server instances.
            // Historical P13 publications did not capture this identity. Never guess it or expose a later draft.
            var programName="Program metadata unavailable for this historical publication";
            if(snapshot.ProgramRevision is {} pinned){
                var verified=await manager.ReadRevision(c.ProgramId,pinned,ct);
                if(verified.Kind!="program")throw new InvalidOperationException("Published program revision is incompatible.");
                programName=System.Text.Json.JsonSerializer.Deserialize<OrganizationProgramView>(verified.SnapshotJson,WireJson.Options)!.Name;
            }
            result.Add(new(c.Id,c.ProgramId,programName,snapshot.Revision,snapshot.Name,snapshot.Description,snapshot.Objective,snapshot.Focus,snapshot.Definition.Template.Id));
        }
        return new(await membership.Public(ct),member.Role,member.State,new(1,result.ToArray(),campaigns.Length>20?checked(offset+20):null));
    }
}
public sealed class OrganizationUnauthorized:Exception;
