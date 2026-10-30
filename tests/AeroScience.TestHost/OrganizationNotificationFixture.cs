using AeroScience.Tests;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AeroScience.Organization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
namespace AeroScience.TestHost;

// Private process fixture only. No alternate transport/configuration endpoint is compiled into the
// organization product. HTTPS verifies discord.com against this ephemeral fixture root.
internal sealed class OrganizationNotificationFixture:IDiscordTransport
{
    readonly HttpClient client;public int Count;public byte[] Last=[];
    OrganizationNotificationFixture(HttpClient client){this.client=client;}
    public Task<DiscordResponse> Send(DiscordWebhookAddress d,byte[] body,CancellationToken ct)=>DiscordTransport.Execute(client,d,body,ct);
    public static async Task Run(bool rateLimited=false)
    {
        using var certificate=DiscordFixtureCertificate.Create();
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,0,l=>l.UseHttps(certificate.Server)));await using var receiver=builder.Build();OrganizationNotificationFixture? fixture=null;
        receiver.MapPost("/api/v10/webhooks/{id}/{token}",async(HttpContext c)=>{using var body=new MemoryStream();await c.Request.Body.CopyToAsync(body);fixture!.Last=body.ToArray();Interlocked.Increment(ref fixture.Count);if(rateLimited){c.Response.StatusCode=429;await c.Response.WriteAsJsonAsync(new{retry_after=2});}else await c.Response.WriteAsJsonAsync(new{id="123456789012345678"});});await receiver.StartAsync();var port=new Uri(receiver.Urls.Single()).Port;
        using var handler=new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false,ConnectCallback=async(context,ct)=>{if(context.DnsEndPoint.Host!="discord.com"||context.DnsEndPoint.Port!=443)throw new InvalidOperationException("Unexpected fixture endpoint.");var s=new Socket(SocketType.Stream,ProtocolType.Tcp);try{await s.ConnectAsync(IPAddress.Loopback,port,ct);return new NetworkStream(s,true);}catch{s.Dispose();throw;}}};handler.SslOptions=certificate.ClientOptions();
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};fixture=new(client);
        await using var app=OrganizationApplication.Build([],b=>b.Services.AddSingleton<IDiscordTransport>(fixture));
        app.MapGet("/api/testing/discord-receiver",()=>new{count=fixture.Count,last=System.Text.Encoding.UTF8.GetString(fixture.Last)}).RequireAuthorization(OrganizationApplication.PublishPolicy);
        await OrganizationApplication.Initialize(app);await app.RunAsync();
    }
}
