using System.Globalization;
using System.Text.Json;

namespace Catalog.Contracts;

public abstract record CatalogCommand
{
    public sealed record CreateFee(string Code, string DisplayName, string? Description, IReadOnlyDictionary<string, string?> Body) : CatalogCommand;

    public sealed record CreateUnavailable(CatalogFamily Family, string Detail) : CatalogCommand;

    public sealed record EditClaim(
        Guid EntryId,
        IReadOnlyDictionary<string, string> Fields,
        VersionToken ExpectedEntryVersion,
        IReadOnlyDictionary<string, string?> Body) : CatalogCommand;

    public sealed record ActivateEntry(
        CatalogFamily Family,
        Guid EntryId,
        bool DesiredActive,
        string Reason,
        VersionToken Expected) : CatalogCommand;

    public sealed record ActivatePartner(
        CatalogFamily Family,
        Guid EntryId,
        string PartnerIdText,
        bool DesiredActive,
        string Reason,
        VersionToken Expected) : CatalogCommand;

    public sealed record ActivateLocation(
        CatalogFamily Family,
        Guid EntryId,
        string LocationIdText,
        bool DesiredActive,
        string Reason,
        VersionToken Expected) : CatalogCommand;

    public sealed record Rejected(ProposedStatus Status, string Code, string Detail) : CatalogCommand;
}

public static class CatalogCommandParser
{
    public const int DoubleInputCap = 4000;
    public const string PartnerTargetSyntaxOnly = "syntax_only_not_membership_or_existence";

    private static readonly string[] FeeKeys = ["code", "display_name", "description"];
    private static readonly string[] ClaimKeys = AuditAllowlist.ClaimEditValueKeys;
    private static readonly string[] ActivationKeys = ["desired_active", "reason", "expected_version"];

    private static readonly HashSet<string> AuthorityFields = new(StringComparer.Ordinal)
    {
        "actor", "actor_user_id", "user_id", "admin_type", "on_behalf_of", "impersonate",
        "delegated_actor", "owner_partner_id", "owner", "acting_as", "partner_header",
        "schema_version", "idempotency_key", "correlation_id", "source_request_id",
    };

    private static readonly HashSet<string> ProtectedClaimFields = new(StringComparer.Ordinal)
    {
        "entry_id", "claim_id", "id", "family", "entry_class", "scope_kind", "code",
        "owner_partner_id", "model_id", "capability_entry_id", "option_key", "source_version",
        "is_active", "row_version", "entry_version", "created_at", "updated_at",
        "actor_user_id", "admin_type", "economic_role", "classification",
    };

