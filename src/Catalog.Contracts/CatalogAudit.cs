namespace Catalog.Contracts;

/// <summary>
/// The frozen source audit schema accepts auth events and a numeric schema_version only.
/// This slice must not emit a catalog envelope and must not emit the unapproved sentinel.
/// </summary>
public static class FrozenAuditSchemaBoundary
{
    public const string RejectedSentinel = "1.2.0-catalog-unapproved";
    public const string DurableAcceptedNotClaimed = "DURABLY_ACCEPTED";

    public static void RejectEmission(string? schemaVersion)
    {
        if (schemaVersion is not null)
            throw new CatalogSchemaEmissionRejectedException(
                "A catalog double cannot emit schema_version. The unapproved sentinel is rejected, and no numeric stand-in is emitted either.");
    }

    public static void RejectDurableClaim(string? status)
    {
        if (string.Equals(status, DurableAcceptedNotClaimed, StringComparison.Ordinal))
            throw new CatalogSchemaEmissionRejectedException(
                DurableAcceptedNotClaimed + " is a write-enable status. An in-memory double cannot claim it.");
    }
}

public sealed class CatalogSchemaEmissionRejectedException(string message) : InvalidOperationException(message);

public static class ProposedCatalogEvents
{
    public const string Created = "catalog.entry.created";
    public const string Updated = "catalog.entry.updated";
    public const string EntryActivation = "catalog.entry.activation_changed";
    public const string ScopeActivation = "catalog.scope.activation_changed";
    public const string Denied = "catalog.activation.denied";

    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Updated, EntryActivation, ScopeActivation, Denied,
    };

    /// <summary>Only create and claim-edit intents are recorded. Activation and denial names are not emitted.</summary>
    public static readonly IReadOnlySet<string> RecordedInThisSlice = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Updated,
    };
}

public static class AuditAllowlist
{
    public static readonly string[] IntentKeys =
    [
        "event_id",
        "change_id",
        "entry_id",
        "proposed_event_name",
        "emitted",
        "proof_boundary",
        "actor_user_id",
        "acting_role",
        "actor_partner_id",
        "target_family",
        "target_scope",
        "entry_version",
        "changed_fields",
        "before",
        "after",
        "reason",
        "correlation_id",
        "source_request_id",
        "occurred_at",
        "intent_status",
    ];

    public static readonly string[] FeeValueKeys = ["code", "display_name", "description"];

    public static readonly string[] ClaimEditValueKeys =
    [
        "claim_value", "availability", "source_url", "checked_at", "display_name", "description",
    ];

    public const string IntentStatus = "double_recorded_not_durable";
}

/// <summary>
/// Existing forms Super Admin audit gap. This catalog slice does not fix it and does not waive it.
/// </summary>
public static class FormsSaAuditGap
{
    public const string Disposition = "open_not_fixed_not_waived";
    public const string Note = "The forms Super Admin audit gap stays open. Catalog doubles do not edit the auth event source.";
}

public static class ProofBoundaryNames
{
    public const string InMemoryDouble = "in_memory_double_not_db_runtime_or_relay";
}

/// <summary>
/// Allowlisted audit intent recorded only inside the double. It is not a source-schema envelope,
/// not an outbox row, and not evidence of relay delivery.
/// </summary>
public sealed class AuditIntent
{
    private AuditIntent(IReadOnlyDictionary<string, string?> fields, IReadOnlyDictionary<string, string?>? before, IReadOnlyDictionary<string, string?> after)
    {
        Fields = fields;
        Before = before;
        After = after;
    }

    public IReadOnlyDictionary<string, string?> Fields { get; }
    public IReadOnlyDictionary<string, string?>? Before { get; }
    public IReadOnlyDictionary<string, string?> After { get; }

    public string ProposedEventName => Fields["proposed_event_name"]!;
    public string IntentStatus => Fields["intent_status"]!;
    public Guid EventId => Guid.Parse(Fields["event_id"]!);
    public Guid ChangeId => Guid.Parse(Fields["change_id"]!);
    public Guid EntryId => Guid.Parse(Fields["entry_id"]!);

