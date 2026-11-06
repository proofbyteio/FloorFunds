using System.Text.Json;

namespace Auth.Data;

/// <summary>Expected reference rows generated with the seed. The archive is not required at runtime.</summary>
public static class RefRows
{
    public static JsonDocument Load()
    {
        using var stream = typeof(RefRows).Assembly.GetManifestResourceStream("Auth.Data.ref-rows.json")
            ?? throw new InvalidOperationException("ref-rows.json is not in the assembly");
        return JsonDocument.Parse(stream);
    }
}
