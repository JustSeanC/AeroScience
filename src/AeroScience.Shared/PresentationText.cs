using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AeroScience.Shared;

public sealed record PresentationLocale(string Id, string Name, bool Preview);
public sealed record ToolbarPresentation(int Version, int CatalogVersion, string Locale, long Revision = 0);

// Canonical resources are linked from the browser catalog, never a second translation source.
public static class PresentationText
{
    private static JsonElement Read(string name)
    {
        using var stream = typeof(PresentationText).Assembly.GetManifestResourceStream("AeroScience.Presentation." + name)
            ?? throw new InvalidOperationException("The bundled presentation catalog is missing.");
        using var json = JsonDocument.Parse(stream); return json.RootElement.Clone();
    }
    private static readonly JsonElement Manifest = Read("manifest.json"), English = Read("en-US.json"), Schema = Read("schema.json");
    public const int ContractVersion = 1;
    public static int CatalogVersion => Manifest.GetProperty("catalogVersion").GetInt32();
    public static IReadOnlyList<PresentationLocale> Locales { get; } = Manifest.GetProperty("locales").EnumerateArray()
        .Select(l => new PresentationLocale(l.GetProperty("id").GetString()!, l.GetProperty("name").GetString()!, l.GetProperty("review").GetString() == "preview")).ToArray();
    public static string Locale(string? requested) => Locales.FirstOrDefault(l => l.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Id ?? "en-US";
    // P02 ships only English. A locale identity/preview is not a translated catalog.
    public static string EffectiveLocale(string? requested) => "en-US";
    public static string Text(string key, IReadOnlyDictionary<string, object>? values = null)
    {
        var fallback = English.GetProperty("messages").GetProperty("common.messageUnavailable").GetString()!;
        if (!Schema.GetProperty("messages").TryGetProperty(key, out var rule) || !English.GetProperty("messages").TryGetProperty(key, out var message)) return fallback;
        values ??= new Dictionary<string, object>();
        foreach (var parameter in rule.GetProperty("parameters").EnumerateArray())
            if (!values.TryGetValue(parameter.GetString()!, out var value) || value is null) return fallback;
        if (message.ValueKind == JsonValueKind.Object)
        {
            var plural = rule.GetProperty("plural").GetString()!;
            if (!values.TryGetValue(plural, out var count) || !long.TryParse(Convert.ToString(count, CultureInfo.InvariantCulture), out var n) || n < 0) return fallback;
            message = message.GetProperty(n == 1 ? "one" : "other");
        }
        return Regex.Replace(message.GetString()!, @"\{([a-z][a-zA-Z0-9]*)\}", m => Convert.ToString(values[m.Groups[1].Value], CultureInfo.InvariantCulture) ?? "");
    }
}
