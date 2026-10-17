using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.Missions;

public sealed class ScienceUnitSettings(string directory)
{
    private readonly object gate = new();
    public string PathName { get; } = Path.Combine(Path.GetFullPath(directory), "display-preferences.json");
    public string? Warning { get; private set; }
    public ScienceUnitPreferences Get()
    {
        lock (gate)
        {
            try
            {
                Warning = null;
                return File.Exists(PathName) ? ScienceUnits.Validate(JsonSerializer.Deserialize<ScienceUnitPreferences>(File.ReadAllText(PathName), WireJson.Options)
                    ?? throw new JsonException("Display units are missing.")) : ScienceUnits.Aviation;
            }
            catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            { Warning = "Saved unit preferences could not be read; Aviation display defaults are in use. The original file was preserved. " + error.Message; return ScienceUnits.Aviation; }
        }
    }
    public ScienceUnitPreferences Set(ScienceUnitPreferences preferences)
    {
        lock (gate) { ScienceUnits.Validate(preferences); PreferenceStore.WriteAtomic(PathName, preferences); Warning = null; return preferences; }
    }
}