    public static AuditIntent Create(
        Guid eventId,
        Guid changeId,
        Guid entryId,
        string proposedEventName,
        CatalogPrincipal actor,
        string targetFamily,
        string targetScope,
        long entryVersion,
        IReadOnlyList<string> changedFields,
        IReadOnlyDictionary<string, string?>? before,
        IReadOnlyDictionary<string, string?> after,
        string? reason,
        string correlationId,
        string sourceRequestId,
        DateTimeOffset occurredAt,
        IReadOnlyCollection<string> valueAllowlist,
        string? schemaVersion = null,
        string? statusOverride = null)
    {
        FrozenAuditSchemaBoundary.RejectEmission(schemaVersion);
        FrozenAuditSchemaBoundary.RejectDurableClaim(statusOverride);
        if (!ProposedCatalogEvents.RecordedInThisSlice.Contains(proposedEventName))
            throw new CatalogSchemaEmissionRejectedException("That catalog event name is not recorded by this double.");
        if (schemaVersion is not null || (statusOverride is not null && statusOverride != AuditAllowlist.IntentStatus))
            throw new CatalogSchemaEmissionRejectedException("Catalog intent status and schema_version cannot be overridden.");

        RejectValueMap(before, valueAllowlist);
        RejectValueMap(after, valueAllowlist);
        foreach (var field in changedFields)
        {
            if (!valueAllowlist.Contains(field, StringComparer.Ordinal))
                throw new CatalogSchemaEmissionRejectedException("Changed field is outside the audit allowlist.");
        }

        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["event_id"] = eventId.ToString("D"),
            ["change_id"] = changeId.ToString("D"),
            ["entry_id"] = entryId.ToString("D"),
            ["proposed_event_name"] = proposedEventName,
            ["emitted"] = "false",
            ["proof_boundary"] = ProofBoundaryNames.InMemoryDouble,
            ["actor_user_id"] = actor.UserId.ToString("D"),
            ["acting_role"] = RoleWire(actor.Role),
            ["actor_partner_id"] = actor.PartnerId?.ToString("D"),
            ["target_family"] = targetFamily,
            ["target_scope"] = targetScope,
            ["entry_version"] = entryVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["changed_fields"] = string.Join(",", changedFields),
            ["before"] = before is null ? null : "allowlisted",
            ["after"] = "allowlisted",
            ["reason"] = reason,
            ["correlation_id"] = correlationId,
            ["source_request_id"] = sourceRequestId,
            ["occurred_at"] = occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["intent_status"] = AuditAllowlist.IntentStatus,
        };

        if (fields.Keys.Count != AuditAllowlist.IntentKeys.Length || AuditAllowlist.IntentKeys.Any(key => !fields.ContainsKey(key)))
            throw new InvalidOperationException("Audit intent keys drifted from the allowlist.");
        RejectSentinelText(fields.Values);
        RejectSentinelText(before?.Values);
        RejectSentinelText(after.Values);
        if (fields["emitted"] != "false" || fields["intent_status"] != AuditAllowlist.IntentStatus)
            throw new InvalidOperationException("Audit intent tried to claim emission or durable acceptance.");

        return new AuditIntent(fields, before, after);
    }

    private static void RejectValueMap(IReadOnlyDictionary<string, string?>? map, IReadOnlyCollection<string> allowlist)
    {
        if (map is null)
            return;
        foreach (var key in map.Keys)
        {
            if (!allowlist.Contains(key, StringComparer.Ordinal))
                throw new CatalogSchemaEmissionRejectedException("Before/after key is outside the audit allowlist.");
        }
    }

    private static void RejectSentinelText(IEnumerable<string?>? values)
    {
        if (values is null)
            return;
        foreach (var value in values)
        {
            if (value is not null && value.Contains(FrozenAuditSchemaBoundary.RejectedSentinel, StringComparison.Ordinal))
                throw new CatalogSchemaEmissionRejectedException("The unapproved schema_version sentinel cannot be stored.");
        }
    }

    private static string RoleWire(CatalogRole role) => role switch
    {
        CatalogRole.SuperAdmin => "super_admin",
        CatalogRole.PartnerAdmin => "partner_admin",
        _ => "no_management_grant",
    };
}
