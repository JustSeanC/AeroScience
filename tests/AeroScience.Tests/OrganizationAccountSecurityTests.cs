using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace AeroScience.Tests;
public sealed class OrganizationAuthenticatorTests
{
    [Fact]public void ReplayCounterMatchesIndependentRfc6238VectorAndRejectsWrongCode(){const string key="GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";Assert.Equal(1,OrganizationAccountSecurity.AuthenticatorStep(key,"287082",DateTimeOffset.FromUnixTimeSeconds(59)));Assert.Equal(-1,OrganizationAccountSecurity.AuthenticatorStep(key,"000000",DateTimeOffset.FromUnixTimeSeconds(59)));}
}
public sealed partial class OrganizationServerTests
{
    async Task RequireMfa(){await app.StopAsync();await app.DisposeAsync();requireAdministratorMfa=true;await Start();browser.Connect(app);await browser.Session();}
    internal static string AuthenticatorCode(string key,long step)
    {
        var bytes=new List<byte>();var buffer=0;var bits=0;foreach(var c in key){var v="ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(c);buffer=(buffer<<5)|v;bits+=5;if(bits>=8){bits-=8;bytes.Add((byte)(buffer>>bits));}}
        var counter=new byte[8];BinaryPrimitives.WriteInt64BigEndian(counter,step);var h=HMACSHA1.HashData(bytes.ToArray(),counter);var offset=h[^1]&15;return ((BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(offset,4))&0x7fffffff)%1000000).ToString("D6",System.Globalization.CultureInfo.InvariantCulture);
    }
    async Task<(string Key,string[] Codes,long Step)> SecureAdministrator()
    {
        await RequireMfa();await Bootstrap();await Login();var begin=await Value<JsonElement>(await browser.Send("/api/account-security/enroll",new AccountEnrollment(Password)));var key=begin.GetProperty("setupKey").GetString()!;var step=DateTimeOffset.UtcNow.ToUnixTimeSeconds()/30+1;
        var enabled=await Value<JsonElement>(await browser.Send("/api/account-security/enable",new AccountCode(AuthenticatorCode(key,step))));var codes=enabled.GetProperty("recoveryCodes").EnumerateArray().Select(v=>v.GetString()!).ToArray();await browser.Session();return(key,codes,step);
    }
    [PostgresFact]public async Task RequiredAdministratorCannotBypassEnrollmentAndActualSingleUseRecoveryConfirmation()
    {
        var enrollment=await SecureAdministrator();Assert.True((await browser.Session()).GetProperty("accountSecurityRequired").GetBoolean());Assert.Equal(HttpStatusCode.PreconditionRequired,(await browser.Client.GetAsync("/api/organization/templates")).StatusCode);
        await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(enrollment.Codes[0])));Assert.False((await browser.Session()).GetProperty("accountSecurityRequired").GetBoolean());Assert.Equal(HttpStatusCode.OK,(await browser.Client.GetAsync("/api/organization/templates")).StatusCode);
        var view=await browser.Client.GetFromJsonAsync<AccountSecurityView>("/api/account-security");Assert.True(view!.SessionVerified);Assert.Equal(9,view.RecoveryCodesRemaining);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();foreach(var value in await db.UserTokens.Select(t=>t.Value!).ToArrayAsync()){Assert.StartsWith("protected-v1:",value);Assert.DoesNotContain(enrollment.Key,value);foreach(var c in enrollment.Codes)Assert.DoesNotContain(c,value);}
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(enrollment.Codes[0]))).StatusCode);
    }
    [PostgresFact]public async Task PasswordAloneNeverGrantsMfaSessionAndRecoveryRevokesEarlierSessions()
    {
        var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));
        using var other=new Browser();other.Connect(app);await other.Session();var password=await Value<JsonElement>(await other.Send("/api/sign-in",new OrganizationLoginRequest("administrator",Password)));Assert.True(password.GetProperty("twoFactorRequired").GetBoolean());await other.Session();Assert.Equal(HttpStatusCode.Unauthorized,(await other.Client.GetAsync("/api/organization/templates")).StatusCode);
        await Value<JsonElement>(await other.Send("/api/sign-in/second-factor",new AccountCode(e.Codes[1],true)));await other.Session();Assert.Equal(HttpStatusCode.OK,(await other.Client.GetAsync("/api/organization/templates")).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync("/api/organization/templates")).StatusCode);
        using var scope=app.Services.CreateScope();Assert.Contains(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Audit.ToArrayAsync(),a=>a.Action=="account-recovered");
    }
    [PostgresFact]public async Task AuthenticatorReplayAndConcurrentRecoveryReuseAreDenied()
    {
        var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));
        using var first=new Browser();first.Connect(app);await first.Session();await Value<JsonElement>(await first.Send("/api/sign-in",new OrganizationLoginRequest("administrator",Password)));await first.Session();Assert.Equal(HttpStatusCode.Unauthorized,(await first.Send("/api/sign-in/second-factor",new AccountCode(AuthenticatorCode(e.Key,e.Step)))).StatusCode);
        using var second=new Browser();second.Connect(app);await second.Session();await Value<JsonElement>(await second.Send("/api/sign-in",new OrganizationLoginRequest("administrator",Password)));await second.Session();var replies=await Task.WhenAll(first.Send("/api/sign-in/second-factor",new AccountCode(e.Codes[1],true)),second.Send("/api/sign-in/second-factor",new AccountCode(e.Codes[1],true)));Assert.Single(replies,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(replies,r=>r.StatusCode==HttpStatusCode.Unauthorized);
    }
    [PostgresFact]public async Task SecureIdentityAndEncryptedRecoverySurviveAnActualServerRestart()
    {
        var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);Assert.False((await browser.Session()).GetProperty("accountSecurityRequired").GetBoolean());Assert.Equal(HttpStatusCode.OK,(await browser.Client.GetAsync("/api/organization/templates")).StatusCode);
        using var fresh=new Browser();fresh.Connect(app);await fresh.Session();await Value<JsonElement>(await fresh.Send("/api/sign-in",new OrganizationLoginRequest("administrator",Password)));await fresh.Session();await Value<JsonElement>(await fresh.Send("/api/sign-in/second-factor",new AccountCode(e.Codes[1],true)));await fresh.Session();Assert.Equal(HttpStatusCode.OK,(await fresh.Client.GetAsync("/api/organization/templates")).StatusCode);
    }
}

