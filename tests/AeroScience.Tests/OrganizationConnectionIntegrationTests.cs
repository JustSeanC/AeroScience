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

public sealed class OrganizationConnectionIntegrationTests
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
        public WebApplication App=>app;public string Origin=>origin;public string Database=>database;
        public int AuthorizedReads;
        public async Task<JsonElement> Post(string path,object body)
        {
            var session=await browser.GetFromJsonAsync<JsonElement>("/api/session");var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(body,options:WireJson.Options)};
            request.Headers.Add("Origin",origin);request.Headers.Add("X-AeroScience-Organization-CSRF",session.GetProperty("csrf").GetString());using var response=await browser.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        public async Task Approve(OrganizationConnectBegin pending)=>await Post("/api/connection/approval",new OrganizationDeviceApproval(pending.PendingId,pending.Organization.Id,true,true));
        public async ValueTask DisposeAsync(){browser.Dispose();await app.StopAsync();await app.DisposeAsync();}
    }
    static async Task<Server> Start(string database,string directory,string name)
    {
        var admin=new NpgsqlConnectionStringBuilder(System.Environment.GetEnvironmentVariable("AEROSCIENCE_ORG_TEST_DATABASE"));
        if(admin.Host is not("127.0.0.1" or "localhost" or "::1"))throw new InvalidOperationException("Explicit isolated loopback PostgreSQL is required.");
        await using(var c=new NpgsqlConnection(admin.ConnectionString)){await c.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE DATABASE \"{database}\"",c);await cmd.ExecuteNonQueryAsync();}
        admin.Database=database;var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var origin="http://127.0.0.1:"+port;
        var secret=new string('s',48);var path=Path.Combine(directory,database);await File.WriteAllTextAsync(path,secret);OrganizationConfigurationTests.ProtectCredential(path);
        var app=OrganizationApplication.Build([],b=>{b.Environment.EnvironmentName="Testing";b.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{["Organization:LocalDevelopment"]="true",["Organization:PublicOrigin"]=origin,["Organization:ConnectionString"]=admin.ConnectionString,["Organization:BootstrapSecretFile"]=path});b.Logging.SetMinimumLevel(LogLevel.Warning);});
        var browser=new HttpClient(new HttpClientHandler{UseCookies=true,AllowAutoRedirect=false}){BaseAddress=new(origin)};var server=new Server(app,browser,origin,database);
        app.Use(async(c,next)=>{if(c.Request.Path=="/api/member/published")server.AuthorizedReads++;await next();});
        await OrganizationApplication.Initialize(app);await app.StartAsync();
        await server.Post("/api/bootstrap",new OrganizationBootstrapRequest(secret,name,"Private local organization","administrator","ResearchOnly!2026Flight"));await server.Post("/api/sign-in",new OrganizationLoginRequest("administrator","ResearchOnly!2026Flight"));return server;
    }
    static async Task<Guid> Connect(OrganizationConnections desktop,Server server)
    {
        var review=await desktop.Review(server.Origin,default);var pending=await desktop.Begin(new(review.ReviewId,review.Organization.Id,true),default);await server.Approve(pending);var finished=await desktop.Finish(pending.PendingId,default);Assert.Equal("connected",JsonSerializer.SerializeToElement(finished,WireJson.Options).GetProperty("status").GetString());return pending.PendingId;
    }
    [PostgresFact]public async Task IndependentRealServersPersistScopedConnectionsRevokeSeparatelyAndNeverReceiveAnotherServersAuthorization()
    {
        var root=Path.Combine(Path.GetTempPath(),"org-connections-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var databases=new[]{"aeroscience_p13_"+Guid.NewGuid().ToString("N"),"aeroscience_p13_"+Guid.NewGuid().ToString("N")};var servers=new List<Server>();var vault=new Vault();
        try{
            servers.Add(await Start(databases[0],root,"First Independent Lab"));servers.Add(await Start(databases[1],root,"Second Independent Lab"));
            var options=new EngineOptions{ConfigurationDirectory=root,OrganizationLoopbackDevelopment=true};Guid one,two;string? firstSecret;
            using(var desktop=new OrganizationConnections(options,new Environment(),vault)){
                one=await Connect(desktop,servers[0]);firstSecret=vault.Read(one);two=await Connect(desktop,servers[1]);Assert.NotEqual(one,two);Assert.NotEqual(firstSecret,vault.Read(two));Assert.Equal(firstSecret,vault.Read(one));Assert.Equal(2,(await desktop.List(default)).Length);
                Assert.Equal("Connected",(await desktop.Status(one,0,default)).Status);Assert.Equal("Connected",(await desktop.Status(two,0,default)).Status);
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
