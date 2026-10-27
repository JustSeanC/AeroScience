using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class EnrollmentPolicyRecord
{
    public int Id {get;set;}=1;
    public int Revision {get;set;}=1;
    public OrganizationEnrollmentMode Mode {get;set;}
    public bool RequireApproval {get;set;}=true;
}
public sealed class MembershipRecord
{
    public Guid UserId {get;set;}
    public string Role {get;set;}=OrganizationRoles.Pilot;
    public OrganizationMembershipState State {get;set;}
    public int Revision {get;set;}=1;
    public DateTimeOffset UpdatedUtc {get;set;}
    public string Statement {get;set;}="";
    public int EnrollmentRevision {get;set;}
}
public sealed class InvitationRecord
{
    public Guid Id {get;set;}
    public string SecretHash {get;set;}="";
    public DateTimeOffset CreatedUtc {get;set;}
    public DateTimeOffset ExpiresUtc {get;set;}
    public DateTimeOffset? RevokedUtc {get;set;}
    public Guid? RedeemedBy {get;set;}
}

public sealed class OrganizationMembership(OrganizationDatabase db,UserManager<OrganizationUser> users,OrganizationManagement history,OrganizationSettings settings)
{
    internal const long MembershipLock=7014001;
    internal static string Secret()=>Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal static string Digest(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static bool Matches(string value,string digest)=>value.Length==64&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Digest(value)),Encoding.ASCII.GetBytes(digest));
    internal static Guid Actor(ClaimsPrincipal actor)=>Guid.Parse(actor.FindFirstValue(ClaimTypes.NameIdentifier)!);
    internal static OrganizationEnrollmentPolicy View(EnrollmentPolicyRecord row)=>new(1,row.Revision,row.Mode,row.RequireApproval);
    public async Task<OrganizationPublicIdentity> Public(CancellationToken ct)
    {
        var org=await db.Organizations.AsNoTracking().SingleOrDefaultAsync(ct)??throw new OrganizationNotFound();
        var policy=await db.EnrollmentPolicies.AsNoTracking().SingleAsync(ct);
        return new(1,org.PublicId,org.Name,org.Description,settings.Origin.GetLeftPart(UriPartial.Authority),View(policy));
    }
    public async Task<MembershipRecord?> Current(ClaimsPrincipal actor,CancellationToken ct)=>
        Guid.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier),out var id)?await db.Memberships.AsNoTracking().SingleOrDefaultAsync(m=>m.UserId==id,ct):null;
    internal async Task RequireManager(ClaimsPrincipal actor,bool administrator,CancellationToken ct)
    {
        var member=await Current(actor,ct);
        if(member?.State!=OrganizationMembershipState.Active||!(administrator?member.Role==OrganizationRoles.Administrator:member.Role is OrganizationRoles.Administrator or OrganizationRoles.Manager))throw new OrganizationForbidden();
    }
    static void Command(int version,Guid request){if(version!=1||request==Guid.Empty)throw new ArgumentException("Use a supported membership request and a new request identity.");}
    public async Task<OrganizationEnrollmentPolicy> Policy(OrganizationPolicyRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);
        if(!Enum.IsDefined(request.Mode))throw new ArgumentException("Choose a supported enrollment policy.");
        return await history.Mutate(actor,request.RequestId,new{operation="enrollment-policy",request},async(id,name)=>{
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MembershipLock})",ct);await RequireManager(actor,true,ct);
            var row=await db.EnrollmentPolicies.SingleAsync(ct);
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("Enrollment settings changed. Reload before saving.");
            row.Revision++;row.Mode=request.Mode;row.RequireApproval=request.RequireApproval;
            // Exact versioned policy history; changing policy never touches prior applications.
            var view=View(row);history.Revision((await db.Organizations.SingleAsync(ct)).PublicId,row.Revision,"enrollment-policy",view,id);
            history.Audit(id,name,"enrollment-policy-changed",(await db.Organizations.SingleAsync(ct)).PublicId,row.Revision);return view;
        },ct);
    }
    public async Task<OrganizationInvitationView> Invite(OrganizationInvitationRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);if(request.ExpiresHours is <1 or >168)throw new ArgumentException("Invitation lifetime must be 1–168 hours (48 hours by default).");
        string? secret=null;
        var result=await history.Mutate(actor,request.RequestId,new{operation="invitation",request},async(id,name)=>{
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MembershipLock})",ct);await RequireManager(actor,false,ct);
            secret=Secret();var row=new InvitationRecord{Id=Guid.NewGuid(),SecretHash=Digest(secret),CreatedUtc=OrganizationTime.UtcNow,ExpiresUtc=OrganizationTime.UtcNow.AddHours(request.ExpiresHours)};
            db.Invitations.Add(row);history.Audit(id,name,"invitation-created",row.Id);return InvitationView(row);
        },ct);
        // Only the first response contains the secret. Receipts/listings/logs never retain it.
        return secret is null?result:result with{Link=settings.Origin.GetLeftPart(UriPartial.Authority)+"/#join="+result.Id.ToString("N")+"."+secret};
    }
    static OrganizationInvitationView InvitationView(InvitationRecord r)=>new(r.Id,r.CreatedUtc,r.ExpiresUtc,r.RevokedUtc is not null?"Revoked":r.RedeemedBy is not null?"Used":r.ExpiresUtc<=OrganizationTime.UtcNow?"Expired":"Invited");
    public async Task<OrganizationPage<OrganizationInvitationView>> Invitations(int offset,CancellationToken ct)
    {
        if(offset<0)throw new ArgumentException("Choose a valid invitation page.");var rows=await db.Invitations.AsNoTracking().OrderByDescending(x=>x.CreatedUtc).ThenBy(x=>x.Id).Skip(offset).Take(21).ToArrayAsync(ct);
        return new(1,rows.Take(20).Select(InvitationView).ToArray(),rows.Length>20?checked(offset+20):null);
    }
    public Task<object> RevokeInvitation(Guid invitation,Guid requestId,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(1,requestId);return history.Mutate<object>(actor,requestId,new{operation="revoke-invitation",invitation},async(id,name)=>{
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MembershipLock})",ct);await RequireManager(actor,false,ct);
            var row=await db.Invitations.FindAsync([invitation],ct)??throw new OrganizationNotFound();
            if(row.RedeemedBy is not null)throw new OrganizationConflict("This invitation has already been used. Manage the pilot's membership instead.");
            row.RevokedUtc??=OrganizationTime.UtcNow;history.Audit(id,name,"invitation-revoked",invitation);return new{message="Invitation revoked."};
        },ct);
    }
    public async Task<object> Join(OrganizationJoinRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        if(request.Version!=1||!request.ConfirmOrganization||request.OrganizationId==Guid.Empty||request.Statement is null||request.Statement.Length>2000||request.Statement.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))throw new ArgumentException("Confirm the organization and enter an application of up to 2,000 characters.");
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MembershipLock})",ct);
        var org=await db.Organizations.SingleAsync(ct);if(org.PublicId!=request.OrganizationId)throw new OrganizationConflict("This link belongs to a different organization.");
        var policy=await db.EnrollmentPolicies.SingleAsync(ct);InvitationRecord? invitation=null;
        if(request.Invitation is {Length:>0}){
            var parts=request.Invitation.Split('.');
            if(parts.Length!=2||!Guid.TryParseExact(parts[0],"N",out var invitationId)||parts[1].Length!=64)throw new ArgumentException("This invitation is unavailable. Ask your organization for a new link.");
            invitation=await db.Invitations.FindAsync([invitationId],ct);
            if(invitation is null||!Matches(parts[1],invitation.SecretHash)||invitation.ExpiresUtc<=OrganizationTime.UtcNow||invitation.RevokedUtc is not null||invitation.RedeemedBy is not null)throw new OrganizationConflict("This invitation is unavailable. Ask your organization for a new link.");
        }else if(policy.Mode!=OrganizationEnrollmentMode.ApplicationsAllowed)throw new OrganizationForbidden();
        OrganizationUser user;
        if(actor.Identity?.IsAuthenticated==true)user=await users.GetUserAsync(actor)??throw new OrganizationForbidden();
        else{
            OrganizationApplication.ValidateIdentity(request.UserName,request.Password);
            user=new(){Id=Guid.NewGuid(),UserName=request.UserName};
            var result=await users.CreateAsync(user,request.Password);
            if(!result.Succeeded)throw new OrganizationConflict("Account creation was not accepted. Use a valid name and a strong password, or sign in to your existing account before joining.");
        }
        var member=await db.Memberships.SingleOrDefaultAsync(m=>m.UserId==user.Id,ct);
        if(member is not null){if(member.State is OrganizationMembershipState.Removed or OrganizationMembershipState.Suspended)throw new OrganizationConflict("Contact the administrator to restore this membership.");return new{message="Your membership or application is already recorded.",state=member.State};}
        var active=invitation is not null||!policy.RequireApproval;
        member=new(){UserId=user.Id,State=active?OrganizationMembershipState.Active:OrganizationMembershipState.PendingApproval,UpdatedUtc=OrganizationTime.UtcNow,EnrollmentRevision=policy.Revision,Statement=request.Statement.Trim()};
        db.Memberships.Add(member);if(invitation is not null)invitation.RedeemedBy=user.Id;
        if(!(await users.AddToRoleAsync(user,OrganizationRoles.Pilot)).Succeeded)throw new InvalidOperationException("Membership role could not be saved.");
        history.Audit(user.Id,user.UserName!,invitation is not null?"invitation-redeemed":active?"application-accepted-by-policy":"application-submitted",user.Id,member.Revision);
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new{message=active?"Membership active. Sign in to connect AeroScience.":"Application saved. An administrator or manager must approve your membership.",state=member.State};
    }
    public async Task<OrganizationPage<OrganizationMemberView>> Members(int offset,CancellationToken ct)
    {
        if(offset<0)throw new ArgumentException("Choose a valid member page.");
        var rows=await (from m in db.Memberships.AsNoTracking() join u in db.Users.AsNoTracking() on m.UserId equals u.Id orderby m.UpdatedUtc descending,m.UserId select new OrganizationMemberView(m.UserId,u.UserName!,m.Role,m.State,m.Revision,m.UpdatedUtc,m.Statement)).Skip(offset).Take(21).ToArrayAsync(ct);
        return new(1,rows.Take(20).ToArray(),rows.Length>20?checked(offset+20):null);
    }
    public Task<OrganizationMemberView> ChangeMember(Guid userId,OrganizationMemberRequest request,ClaimsPrincipal actor,CancellationToken ct)
    {
        Command(request.Version,request.RequestId);
        if(!OrganizationRoles.All.Contains(request.Role)||!Enum.IsDefined(request.State))throw new ArgumentException("Choose a supported role and membership state.");
        return history.Mutate<OrganizationMemberView>(actor,request.RequestId,new{operation="membership",userId,request},async(id,name)=>{
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({MembershipLock})",ct);await RequireManager(actor,false,ct);
            var caller=(await Current(actor,ct))!;var row=await db.Memberships.SingleOrDefaultAsync(m=>m.UserId==userId,ct)??throw new OrganizationNotFound();
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("This membership changed. Reload before saving.");
            if(caller.Role!=OrganizationRoles.Administrator&&(row.Role!=OrganizationRoles.Pilot||request.Role!=OrganizationRoles.Pilot||id==userId))throw new OrganizationForbidden();
            if(row.Role==OrganizationRoles.Administrator&&row.State==OrganizationMembershipState.Active&&(request.Role!=OrganizationRoles.Administrator||request.State!=OrganizationMembershipState.Active)&&await db.Memberships.CountAsync(m=>m.Role==OrganizationRoles.Administrator&&m.State==OrganizationMembershipState.Active,ct)<=1)throw new OrganizationConflict("Keep at least one active administrator so the organization remains manageable.");
            if(request.State==OrganizationMembershipState.PendingApproval&&row.State!=OrganizationMembershipState.PendingApproval)throw new ArgumentException("Only an original application can be pending approval.");
            var user=await users.FindByIdAsync(userId.ToString())??throw new OrganizationNotFound();
            var roles=await users.GetRolesAsync(user);if(!(await users.RemoveFromRolesAsync(user,roles)).Succeeded||!(await users.AddToRoleAsync(user,request.Role)).Succeeded||!(await users.UpdateSecurityStampAsync(user)).Succeeded)throw new InvalidOperationException("Permission revocation could not be confirmed.");
            row.Role=request.Role;row.State=request.State;row.Revision++;row.UpdatedUtc=OrganizationTime.UtcNow;
            // Every prior device grant/credential is revoked, including a pending approval.
            await db.DeviceGrants.Where(g=>g.UserId==userId).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true),ct);
            history.Audit(id,name,"membership-"+request.State.ToString().ToLowerInvariant()+"-"+request.Role.ToLowerInvariant(),userId,row.Revision);
            return new(userId,user.UserName!,row.Role,row.State,row.Revision,row.UpdatedUtc,row.Statement);
        },ct);
    }
}
public sealed class OrganizationForbidden:Exception;
