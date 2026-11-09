using System.Security.Cryptography;
using System.Text;
using Catalog.Contracts;

namespace Catalog.Contracts.Doubles;

/// <summary>
/// Catalog domain service bound to the in-memory double. It does not open a database session,
/// does not call a host, and does not apply a graph. Success means the double recorded the change.
/// </summary>
public sealed class CatalogDomainService
{
    private readonly InMemoryCatalogRepository _repository;
    private readonly ICatalogClock _clock;
    private readonly ICatalogIds _ids;
    private readonly Dictionary<string, object> _keyLocks = new(StringComparer.Ordinal);

    public CatalogDomainService(InMemoryCatalogRepository repository, ICatalogClock clock, ICatalogIds ids)
    {
        _repository = repository;
        _clock = clock;
        _ids = ids;
    }

    public CatalogOutcome Execute(CatalogPrincipal actor, CatalogCall call, CatalogCommand command)
    {
        switch (command)
        {
            case CatalogCommand.Rejected rejected:
                return Refuse(rejected.Status, rejected.Code, rejected.Detail);
            case CatalogCommand.CreateUnavailable unavailable:
                return Unavailable(CatalogGates.CreateDtoUnresolved, unavailable.Detail);
            case CatalogCommand.CreateFee fee:
                return WithKey(actor, call, "fee_create", CatalogFamily.FeeType, null, null, fee.Body, () => CreateFee(actor, call, fee));
            case CatalogCommand.EditClaim edit:
                return WithKey(actor, call, "claim_edit", CatalogFamily.ModelHardwareClaim, edit.EntryId, edit.ExpectedEntryVersion, edit.Body,
                    () => EditClaim(actor, call, edit));
            case CatalogCommand.ActivateEntry entry:
                return Activate(actor, entry.Family, entry.EntryId, entry.DesiredActive, partnerText: null, locationText: null, entryCommand: true);
            case CatalogCommand.ActivatePartner partner:
                return Activate(actor, partner.Family, partner.EntryId, partner.DesiredActive, partner.PartnerIdText, locationText: null, entryCommand: false);
            case CatalogCommand.ActivateLocation location:
                return Activate(actor, location.Family, location.EntryId, location.DesiredActive, partnerText: null, location.LocationIdText, entryCommand: false);
            default:
                return Refuse(ProposedStatus.BadRequest, "invalid_dto", "command");
        }
    }

