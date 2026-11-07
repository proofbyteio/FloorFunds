using System.Text.Json;
using Npgsql;
using PhoneNumbers;

namespace Admin.Api;

/// <summary>
/// DEC-145/146 create setup. Caps are rune counts, the same counting as a location name
/// (<see cref="string.EnumerateRunes"/>), after trim. They follow the engineering proposals and are
/// documented here: address lines 200, city and region 100, postal 32, phone input 64, email 254.
/// No US postal or region pattern. A blank optional field becomes null. Product requiredness is
/// <c>fmt_has_region</c> / <c>fmt_has_postal</c> only. <c>source_require_*</c> is not requiredness.
/// Known used stays required. Known unused stays blank with no confirmation. Unknown stays blank
/// only when that one field is confirmed unused for the selected country.
/// </summary>
public static class SetupValidator
{
    public const int AddressMax = 200;
    public const int CityMax = 100;
    public const int RegionMax = 100;
    public const int PostalMax = 32;
    public const int PhoneInputMax = 64;
    public const int EmailMax = 254;
    public const int ZoneMax = 64;

    public sealed class Draft
    {
        public Guid? ClientId { get; set; }
        public string? Address1 { get; set; }
        public string? Address2 { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? CountryCode { get; set; }
        public string? PostalCode { get; set; }
        public bool? RegionConfirmed { get; set; }
        public bool? PostalConfirmed { get; set; }
        public string? PhoneE164 { get; set; }
        public string? Email { get; set; }
        public string? TimeZoneId { get; set; }
        public string? RegionApplicability { get; set; }
        public string? PostalApplicability { get; set; }
    }

    public static Draft Parse(JsonElement o, List<FieldError> errs, bool requireClientId)
    {
        var d = new Draft();
        d.Address1 = Text(o, errs, "address1", AddressMax, required: true);
        d.Address2 = Text(o, errs, "address2", AddressMax, required: false);
        d.City = Text(o, errs, "city", CityMax, required: true);
        d.Region = Text(o, errs, "region", RegionMax, required: false);
        d.PostalCode = Text(o, errs, "postal_code", PostalMax, required: false);
        d.CountryCode = Country(o, errs);
        d.TimeZoneId = Zone(o, errs);
        d.Email = Email(o, errs);
        d.RegionConfirmed = Flag(o, errs, "region_unused_confirmed");
        d.PostalConfirmed = Flag(o, errs, "postal_unused_confirmed");
        d.PhoneE164 = Phone(o, errs, d.CountryCode);
        if (requireClientId) d.ClientId = ClientId(o, errs);
        return d;
    }

    public static (Guid Partner, string? Name, Draft Setup) ParseClient(JsonElement o, List<FieldError> errs)
    {
        foreach (var p in o.EnumerateObject())
            if (p.Name is not ("partner_id" or "name" or "address1" or "address2" or "city" or "region" or "country_code" or "postal_code"
                or "region_unused_confirmed" or "postal_unused_confirmed" or "phone" or "email" or "time_zone_id"))
                errs.Add(new(LoginConfigValidator.KeyName("", p.Name), "unknown_field"));
        Guid partner = default;
        string? name = null;
        if (!o.TryGetProperty("partner_id", out var pid)) errs.Add(new("partner_id", "required"));
        else if (pid.ValueKind != JsonValueKind.String || !LoginConfigValidator.TryUuid(pid, out partner) || partner == Guid.Empty)
            errs.Add(new("partner_id", pid.ValueKind == JsonValueKind.String ? "partner_id_format" : "type"));
        if (!o.TryGetProperty("name", out var nm)) errs.Add(new("name", "required"));
        else if (nm.ValueKind != JsonValueKind.String) errs.Add(new("name", "type"));
        else
        {
            var t = nm.GetString()!.Trim();
            if (t.EnumerateRunes().Count() is < 1 or > 200) errs.Add(new("name", "name_invalid"));
            else name = t;
        }
        return (partner, name, Parse(o, errs, requireClientId: false));
    }

