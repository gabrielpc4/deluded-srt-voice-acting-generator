using System.Text.Json;

/// <summary>Compact JSON-lines diagnostics. Every record is independently
/// parseable, timestamped, and safe to append to a rolling file.</summary>
internal static class DiagnosticEvent
{
    public static string Create(string eventName, params (string Key, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["event"] = eventName
        };
        foreach ((string key, object? value) in fields) values[key] = value;
        return JsonSerializer.Serialize(values);
    }

    public static bool IsJsonObject(string value) => value.TrimStart().StartsWith('{');
}
