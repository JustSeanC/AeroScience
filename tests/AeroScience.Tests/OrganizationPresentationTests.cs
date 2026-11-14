using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationServerTests
{
    [PostgresFact] public async Task MemberLanguageIsSelfScopedRevisionedCsrfProtectedAndDoesNotGrantSharingOrAdministration()
    {
        const string path="/api/account/presentation";
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync(path)).StatusCode);
        await Bootstrap();await Login();
        using var pilot=await JoinPilot(invitation:InvitationSecret(await Invite()));
        var before=await pilot.Session();
        var userId=before.GetProperty("user").GetProperty("id").GetGuid();
        string? stamp;await using(var scope=app.Services.CreateAsyncScope())stamp=(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Users.SingleAsync(u=>u.Id==userId)).SecurityStamp;
        var defaults=await Value<JsonElement>(await pilot.Client.GetAsync(path));Assert.Equal(JsonValueKind.Null,defaults.GetProperty("locale").ValueKind);Assert.Equal(0,defaults.GetProperty("revision").GetInt64());
        var change=new { version=1,expectedRevision=0,locale="fr-FR" };
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send(path,change,csrf:false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send(path,change,origin:"https://wrong.example")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send(path,new{version=1,expectedRevision=0,locale="unsupported"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send(path,new{version=1,locale="de-DE"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await pilot.Send(path,new{version=1,expectedRevision=0,locale="de-DE",userId=Guid.NewGuid()})).StatusCode);
        var saved=await Value<JsonElement>(await pilot.Send(path,change));Assert.Equal("fr-FR",saved.GetProperty("locale").GetString());Assert.Equal(PresentationText.CatalogIdentity,saved.GetProperty("catalogIdentity").GetString());
        Assert.Equal(HttpStatusCode.Conflict,(await pilot.Send(path,change)).StatusCode);
        var admin=await Value<JsonElement>(await browser.Client.GetAsync(path));Assert.Equal(JsonValueKind.Null,admin.GetProperty("locale").ValueKind);
        Assert.Equal(before.GetProperty("user").GetRawText(),(await pilot.Session()).GetProperty("user").GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/organization/members")).StatusCode);
        await using(var scope=app.Services.CreateAsyncScope()){
            var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var user=await db.Users.SingleAsync(u=>u.Id==userId);
            Assert.Equal(stamp,user.SecurityStamp);Assert.Empty(await db.LiveConsents.ToListAsync());Assert.Empty(await db.DeviceGrants.ToListAsync());
        }
        var follow=await Value<JsonElement>(await pilot.Send(path,new{version=1,expectedRevision=1,locale=(string?)null}));Assert.Equal(2,follow.GetProperty("revision").GetInt64());Assert.Equal(JsonValueKind.Null,follow.GetProperty("locale").ValueKind);
        await Value<JsonElement>(await pilot.Send("/api/sign-out",new{}));
        Assert.Equal(HttpStatusCode.Unauthorized,(await pilot.Client.GetAsync(path)).StatusCode);
    }

    [PostgresFact] public async Task MemberLanguageRetainsTheOriginalAdministratorMfaGate()
    {
        await RequireMfa();await Bootstrap();await Login();
        Assert.Equal((HttpStatusCode)428,(await browser.Client.GetAsync("/api/account/presentation")).StatusCode);
        Assert.Equal((HttpStatusCode)428,(await browser.Send("/api/account/presentation",new{version=1,expectedRevision=0,locale="es-ES"})).StatusCode);
        await using var scope=app.Services.CreateAsyncScope();Assert.All(await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().Users.ToListAsync(),u=>{Assert.Null(u.PresentationLocale);Assert.Equal(0,u.PresentationRevision);});
    }
}
