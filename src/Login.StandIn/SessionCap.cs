using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;

namespace Login.StandIn;

// contract 7.4 (DEC-086, OQ-40 partial, post-freeze item 4): LoginService:Session:AbsoluteLifetime, default 12:00:00.
// Who may change it: admin_type super | partner only, server-enforced. Interim scope rule: a Partner Admin only for its own partner.
// STANDIN endpoint (contract item 7: endpoint unspecified) -> v1.1 item V11-CAP; not a production design.
public static class SessionCap
{
    public const string Setting = "LoginService:Session:AbsoluteLifetime";
    static readonly Regex Fmt = new(@"^([0-9]+\.)?[0-9]{2}:[0-9]{2}:[0-9]{2}$");
    public static TimeSpan Default { get; private set; } = TimeSpan.FromHours(12);
    // G1 (TL 10:30 PM, interim TL Decision): range is > 15 min (the idle timeout) and <= 24 h. PLACEHOLDER until Mohanraj gives the range (OQ-40).
    // Replaces A61 (unbounded). The 24 h bound also keeps AuthTime + cap far from DateTimeOffset overflow.
    public static readonly TimeSpan RangeMax = TimeSpan.FromHours(24);
    static TimeSpan _global;
    static readonly Dictionary<string, TimeSpan> _partner = new(StringComparer.Ordinal);
    static readonly object G = new();

