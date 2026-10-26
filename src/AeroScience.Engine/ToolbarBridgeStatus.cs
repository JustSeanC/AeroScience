namespace AeroScience.Engine;

public sealed record NativeToolbarStatus(string State, string Message, string EngineVersion = "0.7.0", int Protocol = 1, DateTimeOffset? LastRequestUtc = null, string? LastOperation = null, long Requests = 0, long Replies = 0);
public sealed class ToolbarBridgeStatus
{
    private NativeToolbarStatus state = new("Initializing", "Waiting for native bridge initialization. Aircraft telemetry and CommBus are separate channels.");
    public NativeToolbarStatus Snapshot() => Volatile.Read(ref state);
    public void Connection(string status, string message) => Volatile.Write(ref state, Snapshot() with { State = status, Message = message });
    public void Requested(string? operation) => Volatile.Write(ref state, Snapshot() with { LastRequestUtc = DateTimeOffset.UtcNow, LastOperation = operation, Requests = Snapshot().Requests + 1 });
    public void Replied() => Volatile.Write(ref state, Snapshot() with { Replies = Snapshot().Replies + 1 });
}