    /// <summary>Loads the selected country and zone and applies the truth table. Does not insert.</summary>
    public static async Task ApplyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Draft d, List<FieldError> errs, CancellationToken ct)
    {
        if (d.CountryCode is null)
        {
            NoteApplicability(d, null, null);
            return;
        }
        bool? fr = null, fp = null;
        var usable = false;
        await using (var cmd = new NpgsqlCommand(
            "SELECT fmt_has_region, fmt_has_postal, retired_release_id IS NOT NULL FROM ref.country WHERE country_code = $1", c, tx))
        {
            cmd.Parameters.Add(new() { Value = d.CountryCode });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) errs.Add(new("country_code", "country_unknown"));
            else
            {
                fr = r.IsDBNull(0) ? null : r.GetBoolean(0);
                fp = r.IsDBNull(1) ? null : r.GetBoolean(1);
                if (r.GetBoolean(2)) errs.Add(new("country_code", "country_retired"));
                else usable = true;
            }
        }
        NoteApplicability(d, usable ? fr : null, usable ? fp : null);
        if (usable)
        {
            Side(errs, "region", d.Region, d.RegionConfirmed, fr);
            Side(errs, "postal_code", d.PostalCode, d.PostalConfirmed, fp);
        }
        if (d.TimeZoneId is null) return;
        await using var zone = new NpgsqlCommand(
            "SELECT retired_release_id IS NOT NULL FROM ref.time_zone WHERE time_zone_id = $1", c, tx);
        zone.Parameters.Add(new() { Value = d.TimeZoneId });
        var retired = await zone.ExecuteScalarAsync(ct);
        if (retired is null or DBNull) errs.Add(new("time_zone_id", "time_zone_unknown"));
        else if (retired is true) errs.Add(new("time_zone_id", "time_zone_retired"));
    }

    public static void Side(List<FieldError> errs, string field, string? value, bool? confirmed, bool? flag)
    {
        var confirm = field == "region" ? "region_unused_confirmed" : "postal_unused_confirmed";
        if (confirmed is null) return;
        if (flag is true)
        {
            if (value is null) errs.Add(new(field, "required"));
            if (confirmed is true) errs.Add(new(confirm, "confirmation_not_applicable"));
        }
        else if (flag is false)
        {
            if (value is not null) errs.Add(new(field, "field_not_used"));
            if (confirmed is true) errs.Add(new(confirm, "confirmation_not_applicable"));
        }
        else
        {
            if (value is null && confirmed is false) errs.Add(new(confirm, "unused_confirmation_required"));
            if (value is not null && confirmed is true) errs.Add(new(confirm, "confirmation_not_applicable"));
        }
    }

    public static string Applicability(bool? flag) => flag is true ? "used" : flag is false ? "unused" : "unknown";

    static void NoteApplicability(Draft d, bool? region, bool? postal)
    {
        d.RegionApplicability = Applicability(region);
        d.PostalApplicability = Applicability(postal);
    }

    static string? Text(JsonElement o, List<FieldError> errs, string key, int max, bool required)
    {
        if (!o.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null)
        {
            if (required) errs.Add(new(key, "required"));
            return null;
        }
        if (v.ValueKind != JsonValueKind.String) { errs.Add(new(key, "type")); return null; }
        var t = v.GetString()!.Trim();
        if (t.Length == 0)
        {
            if (required) errs.Add(new(key, "required"));
            return null;
        }
        if (t.EnumerateRunes().Count() > max) { errs.Add(new(key, "text_invalid")); return null; }
        return t;
    }

    static string? Country(JsonElement o, List<FieldError> errs)
    {
        if (!o.TryGetProperty("country_code", out var v) || v.ValueKind == JsonValueKind.Null)
        { errs.Add(new("country_code", "required")); return null; }
        if (v.ValueKind != JsonValueKind.String) { errs.Add(new("country_code", "type")); return null; }
        var t = v.GetString()!.Trim().ToUpperInvariant();
        if (t.Length != 2 || t.Any(ch => ch is < 'A' or > 'Z')) { errs.Add(new("country_code", "country_code_invalid")); return null; }
        return t;
    }

    static string? Zone(JsonElement o, List<FieldError> errs)
    {
        if (!o.TryGetProperty("time_zone_id", out var v) || v.ValueKind == JsonValueKind.Null)
        { errs.Add(new("time_zone_id", "required")); return null; }
        if (v.ValueKind != JsonValueKind.String) { errs.Add(new("time_zone_id", "type")); return null; }
        var t = v.GetString()!.Trim();
        if (t.Length == 0) { errs.Add(new("time_zone_id", "required")); return null; }
        if (t.EnumerateRunes().Count() > ZoneMax) { errs.Add(new("time_zone_id", "time_zone_unknown")); return null; }
        return t;
    }

    static string? Email(JsonElement o, List<FieldError> errs)
    {
        if (!o.TryGetProperty("email", out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) { errs.Add(new("email", "type")); return null; }
        var t = v.GetString()!.Trim();
        if (t.Length == 0) return null;
        var at = t.IndexOf('@');
        if (t.EnumerateRunes().Count() > EmailMax || at <= 0 || at != t.LastIndexOf('@') || at == t.Length - 1 || t.Any(char.IsWhiteSpace))
        { errs.Add(new("email", "email_invalid")); return null; }
        return t;
    }

    static bool? Flag(JsonElement o, List<FieldError> errs, string key)
    {
        if (!o.TryGetProperty(key, out var v)) { errs.Add(new(key, "required")); return null; }
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { errs.Add(new(key, "type")); return null; }
        return v.GetBoolean();
    }

    static string? Phone(JsonElement o, List<FieldError> errs, string? region)
    {
        if (!o.TryGetProperty("phone", out var v) || v.ValueKind == JsonValueKind.Null)
        { errs.Add(new("phone", "required")); return null; }
        if (v.ValueKind != JsonValueKind.String) { errs.Add(new("phone", "type")); return null; }
        var raw = v.GetString()!.Trim();
        if (raw.Length == 0) { errs.Add(new("phone", "required")); return null; }
        if (raw.EnumerateRunes().Count() > PhoneInputMax) { errs.Add(new("phone", "phone_invalid")); return null; }
        try
        {
            var util = PhoneNumberUtil.GetInstance();
            var parsed = util.Parse(raw, region);
            if (!util.IsValidNumber(parsed)) { errs.Add(new("phone", "phone_invalid")); return null; }
            return util.Format(parsed, PhoneNumberFormat.E164);
        }
        catch (NumberParseException) { errs.Add(new("phone", "phone_invalid")); return null; }
    }

    static Guid? ClientId(JsonElement o, List<FieldError> errs)
    {
        if (!o.TryGetProperty("client_id", out var v) || v.ValueKind == JsonValueKind.Null)
        { errs.Add(new("client_id", "required")); return null; }
        if (!LoginConfigValidator.TryUuid(v, out var g) || g == Guid.Empty) { errs.Add(new("client_id", "client_id_format")); return null; }
        return g;
    }
}
