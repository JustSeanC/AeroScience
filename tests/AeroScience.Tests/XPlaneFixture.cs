using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AeroScience.Shared;
using AeroScience.XPlane;
using Microsoft.AspNetCore.WebUtilities;

namespace AeroScience.Tests;

// Synthetic API fixture only. No real simulator endpoint, credentials or user stores.
internal sealed class XPlaneFixture
{
    internal readonly ConcurrentQueue<(string Method, Uri Uri)> Requests = new();
    internal readonly ConcurrentDictionary<string, object?> Overrides = new();
    internal readonly ConcurrentDictionary<string, string> RawOverrides = new();
    internal readonly HashSet<string> Missing = [];
    internal readonly Dictionary<string, string> Types = [];
    internal readonly Dictionary<string, XPlaneResolvedDataref> Refs;
    internal readonly FakeSocket Socket;
    internal int Phase, CapabilityStatus = 200, DelayMilliseconds;
    internal int ActiveReads, MaximumActiveReads;
    internal bool FreezeClocks, SeedBeforeAck, SuppressSubscriptionReply;
    internal string[] Versions = ["v3", "v2", "v1"];
    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? OverrideResponse;
    internal bool RejectSubscription;

    internal XPlaneFixture(long firstId = 9007199254740993)
    {
        Refs = XPlaneDatarefs.All.Select((d, i) => new XPlaneResolvedDataref(d, firstId + i)).ToDictionary(d => d.Definition.Key);
        Socket = new(this);
    }
    internal XPlaneDiscovery Discovery => new(Refs.Values.Where(d => !Missing.Contains(d.Definition.Key)).ToArray(), Missing.ToArray());
    internal XPlaneWebApiClient Client(XPlaneOptions? options = null) =>
        new(options ?? new(), new Handler(this), Socket, TimeProvider.System);
    internal XPlaneTelemetryProvider Provider(XPlaneOptions? options = null) =>
        new(options ?? FastOptions, TimeProvider.System, () => Client(options ?? FastOptions));
    internal static XPlaneOptions FastOptions => new()
    { ConfirmationInterval = TimeSpan.FromMilliseconds(25), RequestTimeout = TimeSpan.FromSeconds(1), ShutdownTimeout = TimeSpan.FromMilliseconds(200) };
    internal static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    internal static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
    internal static HttpResponseMessage Response(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    internal JsonElement Value(string key, int phase)
    {
        if (RawOverrides.TryGetValue(key, out var raw))
        { using var doc = JsonDocument.Parse(raw); return doc.RootElement.Clone(); }
        if (Overrides.TryGetValue(key, out var changed)) return Json(changed);
        object value = key switch
        {
            "latitude" => 34d, "longitude" => -117d, "msl" => 1000d, "agl" => 100d,
            "ias" => 120d, "tas" => 100d, "groundspeed" => 90d, "heading" => -10d, "track" => 370d,
            "pitch" => 5d, "bank" => -15d, "vertical-speed" => 2d, "mach" => .4d,
            "aircraft" => Convert.ToBase64String(Encoding.UTF8.GetBytes("Aéro Cessna 172\0")),
            "registration" => Convert.ToBase64String(Encoding.UTF8.GetBytes("N123AS\0")),
            "aircraft-type" => Convert.ToBase64String(Encoding.UTF8.GetBytes("C172\0")),
            "aircraft-token" => Convert.ToBase64String(Encoding.UTF8.GetBytes("Aircraft/Fixture/C172.acf\0")),
            "paused" or "replay" => 0,
            "running-seconds" => 100d + (FreezeClocks ? 0 : phase),
            "flight-seconds" => 10d + (FreezeClocks ? 0 : phase),
            "zulu-seconds" => 36000d, "local-day" or "zulu-day" => 100,
            "requested-rate" => 1, "actual-rate" or "dilation-rate" or "distance-rate" => 1d,
            "build" => 124001, "temperature" => 15d, "pressure" => 101325d,
            "wind-speed" => 10d, "wind-direction" => 270d, "density" => 1.225d,
            _ => throw new InvalidOperationException("Unknown synthetic channel")
        };
        return Json(value);
    }
    internal byte[] Seed(int phase = 0) => Bytes(new { type = "dataref_update_values", data =
        Discovery.Found.ToDictionary(d => d.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), d => Value(d.Definition.Key, phase)) });
    internal byte[] Update(string key, object value) => Bytes(new { type = "dataref_update_values", data =
        new Dictionary<string, object> { [Refs[key].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = value } });
    internal static byte[] Ack(long id, bool success = true) => Bytes(new { type = "result", req_id = id, success });

    private sealed class Handler(XPlaneFixture owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            owner.Requests.Enqueue((request.Method.Method, request.RequestUri!));
            if (owner.OverrideResponse is { } handler && await handler(request, token) is { } response) return response;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/capabilities") return owner.CapabilityStatus switch
            {
                200 => Response(new Dictionary<string, object> {
                    ["api"] = new { versions = owner.Versions }, ["x-plane"] = new { version = "12.4.0-fixture" } }),
                404 => Response(new { error_code = "not_found" }, HttpStatusCode.NotFound),
                _ => Response(new { error_code = "access_denied" }, (HttpStatusCode)owner.CapabilityStatus)
            };
            if (path is "/api/v1/datarefs" or "/api/v2/datarefs")
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var name = query["filter[name]"].ToString();
                var selected = owner.Refs.Values.SingleOrDefault(d => d.Definition.Name == name);
                if (selected is null || owner.Missing.Contains(selected.Definition.Key))
                    return Response(new { error_code = "invalid_dataref_name" }, HttpStatusCode.NotFound);
                var start = int.Parse(query["start"].ToString(), System.Globalization.CultureInfo.InvariantCulture);
                object[] rows = start == 0 ? [new { id = selected.Id, name,
                    value_type = owner.Types.GetValueOrDefault(selected.Definition.Key, selected.Definition.ValueType) }] : [];
                return Response(new { data = rows });
            }
            var parts = path.Split('/');
            if (parts.Length == 6 && parts[3] == "datarefs" && parts[5] == "value")
            {
                var id = long.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
                var selected = owner.Refs.Values.Single(d => d.Id == id);
                if (selected.Definition.Key == "latitude") Interlocked.Increment(ref owner.Phase);
                var count = Interlocked.Increment(ref owner.ActiveReads);
                int prior;
                do { prior = owner.MaximumActiveReads; if (prior >= count) break; }
                while (Interlocked.CompareExchange(ref owner.MaximumActiveReads, count, prior) != prior);
                try
                {
                    if (owner.DelayMilliseconds > 0) await Task.Delay(owner.DelayMilliseconds, token);
                    return Response(new { data = owner.Value(selected.Definition.Key, owner.Phase) });
                }
                finally { Interlocked.Decrement(ref owner.ActiveReads); }
            }
            throw new InvalidOperationException("Fixture received an unexpected API request: " + path);
        }
    }
    internal sealed class FakeSocket(XPlaneFixture owner) : IXPlaneSocket
    {
        private readonly Channel<byte[]?> incoming = Channel.CreateBounded<byte[]?>(8);
        private readonly CancellationTokenSource stopped = new();
        internal readonly ConcurrentQueue<JsonElement> Sent = new();
        internal Uri? Endpoint;
        public bool IsOpen { get; private set; }
        public Task ConnectAsync(Uri endpoint, CancellationToken token) { token.ThrowIfCancellationRequested(); Endpoint = endpoint; IsOpen = true; return Task.CompletedTask; }
        public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            var message = XPlaneProtocol.Parse(bytes, 65536); Sent.Enqueue(message);
            var type = message.GetProperty("type").GetString(); var id = message.GetProperty("req_id").GetInt64();
            if (type == "dataref_subscribe_values")
            {
                if (owner.SuppressSubscriptionReply) return;
                if (owner.SeedBeforeAck) await incoming.Writer.WriteAsync(owner.Seed(), token);
                await incoming.Writer.WriteAsync(Ack(id, !owner.RejectSubscription), token);
                if (!owner.SeedBeforeAck) await incoming.Writer.WriteAsync(owner.Seed(), token);
            }
        }
        public async Task<byte[]?> ReceiveAsync(int maximumBytes, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopped.Token);
            return await incoming.Reader.ReadAsync(linked.Token);
        }
        internal ValueTask Inject(byte[]? bytes, CancellationToken token = default) => incoming.Writer.WriteAsync(bytes, token);
        public void Abort() { IsOpen = false; stopped.Cancel(); }
        public void Dispose() { Abort(); stopped.Dispose(); }
    }
}

