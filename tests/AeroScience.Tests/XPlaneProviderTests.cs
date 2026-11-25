using System.Net;
using AeroScience.Shared;
using AeroScience.XPlane;
using Xunit;

namespace AeroScience.Tests;

public sealed class XPlaneProviderTests
{
    private static async Task<TelemetryUpdate> Until(IAsyncEnumerator<TelemetryUpdate> stream, Func<TelemetryUpdate, bool> predicate)
    {
        while (await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            if (predicate(stream.Current)) return stream.Current;
        throw new Xunit.Sdk.XunitException("Synthetic provider ended before expected state.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealProviderLoopRequiresSeedAcknowledgementAndFreshGetEvidence(bool seedBeforeAck)
    {
        var f = new XPlaneFixture { SeedBeforeAck = seedBeforeAck }; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); Assert.Equal(ConnectionState.Initializing, stream.Current.Connection.State);
        var update = await Until(stream, u => u.Frame is not null);
        Assert.True(update.Frame!.IsValid); Assert.NotNull(update.SampleContext);
        Assert.Equal("v1", update.Connection.Origin!.ApiVersion);
        Assert.All(f.Requests, r => Assert.Equal("GET", r.Method));
        Assert.InRange(f.MaximumActiveReads, 1, 4);
        Assert.Contains(f.Requests, r => r.Uri.AbsolutePath.EndsWith("/value"));
    }

    [Fact]
    public async Task OptionalSlowOrUnavailableReadDoesNotBlockSoundCoreMeasurements()
    {
        var f = new XPlaneFixture(); var optional = f.Refs["temperature"].Id;
        f.Missing.Add("agl");
        f.OverrideResponse = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith($"/{optional}/value")) await Task.Delay(Timeout.Infinite, token);
            return null;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        var update = await Until(stream, u => u.Frame is not null);
        Assert.Null(update.Frame!.AltitudeAglFeet); Assert.Null(update.Atmosphere!.TemperatureCelsius);
        Assert.Contains("temperature", update.SampleContext!.Capabilities.UnavailableChannels);
        Assert.Contains("agl", update.SampleContext.Capabilities.UnavailableChannels);
        Assert.InRange(update.SampleContext.CoreSnapshotSpanMilliseconds!.Value, 0, 500);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeniedOrMissingInitialSubscriptionNeverPublishesFlight(bool denied)
    {
        var f = new XPlaneFixture { RejectSubscription = denied, SuppressSubscriptionReply = !denied };
        var options = XPlaneFixture.FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(100) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider(options).ReadAsync(stop.Token).GetAsyncEnumerator();
        var update = await Until(stream, u => u.Connection.State is ConnectionState.Unavailable or ConnectionState.Disconnected);
        Assert.Null(update.Frame);
        Assert.Equal(denied ? "SubscriptionRejected" : "LocalApiDeadlineExceeded", update.Connection.Message);
    }

    [Theory]
    [InlineData(403, ConnectionState.Unavailable, "ApiAccessDenied")]
    [InlineData(503, ConnectionState.Disconnected, "HttpFailure")]
    public async Task ProviderDistinguishesSecurityDenialFromUnreachableApi(int status, ConnectionState state, string code)
    {
        var f = new XPlaneFixture { CapabilityStatus = status }; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        var update = await Until(stream, u => u.Connection.State == state);
        Assert.Equal(code, update.Connection.Message); Assert.Null(update.Frame);
        Assert.Single(f.Requests); Assert.Empty(f.Socket.Sent);
    }

    [Fact]
    public async Task MidConnectionSecurityDenialCancelsQueuedReadsWithoutAnotherApiFallback()
    {
        var f = new XPlaneFixture { OverrideResponse = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/value") ? XPlaneFixture.Response(new { error_code = "access_denied" }, HttpStatusCode.Forbidden) : null) };
        var options = XPlaneFixture.FastOptions with { ConfirmationConcurrency = 1 };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider(options).ReadAsync(stop.Token).GetAsyncEnumerator();
        var update = await Until(stream, u => u.Connection.State == ConnectionState.Unavailable);
        Assert.Null(update.Frame); Assert.Equal("ApiAccessDenied", update.Connection.Message);
        Assert.Single(f.Requests, r => r.Uri.AbsolutePath.EndsWith("/value"));
        Assert.Single(f.Requests, r => r.Uri.AbsolutePath == "/api/capabilities");
    }

    [Fact]
    public async Task SocketDisconnectRediscoversIdsAndRequalifiesANewEpoch()
    {
        var first = new XPlaneFixture(9007199254740993); var second = new XPlaneFixture(100);
        var attempts = 0;
        var provider = new XPlaneTelemetryProvider(XPlaneFixture.FastOptions, TimeProvider.System,
            () => Interlocked.Increment(ref attempts) == 1 ? first.Client(XPlaneFixture.FastOptions) : second.Client(XPlaneFixture.FastOptions));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = provider.ReadAsync(stop.Token).GetAsyncEnumerator();
        var one = await Until(stream, u => u.Frame is not null);
        await first.Socket.Inject(null);
        var lost = await Until(stream, u => u.Connection.State == ConnectionState.Disconnected);
        Assert.Null(lost.Frame);
        var two = await Until(stream, u => u.Frame is not null);
        Assert.NotEqual(one.Connection.Origin!.ConnectionEpoch, two.Connection.Origin!.ConnectionEpoch);
        Assert.Contains(second.Requests, r => r.Uri.AbsolutePath.EndsWith("/datarefs"));
        Assert.All(second.Socket.Sent.First().GetProperty("params").GetProperty("datarefs").EnumerateArray(),
            id => Assert.InRange(id.GetProperty("id").GetInt64(), 100, 200));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task LostRequiredNumericIdentityTriggersRediscoveryRatherThanRetryingOldIdsForever()
    {
        var first = new XPlaneFixture(); var second = new XPlaneFixture(900);
        var attempts = 0;
        var provider = new XPlaneTelemetryProvider(XPlaneFixture.FastOptions, TimeProvider.System,
            () => Interlocked.Increment(ref attempts) == 1 ? first.Client(XPlaneFixture.FastOptions) : second.Client(XPlaneFixture.FastOptions));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = provider.ReadAsync(stop.Token).GetAsyncEnumerator();
        var one = await Until(stream, u => u.Frame is not null);
        first.OverrideResponse = (request, _) => Task.FromResult<HttpResponseMessage?>(
            request.RequestUri!.AbsolutePath.EndsWith("/value") ? XPlaneFixture.Response(new { error_code = "invalid_dataref_id" }, HttpStatusCode.NotFound) : null);
        var lost = await Until(stream, u => u.Connection.Message == "RequiredDatarefIdentityLost");
        Assert.Equal(ConnectionState.Initializing, lost.Connection.State); Assert.Null(lost.Frame);
        var two = await Until(stream, u => u.Frame is not null);
        Assert.NotEqual(one.Connection.Origin!.ConnectionEpoch, two.Connection.Origin!.ConnectionEpoch);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task MalformedLiveMessageTerminatesEpochBeforeAnyMoreFrames()
    {
        var f = new XPlaneFixture(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        await Until(stream, u => u.Frame is not null); await f.Socket.Inject("bad json"u8.ToArray());
        var update = await Until(stream, u => u.Connection.State == ConnectionState.Unavailable);
        Assert.Null(update.Frame); Assert.Equal("MalformedJson", update.Connection.Message);
    }

    [Fact]
    public async Task SlowConsumerReceivesCurrentConfirmationRatherThanABacklogOfInventedSamples()
    {
        var f = new XPlaneFixture { DelayMilliseconds = 1 }; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        var first = await Until(stream, u => u.Frame is not null);
        await Task.Delay(200, stop.Token);
        var second = await Until(stream, u => u.Frame is not null);
        Assert.True(second.SampleContext!.Clock!.FlightSeconds >= first.SampleContext!.Clock!.FlightSeconds + 3);
        Assert.InRange((DateTimeOffset.UtcNow - second.Frame!.TimestampUtc).TotalSeconds, 0, 2);
        Assert.InRange(f.MaximumActiveReads, 1, 4);
    }

    [Fact]
    public async Task OrderlyDisposeUnsubscribesAndStopsBackgroundTraffic()
    {
        var f = new XPlaneFixture(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        await Until(stream, u => u.Frame is not null);
        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(f.Socket.IsOpen);
        Assert.Contains(f.Socket.Sent, message => message.GetProperty("type").GetString() == "dataref_unsubscribe_values");
        var count = f.Requests.Count; await Task.Delay(75); Assert.Equal(count, f.Requests.Count);
    }

    [Fact]
    public async Task CancellationInterruptsStalledDiscoveryAndStopsWithoutReconnect()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var f = new XPlaneFixture { OverrideResponse = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; } };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var next = stream.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Single(f.Requests);
    }

    [Fact]
    public async Task CancellationInterruptsReconnectBackoff()
    {
        var f = new XPlaneFixture { CapabilityStatus = 503 }; using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider().ReadAsync(stop.Token).GetAsyncEnumerator();
        await Until(stream, u => u.Connection.State == ConnectionState.Disconnected);
        var next = stream.MoveNextAsync().AsTask(); stop.Cancel();
        Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Single(f.Requests);
        Assert.Equal(new[] { 1d, 2d, 5d, 10d, 30d }, XPlaneOptions.RetryDelays.Select(t => t.TotalSeconds));
    }

    [Fact]
    public async Task StalledConfirmationEmitsStaleStatusBeforeTheLongerHttpDeadline()
    {
        var f = new XPlaneFixture();
        var options = XPlaneFixture.FastOptions with { RequestTimeout = TimeSpan.FromSeconds(3),
            MaximumConfirmationAge = TimeSpan.FromMilliseconds(300), MaximumSnapshotSpan = TimeSpan.FromMilliseconds(100) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var stream = f.Provider(options).ReadAsync(stop.Token).GetAsyncEnumerator();
        await Until(stream, u => u.Frame is not null);
        f.OverrideResponse = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
        var start = System.Diagnostics.Stopwatch.StartNew();
        var stale = await Until(stream, u => u.Frame is null && u.Connection.Message == "ConfirmationStale");
        Assert.Equal(ConnectionState.Initializing, stale.Connection.State);
        Assert.InRange(start.Elapsed.TotalSeconds, .2, 2);
        Assert.DoesNotContain("latitude", stale.SampleContext!.Capabilities.AvailableChannels);
    }
}
