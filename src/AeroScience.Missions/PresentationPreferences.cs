using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed record PresentationPreferences(int Version = 1, string Theme = "auto", string Locale = "en-US", long Revision = 0,
    string Experience = "guided", string Density = "balanced")
{
    public static PresentationPreferences Effective(PresentationPreferences? value) => value is { Version: 1, Revision: >= 0 }
        ? value with { Theme = value.Theme is "auto" or "light" or "dark" ? value.Theme : "auto", Locale = PresentationText.Locale(value.Locale), Experience = "guided", Density = "balanced" }
        : new();
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PresentationChange(long? ExpectedRevision, string? Theme, string? Locale);

public sealed partial class PreferenceStore
{
    public PresentationPreferences Presentation() => PresentationPreferences.Effective(Load().Presentation);
    public PresentationPreferences SetPresentation(PresentationChange change)
    {
        if (change.ExpectedRevision is null or < 0 || change.Theme is not ("auto" or "light" or "dark")
            || !PresentationText.Locales.Any(l => l.Id == change.Locale)) throw new ArgumentException("Choose a supported theme/language and supply the current settings revision.");
        lock (SettingsGate)
        {
            // Same path, same atomic writer and profile validation as all existing settings.
            var saved = Load();
            if (saved.Presentation is { Version: not 1 }) throw new InvalidOperationException("Newer presentation settings require a newer companion. Existing settings were preserved.");
            var current = PresentationPreferences.Effective(saved.Presentation);
            if (current.Revision != change.ExpectedRevision || current.Revision >= 9007199254740991L) throw new InvalidOperationException("Appearance settings changed. Reload them before saving your choices.");
            var next = current with { Theme = change.Theme, Locale = change.Locale!, Revision = current.Revision + 1 };
            var document = File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject() : JsonSerializer.SerializeToNode(saved)!.AsObject();
            // Patch only this section; preserve unknown/older settings and every storage path.
            var section = document["Presentation"] as JsonObject ?? new JsonObject();
            foreach (var item in JsonSerializer.SerializeToNode(next)!.AsObject())
                if(item.Key is not ("Experience" or "Density") || !section.ContainsKey(item.Key))section[item.Key] = item.Value?.DeepClone();
            if(document["Presentation"] is not JsonObject)document["Presentation"] = section;
            WriteAtomic(SettingsPath, document); return next;
        }
    }
}
