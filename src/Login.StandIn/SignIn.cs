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
    public static string IssuerPath { get; private set; } = "/standin";

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
        c.Response.Headers.ContentSecurityPolicy = Ui.Csp(controlPort);
    }

    static string E(string s) => WebUtility.HtmlEncode(s);
    // Generic ui/ card (hub, admin, sign-out confirm): layout.html + card.html.
    public static IResult Html(string testid, string body, int status = 200) =>
        Ui.Page("Sign in", Ui.Render("card", ("testid", testid), ("body", body)), null, status);

    // OQ-11 (TL 9:42): an unknown user still runs one PBKDF2 hash against this dummy, so timing matches a known user.
    public static readonly (byte[] Salt, byte[] Hash) DummyCred = StandInUser.Hash(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
    public static bool PasswordMatches(StandInUser? u, string password)
    {
        var (salt, hash) = u is { PasswordHistory.Count: > 0 } ? u.PasswordHistory[0] : DummyCred;
        var ok = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            StandInUser.Hash(password.Normalize(System.Text.NormalizationForm.FormKC), salt).Item2, hash);
        return ok && u is { PasswordHistory.Count: > 0 };
    }
    public static bool Locked(StandInUser u, DateTimeOffset now) => u.LockedUntil is { } lu && lu > now;
    // 6.3 + SEC77 (TL 9:42): one per-user failure counter for wrong passwords and wrong TOTP codes; partner preset limit (Nevada 3, PCI 10).
    public static int Limit(StandInState st, StandInUser u, string? slug)
    {
        string? preset = slug is { Length: > 0 } ? Location(st.Fixtures, slug)?.Preset
            : st.Fixtures["partners"]!.AsArray().FirstOrDefault(p => (string?)p!["partner_id"] == u.PartnerId)?["preset"]?.GetValue<string>();
        // [ASSUMPTION A54] /admin: the partner's preset; a super admin (no partner) gets the stricter limit, 3.
        return preset == PasswordPolicy.Pci ? 10 : 3;
    }
    public static void CountFailure(StandInState st, StandInUser u, int limit, DateTimeOffset now, JsonObject scope, string? ip, string? ua)
    {
        u.FailedPasswords++;
        if (u.FailedPasswords < limit) return;
        var mins = (int?)st.Fixtures["lockout_minutes"] ?? 30;
        u.LockedUntil = now.AddMinutes(mins); u.FailedPasswords = 0;
        var e = Audit.Build(now, "auth.account.locked", "success", u.UserId, new JsonObject
        { ["lock_until"] = u.LockedUntil.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") }, scope, ip, ua);
        if (u.AdminType is not null) e["admin_type"] = u.AdminType;
        st.Emit(e);
    }

    static void SetCookie(HttpContext c, string name, string value, string path) =>
        c.Response.Cookies.Append(name, value, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = path, Secure = c.Request.IsHttps });

    static IResult PasswordPage(HttpContext c, IAntiforgery af, string slugBase, string? error, int agentPort, int status = 200, bool resign = false, bool agentDown = false)
    {
        var t = af.GetAndStoreTokens(c).RequestToken!;
        // The page fetches a fresh nonce at submit (6.5), asks the agent with a 3 s timeout, then posts the form (ui/login-hub/js/signin.js).
        var agent = $"http://localhost:{agentPort}/_standin/agent/attest";
        if (resign) // 5.3 re-prompt (UX 07 #u2/#p2). [ASSUMPTION A44] after attestation_invalid (5.5) it is wrapped in screen 8B (UX 08 agentdown).
        {
            var r = Ui.Render("resign-in", ("action", slugBase), ("agent", agent), ("af", t), ("error", error is null ? "" : Ui.Error(error)));
            return Ui.Page("Sign in to Cash Advance", agentDown ? Ui.Render("screen-8b", ("resign", r)) : r, "signin", status);
        }
        return Ui.Page("Sign in", Ui.Render("signin", ("action", slugBase), ("agent", agent), ("af", t), ("error", Ui.Error(error))), "signin", status);
    }

    static IResult TotpPage(HttpContext c, IAntiforgery af, string slugBase, string? error)
    {
        var t = af.GetAndStoreTokens(c).RequestToken!;
        return Ui.Page("Enter your one-time code", Ui.Render("totp", ("action", slugBase + "/totp"), ("af", t), ("error", Ui.Error(error))));
    }

    public static IResult Hub(StandInState st, LoginSession s)
    {
        var user = st.Users.Values.First(u => u.UserId == s.UserId);
        // Tiles for the apps the user has a role in (PLAN L40); hand-off tiles carry start_url?hub_launch= (5.7).
        // errors.md: a recovery session's hub offers only re-enrolment and sign-out, no hub_launch. [ASSUMPTION A36] re-enrolment itself is not built.
        if (s.Kind == "recovery_reenroll") return Html("screen-hub", $"<p>Set up your authenticator again</p><a href=\"{IssuerPath}/connect/endsession\">Sign out</a>");
        return Html("screen-hub", Handoff.Tiles(st, s, user, st.Clock.Now));
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
        IssuerPath = issuerPath;
        var af = app.Services.GetRequiredService<IAntiforgery>();
        string Base(string slug) => issuerPath + "/l/" + slug;
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
        string? Ua(HttpContext c) => c.Request.Headers.UserAgent.ToString() is { Length: > 0 } u ? u : null;

        app.MapGet(issuerPath + "/l/{slug}", (string slug, string? ret, string? resign, string? agent, HttpContext c) =>
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
                return PasswordPage(c, af, Base(slug), null, ports.Control, resign: tx.Resign = resign == "1" && tx.ReturnTo is not null, agentDown: tx.AgentDown = agent == "down" && tx.Resign);
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
                if (tx is null || loc is null || tx.Stage != "password") return PasswordPage(c, af, Base(slug), Generic, ports.Control, resign: tx?.Resign == true, agentDown: tx?.AgentDown == true);
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
                    return PasswordPage(c, af, Base(slug), Generic, ports.Control, resign: tx?.Resign == true, agentDown: tx?.AgentDown == true);
                }
                // OQ-11: the hash always runs (dummy for an unknown user), before the lockout check too.
                bool hashOk = PasswordMatches(user, password);
                // 6.3 lockout: a locked account is refused whatever the password.
                if (user is not null && Locked(user, now)) return Fail("locked", user.UserId);
                bool pwOk = hashOk && user!.Status == "active" && user.LocationSlugs.Contains(slug);
                if (!pwOk)
                {
                    if (user is not null && user.LocationSlugs.Contains(slug)) CountFailure(st, user, Limit(st, user, slug), now, Scope(loc), Ip(c), Ua(c));
                    return Fail("bad_credentials", null);
                }
                // 6.1: a verified remote_session=true refuses the sign-in (screen 8A); an invalid one is ignored (6.2).
                if (ok && remote)
                {
                    Fail("remote_session", user!.UserId);
                    return Ui.Page("Sign-in is blocked", Ui.Render("screen-8a"));
                }
                // [ASSUMPTION A53] the failure counter is NOT reset by a right password alone (else password+wrong-TOTP never locks); a full sign-in resets it.
                // 4.11: the pre-session id is reissued after the password step.
                st.Auth.Tx.Remove(tx.Id);
                tx.Id = TokenIssuer.NewHandle(); tx.Stage = "totp"; tx.Username = user!.Username;
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
                    return PasswordPage(c, af, Base(slug), Generic, ports.Control, resign: tx?.Resign == true, agentDown: tx?.AgentDown == true);
                var now = st.Clock.Now;
                // G1 +-30 s: one 30 s step either side.
                // SEC77: once locked, even the right code is refused (and its step is not spent).
                if (Locked(user, now))
                {
                    st.Emit(Audit.Build(now, "auth.login.failed", "failure", user.UserId, new JsonObject
                    { ["username_hmac"] = Audit.UsernameHmac(user.Username), ["reason"] = "locked", ["provider_kind"] = "database" }, Scope(loc), Ip(c), Ua(c)));
                    return TotpPage(c, af, Base(slug), Generic);
                }
                // G1 +-30 s; SEC76: a step works once per user (replay refused).
                bool ok = Totp.Accept(user, code, now);
                if (!ok)
                {
                    CountFailure(st, user, Limit(st, user, slug), now, Scope(loc), Ip(c), Ua(c));
                    st.Emit(Audit.Build(now, "auth.mfa.failed", "failure", user.UserId, new JsonObject { ["method"] = "totp" }, Scope(loc), Ip(c), Ua(c)));
                    st.Emit(Audit.Build(now, "auth.login.failed", "failure", user.UserId, new JsonObject
                    { ["username_hmac"] = Audit.UsernameHmac(user.Username), ["reason"] = "mfa_failed", ["provider_kind"] = "database" }, Scope(loc), Ip(c), Ua(c)));
                    return TotpPage(c, af, Base(slug), Generic);
                }
                // 4.11: a brand-new session id after MFA; any session id the browser had is never promoted.
                var s = new LoginSession { Sid = Guid.NewGuid().ToString(), UserId = user.UserId, Username = user.Username, Slug = slug,
                    LocationId = loc.LocationId, AuthTime = now, LastSeen = now, ViaReturn = tx.ReturnTo is not null,
                    Amr = user.FixtureAmr ?? new[] { "pwd", "otp", "mfa" } };
                var ev1 = Audit.Build(now, "auth.mfa.succeeded", "success", user.UserId, new JsonObject { ["method"] = "totp" }, Scope(loc), Ip(c), Ua(c), s.Sid);
                var ev2 = Audit.Build(now, "auth.login.succeeded", "success", user.UserId, new JsonObject
                { ["provider_kind"] = "database", ["amr"] = new JsonArray(s.Amr.Select(a => (JsonNode)a!).ToArray()), ["auth_time"] = now.ToUnixTimeSeconds(), ["attestation"] = tx.Attestation },
                    Scope(loc), Ip(c), Ua(c), s.Sid);
                ev1["session_kind"] = "normal"; ev2["session_kind"] = "normal";
                // [ASSUMPTION A25] if the outbox is down no session is created (fail closed); the user sees the generic error.
                if (st.OutboxFault == "fail") return PasswordPage(c, af, Base(slug), Generic, ports.Control, resign: tx?.Resign == true, agentDown: tx?.AgentDown == true);
                st.Emit(ev1); st.Emit(ev2);
                user.FailedPasswords = 0;
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
