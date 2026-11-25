using System.Text.Json;
using AeroScience.Shared;

namespace AeroScience.XPlane;

internal sealed class XPlaneProtocolException(string code, ConnectionState state = ConnectionState.Unavailable) : IOException(code)
{
    internal string Code { get; } = code;
    internal ConnectionState State { get; } = state;
}

internal static class XPlaneProtocol
{
    internal static JsonElement Parse(ReadOnlyMemory<byte> bytes, int maximum)
    {
        if (bytes.Length == 0 || bytes.Length > maximum) throw new XPlaneProtocolException("InvalidMessageSize");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            Validate(doc.RootElement);
            return doc.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new XPlaneProtocolException("MalformedJson"); }
    }
    private static void Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Contains('\ufffd'))
            throw new XPlaneProtocolException("MalformedUnicode");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name) || seen.Count > 128 || property.Name.Length > 256)
                    throw new XPlaneProtocolException("DuplicateOrOversizedObject");
                Validate(property.Value);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 128) throw new XPlaneProtocolException("OversizedArray");
            foreach (var item in value.EnumerateArray()) Validate(item);
        }
    }
    internal static bool Property(JsonElement item, string name, out JsonElement value)
    {
        value = default;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out value);
    }
    internal static string Text(JsonElement item, string name, int maximum = 256)
    {
        if (!Property(item, name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > maximum
            || text.Any(char.IsControl) || text.Contains('\ufffd'))
            throw new XPlaneProtocolException("InvalidContractText");
        return text;
    }
    internal static long Id(JsonElement item, string name)
    {
        if (!Property(item, name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var id) || id <= 0) throw new XPlaneProtocolException("InvalidNumericId");
        return id;
    }
}
