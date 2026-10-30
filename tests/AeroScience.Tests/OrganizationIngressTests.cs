using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AeroScience.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AeroScience.Tests;

public sealed class OrganizationIngressTests
{
    static int Port(){using var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();return ((IPEndPoint)socket.LocalEndpoint).Port;}
    [Fact]public async Task GatewayUsesExactHostAndAuthenticatedLoopbackWithoutForwardedTrust()
    {
        var publicPort=Port();var internalPort=Port();while(internalPort==publicPort)internalPort=Port();
        var settings=new OrganizationSettings("",new("https://research.example.org"),false,null,null,null,[]){AuthenticatedLocalIngress=true,IngressPublicPort=publicPort,IngressApplicationPort=internalPort};
        using var gateway=new OrganizationIngress(settings);var builder=WebApplication.CreateBuilder();builder.WebHost.ConfigureKestrel(o=>{o.Listen(IPAddress.Loopback,publicPort);o.Listen(IPAddress.Loopback,internalPort);});await using var app=builder.Build();app.Use((c,next)=>gateway.Invoke(c,next));
        app.Run(async c=>await c.Response.WriteAsJsonAsync(new{scheme=c.Request.Scheme,host=c.Request.Host.Value,authorization=c.Request.Headers.Authorization.ToString(),cookie=c.Request.Headers.Cookie.ToString(),origin=c.Request.Headers.Origin.ToString(),csrf=c.Request.Headers["X-AeroScience-Organization-CSRF"].ToString(),forwarded=c.Request.Headers.Keys.Any(k=>k.StartsWith("X-Forwarded",StringComparison.OrdinalIgnoreCase)||k.StartsWith("X-Railway",StringComparison.OrdinalIgnoreCase)||k.Contains("Ingress-Proof")),body=await new StreamReader(c.Request.Body).ReadToEndAsync()}));
        await app.StartAsync();using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false,AllowAutoRedirect=false});
        HttpRequestMessage Message(int port,string host="research.example.org"){
            var r=new HttpRequestMessage(HttpMethod.Post,$"http://127.0.0.1:{port}/inspect?bounded=1"){Content=new StringContent("original body")};r.Headers.Host=host;r.Headers.Add("Authorization","Bearer synthetic");r.Headers.Add("Cookie","test=original");r.Headers.Add("Origin","https://research.example.org");r.Headers.Add("X-AeroScience-Organization-CSRF","synthetic-csrf");r.Headers.Add("X-Forwarded-Proto","https");r.Headers.Add("X-Forwarded-For","127.0.0.1");r.Headers.Add("X-Forwarded-Host","victim.example.org");r.Headers.Add("X-Railway-Edge","forged");r.Headers.Add(OrganizationIngress.ProofHeader,"forged");return r;
        }
        using(var request=Message(publicPort)){using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();var json=await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();Assert.Equal("https",json.GetProperty("scheme").GetString());Assert.Equal("research.example.org",json.GetProperty("host").GetString());Assert.Equal("original body",json.GetProperty("body").GetString());Assert.Equal("test=original",json.GetProperty("cookie").GetString());Assert.Equal("synthetic-csrf",json.GetProperty("csrf").GetString());Assert.Equal("https://research.example.org",json.GetProperty("origin").GetString());Assert.False(json.GetProperty("forwarded").GetBoolean());}
        using(var request=Message(publicPort,"attacker.example.org"))Assert.Equal(HttpStatusCode.BadRequest,(await client.SendAsync(request)).StatusCode);
        using(var request=Message(internalPort))Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(request)).StatusCode);
        // HTTP/2 may split Cookie fields. The gateway must preserve their cookie
        // meaning while signing the exact single HTTP/1 field it forwards locally.
        var split=new DefaultHttpContext();split.Connection.LocalPort=publicPort;split.Request.Host=new("research.example.org");split.Request.Method="GET";split.Request.Path="/cookies";split.Request.Headers.Cookie=new Microsoft.Extensions.Primitives.StringValues(["alpha=one","beta=two"]);split.Response.Body=new MemoryStream();await gateway.Invoke(split,_=>throw new InvalidOperationException("Public traffic must not reach the application directly."));Assert.Equal(200,split.Response.StatusCode);split.Response.Body.Position=0;var splitBody=await System.Text.Json.JsonDocument.ParseAsync(split.Response.Body);Assert.Equal("alpha=one; beta=two",splitBody.RootElement.GetProperty("cookie").GetString());
        using(var request=Message(publicPort)){request.Content=new StringContent(new string('x',32769));Assert.Equal(HttpStatusCode.RequestEntityTooLarge,(await client.SendAsync(request)).StatusCode);}
        await app.StopAsync();
    }
    [Fact]public void ProofIsRequestBoundExpiringSingleUseAndCannotSurviveReplacement()
    {
        var settings=new OrganizationSettings("",new("https://research.example.org"),false,null,null,null,[]);using var gateway=new OrganizationIngress(settings);using var replacement=new OrganizationIngress(settings);const long now=2000000000;var nonce=new string('a',32);var proof=gateway.Sign("original",now,nonce);
        Assert.False(gateway.Verify(proof,"tampered",now));Assert.False(gateway.Verify(proof,"original",now+31));Assert.False(gateway.Verify(proof,"original",now-3));Assert.False(replacement.Verify(proof,"original",now));Assert.True(gateway.Verify(proof,"original",now));Assert.False(gateway.Verify(proof,"original",now));
        Assert.False(gateway.Verify("invalid","original",now));
    }
}