    public static CatalogCommand ParseCreate(string? familyWire, string json)
    {
        if (!CatalogFamilies.TryParse(familyWire, out var family))
            return Reject(ProposedStatus.BadRequest, "invalid_family", "invalid_family");
        if (!FlatJson.TryRead(json, out var fields, out var error))
            return Reject(ProposedStatus.BadRequest, "invalid_dto", error!);
        if (ContainsSentinel(fields))
            return Reject(ProposedStatus.BadRequest, "schema_version_rejected", "schema_version_rejected");

        var authority = First(fields, AuthorityFields);
        if (authority is not null)
            return Reject(ProposedStatus.BadRequest, "authority_field_rejected", authority);

        var disposition = CatalogFamilies.Disposition(family);
        if (disposition != CreateFieldDisposition.NamesOnlyFee)
        {
            var detail = disposition == CreateFieldDisposition.UnresolvedRequiredFields
                ? "required_fields_unresolved"
                : "create_fields_not_closed";
            return new CatalogCommand.CreateUnavailable(family, detail);
        }

        var unknown = fields.Keys.FirstOrDefault(key => !FeeKeys.Contains(key, StringComparer.Ordinal));
        if (unknown is not null)
            return Reject(ProposedStatus.BadRequest, "unknown_field", unknown);
        if (!fields.ContainsKey("code"))
            return Reject(ProposedStatus.BadRequest, "code_required", "code");
        if (!fields.ContainsKey("display_name"))
            return Reject(ProposedStatus.BadRequest, "display_name_required", "display_name");
        if (!TryBounded(fields, "code", required: true, max: 64, allowInternalSpace: false, out var code, out var codeError))
            return Reject(ProposedStatus.BadRequest, codeError!, "code");
        if (!TryBounded(fields, "display_name", required: true, max: 200, allowInternalSpace: true, out var display, out var displayError))
            return Reject(ProposedStatus.BadRequest, displayError!, "display_name");
        if (!TryBounded(fields, "description", required: false, max: 2000, allowInternalSpace: true, out var description, out var descriptionError))
            return Reject(ProposedStatus.BadRequest, descriptionError!, "description");
        if (SensitiveText.ContainsDisallowed(code) || SensitiveText.ContainsDisallowed(display) || SensitiveText.ContainsDisallowed(description))
            return Reject(ProposedStatus.BadRequest, "sensitive_value_rejected", "sensitive_value_rejected");

        var body = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["code"] = code,
            ["display_name"] = display,
        };
        if (fields.ContainsKey("description"))
            body["description"] = description;
        return new CatalogCommand.CreateFee(code!, display!, description, body);
    }

    public static CatalogCommand ParseClaimEdit(string? familyWire, Guid entryId, string json)
    {
        if (!CatalogFamilies.TryParse(familyWire, out var family) || family != CatalogFamily.ModelHardwareClaim)
            return Reject(ProposedStatus.BadRequest, "invalid_family", "claim_edit_family");
        if (!FlatJson.TryRead(json, out var fields, out var error))
            return Reject(ProposedStatus.BadRequest, "invalid_dto", error!);
        if (ContainsSentinel(fields))
            return Reject(ProposedStatus.BadRequest, "schema_version_rejected", "schema_version_rejected");
        if (fields.Keys.Any(ProtectedClaimFields.Contains))
            return Reject(ProposedStatus.Unprocessable, "catalog_column_matrix_unsettled", "protected_field");
        var authority = First(fields, AuthorityFields);
        if (authority is not null)
            return Reject(ProposedStatus.BadRequest, "authority_field_rejected", authority);
        var unknown = fields.Keys.FirstOrDefault(key => key != "expected_version" && !ClaimKeys.Contains(key, StringComparer.Ordinal));
        if (unknown is not null)
            return Reject(ProposedStatus.BadRequest, "unknown_field", unknown);
        if (!fields.TryGetValue("expected_version", out var versionElement))
            return Reject(ProposedStatus.PreconditionRequired, "catalog_precondition_required", "expected_version");
        if (!VersionToken.TryParseJson(versionElement, out var version, out _))
            return Reject(ProposedStatus.BadRequest, "version_domain", "expected_version");
        if (version.IsAbsent)
            return Reject(ProposedStatus.BadRequest, "version_domain", "entry_version_absent");

        var edits = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ClaimKeys)
        {
            if (!fields.TryGetValue(key, out var element))
                continue;
            if (element.ValueKind != JsonValueKind.String)
                return Reject(ProposedStatus.BadRequest, "invalid_field", key);
            var text = element.GetString() ?? "";
            if (!ValidClaimValue(key, text))
                return Reject(ProposedStatus.BadRequest, "invalid_field", key);
            if (SensitiveText.ContainsDisallowed(text))
                return Reject(ProposedStatus.BadRequest, "sensitive_value_rejected", "sensitive_value_rejected");
            edits[key] = key == "checked_at" ? NormalizeInstant(text) : text;
        }
        if (edits.Count == 0)
            return Reject(ProposedStatus.BadRequest, "empty_edit", "claim_fields");

        var body = edits.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        body["expected_version"] = version.ToString();
        return new CatalogCommand.EditClaim(entryId, edits, version, body);
    }

    public static CatalogCommand ParseEntryActivation(string? familyWire, Guid entryId, string json) =>
        ParseActivation(familyWire, json, (family, desired, reason, expected) =>
            new CatalogCommand.ActivateEntry(family, entryId, desired, reason, expected));

    public static CatalogCommand ParsePartnerActivation(string? familyWire, Guid entryId, string? partnerIdText, string json)
    {
        if (string.IsNullOrWhiteSpace(partnerIdText))
            return Reject(ProposedStatus.BadRequest, "invalid_partner_uuid", "partner_id");
        return ParseActivation(familyWire, json, (family, desired, reason, expected) =>
            new CatalogCommand.ActivatePartner(family, entryId, partnerIdText, desired, reason, expected));
    }

    public static CatalogCommand ParseLocationActivation(string? familyWire, Guid entryId, string? locationIdText, string json)
    {
        if (!Guid.TryParseExact(locationIdText, "D", out var location) || location == Guid.Empty)
            return Reject(ProposedStatus.BadRequest, "invalid_location_uuid", "location_id");
        return ParseActivation(familyWire, json, (family, desired, reason, expected) =>
            new CatalogCommand.ActivateLocation(family, entryId, locationIdText!, desired, reason, expected));
    }

    private static CatalogCommand ParseActivation(
        string? familyWire,
        string json,
        Func<CatalogFamily, bool, string, VersionToken, CatalogCommand> build)
    {
        if (!CatalogFamilies.TryParse(familyWire, out var family))
            return Reject(ProposedStatus.BadRequest, "invalid_family", "invalid_family");
        if (!FlatJson.TryRead(json, out var fields, out var error))
            return Reject(ProposedStatus.BadRequest, "invalid_dto", error!);
        if (ContainsSentinel(fields))
            return Reject(ProposedStatus.BadRequest, "schema_version_rejected", "schema_version_rejected");
        if (fields.Keys.Any(ProtectedClaimFields.Contains) || First(fields, AuthorityFields) is not null)
            return Reject(ProposedStatus.BadRequest, "authority_field_rejected", "closed_dto");
        var unknown = fields.Keys.FirstOrDefault(key => !ActivationKeys.Contains(key, StringComparer.Ordinal));
        if (unknown is not null)
            return Reject(ProposedStatus.BadRequest, "unknown_field", unknown);
        if (!fields.TryGetValue("reason", out var reasonElement) || reasonElement.ValueKind != JsonValueKind.String)
            return Reject(ProposedStatus.Unprocessable, "catalog_reason_required", "reason");
        var reason = reasonElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(reason))
            return Reject(ProposedStatus.Unprocessable, "catalog_reason_required", "reason");
        if (reason.Length > DoubleInputCap)
            return Reject(ProposedStatus.BadRequest, "double_input_cap", "double_input_cap_not_owner_reason_policy");
        if (SensitiveText.ContainsDisallowed(reason))
            return Reject(ProposedStatus.BadRequest, "sensitive_value_rejected", "sensitive_value_rejected");
        if (!fields.TryGetValue("desired_active", out var desiredElement) || desiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Reject(ProposedStatus.BadRequest, "invalid_field", "desired_active");
        if (!fields.TryGetValue("expected_version", out var versionElement))
            return Reject(ProposedStatus.PreconditionRequired, "catalog_precondition_required", "expected_version");
        if (!VersionToken.TryParseJson(versionElement, out var version, out _))
            return Reject(ProposedStatus.BadRequest, "version_domain", "expected_version");
        return build(family, desiredElement.GetBoolean(), reason, version);
    }

    private static bool ValidClaimValue(string key, string text) => key switch
    {
        "checked_at" => HasExplicitOffset(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
        "availability" => text.Length is > 0 and <= 64 && !text.Any(char.IsWhiteSpace),
        "claim_value" or "description" => text.Length <= 2000 && !text.Any(char.IsControl),
        "display_name" => text.Length is > 0 and <= 200 && !text.Any(char.IsControl),
        "source_url" => text.Length is > 0 and <= 2000 && !text.Any(char.IsWhiteSpace)
            && !text.Contains("token=", StringComparison.OrdinalIgnoreCase) && !text.Contains('@'),
        _ => false,
    };

    private static bool HasExplicitOffset(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
        && (text.EndsWith('Z') || text.Contains('+', StringComparison.Ordinal) || text.LastIndexOf('-') > 9);

    private static string NormalizeInstant(string text)
    {
        var instant = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static bool TryBounded(
        IReadOnlyDictionary<string, JsonElement> fields,
        string key,
        bool required,
        int max,
        bool allowInternalSpace,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        if (!fields.TryGetValue(key, out var element))
        {
            if (!required)
                return true;
            error = key + "_required";
            return false;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            error = "invalid_field";
            return false;
        }
        var text = element.GetString() ?? "";
        if (text.Length == 0 || text.Length > max || text.Any(char.IsControl) || (!allowInternalSpace && text.Any(char.IsWhiteSpace)) || (allowInternalSpace && string.IsNullOrWhiteSpace(text)))
        {
            error = "invalid_field";
            return false;
        }
        value = text;
        return true;
    }

    private static string? First(IReadOnlyDictionary<string, JsonElement> fields, HashSet<string> names)
    {
        foreach (var key in fields.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (names.Contains(key))
                return key;
        }
        return null;
    }

    private static bool ContainsSentinel(IReadOnlyDictionary<string, JsonElement> fields) =>
        fields.Values.Any(element => element.ValueKind == JsonValueKind.String
            && (element.GetString() ?? "").Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal));

    private static CatalogCommand.Rejected Reject(ProposedStatus status, string code, string detail) => new(status, code, detail);
}

internal static class FlatJson
{
    public static bool TryRead(string json, out Dictionary<string, JsonElement> fields, out string? error)
    {
        fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        error = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "invalid_json";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "invalid_json";
                return false;
            }
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, property.Value.Clone()))
                {
                    error = "duplicate_field";
                    fields.Clear();
                    return false;
                }
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    error = "invalid_field";
                    fields.Clear();
                    return false;
                }
            }
        }

        if (HasDuplicateRawKey(json))
        {
            error = "duplicate_field";
            fields.Clear();
            return false;
        }
        return true;
    }

    private static bool HasDuplicateRawKey(string json)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        Skip(json, ref index);
        if (index >= json.Length || json[index] != '{')
            return false;
        index++;
        while (true)
        {
            Skip(json, ref index);
            if (index < json.Length && json[index] == '}')
                return false;
            if (index >= json.Length || json[index] != '"')
                return false;
            var key = ReadString(json, ref index);
            if (!seen.Add(key))
                return true;
            Skip(json, ref index);
            if (index >= json.Length || json[index] != ':')
                return false;
            index++;
            if (!SkipValue(json, ref index))
                return false;
            Skip(json, ref index);
            if (index < json.Length && json[index] == ',')
            {
                index++;
                continue;
            }
            return false;
        }
    }

    private static void Skip(string json, ref int index)
    {
        while (index < json.Length && char.IsWhiteSpace(json[index]))
            index++;
    }

    private static string ReadString(string json, ref int index)
    {
        index++;
        var start = index;
        while (index < json.Length && json[index] != '"')
        {
            if (json[index] == '\\')
                index++;
            index++;
        }
        var text = json[start..index];
        if (index < json.Length)
            index++;
        return text;
    }

    private static bool SkipValue(string json, ref int index)
    {
        Skip(json, ref index);
        if (index >= json.Length)
            return false;
        var mark = json[index];
        if (mark == '"')
        {
            ReadString(json, ref index);
            return true;
        }
        if (mark is '{' or '[')
            return false;
        while (index < json.Length && json[index] is not (',' or '}'))
            index++;
        return true;
    }
}

internal static class SensitiveText
{
    private static readonly string[] Tokens = ["pan", "pin", "cvv", "micr", "secret", "password", "credential"];

    public static bool ContainsDisallowed(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (value.Contains("token=", StringComparison.OrdinalIgnoreCase))
            return true;
        var digits = new string(value.Where(ch => ch is >= '0' and <= '9').ToArray());
        var separatorsOnly = value.All(ch => char.IsDigit(ch) || ch is ' ' or '-');
        if (separatorsOnly && digits.Length is >= 13 and <= 19)
            return true;
        var lower = value.ToLowerInvariant();
        return Tokens.Any(token => HasBoundedToken(lower, token));
    }

    private static bool HasBoundedToken(string lower, string token)
    {
        var index = 0;
        while ((index = lower.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !char.IsLetter(lower[index - 1]);
            var afterIndex = index + token.Length;
            var after = afterIndex == lower.Length || !char.IsLetter(lower[afterIndex]);
            if (before && after)
                return true;
            index++;
        }
        return false;
    }
}
