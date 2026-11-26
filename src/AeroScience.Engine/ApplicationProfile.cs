using System.Reflection;
using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

// Selected at compilation, never by editable preferences or a command-line switch.
public sealed record ApplicationProfile(bool Validation, string ValidationVersion = "0.7.0", string? Instance = null, bool Beta080 = false)
{
    public const string BetaVersion = "0.8.0-beta.1";
    public bool Isolated => Validation || Beta080;
    public string Channel => Beta080 ? "beta" : Validation ? "validation" : "prerelease";
    public static ApplicationProfile Current { get; } = FromAssembly(typeof(ApplicationProfile).Assembly);
    public static ApplicationProfile FromAssembly(Assembly assembly)
    {
        var metadata=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().GroupBy(a=>a.Key).ToDictionary(g=>g.Key,g=>g.Last().Value);
        return new(metadata.GetValueOrDefault("AeroScienceProfile")=="validation",
            metadata.GetValueOrDefault("AeroScienceValidationVersion")??"0.7.0",metadata.GetValueOrDefault("AeroScienceValidationInstance"),metadata.GetValueOrDefault("AeroScienceProfile")=="beta080");
    }
    public bool Native080 => Validation && (ValidationVersion switch
    { "0.8.0" => true, "0.7.0" => false, _ => throw new InvalidOperationException("Unknown compiled validation profile.") });
    private string PrivateInstance => Guid.TryParseExact(Instance,"N",out var id)&&id!=Guid.Empty&&Instance==id.ToString("N")
        ? Instance : throw new InvalidOperationException("The 0.8.0 private build requires a compiled, nonempty attempt identity.");
    public string Name => Beta080 ? "AeroScience v0.8.0 Beta 1" : Native080 ? "AeroScience 0.8.0 Validation ["+PrivateInstance[..8]+"]" : Validation ? "AeroScience 0.7.0 Validation" : "AeroScience";
    public string Folder => Beta080 ? "AeroScience-Beta-v0.8.0" : Native080 ? Path.Combine("AeroScience-Validation-v0.8.0",PrivateInstance) : Validation ? "AeroScience-Validation-v0.7.0" : "AeroScience";
    public string ConfigurationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
    public string RegistryKey => Beta080 ? @"Software\AeroScience\Beta\v0.8.0" : Native080 ? @"Software\AeroScience\Validation\v0.8.0\"+PrivateInstance : Validation ? @"Software\AeroScience\Validation\v0.7.0" : @"Software\AeroScience";
    public int Port => Beta080 ? 5089 : Native080 ? 5088 : Validation ? 5086 : 5080;
    // Deliberately shared: preserve the older application's exclusion mechanism.
    public const string DesktopMutex = @"Local\AeroScience.Desktop";
    public string ToolbarPackage => Beta080 ? "aeroscience-beta-v0-8-0-toolbar" : Native080 ? "aeroscience-validation-v0-8-0-toolbar" : Validation ? "aeroscience-validation-v0-7-0-toolbar" : "aeroscience-toolbar";
    public string ToolbarTitle => Beta080 ? "AeroScience v0.8.0 Beta 1" : Native080 ? "AeroScience 0.8.0 Validation" : Validation ? "AeroScience 0.7.0 Validation" : "AeroScience";
    public string ToolbarFolder => Beta080 ? "AeroScienceBetaV080" : Native080 ? "AeroScienceValidationV080" : Validation ? "AeroScienceValidationV070" : "AeroScience";
    public string ToolbarRegistrationPath => Beta080 ? "InGamePanels/aeroscience_beta_v080_panel.spb" : Native080 ? "InGamePanels/aeroscience_validation_v080_panel.spb" : Validation ? "InGamePanels/aeroscience_validation_v070_panel.spb" : "InGamePanels/aeroscience_panel.spb";
    public string CommandEvent => Beta080 ? "AeroScience.Beta.0_8_0.Command.v1" : Native080 ? "AeroScience.Validation.0_8_0.Command.v1" : Validation ? "AeroScience.Validation.0_7_0.Command.v1" : "AeroScience.Command.v1";
    public string ReplyEvent => Beta080 ? "AeroScience.Beta.0_8_0.Reply.v1" : Native080 ? "AeroScience.Validation.0_8_0.Reply.v1" : Validation ? "AeroScience.Validation.0_7_0.Reply.v1" : "AeroScience.Reply.v1";
    public string SessionCookieName => Beta080 ? "AeroScience.Beta.0_8_0.Session" : Native080 ? "AeroScience.Validation.0_8_0."+PrivateInstance+".Session" : Validation ? "AeroScience.Validation.0_7_0.Session" : AccessControl.CookieName;
    public EngineOptions EngineOptions(AppPreferences preferences)
    {
        var simulator = SimulatorPreferences.Effective(preferences.Simulator);
        return new()
        {
        Telemetry = simulator.Id == SimulatorPreferences.XPlane12 ? "XPlane" : "SimConnect",
        XPlanePort = simulator.XPlanePort, EnableToolbarBridge = simulator.Id == SimulatorPreferences.Msfs2024,
        ConfigurationDirectory=ConfigurationDirectory, DataDirectory=preferences.MissionDirectory,
        ScienceLibraryDirectory=preferences.ScienceLibraryDirectory, Port=Port, RequireExistingDatabase=true,
        EnableLan=preferences.EnableLan,LanAddress=preferences.LanAddress,SamplingIntervalMs=preferences.SamplingIntervalMs
        };
    }
    public PreferenceStore Preferences()
    {
        if(Native080)ValidatePrivateMarker(ConfigurationDirectory);
        return new(ConfigurationDirectory, Isolated ? ConfigurationDirectory : null);
    }
    public void ValidatePrivateMarker(string directory)
    {
        if(!Native080)return;
        new PreferenceStore(directory,directory).ValidateDirectory(directory);
        var marker=Path.Combine(directory,"validation-profile.json");
        if(!File.Exists(marker)||(File.GetAttributes(marker)&FileAttributes.ReparsePoint)!=0)
            throw new IOException("Start this private build through Start-080Validation.ps1; its isolated profile has not been verified.");
        using var doc=JsonDocument.Parse(File.ReadAllText(marker));var root=doc.RootElement;
        if(!root.TryGetProperty("version",out var version)||version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out var value)||value!=1
            ||!root.TryGetProperty("source_commit",out var source)||source.ValueKind!=JsonValueKind.String||source.GetString()!=SourceCommit
            ||!root.TryGetProperty("instance",out var instance)||instance.ValueKind!=JsonValueKind.String||instance.GetString()!=PrivateInstance)
            throw new IOException("Private profile identity differs from this compiled attempt. Existing data was not opened.");
    }
    public static string SourceCommit => typeof(ApplicationProfile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+').LastOrDefault() ?? "unknown";
    public string? AuthorizeToolbar(string command)
    {
        if(Beta080)
        {
            using var data=JsonDocument.Parse(command);var body=data.RootElement;
            return body.TryGetProperty("betaSource",out var betaSource)&&betaSource.ValueKind==JsonValueKind.String&&betaSource.GetString()==SourceCommit
                &&body.TryGetProperty("betaVersion",out var betaVersion)&&betaVersion.ValueKind==JsonValueKind.String&&betaVersion.GetString()==BetaVersion
                ? null : "Beta toolbar source mismatch. Close MSFS and install the matching Beta 1 toolbar from Settings.";
        }
        if(!Validation)return null;
        using var json=JsonDocument.Parse(command);var root=json.RootElement;
        return root.TryGetProperty("validationSource",out var source) && source.ValueKind==JsonValueKind.String && source.GetString()==SourceCommit
            && (!Native080||root.TryGetProperty("validationInstance",out var instance)&&instance.ValueKind==JsonValueKind.String&&instance.GetString()==PrivateInstance)
            ? null : "Validation toolbar source mismatch. Install this candidate's matching validation toolbar in Settings.";
    }
    public void ValidateEngine(EngineOptions options)
    {
        if (!Isolated) return;
        if (options.Port != Port || options.ConfigurationDirectory is null ||
            !Path.GetFullPath(options.ConfigurationDirectory).Equals(Path.GetFullPath(ConfigurationDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Validation requires its isolated configuration root and port {Port}. Production was not opened.");
        var preferences = Preferences();
        preferences.ValidateDirectory(options.DataDirectory);
        preferences.ValidateDirectory(options.ScienceLibraryDirectory ?? throw new IOException("Validation requires an explicit isolated science library."));
    }
}
