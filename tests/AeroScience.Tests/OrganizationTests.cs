using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace AeroScience.Tests;

public sealed class OrganizationConfigurationTests
{
    sealed class EnvironmentStub(string name):IHostEnvironment
    {
        public string EnvironmentName {get;set;}=name;public string ApplicationName {get;set;}="organization-test";
        public string ContentRootPath {get;set;}="";public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider {get;set;}=new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    static Dictionary<string,string?> Settings()=>new(){["Organization:LocalDevelopment"]="true",["Organization:PublicOrigin"]="http://127.0.0.1:5193",["Organization:ConnectionString"]="Host=127.0.0.1;Database=isolated;Username=test"};
    static OrganizationSettings Read(Dictionary<string,string?> values,string environment="Development")=>OrganizationSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),new EnvironmentStub(environment));
    [Theory][InlineData("http://0.0.0.0:5193")][InlineData("http://example.org")][InlineData("http://127.0.0.1:5193/path")][InlineData("https://user:password@example.org")]
    public void DevelopmentCannotAccidentallyBecomePublicHosting(string origin){var values=Settings();values["Organization:PublicOrigin"]=origin;Assert.Throws<InvalidOperationException>(()=>Read(values));}
    [Fact]public void ProductionRequiresSecureDatabaseOriginAndPersistentKeyProtection()
    {
        var values=Settings();Assert.Throws<InvalidOperationException>(()=>Read(values,"Production"));values["Organization:LocalDevelopment"]="false";Assert.Throws<InvalidOperationException>(()=>Read(values));
        values["Organization:PublicOrigin"]="https://research.example.org";Assert.Throws<InvalidOperationException>(()=>Read(values));values["Organization:ConnectionString"]+=";SSL Mode=VerifyFull";Assert.Throws<InvalidOperationException>(()=>Read(values));values["Organization:KeyCertificateFile"]="operator-owned.pfx";Assert.False(Read(values).LocalDevelopment);
        values["Organization:ConnectionString"]+=";Include Error Detail=true";Assert.Throws<InvalidOperationException>(()=>Read(values));
    }
    [Fact]public void CatalogUsesTheSixOriginalDefinitionsAndExistingCustomValidatorsWithoutCreatingAStore()
    {
        var templates=OrganizationTemplates.All();Assert.Equal(8,templates.Length);Assert.Equal(8,templates.Select(t=>t.Reference).Distinct().Count());foreach(var t in templates)Assert.Equal(JsonSerializer.Serialize(t.Definition,WireJson.Options),JsonSerializer.Serialize(OrganizationTemplates.Resolve(t.Reference),WireJson.Options));
        Assert.Throws<ArgumentException>(()=>OrganizationTemplates.Resolve(new("unknown",1)));Assert.Throws<ArgumentException>(()=>OrganizationTemplates.Resolve(new("flight-regime",999)));
        Assert.Contains(templates,t=>t.Definition.Procedure?.Points[1].Repetitions==2);Assert.Contains(templates,t=>t.Definition.ExperimentalSensor is not null);
    }
    [Fact]public void BootstrapCredentialUsesBoundedSecretVerificationAndCanBeDisposed()
    {
        using var secret=new BootstrapCredential();secret.Initialize(System.Text.Encoding.UTF8.GetBytes(new string('a',48)));Assert.False(secret.Matches("short"));Assert.False(secret.Matches(new string('b',48)));Assert.True(secret.Matches(new string('a',48)));secret.Dispose();Assert.False(secret.Matches(new string('a',48)));
    }
    internal static void ProtectCredential(string path)
    {
        if(OperatingSystem.IsWindows()){
            using var identity=WindowsIdentity.GetCurrent();var security=new FileSecurity();security.SetAccessRuleProtection(true,false);security.SetOwner(identity.User!);security.AddAccessRule(new FileSystemAccessRule(identity.User!,FileSystemRights.FullControl,AccessControlType.Allow));new FileInfo(path).SetAccessControl(security);
        }else File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }
    [Fact]public void ProtectedBootstrapFileIsRequiredAndBroadlyReadableMaterialIsRejected()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-credential-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var file=Path.Combine(root,"secret");var options=Read(Settings()) with{BootstrapSecretFile=file};
        try{
            Assert.Throws<InvalidOperationException>(()=>options.ReadBootstrapSecret());File.WriteAllText(file,new string('s',48));ProtectCredential(file);Assert.Equal(48,options.ReadBootstrapSecret().Length);
            if(OperatingSystem.IsWindows()){var security=new FileInfo(file).GetAccessControl();security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid,null),FileSystemRights.ReadData,AccessControlType.Allow));new FileInfo(file).SetAccessControl(security);}
            else File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.OtherRead);
            Assert.Throws<InvalidOperationException>(()=>options.ReadBootstrapSecret());
        }finally{Directory.Delete(root,true);}
    }
}