public sealed partial class OrganizationServerTests
{
    [PostgresFact]public async Task ServerProtectionNeverGrantsPasswordOnlyOrPilotAdministration()
    {
        await Bootstrap();await Login();Assert.Equal(HttpStatusCode.Forbidden,(await browser.Client.GetAsync("/api/organization/operations")).StatusCode);
        var account=await JoinPilot("protection-pilot",InvitationSecret(await Invite()));try{Assert.Equal(HttpStatusCode.Forbidden,(await account.Client.GetAsync("/api/organization/operations")).StatusCode);}finally{account.Dispose();}
    }
    [PostgresFact]public async Task VerifiedAdministratorSeesHonestUnconfiguredRecoveryWithoutLeakingPrivateSettings()
    {
        var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));
        var status=await Value<ServerOperationsView>(await browser.Client.GetAsync("/api/organization/operations"));Assert.False(status.Configured);Assert.Empty(status.Backups);Assert.True(status.Storage.DatabaseBytes>0);var text=JsonSerializer.Serialize(status);Assert.DoesNotContain(connection,text);Assert.DoesNotContain(e.Key,text);Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/organization/operations/commands",new ServerOperationCommand(Guid.NewGuid(),ServerOperationKind.Backup,null,Password,true))).StatusCode);
    }
}
