using System.Globalization;
using Catalog.Contracts;

namespace Catalog.Transport;

internal static class ResponseJson
{
    public static string Transport(string code, string detail)
    {
        var json = new JsonText();
        json.BeginObject();
        json.Property("code", code);
        json.Property("detail", detail);
        json.Property("wrote", false);
        json.Property("proof_boundary", ProofBoundaryNames.InMemoryDouble);
        json.Property("mapping", CatalogHttpMapping.Name);
        json.EndObject();
        return json.Text;
    }

    public static string Outcome(CatalogOutcome outcome)
    {
        RejectUnemittable(outcome);
        var json = new JsonText();
        json.BeginObject();
        json.Property("code", outcome.Code);
        if (outcome.Detail is not null)
            json.Property("detail", outcome.Detail);
        if (outcome.Gate is not null)
            json.Property("gate", outcome.Gate);
        json.Property("wrote", outcome.Wrote);
        if (outcome.EntryId is Guid entryId)
            json.Property("entry_id", entryId.ToString("D"));
        if (outcome.EntryVersion is long version)
        {
            if (version < 1)
                throw new InvalidOperationException("Refusing to serialize a version below 1.");
            json.Property("entry_version", version);
        }
        if (outcome.PartnerTargetCheck is not null)
            json.Property("partner_target_check", outcome.PartnerTargetCheck);
        if (outcome.LocationMembership is not null)
            json.Property("location_membership", outcome.LocationMembership);
        if (outcome.AuditIntentStatus is not null)
            json.Property("audit_intent_status", outcome.AuditIntentStatus);
        if (outcome.Intent is AuditIntent intent)
        {
            json.Property("audit_emitted", false);
            json.Property("proposed_event_name", intent.ProposedEventName);
            json.Property("event_id", intent.EventId.ToString("D"));
            json.Property("change_id", intent.ChangeId.ToString("D"));
            json.Property("correlation_id", intent.Fields["correlation_id"] ?? "");
        }
        json.Property("proof_boundary", outcome.ProofBoundary);
        json.Property("mapping", CatalogHttpMapping.Name);
        if (outcome.Entry is EntryProjection entry)
            WriteEntry(json, entry);
        if (outcome.List is EntryList list)
        {
            json.Name("entries");
            json.BeginArray();
            foreach (var item in list.Entries)
            {
                json.Item();
                WriteEntryObject(json, item);
            }
            json.EndArray();
            json.Property("pagination", list.Pagination);
        }
        if (outcome.History is not null)
        {
            json.Name("history");
            json.BeginArray();
            foreach (var row in outcome.History)
            {
                json.Item();
                WriteHistory(json, row);
            }
            json.EndArray();
        }
        if (outcome.Preview is PreviewReading preview)
            WritePreview(json, "preview", preview.Impact, preview.AffectedCount, preview.ApplyAllowed, preview.FixtureRevision, preview.FixtureDeclaredEdgeCount, preview.RuntimeConfiguration, preview.AuthorizesWrites);
        json.EndObject();
        return json.Text;
    }

