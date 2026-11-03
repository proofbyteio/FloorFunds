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
    // PLACEHOLDER range (OQ-40 (b) open, no values proposed): optional StandIn:SessionCapRange:Min / :Max; unset = no bound.
    // The only built-in bound is the technical check (cap > 15-min idle timeout), not policy. [ASSUMPTION A61]
    public static TimeSpan? RangeMin { get; private set; }
    public static TimeSpan? RangeMax { get; private set; }
    static TimeSpan _global;
    static readonly Dictionary<string, TimeSpan> _partner = new(StringComparer.Ordinal);
    static readonly object G = new();

    // Start-up check (7.4, OQ-SSE2-36): returns the refusal text, or null.
    public static string? Configure(IConfiguration cfg)
    {
        var raw = cfg[Setting] ?? "12:00:00";
        if (!Fmt.IsMatch(raw) || !TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var v)) return $"{Setting} is not a TimeSpan";
        if (v <= TimeSpan.FromMinutes(SignIn.IdleMinutes)) return $"{Setting} must be greater than the {SignIn.IdleMinutes}-minute idle timeout";
        Default = v;
        if (TimeSpan.TryParse(cfg["StandIn:SessionCapRange:Min"], CultureInfo.InvariantCulture, out var mn)) RangeMin = mn;
        if (TimeSpan.TryParse(cfg["StandIn:SessionCapRange:Max"], CultureInfo.InvariantCulture, out var mx)) RangeMax = mx;
        Reset(); return null;
    }
    public static void Reset() { lock (G) { _global = Default; _partner.Clear(); } }
    public static string Text(TimeSpan t) => t.ToString("c", CultureInfo.InvariantCulture);
    static TimeSpan ForPartner(string? pid) { lock (G) return pid is not null && _partner.TryGetValue(pid, out var v) ? v : _global; }
    static string? PartnerOf(StandInState st, LoginSession s) => s.AdminType is not null ? s.PartnerId : SignIn.Location(st.Fixtures, s.Slug)?.PartnerId;
    // [ASSUMPTION A62] the effective cap is read at each check (OQ-40: effect on running sessions is NOT asserted by any test).
    public static TimeSpan For(StandInState st, LoginSession s) => ForPartner(PartnerOf(st, s));
    static bool InRange(TimeSpan v) => v > TimeSpan.FromMinutes(SignIn.IdleMinutes) && (RangeMin is null || v >= RangeMin) && (RangeMax is null || v <= RangeMax);

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
<p>Current: <code data-testid="session-cap-current">{E(Text(ForPartner(pid)))}</code> <span>STANDIN; range PLACEHOLDER (OQ-40)</span></p>
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
                // [ASSUMPTION A65] a value that is not TimeSpan text (schema pattern) is 400 with no event: attempted_value must match the pattern.
                if (!Fmt.IsMatch(raw) || !TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var v)) return Page(c, PartnerOf(st, s), "Not a TimeSpan", 400);
                var partners = st.Fixtures["partners"]!.AsArray().Select(p => (string)p!["partner_id"]!).ToHashSet();
                // Target: super -> partner_id given = that partner, empty = global default; partner -> empty = own partner.
                string? target = want.Length > 0 ? want : (s.AdminType == "partner" ? s.PartnerId : null);
                if (target is not null && !partners.Contains(target) && s.AdminType == "super") return Page(c, null, "Unknown partner", 400); // [ASSUMPTION A66]
                string? reason = s.AdminType is not ("super" or "partner") ? "not_admin"
                    : s.AdminType == "partner" && target != s.PartnerId ? "wrong_partner"
                    : !InRange(v) ? "out_of_range" : null;
                if (reason is not null)
                {
                    var d = Audit.Build(now, "auth.config.change_denied", "denied", s.UserId,
                        new JsonObject { ["setting"] = Setting, ["attempted_value"] = Text(v), ["reason"] = reason }, Handoff.SessionScope(st, s), Ip(c), null, s.Sid);
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
// [ASSUMPTION A60] attestation checking for location users is not built here (SSE2 / Cash Advance BFF stub): nonce issued, action -> 501.
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
            lock (st.Gate) return Refuse(c, "attest-nonce") ?? Results.Json(new { nonce = TokenIssuer.NewHandle() });
        });
        app.MapPost(issuerPath + "/cage/actions/{action}", async (string action, HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (action is not ("payment" or "id_scan" or "print")) return Results.NotFound();
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            lock (st.Gate) return Refuse(c, "action " + action) ?? Results.Json(new { error = "not_implemented" }, statusCode: 501);
        });
    }
}
