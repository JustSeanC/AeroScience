using System.Buffers.Binary;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

// Same ASP.NET Core Identity token store; encrypt authenticator/recovery material with the existing persistent key ring.
public sealed class ProtectedOrganizationUserStore(OrganizationDatabase db,IDataProtectionProvider protection):UserStore<OrganizationUser,IdentityRole<Guid>,OrganizationDatabase,Guid>(db)
{
    IDataProtector Protector(OrganizationUser user,string provider,string name)=>protection.CreateProtector("AeroScience.Organization.IdentityTokens.v1",user.Id.ToString(),provider,name);
    public override Task SetTokenAsync(OrganizationUser user,string loginProvider,string name,string? value,CancellationToken cancellationToken=default)=>base.SetTokenAsync(user,loginProvider,name,value is null?null:"protected-v1:"+Protector(user,loginProvider,name).Protect(value),cancellationToken);
    public override async Task<string?> GetTokenAsync(OrganizationUser user,string loginProvider,string name,CancellationToken cancellationToken=default)
    {
        var value=await base.GetTokenAsync(user,loginProvider,name,cancellationToken);
        if(value is null)return null;
        if(!value.StartsWith("protected-v1:",StringComparison.Ordinal))throw new InvalidOperationException("Identity token protection is incompatible; no unprotected credential fallback was used.");
        return Protector(user,loginProvider,name).Unprotect(value[13..]);
    }
}
public sealed class AccountSecurityDenied():Exception("Verification was not accepted. Use a fresh authenticator code or an unused saved recovery code. If the account is locked, wait 15 minutes.");
public sealed record AccountPassword(string Password);
public sealed record AccountEnrollment(string Password,bool ReplaceExisting=false);
public sealed record AccountCode(string Code,bool Recovery=false);
public sealed record AccountRecoveryConfirmation(string Code);
public sealed record AccountSecurityView(bool Required,bool Enabled,bool RecoverySaved,int RecoveryCodesRemaining,bool SessionVerified,string[] Limitations);
public sealed class OrganizationAccountSecurity(OrganizationDatabase db,UserManager<OrganizationUser> users,SignInManager<OrganizationUser> signIn,OrganizationManagement history,OrganizationSettings settings)
{
    public const string MfaClaim="organization-mfa-utc";
    public static bool Verified(ClaimsPrincipal principal)=>long.TryParse(principal.FindFirstValue(MfaClaim),NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()-value is >=0 and <=28800;
    public static bool Recent(ClaimsPrincipal principal)=>Verified(principal)&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()-long.Parse(principal.FindFirstValue(MfaClaim)!,CultureInfo.InvariantCulture)<=600;
    public static bool Ready(OrganizationUser user)=>user.TwoFactorEnabled&&user.RecoveryAcknowledgedUtc is not null;
    async Task<OrganizationUser> Current(ClaimsPrincipal actor,CancellationToken ct)
    {
        var user=await users.GetUserAsync(actor)??throw new AccountSecurityDenied();
        if(!await db.Memberships.AnyAsync(m=>m.UserId==user.Id&&m.State==AeroScience.Shared.OrganizationMembershipState.Active,ct))throw new OrganizationForbidden();
        return user;
    }
    public async Task<AccountSecurityView> View(ClaimsPrincipal actor,CancellationToken ct)
    {
        var user=await Current(actor,ct);return new(settings.RequireAdministratorMfa&&await users.IsInRoleAsync(user,"Administrator"),user.TwoFactorEnabled,user.RecoveryAcknowledgedUtc is not null,user.TwoFactorEnabled?await users.CountRecoveryCodesAsync(user):0,Verified(actor),["Authenticator codes and recovery codes protect this organization's account only. No pilot evidence is shared by setup.","Passkeys are not implemented; use a supported authenticator and preserve the independent recovery codes."]);
    }
    public async Task<object> Begin(AccountEnrollment request,HttpContext context)
    {
        await using var tx=await db.Database.BeginTransactionAsync(context.RequestAborted);await Lock(context.RequestAborted);
        var user=await Current(context.User,context.RequestAborted);
        if(user.TwoFactorEnabled&&(!request.ReplaceExisting||!Recent(context.User)))throw new OrganizationForbidden();
        var checkedPassword=await signIn.CheckPasswordSignInAsync(user,request.Password,lockoutOnFailure:true);
        if(!checkedPassword.Succeeded){await tx.CommitAsync(context.RequestAborted);throw new AccountSecurityDenied();}
        if(user.TwoFactorEnabled){await Check(users.SetTwoFactorEnabledAsync(user,false));user.RecoveryAcknowledgedUtc=null;await Revoke(user,context.RequestAborted);}
        await Check(users.ResetAuthenticatorKeyAsync(user));var key=await users.GetAuthenticatorKeyAsync(user)??throw new InvalidOperationException("Authenticator setup unavailable.");
        user.AuthenticatorLastStep=-1;user.RecoveryAcknowledgedUtc=null;await Check(users.UpdateAsync(user));history.Audit(user.Id,user.UserName!,"authenticator-setup-started",user.Id);await db.SaveChangesAsync(context.RequestAborted);await tx.CommitAsync(context.RequestAborted);
        await signIn.SignInAsync(user,false);return new{setupKey=key,account=user.UserName,issuer="AeroScience Organization",message="Add this account in an authenticator using the private setup key. Then enter its current six-digit code. The key is never placed in a URL."};
    }
    public async Task<object> Enable(AccountCode request,HttpContext context)
    {
        await using var tx=await db.Database.BeginTransactionAsync(context.RequestAborted);await Lock(context.RequestAborted);var user=await Current(context.User,context.RequestAborted);
        if(user.TwoFactorEnabled||request.Recovery||!await ValidTotp(user,request.Code)){await Failed(user,context.RequestAborted);await tx.CommitAsync(context.RequestAborted);throw new AccountSecurityDenied();}
        await Check(users.SetTwoFactorEnabledAsync(user,true));var codes=(await users.GenerateNewTwoFactorRecoveryCodesAsync(user,10))?.ToArray()??throw new InvalidOperationException("Recovery setup unavailable.");
        await Revoke(user,context.RequestAborted);history.Audit(user.Id,user.UserName!,"authenticator-enabled-recovery-pending",user.Id);await db.SaveChangesAsync(context.RequestAborted);await tx.CommitAsync(context.RequestAborted);await VerifiedSignIn(user);
        return new{recoveryCodes=codes,message="Save these recovery codes somewhere separate from this server. They are shown only now. Enter one saved code to confirm recovery works."};
    }
    public async Task<object> ConfirmRecovery(AccountRecoveryConfirmation request,HttpContext context)
    {
        if(!Recent(context.User))throw new OrganizationForbidden();await using var tx=await db.Database.BeginTransactionAsync(context.RequestAborted);await Lock(context.RequestAborted);var user=await Current(context.User,context.RequestAborted);
        if(!user.TwoFactorEnabled||!await Redeem(user,request.Code)){await Failed(user,context.RequestAborted);await tx.CommitAsync(context.RequestAborted);throw new AccountSecurityDenied();}
        user.RecoveryAcknowledgedUtc=DateTimeOffset.UtcNow;await Check(users.UpdateAsync(user));history.Audit(user.Id,user.UserName!,"recovery-code-verified",user.Id);await db.SaveChangesAsync(context.RequestAborted);await tx.CommitAsync(context.RequestAborted);return new{message="Account security is ready. The confirmation code was consumed; your other saved codes remain available."};
    }
    public async Task<object> Regenerate(AccountPassword request,HttpContext context)
    {
        if(!Recent(context.User))throw new OrganizationForbidden();await using var tx=await db.Database.BeginTransactionAsync(context.RequestAborted);await Lock(context.RequestAborted);var user=await Current(context.User,context.RequestAborted);
        if(!user.TwoFactorEnabled||!(await signIn.CheckPasswordSignInAsync(user,request.Password,true)).Succeeded){await tx.CommitAsync(context.RequestAborted);throw new AccountSecurityDenied();}
        user.RecoveryAcknowledgedUtc=null;var codes=(await users.GenerateNewTwoFactorRecoveryCodesAsync(user,10))!.ToArray();await Revoke(user,context.RequestAborted);history.Audit(user.Id,user.UserName!,"recovery-codes-replaced",user.Id);await db.SaveChangesAsync(context.RequestAborted);await tx.CommitAsync(context.RequestAborted);await VerifiedSignIn(user);return new{recoveryCodes=codes,message="Old recovery codes no longer work. Save these new codes, then confirm one."};
    }
    public async Task<object> CompleteSignIn(AccountCode request,HttpContext context)
    {
        await using var tx=await db.Database.BeginTransactionAsync(context.RequestAborted);await Lock(context.RequestAborted);
        var user=await signIn.GetTwoFactorAuthenticationUserAsync()??throw new AccountSecurityDenied();
        if(await users.IsLockedOutAsync(user)||!await db.Memberships.AnyAsync(m=>m.UserId==user.Id&&m.State==AeroScience.Shared.OrganizationMembershipState.Active,context.RequestAborted))throw new AccountSecurityDenied();
        var accepted=request.Recovery?await Redeem(user,request.Code):await ValidTotp(user,request.Code);
        if(!accepted){await Failed(user,context.RequestAborted);await tx.CommitAsync(context.RequestAborted);throw new AccountSecurityDenied();}
        await Check(users.ResetAccessFailedCountAsync(user));if(request.Recovery)await Revoke(user,context.RequestAborted);else await Check(users.UpdateAsync(user));
        history.Audit(user.Id,user.UserName!,request.Recovery?"account-recovered":"signed-in-with-authenticator",user.Id);await db.SaveChangesAsync(context.RequestAborted);await tx.CommitAsync(context.RequestAborted);
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);await VerifiedSignIn(user);return new{message=request.Recovery?"Signed in using a recovery code. Replace your authenticator if the old device was lost.":"Signed in securely."};
    }
    public async Task VerifyPassword(AccountPassword request,HttpContext context)
    {
        var user=await Current(context.User,context.RequestAborted);if(!(await signIn.CheckPasswordSignInAsync(user,request.Password,true)).Succeeded)throw new AccountSecurityDenied();
    }
    async Task<bool> ValidTotp(OrganizationUser user,string code)
    {
        if(code is not {Length:6}||code.Any(c=>!char.IsAsciiDigit(c))||await users.IsLockedOutAsync(user))return false;
        if(!await users.VerifyTwoFactorTokenAsync(user,TokenOptions.DefaultAuthenticatorProvider,code))return false;
        var key=await users.GetAuthenticatorKeyAsync(user);if(key is null)return false;var step=AuthenticatorStep(key,code,DateTimeOffset.UtcNow);
        if(step<=user.AuthenticatorLastStep)return false;user.AuthenticatorLastStep=step;return true;
    }
    public static long AuthenticatorStep(string key,string code,DateTimeOffset at)
    {
        var bytes=new List<byte>();var buffer=0;var bits=0;foreach(var c in key.ToUpperInvariant()){var digit="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);if(digit<0)throw new ArgumentException("Unsupported authenticator key.");buffer=(buffer<<5)|digit;bits+=5;if(bits>=8){bits-=8;bytes.Add((byte)(buffer>>bits));}}
        Span<byte> counter=stackalloc byte[8];for(var offset=-2;offset<=2;offset++){var step=at.ToUnixTimeSeconds()/30+offset;BinaryPrimitives.WriteInt64BigEndian(counter,step);var hash=HMACSHA1.HashData(bytes.ToArray(),counter);var i=hash[^1]&15;var value=(BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(i,4))&0x7fffffff)%1000000;if(CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value.ToString("D6",CultureInfo.InvariantCulture)),Encoding.ASCII.GetBytes(code)))return step;}
        return -1;
    }
    async Task<bool> Redeem(OrganizationUser user,string code)=>code is {Length:>=8 and <=64}&&!(await users.IsLockedOutAsync(user))&&(await users.RedeemTwoFactorRecoveryCodeAsync(user,code.Replace(" ","",StringComparison.Ordinal))).Succeeded;
    async Task Failed(OrganizationUser user,CancellationToken ct){await Check(users.AccessFailedAsync(user));history.Audit(user.Id,user.UserName!,"account-security-verification-failed",user.Id);await db.SaveChangesAsync(ct);}
    async Task Revoke(OrganizationUser user,CancellationToken ct){await Check(users.UpdateSecurityStampAsync(user));await db.DeviceGrants.Where(g=>g.UserId==user.Id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true),ct);}
    Task VerifiedSignIn(OrganizationUser user)=>signIn.SignInWithClaimsAsync(user,false,[new Claim(MfaClaim,DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]);
    static async Task Check(Task<IdentityResult> task){if(!(await task).Succeeded)throw new OrganizationConflict("Account security changed. Refresh and try again; no credential change was confirmed.");}
    Task Lock(CancellationToken ct)=>db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({OrganizationMembership.MembershipLock})",ct);
}
public static class OrganizationAccountSecurityApi
{
    public static void Map(WebApplication app)
    {
        var api=app.MapGroup("/api/account-security").RequireAuthorization();
        api.MapGet("",(OrganizationAccountSecurity security,HttpContext c)=>security.View(c.User,c.RequestAborted));
        api.MapPost("/enroll",(AccountEnrollment request,OrganizationAccountSecurity security,HttpContext c)=>security.Begin(request,c)).RequireRateLimiting("identity");
        api.MapPost("/enable",(AccountCode request,OrganizationAccountSecurity security,HttpContext c)=>security.Enable(request,c)).RequireRateLimiting("identity");
        api.MapPost("/confirm-recovery",(AccountRecoveryConfirmation request,OrganizationAccountSecurity security,HttpContext c)=>security.ConfirmRecovery(request,c)).RequireRateLimiting("identity");
        api.MapPost("/replace-recovery",(AccountPassword request,OrganizationAccountSecurity security,HttpContext c)=>security.Regenerate(request,c)).RequireRateLimiting("identity");
        app.MapPost("/api/sign-in/second-factor",(AccountCode request,OrganizationAccountSecurity security,HttpContext c)=>security.CompleteSignIn(request,c)).RequireRateLimiting("identity");
    }
}
