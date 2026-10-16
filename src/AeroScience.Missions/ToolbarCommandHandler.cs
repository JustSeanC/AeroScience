using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ToolbarCommand(int Protocol, Guid RequestId, string Op, string? Name = null, Guid? MissionId = null,
    ScienceMode Mode = ScienceMode.Universal);
public sealed record ToolbarReply(int Protocol, Guid RequestId, bool Ok, string? Error, EngineSnapshot State);

// The simulator-native channel reaches the same coordinator as the authenticated browser API.
public sealed class ToolbarCommandHandler(MissionCoordinator coordinator)
{
    private readonly Dictionary<Guid, (string Request, string Reply)> completed = [];
    private readonly Queue<Guid> order = new();
    private readonly object gate = new();

    public string Handle(string json)
    {
        lock (gate)
        {
            ToolbarCommand? command = null;
            try
            {
                if (json.Length > 1024) throw new ArgumentException("Toolbar command is too large.");
                command = JsonSerializer.Deserialize<ToolbarCommand>(json, WireJson.Options);
                if (command is null || command.Protocol != 1 || command.RequestId == Guid.Empty)
                    throw new ArgumentException("Invalid toolbar protocol or request identifier.");
                if (completed.TryGetValue(command.RequestId, out var previous))
                    return previous.Request == json ? previous.Reply : Reply(command.RequestId, false, "Request identifier was already used.");
                switch (command.Op)
                {
                    case "state": return Reply(command.RequestId, true, null);
                    case "start": coordinator.Start(command.Name, command.Mode); break;
                    case "end" when command.MissionId is { } id: coordinator.End(id); break;
                    default: throw new ArgumentException("Unknown toolbar command or missing mission ID.");
                }
                var reply = Reply(command.RequestId, true, null);
                Remember(command.RequestId, json, reply);
                return reply;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException)
            {
                var reply = Reply(command?.RequestId ?? Guid.Empty, false, e is JsonException ? "Malformed toolbar JSON." : e.Message);
                if (command is { RequestId: var id, Op: not "state" } && id != Guid.Empty) Remember(id, json, reply);
                return reply;
            }
        }
    }
    private string Reply(Guid id, bool ok, string? error) => JsonSerializer.Serialize(new ToolbarReply(1, id, ok, error, coordinator.Snapshot()), WireJson.Options);
    private void Remember(Guid id, string json, string reply)
    {
        if (completed.ContainsKey(id)) return;
        completed.Add(id, (json, reply)); order.Enqueue(id);
        while (order.Count > 128) completed.Remove(order.Dequeue());
    }
}
