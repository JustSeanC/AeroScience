using System.Reflection;
using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

// Selected at compilation, never by editable preferences or a command-line switch.
public sealed record ApplicationProfile(bool Validation)
{
    public static ApplicationProfile Current { get; } = new(typeof(ApplicationProfile).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "AeroScienceProfile" && a.Value == "validation"));
    public string Name => Validation ? "AeroScience 0.5.0 Validation" : "AeroScience";
    public string Folder => Validation ? "AeroScience-Validation-v0.5.0" : "AeroScience";
    public string ConfigurationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
    public string RegistryKey => Validation ? @"Software\AeroScience\Validation\v0.5.0" : @"Software\AeroScience";
    public int Port => Validation ? 5085 : 5080;
    // Deliberately shared: preserve the older application's exclusion mechanism.
    public const string DesktopMutex = @"Local\AeroScience.Desktop";
    public string ToolbarPackage => Validation ? "aeroscience-validation-toolbar" : "aeroscience-toolbar";
    public string ToolbarTitle => Validation ? "AeroScience 0.5.0 Validation" : "AeroScience";
    public string ToolbarFolder => Validation ? "AeroScienceValidation" : "AeroScience";
    public string CommandEvent => Validation ? "AeroScience.Validation.0_5_0.Command.v1" : "AeroScience.Command.v1";
    public string ReplyEvent => Validation ? "AeroScience.Validation.0_5_0.Reply.v1" : "AeroScience.Reply.v1";
    public PreferenceStore Preferences() => new(ConfigurationDirectory, Validation ? ConfigurationDirectory : null);
    public static string SourceCommit => typeof(ApplicationProfile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').LastOrDefault() ?? "unknown";
    public string? AuthorizeToolbar(string command)
    {
        if(!Validation)return null;
        using var json=JsonDocument.Parse(command);var root=json.RootElement;
        return root.TryGetProperty("validationSource",out var source) && source.ValueKind==JsonValueKind.String && source.GetString()==SourceCommit
            ? null : "Validation toolbar source mismatch. Install this candidate's matching validation toolbar in Settings.";
    }
    public void ValidateEngine(EngineOptions options)
    {
        if (!Validation) return;
        if (options.Port != Port || options.ConfigurationDirectory is null ||
            !Path.GetFullPath(options.ConfigurationDirectory).Equals(Path.GetFullPath(ConfigurationDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Validation requires its isolated configuration root and port 5085. Production was not opened.");
        var preferences = Preferences();
        preferences.ValidateDirectory(options.DataDirectory);
        preferences.ValidateDirectory(options.ScienceLibraryDirectory ?? throw new IOException("Validation requires an explicit isolated science library."));
    }
}
