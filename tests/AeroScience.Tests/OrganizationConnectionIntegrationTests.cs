using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AeroScience.Engine;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace AeroScience.Tests;

public sealed partial class OrganizationConnectionIntegrationTests
{
    sealed class Environment:IHostEnvironment
    {
        public string EnvironmentName {get;set;}="Testing";public string ApplicationName {get;set;}="Organization connection test";
        public string ContentRootPath {get;set;}="";public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
    }
    sealed class Vault:IOrganizationCredentials
    {
        readonly Dictionary<Guid,string> entries=[];public bool Available=>true;
        public string? Read(Guid id)=>entries.GetValueOrDefault(id);public void Save(Guid id,string value)=>entries[id]=value;public void Remove(Guid id)=>entries.Remove(id);
    }
    sealed class Server(WebApplication app,HttpClient browser,string origin,string database):IAsyncDisposable
    {
        public HttpClient Browser=>browser;public WebApplication App=>app;public string Origin=>origin;public string Database=>database;
        public int AuthorizedReads,ClaimReads,SubmissionWrites;public bool DropNextSubmissionAcknowledgment,InterruptNextSubmission;public Guid? ForcedDeviceIdentity;
        public async Task<JsonElement> Post(string path,object body,HttpMethod? method=null)
        {
            var session=await browser.GetFromJsonAsync<JsonElement>("/api/session");var request=new HttpRequestMessage(method??HttpMethod.Post,path){Content=JsonContent.Create(body,options:WireJson.Options)};
            request.Headers.Add("Origin",origin);request.Headers.Add("X-AeroScience-Organization-CSRF",session.GetProperty("csrf").GetString());using var response=await browser.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task Approve(OrganizationConnectBegin pending)=>await Post("/api/connection/approval",new OrganizationDeviceApproval(pending.PendingId,pending.Organization.Id,true,true,pending.Permission==OrganizationDesktopPermission.ManageOwnClaims,pending.Permission==OrganizationDesktopPermission.SubmitEvidence));
        public async ValueTask DisposeAsync(){browser.Dispose();await app.StopAsync();await app.DisposeAsync();}
    }
    static async Task<Server> Start(string database,string directory,string name,bool existing=false,string? retainedOrigin=null)
    {
        var admin=new NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));
        if(admin.Host is not("127.0.0.1" or "localhost" or "::1"))throw new InvalidOperationException("Explicit isolated loopback PostgreSQL is required.");
        if(!existing)await using(var c=new NpgsqlConnection(admin.ConnectionString)){await c.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE DATABASE \"{database}\"",c);await cmd.ExecuteNonQueryAsync();}
        admin.Database=database;var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var origin=retainedOrigin??"http://127.0.0.1:"+port;
        var secret=new string('s',48);var path=Path.Combine(directory,database);await File.WriteAllTextAsync(path,secret);OrganizationConfigurationTests.ProtectCredential(path);
        var app=OrganizationApplication.Build([],b=>{b.Environment.EnvironmentName="Testing";b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Organization:LocalDevelopment"]="true",["Organization:PublicOrigin"]=origin,["Organization:ConnectionString"]=admin.ConnectionString,["Organization:BootstrapSecretFile"]=path});b.Logging.SetMinimumLevel(LogLevel.Warning);});
        var browser=new HttpClient(new HttpClientHandler{UseCookies=true,AllowAutoRedirect=false}){BaseAddress=new(origin)};var server=new Server(app,browser,origin,database);
        app.Use(async(c,next)=>{
            if(c.Request.Path=="/api/device/start"&&server.ForcedDeviceIdentity is {} collision){
                // Adversarial response from the second isolated server; never a genuine provider fixture.
                await c.Response.WriteAsJsonAsync(new OrganizationDevicePending(collision,new string('a',64),"ABCDE-12345",origin+"/#connect="+collision,DateTimeOffset.UtcNow.AddMinutes(10),5),WireJson.Options);return;
            }
            if(c.Request.Path==OrganizationSubmissionApi.ReceivePath){server.SubmissionWrites++;if(server.InterruptNextSubmission){server.InterruptNextSubmission=false;c.Abort();return;}if(server.DropNextSubmissionAcknowledgment){server.DropNextSubmissionAcknowledgment=false;var response=c.Response.Body;using var lost=new MemoryStream();c.Response.Body=lost;try{await next();}finally{c.Response.Body=response;}c.Abort();return;}}
            if(c.Request.Path=="/api/member/published")server.AuthorizedReads++;if(c.Request.Path.StartsWithSegments("/api/member/claims"))server.ClaimReads++;await next();
        });
        await OrganizationApplication.Initialize(app);await app.StartAsync();
        if(!existing)await server.Post("/api/bootstrap",new OrganizationBootstrapRequest(secret,name,"Private local organization","administrator","ResearchOnly!2026Flight"));await server.Post("/api/sign-in",new OrganizationLoginRequest("administrator","ResearchOnly!2026Flight"));return server;
    }
    static async Task<Guid> Connect(OrganizationConnections desktop,Server server,OrganizationDesktopPermission permission=OrganizationDesktopPermission.ReadPublished)
    {
        var review=await desktop.Review(server.Origin,default);var pending=await desktop.Begin(new(review.ReviewId,review.Organization.Id,true,permission),default);await server.Approve(pending);var finished=await desktop.Finish(pending.PendingId,default);Assert.Equal("connected",JsonSerializer.SerializeToElement(finished,WireJson.Options).GetProperty("status").GetString());return pending.PendingId;
    }
    [PostgresFact]public async Task IndependentRealServersPersistScopedConnectionsRevokeSeparatelyAndNeverReceiveAnotherServersAuthorization()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-connections-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var databases=new[]{"aeroscience_p13_"+Guid.NewGuid().ToString("N"),"aeroscience_p13_"+Guid.NewGuid().ToString("N")};var servers=new List<Server>();var vault=new Vault();
        try{
            servers.Add(await Start(databases[0],root,"First Independent Lab"));servers.Add(await Start(databases[1],root,"Second Independent Lab"));
            var program=await servers[0].Post("/api/organization/programs",new OrganizationProgramRequest(1,Guid.NewGuid(),0,"Published research program",""));
            var programId=program.GetProperty("id").GetGuid();
            // A legitimate page of multilingual descriptions exceeds 256 KiB, but remains bounded below 1 MiB.
            for(var i=0;i<20;i++){
                var draft=await servers[0].Post($"/api/organization/programs/{programId}/campaigns",new OrganizationCampaignRequest(1,Guid.NewGuid(),0,"Published investigation "+i,new string('x',4000),new string('测',2000),OrganizationFocus.ScientificResearch,new("vertical-atmospheric-profile",1)));
                await servers[0].Post("/api/organization/campaigns/"+draft.GetProperty("id").GetGuid()+"/publish",new OrganizationPublishRequest(1,Guid.NewGuid(),1));
            }
            var options=new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true};Guid one,two;string? firstSecret;
            using(var desktop=new OrganizationConnections(options,new Environment(),vault)){
                one=await Connect(desktop,servers[0]);firstSecret=vault.Read(one);
                servers[1].ForcedDeviceIdentity=one;var maliciousReview=await desktop.Review(servers[1].Origin,default);
                await Assert.ThrowsAsync<IOException>(()=>desktop.Begin(new(maliciousReview.ReviewId,maliciousReview.Organization.Id,true),default));
                Assert.Equal(one,Assert.Single(await desktop.List(default)).Id);Assert.Equal(firstSecret,vault.Read(one));servers[1].ForcedDeviceIdentity=null;
                two=await Connect(desktop,servers[1]);Assert.NotEqual(one,two);Assert.NotEqual(firstSecret,vault.Read(two));Assert.Equal(firstSecret,vault.Read(one));Assert.Equal(2,(await desktop.List(default)).Length);
                var firstPage=await desktop.Status(one,0,default);Assert.Equal("Connected",firstPage.Status);Assert.Equal(20,firstPage.Information!.Campaigns.Items.Length);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(firstPage.Information,WireJson.Options))>262144);