    private static void RejectUnemittable(CatalogOutcome outcome)
    {
        if (string.Equals(outcome.AuditIntentStatus, FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal)
            || string.Equals(outcome.Intent?.IntentStatus, FrozenAuditSchemaBoundary.DurableAcceptedNotClaimed, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to serialize a durable-acceptance status.");
        if (outcome.Intent is not null && outcome.Intent.Fields["emitted"] != "false")
            throw new InvalidOperationException("Refusing to serialize an emitted catalog intent.");
        if (outcome.Preview is { AffectedCount: not null } || outcome.Graph is { AffectedCount: not null })
            throw new InvalidOperationException("Refusing to serialize an impact count.");
        if (outcome.Preview is { ApplyAllowed: true } or { RuntimeConfiguration: true } or { AuthorizesWrites: true })
            throw new InvalidOperationException("Refusing to serialize a preview that claims authorization.");
        if (outcome.Graph is { ApplyAllowed: true } or { RuntimeConfiguration: true } or { AuthorizesWrites: true })
            throw new InvalidOperationException("Refusing to serialize a graph that claims authorization.");
    }

    private static void WriteEntry(JsonText json, EntryProjection entry)
    {
        json.Name("entry");
        WriteEntryObject(json, entry);
    }

    private static void WriteEntryObject(JsonText json, EntryProjection entry)
    {
        json.BeginObject();
        json.Property("entry_id", entry.EntryId.ToString("D"));
        json.Property("family", entry.Family);
        json.Property("entry_class", entry.EntryClass);
        if (entry.OwnerPartnerId is Guid owner)
            json.Property("owner_partner_id", owner.ToString("D"));
        json.Property("scope_kind", entry.ScopeKind);
        json.Property("code", entry.Code);
        json.Property("display_name", entry.DisplayName);
        if (entry.Description is not null)
            json.Property("description", entry.Description);
        if (entry.EntryVersion < 1)
            throw new InvalidOperationException("Refusing to serialize a version below 1.");
        json.Property("entry_version", entry.EntryVersion);
        json.Name("fields");
        json.BeginObject();
        foreach (var key in entry.Fields.Keys.OrderBy(key => key, StringComparer.Ordinal))
            json.Property(key, entry.Fields[key]);
        json.EndObject();
        WriteActivation(json, "entry_activation", entry.EntryActivation);
        WriteActivation(json, "partner_switch", entry.PartnerSwitch);
        WriteActivation(json, "location_switch", entry.LocationSwitch);
        json.Property("effective_availability", entry.EffectiveAvailability);
        json.Name("allowed_actions");
        json.BeginArray();
        foreach (var action in entry.AllowedActions)
        {
            json.Item();
            json.BeginObject();
            json.Property("action", action.Action);
            json.Property("allowed", action.Allowed);
            json.Property("gate", action.Gate);
            json.EndObject();
        }
        json.EndArray();
        json.Property("proof_boundary", entry.ProofBoundary);
        json.EndObject();
    }

    private static void WriteActivation(JsonText json, string name, ActivationProjection activation)
    {
        if (activation.Token == "ABSENT" && (activation.Number is not null || activation.Active is not null))
            throw new InvalidOperationException("Refusing to serialize a stored version on an absent switch.");
        if (activation.Number == 0)
            throw new InvalidOperationException("Refusing to serialize version zero.");
        json.Name(name);
        json.BeginObject();
        json.Property("token", activation.Token);
        if (activation.Number is long number)
            json.Property("number", number);
        if (activation.Active is bool active)
            json.Property("active", active);
        json.EndObject();
    }

    private static void WriteHistory(JsonText json, HistoryProjection row)
    {
        json.BeginObject();
        json.Property("change_id", row.ChangeId.ToString("D"));
        json.Property("entry_id", row.EntryId.ToString("D"));
        json.Property("change_kind", row.ChangeKind);
        if (row.EntryVersion < 1)
            throw new InvalidOperationException("Refusing to serialize a version below 1.");
        json.Property("entry_version", row.EntryVersion);
        json.Name("changed_fields");
        json.BeginArray();
        foreach (var field in row.ChangedFields)
        {
            json.Item();
            json.String(field);
        }
        json.EndArray();
        WriteMap(json, "before", row.Before);
        WriteMap(json, "after", row.After);
        json.Property("occurred_at", row.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
        json.EndObject();
    }

    private static void WriteMap(JsonText json, string name, IReadOnlyDictionary<string, string?>? map)
    {
        if (map is null)
            return;
        json.Name(name);
        json.BeginObject();
        foreach (var key in map.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (map[key] is string value)
                json.Property(key, value);
        }
        json.EndObject();
    }

    private static void WritePreview(
        JsonText json,
        string name,
        string impact,
        int? affectedCount,
        bool applyAllowed,
        string? fixtureRevision,
        int? fixtureDeclaredEdgeCount,
        bool runtimeConfiguration,
        bool authorizesWrites)
    {
        if (affectedCount is not null)
            throw new InvalidOperationException("Refusing to serialize an impact count.");
        json.Name(name);
        json.BeginObject();
        json.Property("impact", impact);
        json.Property("apply_allowed", applyAllowed);
        if (fixtureRevision is not null)
            json.Property("fixture_revision", fixtureRevision);
        if (fixtureDeclaredEdgeCount is int edges)
            json.Property("fixture_declared_edge_count", edges);
        json.Property("runtime_configuration", runtimeConfiguration);
        json.Property("authorizes_writes", authorizesWrites);
        json.EndObject();
    }
}
