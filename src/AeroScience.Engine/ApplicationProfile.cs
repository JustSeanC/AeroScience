using System.Reflection;
using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

// Selected at compilation, never by editable preferences or a command-line switch.
public sealed record ApplicationProfile
{
    public const string BetaVersion = "0.7.1-beta.2";
    public bool Validation { get; }
    public string? PrivateBetaVersion { get; }
    public bool PrivateBeta => PrivateBetaVersion is not null;
    public ApplicationProfile(bool validation, string? privateBetaVersion = null)
    {
        if (privateBetaVersion is not null && (!validation || privateBetaVersion != BetaVersion))
            throw new ArgumentException("Unsupported private beta identity.");
        Validation=validation; PrivateBetaVersion=privateBetaVersion;
    }
    public static ApplicationProfile Current { get; } = new(typeof(ApplicationProfile).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "AeroScienceProfile" && a.Value == "validation"),
        typeof(ApplicationProfile).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == "AeroSciencePrivateBetaVersion")?.Value);
    public string Version => PrivateBetaVersion ?? AeroScience.Shared.ApplicationBuild.Version;
    public string Name => PrivateBeta ? "AeroScience " + BetaVersion : Validation ? "AeroScience 0.7.0 Validation" : "AeroScience";
    public string Folder => PrivateBeta ? "AeroScience-Beta-v" + BetaVersion : Validation ? "AeroScience-Validation-v0.7.0" : "AeroScience";
    public string ConfigurationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
    public string RegistryKey => PrivateBeta ? @"Software\AeroScience\Beta\v" + BetaVersion : Validation ? @"Software\AeroScience\Validation\v0.7.0" : @"Software\AeroScience";
    public int Port => PrivateBeta ? 5087 : Validation ? 5086 : 5080;
    // Deliberately shared: preserve the older application's exclusion mechanism.
    public const string DesktopMutex = @"Local\AeroScience.Desktop";
    public string ToolbarPackage => PrivateBeta ? "aeroscience-beta-v0-7-1-beta-2-toolbar" : Validation ? "aeroscience-validation-v0-7-0-toolbar" : "aeroscience-toolbar";
    public string ToolbarTitle => Name;
    public string ToolbarFolder => PrivateBeta ? "AeroScienceBetaV071Beta2" : Validation ? "AeroScienceValidationV070" : "AeroScience";
    public string ToolbarRegistrationPath => PrivateBeta ? "InGamePanels/aeroscience_beta_v071_beta2_panel.spb" : Validation ? "InGamePanels/aeroscience_validation_v070_panel.spb" : "InGamePanels/aeroscience_panel.spb";
    private string Channel => PrivateBeta ? "AeroScience.Beta.0_7_1_beta_2" : Validation ? "AeroScience.Validation.0_7_0" : "AeroScience";
    public string CommandEvent => Channel + ".Command.v1";
    public string ReplyEvent => Channel + ".Reply.v1";
    public string SessionCookieName => Validation ? Channel + ".Session" : AccessControl.CookieName;
    public EngineOptions EngineOptions(AppPreferences preferences) => new()
    {
        ConfigurationDirectory=ConfigurationDirectory, DataDirectory=preferences.MissionDirectory,
        ScienceLibraryDirectory=preferences.ScienceLibraryDirectory, Port=Port, RequireExistingDatabase=true,
        EnableLan=preferences.EnableLan,LanAddress=preferences.LanAddress,SamplingIntervalMs=preferences.SamplingIntervalMs
    };
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
            throw new IOException($"{Name} requires its isolated configuration root and port {Port}. Existing profiles were not opened.");
        var preferences = Preferences();
        preferences.ValidateDirectory(options.DataDirectory);
        preferences.ValidateDirectory(options.ScienceLibraryDirectory ?? throw new IOException("Validation requires an explicit isolated science library."));
    }
}