    public CatalogOutcome Read(CatalogPrincipal actor, CatalogFamily family, Guid entryId)
    {
        var entry = _repository.Find(entryId);
        if (!Visible(actor, entry) || entry!.Family != family)
            return CatalogOutcome.NotFound();
        return new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "ok",
            Entry = Project(actor, entry),
        };
    }

    public CatalogOutcome List(CatalogPrincipal actor, CatalogFamily family)
    {
        if (!actor.IsManagementRole)
            return Refuse(ProposedStatus.Forbidden, "no_management_grant", "no_management_grant");
        var entries = _repository.ListFamily(family).Where(entry => Visible(actor, entry)).Select(entry => Project(actor, entry)).ToArray();
        return new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "ok",
            List = new EntryList(entries, "not_a_cursor_contract"),
        };
    }

    public CatalogOutcome History(CatalogPrincipal actor, CatalogFamily family, Guid entryId)
    {
        var entry = _repository.Find(entryId);
        if (!Visible(actor, entry) || entry!.Family != family)
            return CatalogOutcome.NotFound();
        var rows = _repository.HistoryFor(entryId).Select(row => new HistoryProjection(
            row.ChangeId,
            row.EntryId,
            row.ChangeKind,
            row.EntryVersion,
            row.ChangedFields,
            row.Before,
            row.After,
            row.OccurredAt)).ToArray();
        return new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "ok",
            History = rows,
        };
    }

    public CatalogOutcome Preview(CatalogPrincipal actor, CatalogFamily family, Guid entryId, GraphView? graph)
    {
        var entry = _repository.Find(entryId);
        if (!Visible(actor, entry) || entry!.Family != family)
            return CatalogOutcome.NotFound();
        return new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "ok",
            Wrote = false,
            Preview = ReadPreview(graph ?? GraphView.Default),
        };
    }

    public CatalogOutcome DescribeGraph(GraphView? graph)
    {
        var view = graph ?? GraphView.Default;
        var preview = ReadPreview(view);
        return new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "ok",
            Wrote = false,
            Graph = new GraphReading(
                view is GraphView.Fixture ? "fixture" : "unconfigured",
                preview.Impact,
                preview.AffectedCount,
                preview.ApplyAllowed,
                preview.FixtureRevision,
                preview.FixtureDeclaredEdgeCount,
                preview.RuntimeConfiguration,
                preview.AuthorizesWrites),
        };
    }

    private CatalogOutcome CreateFee(CatalogPrincipal actor, CatalogCall call, CatalogCommand.CreateFee fee)
    {
        if (!actor.IsManagementRole)
            return Refuse(ProposedStatus.Forbidden, "no_management_grant", "no_management_grant");
        if (string.IsNullOrWhiteSpace(call.CorrelationId) || string.IsNullOrWhiteSpace(call.SourceRequestId))
            return Refuse(ProposedStatus.BadRequest, "server_context_required", "server_context_required");

        var incoming = actor.Role == CatalogRole.SuperAdmin ? EntryClass.Global : EntryClass.Private;
        if (!CodeAllowed(CatalogFamily.FeeType, fee.Code, incoming, out var codeRefusal))
            return codeRefusal!;

        var entryId = _ids.NewId();
        var changeId = _ids.NewId();
        var eventId = _ids.NewId();
        var occurredAt = _clock.UtcNow;
        var after = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["code"] = fee.Code,
            ["display_name"] = fee.DisplayName,
        };
        var changed = new List<string> { "code", "display_name" };
        if (fee.Description is not null)
        {
            after["description"] = fee.Description;
            changed.Add("description");
        }

        var entry = new StoredEntry
        {
            EntryId = entryId,
            Family = CatalogFamily.FeeType,
            Class = incoming,
            OwnerPartnerId = incoming == EntryClass.Private ? actor.PartnerId : null,
            Code = fee.Code,
            DisplayName = fee.DisplayName,
            Description = fee.Description,
            EntryVersion = 1,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal),
            Origin = EntryOrigin.Command,
        };
        var intent = AuditIntent.Create(
            eventId, changeId, entryId, ProposedCatalogEvents.Created, actor, CatalogFamilies.Wire(CatalogFamily.FeeType),
            "entry", 1, changed, before: null, after, reason: null, call.CorrelationId, call.SourceRequestId, occurredAt,
            AuditAllowlist.FeeValueKeys);
        var history = HistoryRow(changeId, entry, actor, "created", changed, before: null, after, occurredAt);
        var outcome = new CatalogOutcome
        {
            Status = ProposedStatus.Created,
            Code = "created",
            Wrote = true,
            EntryId = entryId,
            EntryVersion = 1,
            AuditIntentStatus = AuditAllowlist.IntentStatus,
            Intent = intent,
            Entry = Project(actor, entry),
        };
        _repository.Commit(entry, history, intent, new IdempotencyRecord(call.IdempotencyKey!, Bind(actor, "fee_create", CatalogFamily.FeeType, null, null, fee.Body), outcome));
        return outcome;
    }

    private CatalogOutcome EditClaim(CatalogPrincipal actor, CatalogCall call, CatalogCommand.EditClaim edit)
    {
        if (string.IsNullOrWhiteSpace(call.CorrelationId) || string.IsNullOrWhiteSpace(call.SourceRequestId))
            return Refuse(ProposedStatus.BadRequest, "server_context_required", "server_context_required");
        var entry = _repository.Find(edit.EntryId);
        if (!Visible(actor, entry) || entry!.Family != CatalogFamily.ModelHardwareClaim)
            return CatalogOutcome.NotFound();
        if (!CanEditClaim(actor, entry))
            return Refuse(ProposedStatus.Forbidden, "catalog_claim_edit_actor", "catalog_claim_edit_actor");
        var expected = edit.ExpectedEntryVersion.Number!.Value;
        if (!_repository.VersionMatches(edit.EntryId, expected))
            return Refuse(ProposedStatus.Conflict, "catalog_version_conflict", "catalog_version_conflict");

        var before = new Dictionary<string, string?>(StringComparer.Ordinal);
        var after = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var pair in edit.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var current = CurrentValue(entry, pair.Key);
            if (current == pair.Value)
                continue;
            before[pair.Key] = current;
            after[pair.Key] = pair.Value;
            changed.Add(pair.Key);
        }
        if (changed.Count == 0)
            return Refuse(ProposedStatus.Conflict, "catalog_noop", "catalog_noop");

        var fields = new Dictionary<string, string>(entry.Fields, StringComparer.Ordinal);
        foreach (var key in changed)
            fields[key] = after[key]!;
        var updated = new StoredEntry
        {
            EntryId = entry.EntryId,
            Family = entry.Family,
            Class = entry.Class,
            OwnerPartnerId = entry.OwnerPartnerId,
            Code = entry.Code,
            DisplayName = fields.TryGetValue("display_name", out var display) ? display : entry.DisplayName,
            Description = fields.TryGetValue("description", out var description) ? description : entry.Description,
            EntryVersion = entry.EntryVersion + 1,
            Fields = fields,
            Origin = entry.Origin,
        };
        var changeId = _ids.NewId();
        var eventId = _ids.NewId();
        var occurredAt = _clock.UtcNow;
        var intent = AuditIntent.Create(
            eventId, changeId, entry.EntryId, ProposedCatalogEvents.Updated, actor, CatalogFamilies.Wire(entry.Family),
            "entry", updated.EntryVersion, changed, before, after, reason: null, call.CorrelationId, call.SourceRequestId,
            occurredAt, AuditAllowlist.ClaimEditValueKeys);
        var history = HistoryRow(changeId, updated, actor, "updated", changed, before, after, occurredAt);
        var outcome = new CatalogOutcome
        {
            Status = ProposedStatus.Ok,
            Code = "updated",
            Wrote = true,
            EntryId = updated.EntryId,
            EntryVersion = updated.EntryVersion,
            AuditIntentStatus = AuditAllowlist.IntentStatus,
            Intent = intent,
            Entry = Project(actor, updated),
        };
        _repository.Commit(updated, history, intent, new IdempotencyRecord(call.IdempotencyKey!, Bind(actor, "claim_edit", CatalogFamily.ModelHardwareClaim, edit.EntryId, edit.ExpectedEntryVersion, edit.Body), outcome));
        return outcome;
    }

    private CatalogOutcome Activate(
        CatalogPrincipal actor,
        CatalogFamily family,
        Guid entryId,
        bool desiredActive,
        string? partnerText,
        string? locationText,
        bool entryCommand)
    {
        string? partnerCheck = null;
        string? locationMembership = null;
        if (partnerText is not null)
        {
            if (actor.Role == CatalogRole.SuperAdmin)
            {
                if (!Guid.TryParseExact(partnerText, "D", out var partner) || partner == Guid.Empty)
                    return Refuse(ProposedStatus.BadRequest, "invalid_partner_uuid", "partner_id");
                partnerCheck = CatalogCommandParser.PartnerTargetSyntaxOnly;
            }
            else if (actor.Role == CatalogRole.PartnerAdmin)
            {
                if (!Guid.TryParseExact(partnerText, "D", out var partner) || partner != actor.PartnerId)
                    return Refuse(ProposedStatus.BadRequest, "contradictory_partner", "partner_id");
                partnerCheck = "authenticated_principal_not_registry";
            }
        }
        if (locationText is not null && actor.Role == CatalogRole.PartnerAdmin)
            locationMembership = "not_verified";

        if (!actor.IsManagementRole)
            return CatalogOutcome.NotFound();
        var entry = _repository.Find(entryId);
        if (!Visible(actor, entry) || entry!.Family != family)
            return CatalogOutcome.NotFound();
        if (locationText is not null && actor.Role == CatalogRole.SuperAdmin)
            return Refuse(ProposedStatus.Forbidden, "sa_has_no_location_switch", "sa_has_no_location_switch");

        if (partnerText is not null && entry.Class == EntryClass.Private)
            return Unavailable(CatalogGates.PrivatePartnerSwitchUnresolved, OpenPolicies.PrivatePartnerSwitch, partnerCheck, locationMembership);
        if (entryCommand && family == CatalogFamily.ModelHardwareClaim && desiredActive)
            return Unavailable(CatalogGates.ClaimReactivationPendingPq272, "pending_pq272", partnerCheck, locationMembership);
        if (!desiredActive)
            return Unavailable(CatalogGates.GraphUnconfiguredAllOff, CatalogGates.GraphUnconfiguredAllOff, partnerCheck, locationMembership);
        return Unavailable(CatalogGates.GraphDependentRestoreUnavailable, CatalogGates.GraphDependentRestoreUnavailable, partnerCheck, locationMembership);
    }

    private CatalogOutcome WithKey(
        CatalogPrincipal actor,
        CatalogCall call,
        string operation,
        CatalogFamily family,
        Guid? entryId,
        VersionToken? version,
        IReadOnlyDictionary<string, string?> body,
        Func<CatalogOutcome> apply)
    {
        if (string.IsNullOrWhiteSpace(call.IdempotencyKey) || call.IdempotencyKey.Length > 200)
            return Refuse(ProposedStatus.BadRequest, "idempotency_key_required", "idempotency_key_required");
        var hash = Bind(actor, operation, family, entryId, version, body);
        lock (LockFor(call.IdempotencyKey))
        {
            var existing = _repository.FindIdempotency(call.IdempotencyKey);
            if (existing is not null)
            {
                return existing.BindingHash == hash
                    ? existing.Outcome
                    : Refuse(ProposedStatus.Conflict, "idempotency_conflict", "idempotency_conflict");
            }
            try
            {
                return apply();
            }
            catch (CatalogCommitRolledBackException)
            {
                return Refuse(ProposedStatus.Unavailable, "commit_rolled_back", "commit_rolled_back");
            }
        }
    }

    private bool CodeAllowed(CatalogFamily family, string code, EntryClass incoming, out CatalogOutcome? refusal)
    {
        var same = _repository.FindByCode(family, code);
        if (same.Count == 0 || (same.Count == 1 && same[0].Class != incoming))
        {
            refusal = null;
            return true;
        }
        var privateCollision = incoming == EntryClass.Private || same.Any(entry => entry.Class == EntryClass.Private);
        refusal = Unavailable(
            privateCollision ? CatalogGates.PrivateCodePolicyUnresolved : "code_policy_unresolved",
            "code_policy_unresolved");
        return false;
    }

    private static bool Visible(CatalogPrincipal actor, StoredEntry? entry)
    {
        if (entry is null || !actor.IsManagementRole)
            return false;
        if (entry.Class == EntryClass.Global)
            return true;
        if (entry.Family == CatalogFamily.ModelHardwareClaim)
            return actor.Role == CatalogRole.SuperAdmin || actor.PartnerId == entry.OwnerPartnerId;
        return actor.Role == CatalogRole.PartnerAdmin && actor.PartnerId == entry.OwnerPartnerId;
    }

    private static bool CanEditClaim(CatalogPrincipal actor, StoredEntry entry) =>
        actor.Role == CatalogRole.SuperAdmin
        || (actor.Role == CatalogRole.PartnerAdmin && entry.Class == EntryClass.Private && actor.PartnerId == entry.OwnerPartnerId);

    private static string? CurrentValue(StoredEntry entry, string key) => key switch
    {
        "display_name" => entry.Fields.TryGetValue("display_name", out var display) ? display : entry.DisplayName,
        "description" => entry.Fields.TryGetValue("description", out var description) ? description : entry.Description,
        _ => entry.Fields.TryGetValue(key, out var value) ? value : null,
    };

    private EntryProjection Project(CatalogPrincipal actor, StoredEntry entry)
    {
        var fields = new Dictionary<string, string>(entry.Fields, StringComparer.Ordinal);
        fields.Remove("display_name");
        fields.Remove("description");
        return new EntryProjection(
            entry.EntryId,
            CatalogFamilies.Wire(entry.Family),
            entry.Class == EntryClass.Global ? "global" : "private",
            entry.OwnerPartnerId,
            entry.Class == EntryClass.Global ? "global" : "private",
            entry.Code,
            entry.DisplayName,
            entry.Description,
            entry.EntryVersion,
            fields,
            ActivationProjection.Absent,
            ActivationProjection.Absent,
            ActivationProjection.Absent,
            "unknown",
            Allowed(actor, entry),
            ProofBoundaryNames.InMemoryDouble);
    }

    private static IReadOnlyList<AllowedAction> Allowed(CatalogPrincipal actor, StoredEntry entry)
    {
        var actions = new List<AllowedAction>
        {
            new("entry_off", false, CatalogGates.GraphUnconfiguredAllOff),
            new("entry_on", false, entry.Family == CatalogFamily.ModelHardwareClaim
                ? CatalogGates.ClaimReactivationPendingPq272
                : CatalogGates.GraphDependentRestoreUnavailable),
            new("partner_off", false, entry.Class == EntryClass.Private
                ? CatalogGates.PrivatePartnerSwitchUnresolved
                : CatalogGates.GraphUnconfiguredAllOff),
            new("partner_on", false, entry.Class == EntryClass.Private
                ? CatalogGates.PrivatePartnerSwitchUnresolved
                : CatalogGates.GraphDependentRestoreUnavailable),
            new("location_off", false, actor.Role == CatalogRole.SuperAdmin
                ? "sa_has_no_location_switch"
                : CatalogGates.GraphUnconfiguredAllOff),
            new("location_on", false, actor.Role == CatalogRole.SuperAdmin
                ? "sa_has_no_location_switch"
                : CatalogGates.GraphDependentRestoreUnavailable),
        };
        if (entry.Family == CatalogFamily.ModelHardwareClaim)
            actions.Add(new("claim_edit", CanEditClaim(actor, entry), CanEditClaim(actor, entry) ? "proposed_six_field_allowlist" : "catalog_claim_edit_actor"));
        return actions;
    }

    private static PreviewReading ReadPreview(GraphView graph)
    {
        if (graph is GraphView.Fixture fixture)
        {
            return new PreviewReading(
                "fixture_not_authorization",
                AffectedCount: null,
                ApplyAllowed: false,
                string.IsNullOrWhiteSpace(fixture.Revision) ? null : fixture.Revision,
                fixture.Edges.Count,
                RuntimeConfiguration: false,
                AuthorizesWrites: false);
        }
        return new PreviewReading(
            "unknown",
            AffectedCount: null,
            ApplyAllowed: false,
            FixtureRevision: null,
            FixtureDeclaredEdgeCount: null,
            RuntimeConfiguration: false,
            AuthorizesWrites: false);
    }

    private static StoredHistory HistoryRow(
        Guid changeId,
        StoredEntry entry,
        CatalogPrincipal actor,
        string changeKind,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, string?>? before,
        IReadOnlyDictionary<string, string?> after,
        DateTimeOffset occurredAt) => new()
        {
            ChangeId = changeId,
            EntryId = entry.EntryId,
            Family = entry.Family,
            ActorUserId = actor.UserId,
            ChangeKind = changeKind,
            EntryVersion = entry.EntryVersion,
            ChangedFields = changed,
            Before = before,
            After = after,
            OccurredAt = occurredAt,
        };

    private object LockFor(string key)
    {
        lock (_keyLocks)
        {
            if (!_keyLocks.TryGetValue(key, out var gate))
                _keyLocks[key] = gate = new object();
            return gate;
        }
    }

    private static string Bind(
        CatalogPrincipal actor,
        string operation,
        CatalogFamily family,
        Guid? entryId,
        VersionToken? version,
        IReadOnlyDictionary<string, string?> body)
    {
        var builder = new StringBuilder();
        builder.Append("v1\n").Append(actor.UserId.ToString("D")).Append('\n')
            .Append(actor.Role).Append('\n')
            .Append(actor.PartnerId?.ToString("D") ?? "-").Append('\n')
            .Append(operation).Append('\n')
            .Append(CatalogFamilies.Wire(family)).Append('\n')
            .Append(entryId?.ToString("D") ?? "-").Append('\n')
            .Append(version?.ToString() ?? "-").Append('\n');
        foreach (var pair in body.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static CatalogOutcome Refuse(ProposedStatus status, string code, string detail) => new()
    {
        Status = status,
        Code = code,
        Detail = detail,
        Wrote = false,
    };

    private static CatalogOutcome Unavailable(string gate, string detail, string? partnerCheck = null, string? locationMembership = null) => new()
    {
        Status = ProposedStatus.Unavailable,
        Code = "unavailable",
        Gate = gate,
        Detail = detail,
        Wrote = false,
        PartnerTargetCheck = partnerCheck,
        LocationMembership = locationMembership,
    };

}
