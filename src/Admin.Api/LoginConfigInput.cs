using System.Text.Json;
using System.Text.RegularExpressions;

namespace Admin.Api;

public sealed record FieldError(string Field, string Code);

/// <summary>The current stored row, as the validator needs it (null on create).</summary>
public sealed record CurrentConfig(string ProviderKind, Guid[] Tenants, bool AllowDbFallback, bool MfaRequired);

/// <summary>A validated login_config_write. Values are already normalised (trimmed, lower-case uuids).</summary>
public sealed record LoginConfigInput(
    string ProviderKind, string DisplayName, Guid[] Tenants,
    bool AllowDbFallback, bool MfaRequired, string? Reason, IReadOnlyList<string> PresentConfigFields);

public sealed record CreateInput(Guid PartnerId, string Name, LoginConfigInput Config, SetupValidator.Draft Setup);

/// <summary>
/// The reason-length bounds in force for one partner (Slice A, TLD 08:00 Q3): the partner's auth.partner_reason_bounds row when
/// present, else the deployment config LoginConfig:ReasonMinLength / ReasonMaxLength (startup snapshot; ships 10/500, DEC-115).
/// Always inside 10..500 (DB CHECK prb_bounds / llcc_reason; config checked at startup, TLD 08:00b(2)).
/// </summary>
public readonly record struct ReasonBounds(int Min, int Max)
{
    public static ReasonBounds FromConfig(AdminApiOptions opt) => new(opt.ReasonMinLength, opt.ReasonMaxLength);
}

