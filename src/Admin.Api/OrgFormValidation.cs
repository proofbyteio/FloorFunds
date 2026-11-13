// Shared Partner / Client / Location field rules (DEC-257). Exact owner-approved sentences
// live in Code on FieldError so the existing 422 validation_failed / fields[] shape carries them.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PhoneNumbers;

namespace Admin.Api;

public sealed record OrgAddress(
    string AddressLine1,
    string AddressLine2,
    string City,
    string Country,
    string Region,
    string PostalCode,
    string PhoneE164,
    string? Email,
    string TimeZone);

public static class OrgFormValidation
{
    static readonly PhoneNumberUtil Phone = PhoneNumberUtil.GetInstance();

    // Letters (Unicode), digits, spaces, and & . , ' - ( ) /
    static readonly Regex NameChars = new(@"^[\p{L}\p{N} &\.,'\-()/]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex StreetChars = new(@"^[\p{L}\p{N} #\-\.,/']+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex CityChars = new(@"^[\p{L} \-\.']+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex HasControlOrEmoji = new(@"\p{C}|\p{Cs}|\p{So}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string? value)
    {
        var t = (value ?? "").Trim();
        if (t.Length == 0) return "";
        var sb = new StringBuilder(t.Length);
        var space = false;
        foreach (var ch in t)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public static int ScalarLen(string s) => s.EnumerateRunes().Count();

    public static string? ValidateEntityName(string entity, string? raw, List<FieldError> errors)
    {
        // entity = "partner" | "client" | "location" (lowercase for messages that need it)
        var label = entity switch
        {
            "partner" => "Partner",
            "client" => "Client",
            "location" => "Location",
            _ => throw new ArgumentOutOfRangeException(nameof(entity)),
        };
        var field = "name";
        if (raw is null)
        {
            errors.Add(new(field, $"Enter a {entity} name."));
            return null;
        }
        var name = Normalize(raw);
        if (name.Length == 0)
        {
            errors.Add(new(field, $"Enter a {entity} name."));
            return null;
        }
        var n = ScalarLen(name);
        if (n is < 2 or > 100)
        {
            errors.Add(new(field, $"{label} name must be 2 to 100 characters."));
            return null;
        }
        if (HasControlOrEmoji.IsMatch(name) || !NameChars.IsMatch(name))
        {
            errors.Add(new(field, $"{label} name can only use letters, numbers, spaces and & . , ' - ( ) /"));
            return null;
        }
        return name;
    }

    public static async Task<OrgAddress?> ParseAddressAsync(JsonElement address, List<FieldError> errors,
        Func<string, Task<bool>> countryActive,
        Func<string, string, Task<bool>> regionOk,
        Func<string, string, Task<bool>> timeZoneOk,
        Func<string, Task<(string Pattern, int MaxLength, string Normalize)?>> postalRule)
    {
        if (address.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("address", "Enter a street address."));
            return null;
        }

        var before = errors.Count;
        var line1 = StreetLine1(Prop(address, "address_line_1"), errors);
        var line2 = StreetLine2(Prop(address, "address_line_2"), errors);
        var city = City(Prop(address, "city"), errors);
        var countryRaw = Prop(address, "country");
        string? country = null;
        if (countryRaw == "\u0000" || string.IsNullOrWhiteSpace(countryRaw))
            errors.Add(new("country", "Select a country."));
        else
        {
            var code = countryRaw.Trim().ToUpperInvariant();
            if (code.Length != 2 || !await countryActive(code))
                errors.Add(new("country", "Select a country."));
            else country = code;
        }
        var region = await RegionAsync(Prop(address, "region"), country, regionOk, errors);
        var postal = await PostalAsync(Prop(address, "postal_code"), country, postalRule, errors);
        var phone = PhoneNumber(Prop(address, "phone"), country, errors);
        var email = Email(Prop(address, "email"), errors);
        var zone = await TimeZoneAsync(Prop(address, "time_zone"), country, timeZoneOk, errors);

        if (errors.Count > before || line1 is null || city is null || country is null || region is null
            || postal is null || phone is null || zone is null)
            return null;

        return new OrgAddress(line1, line2 ?? "", city, country, region, postal, phone, email, zone);
    }

    static string? Prop(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) return "\u0000"; // marker: wrong type
        return v.GetString();
    }

    static string? StreetLine1(string? raw, List<FieldError> errors)
    {
        if (raw == "\u0000") { errors.Add(new("address_line_1", "Enter a street address.")); return null; }
        var v = Normalize(raw);
        if (v.Length == 0) { errors.Add(new("address_line_1", "Enter a street address.")); return null; }
        if (ScalarLen(v) > 100) { errors.Add(new("address_line_1", "Street address must be 100 characters or fewer.")); return null; }
        if (HasControlOrEmoji.IsMatch(v) || !StreetChars.IsMatch(v))
        {
            errors.Add(new("address_line_1", "Street address can only use letters, numbers, spaces and # - . , / '"));
            return null;
        }
        return v;
    }

    static string? StreetLine2(string? raw, List<FieldError> errors)
    {
        if (raw == "\u0000") { errors.Add(new("address_line_2", "Address line 2 can only use letters, numbers, spaces and # - . , / '")); return null; }
        if (raw is null) return "";
        var v = Normalize(raw);
        if (v.Length == 0) return "";
        if (ScalarLen(v) > 40) { errors.Add(new("address_line_2", "Address line 2 must be 40 characters or fewer.")); return null; }
        if (HasControlOrEmoji.IsMatch(v) || !StreetChars.IsMatch(v))
        {
            errors.Add(new("address_line_2", "Address line 2 can only use letters, numbers, spaces and # - . , / '"));
            return null;
        }
        return v;
    }

    static string? City(string? raw, List<FieldError> errors)
    {
        if (raw == "\u0000") { errors.Add(new("city", "Enter a city.")); return null; }
        var v = Normalize(raw);
        if (v.Length == 0) { errors.Add(new("city", "Enter a city.")); return null; }
        var n = ScalarLen(v);
        if (n is < 2 or > 50) { errors.Add(new("city", "City must be 2 to 50 characters.")); return null; }
        if (HasControlOrEmoji.IsMatch(v) || !CityChars.IsMatch(v) || v.Any(char.IsDigit))
        {
            errors.Add(new("city", "City can only use letters, spaces and - . '"));
            return null;
        }
        return v;
    }

    static async Task<string?> RegionAsync(string? raw, string? country, Func<string, string, Task<bool>> regionOk, List<FieldError> errors)
    {
        if (country is null) return null;
        if (raw == "\u0000" || string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new("region", "Select a state or province."));
            return null;
        }
        var code = raw.Trim().ToUpperInvariant();
        if (!await regionOk(country, code))
        {
            errors.Add(new("region", "Select a state or province."));
            return null;
        }
        return code;
    }

