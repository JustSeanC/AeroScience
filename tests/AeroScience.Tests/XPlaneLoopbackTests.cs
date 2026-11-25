using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AeroScience.Shared;
using AeroScience.XPlane;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AeroScience.Tests;

// Real HTTP/WebSocket transports against an ephemeral loopback fixture. These
// tests never contact port 8086, a simulator, a scientific store or the internet.
public sealed class XPlaneLoopbackTests
{
    [Fact]
    public async Task BuiltInTransportsDiscoverSubscribeConfirmAndUnsubscribeReadOnly()
    {
        await using var host = await LocalApi.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var provider = new XPlaneTelemetryProvider(host.Options);
        var stream = provider.ReadAsync(stop.Token).GetAsyncEnumerator();
        TelemetryUpdate? received = null;
        try
        {
            while (await stream.MoveNextAsync()) if (stream.Current.Frame is not null) { received = stream.Current; break; }
            Assert.NotNull(received); Assert.True(received.Frame!.IsValid);
            Assert.Equal("xplane12", received.Connection.Origin!.SimulatorId);
            Assert.Contains(host.Requests, r => r.Path == "/api/v1");
            Assert.All(host.Requests, r => Assert.Equal("GET", r.Method));
            Assert.InRange(host.Options.Port, 1, 65535); Assert.NotEqual(8086, host.Options.Port);
        }
        finally { await stream.DisposeAsync(); }
        await host.Unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "dataref_subscribe_values", "dataref_unsubscribe_values" }, host.Messages.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NeitherHttpNorWebsocketFollowsARedirect(bool websocket)
    {
        await using var host = await LocalApi.Start(websocket ? "ws-redirect" : "http-redirect");
        using var api = new XPlaneWebApiClient(host.Options);
        if (websocket)
        {
            await api.NegotiateAsync(default);
            Assert.Equal("RedirectRefused", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.OpenAsync(default))).Code);
        }
        else Assert.Equal("RedirectRefused", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.NegotiateAsync(default))).Code);
        Assert.DoesNotContain(host.Requests, r => r.Path == "/redirect-target");
    }

    [Fact]
    public async Task WebsocketHandshakeSecurityDenialRemainsUnavailableRatherThanDisconnected()
    {
        await using var host = await LocalApi.Start("ws-denied"); using var api = new XPlaneWebApiClient(host.Options);
        await api.NegotiateAsync(default);
        var failure = await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.OpenAsync(default));
        Assert.Equal("ApiAccessDenied", failure.Code); Assert.Equal(ConnectionState.Unavailable, failure.State);
        Assert.DoesNotContain(host.Requests, r => r.Path.EndsWith("/value"));
    }

    [Fact]
    public async Task AConfiguredProxyIsNeverConsultedByTheProductionHttpHandler()
    {
        await using var host = await LocalApi.Start();
        var proxy = new RefusingProxy(); var handler = XPlaneWebApiClient.CreateHandler(); handler.Proxy = proxy;
        using var api = new XPlaneWebApiClient(host.Options, handler, new XPlaneSocket(), TimeProvider.System);
        await api.NegotiateAsync(default); await api.OpenAsync(default);
        Assert.Equal(0, proxy.Calls);
    }

    [Fact]
    public async Task ChunkedHttpPayloadIsBoundedWithoutTrustingContentLength()
    {
        await using var host = await LocalApi.Start("http-oversized"); using var api = new XPlaneWebApiClient(host.Options);
        Assert.Equal("OversizedHttpResponse", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.NegotiateAsync(default))).Code);
    }

    private sealed class RefusingProxy : IWebProxy
    {
        internal int Calls;
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Proxy routing is forbidden in this fixture."); }
        public bool IsBypassed(Uri host) { Interlocked.Increment(ref Calls); throw new InvalidOperationException("Proxy resolution is forbidden in this fixture."); }
    }

    [Theory]
    [InlineData("binary", "NonTextWebSocketMessage")]
    [InlineData("oversized", "OversizedWebSocketMessage")]
    public async Task RealSocketRejectsUnexpectedTypesAndFragmentedOversizedMessages(string mode, string code)
    {
        await using var host = await LocalApi.Start(mode); using var api = new XPlaneWebApiClient(host.Options);
        await api.NegotiateAsync(default); await api.OpenAsync(default);
        Assert.Equal(code, (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.ReceiveAsync(default))).Code);
    }

    private sealed class LocalApi : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly XPlaneFixture fixture = new(700);
        internal readonly ConcurrentQueue<(string Method, string Path)> Requests = new();
        internal readonly ConcurrentQueue<string> Messages = new();
        internal readonly TaskCompletionSource Unsubscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal XPlaneOptions Options = null!;
        private LocalApi(WebApplication app) => this.app = app;
        internal static async Task<LocalApi> Start(string mode = "normal")
        {
            var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build(); var host = new LocalApi(app); app.UseWebSockets();
            app.Use(async (context, next) => { host.Requests.Enqueue((context.Request.Method, context.Request.Path)); await next(context); });
            app.MapGet("/redirect-target", () => Results.Json(new { unexpected = true }));
            app.MapGet("/api/capabilities", async context =>
            {
                if (mode == "http-redirect") { context.Response.Redirect("/redirect-target"); return; }
                if (mode == "http-oversized")
                {
                    for (var i = 0; i < 17; i++)
                    {
                        await context.Response.Body.WriteAsync(new byte[4096], context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                    return;
                }
                await context.Response.WriteAsJsonAsync(new Dictionary<string, object> {
                    ["api"] = new { versions = new[] { "v1" } }, ["x-plane"] = new { version = "12.4-fixture" } });
            });
            app.MapGet("/api/v1/datarefs", (HttpRequest request) =>
            {
                var name = request.Query["filter[name]"].ToString();
                var d = host.fixture.Refs.Values.Single(r => r.Definition.Name == name);
                return Results.Json(new { data = new[] { new { id = d.Id, name, value_type = d.Definition.ValueType } } });
            });
            app.MapGet("/api/v1/datarefs/{id:long}/value", (long id) =>
            {
                var d = host.fixture.Refs.Values.Single(r => r.Id == id);
                if (d.Definition.Key == "latitude") Interlocked.Increment(ref host.fixture.Phase);
                return Results.Json(new { data = host.fixture.Value(d.Definition.Key, host.fixture.Phase) });
            });
            app.MapGet("/api/v1", async context =>
            {
                if (mode == "ws-redirect") { context.Response.Redirect("/redirect-target"); return; }
                if (mode == "ws-denied") { context.Response.StatusCode = 403; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                try
                {
                    if (mode == "binary") { await socket.SendAsync(new byte[4], WebSocketMessageType.Binary, true, context.RequestAborted); return; }
                    if (mode == "oversized")
                    {
                        for (var i = 0; i < 17; i++) await socket.SendAsync(new byte[4096], WebSocketMessageType.Text, i == 16, context.RequestAborted);
                        return;
                    }
                    var buffer = new byte[8192];
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        using var message = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                        var type = message.RootElement.GetProperty("type").GetString()!; host.Messages.Enqueue(type);
                        if (type == "dataref_unsubscribe_values") { host.Unsubscribed.TrySetResult(); break; }
                        var id = message.RootElement.GetProperty("req_id").GetInt64();
                        await socket.SendAsync(XPlaneFixture.Ack(id), WebSocketMessageType.Text, true, context.RequestAborted);
                        await socket.SendAsync(host.fixture.Seed(), WebSocketMessageType.Text, true, context.RequestAborted);
                    }
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host.Options = XPlaneFixture.FastOptions with { Port = new Uri(address).Port };
            return host;
        }
        public async ValueTask DisposeAsync()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(stop.Token); await app.DisposeAsync();
        }
    }
}
