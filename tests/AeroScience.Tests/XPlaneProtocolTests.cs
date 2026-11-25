using System.Net;
using System.Text;
using System.Text.Json;
using AeroScience.XPlane;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace AeroScience.Tests;

public sealed class XPlaneProtocolTests
{
    [Theory]
    [InlineData(200, "v1")] [InlineData(404, "v1")]
    public async Task CapabilityDiscoveryAndLegitimateLegacy404UseV1(int status, string version)
    {
        var fixture = new XPlaneFixture { CapabilityStatus = status }; using var api = fixture.Client();
        var result = await api.NegotiateAsync(default); Assert.Equal(version, result.ApiVersion);
        Assert.Equal(status == 404 ? null : "12.4.0-fixture", result.ReportedVersion);
        var refs = await api.DiscoverAsync(default);
        Assert.Equal(fixture.Refs.Count, refs.Found.Count);
        Assert.Equal(9007199254740993, refs.Found[0].Id); // No conversion through IEEE-754 doubles.
        var value = await api.ReadValueAsync(refs.Found[0].Id, default); Assert.Equal(34, value!.Value.GetDouble());
        Assert.All(fixture.Requests, r => Assert.Equal("GET", r.Method));
        Assert.All(fixture.Requests.Skip(1), r => Assert.StartsWith("/api/v1/", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task AdvertisedV2SupportsTheSameReadOnlyContractWhenV1IsNotAdvertised()
    {
        var fixture = new XPlaneFixture { Versions = ["v3", "v2"] }; using var api = fixture.Client();
        Assert.Equal("v2", (await api.NegotiateAsync(default)).ApiVersion);
        await api.DiscoverAsync(default); await api.OpenAsync(default);
        Assert.Equal("ws://127.0.0.1:8086/api/v2", fixture.Socket.Endpoint!.AbsoluteUri);
    }

    [Theory]
    [InlineData("{\"api\":{\"versions\":[\"v3\"]},\"x-plane\":{\"version\":\"12.4\"}}", "UnsupportedApiVersion")]
    [InlineData("{\"api\":{\"versions\":[\"v1\",\"v1\"]},\"x-plane\":{\"version\":\"12.4\"}}", "MalformedCapabilities")]
    [InlineData("{\"api\":{\"versions\":[]}}", "MalformedCapabilities")]
    [InlineData("{\"api\":{\"versions\":[1]},\"x-plane\":{\"version\":\"12.4\"}}", "MalformedCapabilities")]
    [InlineData("{\"api\":{\"versions\":[\"v1\"]},\"x-plane\":{\"version\":42}}", "InvalidContractText")]
    public async Task UnsupportedOrMalformedAdvertisedContractsAreRejected(string body, string code)
    {
        var f = new XPlaneFixture { OverrideResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(
            new(HttpStatusCode.OK) { Content = new StringContent(body) }) };
        using var api = f.Client();
        Assert.Equal(code, (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.NegotiateAsync(default))).Code);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData(403, "ApiAccessDenied")] [InlineData(302, "RedirectRefused")]
    public async Task DenialAndRedirectDoNotFallBackToOlderVersionsOrFollowTargets(int status, string code)
    {
        var f = new XPlaneFixture { CapabilityStatus = status }; using var api = f.Client();
        Assert.Equal(code, (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.NegotiateAsync(default))).Code);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task MissingOptionalDatarefIsIndependentOfRequiredCoreDiscovery()
    {
        var f = new XPlaneFixture(); f.Missing.Add("agl"); f.Types["density"] = "double";
        using var api = f.Client(); await api.NegotiateAsync(default); var discovery = await api.DiscoverAsync(default);
        Assert.Contains("agl", discovery.Unavailable); Assert.Contains("density", discovery.Unavailable);
        Assert.DoesNotContain(discovery.Found, d => d.Definition.Key is "agl" or "density");
    }

    [Theory]
    [InlineData(false, "RequiredDatarefUnavailable")] [InlineData(true, "RequiredDatarefTypeMismatch")]
    public async Task MissingOrWrongRequiredTypesCannotProduceFrames(bool wrongType, string code)
    {
        var f = new XPlaneFixture();
        if (wrongType) f.Types["latitude"] = "float"; else f.Missing.Add("latitude");
        using var api = f.Client(); await api.NegotiateAsync(default);
        Assert.Equal(code, (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.DiscoverAsync(default))).Code);
    }

    [Fact]
    public async Task EmptyDatarefRegistryReportsWaitingForFlight()
    {
        var f = new XPlaneFixture(); foreach (var d in f.Refs.Keys) f.Missing.Add(d);
        using var api = f.Client(); await api.NegotiateAsync(default);
        var failure = await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.DiscoverAsync(default));
        Assert.Equal("WaitingForLoadedFlight", failure.Code); Assert.Equal(AeroScience.Shared.ConnectionState.Initializing, failure.State);
    }

