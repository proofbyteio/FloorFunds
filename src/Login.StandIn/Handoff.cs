using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.IdentityModel.Tokens;

namespace Login.StandIn;

// contract 5.1-5.13: hub_launch issuance (5.7), the Cash Advance hand-off rules, the interstitial (5.8),
// and a minimal /admin sign-in (UX 05 ids) so the 5.12 / 5.13 refusals can be shown live.
public static class Handoff
{
    public const string TxCookie = "standin_hx";
    // [ASSUMPTION A42] ages are compared in whole elapsed seconds (truncated), like the integer auth_time claim: 300 s passes, 301 s fails.
    // A42 (TL 9:32, changed): compare in milliseconds; refuse anything over the limit, no rounding down (300.4 s is refused).
    public const long AuthAgeMs = 300_000, LaunchMs = 60_000;
    public static bool Over(DateTimeOffset now, DateTimeOffset t, long limitMs) => (now - t).Ticks > limitMs * TimeSpan.TicksPerMillisecond;
    // Boundary self-test (the frozen clock control only advances in whole seconds, so exact-ms cases are checked here at startup).
    public static void SelfTest()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        bool ok = !Over(t0.AddMilliseconds(300_000), t0, AuthAgeMs) && Over(t0.AddMilliseconds(300_001), t0, AuthAgeMs)
               && Over(t0.AddMilliseconds(300_400), t0, AuthAgeMs) && Over(t0.AddTicks(300_000 * TimeSpan.TicksPerMillisecond + 1), t0, AuthAgeMs)
               && !Over(t0.AddMilliseconds(60_000), t0, LaunchMs) && Over(t0.AddMilliseconds(60_001), t0, LaunchMs);
        Console.WriteLine($"A42 boundary selftest: 300000ms=accept 300001ms=refuse 300400ms=refuse 60000ms=accept 60001ms=refuse -> {(ok ? "PASS" : "FAIL")}");
        if (!ok) throw new InvalidOperationException("A42 boundary selftest failed");
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    // Audit scope for any session: an /admin session has no location (location_ids: [] is allowed only with admin_type).
    public static JsonObject SessionScope(StandInState st, LoginSession s) =>
        s.AdminType is not null ? new JsonObject { ["partner_id"] = s.PartnerId, ["location_ids"] = new JsonArray() } : SignIn.Scope(SignIn.Location(st.Fixtures, s.Slug)!);

    // Rules 1, 2, 3, 5, 6 of 5.1 (rule 4 is checked by the caller). Returns the failed rule, or null.
    public static string? RuleFailure(StandInState st, LoginSession s, StandInUser user, OAuthClient client, DateTimeOffset now)
    {
        // [TL 9:26 PM, A32] auth_time is set only by a real credential entry (password + MFA), never by SSO reuse or refresh.
        if (!s.Interactive || Over(now, s.AuthTime, AuthAgeMs)) return "rule1";
        if (!s.Amr.Contains("mfa")) return "rule2"; // Entra mfa/ngcmfa map to mfa at sign-in; recovery [pwd] never counts
        if (s.HandoffUsed) return "rule3";
        // Rule 5: the user still holds the session's location and is active. The IP allowlist is enforced on every front request (Allowlist.cs).
        if (user.Status != "active" || !user.LocationSlugs.Contains(s.Slug) || SignIn.Location(st.Fixtures, s.Slug) is null) return "rule5";
        if (s.CageConsumed) return "rule6";
        return null;
    }