    // Start-up check (7.4, OQ-SSE2-36): returns the refusal text, or null.
    public static string? Configure(IConfiguration cfg)
    {
        var raw = cfg[Setting] ?? "12:00:00";
        if (!Fmt.IsMatch(raw) || !TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var v)) return $"{Setting} is not a TimeSpan";
        if (v <= TimeSpan.FromMinutes(SignIn.IdleMinutes)) return $"{Setting} must be greater than the {SignIn.IdleMinutes}-minute idle timeout";
        if (v > RangeMax) return $"{Setting} must be at most 24:00:00 (PLACEHOLDER range, OQ-40)";
        Default = v;
        Reset(); return null;
    }
    public static void Reset() { lock (G) { _global = Default; _partner.Clear(); } }
    public static string Text(TimeSpan t) => t.ToString("c", CultureInfo.InvariantCulture);
    static TimeSpan ForPartner(string? pid) { lock (G) return pid is not null && _partner.TryGetValue(pid, out var v) ? v : _global; }
    static string? PartnerOf(StandInState st, LoginSession s) => s.AdminType is not null ? s.PartnerId : SignIn.Location(st.Fixtures, s.Slug)?.PartnerId;
    // [ASSUMPTION A62] the effective cap is read at each check (OQ-40: effect on running sessions is NOT asserted by any test).
    public static TimeSpan For(StandInState st, LoginSession s) => ForPartner(PartnerOf(st, s));
    static bool InRange(TimeSpan v) => v > TimeSpan.FromMinutes(SignIn.IdleMinutes) && v <= RangeMax;
    // G1: expiry math that can never overflow or throw (cap is clamped to the range; AuthTime is a real clock time).
    public static bool Expired(StandInState st, LoginSession s, DateTimeOffset now)
    {
        var cap = For(st, s); if (cap > RangeMax) cap = RangeMax; if (cap < TimeSpan.Zero) cap = TimeSpan.Zero;
        return now - s.AuthTime >= cap;
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    public static void Map(WebApplication app, StandInState st, string issuerPath, StandInPorts ports)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        var P = issuerPath + "/admin/session-cap"; // STANDIN path [ASSUMPTION A63]
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
        IResult Page(HttpContext c, string? pid, string msg, int status)
        {
            var t = af.GetAndStoreTokens(c).RequestToken!;
            var body = $"""
<p data-testid="session-cap-status">{E(msg)}</p>
<p>Current: <code data-testid="session-cap-current">{E(Text(ForPartner(pid)))}</code> <span>STANDIN; range PLACEHOLDER (OQ-40): more than 00:15:00, at most 1.00:00:00</span></p>
<form method="post" action="{P}"><input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<label>New cap (TimeSpan, e.g. 12:00:00) <input data-testid="field-session-cap" name="value"></label>
<label>Partner id <input data-testid="field-session-cap-partner" name="partner_id" value="{E(pid ?? "")}"></label>
<button data-testid="session-cap-submit" type="submit">Save</button></form>
""";
            return SignIn.Html("page-session-cap", body, status);
        }
        app.MapGet(P, (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            lock (st.Gate)
            {
                var s = SignIn.CurrentSession(st, c, st.Clock.Now);
                if (s is null) return Results.StatusCode(401);
                if (s.Kind == "recovery_reenroll") return Results.NotFound();
                s.LastSeen = st.Clock.Now; // activity, like the re-enrol GET
                return Page(c, PartnerOf(st, s), "", 200);
            }
        });
        app.MapPost(P, async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var f = await c.Request.ReadFormAsync();
            var raw = f["value"].ToString().Trim(); var want = f["partner_id"].ToString().Trim();
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                var s = SignIn.CurrentSession(st, c, now);
                if (s is null) return Results.StatusCode(401);             // [ASSUMPTION A64] no session: no actor, so 401 and no event
                if (s.Kind == "recovery_reenroll") return Results.NotFound(); // 7.8: nothing but re-enrolment
                s.LastSeen = now;
                // G9 (TL 10:30 PM): WHO first, then the value. A non-admin is refused (403 + change_denied not_admin) whatever it sent.
                bool fmt = Fmt.IsMatch(raw);
                bool parsed = fmt && TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out _); TimeSpan v = parsed ? TimeSpan.Parse(raw, CultureInfo.InvariantCulture) : TimeSpan.Zero;
                // [ASSUMPTION A69] attempted_value must be TimeSpan text (schema pattern): pattern-shaped text is logged as sent; anything else is
                // logged as "00:00:00" (a value that is itself out of range), because the schema forbids free text there.
                string attempted = fmt ? raw : "00:00:00";
                string? reason = null; string? target = null;
                if (s.AdminType is not ("super" or "partner")) reason = "not_admin";
                else
                {
                    // [ASSUMPTION A65, narrowed by G9] an ADMIN sending non-TimeSpan text (malformed, negative) gets 400 with no event.
                    if (!fmt) return Page(c, PartnerOf(st, s), "Not a TimeSpan", 400);
                    var partners = st.Fixtures["partners"]!.AsArray().Select(p => (string)p!["partner_id"]!).ToHashSet();
                    // Target: super -> partner_id given = that partner, empty = global default; partner -> empty = own partner.
                    target = want.Length > 0 ? want : (s.AdminType == "partner" ? s.PartnerId : null);
                    if (target is not null && !partners.Contains(target) && s.AdminType == "super") return Page(c, null, "Unknown partner", 400); // [ASSUMPTION A66]
                    // G1: pattern-shaped but not representable (huge) counts as out of range, never 500.
                    reason = s.AdminType == "partner" && target != s.PartnerId ? "wrong_partner" : !parsed || !InRange(v) ? "out_of_range" : null;
                }
                if (reason is not null)
                {
                    var d = Audit.Build(now, "auth.config.change_denied", "denied", s.UserId,
                        new JsonObject { ["setting"] = Setting, ["attempted_value"] = attempted, ["reason"] = reason }, Handoff.SessionScope(st, s), Ip(c), null, s.Sid);
                    d["target"] = new JsonObject { ["type"] = "config", ["id"] = Setting };
                    if (reason != "not_admin") d["admin_type"] = s.AdminType;
                    if (!SignIn.TryEmit(st, d)) return Page(c, target, "Not saved", 503);
                    return Page(c, PartnerOf(st, s), "Not allowed", 403);
                }
                var old = ForPartner(target);
                var e = Audit.Build(now, "auth.config.session_cap_changed", "success", s.UserId,
                    new JsonObject { ["old_value"] = Text(old), ["new_value"] = Text(v) }, Handoff.SessionScope(st, s), Ip(c), null, s.Sid);
                e["target"] = new JsonObject { ["type"] = "config", ["id"] = Setting }; e["admin_type"] = s.AdminType;
                if (!SignIn.TryEmit(st, e)) return Page(c, target, "Not saved", 503); // fail closed: no change without its audit event
                lock (G) { if (target is null) _global = v; else _partner[target] = v; }
                return Page(c, target, "Saved", 200);
            }
        });
    }
}