Assert.Equal("Connected",(await desktop.Status(two,0,default)).Status);
            }
            var metadata=await File.ReadAllTextAsync(Path.Combine(root,"organization-connections.json"));Assert.DoesNotContain(firstSecret!,metadata);Assert.DoesNotContain(vault.Read(two)!,metadata);Assert.DoesNotContain("ResearchOnly",metadata);
            using(var restarted=new OrganizationConnections(options,new Environment(),vault)){
                Assert.Equal(2,(await restarted.List(default)).Length);Assert.Equal("First Independent Lab",(await restarted.Status(one,0,default)).Information!.Organization.Name);
                await restarted.Disconnect(one,default);Assert.Null(vault.Read(one));Assert.NotNull(vault.Read(two));Assert.Single(await restarted.List(default));Assert.Equal("Connected",(await restarted.Status(two,0,default)).Status);
                // Identity changes are detected before sending a retained bearer, even at the same host.
                var before=servers[1].AuthorizedReads;using(var scope=servers[1].App.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE organization.\"Organizations\" SET \"PublicId\"={Guid.NewGuid()}");}
                Assert.Equal("Offline / Unable to Connect",(await restarted.Status(two,0,default)).Status);Assert.Equal(before,servers[1].AuthorizedReads);
                await restarted.Disconnect(two,default);Assert.Null(vault.Read(two));Assert.Empty(await restarted.List(default));
            }
            Assert.False(File.Exists(Path.Combine(root,"missions.db")));Assert.False(Directory.Exists(Path.Combine(root,"ScienceLibrary")));
        }finally{
            foreach(var server in servers)await server.DisposeAsync();NpgsqlConnection.ClearAllPools();
            var admin=new NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));await using var c=new NpgsqlConnection(admin.ConnectionString);await c.OpenAsync();foreach(var database in databases){await using var cmd=new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)",c);await cmd.ExecuteNonQueryAsync();}Directory.Delete(root,true);
        }
    }
}