    // 5.7: one opaque 128-bit CSPRNG value per hand-off tile view, base64url, stored as SHA-256 only, bound to sid + client_id.
    public static string Tiles(StandInState st, LoginSession s, StandInUser user, DateTimeOffset now)
    {
        if (s.Kind == "recovery_reenroll" || s.AdminType is not null) return ""; // errors.md: a recovery hub shows no hub_launch
        var sb = new System.Text.StringBuilder();
        foreach (var app in user.Roles.Select(r => r.AppCode).Distinct())
        {
            var hc = st.OAuthClients.Values.FirstOrDefault(c => c.Handoff && c.AppCode == app && c.StartUrl is not null);
            if (hc is null) { sb.Append($"<div data-testid=\"hub-tile-{E(app)}\">{E(app)}</div>"); continue; }
            var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
            st.Auth.HubLaunches[AuthStore.H(raw)] = new HubLaunch(s.Sid, hc.ClientId, now);
            sb.Append($"<a data-testid=\"hub-tile-{E(app)}\" href=\"{E(hc.StartUrl + "?hub_launch=" + raw)}\">{E(app)}</a>");
        }
        return sb.ToString();
    }

    public static IResult Interstitial(HttpContext c, IAntiforgery af, SignInTx tx, string issuerPath, int agentPort)
    {
        c.Response.Cookies.Append(TxCookie, tx.Id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = issuerPath, Secure = c.Request.IsHttps });
        var t = af.GetAndStoreTokens(c).RequestToken!;
        // [ASSUMPTION A38] ui-driver has no id for this page; the form id is "handoff" and the page has no screen testid (v1.1 UI item).

