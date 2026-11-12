// Screen 05 address checks. Requiredness of region and postal code comes from
// ui/address/country-rules.json. A country that is not in that file is rejected.
// This does not invent an N/A value.
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Admin.Api;

public sealed record LocationAddress(
    string Line1, string? Line2, string City, string Country, string? Region, string? Postal, string Phone, string? Email, string TimeZone);

public static class CountryAddress
{
    public const string RowsSha256 = "256bb1e0d42e36e9518bc7c71cd9d0df924e047149b7dffd43f94a651a5a40bb";
    public const string FileSha256 = "cc95aa68a103b97fcfe881064e77af278921aee842c66dda7abc2c22e0210f0d";

    static readonly Dictionary<string, (string State, string Postal)> Rules = Load();

    public static bool TryParse(JsonElement address, List<FieldError> errors, out LocationAddress? parsed)
    {
        parsed = null;
        if (address.ValueKind != JsonValueKind.Object) { errors.Add(new("address", "invalid")); return false; }
        var line1 = Text(address, "address_line_1", true, errors);
        var line2 = Text(address, "address_line_2", false, errors);
        var city = Text(address, "city", true, errors);
        var country = Text(address, "country", true, errors);
        var region = Text(address, "region", false, errors);
        var postal = Text(address, "postal_code", false, errors);
        var phone = Text(address, "phone", true, errors);
        var email = Text(address, "email", false, errors);
        var zone = Text(address, "time_zone", true, errors);
        if (country.Length > 0)
        {
            if (!Rules.TryGetValue(country, out var rule)) errors.Add(new("country", "unknown_country"));
            else
            {
                Place("region", rule.State, region, errors);
                Place("postal_code", rule.Postal, postal, errors);
            }
        }
        if (email.Length > 0 && !email.Contains('@')) errors.Add(new("email", "invalid"));
        if (errors.Count > 0) return false;
        parsed = new LocationAddress(
            line1,
            line2.Length == 0 ? null : line2,
            city,
            country,
            region.Length == 0 ? null : region,
            postal.Length == 0 ? null : postal,
            phone,
            email.Length == 0 ? null : email,
            zone);
        return true;
    }

    static string Text(JsonElement address, string name, bool required, List<FieldError> errors)
    {
        if (!address.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) errors.Add(new(name, "required"));
            return "";
        }
        if (value.ValueKind != JsonValueKind.String) { errors.Add(new(name, "invalid")); return ""; }
        var text = value.GetString()?.Trim() ?? "";
        if (required && text.Length == 0) errors.Add(new(name, "required"));
        return text;
    }

    static void Place(string field, string rule, string value, List<FieldError> errors)
    {
        if (rule == "not_used")
        {
            if (value.Length > 0) errors.Add(new(field, "not_used"));
            return;
        }
        if (rule == "required")
        {
            if (value.Length == 0) errors.Add(new(field, "required"));
            return;
        }
        if (rule != "optional") errors.Add(new(field, "bad_rule"));
    }

    static Dictionary<string, (string State, string Postal)> Load()
    {
        using var stream = typeof(CountryAddress).Assembly.GetManifestResourceStream("Admin.Api.country-rules.json")
            ?? throw new InvalidOperationException("missing embedded country-rules.json");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var bytes = copy.ToArray();
        var file = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (file != FileSha256) throw new InvalidOperationException("country-rules.json sha256 mismatch");
        var node = JsonNode.Parse(bytes) ?? throw new InvalidOperationException("country-rules.json is empty");
        var rows = node["rows"] ?? throw new InvalidOperationException("country-rules.json has no rows");
        var compact = rows.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var rowsHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(compact))).ToLowerInvariant();
        if (rowsHash != RowsSha256) throw new InvalidOperationException("country-rules rows_sha256 mismatch");
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var row in rows.AsArray())
        {
            var country = row?["country"]?.GetValue<string>() ?? "";
            map[country] = (row?["state"]?.GetValue<string>() ?? "", row?["postal"]?.GetValue<string>() ?? "");
        }
        return map;
    }
}
