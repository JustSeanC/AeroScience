using System.Text.Json;
using System.Text;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record ToolbarCommand(int Protocol, Guid RequestId, string Op, string? Name = null, Guid? MissionId = null,
    ScienceMode Mode = ScienceMode.Universal, string? InstrumentId = null, bool? Enabled = null, int? IntervalMs = null,
    string? Category = null, string? Notes = null, Guid? PlanId = null, string? Revision = null, int Offset = 0, long? After = null, int? WaypointIndex = null);
public sealed record ToolbarReply(int Protocol, Guid RequestId, bool Ok, string? Error, EngineSnapshot State);

// The simulator-native channel reaches the same coordinator as the authenticated browser API.
public sealed class ToolbarCommandHandler(MissionCoordinator coordinator, RouteMapService? maps = null)
{
    private readonly RouteMapService navigation = maps ?? new(coordinator);
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
                if (command.Op is "map-state" or "map-chunk" or "map-plans")
                {
                    object value = command.Op switch {
                        "map-state" => navigation.State(command.PlanId, command.After, waypointIndex: command.WaypointIndex),
                        "map-chunk" => navigation.Chunk(command.PlanId, command.Revision ?? "", command.Offset),
                        _ => navigation.Plans(command.Offset) };
                    var encoded = JsonSerializer.Serialize(new { protocol = 1, command.RequestId, ok = true, map = value }, WireJson.Options);
                    if (Encoding.UTF8.GetByteCount(encoded) > 8000) throw new InvalidOperationException("Map response exceeds its safe packet bound. Resynchronize the map.");
                    return encoded;
                }
                if (completed.TryGetValue(command.RequestId, out var previous))
                    return previous.Request == json ? previous.Reply : Reply(command.RequestId, false, "Request identifier was already used.");
                switch (command.Op)
                {
                    case "state": return Reply(command.RequestId, true, null);
                    case "start": coordinator.Start(new StartMissionRequest(command.Name ?? "", command.Mode, PlanId: command.PlanId)); break;
                    case "end" when command.MissionId is { } id: coordinator.End(id); break;
                    case "science-pause" when command.MissionId is { } pauseMission: coordinator.SetSciencePaused(pauseMission, true); break;
                    case "science-resume" when command.MissionId is { } resumeMission: coordinator.SetSciencePaused(resumeMission, false); break;
                    case "marker" when command.MissionId is { } markerMission:
                        coordinator.AddMarker(markerMission, new(command.Category ?? "observation", command.Notes)); break;
                    case "instrument" when command.InstrumentId is { } instrument:
                        coordinator.ConfigureInstrument(instrument,command.Enabled,command.IntervalMs);break;
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
    private string Reply(Guid id, bool ok, string? error)
    {
        static string? Short(string? value, int length = 100) => value?.Length > length ? value[..length] + "…" : value;
        var state = coordinator.Snapshot();
        // Display-only text bounds keep native CommBus below 8 KiB; database/export values stay intact.
        state = state with
        {
            Connection = state.Connection with { Message = Short(state.Connection.Message, 160)! },
            Telemetry = state.Telemetry is { } t ? t with { Aircraft = Short(t.Aircraft, 80)! } : null,
            ActiveMission = state.ActiveMission is { } m ? m with { Name = Short(m.Name, 80)!, Aircraft = Short(m.Aircraft, 80) } : null,
            Research = state.Research is { } r ? r with
            {
                ScienceStorageError = Short(r.ScienceStorageError, 120),
                Instruments = r.Instruments.Select(i => i with { Message = Short(i.Message, 80) }).ToArray()
            } : null
        };
        return JsonSerializer.Serialize(new ToolbarReply(1, id, ok, Short(error, 180), state), WireJson.Options);
    }
    private void Remember(Guid id, string json, string reply)
    {
        if (completed.ContainsKey(id)) return;
        completed.Add(id, (json, reply)); order.Enqueue(id);
        while (order.Count > 128) completed.Remove(order.Dequeue());
    }
}
