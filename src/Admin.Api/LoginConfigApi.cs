using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Admin.Api;

/// <summary>A caller after step 1 (authn) and the server-side role lookup. Nothing here comes from request data.</summary>
public sealed record Caller(AdminIdentity Id, AdminGrant Grant, string RequestId)
{
    public bool IsSuper => Grant.Role == BuiltInRole.SuperAdmin;
    public bool IsPartnerAdmin => Grant.Role == BuiltInRole.PartnerAdmin;
    public bool IsAdmin => IsSuper || IsPartnerAdmin;
}

sealed record LlcRow(Guid LocationId, Guid PartnerId, string Slug, string Name, string ProviderKind, string DisplayName, Guid[] Tenants,
    bool AllowDbFallback, bool MfaRequired, long RowVersion,
    Guid CreatedBy, DateTime CreatedAt, Guid UpdatedBy, DateTime UpdatedAt, DateTime? RotatedAt)
{
    public CurrentConfig Current => new(ProviderKind, Tenants, AllowDbFallback, MfaRequired);
    public JsonObject State => AuditOutbox.State(ProviderKind, Tenants, AllowDbFallback, MfaRequired);
}

/// <summary>
/// S2a location login config endpoints (spec §3), check order §4. Trust boundary (§8.3, K4): app.admin_type / app.partner_id are
/// set only by <see cref="SetScopeAsync"/>, transaction-local (set_config(..., true)), from the <see cref="Caller"/> (validated token +
/// server-side directory) or from the definer function's answer, never from the body, headers, query or path. All SQL is parameterised.
/// </summary>
public sealed class LoginConfigApi(AdminApiOptions opt, NpgsqlDataSource db, AdminTokenValidator tokens, IAdminDirectory directory,
    AuditOutbox outbox, ILogger<LoginConfigApi> log, TimeProvider clock)
{
    const int MaxBody = 64 * 1024;
    static readonly Regex IfMatchRe = new(@"^""([1-9][0-9]{0,17})""\z", RegexOptions.CultureInvariant);
    static readonly JsonDocumentOptions DocOpts = new() { MaxDepth = 16 };

    // ---------- plumbing ----------

    sealed class Fail(int status, JsonObject? body) : Exception
    {
        public int Status { get; } = status;
        public JsonObject? Body { get; } = body;
    }

    static Fail Err(int status, string code, Action<JsonObject>? more = null)
    {
        var b = new JsonObject { ["error"] = code };
        more?.Invoke(b);
        return new Fail(status, b);
    }

    static IResult Json(int status, JsonNode body) => Results.Text(body.ToJsonString(), "application/json; charset=utf-8", null, status);

    /// <summary>
    /// Server-generated UUIDv7 per request, on every response (incl. 401 and 500); a client value is ignored, never echoed
    /// [TLD 2026-11-04 9:04, OQ-P3-3]. Name as v1 errors.md L67 [TLD 2026-11-04 9:20]. Set by <see cref="RequestEnvelope"/>
    /// middleware, so 405 and unrouted 404 carry it too [TLD 2026-11-04 9:28, P3-4]. The same value is the audit event's
    /// correlation_id and is in the 500 error log line.
    /// </summary>
    public const string RequestIdHeader = RequestEnvelope.Header;

    async Task<IResult> Run(HttpContext ctx, Func<Caller, Task<IResult>> f)
    {
        var requestId = RequestEnvelope.Id(ctx);
        ctx.Response.Headers.CacheControl = "no-store";
        try
        {
            var id = await tokens.AuthenticateAsync(ctx.Request);
            if (id is null) { ctx.Response.Headers.WWWAuthenticate = "Bearer"; return Results.StatusCode(401); }
            var g = await directory.ResolveAsync(id, ctx.RequestAborted);
            // A built-in role must agree with the sign-in kind; otherwise only org:V (if any) remains.
            if ((g.Role == BuiltInRole.SuperAdmin && id.AdminType != "super") || (g.Role == BuiltInRole.PartnerAdmin && (id.AdminType != "partner" || id.PartnerId is null)))
                g = g with { Role = BuiltInRole.None, LoginMethodWrite = false, OrgCreateEdit = false };
            return await f(new Caller(id, g, requestId));
        }
        catch (Fail x) { return x.Body is null ? Results.StatusCode(x.Status) : Json(x.Status, x.Body); }
        catch (Exception x)
        {
            // Fail closed: nothing committed. Log the type/SQLSTATE only: messages and DETAIL lines can carry row values.
            log.LogError("admin-api request failed: {Type} {SqlState} request_id={RequestId}", x.GetType().Name, (x as PostgresException)?.SqlState, requestId);
            return Results.StatusCode(500);
        }
    }

    DateTimeOffset Now()
    {
        var t = clock.GetUtcNow();
        return new DateTimeOffset(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero); // PostgreSQL keeps microseconds.
    }

    static Guid? ParseId(string s) => Guid.TryParseExact(s, "D", out var g) ? g : null;

    /// <summary>The ONLY place app.* settings are written. Transaction-local; values from the caller or the definer function.</summary>
    static async Task SetScopeAsync(NpgsqlConnection c, NpgsqlTransaction tx, string adminType, Guid? partner)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.admin_type', $1, true), set_config('app.partner_id', $2, true)", c, tx);
        cmd.Parameters.Add(new() { Value = adminType });
        cmd.Parameters.Add(new() { Value = partner?.ToString("D") ?? "" });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>§4 steps 3-4: the definer function sees every row, so it can tell missing (null) from out of scope.</summary>
    async Task<Guid?> LocationPartnerAsync(Guid location)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.location_partner($1)", c);
        cmd.Parameters.Add(new() { Value = location });
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>Denied events: their own short outbox-only transaction (§8.5). A failure here propagates (500, fail closed).</summary>
    async Task WriteDeniedAsync(JsonObject ev)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await outbox.InsertAsync(c, tx, ev, CancellationToken.None);
        await tx.CommitAsync();
    }

    Task DenyAsync(Caller who, string reason, Guid? targetPartner, Guid? location, IEnumerable<string> attempted) =>
        WriteDeniedAsync(AuditOutbox.Denied(Guid.CreateVersion7(), Now(), who.Id, who.RequestId, reason, targetPartner, location, attempted));

    /// <summary>The body as read: <see cref="TooLarge"/> = over 64 KiB by Content-Length (nothing read) or while reading (chunked or
    /// unknown length: reading stops at the first buffer past the cap) [TLD 2026-11-04 9:28, P3-2]. <see cref="TimedOut"/> = not fully
    /// received within BodyReadTimeout [TLD 2026-11-04 9:53/10:02, P3-7]. Bytes is null when TooLarge or TimedOut.</summary>
    readonly record struct ReadBody(byte[]? Bytes, bool TooLarge, bool TimedOut = false);

    /// <summary>Reads at most 64 KiB + one 8 KiB buffer. Reaching the end marks the body consumed (<see cref="RequestEnvelope"/>);
    /// a body left unread gets Connection: close and a bounded drain after the response, so the client sees the status, not a reset.</summary>
    async Task<ReadBody> ReadBodyAsync(HttpContext ctx)
    {
        var req = ctx.Request;
        // Checked before the first read, so Expect: 100-continue over the cap never gets a 100 Continue [P3-8].
        if (req.ContentLength is > MaxBody) return new(null, true);
        using var ms = new MemoryStream();
        var (end, _) = await RequestEnvelope.ReadBoundedAsync(ctx, opt.BodyReadTimeout, MaxBody, seg => ms.Write(seg.Span));
        switch (end)
        {
            case RequestEnvelope.BodyEnd.OverLimit: return new(null, true);
            case RequestEnvelope.BodyEnd.TimedOut: return new(null, false, true);
            case RequestEnvelope.BodyEnd.Failed: throw new BadHttpRequestException("request body read failed");
        }
        RequestEnvelope.MarkBodyRead(ctx);
        return new(ms.ToArray(), false);
    }

    /// <summary>408 request_timeout [TLD 2026-11-04 9:53/10:02, P3-7]: ONE Warning line (request_id only, no exception), nothing
    /// written. The body stays unread, so RequestEnvelope adds Connection: close; its drain does not log again.</summary>
    Fail BodyTimeout(HttpContext ctx, Caller who)
    {
        log.LogWarning("admin-api request body not received within {Seconds} s: 408 request_timeout, request_id={RequestId}",
            opt.BodyReadTimeout.TotalSeconds, who.RequestId);
        RequestEnvelope.MarkWarned(ctx);
        return Err(408, "request_timeout");
    }

    /// <summary>For attributing a refusal only: any read problem (too large, aborted) = no body; never changes the answer.</summary>
    async Task<byte[]?> TryReadBodyAsync(HttpContext ctx)
    {
        try { return (await ReadBodyAsync(ctx)).Bytes; }
        catch (Exception x) when (x is BadHttpRequestException or IOException or OperationCanceledException) { return null; }
    }

    /// <summary>Step 7. Null = not a JSON object (or too large, duplicate keys, too deep).</summary>
    static JsonElement? ParseObject(byte[]? body)
    {
        if (body is null || body.Length == 0) return null;
        try
        {
            using var d = JsonDocument.Parse(body, DocOpts);
            if (d.RootElement.ValueKind != JsonValueKind.Object || LoginConfigValidator.HasDuplicateKeys(d.RootElement)) return null;
            return d.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    static async Task<LlcRow?> LoadAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid location, Guid filterPartner, bool forUpdate)
    {
        // Server query filter on partner_id (LD L173) in addition to RLS. The row's oidc_authority / oidc_client_id / secret_ref copies are
        // never selected: deployment config is the source of truth [TLD 2026-11-04 10:02 Q1; V11-ROWSECRETCLEANUP].
        await using var cmd = new NpgsqlCommand(
            """
            SELECT l.partner_id, l.slug, l.name, c.provider_kind, c.display_name, c.entra_allowed_tenant_ids,
                   c.allow_db_fallback, c.mfa_required, c.row_version, c.created_by, c.created_at, c.updated_by, c.updated_at,
                   (SELECT max(ch.at) FROM auth.location_login_config_change ch WHERE ch.location_id = l.location_id AND 'secret_ref' = ANY (ch.changed_fields))
            FROM auth.location l JOIN auth.location_login_config c ON c.location_id = l.location_id
            WHERE l.location_id = $1 AND l.partner_id = $2
            """ + (forUpdate ? " FOR UPDATE OF c" : ""), c, tx);
        cmd.Parameters.Add(new() { Value = location });
        cmd.Parameters.Add(new() { Value = filterPartner });
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new LlcRow(location, r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<Guid[]>(5),
            r.GetBoolean(6), r.GetBoolean(7), r.GetInt64(8), r.GetGuid(9), r.GetFieldValue<DateTime>(10), r.GetGuid(11), r.GetFieldValue<DateTime>(12),
            r.IsDBNull(13) ? null : r.GetFieldValue<DateTime>(13));
    }

    static string T(DateTime d) => AuditOutbox.Ts(new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)));

    JsonObject View(LlcRow r, bool super)
    {
        var v = new JsonObject
        {
            ["location_id"] = r.LocationId.ToString("D"), ["partner_id"] = r.PartnerId.ToString("D"), ["slug"] = r.Slug, ["name"] = r.Name,
            ["provider_kind"] = r.ProviderKind, ["display_name"] = r.DisplayName,
            ["entra_allowed_tenant_ids"] = new JsonArray(r.Tenants.Select(t => (JsonNode)JsonValue.Create(t.ToString("D"))!).ToArray()),
        };
        // [TLD 2026-11-04 10:02 Q1] oidc_* and secret.set follow deployment config (Entra:Authority / Entra:ClientId / Entra:SecretRef) for
        // every entra location, never the row copy. [TLD 2026-11-04 10:02 Q2] oidc_* AND secret are Super Admin only: for a Partner Admin
        // or an org:V reader the keys are omitted (not null), as login_config_view_restricted and UX mockup cbb3ab4b.
        if (super)
        {
            var isEntra = r.ProviderKind == "entra";
            v["oidc_authority"] = isEntra ? opt.EntraAuthority : null;
            v["oidc_client_id"] = isEntra ? opt.EntraClientId : null;
            v["secret"] = new JsonObject { ["set"] = isEntra, ["rotated_at"] = isEntra && r.RotatedAt is { } ra ? T(ra) : null }; // null when not set (secret_view)
        }
        v["allow_db_fallback"] = r.AllowDbFallback; v["mfa_required"] = r.MfaRequired; v["row_version"] = r.RowVersion;
        v["created_by"] = r.CreatedBy.ToString("D"); v["created_at"] = T(r.CreatedAt);
        v["updated_by"] = r.UpdatedBy.ToString("D"); v["updated_at"] = T(r.UpdatedAt);
        return v;
    }

    IResult ViewResult(HttpContext ctx, int status, LlcRow r, Caller who)
    {
        ctx.Response.Headers.ETag = $"\"{r.RowVersion}\"";
        return Json(status, View(r, who.IsSuper));
    }

    /// <summary>tenant_bound_elsewhere (§5.2), under per-tenant advisory locks taken in sorted order (§8.5).</summary>
    static async Task<bool> TenantBoundAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid[] tenants, Guid partner)
    {
        bool bound = false;
        foreach (var t in tenants.Distinct().OrderBy(x => x.ToString("D"), StringComparer.Ordinal))
        {
            await using var l = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('llc-tenant:' || $1::text))", c, tx);
            l.Parameters.Add(new() { Value = t });
            await l.ExecuteNonQueryAsync();
            await using var q = new NpgsqlCommand("SELECT auth.tenant_bound_elsewhere($1, $2)", c, tx);
            q.Parameters.Add(new() { Value = t });
            q.Parameters.Add(new() { Value = partner });
            bound |= await q.ExecuteScalarAsync() is true;
        }
        return bound;
    }

    static Fail Validation(List<FieldError> errs) => Err(422, "validation_failed", b =>
        b["fields"] = new JsonArray(errs.Select(e => (JsonNode)new JsonObject { ["field"] = e.Field, ["code"] = e.Code }).ToArray()));

    static async Task InsertChangeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid changeId, Guid location, Caller who, string? reason,
        DateTimeOffset at, bool created, string[] changed, JsonObject? before, JsonObject after, long rowVersion)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO auth.location_login_config_change (change_id, location_id, actor_user_id, admin_type, reason, at, change_kind, changed_fields, before, after, row_version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """, c, tx);
        cmd.Parameters.Add(new() { Value = changeId });
        cmd.Parameters.Add(new() { Value = location });
        cmd.Parameters.Add(new() { Value = who.Id.UserId });
        cmd.Parameters.Add(new() { Value = who.Id.AdminType });
        cmd.Parameters.Add(new() { Value = (object?)reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = at.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = created ? "created" : "updated" });
        cmd.Parameters.Add(new() { Value = changed, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)before?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = after.ToJsonString(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = rowVersion });
        await cmd.ExecuteNonQueryAsync();
    }

    static List<string> DowngradeFields(bool mfaBefore, bool fallbackBefore, LoginConfigInput n)
    {
        var f = new List<string>();
        if (mfaBefore && !n.MfaRequired) f.Add("mfa_required");
        if (!fallbackBefore && n.AllowDbFallback) f.Add("allow_db_fallback");
        return f;
    }

    // ---------- GET /api/orgs/locations/{id}/login-config ----------

    public Task<IResult> GetAsync(HttpContext ctx, string id) => Run(ctx, async who =>
    {
        // Step 2: org:V (built-in admins hold it; a reader holds it on listed locations).
        if (!who.IsAdmin && who.Grant.OrgViewLocations.Count == 0) throw Err(403, "forbidden");
        var loc = ParseId(id) ?? throw Err(404, "not_found");
        var real = await LocationPartnerAsync(loc) ?? throw Err(404, "not_found");
        if (who.IsPartnerAdmin && real != who.Id.PartnerId) throw Err(404, "not_found");
        if (!who.IsAdmin && !who.Grant.OrgViewLocations.Contains(loc)) throw Err(404, "not_found");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        // org:V reader: RLS scoped to the location's partner, decided server-side above (OQ-S2a-1).
        await SetScopeAsync(c, tx, who.IsSuper ? "super" : "partner", who.IsSuper ? null : who.IsPartnerAdmin ? who.Id.PartnerId : real);
        var row = await LoadAsync(c, tx, loc, who.IsPartnerAdmin ? who.Id.PartnerId!.Value : real, false) ?? throw Err(404, "not_found");
        await tx.CommitAsync();
        return ViewResult(ctx, 200, row, who);
    });

    // ---------- GET /api/orgs/locations/{id}/login-config/changes ----------

    public Task<IResult> ChangesAsync(HttpContext ctx, string id) => Run(ctx, async who =>
    {
        if (!who.IsAdmin) throw Err(403, "forbidden");
        var loc = ParseId(id) ?? throw Err(404, "not_found");
        var real = await LocationPartnerAsync(loc) ?? throw Err(404, "not_found");
        if (who.IsPartnerAdmin && real != who.Id.PartnerId) throw Err(404, "not_found");
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT ch.change_id, ch.at, ch.actor_user_id, ch.admin_type, ch.change_kind, ch.changed_fields, ch.before::text, ch.after::text, ch.reason, ch.row_version
            FROM auth.location_login_config_change ch JOIN auth.location l ON l.location_id = ch.location_id
            WHERE ch.location_id = $1 AND l.partner_id = $2
            ORDER BY ch.at DESC, ch.row_version DESC LIMIT 100
            """, c, tx);
        cmd.Parameters.Add(new() { Value = loc });
        cmd.Parameters.Add(new() { Value = who.IsPartnerAdmin ? who.Id.PartnerId!.Value : real });
        var items = new JsonArray();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                items.Add(new JsonObject
                {
                    ["change_id"] = r.GetGuid(0).ToString("D"), ["at"] = T(r.GetFieldValue<DateTime>(1)), ["actor_user_id"] = r.GetGuid(2).ToString("D"),
                    ["admin_type"] = r.GetString(3), ["change_kind"] = r.GetString(4),
                    ["changed_fields"] = new JsonArray(r.GetFieldValue<string[]>(5).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                    ["before"] = r.IsDBNull(6) ? null : JsonNode.Parse(r.GetString(6)), ["after"] = JsonNode.Parse(r.GetString(7)),
                    ["reason"] = r.IsDBNull(8) ? null : r.GetString(8), ["row_version"] = r.GetInt64(9),
                });
        await tx.CommitAsync();
        return Json(200, new JsonObject { ["items"] = items });
    });

    // ---------- PUT /api/orgs/locations/{id}/login-config ----------

    public Task<IResult> PutAsync(HttpContext ctx, string id) => Run(ctx, async who =>
    {
        var loc = ParseId(id);
        // Step 2, decided before the body is read [TLD 2026-11-04 9:04, OQ-P3-8]; the body is read afterwards only to name the attempted fields.
        if (!who.IsAdmin || !who.Grant.LoginMethodWrite)
        {
            if (loc is { } l0 && await LocationPartnerAsync(l0) is { } p0)
                await DenyAsync(who, "not_admin", p0, l0, LoginConfigValidator.AttemptedFields(ParseObject(await TryReadBodyAsync(ctx))));
            throw Err(403, "forbidden");
        }
        var read = await ReadBodyAsync(ctx);
        // [TLD 2026-11-04 9:53/10:02, P3-7] an incomplete body is answered at once (before steps 3-6): nothing to evaluate, nothing written.
        if (read.TimedOut) throw BodyTimeout(ctx, who);
        var body = ParseObject(read.Bytes);
        // Steps 3-4.
        if (loc is not { } location) throw Err(404, "not_found");
        var real = await LocationPartnerAsync(location) ?? throw Err(404, "not_found");
        if (who.IsPartnerAdmin && real != who.Id.PartnerId)
        {
            await DenyAsync(who, "forbidden_scope", real, location, LoginConfigValidator.AttemptedFields(body));
            throw Err(404, "not_found");
        }
        // Step 5.
        var im = ctx.Request.Headers.IfMatch;
        if (im.Count == 0) throw Err(428, "precondition_required");
        if (im.Count != 1 || IfMatchRe.Match(im[0] ?? "") is not { Success: true } m) throw Err(400, "if_match_invalid");
        var expected = long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        var row = await LoadAsync(c, tx, location, who.IsPartnerAdmin ? who.Id.PartnerId!.Value : real, true) ?? throw Err(404, "not_found");
        // Step 6.
        if (row.RowVersion != expected) throw Err(409, "concurrency_conflict", b => b["current_row_version"] = row.RowVersion);
        // Step 7. Oversize = 413, nothing written (rolled back) [TLD 2026-11-04 9:28, P3-2]; steps 3-6 still answer first (P3-3: no change).
        if (read.TooLarge) throw Err(413, "payload_too_large");
        if (body is not { } o) throw Err(400, "malformed_body");
        // Step 8.
        var errs = new List<FieldError>();
        var n = LoginConfigValidator.ValidateWrite(o, "", row.Current, opt, errs, out var bound);
        var tenantBound = bound.Length > 0 && await TenantBoundAsync(c, tx, bound, row.PartnerId);
        if (tenantBound) errs.Add(new("entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        if (errs.Count > 0)
        {
            await tx.RollbackAsync();
            if (tenantBound) await DenyAsync(who, "tenant_bound_elsewhere", row.PartnerId, location, ["entra_allowed_tenant_ids"]);
            throw Validation(errs);
        }
        // Steps 9-10.
        var down = DowngradeFields(row.MfaRequired, row.AllowDbFallback, n!);
        if (down.Count > 0 && who.IsPartnerAdmin)
        {
            await tx.RollbackAsync();
            await DenyAsync(who, "not_super_admin", row.PartnerId, location, down);
            throw Err(403, "not_super_admin");
        }
        if (down.Count > 0 && n!.Reason is null) throw Err(400, "reason_required");

        // Step 11: new state; the server owns oidc_* and secret_ref (TLD 2026-11-04 9:49, D-51). [TLD 2026-11-04 10:02 Q1] deployment
        // config is the source of truth and the row columns are a non-authoritative copy kept only for the DB CHECK (db/ frozen;
        // V11-ROWSECRETCLEANUP): set from config on a switch to entra, cleared on a switch to database, left untouched (never read,
        // never rewritten) on Entra -> Entra. changed_fields names them only on a provider switch.
        var entra = n!.ProviderKind == "entra";
        var wasEntra = row.ProviderKind == "entra";
        var keepCopy = entra && wasEntra;
        var changed = new List<string>();
        if (n.ProviderKind != row.ProviderKind) changed.Add("provider_kind");
        if (n.DisplayName != row.DisplayName) changed.Add("display_name");
        if (!n.Tenants.SequenceEqual(row.Tenants)) changed.Add("entra_allowed_tenant_ids");
        if (entra != wasEntra) { changed.Add("oidc_authority"); changed.Add("oidc_client_id"); changed.Add("secret_ref"); }
        if (n.AllowDbFallback != row.AllowDbFallback) changed.Add("allow_db_fallback");
        if (n.MfaRequired != row.MfaRequired) changed.Add("mfa_required");
        if (changed.Count == 0)
        {
            await tx.RollbackAsync(); // no-op PUT: nothing written, no event (OQ-P3-6).
            return ViewResult(ctx, 200, row, who);
        }

        var now = Now();
        await using (var up = new NpgsqlCommand(
            """
            UPDATE auth.location_login_config SET provider_kind = $3, display_name = $4, entra_allowed_tenant_ids = $5,
                   oidc_authority = CASE WHEN $8 THEN oidc_authority ELSE $6 END, oidc_client_id = CASE WHEN $8 THEN oidc_client_id ELSE $7 END,
                   secret_ref = CASE WHEN $8 THEN secret_ref ELSE $9 END, allow_db_fallback = $10, mfa_required = $11,
                   updated_by = $12, updated_at = $13, row_version = row_version + 1
            WHERE location_id = $1 AND row_version = $2
            """, c, tx))
        {
            up.Parameters.Add(new() { Value = location });
            up.Parameters.Add(new() { Value = expected });
            up.Parameters.Add(new() { Value = n.ProviderKind });
            up.Parameters.Add(new() { Value = n.DisplayName });
            up.Parameters.Add(new() { Value = n.Tenants });
            up.Parameters.Add(new() { Value = entra ? opt.EntraAuthority : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            up.Parameters.Add(new() { Value = entra ? opt.EntraClientId : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            up.Parameters.Add(new() { Value = keepCopy });
            up.Parameters.Add(new() { Value = entra ? opt.EntraSecretRef : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            up.Parameters.Add(new() { Value = n.AllowDbFallback });
            up.Parameters.Add(new() { Value = n.MfaRequired });
            up.Parameters.Add(new() { Value = who.Id.UserId });
            up.Parameters.Add(new() { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            if (await up.ExecuteNonQueryAsync() != 1) throw Err(409, "concurrency_conflict", b => b["current_row_version"] = row.RowVersion);
        }
        var changeId = Guid.CreateVersion7();
        var after = AuditOutbox.State(n.ProviderKind, n.Tenants, n.AllowDbFallback, n.MfaRequired);
        var cf = changed.ToArray();
        await InsertChangeAsync(c, tx, changeId, location, who, n.Reason, now, false, cf, row.State, after, expected + 1);
        await outbox.InsertAsync(c, tx, AuditOutbox.ProviderChanged(Guid.CreateVersion7(), now, who.Id, who.RequestId, row.PartnerId, location, changeId, false,
            expected + 1, cf, row.State, after, n.Reason), CancellationToken.None);
        var saved = await LoadAsync(c, tx, location, row.PartnerId, false) ?? throw new InvalidOperationException("row vanished");
        await tx.CommitAsync();
        return ViewResult(ctx, 200, saved, who);
    });

    // ---------- POST /api/orgs/locations ----------

    const string SlugAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>16 base32 chars from a CSPRNG (80 bits), grouped xxxx-xxxx-xxxx-xxxx (LD L193).</summary>
    public static string NewSlug()
    {
        Span<char> s = stackalloc char[19];
        int j = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0) s[j++] = '-';
            s[j++] = SlugAlphabet[RandomNumberGenerator.GetInt32(SlugAlphabet.Length)];
        }
        return new string(s);
    }

    public Task<IResult> PostAsync(HttpContext ctx) => Run(ctx, async who =>
    {
        // Step 2, decided before the body is read [TLD 2026-11-04 9:04, OQ-P3-8]: a 403 never depends on the body (never 400/413).
        // The refused create is still audited [TLD 2026-11-04 9:04, OQ-P3-2]: location_id null, target partner = the body's partner_id
        // when the body yields a valid one, else target.id / scope.partner_id absent (OQ-P3-10).
        if (!who.IsAdmin || !who.Grant.LoginMethodWrite || !who.Grant.OrgCreateEdit)
        {
            var b0 = ParseObject(await TryReadBodyAsync(ctx));
            var lc0 = b0 is { } o0 && o0.TryGetProperty("login_config", out var l0) ? l0 : (JsonElement?)null;
            await DenyAsync(who, "not_admin", b0 is { } ob ? LoginConfigValidator.BodyPartner(ob) : null, null, LoginConfigValidator.AttemptedFields(lc0));
            throw Err(403, "forbidden");
        }
        var read = await ReadBodyAsync(ctx);
        if (read.TimedOut) throw BodyTimeout(ctx, who); // P3-7, as PUT
        // Steps 3-4 (create: the body's partner_id). Needs the body, so oversize is 413 [TLD 2026-11-04 9:28, P3-2] and a non-object
        // body is 400 here (authorised callers only); neither writes anything.
        if (read.TooLarge) throw Err(413, "payload_too_large");
        if (ParseObject(read.Bytes) is not { } o) throw Err(400, "malformed_body");
        var bodyPartner = LoginConfigValidator.BodyPartner(o);
        if (who.IsPartnerAdmin && bodyPartner is { } bp && bp != who.Id.PartnerId)
        {
            var lc = o.TryGetProperty("login_config", out var l) ? l : (JsonElement?)null;
            await DenyAsync(who, "forbidden_scope", bp, null, LoginConfigValidator.AttemptedFields(lc));
            throw Err(404, "not_found");
        }
        // Step 8.
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await SetScopeAsync(c, tx, who.Id.AdminType, who.Id.PartnerId);
        var errs = new List<FieldError>();
        var n = LoginConfigValidator.ValidateCreate(o, opt, errs, out var bound);
        var tenantBound = bound.Length > 0 && bodyPartner is { } tp && await TenantBoundAsync(c, tx, bound, tp);
        if (tenantBound) errs.Add(new("login_config/entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        if (errs.Count > 0)
        {
            await tx.RollbackAsync();
            if (tenantBound) await DenyAsync(who, "tenant_bound_elsewhere", bodyPartner!.Value, null, ["entra_allowed_tenant_ids"]);
            throw Validation(errs);
        }
        var cfg = n!.Config;
        // Steps 9-10: on create, vs "none" (A-5).
        var down = DowngradeFields(true, false, cfg);
        if (down.Count > 0 && who.IsPartnerAdmin)
        {
            await tx.RollbackAsync();
            await DenyAsync(who, "not_super_admin", n.PartnerId, null, down);
            throw Err(403, "not_super_admin");
        }
        if (down.Count > 0 && cfg.Reason is null) throw Err(400, "reason_required");

        var entra = cfg.ProviderKind == "entra";
        var location = Guid.NewGuid();
        var now = Now();
        await using (var ins = new NpgsqlCommand("INSERT INTO auth.location (location_id, partner_id, slug, name) VALUES ($1, $2, $3, $4)", c, tx))
        {
            ins.Parameters.Add(new() { Value = location });
            ins.Parameters.Add(new() { Value = n.PartnerId });
            ins.Parameters.Add(new() { Value = NewSlug() });
            ins.Parameters.Add(new() { Value = n.Name });
            await ins.ExecuteNonQueryAsync();
        }
        await using (var ins = new NpgsqlCommand(
            """
            INSERT INTO auth.location_login_config (location_id, provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id,
                   secret_ref, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at, row_version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $10, $11, 1)
            """, c, tx))
        {
            ins.Parameters.Add(new() { Value = location });
            ins.Parameters.Add(new() { Value = cfg.ProviderKind });
            ins.Parameters.Add(new() { Value = cfg.DisplayName });
            ins.Parameters.Add(new() { Value = cfg.Tenants });
            ins.Parameters.Add(new() { Value = entra ? opt.EntraAuthority : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            ins.Parameters.Add(new() { Value = entra ? opt.EntraClientId : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            ins.Parameters.Add(new() { Value = entra ? opt.EntraSecretRef : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text }); // TLD 9:49, D-51
            ins.Parameters.Add(new() { Value = cfg.AllowDbFallback });
            ins.Parameters.Add(new() { Value = cfg.MfaRequired });
            ins.Parameters.Add(new() { Value = who.Id.UserId });
            ins.Parameters.Add(new() { Value = now.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            await ins.ExecuteNonQueryAsync();
        }
        // changed_fields on create: vs the column defaults (OQ-P3-7).
        var changed = new List<string> { "provider_kind", "display_name" };
        if (cfg.Tenants.Length > 0) changed.Add("entra_allowed_tenant_ids");
        if (entra) { changed.Add("oidc_authority"); changed.Add("oidc_client_id"); changed.Add("secret_ref"); }
        if (cfg.AllowDbFallback) changed.Add("allow_db_fallback");
        changed.Add("mfa_required");
        var cf = changed.ToArray();
        var changeId = Guid.CreateVersion7();
        var after = AuditOutbox.State(cfg.ProviderKind, cfg.Tenants, cfg.AllowDbFallback, cfg.MfaRequired);
        await InsertChangeAsync(c, tx, changeId, location, who, cfg.Reason, now, true, cf, null, after, 1);
        await outbox.InsertAsync(c, tx, AuditOutbox.ProviderChanged(Guid.CreateVersion7(), now, who.Id, who.RequestId, n.PartnerId, location, changeId, true,
            1, cf, null, after, cfg.Reason), CancellationToken.None);
        var saved = await LoadAsync(c, tx, location, n.PartnerId, false) ?? throw new InvalidOperationException("row vanished");
        await tx.CommitAsync();
        ctx.Response.Headers.Location = $"/api/orgs/locations/{location:D}/login-config";
        return ViewResult(ctx, 201, saved, who);
    });
}
