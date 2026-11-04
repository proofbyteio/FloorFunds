using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Admin.Api;

/// <summary>Audit events for S2a (spec §7). Every envelope is validated against audit-events.schema.json before the insert.</summary>
public sealed class AuditOutbox(JsonSchemaLite schema)
{
    public const string SchemaVersion = "1.1.0";

    public static string Ts(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    public static JsonObject State(string kind, Guid[] tenants, bool fallback, bool mfa) => new()
    {
        ["provider_kind"] = kind, ["entra_allowed_tenant_ids"] = Arr(tenants.Select(t => t.ToString("D"))),
        ["allow_db_fallback"] = fallback, ["mfa_required"] = mfa,
    };

    static JsonObject Envelope(Guid eventId, string type, string outcome, DateTimeOffset at, AdminIdentity actor, string requestId, bool withAdminType,
        string targetType, Guid? targetId, string action, Guid? scopePartner, Guid[] scopeLocations, JsonObject details)
    {
        var e = new JsonObject
        {
            ["event_id"] = eventId.ToString("D"), ["schema"] = "cachd.log", ["schema_version"] = SchemaVersion, ["category"] = "event",
            ["event_type"] = type, ["occurred_at"] = Ts(at), ["outcome"] = outcome, ["is_audit"] = true, ["ingest_path"] = "outbox",
            ["correlation_id"] = requestId, // = the response X-Request-ID [TLD 2026-11-04 9:20]
        };
        if (withAdminType) e["admin_type"] = actor.AdminType;
        e["actor"] = new JsonObject { ["user_id"] = actor.UserId.ToString("D") };
        e["source"] = new JsonObject { ["service"] = "backoffice-api" };
        var target = new JsonObject { ["type"] = targetType };
        if (targetId is { } t) target["id"] = t.ToString("D");
        e["target"] = target;
        e["action"] = action;
        var scope = new JsonObject();
        if (scopePartner is { } sp) scope["partner_id"] = sp.ToString("D");
        scope["location_ids"] = Arr(scopeLocations.Select(l => l.ToString("D")));
        e["scope"] = scope;
        e["details"] = details;
        return e;
    }

    public static JsonObject ProviderChanged(Guid eventId, DateTimeOffset at, AdminIdentity actor, string requestId, Guid partner, Guid location, Guid changeId,
        bool created, long rowVersion, IEnumerable<string> changed, JsonObject? before, JsonObject after, string? reason) =>
        Envelope(eventId, "auth.provider.changed", "success", at, actor, requestId, true, "location", location, created ? "create" : "update", partner, [location],
            new JsonObject
            {
                ["location_id"] = location.ToString("D"), ["change_id"] = changeId.ToString("D"), ["change_kind"] = created ? "created" : "updated",
                ["row_version"] = rowVersion, ["changed_fields"] = Arr(changed), ["before"] = before, ["after"] = after, ["reason"] = reason,
            });

    /// <summary>
    /// auth.config.change_denied, location_login_config branch (§7.2). not_admin carries no admin_type and no actor_partner_id.
    /// Update: target location_login_config/location, scope [location], details.location_id = location.
    /// Create: target partner/partner, scope [], details.location_id null [TLD 2026-11-04 9:04, OQ-P3-2]. targetPartner is null only for
    /// a not_admin create whose body gave no valid partner_id: then target.id and scope.partner_id are both absent (OQ-P3-10).
    /// </summary>
    public static JsonObject Denied(Guid eventId, DateTimeOffset at, AdminIdentity actor, string requestId, string reason, Guid? targetPartner, Guid? location,
        IEnumerable<string> attempted)
    {
        var isAdmin = reason != "not_admin";
        if (targetPartner is null && (isAdmin || location is not null)) throw new InvalidOperationException("denied event without a target partner");
        var d = new JsonObject
        {
            ["setting"] = "location_login_config", ["operation"] = location is null ? "create" : "update",
            ["location_id"] = location?.ToString("D"), ["reason"] = reason, ["attempted_fields"] = Arr(attempted.Distinct()),
        };
        if (isAdmin && actor.AdminType == "partner") d["actor_partner_id"] = actor.PartnerId!.Value.ToString("D");
        return location is { } l
            ? Envelope(eventId, "auth.config.change_denied", "denied", at, actor, requestId, isAdmin, "location_login_config", l, "update", targetPartner, [l], d)
            : Envelope(eventId, "auth.config.change_denied", "denied", at, actor, requestId, isAdmin, "partner", targetPartner, "create", targetPartner, [], d);
    }

    /// <summary>Validates, then inserts in the caller's transaction. Invalid envelope = exception (the caller returns 500, fail closed).</summary>
    public async Task InsertAsync(NpgsqlConnection c, NpgsqlTransaction tx, JsonObject envelope, CancellationToken ct)
    {
        var errs = schema.Validate(envelope);
        if (errs.Count > 0) throw new InvalidOperationException("audit envelope failed audit-events.schema.json: " + string.Join("; ", errs));
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO auth.audit_outbox (event_id, occurred_at, event_type, envelope) VALUES ($1, $2, $3, $4)", c, tx);
        cmd.Parameters.Add(new() { Value = Guid.Parse((string)envelope["event_id"]!) });
        cmd.Parameters.Add(new() { Value = DateTimeOffset.Parse((string)envelope["occurred_at"]!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = (string)envelope["event_type"]! });
        cmd.Parameters.Add(new() { Value = envelope.ToJsonString(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
