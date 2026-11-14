using System.Reflection;
using System.Text.Json;
using AeroScience.Missions;

namespace AeroScience.Engine;

// Selected at compilation, never by editable preferences or a command-line switch.
public sealed record ApplicationProfile(bool Validation, string ValidationVersion = "0.7.0", string? Instance = null)
{
    public static ApplicationProfile Current { get; } = FromAssembly(typeof(ApplicationProfile).Assembly);
    public static ApplicationProfile FromAssembly(Assembly assembly)
    {
        var metadata=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().GroupBy(a=>a.Key).ToDictionary(g=>g.Key,g=>g.Last().Value);
        return new(metadata.GetValueOrDefault("AeroScienceProfile")=="validation",
            metadata.GetValueOrDefault("AeroScienceValidationVersion")??"0.7.0",metadata.GetValueOrDefault("AeroScienceValidationInstance"));
    }
    public bool Native080 => Validation && (ValidationVersion switch
    { "0.8.0" => true, "0.7.0" => false, _ => throw new InvalidOperationException("Unknown compiled validation profile.") });
    private string PrivateInstance => Guid.TryParseExact(Instance,"N",out var id)&&id!=Guid.Empty&&Instance==id.ToString("N")
        ? Instance : throw new InvalidOperationException("The 0.8.0 private build requires a compiled, nonempty attempt identity.");
    public string Name => Native080 ? "AeroScience 0.8.0 Validation ["+PrivateInstance[..8]+"]" : Validation ? "AeroScience 0.7.0 Validation" : "AeroScience";
    public string Folder => Native080 ? Path.Combine("AeroScience-Validation-v0.8.0",PrivateInstance) : Validation ? "AeroScience-Validation-v0.7.0" : "AeroScience";
    public string ConfigurationDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Folder);
    public string RegistryKey => Native080 ? @"Software\AeroScience\Validation\v0.8.0\"+PrivateInstance : Validation ? @"Software\AeroScience\Validation\v0.7.0" : @"Software\AeroScience";
    public int Port => Native080 ? 5088 : Validation ? 5086 : 5080;
    // Deliberately shared: preserve the older application's exclusion mechanism.
    public const string DesktopMutex = @"Local\AeroScience.Desktop";
    public string ToolbarPackage => Native080 ? "aeroscience-validation-v0-8-0-toolbar" : Validation ? "aeroscience-validation-v0-7-0-toolbar" : "aeroscience-toolbar";
    public string ToolbarTitle => Native080 ? "AeroScience 0.8.0 Validation" : Validation ? "AeroScience 0.7.0 Validation" : "AeroScience";
    public string ToolbarFolder => Native080 ? "AeroScienceValidationV080" : Validation ? "AeroScienceValidationV070" : "AeroScience";
    public string ToolbarRegistrationPath => Native080 ? "InGamePanels/aeroscience_validation_v080_panel.spb" : Validation ? "InGamePanels/aeroscience_validation_v070_panel.spb" : "InGamePanels/aeroscience_panel.spb";
    public string CommandEvent => Native080 ? "AeroScience.Validation.0_8_0.Command.v1" : Validation ? "AeroScience.Validation.0_7_0.Command.v1" : "AeroScience.Command.v1";
    public string ReplyEvent => Native080 ? "AeroScience.Validation.0_8_0.Reply.v1" : Validation ? "AeroScience.Validation.0_7_0.Reply.v1" : "AeroScience.Reply.v1";
    public string SessionCookieName => Native080 ? "AeroScience.Validation.0_8_0."+PrivateInstance+".Session" : Validation ? "AeroScience.Validation.0_7_0.Session" : AccessControl.CookieName;
    public EngineOptions EngineOptions(AppPreferences preferences) => new()
    {
        ConfigurationDirectory=ConfigurationDirectory, DataDirectory=preferences.MissionDirectory,
        ScienceLibraryDirectory=preferences.ScienceLibraryDirectory, Port=Port, RequireExistingDatabase=true,
        EnableLan=preferences.EnableLan,LanAddress=preferences.LanAddress,SamplingIntervalMs=preferences.SamplingIntervalMs
    };
    public PreferenceStore Preferences()
    {
        if(Native080)ValidatePrivateMarker(ConfigurationDirectory);
        return new(ConfigurationDirectory, Validation ? ConfigurationDirectory : null);
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
        if(!Validation)return null;
        using var json=JsonDocument.Parse(command);var root=json.RootElement;
        return root.TryGetProperty("validationSource",out var source) && source.ValueKind==JsonValueKind.String && source.GetString()==SourceCommit
            && (!Native080||root.TryGetProperty("validationInstance",out var instance)&&instance.ValueKind==JsonValueKind.String&&instance.GetString()==PrivateInstance)
            ? null : "Validation toolbar source mismatch. Install this candidate's matching validation toolbar in Settings.";
    }
    public void ValidateEngine(EngineOptions options)
    {
        if (!Validation) return;
        if (options.Port != Port || options.ConfigurationDirectory is null ||
            !Path.GetFullPath(options.ConfigurationDirectory).Equals(Path.GetFullPath(ConfigurationDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Validation requires its isolated configuration root and port {Port}. Production was not opened.");
        var preferences = Preferences();
        preferences.ValidateDirectory(options.DataDirectory);
        preferences.ValidateDirectory(options.ScienceLibraryDirectory ?? throw new IOException("Validation requires an explicit isolated science library."));
    }
}
