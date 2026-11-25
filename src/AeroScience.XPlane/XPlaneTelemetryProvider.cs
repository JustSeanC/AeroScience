using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AeroScience.Shared;
using AeroScience.Telemetry;

namespace AeroScience.XPlane;

/// <summary>
/// Experimental read-only adapter. P02 does not register this provider in the
/// production host, persist research, or establish genuine simulator acceptance.
/// </summary>
public sealed class XPlaneTelemetryProvider : ITelemetryProvider
{
    private readonly XPlaneOptions options;
    private readonly TimeProvider time;
    private readonly Func<XPlaneWebApiClient> createClient;
    private static long nextRequestId;

    public XPlaneTelemetryProvider(XPlaneOptions? options = null, TimeProvider? timeProvider = null)
    {
        this.options = options ?? new(); this.options.Validate(); time = timeProvider ?? TimeProvider.System;
        createClient = () => new(this.options, time);
    }
    internal XPlaneTelemetryProvider(XPlaneOptions options, TimeProvider time, Func<XPlaneWebApiClient> createClient)
    { options.Validate(); this.options = options; this.time = time; this.createClient = createClient; }

    public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var epoch = Guid.NewGuid();
            var origin = new SimulatorOrigin(1, "xplane12", null, "aeroscience.xplane.web", "p02-v1", null, epoch, XPlaneDatarefs.MappingRevision);
            var lastOrigin = origin;
            yield return Status(origin, ConnectionState.Initializing, "Connecting to the local X-Plane API.");
            Exception? failure = null;
            await using (var session = Session(origin, cancellationToken).GetAsyncEnumerator(cancellationToken))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool moved = false;
                    try { moved = await session.MoveNextAsync(); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception error) when (error is IOException or HttpRequestException or WebSocketException
                        or OperationCanceledException or ChannelClosedException) { failure = error; }
                    if (failure is not null || !moved) break;
                    var update = session.Current;
                    lastOrigin = update.Connection.Origin ?? lastOrigin;
                    if (update.Connection.State == ConnectionState.Connected) failures = 0;
                    yield return update;
                }
            }
            if (cancellationToken.IsCancellationRequested) yield break;
            var protocol = failure as XPlaneProtocolException;
            yield return Status(lastOrigin, protocol?.State ?? ConnectionState.Disconnected,
                protocol?.Code ?? (failure is OperationCanceledException ? "LocalApiDeadlineExceeded" : "LocalApiConnectionLost"));
            var delay = XPlaneOptions.RetryDelays[Math.Min(failures++, XPlaneOptions.RetryDelays.Length - 1)];
            try { await Task.Delay(delay, time, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { yield break; }
        }
    }

    private async IAsyncEnumerable<TelemetryUpdate> Session(SimulatorOrigin initial,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var client = createClient();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? receive = null, confirm = null, freshness = null;
        Exception? terminalError = null;
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
        long[] ids = []; long request = 0;
        try
        {
            var protocol = await client.NegotiateAsync(session.Token);
            var origin = initial with { ReportedVersion = protocol.ReportedVersion, ApiVersion = protocol.ApiVersion };
            var discovery = await client.DiscoverAsync(session.Token);
            ids = discovery.Found.Select(d => d.Id).ToArray(); request = RequestId();
            var assembler = new XPlaneSnapshotAssembler(options, time, discovery, origin, request);
            await client.OpenAsync(session.Token);
            receive = Receive();
            await client.SubscribeAsync(request, ids, session.Token);
            using (var deadline = new CancellationTokenSource(options.RequestTimeout, time))
            using (var initialWait = CancellationTokenSource.CreateLinkedTokenSource(session.Token, deadline.Token))
            {
                var first = await Task.WhenAny(assembler.Ready, receive).WaitAsync(initialWait.Token);
                await first;
                await assembler.Ready.WaitAsync(initialWait.Token);
            }
            if (Volatile.Read(ref terminalError) is { } initialError) ExceptionDispatchInfo.Capture(initialError).Throw();
            confirm = Confirm(); freshness = CheckFreshness();
            string? lastStatus = null;
            while (await signals.Reader.WaitToReadAsync(session.Token))
            {
                while (signals.Reader.TryRead(out _)) { }
                if (Volatile.Read(ref terminalError) is { } terminal) ExceptionDispatchInfo.Capture(terminal).Throw();
                if (receive.IsFaulted) await receive;
                if (confirm.IsFaulted) await confirm;
                var update = assembler.Snapshot();
                if (update is null) continue;
                var status = update.Connection.State + ":" + update.Connection.Message;
                if (update.Frame is null && status == lastStatus) continue;
                lastStatus = status;
                yield return update;
            }
            async Task Receive()
            {
                try
                {
                    while (!session.IsCancellationRequested)
                    {
                        var bytes = await client.ReceiveAsync(session.Token);
                        if (bytes is null) throw new XPlaneProtocolException("WebSocketClosed", ConnectionState.Disconnected);
                        assembler.Ingest(origin.ConnectionEpoch, bytes);
                        signals.Writer.TryWrite(true); // Apply every delta before coalescing wakeups.
                    }
                }
                catch (Exception error) { Interlocked.CompareExchange(ref terminalError, error, null); signals.Writer.TryComplete(error); throw; }
            }
            async Task Confirm()
            {
                using var timer = new PeriodicTimer(options.ConfirmationInterval, time);
                try
                {
                    do
                    {
                        var startUtc = time.GetUtcNow().ToUniversalTime(); var start = time.GetTimestamp();
                        using var batchStop = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                        using var optionalDeadline = new CancellationTokenSource(options.MaximumSnapshotSpan, time);
                        using var optionalToken = CancellationTokenSource.CreateLinkedTokenSource(batchStop.Token, optionalDeadline.Token);
                        using var slots = new SemaphoreSlim(options.ConfirmationConcurrency);
                        var reads = discovery.Found.OrderByDescending(d => d.Definition.Required).Select(async definition =>
                        {
                            await slots.WaitAsync(batchStop.Token);
                            try
                            {
                                System.Text.Json.JsonElement? value;
                                try
                                {
                                    var readToken = definition.Definition.Required ? batchStop.Token : optionalToken.Token;
                                    value = await client.ReadValueAsync(definition.Id, readToken);
                                    readToken.ThrowIfCancellationRequested();
                                    if (value is null && definition.Definition.Required)
                                        throw new XPlaneProtocolException("RequiredDatarefIdentityLost", ConnectionState.Initializing);
                                }
                                catch (OperationCanceledException) when (!batchStop.IsCancellationRequested && !definition.Definition.Required) { value = null; }
                                catch (HttpRequestException) when (!definition.Definition.Required) { value = null; }
                                return new XPlaneValueConfirmation(definition.Id, value, time.GetUtcNow().ToUniversalTime(), time.GetTimestamp());
                            }
                            catch (Exception error)
                            {
                                // A denied or broken contract cancels pending reads;
                                // do not keep polling other fields after that denial.
                                Interlocked.CompareExchange(ref terminalError, error, null);
                                batchStop.Cancel(); signals.Writer.TryComplete(error); throw;
                            }
                            finally { slots.Release(); }
                        }).ToArray();
                        var values = await Task.WhenAll(reads);
                        assembler.Confirm(origin.ConnectionEpoch, new(startUtc, start, time.GetUtcNow().ToUniversalTime(), time.GetTimestamp(), values));
                        signals.Writer.TryWrite(true);
                    } while (await timer.WaitForNextTickAsync(session.Token));
                }
                catch (Exception error) { Interlocked.CompareExchange(ref terminalError, error, null); signals.Writer.TryComplete(error); throw; }
            }
            async Task CheckFreshness()
            {
                // Wake status evaluation while a read is stalled. This timer can
                // invalidate a stale pose, but cannot qualify or duplicate frames.
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), time);
                while (await timer.WaitForNextTickAsync(session.Token)) signals.Writer.TryWrite(true);
            }
        }
        finally
        {
            // Best effort unsubscribe on orderly enumerator disposal. Cancellation
            // may already abort a socket; all cleanup has a separate bounded budget.
            using var cleanup = new CancellationTokenSource(options.ShutdownTimeout, time);
            try { if (client.SocketOpen && ids.Length > 0) await client.UnsubscribeAsync(RequestId(), ids, cleanup.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or WebSocketException) { }
            session.Cancel(); client.Abort();
            var tasks = new[] { receive, confirm, freshness }.Where(t => t is not null).Cast<Task>().ToArray();
            try { await Task.WhenAll(tasks).WaitAsync(options.ShutdownTimeout, time, CancellationToken.None); }
            catch (Exception error) when (error is IOException or OperationCanceledException or WebSocketException or TimeoutException) { }
        }
    }
    private TelemetryUpdate Status(SimulatorOrigin origin, ConnectionState state, string message) =>
        new(new(state, TelemetrySource.Simulator, message, time.GetUtcNow().ToUniversalTime()) { Origin = origin }, null);
    private static long RequestId()
    {
        var id = Interlocked.Increment(ref nextRequestId);
        if (id > int.MaxValue) throw new XPlaneProtocolException("RequestIdBudgetExhausted");
        return id;
    }
}
