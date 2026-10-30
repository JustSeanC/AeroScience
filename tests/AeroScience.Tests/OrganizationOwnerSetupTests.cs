using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    async Task<ECDsa> PairOwner()
    {
        var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);deploymentOverrides=new(){["Organization:OwnerPublicKey"]=Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),["Organization:OwnerIntentId"]=Guid.NewGuid().ToString(),["Organization:OwnerProjectId"]=Guid.NewGuid().ToString(),["Organization:OwnerEnvironmentId"]=Guid.NewGuid().ToString(),["Organization:OwnerServiceId"]=Guid.NewGuid().ToString()};
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();return key;
    }
    static OrganizationOwnerApproval SignOwner(ECDsa key,OrganizationOwnerChallenge c)=>new(c.Id,Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(OrganizationOwnerProtocol.SignedText(c)),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    async Task<OrganizationOwnerChallenge> BeginOwner(string username="paired-owner")=>await Value<OrganizationOwnerChallenge>(await browser.Send("/api/owner/setup",new OrganizationOwnerRequest("Paired Organization","Synthetic fixture, not provider acceptance.",username,Password)));
    [PostgresFact]public async Task OwnerPairingRefusesAnonymousTakeoverTamperingReplayAndKeepsMfaPath()
    {
        using var key=await PairOwner();var c=await BeginOwner();
        Assert.True((await browser.Session()).GetProperty("ownerPairing").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/bootstrap",new OrganizationBootstrapRequest(secret,"Forged","","attacker",Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/owner/approve",SignOwner(key,c))).StatusCode); // A browser cookie/Origin is not native owner proof.
        using var native=app.GetTestServer().CreateClient();native.BaseAddress=new(Origin);
        using(var wrong=ECDsa.Create(ECCurve.NamedCurves.nistP256))Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/owner/approve",SignOwner(wrong,c),WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/owner/approve",SignOwner(key,c with{ProjectId=Guid.NewGuid().ToString()}),WireJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/owner/approve",SignOwner(key,c with{UserName="attacker"}),WireJson.Options)).StatusCode);
        var approval=SignOwner(key,c);(await native.PostAsJsonAsync("/api/owner/approve",approval,WireJson.Options)).EnsureSuccessStatusCode();
        (await native.PostAsJsonAsync("/api/owner/approve",approval,WireJson.Options)).EnsureSuccessStatusCode();
        await Login(name:"paired-owner");using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Single(await db.Organizations.ToArrayAsync());Assert.Single(await db.Users.ToArrayAsync());Assert.Single(await db.Audit.Where(a=>a.Action=="owner-pairing-completed").ToArrayAsync());Assert.Empty((await db.OwnerSetups.SingleAsync()).PasswordHash);
        Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/owner/setup",new OrganizationOwnerRequest("Other","","other",Password))).StatusCode);
    }
    [PostgresFact]public async Task OwnerPairingSurvivesProcessRestartAndOnlyOneConcurrentAccountWins()
    {
        using var key=await PairOwner();var a=await BeginOwner("first-owner");var b=await BeginOwner("second-owner");await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);
        using var native=app.GetTestServer().CreateClient();native.BaseAddress=new(Origin);var results=await Task.WhenAll(native.PostAsJsonAsync("/api/owner/approve",SignOwner(key,a),WireJson.Options),native.PostAsJsonAsync("/api/owner/approve",SignOwner(key,b),WireJson.Options));Assert.Single(results,r=>r.IsSuccessStatusCode);Assert.Single(results,r=>r.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(1,await db.Users.CountAsync());Assert.Equal(1,await db.Organizations.CountAsync());
    }
    [PostgresFact]public async Task OwnerPairingExpiredOrDifferentDeploymentApprovalIsRefused()
    {
        using var key=await PairOwner();var c=await BeginOwner();using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();(await db.OwnerSetups.SingleAsync()).ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();}
        using var native=app.GetTestServer().CreateClient();native.BaseAddress=new(Origin);Assert.Equal(HttpStatusCode.Forbidden,(await native.PostAsJsonAsync("/api/owner/approve",SignOwner(key,c),WireJson.Options)).StatusCode);
        deploymentOverrides!["Organization:OwnerProjectId"]=Guid.NewGuid().ToString();await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);using var changed=app.GetTestServer().CreateClient();changed.BaseAddress=new(Origin);Assert.Equal(HttpStatusCode.Forbidden,(await changed.PostAsJsonAsync("/api/owner/approve",SignOwner(key,c),WireJson.Options)).StatusCode);
    }
}
