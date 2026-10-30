using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AeroScience.Organization;
using AeroScience.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
namespace AeroScience.Tests;

// Only this test assembly supplies a loopback connection and ephemeral trust root. Product transport
// has no alternate host, root, proxy, callback or insecure TLS option in runtime configuration.
public sealed class DiscordHttpsFixture:IDiscordTransport,IAsyncDisposable
{
    readonly WebApplication app;readonly X509Certificate2 certificate;readonly HttpClient client;
    public readonly List<byte[]> Bodies=[];public int Status=200;public string Response="{\"id\":\"123456789012345678\"}";public bool Abort,StallAfterHeaders;
    DiscordHttpsFixture(WebApplication app,X509Certificate2 certificate,HttpClient client){this.app=app;this.certificate=certificate;this.client=client;}
    public static async Task<DiscordHttpsFixture> Create(string host="discord.com")
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=discord.com",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);var names=new SubjectAlternativeNameBuilder();names.AddDnsName(host);request.CertificateExtensions.Add(names.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddDays(1));
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0,l=>l.UseHttps(certificate)));var app=builder.Build();DiscordHttpsFixture? fixture=null;
        app.MapPost("/api/v10/webhooks/{id}/{token}",async(HttpContext c)=>{using var body=new MemoryStream();await c.Request.Body.CopyToAsync(body);lock(fixture!.Bodies)fixture.Bodies.Add(body.ToArray());Assert.Equal("true",c.Request.Query["wait"]);if(fixture.Abort){c.Abort();return;}c.Response.StatusCode=fixture.Status;if(fixture.StallAfterHeaders){await c.Response.StartAsync();try{await Task.Delay(Timeout.Infinite,c.RequestAborted);}catch(OperationCanceledException){}return;}if(fixture.Status==302)c.Response.Headers.Location="https://127.0.0.1/private";await c.Response.WriteAsync(fixture.Response);});await app.StartAsync();var port=new Uri(app.Urls.Single()).Port;
        var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectCallback=async(context,ct)=>{Assert.Equal("discord.com",context.DnsEndPoint.Host);Assert.Equal(443,context.DnsEndPoint.Port);var socket=new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);await socket.ConnectAsync(IPAddress.Loopback,port,ct);return new System.Net.Sockets.NetworkStream(socket,true);}};
        handler.SslOptions.CertificateChainPolicy=new(){TrustMode=X509ChainTrustMode.CustomRootTrust,RevocationMode=X509RevocationMode.NoCheck};handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(certificate);
        var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};fixture=new(app,certificate,client);return fixture;
    }
    public Task<DiscordResponse> Send(DiscordWebhookAddress d,byte[] body,CancellationToken ct)=>DiscordTransport.Execute(client,d,body,ct);
    public async ValueTask DisposeAsync(){client.Dispose();await app.StopAsync();await app.DisposeAsync();certificate.Dispose();}
}

public sealed class DiscordTransportTests
{
    const string Url="https://discord.com/api/webhooks/123456789012345678/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [Fact]public async Task ActualHttpsReceiverValidatesHostAndSendsBoundedMentionDisabledJsonWithoutFollowingRedirects()
    {
        await using var receiver=await DiscordHttpsFixture.Create();var d=DiscordWebhookAddress.Parse(Url);var body=DiscordWebhookPolicy.Message(DiscordWebhookPolicy.PlainText("@everyone **private** <@123> safe",2000));
        Assert.Equal(DiscordAcknowledgment.Delivered,(await receiver.Send(d,body,default)).State);var sent=JsonDocument.Parse(Assert.Single(receiver.Bodies)).RootElement;Assert.Empty(sent.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());Assert.DoesNotContain("@",sent.GetProperty("content").GetString());
        receiver.Status=302;receiver.Response="";Assert.Equal(DiscordAcknowledgment.Ambiguous,(await receiver.Send(d,body,default)).State);Assert.Equal(2,receiver.Bodies.Count);
        receiver.Status=429;receiver.Response="{\"retry_after\":2.25}";Assert.Equal(3,(await receiver.Send(d,body,default)).RetryAfterSeconds);
        receiver.Status=200;receiver.Response=new string('x',20000);Assert.Equal(DiscordAcknowledgment.Ambiguous,(await receiver.Send(d,body,default)).State);
        receiver.Abort=true;Assert.Equal(DiscordAcknowledgment.Ambiguous,(await receiver.Send(d,body,default)).State);
    }