    [Fact]
    public async Task PaginationIsBoundedAndKeepsTheExactNameFilter()
    {
        var f = new XPlaneFixture(); using var api = f.Client(new() { DiscoveryPageSize = 1 });
        await api.NegotiateAsync(default); await api.DiscoverAsync(default);
        var pages = f.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/datarefs")).ToArray();
        Assert.Equal(f.Refs.Count * 2, pages.Length);
        Assert.All(pages, p => Assert.False(string.IsNullOrEmpty(QueryHelpers.ParseQuery(p.Uri.Query)["filter[name]"].ToString())));
        Assert.All(pages, p => Assert.Contains(QueryHelpers.ParseQuery(p.Uri.Query)["start"].ToString(), new[] { "0", "1" }));
    }

    [Theory]
    [InlineData("unknown-name", "UnexpectedOrDuplicateDataref")]
    [InlineData("duplicate-name", "UnexpectedOrDuplicateDataref")]
    [InlineData("colliding-id", "CollidingDatarefIds")]
    [InlineData("fractional-id", "InvalidNumericId")]
    [InlineData("overflow-id", "InvalidNumericId")]
    public async Task DiscoveryRejectsAmbiguousNamesAndUnsafeNumericIds(string mode, string code)
    {
        var f = new XPlaneFixture();
        f.OverrideResponse = (request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/datarefs")) return Task.FromResult<HttpResponseMessage?>(null);
            var name = QueryHelpers.ParseQuery(request.RequestUri.Query)["filter[name]"].ToString();
            var d = f.Refs.Values.Single(r => r.Definition.Name == name);
            object id = mode == "fractional-id" ? 1.5 : mode == "overflow-id" ? 1e30 : 1L;
            var row = new { id, name = mode == "unknown-name" ? "sim/unknown" : name, value_type = d.Definition.ValueType };
            return Task.FromResult<HttpResponseMessage?>(XPlaneFixture.Response(new { data = mode == "duplicate-name" ? new[] { row, row } : [row] }));
        };
        using var api = f.Client(); await api.NegotiateAsync(default);
        Assert.Equal(code, (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.DiscoverAsync(default))).Code);
    }

    [Fact]
    public async Task ReadOnlyOperationsOnlyUseDiscoveredIdsAndTypedSubscriptionMessages()
    {
        var f = new XPlaneFixture(); using var api = f.Client(); await api.NegotiateAsync(default); var found = await api.DiscoverAsync(default);
        var before = f.Requests.Count;
        Assert.Equal("UndiscoveredDatarefId", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.ReadValueAsync(1, default))).Code);
        Assert.Equal(before, f.Requests.Count);
        await api.OpenAsync(default); var ids = found.Found.Select(d => d.Id).ToArray();
        await api.SubscribeAsync(1, ids, default); await api.UnsubscribeAsync(2, ids, default);
        Assert.Equal(new[] { "dataref_subscribe_values", "dataref_unsubscribe_values" }, f.Socket.Sent.Select(m => m.GetProperty("type").GetString()));
        Assert.All(f.Socket.Sent, m => Assert.Equal(ids, m.GetProperty("params").GetProperty("datarefs").EnumerateArray().Select(r => r.GetProperty("id").GetInt64())));
        await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.SubscribeAsync(0, ids, default));
        await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.SubscribeAsync(3, [ids[0], ids[0]], default));
        await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.SubscribeAsync(3, [1], default));
        Assert.Equal(2, f.Socket.Sent.Count);
    }

    [Theory]
    [InlineData("")] [InlineData("{")] [InlineData("{\"id\":1,\"id\":2}")]
    [InlineData("{\"s\":\"\\uD800\"}")] [InlineData("{\"s\":\"\\ufffd\"}")]
    [InlineData("[[[[[[[[[[[[[0]]]]]]]]]]]]]")]
    public void MalformedOrAmbiguousProtocolJsonIsRejected(string text) =>
        Assert.Throws<XPlaneProtocolException>(() => XPlaneProtocol.Parse(Encoding.UTF8.GetBytes(text), 65536));

    [Fact]
    public void ArraysAndMessagesHaveIndependentBounds()
    {
        Assert.Equal(2, XPlaneProtocol.Parse("[1,2]"u8.ToArray(), 100).GetArrayLength());
        Assert.Throws<XPlaneProtocolException>(() => XPlaneProtocol.Parse(XPlaneFixture.Bytes(new int[129]), 65536));
        Assert.Throws<XPlaneProtocolException>(() => XPlaneProtocol.Parse(new byte[65537], 65536));
    }

    [Fact]
    public async Task OversizedHttpResponseCannotAllocateAnUnboundedPayload()
    {
        var f = new XPlaneFixture { OverrideResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(
            new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) }) };
        using var api = f.Client();
        Assert.Equal("OversizedHttpResponse", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.NegotiateAsync(default))).Code);
    }

    [Fact]
    public async Task IncorrectNotFoundEnvelopesAndMissingValueEnvelopesAreContractFailures()
    {
        var f = new XPlaneFixture(); using var api = f.Client(); await api.NegotiateAsync(default); await api.DiscoverAsync(default);
        f.OverrideResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(XPlaneFixture.Response(new { error_code = "wrong_contract" }, HttpStatusCode.NotFound));
        Assert.Equal("UnexpectedNotFoundContract", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.ReadValueAsync(f.Refs["latitude"].Id, default))).Code);
        f.OverrideResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(XPlaneFixture.Response(new { unexpected = 1 }));
        Assert.Equal("MissingValueEnvelope", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.ReadValueAsync(f.Refs["latitude"].Id, default))).Code);
        f.OverrideResponse = (_, _) => Task.FromResult<HttpResponseMessage?>(XPlaneFixture.Response(new { error_code = "invalid_dataref_id" }, HttpStatusCode.NotFound));
        Assert.Null(await api.ReadValueAsync(f.Refs["latitude"].Id, default));
    }

    [Fact]
    public async Task DiscoveryCannotContinuePastItsPageBudget()
    {
        var f = new XPlaneFixture(); using var api = f.Client(new() { DiscoveryPageSize = 1, MaximumDiscoveryPages = 1 });
        await api.NegotiateAsync(default);
        Assert.Equal("DiscoveryPageLimit", (await Assert.ThrowsAsync<XPlaneProtocolException>(() => api.DiscoverAsync(default))).Code);
        Assert.Equal(2, f.Requests.Count);
    }

    [Theory]
    [InlineData("localhost")] [InlineData("127.1")] [InlineData("192.168.0.2")]
    [InlineData("https://127.0.0.1")] [InlineData("user:password@127.0.0.1")]
    [InlineData("::ffff:127.0.0.1")] [InlineData("0.0.0.0")] [InlineData("::")]
    public void ArbitraryUrlsRemoteHostsCredentialsAndNoncanonicalAddressesAreRejected(string address) =>
        Assert.Throws<ArgumentException>(() => new XPlaneTelemetryProvider(new() { Address = address }));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(65536)]
    public void InvalidPortsAreRejectedBeforeAnyTraffic(int port) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPlaneTelemetryProvider(new() { Port = port }));

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("::1")]
    public void ExplicitLoopbackAddressesAreAcceptedWithoutResolvingDns(string address)
    {
        var options = new XPlaneOptions { Address = address, Port = 45678 }; options.Validate();
        Assert.Equal("/api/v1", options.Endpoint("api/v1", true).AbsolutePath);
        Assert.Equal(45678, options.Endpoint("api/v1", true).Port);
    }

    [Fact]
    public void HttpAndWebsocketHandshakeShareTheNoProxyNoRedirectPolicy()
    {
        using var handler = XPlaneWebApiClient.CreateHandler();
        Assert.False(handler.UseProxy); Assert.False(handler.AllowAutoRedirect); Assert.False(handler.UseCookies);
        Assert.Equal(8, handler.MaxConnectionsPerServer);
    }
}