/// <summary>
/// §5.2 field validation, ALL errors collected, deterministic order. Never copies a submitted VALUE into an error
/// (field names only; unknown keys are named only when they look like plain identifiers).
/// </summary>
public static class LoginConfigValidator
{
    public static readonly string[] WriteKeys = ["provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required", "reason"];
    /// <summary>
    /// Server-filled from deployment config; any of these keys in a client body (any value, null included) -> 422 field_not_writable,
    /// field NAME only, the value is never echoed. secret_ref joined oidc_* at TLD 2026-11-04 9:49 (D-51: one app registration per deployment).
    /// </summary>
    public static readonly string[] ServerFilled = ["oidc_authority", "oidc_client_id", "secret_ref"];
    /// <summary>login_config_field names (audit-events.schema.json) a body may carry; used for attempted_fields.</summary>
    static readonly string[] AuditFieldNames = ["provider_kind", "display_name", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref", "allow_db_fallback", "mfa_required"];
    static readonly Regex UuidRe = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", RegexOptions.CultureInvariant);
    static readonly Regex TrimmedRe = new(@"^\S(?:.*\S)?\z", RegexOptions.CultureInvariant);
    static readonly Regex SafeKeyRe = new(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.CultureInvariant);

    public static bool TryUuid(JsonElement e, out Guid g)
    {
        g = default;
        return e.ValueKind == JsonValueKind.String && e.GetString() is { } s && UuidRe.IsMatch(s) && Guid.TryParse(s, out g);
    }

    static int Runes(string s) => s.EnumerateRunes().Count();

    /// <summary>Body keys that are login_config_field names (names only), for attempted_fields of a denied event.</summary>
    public static List<string> AttemptedFields(JsonElement? obj)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o) return [];
        var keys = o.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return AuditFieldNames.Where(keys.Contains).ToList();
    }

    internal static string KeyName(string prefix, string key) => prefix + (SafeKeyRe.IsMatch(key) ? key : "*");

    public static LoginConfigInput? ValidateWrite(JsonElement o, string prefix, CurrentConfig? current, ReasonBounds bounds, List<FieldError> errs, out Guid[] boundCandidates)
    {
        int before = errs.Count;
        boundCandidates = [];
        foreach (var p in o.EnumerateObject())
        {
            if (WriteKeys.Contains(p.Name)) continue;
            if (ServerFilled.Contains(p.Name)) errs.Add(new(prefix + p.Name, "field_not_writable"));
            else errs.Add(new(KeyName(prefix, p.Name), "unknown_field"));
        }
        JsonElement? Get(string k) => o.TryGetProperty(k, out var v) ? v : null;
        void Req(string k) { if (Get(k) is null) errs.Add(new(prefix + k, "required")); }
        foreach (var k in new[] { "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required" }) Req(k);

        string? kind = null;
        if (Get("provider_kind") is { } pk)
        {
            if (pk.ValueKind != JsonValueKind.String) errs.Add(new(prefix + "provider_kind", "type"));
            else if (pk.GetString() is "database" or "entra") kind = pk.GetString();
            else errs.Add(new(prefix + "provider_kind", "provider_kind_invalid"));
        }

        string? display = null;
        if (Get("display_name") is { } dn)
        {
            if (dn.ValueKind != JsonValueKind.String) errs.Add(new(prefix + "display_name", "type"));
            else
            {
                var t = dn.GetString()!.Trim();
                if (Runes(t) is < 1 or > 100 || !TrimmedRe.IsMatch(t)) errs.Add(new(prefix + "display_name", "display_name_invalid"));
                else display = t;
            }
        }

        var tenants = new List<Guid>();
        bool tenantsOk = false;
        if (Get("entra_allowed_tenant_ids") is { } ta)
        {
            if (ta.ValueKind != JsonValueKind.Array) errs.Add(new(prefix + "entra_allowed_tenant_ids", "type"));
            else
            {
                tenantsOk = true;
                int i = 0;
                foreach (var e in ta.EnumerateArray())
                {
                    var f = $"{prefix}entra_allowed_tenant_ids/{i++}";
                    if (e.ValueKind != JsonValueKind.String) { errs.Add(new(f, "type")); tenantsOk = false; }
                    else if (!TryUuid(e, out var g) || g == Guid.Empty) { errs.Add(new(f, "tenant_id_format")); tenantsOk = false; }
                    else tenants.Add(g);
                }
                if (kind == "entra" && i != 1) { errs.Add(new(prefix + "entra_allowed_tenant_ids", "tenant_count")); tenantsOk = false; }
                if (kind == "database" && i != 0) { errs.Add(new(prefix + "entra_allowed_tenant_ids", "field_not_allowed")); tenantsOk = false; }
            }
        }

        bool? fallback = null, mfa = null;
        if (Get("allow_db_fallback") is { } fb)
        {
            if (fb.ValueKind is JsonValueKind.True or JsonValueKind.False) fallback = fb.GetBoolean();
            else errs.Add(new(prefix + "allow_db_fallback", "type"));
            if (kind == "database" && fallback == true) errs.Add(new(prefix + "allow_db_fallback", "fallback_not_applicable"));
        }
        if (Get("mfa_required") is { } mr)
        {
            if (mr.ValueKind is JsonValueKind.True or JsonValueKind.False) mfa = mr.GetBoolean();
            else errs.Add(new(prefix + "mfa_required", "type"));
        }

        string? reason = null;
        if (Get("reason") is { } rs && rs.ValueKind != JsonValueKind.Null)
        {
            if (rs.ValueKind != JsonValueKind.String) errs.Add(new(prefix + "reason", "type"));
            else
            {
                var raw = rs.GetString()!;
                var t = raw.Trim();
                // DEC-107 + Slice A (TLD 08:00 Q3): the location's partner's bounds (partner row, else LoginConfig:*), inside the DB's 10..500.
                if (Runes(raw) > 2000 || (t.Length > 0 && (Runes(t) < bounds.Min || Runes(t) > bounds.Max))) errs.Add(new(prefix + "reason", "reason_length"));
                else if (t.Length > 0) reason = t;
            }
        }

        // tenant_bound_elsewhere needs the DB; the caller checks these (well-formed, exactly one, entra) alongside the rest.
        if (kind == "entra" && tenantsOk) boundCandidates = tenants.ToArray();
        if (errs.Count > before) return null;
        var present = AttemptedFields(o);
        return new LoginConfigInput(kind!, display!, tenants.ToArray(), fallback!.Value, mfa!.Value, reason, present);
    }

    public static CreateInput? ValidateCreate(JsonElement o, ReasonBounds bounds, List<FieldError> errs, out Guid[] boundCandidates, out SetupValidator.Draft setup)
    {
        boundCandidates = [];
        int before = errs.Count;
        foreach (var p in o.EnumerateObject())
            if (p.Name is not ("partner_id" or "name" or "login_config" or "client_id" or "address1" or "address2" or "city" or "region"
                or "country_code" or "postal_code" or "region_unused_confirmed" or "postal_unused_confirmed" or "phone" or "email" or "time_zone_id"))
                errs.Add(new(KeyName("", p.Name), "unknown_field"));
        Guid partner = default; string? name = null; LoginConfigInput? cfg = null;
        setup = SetupValidator.Parse(o, errs, requireClientId: true);
        if (!o.TryGetProperty("partner_id", out var pid)) errs.Add(new("partner_id", "required"));
        else if (pid.ValueKind != JsonValueKind.String) errs.Add(new("partner_id", "type"));
        else if (!TryUuid(pid, out partner) || partner == Guid.Empty) errs.Add(new("partner_id", "partner_id_format"));
        if (!o.TryGetProperty("name", out var nm)) errs.Add(new("name", "required"));
        else if (nm.ValueKind != JsonValueKind.String) errs.Add(new("name", "type"));
        else
        {
            var t = nm.GetString()!.Trim();
            if (Runes(t) is < 1 or > 200 || !TrimmedRe.IsMatch(t)) errs.Add(new("name", "name_invalid"));
            else name = t;
        }
        if (!o.TryGetProperty("login_config", out var lc)) errs.Add(new("login_config", "required"));
        else if (lc.ValueKind != JsonValueKind.Object) errs.Add(new("login_config", "type"));
        else cfg = ValidateWrite(lc, "login_config/", null, bounds, errs, out boundCandidates);
        return errs.Count > before || cfg is null ? null : new CreateInput(partner, name!, cfg, setup);
    }

    /// <summary>
    /// PUT /api/orgs/partners/{partner_id}/login-config/reason-bounds body (Slice A): exactly {reason_min_length, reason_max_length},
    /// both JSON integers. Each outside 10..500 = reason_bound_range; both in range but min &gt; max = reason_bound_order on
    /// reason_min_length [DEC-114: hard 10 &lt;= min &lt;= max &lt;= 500, = DB CHECK prb_bounds]. All errors collected; values never echoed.
    /// </summary>
    public static ReasonBounds? ValidateBounds(JsonElement o, List<FieldError> errs)
    {
        int before = errs.Count;
        foreach (var p in o.EnumerateObject())
            if (p.Name is not ("reason_min_length" or "reason_max_length")) errs.Add(new(KeyName("", p.Name), "unknown_field"));
        int? Num(string k)
        {
            if (!o.TryGetProperty(k, out var v)) { errs.Add(new(k, "required")); return null; }
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) { errs.Add(new(k, "type")); return null; }
            if (n < AdminApiOptions.ReasonFloor || n > AdminApiOptions.ReasonCeiling) { errs.Add(new(k, "reason_bound_range")); return null; }
            return n;
        }
        var min = Num("reason_min_length");
        var max = Num("reason_max_length");
        if (min is { } a && max is { } b && a > b) errs.Add(new("reason_min_length", "reason_bound_order"));
        return errs.Count > before ? null : new ReasonBounds(min!.Value, max!.Value);
    }

    /// <summary>Partner id from a create body when it is well formed (scope check, §4 step 3-4 on create).</summary>
    public static Guid? BodyPartner(JsonElement? o) =>
        o is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("partner_id", out var p) && TryUuid(p, out var g) && g != Guid.Empty ? g : null;

    /// <summary>Rejects duplicate keys anywhere (a duplicate could make two readers see two different values).</summary>
    public static bool HasDuplicateKeys(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject()) { if (!seen.Add(p.Name) || HasDuplicateKeys(p.Value)) return true; }
                return false;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) if (HasDuplicateKeys(x)) return true;
                return false;
            default: return false;
        }
    }
}