    [Fact]public async Task ActualHttpsBodyCannotStallPastTheWholeRequestDeadline()
    {
        await using var receiver=await DiscordHttpsFixture.Create();receiver.StallAfterHeaders=true;
        var send=receiver.Send(DiscordWebhookAddress.Parse(Url),DiscordWebhookPolicy.Message("AeroScience bounded request"),default);
        Assert.Equal(DiscordAcknowledgment.Ambiguous,(await send.WaitAsync(TimeSpan.FromSeconds(15))).State);Assert.Single(receiver.Bodies);
    }
    [Fact]public async Task RealHttpsRejectsAReceiverCertificateForAnotherHost()
    {
        await using var receiver=await DiscordHttpsFixture.Create("wrong.example");Assert.Equal(DiscordAcknowledgment.Ambiguous,(await receiver.Send(DiscordWebhookAddress.Parse(Url),DiscordWebhookPolicy.Message("AeroScience test"),default)).State);Assert.Empty(receiver.Bodies);
    }
    [Fact]public async Task PinnedConnectorRejectsPrivateDnsMixedAnswersAlternateHostsAndActualPeerSubstitution()
    {
        var called=false;
        Task<IPAddress[]> Public(CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Parse("1.1.1.1")});
        Task<System.Net.Sockets.Socket> Never(IPAddress a,CancellationToken ct){called=true;throw new InvalidOperationException("Must not connect");}
        foreach(var host in new[]{"localhost","discord.com.evil.example","127.0.0.1"})await Assert.ThrowsAsync<InvalidOperationException>(async()=>await DiscordTransport.ConnectChecked(new(host,443),Public,Never,default));
        await Assert.ThrowsAsync<InvalidOperationException>(async()=>await DiscordTransport.ConnectChecked(new("discord.com",8443),Public,Never,default));
        foreach(var address in new[]{"127.0.0.1","10.0.0.1","169.254.169.254","::1","fc00::1"})await Assert.ThrowsAsync<InvalidOperationException>(async()=>await DiscordTransport.ConnectChecked(new("discord.com",443),ct=>Task.FromResult(new[]{IPAddress.Parse("1.1.1.1"),IPAddress.Parse(address)}),Never,default));Assert.False(called);
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();
        try{var port=((IPEndPoint)listener.LocalEndpoint).Port;var accepting=listener.AcceptSocketAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(async()=>await DiscordTransport.ConnectChecked(new("discord.com",443),Public,async(a,ct)=>{var socket=new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);await socket.ConnectAsync(IPAddress.Loopback,port,ct);return socket;},default));using var accepted=await accepting;
        }finally{listener.Stop();}
    }

}

