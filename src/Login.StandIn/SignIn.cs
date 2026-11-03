using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;

namespace Login.StandIn;

public sealed record LocationInfo(string Slug, string LocationId, string PartnerId, string? CachdClientId, string Preset);

// /l/{slug} sign-in, TOTP step, attest-nonce (contract 4.11, 6.1-6.7; ui-driver testids and strings).
public static class SignIn
{
    public const string TxCookie = "standin_tx", SessionCookie = "standin_session", PcCookie = "standin_pc";
    public const string Generic = "Sign-in didn't work.";
    public const int IdleMinutes = 15, CapHours = 12;

    public static LocationInfo? Location(JsonObject fx, string slug) =>
        fx["partners"]!.AsArray().SelectMany(p => p!["locations"]!.AsArray().Select(l => new LocationInfo((string)l!["slug"]!,
            (string)l["location_id"]!, (string)p!["partner_id"]!, (string?)p["cachd_client_id"], (string)p["preset"]!)))
        .FirstOrDefault(l => l.Slug == slug);

    public static JsonObject Scope(LocationInfo l)
    {
        var s = new JsonObject { ["partner_id"] = l.PartnerId, ["location_ids"] = new JsonArray(l.LocationId) };
        if (l.CachdClientId is not null) s["cachd_client_id"] = l.CachdClientId;
        return s;
    }

    // Writes one audit event; false when the outbox fault is set (PUT /_standin/faults outbox=fail).
    public static bool TryEmit(StandInState st, JsonObject e) { if (st.OutboxFault == "fail") return false; st.Emit(e); return true; }

    public static void Headers(HttpContext c, int controlPort)
    {
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers.Pragma = "no-cache";
        c.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'unsafe-inline'; connect-src 'self' http://localhost:{controlPort}; frame-ancestors 'none'";
    }

    static string E(string s) => WebUtility.HtmlEncode(s);
    public static IResult Html(string testid, string body, int status = 200) => Results.Content(
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign in</title></head><body><main data-testid=\"{testid}\">{body}</main></body></html>",
        "text/html; charset=utf-8", null, status);
    static string Err(string? msg) => $"<div data-testid=\"error-area\" role=\"alert\">{(msg is null ? "" : E(msg))}</div>";

