using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

public sealed class OwnerSetupRecord
{
    public Guid Id {get;set;}
    public string ChallengeJson {get;set;}="";
    // Identity's salted, iterated hash, never the submitted password or signing key.
    public string PasswordHash {get;set;}="";
    public DateTimeOffset ExpiresUtc {get;set;}
    public DateTimeOffset? ConsumedUtc {get;set;}
}
public sealed class OrganizationOwnerSetup(OrganizationDatabase db,OrganizationSettings settings,UserManager<OrganizationUser> users,RoleManager<IdentityRole<Guid>> roles,OrganizationManagement management)
{
    public async Task<OrganizationOwnerChallenge> Begin(OrganizationOwnerRequest r,CancellationToken ct)
    {
        RequireConfigured();OrganizationApplication.ValidateIdentity(r.UserName,r.Password);
        if(string.IsNullOrWhiteSpace(r.OrganizationName)||r.OrganizationName.Length>120||r.OrganizationName.Any(char.IsControl)||r.Description is null||r.Description.Length>4000||r.Description.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))throw new ArgumentException("Enter a short organization name and description.");
        var user=new OrganizationUser{Id=Guid.NewGuid(),UserName=r.UserName};
        foreach(var validator in users.PasswordValidators)if(!(await validator.ValidateAsync(users,user,r.Password)).Succeeded)throw new ArgumentException("Choose a password with at least 14 characters, upper/lowercase letters, a number and a symbol.");
        var passwordHash=users.PasswordHasher.HashPassword(user,r.Password);
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7013001)",ct);
        if(await db.Organizations.AnyAsync(ct))throw new OrganizationConflict("Organization ownership is already established. Sign in instead.");
        // Only expired, unconsumed passwords are removed; original completed audit remains.
        await db.OwnerSetups.Where(s=>s.ConsumedUtc==null&&s.ExpiresUtc<=DateTimeOffset.UtcNow).ExecuteDeleteAsync(ct);
        if(await db.OwnerSetups.CountAsync(s=>s.ConsumedUtc==null,ct)>=32)throw new OrganizationConflict("Setup requests are busy. Wait five minutes and try again.");
        var payload=OrganizationDeployment.Digest(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{r.OrganizationName,r.Description,r.UserName,passwordHash})));
        var challenge=new OrganizationOwnerChallenge(Guid.NewGuid(),settings.OwnerIntentId,settings.Origin.GetLeftPart(UriPartial.Authority),settings.OwnerProjectId,settings.OwnerEnvironmentId,settings.OwnerServiceId,OrganizationOwnerProtocol.Fingerprint(settings.OwnerPublicKey!),r.OrganizationName.Trim(),r.UserName,payload,Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()),"pending");
        // Description is part of the hashed exact account payload, retained only until consumed.
        db.OwnerSetups.Add(new(){Id=challenge.Id,ChallengeJson=JsonSerializer.Serialize(new OwnerPayload(challenge,r.Description.Trim())),PasswordHash=passwordHash,ExpiresUtc=challenge.ExpiresUtc});
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return challenge;
    }
    sealed record OwnerPayload(OrganizationOwnerChallenge Challenge,string Description);
    void RequireConfigured(){if(!OrganizationOwnerProtocol.ValidPublicKey(settings.OwnerPublicKey)||settings.OwnerIntentId==Guid.Empty)throw new OrganizationForbidden();}
    async Task<(OwnerSetupRecord Row,OwnerPayload Payload)> Read(Guid id,CancellationToken ct)
    {
        RequireConfigured();var row=await db.OwnerSetups.SingleOrDefaultAsync(s=>s.Id==id,ct)??throw new OrganizationNotFound();
        var payload=JsonSerializer.Deserialize<OwnerPayload>(row.ChallengeJson)??throw new OrganizationForbidden();var c=payload.Challenge;
        if(c.IntentId!=settings.OwnerIntentId||c.Origin!=settings.Origin.GetLeftPart(UriPartial.Authority)||c.ProjectId!=settings.OwnerProjectId||c.EnvironmentId!=settings.OwnerEnvironmentId||c.ServiceId!=settings.OwnerServiceId||c.PublicKeySha256!=OrganizationOwnerProtocol.Fingerprint(settings.OwnerPublicKey!))throw new OrganizationForbidden();
        return(row,payload);
    }
    public async Task<OrganizationOwnerChallenge> Inspect(Guid id,CancellationToken ct)
    {
        var(row,payload)=await Read(id,ct);return payload.Challenge with{State=row.ConsumedUtc is not null?"completed":row.ExpiresUtc<=DateTimeOffset.UtcNow?"expired":"pending"};
    }
    public async Task<object> Approve(OrganizationOwnerApproval approval,CancellationToken ct)
    {
        await using var tx=await db.Database.BeginTransactionAsync(ct);await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7013001)",ct);
        var(row,payload)=await Read(approval.Id,ct);var challenge=payload.Challenge;
        if(!OrganizationOwnerProtocol.Verify(settings.OwnerPublicKey!,challenge,approval.Signature))throw new OrganizationForbidden();
        // Lost acknowledgement can retrieve the one outcome; never reapply the command.
        if(row.ConsumedUtc is not null)return new{state="completed"};
        if(row.ExpiresUtc<=DateTimeOffset.UtcNow)throw new OrganizationForbidden();
        if(await db.Organizations.AnyAsync(ct))throw new OrganizationConflict("Another owner request completed first. Sign in with that administrator account.");
        foreach(var role in OrganizationRoles.All){var made=await roles.CreateAsync(new(role){Id=Guid.NewGuid()});if(!made.Succeeded)throw new InvalidOperationException("Role initialization failed.");}
        var admin=new OrganizationUser{Id=Guid.NewGuid(),UserName=challenge.UserName,PasswordHash=row.PasswordHash,SecurityStamp=Guid.NewGuid().ToString()};
        if(!(await users.CreateAsync(admin)).Succeeded||!(await users.AddToRoleAsync(admin,OrganizationRoles.Administrator)).Succeeded)throw new InvalidOperationException("Owner account could not be initialized.");
        var org=new OrganizationRecord{PublicId=Guid.NewGuid(),Name=challenge.OrganizationName,Description=payload.Description,AdministratorId=admin.Id,CreatedUtc=OrganizationTime.UtcNow};
        db.Organizations.Add(org);management.Revision(org.PublicId,1,"enrollment-policy",new OrganizationEnrollmentPolicy(1,1,OrganizationEnrollmentMode.InvitationOnly,true),admin.Id);
        db.Memberships.Add(new(){UserId=admin.Id,Role=OrganizationRoles.Administrator,State=OrganizationMembershipState.Active,UpdatedUtc=OrganizationTime.UtcNow});management.Audit(admin.Id,admin.UserName!,"owner-pairing-completed",org.PublicId);
        row.ConsumedUtc=DateTimeOffset.UtcNow;row.PasswordHash="";
        await db.OwnerSetups.Where(s=>s.Id!=row.Id&&s.ConsumedUtc==null).ExecuteDeleteAsync(ct);
        var deployment=await db.DeploymentState.SingleOrDefaultAsync(ct);if(deployment is not null)deployment.BootstrapConsumedUtc=DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return new{state="completed"};
    }
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/owner/setup",(OrganizationOwnerRequest r,OrganizationOwnerSetup setup,CancellationToken ct)=>setup.Begin(r,ct)).RequireRateLimiting("identity");
        app.MapGet("/api/owner/setup/{id:guid}",(Guid id,OrganizationOwnerSetup setup,CancellationToken ct)=>setup.Inspect(id,ct)).RequireRateLimiting("public");
        // Native companion only; cookie/Origin requests are refused by the common boundary.
        app.MapPost("/api/owner/approve",(OrganizationOwnerApproval r,OrganizationOwnerSetup setup,CancellationToken ct)=>setup.Approve(r,ct)).RequireRateLimiting("identity");
    }
}