// contract 6.9 BFF cage stub, refusal part only (STANDIN, mounted under the issuer path so the stand-in session cookie applies) [ASSUMPTION A59].
// DEC-084: any admin_type session -> 403 {"error":"access_denied"} on the nonce and every action, checked before attestation;
// DEC-085: recovery_reenroll the same. The reason goes to the stub's own log only (G2; the Cash Advance audit is outside this contract).
// A60 withdrawn (TL 10:08, BP-2a): location actions check attestation first; G2 (TL 10:30): valid -> 200 {"ok":true}.
public static class CageStub
{
    public static void Map(WebApplication app, StandInState st, string issuerPath, StandInPorts ports)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        IResult? Refuse(HttpContext c, string what)
        {
            var s = SignIn.CurrentSession(st, c, st.Clock.Now);
            if (s is null) return Results.Json(new { error = "access_denied" }, statusCode: 401);
            string? reason = s.AdminType is not null ? "not_location_user" : s.Kind == "recovery_reenroll" ? "recovery_session" : null;
            if (reason is null) return null;
            Console.WriteLine($"cage_stub refused {what} reason={reason} admin_type={s.AdminType ?? "-"}");
            return Results.Json(new { error = "access_denied" }, statusCode: 403);
        }
        app.MapPost(issuerPath + "/cage/attest-nonce", async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            lock (st.Gate)
            {
                var r = Refuse(c, "attest-nonce"); if (r is not null) return r;
                var s = SignIn.CurrentSession(st, c, st.Clock.Now)!;
                // G4: base64url 128-bit = 22 chars (cage_nonce_response). BP-2a (TL 10:08): a server-stored, single-use nonce bound to this session's location and standin_pc (6.7, 6.8).
                c.Request.Cookies.TryGetValue(SignIn.PcCookie, out var pcid);
                var tx = new SignInTx { Id = TokenIssuer.NewHandle(), Slug = s.Slug, Purpose = "cage_action", Stage = "cage", Sid = s.Sid,
                    Nonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), NonceAt = st.Clock.Now, PcId = pcid };
                st.Auth.Tx[tx.Id] = tx;
                c.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { nonce = tx.Nonce });
            }
        });
        app.MapPost(issuerPath + "/cage/actions/{action}", async (string action, HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (action is not ("payment" or "id_scan" or "print")) return Results.NotFound();
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            // G2 (TL 10:30): 6.9 body exactly: JSON {"device_attestation":"<JWS>"} (cage_action_request, closed); antiforgery in the
            // RequestVerificationToken header. [ASSUMPTION A73] any other body (form, extra keys, not JSON) = attestation missing -> 403.
            string? jws = null;
            if (c.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    if ((await System.Text.Json.Nodes.JsonNode.ParseAsync(c.Request.Body)) is JsonObject o && o.Count == 1 && o["device_attestation"] is JsonValue jv && jv.TryGetValue(out string? js)) jws = js;
                }
                catch (System.Text.Json.JsonException) { jws = null; }
            }
            lock (st.Gate)
            {
                var r = Refuse(c, "action " + action); if (r is not null) return r;
                var now = st.Clock.Now; var s = SignIn.CurrentSession(st, c, now)!;
                // BP-2a / 6.7 (TL 10:08, replaces A60): attestation FIRST, purpose cage_action, aud = the Cash Advance server [ASSUMPTION A68].
                // The JWS names its nonce; take the matching cage tx of THIS session (consumed on every outcome by Check).
                string? n = null;
                try { if (jws?.Split('.') is { Length: 3 } pp) n = (string?)System.Text.Json.Nodes.JsonNode.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(pp[1]))?["nonce"]; }
                catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or ArgumentException) { n = null; }
                var tx = st.Auth.Tx.Values.FirstOrDefault(t => t.Purpose == "cage_action" && t.Sid == s.Sid && t.Nonce is not null && t.Nonce == n)
                      ?? st.Auth.Tx.Values.FirstOrDefault(t => t.Purpose == "cage_action" && t.Sid == s.Sid && t.Nonce is not null);
                var (ok, remote) = tx is not null ? Attestation.Check(st, tx, jws, Attestation.CageAud, "cage_action", now) : (false, false);
                // G3 BLOCKER (TL 10:30): a valid attestation reporting remote_session=true never gets a cage action.
                // Client: 403 attestation_invalid (same body); audit reason remote_session [ASSUMPTION A70]. v1.1 (SA): 6.7 to say so.
                string? why = !ok ? "attestation_invalid" : remote ? "remote_session" : null;
                if (why is not null)
                {
                    var e = Audit.Build(now, "auth.app_access.denied", "denied", s.UserId, new JsonObject { ["reason"] = why },
                        Handoff.SessionScope(st, s), c.Connection.RemoteIpAddress?.ToString(), null, s.Sid);
                    e["target"] = new JsonObject { ["type"] = "app", ["id"] = "cash_advance" }; e["session_kind"] = s.Kind;
                    st.Emit(e);
                    Console.WriteLine($"cage_stub refused action {action} reason={why} attestation={(ok ? "valid" : string.IsNullOrEmpty(jws) ? "missing" : "invalid")}");
                    return Results.Json(new { error = "attestation_invalid" }, statusCode: 403);
                }
                return Results.Json(new { ok = true }); // G2: 6.9 cage_action_ok; the stub performs no real action
            }
        });
    }
}
