using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.XPlane;

internal sealed record XPlaneProtocolInfo(string ApiVersion, string? ReportedVersion);

// Transport seam is internal and used by controlled fixtures. No general send API
// is exposed by the provider or its public options.
internal interface IXPlaneSocket : IDisposable
{
    bool IsOpen { get; }
    Task ConnectAsync(Uri endpoint, CancellationToken token);
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token);
    Task<byte[]?> ReceiveAsync(int maximumBytes, CancellationToken token);
    Task CloseOutputAsync(CancellationToken token);
    void Abort();
}

internal sealed class XPlaneSocket : IXPlaneSocket
{
    private readonly ClientWebSocket socket = new();
    private readonly HttpMessageInvoker handshake = new(XPlaneWebApiClient.CreateHandler(), disposeHandler: true);
    internal XPlaneSocket()
    {
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(3);
    }
    public bool IsOpen => socket.State == WebSocketState.Open;
    public async Task ConnectAsync(Uri endpoint, CancellationToken token)
    {
        try { await socket.ConnectAsync(endpoint, handshake, token); }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Forbidden)
        { throw new XPlaneProtocolException("ApiAccessDenied"); }
        catch (WebSocketException) when ((int)socket.HttpStatusCode is >= 300 and <= 399)
        { throw new XPlaneProtocolException("RedirectRefused"); }
    }
    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) =>
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    public async Task<byte[]?> ReceiveAsync(int maximumBytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new XPlaneProtocolException("NonTextWebSocketMessage");
            if (output.Length + result.Count > maximumBytes) throw new XPlaneProtocolException("OversizedWebSocketMessage");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return output.ToArray();
        }
    }
    public void Abort() => socket.Abort();
    public Task CloseOutputAsync(CancellationToken token) =>
        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "AeroScience shutdown", token);
    public void Dispose() { socket.Dispose(); handshake.Dispose(); }
}

