using System.Text.Json;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// F-SURR-LOGIN (TLD 16:48): the one JSON parse-and-decode entry for the stand-in. System.Text.Json reads property names and
// string values lazily, so a lone or reversed surrogate escape (or invalid UTF-8) throws InvalidOperationException at first
// use, outside the caller's catch. Parse here, then force-decode EVERY key name and string value at every level; any failure
// returns null and the caller takes its own "not valid JSON" path (400 invalid_request, attestation missing/invalid, ...).
// Only JsonException and InvalidOperationException are caught; nothing wider.
public static class StandInJson
{
    public static JsonNode? Parse(string text)
    {
        try { var n = JsonNode.Parse(text); Decode(n); return n; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static async Task<JsonNode?> ParseAsync(Stream body)
    {
        try { var n = await JsonNode.ParseAsync(body); Decode(n); return n; }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static JsonObject? ParseObject(string text) => Parse(text) as JsonObject;
    public static async Task<JsonObject?> ParseObjectAsync(Stream body) => await ParseAsync(body) as JsonObject;

    // A JSON string value, or null when absent/null; false when present but not a string (node type checked before GetValue).
    public static bool TryOptString(JsonObject o, string key, out string? s)
    {
        s = null;
        var n = o[key];
        if (n is null) return true;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String) { s = v.GetValue<string>(); return true; }
        return false;
    }

    static void Decode(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o: foreach (var kv in o) Decode(kv.Value); break; // enumerating materialises (decodes) every key name
            case JsonArray a: foreach (var x in a) Decode(x); break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String: _ = v.GetValue<string>(); break;
        }
    }
}
