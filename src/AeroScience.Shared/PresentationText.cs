using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AeroScience.Shared;

public sealed record PresentationLocale(string Id, string Name, bool Preview, string Review = "source");
public sealed record ToolbarPresentation(int Version, int CatalogVersion, string Locale, long Revision = 0, string? CatalogIdentity = null);

// One canonical source, consumed by native .NET without changing thread culture,
// recorded units, JSON serialization, UTC, scientific values or permissions.
public static class PresentationText
{
    private static JsonElement Read(string name)
    {
        using var stream = typeof(PresentationText).Assembly.GetManifestResourceStream("AeroScience.Presentation." + name)
            ?? throw new InvalidOperationException("The bundled presentation catalog is missing.");
        using var json = JsonDocument.Parse(stream); return json.RootElement.Clone();
    }
    private static readonly JsonElement Manifest = Read("manifest.json"), English = Read("en-US.json"), Schema = Read("schema.json");
    private static readonly IReadOnlyDictionary<string, JsonElement> Catalogs = LoadCatalogs();
    public const int ContractVersion = 1;
    public static int CatalogVersion => Manifest.GetProperty("catalogVersion").GetInt32();
    public static string SourceIdentity => Manifest.GetProperty("catalogIdentity").GetString()!;
    public static string CatalogIdentity => Manifest.TryGetProperty("bundleIdentity",out var bundle) ? bundle.GetString()! : SourceIdentity;
    public static IReadOnlyList<PresentationLocale> Locales { get; } = Manifest.GetProperty("locales").EnumerateArray()
        .Select(l => new PresentationLocale(l.GetProperty("id").GetString()!, l.GetProperty("name").GetString()!, l.GetProperty("review").GetString() is "preview" or "candidate", l.GetProperty("review").GetString()!)).ToArray();
    private static IReadOnlyDictionary<string, JsonElement> LoadCatalogs()
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["en-US"] = English };
        foreach (var locale in Manifest.GetProperty("locales").EnumerateArray())
        {
            var id = locale.GetProperty("id").GetString()!; if (id == "en-US") continue;
            try { var catalog = Read(id + ".json"); if (IsCompatibleCatalog(catalog, id)) result[id] = catalog; }
            catch (Exception error) when (error is InvalidOperationException or JsonException) { /* deterministic English fallback */ }
        }
        return result;
    }
    public static string Locale(string? requested) => Locales.FirstOrDefault(l => l.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Id ?? "en-US";
    public static bool IsCompatibleCatalog(JsonElement catalog, string locale) => catalog.ValueKind == JsonValueKind.Object
        && catalog.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == CatalogVersion
        && catalog.TryGetProperty("locale", out var identity) && identity.ValueKind == JsonValueKind.String && identity.GetString() == locale
        && catalog.TryGetProperty("sourceHash", out var source) && source.ValueKind == JsonValueKind.String && source.GetString() == SourceIdentity
        && catalog.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Object;
    public static string EffectiveLocale(string? requested) => Catalogs.ContainsKey(Locale(requested)) ? Locale(requested) : "en-US";
    public static string PluralCategory(string locale, long count) => locale is "es-ES" or "fr-FR" or "pt-BR" && count != 0 && count % 1000000 == 0 ? "many"
        : (locale is "fr-FR" or "pt-BR" ? count is 0 or 1 : count == 1) ? "one" : "other";
    private static bool Count(object? value, out long count)
    {
        count = -1; if (value is null or string or bool) return false;
        if (!decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || n < 0 || n != decimal.Truncate(n) || n > 9007199254740991m) return false;
        count = (long)n; return true;
    }
    private static bool Valid(string? text, string[] parameters) => !string.IsNullOrWhiteSpace(text)
        && parameters.All(p => text.Contains("{" + p + "}", StringComparison.Ordinal))
        && Regex.Matches(text, @"\{([a-z][a-zA-Z0-9]*)\}").All(m => parameters.Contains(m.Groups[1].Value))
        && !Regex.IsMatch(Regex.Replace(text, @"\{[a-z][a-zA-Z0-9]*\}", ""), "[{}]");
    public static string Text(string key, IReadOnlyDictionary<string, object>? values = null, string? locale = null)
    {
        var fallback = English.GetProperty("messages").GetProperty("common.messageUnavailable").GetString()!;
        if (!Schema.GetProperty("messages").TryGetProperty(key, out var rule) || !English.GetProperty("messages").TryGetProperty(key, out var original)) return fallback;
        values ??= new Dictionary<string, object>();
        var parameters = rule.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()!).ToArray();
        foreach (var parameter in parameters)
            if (!values.TryGetValue(parameter, out var value) || value is not (string or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal) || value is double d && !double.IsFinite(d) || value is float f && !float.IsFinite(f)) return fallback;
        long count = 0;
        if (rule.TryGetProperty("plural", out var plural) && (!values.TryGetValue(plural.GetString()!, out var pluralValue) || !Count(pluralValue, out count))) return fallback;
        var effective = EffectiveLocale(locale); var selected = Catalogs[effective];
        if (selected.TryGetProperty("pendingEnglishFallbackKeys", out var pending) && pending.EnumerateArray().Any(p => p.GetString() == key)) { effective = "en-US"; selected = English; }
        var message = selected.GetProperty("messages").TryGetProperty(key, out var translated) ? translated : original;
        string? Template(JsonElement candidate, string language) => candidate.ValueKind == JsonValueKind.String ? candidate.GetString()
            : candidate.ValueKind == JsonValueKind.Object && (candidate.TryGetProperty(PluralCategory(language, count), out var form) || candidate.TryGetProperty("other", out form)) && form.ValueKind == JsonValueKind.String ? form.GetString() : null;
        var text = Template(message, effective); if (!Valid(text, parameters)) text = Template(original, "en-US");
        if (!Valid(text, parameters)) return fallback;
        return Regex.Replace(text!, @"\{([a-z][a-zA-Z0-9]*)\}", m => Convert.ToString(values[m.Groups[1].Value], CultureInfo.InvariantCulture) ?? "");
    }
}