internal sealed class XPlaneWebApiClient : IDisposable
{
    private readonly XPlaneOptions options;
    private readonly TimeProvider time;
    private readonly HttpClient http;
    private readonly IXPlaneSocket socket;
    private readonly HashSet<long> discoveredIds = [];
    private string? version;
    internal bool SocketOpen => socket.IsOpen;

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        MaxConnectionsPerServer = 8, ConnectTimeout = TimeSpan.FromSeconds(3)
    };
    internal XPlaneWebApiClient(XPlaneOptions options, TimeProvider? time = null)
        : this(options, CreateHandler(), new XPlaneSocket(), time ?? TimeProvider.System) { }
    internal XPlaneWebApiClient(XPlaneOptions options, HttpMessageHandler handler, IXPlaneSocket socket, TimeProvider time)
    {
        options.Validate(); this.options = options; this.socket = socket; this.time = time;
        http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<XPlaneProtocolInfo> NegotiateAsync(CancellationToken token)
    {
        var root = await GetAsync("api/capabilities", null, "legacy", token);
        string? reported = null;
        if (root is null) version = "v1"; // capabilities was introduced with API v2.
        else
        {
            if (!XPlaneProtocol.Property(root.Value, "api", out var api)
                || !XPlaneProtocol.Property(api, "versions", out var versions)
                || versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() is < 1 or > 16
                || !XPlaneProtocol.Property(root.Value, "x-plane", out var simulator))
                throw new XPlaneProtocolException("MalformedCapabilities");
            var offered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in versions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 16 } v
                    || !offered.Add(v)) throw new XPlaneProtocolException("MalformedCapabilities");
            }
            version = offered.Contains("v1") ? "v1" : offered.Contains("v2") ? "v2" : null;
            if (version is null) throw new XPlaneProtocolException("UnsupportedApiVersion");
            reported = XPlaneProtocol.Text(simulator, "version", 32);
        }
        return new(version, reported);
    }

    internal async Task<XPlaneDiscovery> DiscoverAsync(CancellationToken token)
    {
        RequireVersion();
        var found = new List<XPlaneResolvedDataref>(); var unavailable = new List<string>();
        discoveredIds.Clear();
        foreach (var definition in XPlaneDatarefs.All)
        {
            var matches = new List<XPlaneResolvedDataref>();
            for (var page = 0; page < options.MaximumDiscoveryPages; page++)
            {
                var start = page * options.DiscoveryPageSize;
                var query = "filter%5Bname%5D=" + Uri.EscapeDataString(definition.Name)
                    + "&start=" + start.ToString(CultureInfo.InvariantCulture)
                    + "&limit=" + options.DiscoveryPageSize.ToString(CultureInfo.InvariantCulture);
                var root = await GetAsync($"api/{version}/datarefs", query, "invalid_dataref_name", token);
                if (root is null) break;
                if (!XPlaneProtocol.Property(root.Value, "data", out var data) || data.ValueKind != JsonValueKind.Array
                    || data.GetArrayLength() > options.DiscoveryPageSize) throw new XPlaneProtocolException("MalformedDatarefList");
                foreach (var item in data.EnumerateArray())
                {
                    var name = XPlaneProtocol.Text(item, "name"); var type = XPlaneProtocol.Text(item, "value_type", 32);
                    var id = XPlaneProtocol.Id(item, "id");
                    if (name != definition.Name || matches.Count != 0) throw new XPlaneProtocolException("UnexpectedOrDuplicateDataref");
                    if (type != definition.ValueType)
                    {
                        if (definition.Required) throw new XPlaneProtocolException("RequiredDatarefTypeMismatch");
                        unavailable.Add(definition.Key); break;
                    }
                    matches.Add(new(definition, id));
                }
                if (data.GetArrayLength() < options.DiscoveryPageSize) break;
                if (page == options.MaximumDiscoveryPages - 1) throw new XPlaneProtocolException("DiscoveryPageLimit");
            }
            if (matches.Count == 0) unavailable.Add(definition.Key);
            else
            {
                if (!discoveredIds.Add(matches[0].Id)) throw new XPlaneProtocolException("CollidingDatarefIds");
                found.Add(matches[0]);
            }
        }
        if (found.Count == 0) throw new XPlaneProtocolException("WaitingForLoadedFlight", ConnectionState.Initializing);
        if (XPlaneDatarefs.All.Any(d => d.Required && !found.Any(f => f.Definition.Key == d.Key)))
            throw new XPlaneProtocolException("RequiredDatarefUnavailable");
        return new(found.AsReadOnly(), unavailable.Distinct().Order().ToArray());
    }

    internal async Task<JsonElement?> ReadValueAsync(long id, CancellationToken token)
    {
        RequireVersion();
        if (!discoveredIds.Contains(id)) throw new XPlaneProtocolException("UndiscoveredDatarefId");
        var root = await GetAsync($"api/{version}/datarefs/{id.ToString(CultureInfo.InvariantCulture)}/value", null,
            "invalid_dataref_id", token);
        if (root is null) return null;
        if (!XPlaneProtocol.Property(root.Value, "data", out var value)) throw new XPlaneProtocolException("MissingValueEnvelope");
        return value;
    }

    internal async Task OpenAsync(CancellationToken token)
    {
        RequireVersion();
        using var deadline = new CancellationTokenSource(options.RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        await socket.ConnectAsync(options.Endpoint($"api/{version}", socket: true), linked.Token);
    }
    internal Task SubscribeAsync(long requestId, IReadOnlyList<long> ids, CancellationToken token) =>
        SendSubscriptionAsync(requestId, ids, subscribe: true, token);
    internal Task UnsubscribeAsync(long requestId, IReadOnlyList<long> ids, CancellationToken token) =>
        SendSubscriptionAsync(requestId, ids, subscribe: false, token);
    private async Task SendSubscriptionAsync(long requestId, IReadOnlyList<long> ids, bool subscribe, CancellationToken token)
    {
        if (requestId is < 1 or > int.MaxValue || ids.Count is < 1 or > 128 || ids.Distinct().Count() != ids.Count
            || ids.Any(id => !discoveredIds.Contains(id))) throw new XPlaneProtocolException("InvalidSubscriptionIdentity");
        // These are the only two application messages this client can generate.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { req_id = requestId,
            type = subscribe ? "dataref_subscribe_values" : "dataref_unsubscribe_values",
            @params = new { datarefs = ids.Select(id => new { id }).ToArray() } });
        using var deadline = new CancellationTokenSource(options.RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        await socket.SendAsync(bytes, linked.Token);
    }
    internal Task<byte[]?> ReceiveAsync(CancellationToken token) => socket.ReceiveAsync(options.MaximumMessageBytes, token);
    internal void Abort() => socket.Abort();
    internal Task CloseOutputAsync(CancellationToken token) => socket.CloseOutputAsync(token);
    private void RequireVersion()
    {
        if (version is not ("v1" or "v2")) throw new XPlaneProtocolException("ProtocolNotNegotiated");
    }

    private async Task<JsonElement?> GetAsync(string path, string? query, string? missingCode, CancellationToken token)
    {
        // Path/query are generated only by the typed read operations above. No URL,
        // method, host, credentials or redirect target is accepted from the server.
        var builder = new UriBuilder(options.Endpoint(path)) { Query = query ?? "" };
        using var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = new CancellationTokenSource(options.RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new XPlaneProtocolException("ApiAccessDenied");
        if ((int)response.StatusCode is >= 300 and <= 399) throw new XPlaneProtocolException("RedirectRefused");
        if (response.StatusCode == HttpStatusCode.NotFound && missingCode == "legacy") return null;
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NotFound)
            throw new XPlaneProtocolException("HttpFailure", (int)response.StatusCode >= 500 ? ConnectionState.Disconnected : ConnectionState.Unavailable);
        if (response.Content.Headers.ContentLength > options.MaximumMessageBytes) throw new XPlaneProtocolException("OversizedHttpResponse");
        using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, linked.Token);
            if (count == 0) break;
            if (output.Length + count > options.MaximumMessageBytes) throw new XPlaneProtocolException("OversizedHttpResponse");
            output.Write(buffer, 0, count);
        }
        var root = XPlaneProtocol.Parse(output.GetBuffer().AsMemory(0, checked((int)output.Length)), options.MaximumMessageBytes);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (missingCode is null || XPlaneProtocol.Text(root, "error_code", 64) != missingCode)
                throw new XPlaneProtocolException("UnexpectedNotFoundContract");
            return null;
        }
        return root;
    }

    public void Dispose() { socket.Abort(); socket.Dispose(); http.Dispose(); }
}
