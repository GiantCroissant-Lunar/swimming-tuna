using System.Text.Json;

namespace SwarmAssistant.Runtime.Ui;

internal static class UiActionPayload
{
    public static string? GetString(Dictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };
    }

    public static int? GetInt(Dictionary<string, object?>? payload, string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var intValue) => intValue,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var intValue) => intValue,
            string text when int.TryParse(text, out var intValue) => intValue,
            _ => null
        };
    }
}
