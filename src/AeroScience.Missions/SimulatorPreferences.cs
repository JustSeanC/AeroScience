using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AeroScience.Shared;

namespace AeroScience.Missions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SimulatorPreferences(int Version, string Id, int XPlanePort, long Revision = 0)
{
    public const string Msfs2024 = "msfs2024", XPlane12 = "xplane12";
    public static SimulatorPreferences Default => new(1, Msfs2024, 8086);
    public static bool Known(string? id) => id is Msfs2024 or XPlane12;
    public static SimulatorPreferences Effective(SimulatorPreferences? saved)
    {
        if (saved is null) return Default;
        if (saved.Version != 1 || !Known(saved.Id) || saved.XPlanePort is < 1 or > 65535 || saved.Revision is < 0 or > 9007199254740991L)
            throw new InvalidOperationException(PresentationText.Text("simulator.invalidSaved"));
        return saved;
    }
    public bool SameConnection(SimulatorPreferences other) => Id == other.Id && XPlanePort == other.XPlanePort;
    public static string Name(string id) => id switch
    { Msfs2024 => "Microsoft Flight Simulator 2024", XPlane12 => "X-Plane 12", _ => throw new ArgumentException("Unsupported simulator identity.") };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SimulatorChange(long? ExpectedRevision, string? Id, int? XPlanePort);

public sealed partial class PreferenceStore
{
    public SimulatorPreferences Simulator() => SimulatorPreferences.Effective(Load().Simulator);
    public SimulatorPreferences SetSimulator(SimulatorChange change)
    {
        if (change.ExpectedRevision is null or < 0 || !SimulatorPreferences.Known(change.Id) || change.XPlanePort is null or < 1 or > 65535)
            throw new ArgumentException(PresentationText.Text("simulator.invalidChoice"));
        lock (SettingsGate)
        {
            if (File.Exists(JournalPath)) throw new InvalidOperationException(PresentationText.Text("simulator.maintenance"));
            var saved = Load(); var current = SimulatorPreferences.Effective(saved.Simulator);
            if (current.Revision != change.ExpectedRevision || current.Revision >= 9007199254740991L)
                throw new InvalidOperationException(PresentationText.Text("simulator.changed"));
            var next = new SimulatorPreferences(1, change.Id!, change.XPlanePort.Value, current.Revision + 1);
            if (next.SameConnection(current)) return current;
            var document = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject()
                : JsonSerializer.SerializeToNode(saved)!.AsObject();
            // One existing atomic writer/gate; other preferences and unknown
            // sections are not rewritten by a source-selection operation.
            document["Simulator"] = JsonSerializer.SerializeToNode(next);
            WriteAtomic(SettingsPath, document); return next;
        }
    }
}