public sealed class PostgresFactAttribute:FactAttribute
{
    public PostgresFactAttribute(){if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE")))Skip="Isolated PostgreSQL is not configured on this platform; no in-memory substitute.";}
}
public sealed class OrganizationServerTests:IAsyncLifetime
{
    const string Origin="http://127.0.0.1:5193",Password="ResearchOnly!2026Flight";
    readonly string directory=Path.Combine(Path.GetTempPath(),"aeroscience-org-test-"+Guid.NewGuid().ToString("N"));
    readonly string database="aeroscience_p13_"+Guid.NewGuid().ToString("N"),secret=new string('s',48);
    string connection="";WebApplication app=null!;Browser browser=null!;
    sealed class Cookies(CookieContainer jar):DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){var cookies=jar.GetCookieHeader(request.RequestUri!);if(cookies.Length>0)request.Headers.Add("Cookie",cookies);var response=await base.SendAsync(request,token);if(response.Headers.TryGetValues("Set-Cookie",out var values))foreach(var value in values)jar.SetCookies(request.RequestUri!,value);return response;}
    }
    sealed class Browser:IDisposable
    {
        public CookieContainer Jar {get;}=new();public HttpClient Client {get;private set;}=null!;public string Csrf {get;private set;}="";
        public void Connect(WebApplication server){Client?.Dispose();Client=new(new Cookies(Jar){InnerHandler=server.GetTestServer().CreateHandler()}){BaseAddress=new(Origin)};}
        public async Task<JsonElement> Session(){var r=await Client.GetAsync("/api/session");r.EnsureSuccessStatusCode();var json=await r.Content.ReadFromJsonAsync<JsonElement>();Csrf=json.GetProperty("csrf").GetString()!;return json;}
        public async Task<HttpResponseMessage> Send(string path,object body,string method="POST",bool csrf=true,string origin=Origin){var message=new HttpRequestMessage(new HttpMethod(method),path){Content=JsonContent.Create(body,options:WireJson.Options)};message.Headers.Add("Origin",origin);if(csrf)message.Headers.Add("X-AeroScience-Organization-CSRF",Csrf);await message.Content.LoadIntoBufferAsync();return await Client.SendAsync(message);}
        public void Dispose()=>Client.Dispose();
    }
    public async Task InitializeAsync()
    {
        var admin=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));
        if(admin.Host is not("127.0.0.1" or "localhost" or "::1"))throw new InvalidOperationException("Organization tests require an explicit local isolated PostgreSQL fixture.");
        await using(var db=new NpgsqlConnection(admin.ConnectionString)){await db.OpenAsync();await using var command=new NpgsqlCommand($"CREATE DATABASE \"{database}\"",db);await command.ExecuteNonQueryAsync();}
        admin.Database=database;connection=admin.ConnectionString;Directory.CreateDirectory(directory);var path=Path.Combine(directory,"bootstrap-secret");await File.WriteAllTextAsync(path,secret);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        OrganizationConfigurationTests.ProtectCredential(path);await Start();browser=new();browser.Connect(app);await browser.Session();
    }
    async Task Start()
    {
        app=OrganizationApplication.Build([],b=>{b.Environment.EnvironmentName="Testing";b.WebHost.UseTestServer();b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Organization:LocalDevelopment"]="true",["Organization:PublicOrigin"]=Origin,["Organization:ConnectionString"]=connection,["Organization:BootstrapSecretFile"]=Path.Combine(directory,"bootstrap-secret")});b.Logging.SetMinimumLevel(LogLevel.Warning);});
        await OrganizationApplication.Initialize(app);await app.StartAsync();
    }
    object Setup(string code)=>new{secret=code,organizationName="Research Flight Lab",description="Private scientific research",userName="administrator",password=Password};
    async Task Bootstrap(){var r=await browser.Send("/api/bootstrap",Setup(secret));Assert.Equal(HttpStatusCode.OK,r.StatusCode);}
    async Task Login(Browser? target=null,string name="administrator"){target??=browser;await target.Session();var response=await target.Send("/api/sign-in",new{userName=name,password=Password});Assert.Equal(HttpStatusCode.OK,response.StatusCode);await target.Session();}
    static async Task<T> Value<T>(HttpResponseMessage response){Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return(await response.Content.ReadFromJsonAsync<T>(WireJson.Options))!;}
    async Task<OrganizationProgramView> Program()=>await Value<OrganizationProgramView>(await browser.Send("/api/organization/programs",new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Atmospheric Research","Saved program")));
    static OrganizationCampaignRequest Campaign(string objective="How does temperature change with altitude?")=>new(1,Guid.NewGuid(),0,"Autumn Profiles","Recorded simulator atmosphere",objective,OrganizationFocus.ScientificResearch,new("vertical-atmospheric-profile",1));
    [PostgresFact]public async Task FirstVisitorCannotBecomeAdministratorAndBootstrapIsAtomicSingleUse()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync("/api/organization/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/bootstrap",Setup(new string('x',48)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/bootstrap",Setup(secret),csrf:false)).StatusCode);
        var responses=await Task.WhenAll(browser.Send("/api/bootstrap",Setup(secret)),browser.Send("/api/bootstrap",Setup(secret)));Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Conflict);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(1,await db.Users.CountAsync());Assert.Equal(1,await db.Organizations.CountAsync());Assert.Equal(4,await db.Roles.CountAsync());Assert.NotEqual(Password,(await db.Users.SingleAsync()).PasswordHash);
    }
    [PostgresFact]public async Task SignInSignOutAndCsrfOriginProtectionsNeverAcceptDesktopPairingCredentials()
    {
        await Bootstrap();Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Send("/api/sign-in",new{userName="administrator",password="Wrong!Password2026"})).StatusCode);await Login();
        var input=new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Protected","No hidden local access");Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/organization/programs",input,csrf:false)).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/organization/programs",input,origin:"https://evil.example")).StatusCode);
        using var anonymous=new Browser();anonymous.Connect(app);anonymous.Client.DefaultRequestHeaders.Add("X-AeroScience-CSRF","desktop-pairing-is-not-an-organization-login");Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.Client.GetAsync("/api/organization/programs")).StatusCode);
        var oldCookies=browser.Jar.GetCookieHeader(new Uri(Origin));Assert.Equal(HttpStatusCode.OK,(await browser.Send("/api/sign-out",new{})).StatusCode);Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync("/api/organization/programs")).StatusCode);
        using var replay=new Browser();replay.Connect(app);replay.Jar.SetCookies(new Uri(Origin),oldCookies.Replace("; ",","));Assert.Equal(HttpStatusCode.Unauthorized,(await replay.Client.GetAsync("/api/organization/programs")).StatusCode);
    }
    [PostgresFact]public async Task RolesAreEnforcedOnEveryManagementAndPublicationRequest()
    {
        await Bootstrap();await Login();var p=await Program();var campaign=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign()));
        foreach(var role in new[]{OrganizationRoles.Manager,OrganizationRoles.Reviewer,OrganizationRoles.Pilot}){
            using(var scope=app.Services.CreateScope()){var users=scope.ServiceProvider.GetRequiredService<UserManager<OrganizationUser>>();var user=new OrganizationUser{Id=Guid.NewGuid(),UserName=role};Assert.True((await users.CreateAsync(user,Password)).Succeeded);Assert.True((await users.AddToRoleAsync(user,role)).Succeeded);}
            using var target=new Browser();target.Connect(app);await Login(target,role);
            Assert.Equal(role==OrganizationRoles.Manager?HttpStatusCode.OK:HttpStatusCode.Forbidden,(await target.Client.GetAsync("/api/organization/programs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await target.Send($"/api/organization/campaigns/{campaign.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await target.Client.GetAsync("/api/organization/audit")).StatusCode);
            var edited=await target.Send($"/api/organization/programs/{p.Id}",new OrganizationProgramRequest(1,Guid.NewGuid(),p.Revision,"Manager program edit","Role boundary"),"PUT");Assert.Equal(role==OrganizationRoles.Manager?HttpStatusCode.OK:HttpStatusCode.Forbidden,edited.StatusCode);
        }
    }
    [PostgresFact]public async Task ConcurrentCommandsAreIdempotentAndConflictingEditsPreserveOriginalRevision()
    {
        await Bootstrap();await Login();var command=new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Exact retry","One program");var responses=await Task.WhenAll(browser.Send("/api/organization/programs",command),browser.Send("/api/organization/programs",command));var a=await Value<OrganizationProgramView>(responses[0]);Assert.Equal(a,await Value<OrganizationProgramView>(responses[1]));
        Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/organization/programs",command with{Name="Different payload"})).StatusCode);
        var edit=command with{RequestId=Guid.NewGuid(),ExpectedRevision=1,Name="First editor"};responses=await Task.WhenAll(browser.Send($"/api/organization/programs/{a.Id}",edit,"PUT"),browser.Send($"/api/organization/programs/{a.Id}",edit with{RequestId=Guid.NewGuid(),Name="Other editor"},"PUT"));Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(responses,r=>r.StatusCode==HttpStatusCode.Conflict);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.Equal(1,await db.Programs.CountAsync());Assert.Equal(2,await db.Revisions.CountAsync());Assert.Equal(2,(await db.Programs.SingleAsync()).Revision);
    }
    [PostgresFact]public async Task PublicationFreezesDefinitionsAndNewDraftCannotRewriteThePublishedCampaign()
    {
        await Bootstrap();await Login();var p=await Program();var input=Campaign("");var draft=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",input));
        Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1))).StatusCode);
        var updated=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns/{draft.Id}",input with{RequestId=Guid.NewGuid(),ExpectedRevision=1,Objective="How does temperature change during a climb?"},"PUT"));var publication=new OrganizationPublishRequest(1,Guid.NewGuid(),2);
        var published=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",publication));Assert.Equal(OrganizationPublication.Published,published.Status);Assert.Equal(published,await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",publication)));
        var next=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns/{draft.Id}",input with{RequestId=Guid.NewGuid(),ExpectedRevision=3,Objective="A new draft question"},"PUT"));Assert.Equal(3,next.PublishedRevision);Assert.Equal(OrganizationPublication.Draft,next.Status);
        var old=await Value<DefinitionRevision>(await browser.Client.GetAsync($"/api/organization/definitions/{draft.Id}/revisions/3"));Assert.Equal(published,JsonSerializer.Deserialize<OrganizationCampaignView>(old.SnapshotJson,WireJson.Options));Assert.Equal(1,published.Definition.AtmosphericProfile!.Version);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var row=await db.Revisions.SingleAsync(r=>r.ResourceId==draft.Id&&r.Revision==3);row.SnapshotJson="{}";await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());
    }
    [PostgresFact]public async Task DatabaseAndSecureSessionsSurviveServerRestartWithoutAnotherBootstrapSecret()
    {
        await Bootstrap();await Login();var p=await Program();var campaign=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{p.Id}/campaigns",Campaign()));
        await app.StopAsync();await app.DisposeAsync();File.Delete(Path.Combine(directory,"bootstrap-secret"));await Start();browser.Connect(app);var session=await browser.Session();Assert.Equal("administrator",session.GetProperty("user").GetProperty("name").GetString());Assert.Equal(p,await Value<OrganizationProgramView>(await browser.Client.GetAsync($"/api/organization/programs/{p.Id}")));
        var page=await Value<OrganizationPage<OrganizationCampaignView>>(await browser.Client.GetAsync($"/api/organization/programs/{p.Id}/campaigns"));Assert.Equal(campaign,Assert.Single(page.Items));Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/bootstrap",Setup(secret))).StatusCode);
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.NotEmpty(await db.DataProtectionKeys.ToArrayAsync());Assert.False(db.Database.HasPendingModelChanges());
    }
    [PostgresFact]public async Task InputValidationUnknownVersionsAndPrivatePaginationCannotBypassTheDefinitionContract()
    {
        await Bootstrap();await Login();var p=await Program();var path=$"/api/organization/programs/{p.Id}/campaigns";var input=Campaign();
        foreach(var bad in new[]{input with{Version=2},input with{Name=""},input with{Template=new("unsupported-mach-detector",1)},input with{Template=new("vertical-atmospheric-profile",999)},input with{Focus=OrganizationFocus.Unknown}})Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send(path,bad)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await browser.Send(path,new{input.Version,input.RequestId,input.ExpectedRevision,input.Name,input.Description,input.Objective,input.Focus,input.Template,rawFlightEvidence="never accepted"})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await browser.Client.GetAsync("/api/organization/programs?offset=-1")).StatusCode);
        for(var i=0;i<24;i++)await Value<OrganizationCampaignView>(await browser.Send(path,input with{RequestId=Guid.NewGuid(),Name="Retained campaign "+i}));var first=await Value<OrganizationPage<OrganizationCampaignView>>(await browser.Client.GetAsync(path));var second=await Value<OrganizationPage<OrganizationCampaignView>>(await browser.Client.GetAsync(path+"?offset="+first.NextOffset));Assert.Equal(20,first.Items.Length);Assert.Equal(4,second.Items.Length);Assert.Empty(first.Items.Select(c=>c.Id).Intersect(second.Items.Select(c=>c.Id)));
    }
    [PostgresFact]public async Task IdentityLockoutAndStampRevocationPreserveAdministrativeAccessBoundaries()
    {
        await Bootstrap();for(var i=0;i<5;i++)Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Send("/api/sign-in",new{userName="administrator",password="NotThePassword!2026"})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Send("/api/sign-in",new{userName="administrator",password=Password})).StatusCode);
        using(var scope=app.Services.CreateScope()){var users=scope.ServiceProvider.GetRequiredService<UserManager<OrganizationUser>>();var user=(await users.FindByNameAsync("administrator"))!;Assert.True(await users.IsLockedOutAsync(user));await users.SetLockoutEndDateAsync(user,DateTimeOffset.UtcNow.AddMinutes(-1));}
        await Login();using(var scope=app.Services.CreateScope()){var users=scope.ServiceProvider.GetRequiredService<UserManager<OrganizationUser>>();await users.UpdateSecurityStampAsync((await users.FindByNameAsync("administrator"))!);}
        Assert.Equal(HttpStatusCode.Unauthorized,(await browser.Client.GetAsync("/api/organization/programs")).StatusCode);
    }
    [PostgresFact]public async Task UntrustedHostsForwardingHeadersAndOversizedBodiesFailWithoutPrivateResponses()
    {
        await Bootstrap();await Login();using var request=new HttpRequestMessage(HttpMethod.Get,"/api/organization/overview");request.Headers.Host="evil.example";Assert.Equal(HttpStatusCode.BadRequest,(await browser.Client.SendAsync(request)).StatusCode);
        using var forwarded=new HttpRequestMessage(HttpMethod.Get,"/api/session");forwarded.Headers.Add("X-Forwarded-Host","evil.example");forwarded.Headers.Add("X-Forwarded-Proto","https");Assert.Equal(HttpStatusCode.OK,(await browser.Client.SendAsync(forwarded)).StatusCode);
        var response=await browser.Send("/api/organization/programs",new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Too long",new string('x',5000)));Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.DoesNotContain(connection,await response.Content.ReadAsStringAsync());Assert.Equal(HttpStatusCode.RequestEntityTooLarge,(await browser.Send("/api/organization/programs",new{name=new string('x',40000)})).StatusCode);
    }
    [PostgresFact]public async Task MissingBootstrapCannotStartAnUnclaimedServerAndCorruptRevisionCannotAppearVerified()
    {
        await app.StopAsync();await app.DisposeAsync();File.Delete(Path.Combine(directory,"bootstrap-secret"));await Assert.ThrowsAsync<InvalidOperationException>(Start);
        await File.WriteAllTextAsync(Path.Combine(directory,"bootstrap-secret"),secret);OrganizationConfigurationTests.ProtectCredential(Path.Combine(directory,"bootstrap-secret"));await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();await Bootstrap();await Login();var p=await Program();
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organization.\"Revisions\" SET \"SnapshotJson\"='{{}}' WHERE \"ResourceId\"={p.Id}");}
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await browser.Client.GetAsync($"/api/organization/definitions/{p.Id}/revisions/1")).StatusCode);
    }
    [PostgresFact]public async Task FutureDatabaseSchemaIsRefusedRatherThanOpenedByAnOlderServer()
    {
        await Bootstrap();using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('99999999999999_future_organization','99.0.0')");}
        await app.StopAsync();await app.DisposeAsync();await Assert.ThrowsAsync<InvalidOperationException>(Start);
    }
    [PostgresFact]public async Task AllEightSupportedApproachesPublishExactDefinitionsWithoutAnyFlightOrScienceTables()
    {
        await Bootstrap();await Login();var program=await Program();var index=0;
        foreach(var template in OrganizationTemplates.All()){
            var input=Campaign() with{RequestId=Guid.NewGuid(),Template=template.Reference,Name=template.Name,Focus=new[]{OrganizationFocus.ScientificResearch,OrganizationFocus.ExperimentalFlightTest,OrganizationFocus.Mixed}[index++%3]};
            var draft=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/programs/{program.Id}/campaigns",input));
            var published=await Value<OrganizationCampaignView>(await browser.Send($"/api/organization/campaigns/{draft.Id}/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1)));
            Assert.Equal(JsonSerializer.Serialize(template.Definition,WireJson.Options),JsonSerializer.Serialize(published.Definition,WireJson.Options));
            Assert.Equal(HttpStatusCode.OK,(await browser.Client.GetAsync($"/api/organization/definitions/{draft.Id}/revisions/2")).StatusCode);
        }
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();Assert.DoesNotContain(db.Model.GetEntityTypes(),t=>t.ClrType==typeof(Mission)||t.ClrType==typeof(ScienceObservation)||t.ClrType==typeof(PersonalResearchCard));Assert.Equal(16,await db.Revisions.CountAsync(r=>r.Kind=="campaign"));
    }
    public async Task DisposeAsync()
    {
        browser?.Dispose();if(app is not null){await app.StopAsync();await app.DisposeAsync();}NpgsqlConnection.ClearAllPools();
        if(connection.Length>0){var admin=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));await using var db=new NpgsqlConnection(admin.ConnectionString);await db.OpenAsync();await using var cmd=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)",db);await cmd.ExecuteNonQueryAsync();}
        if(Directory.Exists(directory))Directory.Delete(directory,true);
    }
}