    static void SetCookie(HttpContext c, string name, string value, string path) =>
        c.Response.Cookies.Append(name, value, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = path, Secure = c.Request.IsHttps });

    static IResult PasswordPage(HttpContext c, IAntiforgery af, string slugBase, string? error, int agentPort, int status = 200)
    {
        var t = af.GetAndStoreTokens(c).RequestToken!;
        // The page fetches a fresh nonce at submit (6.5), asks the agent with a 3 s timeout, then posts the form.
        var js = $$"""
<script>
document.getElementById('f').addEventListener('submit', async function (ev) {
  if (this.dataset.ready) return; ev.preventDefault(); const f = this;
  try {
    const fd = new FormData(); fd.append('__RequestVerificationToken', f.__RequestVerificationToken.value);
    const n = await (await fetch('{{slugBase}}/attest-nonce', { method: 'POST', body: fd, credentials: 'same-origin' })).json();
    const ctl = new AbortController(); setTimeout(() => ctl.abort(), 3000);
    const r = await fetch('http://localhost:{{agentPort}}/_standin/agent/attest', { method: 'POST', signal: ctl.signal,
      headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ nonce: n.nonce }) });
    if (r.ok) f.device_attestation.value = (await r.json()).device_attestation;
  } catch (e) { }
  f.dataset.ready = '1'; f.submit();
});
</script>
""";
        return Html("screen-signin", Err(error) + $"""
<form id="f" method="post" action="{slugBase}">
<input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<input type="hidden" name="device_attestation" value="">
<label>Username <input data-testid="signin-username" name="username" autocomplete="username"></label>
<label>Password <input data-testid="signin-password" name="password" type="password" autocomplete="current-password"></label>
<button data-testid="signin-submit" type="submit">Sign in</button>
</form>{js}
""", status);
    }

    static IResult TotpPage(HttpContext c, IAntiforgery af, string slugBase, string? error)
    {
        var t = af.GetAndStoreTokens(c).RequestToken!;
        return Html("screen-totp", Err(error) + $"""
<form method="post" action="{slugBase}/totp">
<input type="hidden" name="__RequestVerificationToken" value="{E(t)}">
<label>Code <input data-testid="totp-code" name="totp_code" inputmode="numeric" autocomplete="one-time-code"></label>
<button data-testid="totp-submit" type="submit">Continue</button>
</form>
""");
    }

    public static IResult Hub(StandInState st, LoginSession s)
    {
        var user = st.Users.Values.First(u => u.UserId == s.UserId);
        // Tiles for the apps the user has a role in (PLAN L40). hub_launch issuance (5.7) is not built yet: tiles carry no link.
        var tiles = string.Concat(user.Roles.Select(r => r.AppCode).Distinct().Select(a => $"<div data-testid=\"hub-tile-{E(a)}\">{E(a)}</div>"));
        return Html("screen-hub", tiles);
    }

    static async Task<bool> Af(IAntiforgery af, HttpContext c) { try { await af.ValidateRequestAsync(c); return true; } catch (AntiforgeryValidationException) { return false; } }

    static SignInTx? GetTx(StandInState st, HttpContext c, string slug) =>
        c.Request.Cookies.TryGetValue(TxCookie, out var id) && st.Auth.Tx.TryGetValue(id, out var tx) && tx.Slug == slug ? tx : null;

    public static LoginSession? CurrentSession(StandInState st, HttpContext c, DateTimeOffset now)
    {
        if (!c.Request.Cookies.TryGetValue(SessionCookie, out var sid) || !st.Auth.Sessions.TryGetValue(sid, out var s)) return null;
        if (s.Revoked || now - s.LastSeen > TimeSpan.FromMinutes(IdleMinutes) || now >= s.AuthTime.AddHours(CapHours)) return null;
        return s;
    }

    public static void Map(WebApplication app, StandInState st, string issuerPath, string issuer, StandInPorts ports)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        string Base(string slug) => issuerPath + "/l/" + slug;
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
        string? Ua(HttpContext c) => c.Request.Headers.UserAgent.ToString() is { Length: > 0 } u ? u : null;

        app.MapGet(issuerPath + "/l/{slug}", (string slug, string? ret, HttpContext c) =>
        {
            Headers(c, ports.Control);
            lock (st.Gate)
            {
                if (Location(st.Fixtures, slug) is null) return Results.NotFound();
                var now = st.Clock.Now;
                // A ret is only ever our own authorize URL (no open redirect).
                var tx = new SignInTx { Id = TokenIssuer.NewHandle(), Slug = slug, Nonce = TokenIssuer.NewHandle(), NonceAt = now,
                    PcId = c.Request.Cookies.TryGetValue(PcCookie, out var pc) ? pc : null,
                    ReturnTo = ret is not null && ret.StartsWith(issuerPath + "/connect/authorize?", StringComparison.Ordinal) ? ret : null };
                st.Auth.Tx[tx.Id] = tx;
                SetCookie(c, TxCookie, tx.Id, issuerPath);
                return PasswordPage(c, af, Base(slug), null, ports.Control);
            }
        });

        // 6.5: fresh nonce at submit, invalidates the earlier one; records standin_pc on the transaction (6.8).
        app.MapPost(issuerPath + "/l/{slug}/attest-nonce", async (string slug, HttpContext c) =>
        {
            Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            lock (st.Gate)
            {
                var tx = GetTx(st, c, slug);
                if (tx is null || tx.Stage != "password") return Results.BadRequest();
                tx.Nonce = TokenIssuer.NewHandle(); tx.NonceAt = st.Clock.Now;
                tx.PcId = c.Request.Cookies.TryGetValue(PcCookie, out var pc) ? pc : null;
                return Results.Json(new { nonce = tx.Nonce });
            }
        });

        app.MapPost(issuerPath + "/l/{slug}", async (string slug, HttpContext c) =>
        {
            Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            var f = await c.Request.ReadFormAsync();
            string username = f["username"].ToString(), password = f["password"].ToString(), jws = f["device_attestation"].ToString();
            lock (st.Gate)
            {
                var tx = GetTx(st, c, slug);
                var loc = Location(st.Fixtures, slug);
                if (tx is null || loc is null || tx.Stage != "password") return PasswordPage(c, af, Base(slug), Generic, ports.Control);
                var now = st.Clock.Now;
                // 6.7 step 1 consumes the nonce before anything else, whatever happens next.
                var (ok, remote) = Attestation.Check(st, tx, jws, issuer, "signin", now);
                tx.Attestation = ok ? "valid" : string.IsNullOrEmpty(jws) ? "missing" : "invalid";
                var key = username.Trim().Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant();
                var user = st.Users.Values.FirstOrDefault(u => u.Username.ToLowerInvariant() == key);
                IResult Fail(string reason, string? uid)
                {
                    st.Emit(Audit.Build(now, "auth.login.failed", "failure", uid, new JsonObject
                    { ["username_hmac"] = Audit.UsernameHmac(username), ["reason"] = reason, ["provider_kind"] = "database" }, Scope(loc), Ip(c), Ua(c)));
                    return PasswordPage(c, af, Base(slug), Generic, ports.Control);
                }
                // 6.3 lockout: a locked account is refused without checking the password.
                if (user is not null && user.LockedUntil is { } lu && lu > now) return Fail("locked", user.UserId);
                bool pwOk = user is not null && user.Status == "active" && user.LocationSlugs.Contains(slug) && user.PasswordHistory.Count > 0
                    && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                        StandInUser.Hash(password.Normalize(System.Text.NormalizationForm.FormKC), user.PasswordHistory[0].Salt).Item2, user.PasswordHistory[0].Hash);
                if (!pwOk)
                {
                    if (user is not null && user.LocationSlugs.Contains(slug))
                    {
                        user.FailedPasswords++;
                        int limit = loc.Preset == PasswordPolicy.Nevada ? 3 : 10;
                        if (user.FailedPasswords >= limit)
                        {
                            var mins = (int?)st.Fixtures["lockout_minutes"] ?? 30;
                            user.LockedUntil = now.AddMinutes(mins); user.FailedPasswords = 0;
                            st.Emit(Audit.Build(now, "auth.account.locked", "success", user.UserId, new JsonObject
                            { ["lock_until"] = user.LockedUntil.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }, Scope(loc), Ip(c), Ua(c)));
                        }
                    }
                    return Fail("bad_credentials", null);
                }
                // 6.1: a verified remote_session=true refuses the sign-in (screen 8A); an invalid one is ignored (6.2).
                if (ok && remote)
                {
                    Fail("remote_session", user!.UserId);
                    return Html("screen-8a", "<p>Try again after the remote session ends</p>");
                }
                user!.FailedPasswords = 0;
                // 4.11: the pre-session id is reissued after the password step.
                st.Auth.Tx.Remove(tx.Id);
                tx.Id = TokenIssuer.NewHandle(); tx.Stage = "totp"; tx.Username = user.Username;
                st.Auth.Tx[tx.Id] = tx;
                SetCookie(c, TxCookie, tx.Id, issuerPath);
                return TotpPage(c, af, Base(slug), null);
            }
        });

        app.MapPost(issuerPath + "/l/{slug}/totp", async (string slug, HttpContext c) =>
        {
            Headers(c, ports.Control);
            if (!await Af(af, c)) return Results.BadRequest();
            var code = (await c.Request.ReadFormAsync())["totp_code"].ToString().Trim();
            lock (st.Gate)
            {
                var tx = GetTx(st, c, slug);
                var loc = Location(st.Fixtures, slug);
                if (tx is null || loc is null || tx.Stage != "totp" || !st.Users.TryGetValue(tx.Username!, out var user))
                    return PasswordPage(c, af, Base(slug), Generic, ports.Control);
                var now = st.Clock.Now;
                // G1 +-30 s: one 30 s step either side.
                bool ok = code.Length == 6 && new[] { -30, 0, 30 }.Any(d => Totp.Code(user.TotpSecret, now.AddSeconds(d)) == code);
                if (!ok)
                {
                    st.Emit(Audit.Build(now, "auth.mfa.failed", "failure", user.UserId, new JsonObject { ["method"] = "totp" }, Scope(loc), Ip(c), Ua(c)));
                    st.Emit(Audit.Build(now, "auth.login.failed", "failure", user.UserId, new JsonObject
                    { ["username_hmac"] = Audit.UsernameHmac(user.Username), ["reason"] = "mfa_failed", ["provider_kind"] = "database" }, Scope(loc), Ip(c), Ua(c)));
                    return TotpPage(c, af, Base(slug), Generic);
                }
                // 4.11: a brand-new session id after MFA; any session id the browser had is never promoted.
                var s = new LoginSession { Sid = Guid.NewGuid().ToString(), UserId = user.UserId, Username = user.Username, Slug = slug,
                    LocationId = loc.LocationId, AuthTime = now, LastSeen = now };
                var ev1 = Audit.Build(now, "auth.mfa.succeeded", "success", user.UserId, new JsonObject { ["method"] = "totp" }, Scope(loc), Ip(c), Ua(c), s.Sid);
                var ev2 = Audit.Build(now, "auth.login.succeeded", "success", user.UserId, new JsonObject
                { ["provider_kind"] = "database", ["amr"] = new JsonArray("pwd", "otp", "mfa"), ["auth_time"] = now.ToUnixTimeSeconds(), ["attestation"] = tx.Attestation },
                    Scope(loc), Ip(c), Ua(c), s.Sid);
                ev1["session_kind"] = "normal"; ev2["session_kind"] = "normal";
                // [ASSUMPTION A25] if the outbox is down no session is created (fail closed); the user sees the generic error.
                if (st.OutboxFault == "fail") return PasswordPage(c, af, Base(slug), Generic, ports.Control);
                st.Emit(ev1); st.Emit(ev2);
                st.Auth.Tx.Remove(tx.Id);
                st.Auth.Sessions[s.Sid] = s;
                c.Response.Cookies.Delete(TxCookie, new CookieOptions { Path = issuerPath });
                SetCookie(c, SessionCookie, s.Sid, issuerPath);
                if (tx.ReturnTo is not null) return Results.Redirect(tx.ReturnTo);
                return Hub(st, s);
            }
        });

        // The fake agent (standin-control 2.4). [ASSUMPTION A22] it answers on the control listener at /_standin/agent/attest,
        // because only ports 18080-18082 may be used; the real agent has its own loopback port (v1.1 item AG1).
        app.MapMethods("/_standin/agent/attest", new[] { "OPTIONS" }, (HttpContext c) => { Cors(c, ports.Front); return Results.NoContent(); });
        app.MapPost("/_standin/agent/attest", async (HttpContext c) =>
        {
            Cors(c, ports.Front);
            JsonObject? o; try { o = (await JsonNode.ParseAsync(c.Request.Body))?.AsObject(); } catch (System.Text.Json.JsonException) { o = null; }
            var nonce = (string?)o?["nonce"];
            lock (st.Gate)
            {
                var tx = nonce is null ? null : st.Auth.Tx.Values.FirstOrDefault(t => t.Nonce == nonce);
                var jws = tx is null ? null : Attestation.FakeAgent(st, tx, issuer, st.Clock.Now);
                // "missing": the agent does not answer; the page times out and submits without one.
                return jws is null ? Results.StatusCode(504) : Results.Json(new { device_attestation = jws });
            }
        });
    }

    static void Cors(HttpContext c, int front)
    {
        c.Response.Headers.AccessControlAllowOrigin = $"http://localhost:{front}";
        c.Response.Headers.AccessControlAllowMethods = "POST";
        c.Response.Headers.AccessControlAllowHeaders = "Content-Type";
        c.Response.Headers.CacheControl = "no-store";
    }
}