public sealed partial class OrganizationServerTests
{
    IDiscordTransport? discordTransport;
    const string DiscordUrl="https://discord.com/api/webhooks/123456789012345678/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    async Task NotificationAdmin(){var e=await SecureAdministrator();await Value<JsonElement>(await browser.Send("/api/account-security/confirm-recovery",new AccountRecoveryConfirmation(e.Codes[0])));}
    async Task<DiscordDestinationView> ConfigureDiscord(bool enabled=true,int categories=7,int expected=0,string? webhook=DiscordUrl)=>await Value<DiscordDestinationView>(await browser.Send("/api/organization/notifications",new DiscordSettingsCommand(Guid.NewGuid(),expected,"Research announcements",enabled,categories,webhook)));
    async Task NotificationTick(){using var scope=app.Services.CreateScope();await scope.ServiceProvider.GetRequiredService<OrganizationNotifications>().Tick(default);}
    async Task<DiscordDeliveryRecord[]> NotificationRows(){using var scope=app.Services.CreateScope();return await scope.ServiceProvider.GetRequiredService<OrganizationDatabase>().DiscordDeliveries.AsNoTracking().OrderBy(x=>x.Sequence).ToArrayAsync();}
    async Task<DiscordConsentView> AllowDiscord(Browser pilot,LiveSharingSession s,int revision=0,bool allowed=true,int destination=1)=>await Value<DiscordConsentView>(await pilot.Send("/api/membership/discord/consent",new DiscordConsentCommand(Guid.NewGuid(),s.Id,revision,destination,allowed)));
    [PostgresFact]public async Task DiscordOffByDefaultRequiresAdministratorRecentMfaCsrfAndNeverReturnsOrArchivesCredentials()
    {
        await Bootstrap();await Login();var view=await Value<DiscordDestinationView>(await browser.Client.GetAsync("/api/organization/notifications"));Assert.False(view.Enabled);
        var command=new DiscordSettingsCommand(Guid.NewGuid(),0,"Public announcements",false,1,DiscordUrl);Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/organization/notifications",command)).StatusCode);
        using var pilot=await JoinPilot("discord-pilot",InvitationSecret(await Invite()));Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Client.GetAsync("/api/organization/notifications")).StatusCode);Assert.Equal(HttpStatusCode.Forbidden,(await pilot.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),0))).StatusCode);
    }
    [PostgresFact]public async Task DiscordProtectedCredentialRotationDeletionStaleCommandsAndExplicitTestsUseRealHttps()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();var d=await ConfigureDiscord(false);await NotificationTick();Assert.Empty(receiver.Bodies);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var saved=await db.DiscordDestinations.SingleAsync();Assert.DoesNotContain("aaaaa",saved.ProtectedCredential);Assert.NotEqual(DiscordUrl,saved.ProtectedCredential);Assert.All(await db.Receipts.ToArrayAsync(),r=>Assert.DoesNotContain(DiscordUrl,r.ResponseJson));Assert.All(await db.Revisions.ToArrayAsync(),r=>Assert.DoesNotContain(DiscordUrl,r.SnapshotJson));}
        Assert.DoesNotContain("aaaaa",JsonSerializer.Serialize(d));d=await ConfigureDiscord(true,7,d.Revision,null);var command=new DiscordAction(Guid.NewGuid(),d.Revision);var retries=await Task.WhenAll(browser.Send("/api/organization/notifications/test",command),browser.Send("/api/organization/notifications/test",command));var first=await Value<DiscordDeliveryView>(retries[0]);Assert.Equal(first,await Value<DiscordDeliveryView>(retries[1]));
        await NotificationTick();Assert.Single(receiver.Bodies);Assert.Equal(NotificationState.Delivered,Assert.Single(await NotificationRows()).State);await NotificationTick();Assert.Single(receiver.Bodies);
        var pending=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),d.Revision)));d=await ConfigureDiscord(true,7,d.Revision,DiscordUrl.Replace("aaaaa","bbbbb"));await NotificationTick();Assert.Single(receiver.Bodies);Assert.Equal(NotificationState.Disabled,(await NotificationRows()).Single(x=>x.Id==pending.Id).State);
        Assert.Equal(HttpStatusCode.Conflict,(await browser.Send("/api/organization/notifications",new DiscordSettingsCommand(Guid.NewGuid(),d.Revision-1,"stale",true,1))).StatusCode);
        await Value<DiscordDestinationView>(await browser.Send("/api/organization/notifications",new DiscordSettingsCommand(Guid.NewGuid(),d.Revision,"delete",false,0,Delete:true)));
        using var after=app.Services.CreateScope();var record=await after.ServiceProvider.GetRequiredService<OrganizationDatabase>().DiscordDestinations.SingleAsync();Assert.Empty(record.ProtectedCredential);Assert.Empty(record.MaskedIdentifier);Assert.True(record.Deleted);
    }
    [PostgresFact]public async Task DiscordFlightAnnouncementsNeedIndependentPerFlightConsentRevocationWinsAndEndMeansOnlySharingEnded()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ConfigureDiscord();using var pilot=await JoinPilot("external-pilot",InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var s=await StartLive(grant,"@everyone PRIVATE <script> public label");
        await NotificationTick();Assert.Empty(receiver.Bodies);await AllowDiscord(pilot,s);await NotificationTick();Assert.Single(receiver.Bodies);Assert.Equal(NotificationState.Delivered,Assert.Single(await NotificationRows()).State);var text=Encoding.UTF8.GetString(receiver.Bodies[0]);Assert.DoesNotContain("@everyone",text);Assert.DoesNotContain("latitude",text);Assert.DoesNotContain("longitude",text);
        await AllowDiscord(pilot,s,1,false);await Value<LiveSharingSession>(await LiveRequest("/api/member/live/stop",grant,new LiveSharingStop(1,Guid.NewGuid(),s.Id,1,"flightEnded")));await NotificationTick();Assert.Single(receiver.Bodies);
        var next=await StartLive(grant,"Second flight");await AllowDiscord(pilot,next);await AllowDiscord(pilot,next,1,false);await NotificationTick();Assert.Single(receiver.Bodies);Assert.Contains(await NotificationRows(),x=>x.State==NotificationState.Disabled);
    }
    [PostgresFact]public async Task DiscordRateLimitsAreBoundedAndAmbiguousAcknowledgmentsAreNeverRetriedAfterRestart()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ClockForLive();await ConfigureDiscord();receiver.Status=429;receiver.Response="{\"retry_after\":10}";
        await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));await NotificationTick();var first=Assert.Single(await NotificationRows());Assert.Equal(NotificationState.Pending,first.State);Assert.Equal(1,first.Attempts);await NotificationTick();Assert.Single(receiver.Bodies);
        for(var i=1;i<5;i++){liveClock!.Advance(60);await NotificationTick();}Assert.Equal(NotificationState.Failed,Assert.Single(await NotificationRows()).State);Assert.Equal(5,receiver.Bodies.Count);
        liveClock!.Advance(60);receiver.Status=200;receiver.Abort=true;await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));await NotificationTick();Assert.Equal(NotificationState.Ambiguous,(await NotificationRows())[^1].State);var count=receiver.Bodies.Count;
        await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();liveClock.Advance(60);await NotificationTick();Assert.Equal(count,receiver.Bodies.Count);Assert.Equal(NotificationState.Ambiguous,(await NotificationRows())[^1].State);
    }
    [PostgresFact]public async Task DiscordDestinationOrAdministratorRevocationCancelsPendingWorkAndCrossPilotConsentIsForbidden()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ConfigureDiscord();using var pilot=await JoinPilot("consent-owner",InvitationSecret(await Invite()));using var other=await JoinPilot("other-pilot",InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var s=await StartLive(grant);
        Assert.Equal(HttpStatusCode.NotFound,(await other.Send("/api/membership/discord/consent",new DiscordConsentCommand(Guid.NewGuid(),s.Id,0,1,true))).StatusCode);await AllowDiscord(pilot,s);await ConfigureDiscord(false,7,1,null);await NotificationTick();Assert.Empty(receiver.Bodies);
        await ConfigureDiscord(true,7,2,null);await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),3)));
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var admin=await db.Memberships.SingleAsync(x=>x.Role==OrganizationRoles.Administrator);admin.State=OrganizationMembershipState.Suspended;await db.SaveChangesAsync();}
        await NotificationTick();Assert.Empty(receiver.Bodies);Assert.All(await NotificationRows(),r=>Assert.Equal(NotificationState.Disabled,r.State));
    }
    [PostgresFact]public async Task DiscordConsentedStartAndEndRemainDistinctAndUnsentIntentsSurviveRestartWithoutResendingUnknownPosts()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ClockForLive();await ConfigureDiscord();using var pilot=await JoinPilot("ended-pilot",InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var s=await StartLive(grant,"Public flight");await AllowDiscord(pilot,s);await NotificationTick();Assert.Single(receiver.Bodies);
        await Value<LiveSharingSession>(await LiveRequest("/api/member/live/stop",grant,new LiveSharingStop(1,Guid.NewGuid(),s.Id,1,"flightEnded")));liveClock!.Advance(10);await NotificationTick();Assert.Equal(2,receiver.Bodies.Count);Assert.Contains("ended research-flight sharing",Encoding.UTF8.GetString(receiver.Bodies[1]));
        liveClock.Advance(10);var pending=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));await app.StopAsync();await app.DisposeAsync();await Start();browser.Connect(app);await browser.Session();await NotificationTick();Assert.Equal(NotificationState.Delivered,(await NotificationRows()).Single(x=>x.Id==pending.Id).State);Assert.Equal(3,receiver.Bodies.Count);
        liveClock.Advance(10);var unknown=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var q=await db.DiscordDeliveries.SingleAsync(x=>x.Id==unknown.Id);q.State=NotificationState.Sending;q.SenderInstance=Guid.NewGuid();await db.SaveChangesAsync();}
        await NotificationTick();Assert.Equal(NotificationState.Ambiguous,(await NotificationRows()).Single(x=>x.Id==unknown.Id).State);Assert.Equal(3,receiver.Bodies.Count);
    }
    [PostgresFact]public async Task DiscordPilotMembershipRevocationAndChangedDestinationConsentCannotReleaseUnsentFlightAnnouncements()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ConfigureDiscord();using var pilot=await JoinPilot("revoked-external",InvitationSecret(await Invite()));var grant=await LiveGrant(pilot);var s=await StartLive(grant);await AllowDiscord(pilot,s);
        using(var scope=app.Services.CreateScope()){var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();var m=await db.Memberships.SingleAsync(m=>m.UserId==s.PilotId);m.State=OrganizationMembershipState.Suspended;await db.SaveChangesAsync();}
        await NotificationTick();Assert.Empty(receiver.Bodies);Assert.Equal(NotificationState.Disabled,Assert.Single(await NotificationRows()).State);
    }

    async Task DiscordForbiddenRole(string role)
    {
        await NotificationAdmin();await ConfigureDiscord();
        {
            using var account=await JoinPilot("notification-"+role.ToLowerInvariant(),InvitationSecret(await Invite()));if(role!=OrganizationRoles.Pilot){await Change(await Member(account),role:role);await Login(account,"notification-"+role.ToLowerInvariant());}
            foreach(var path in new[]{"","/history","/announcements"})Assert.Equal(HttpStatusCode.Forbidden,(await account.Client.GetAsync("/api/organization/notifications"+path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await account.Send("/api/organization/notifications",new DiscordSettingsCommand(Guid.NewGuid(),1,"Forged",true,7,DiscordUrl))).StatusCode);
            foreach(var path in new[]{"/test","/disclose"})Assert.Equal(HttpStatusCode.Forbidden,(await account.Send("/api/organization/notifications"+path,new DiscordAction(Guid.NewGuid(),1,Guid.NewGuid(),true))).StatusCode);
        }
        using var anonymous=new Browser();anonymous.Connect(app);await anonymous.Session();Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.Client.GetAsync("/api/organization/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1),csrf:false)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1),origin:"https://evil.example")).StatusCode);
    }

    [PostgresFact]public Task DiscordPilotCannotReadConfigureTestOrDisclose()=>DiscordForbiddenRole(OrganizationRoles.Pilot);
    [PostgresFact]public Task DiscordReviewerCannotReadConfigureTestOrDisclose()=>DiscordForbiddenRole(OrganizationRoles.Reviewer);
    [PostgresFact]public Task DiscordManagerCannotReadConfigureTestOrDisclose()=>DiscordForbiddenRole(OrganizationRoles.Manager);

    [PostgresFact]public async Task DiscordAdministratorCancellationStopsUnsentWorkAndPreservesAnUnknownOutcome()
    {
        await using var receiver=await DiscordHttpsFixture.Create();discordTransport=receiver;await NotificationAdmin();await ConfigureDiscord();var queued=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));
        await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/history/"+queued.Id+"/cancel",new DiscordAction(Guid.NewGuid(),1)));await NotificationTick();Assert.Empty(receiver.Bodies);Assert.Equal(NotificationState.Disabled,Assert.Single(await NotificationRows()).State);
        receiver.Abort=true;var unknown=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/test",new DiscordAction(Guid.NewGuid(),1)));await NotificationTick();Assert.Equal(NotificationState.Ambiguous,(await NotificationRows()).Single(x=>x.Id==unknown.Id).State);
        var closed=await Value<DiscordDeliveryView>(await browser.Send("/api/organization/notifications/history/"+unknown.Id+"/cancel",new DiscordAction(Guid.NewGuid(),1)));Assert.Equal(NotificationState.Ambiguous,closed.State);Assert.Contains("may have received",closed.Reason);await NotificationTick();Assert.Single(receiver.Bodies);
    }

}
