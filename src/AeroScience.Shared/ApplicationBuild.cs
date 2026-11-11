using System.Reflection;

namespace AeroScience.Shared;

// Informational version includes the private beta label; CLR/package versions stay numeric.
public static class ApplicationBuild
{
    public static string Version { get; } = typeof(ApplicationBuild).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.7.1";
}