        return Ui.Page("Opening Cash Advance", Ui.Render("handoff", ("action", issuerPath + "/connect/authorize"), ("agent", $"http://localhost:{agentPort}/_standin/agent/attest"), ("af", t)), "handoff");
    }

    public static void Map(WebApplication app, StandInState st, string issuerPath, string issuer, StandInPorts ports)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
        static async Task<bool> Af(IAntiforgery af, HttpContext c) { try { await af.ValidateRequestAsync(c); return true; } catch (AntiforgeryValidationException) { return false; } }

        app.MapPost(issuerPath + "/connect/authorize/handoff-nonce", async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            lock (st.Gate)
            {
                if (!c.Request.Cookies.TryGetValue(TxCookie, out var id) || !st.Auth.Tx.TryGetValue(id, out var tx) || tx.Purpose != "handoff") return Results.BadRequest();
                tx.Nonce = TokenIssuer.NewHandle(); tx.NonceAt = st.Clock.Now;
                tx.PcId = c.Request.Cookies.TryGetValue(SignIn.PcCookie, out var pc) ? pc : null;
                return Results.Json(new { nonce = tx.Nonce });
            }
        });

        // The hub (PLAN L40) for the current session; each view issues fresh hub_launch values (2.8: tests read them from the tile).
        app.MapGet(issuerPath + "/apps", (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            lock (st.Gate)
            {
                var s = SignIn.CurrentSession(st, c, st.Clock.Now);
                if (s is null) return Results.Redirect(issuerPath + "/l/" + (string)st.Fixtures["partners"]![0]!["locations"]![0]!["slug"]!);
                return SignIn.Hub(st, s);
            }
        });

        // Minimal /admin sign-in (UX 05 ids): password, then TOTP or a recovery code. [ASSUMPTION A33] no lockout, attestation or
        // re-enrolment flow; a recovery code makes a recovery_reenroll session with amr [pwd] (5.13). Not a full /admin build.
        string Page(string? err, int step, string t) => $"""
<div data-testid="error-admin-sign-in" role="alert">{(err is null ? "" : E(err))}</div>
""" + (step == 1 ? $"""
<form method="post" action="{issuerPath}/admin"><input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<label>Username <input data-testid="field-admin-username" name="username"></label>
<label>Password <input data-testid="field-admin-password" name="password" type="password"></label>
<button data-testid="admin-signin-submit" type="submit">Sign in</button></form>
""" : $"""
<form method="post" action="{issuerPath}/admin/mfa"><input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<label>Code <input data-testid="field-admin-totp" name="totp_code" inputmode="numeric"></label>
<button data-testid="admin-totp-submit" type="submit">Continue</button></form>
<form method="post" action="{issuerPath}/admin/mfa"><input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<label>Recovery code <input data-testid="field-admin-recovery-code" name="recovery_code"></label>
<button data-testid="admin-recovery-submit" type="submit">Use recovery code</button></form>
""");
        IResult Admin(HttpContext c, string? err, int step) => SignIn.Html("page-admin-sign-in", Page(err, step, af.GetAndStoreTokens(c).RequestToken!));
        void Cookie(HttpContext c, string n, string v) => c.Response.Cookies.Append(n, v, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = issuerPath, Secure = c.Request.IsHttps });

        app.MapGet(issuerPath + "/admin", (HttpContext c) => { SignIn.Headers(c, ports.Control); lock (st.Gate) return Admin(c, null, 1); });
        app.MapPost(issuerPath + "/admin", async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            var f = await c.Request.ReadFormAsync();
            lock (st.Gate)
            {
                var key = f["username"].ToString().Trim().ToLowerInvariant();
                var u = st.Users.Values.FirstOrDefault(x => x.Username.ToLowerInvariant() == key && x.AdminType is not null);
                bool ok = u is not null && u.Status == "active" && u.PasswordHistory.Count > 0 && CryptographicOperations.FixedTimeEquals(
                    StandInUser.Hash(f["password"].ToString().Normalize(System.Text.NormalizationForm.FormKC), u.PasswordHistory[0].Salt).Item2, u.PasswordHistory[0].Hash);
                if (!ok) return Admin(c, SignIn.Generic, 1);
                var tx = new SignInTx { Id = TokenIssuer.NewHandle(), Slug = "", Purpose = "admin", Stage = "mfa", Username = u!.Username, NonceAt = st.Clock.Now };
                st.Auth.Tx[tx.Id] = tx; Cookie(c, SignIn.TxCookie, tx.Id);
                return Admin(c, null, 2);
            }
        });
        app.MapPost(issuerPath + "/admin/mfa", async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            var f = await c.Request.ReadFormAsync();
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                if (!c.Request.Cookies.TryGetValue(SignIn.TxCookie, out var id) || !st.Auth.Tx.TryGetValue(id, out var tx) || tx.Purpose != "admin"
                    || !st.Users.TryGetValue(tx.Username!, out var u)) return Admin(c, SignIn.Generic, 1);
                var totp = f["totp_code"].ToString().Trim(); var rc = f["recovery_code"].ToString().Trim();
                string kind; string[] amr;
                if (totp.Length == 6 && new[] { -30, 0, 30 }.Any(d => Totp.Code(u.TotpSecret, now.AddSeconds(d)) == totp)) { kind = "normal"; amr = new[] { "pwd", "otp", "mfa" }; }
                else if (rc.Length > 0 && u.RecoveryCodeHashes.Remove(AuthStore.H(rc))) { kind = "recovery_reenroll"; amr = new[] { "pwd" }; }
                else return Admin(c, SignIn.Generic, 2);
                st.Auth.Tx.Remove(tx.Id);
                var s = new LoginSession { Sid = Guid.NewGuid().ToString(), UserId = u.UserId, Username = u.Username, Slug = "", LocationId = "", AuthTime = now, LastSeen = now,
                    Amr = amr, Kind = kind, AdminType = u.AdminType, PartnerId = u.PartnerId };
                var ev = Audit.Build(now, "auth.login.succeeded", "success", u.UserId, new JsonObject
                { ["provider_kind"] = "database", ["amr"] = new JsonArray(amr.Select(a => (JsonNode)a!).ToArray()), ["auth_time"] = now.ToUnixTimeSeconds() },
                    SessionScope(st, s), Ip(c), null, s.Sid);
                ev["session_kind"] = kind; ev["admin_type"] = u.AdminType;
                if (!SignIn.TryEmit(st, ev)) return Admin(c, SignIn.Generic, 1);
                st.Auth.Sessions[s.Sid] = s;
                c.Response.Cookies.Delete(SignIn.TxCookie, new CookieOptions { Path = issuerPath });
                Cookie(c, SignIn.SessionCookie, s.Sid);
                return SignIn.Hub(st, s);
            }
        });
    }
}