    static async Task<string?> PostalAsync(string? raw, string? country,
        Func<string, Task<(string Pattern, int MaxLength, string Normalize)?>> postalRule, List<FieldError> errors)
    {
        if (country is null) return null;
        if (raw == "\u0000" || string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new("postal_code", "Enter a ZIP or postal code."));
            return null;
        }
        var rule = await postalRule(country);
        if (rule is null)
        {
            errors.Add(new("postal_code", "Enter a ZIP or postal code."));
            return null;
        }
        var v = raw.Trim();
        if (rule.Value.Normalize == "ca_postal")
        {
            v = Regex.Replace(v.ToUpperInvariant(), @"\s+", "");
            if (v.Length > 3) v = v[..3] + " " + v[3..Math.Min(6, v.Length)];
        }
        else if (rule.Value.Normalize == "us_zip")
            v = Regex.Replace(v, @"\s+", "");

        if (ScalarLen(v) > rule.Value.MaxLength || !Regex.IsMatch(v, rule.Value.Pattern))
        {
            errors.Add(new("postal_code", country == "CA"
                ? "Enter a valid Canadian postal code, like A1A 1A1."
                : "Enter a valid US ZIP code, like 12345 or 12345-6789."));
            return null;
        }
        return v;
    }

    /// <summary>Strip spaces - . ( ); reject letters/extensions; IsValidNumberForRegion; return E.164.</summary>
    public static string? PhoneNumber(string? raw, string? region, List<FieldError> errors)
    {
        if (region is null) return null;
        if (raw == "\u0000" || string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new("phone", "Enter a phone number."));
            return null;
        }
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '.' or '(' or ')') continue;
            sb.Append(ch);
        }
        var cleaned = sb.ToString();
        if (cleaned.Length == 0)
        {
            errors.Add(new("phone", "Enter a phone number."));
            return null;
        }
        if (cleaned.Any(char.IsLetter) || cleaned.Contains('x', StringComparison.OrdinalIgnoreCase)
            || cleaned.Contains("ext", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("phone", "Enter a valid phone number for the selected country."));
            return null;
        }
        try
        {
            var parsed = Phone.Parse(cleaned, region);
            if (!Phone.IsValidNumberForRegion(parsed, region))
            {
                errors.Add(new("phone", "Enter a valid phone number for the selected country."));
                return null;
            }
            var e164 = Phone.Format(parsed, PhoneNumberFormat.E164);
            if (e164.Length < 3 || e164[0] != '+' || e164[1..].Any(c => !char.IsDigit(c)) || e164.Length > 16)
            {
                errors.Add(new("phone", "Enter a valid phone number for the selected country."));
                return null;
            }
            return e164;
        }
        catch (NumberParseException)
        {
            errors.Add(new("phone", "Enter a valid phone number for the selected country."));
            return null;
        }
    }

    public static string FormatNational(string e164, string region)
    {
        try
        {
            var n = Phone.Parse(e164, region);
            return Phone.Format(n, PhoneNumberFormat.NATIONAL);
        }
        catch (NumberParseException)
        {
            return e164;
        }
    }

    static string? Email(string? raw, List<FieldError> errors)
    {
        if (raw == "\u0000")
        {
            errors.Add(new("email", "Enter a valid email address, like name@example.com."));
            return null;
        }
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Length == 0) return null;
        if (ScalarLen(v) > 100)
        {
            errors.Add(new("email", "Email must be 100 characters or fewer."));
            return null;
        }
        var at = v.IndexOf('@');
        if (at <= 0 || at != v.LastIndexOf('@') || v.Contains(' ')
            || !v[(at + 1)..].Contains('.') || v.StartsWith('@') || v.EndsWith('@') || v.EndsWith('.'))
        {
            errors.Add(new("email", "Enter a valid email address, like name@example.com."));
            return null;
        }
        return v.ToLowerInvariant();
    }

    static async Task<string?> TimeZoneAsync(string? raw, string? country, Func<string, string, Task<bool>> timeZoneOk, List<FieldError> errors)
    {
        if (country is null) return null;
        if (raw == "\u0000" || string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new("time_zone", "Select a time zone."));
            return null;
        }
        var name = raw.Trim();
        if (!await timeZoneOk(country, name))
        {
            errors.Add(new("time_zone", "Select a time zone."));
            return null;
        }
        return name;
    }

    public static JsonObject AddressJson(OrgAddress a, string phoneDisplay) => new()
    {
        ["address_line_1"] = a.AddressLine1,
        ["address_line_2"] = a.AddressLine2,
        ["city"] = a.City,
        ["country"] = a.Country,
        ["region"] = a.Region,
        ["postal_code"] = a.PostalCode,
        ["phone"] = phoneDisplay,
        ["phone_e164"] = a.PhoneE164,
        ["email"] = a.Email is null ? null : a.Email,
        ["time_zone"] = a.TimeZone,
    };
}