internal sealed class XPlaneTestClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero).AddTicks(ticks);
    internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
}

internal sealed class XPlaneAssemblerFixture
{
    internal readonly XPlaneFixture Api = new();
    internal readonly XPlaneTestClock Time = new();
    internal readonly XPlaneSnapshotAssembler Assembler;
    internal readonly Guid Epoch = Guid.NewGuid();
    internal XPlaneAssemblerFixture(XPlaneOptions? options = null)
    {
        Assembler = new(options ?? new(), Time, Api.Discovery,
            new(1, "xplane12", "12.4.0-fixture", "aeroscience.xplane.web", "p02-v1", "v1", Epoch, XPlaneDatarefs.MappingRevision), 1);
    }
    internal void Seed(bool acknowledge = true)
    {
        Assembler.Ingest(Epoch, Api.Seed());
        if (acknowledge) Assembler.Ingest(Epoch, XPlaneFixture.Ack(1));
    }
    internal void Confirm(int phase = 1, TimeSpan? perField = null)
    {
        var utc = Time.GetUtcNow(); var start = Time.GetTimestamp();
        var values = Api.Discovery.Found.OrderByDescending(d => d.Definition.Required).Select(d =>
        {
            Time.Advance(perField ?? TimeSpan.FromMilliseconds(1));
            return new XPlaneValueConfirmation(d.Id, Api.Value(d.Definition.Key, phase), Time.GetUtcNow(), Time.GetTimestamp());
        }).ToArray();
        Assembler.Confirm(Epoch, new(utc, start, Time.GetUtcNow(), Time.GetTimestamp(), values));
    }
}
