using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// SEC70-79: adversarial probes against the stand-in (SSE2, Mon Nov 2 2026 ~9:30-10:30 PM PT, target feat/si1-standin 8aab98a (rerun 9:50 PM PT; first run ed0ecc1)).
/// Every assertion cites contract-v1 (e363090) or the LD line it rests on. Where neither states a behaviour,
/// the probe records what it saw and skips with an OQ-SSE2-5x instead of asserting a guess.
/// Stand-in-only: uses /_standin/* (reset, clock, totp, audit).
/// </summary>
[Trait("check", "adversarial-standin"), Trait("target", "standin-only")]
public class StandInAdversarialTests
{
    static async Task Ready()
    {
        var r = StandIn.SkipReason(); if (r is not null) Assert.Skip(r);
        await StandIn.Reset();
    }
    static Dictionary<string, string> CodeForm(string code, string verifier, string redirect) =>
        new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier };
    static Dictionary<string, string> RefreshForm(string rt) => new() { ["grant_type"] = "refresh_token", ["refresh_token"] = rt };
    static string Err(JsonObject? b) => (string?)b?["error"] ?? "<none>";
    const string Inactive = "200 {\"active\":false}"; // contract 10.1: exactly {"active":false}, no other member

    // ---------- SEC70: authorization code is single-use and short-lived ----------
    [Fact]
    public async Task SEC70_code_reuse_is_invalid_grant()
    {
        await Ready();
        using var b = new Browser();
        var (tok, v, code) = await b.SignInAndGetTokens();
        var (st, body, _) = await StandIn.Token("backoffice_stub", CodeForm(code, v, StandIn.Redirect("backoffice_stub")));
        Assert.Equal(HttpStatusCode.BadRequest, st);
        Assert.Equal("invalid_grant", Err(body));
        // RFC 6749 §4.1.2 SHOULD revoke tokens already issued from a reused code; contract silent -> logged, not asserted.
        TestContext.Current.TestOutputHelper?.WriteLine("after code reuse, first access token introspects: " + await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
    }

    [Fact] // G1: code <=60 s, server clock, no skew
    public async Task SEC70b_code_older_than_60s_is_invalid_grant()
    {
        await Ready();
        using var b = new Browser();
        await b.SignIn();
        var v = Pkce.NewVerifier();
        var cr = await b.Authorize("backoffice_stub", Pkce.S256(v));
        Assert.NotNull(cr.Code);
        await StandIn.Advance(61);
        var (st, body, _) = await StandIn.Token("backoffice_stub", CodeForm(cr.Code!, v, StandIn.Redirect("backoffice_stub")));
        Assert.Equal("400 invalid_grant", $"{(int)st} {Err(body)}");
    }

    // ---------- SEC71: PKCE and redirect_uri binding (contract 4.1, 4.8, 7.1) ----------
    [Fact]
    public async Task SEC71_pkce_and_redirect_mismatch_at_token_are_refused()
    {
        await Ready();
        using var b = new Browser();
        await b.SignIn();
        var cases = new List<string>();
        async Task<string> Try(Func<string, string, Dictionary<string, string>> form, string client = "backoffice_stub")
        {
            var v = Pkce.NewVerifier();
            var cr = await b.Authorize("backoffice_stub", Pkce.S256(v));
            Assert.NotNull(cr.Code);
            var (st, body, _) = await StandIn.Token(client, form(cr.Code!, v));
            return $"{(int)st} {Err(body)}";
        }
        var red = StandIn.Redirect("backoffice_stub");
        cases.Add("wrong verifier: " + await Try((c, v) => CodeForm(c, Pkce.NewVerifier(), red)));
        cases.Add("no verifier: " + await Try((c, v) => new() { ["grant_type"] = "authorization_code", ["code"] = c, ["redirect_uri"] = red }));
        cases.Add("verifier = challenge (plain downgrade): " + await Try((c, v) => CodeForm(c, Pkce.S256(v), red)));
        cases.Add("redirect_uri + '/': " + await Try((c, v) => CodeForm(c, v, red + "/")));
        cases.Add("redirect_uri missing: " + await Try((c, v) => new() { ["grant_type"] = "authorization_code", ["code"] = c, ["code_verifier"] = v }));
        cases.Add("code redeemed by other client: " + await Try((c, v) => CodeForm(c, v, StandIn.Redirect("cash_advance_bff")), "cash_advance_bff"));
        foreach (var c in cases) TestContext.Current.TestOutputHelper?.WriteLine(c);
        Assert.All(cases, c => Assert.Matches(@": 400 invalid_(grant|request)$", c));
    }

    [Fact] // 4.1 S256 only + PKCE required; 4.8 exact redirect, bad redirect -> page on our host, never a redirect
    public async Task SEC71b_authorize_refuses_plain_missing_pkce_and_inexact_redirect()
    {
        await Ready();
        using var b = new Browser();
        await b.SignIn();
        var red = StandIn.Redirect("backoffice_stub");
        string Base(string redirect) => StandIn.IssuerPath + "/connect/authorize?response_type=code&client_id=backoffice_stub&scope=openid%20backoffice_api&state=s&nonce=n&redirect_uri=" + Uri.EscapeDataString(redirect);
        var v = Pkce.NewVerifier();
        var plain = await b.Get(Base(red) + "&code_challenge=" + v + "&code_challenge_method=plain");
        var none = await b.Get(Base(red));
        var noMethod = await b.Get(Base(red) + "&code_challenge=" + Pkce.S256(v));
        static bool NoCode(Page p) => p.Location is null || !p.Location.Query.Contains("code=");
        Assert.True(NoCode(plain), $"plain: {plain.Location}");
        Assert.True(NoCode(none), $"no PKCE: {none.Location}");
        Assert.True(NoCode(noMethod), $"no method: {noMethod.Location}");
        foreach (var bad in new[] { red + "/", red.ToUpperInvariant(), red + "?x=1", red.Replace("https", "http"), "https://evil.fixture.invalid/signin-oidc" })
        {
            var p = await b.Get(Base(bad) + "&code_challenge=" + Pkce.S256(v) + "&code_challenge_method=S256");
            Assert.True(p.Status == HttpStatusCode.BadRequest && p.Location is null, $"{bad}: {(int)p.Status} {p.Location}");
            Assert.DoesNotContain("fixture.invalid", p.Body); // never echoes redirect_uri
        }
    }

    // ---------- SEC72: alg confusion / forged tokens (contract 3.1, 8.9, 10.1, 11.2a/d) ----------
    static string B64(string s) => Pkce.B64Url(Encoding.UTF8.GetBytes(s));
    static string Forge(string header, string payloadB64, byte[] sig) => B64(header) + "." + payloadB64 + "." + Pkce.B64Url(sig);

    [Fact]
    public async Task SEC72_forged_and_wrong_type_tokens_introspect_inactive()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var at = (string)tok["access_token"]!; var idt = (string)tok["id_token"]!;
        Assert.Equal("200", (await StandIn.IntrospectRaw("backoffice_stub", at))[..3]);
        Assert.Contains("\"active\":true", await StandIn.IntrospectRaw("backoffice_stub", at)); // positive control
        var parts = at.Split('.');
        var hdr = JsonNode.Parse(Encoding.UTF8.GetString(Pkce.FromB64Url(parts[0])))!.AsObject();
        var kid = (string)hdr["kid"]!;
        using var h = new HttpClient();
        var jwks = await h.GetStringAsync(StandIn.FrontPath("/.well-known/jwks"));
        var n = Pkce.FromB64Url((string)JsonNode.Parse(jwks)!["keys"]![0]!["n"]!);
        // tamper payload: sub changed, original signature kept
        var pl = JsonNode.Parse(Encoding.UTF8.GetString(Pkce.FromB64Url(parts[1])))!.AsObject();
        pl["sub"] = "5f0c3e1a-0003-4000-8000-000000000c02";
        var tamperedPayload = Pkce.B64Url(Encoding.UTF8.GetBytes(pl.ToJsonString()));
        using var rsa = RSA.Create(2048);
        var otherKeyHeader = $"{{\"alg\":\"RS256\",\"kid\":\"{kid}\",\"typ\":\"at+jwt\"}}";
        var otherKeyInput = B64(otherKeyHeader) + "." + parts[1];
        var forged = new Dictionary<string, string>
        {
            ["alg none"] = B64($"{{\"alg\":\"none\",\"kid\":\"{kid}\",\"typ\":\"at+jwt\"}}") + "." + parts[1] + ".",
            ["alg none, sig kept"] = B64($"{{\"alg\":\"none\",\"kid\":\"{kid}\",\"typ\":\"at+jwt\"}}") + "." + parts[1] + "." + parts[2],
            ["HS256 keyed with RSA modulus"] = Hs(parts[1], kid, n),
            ["HS256 keyed with JWKS text"] = Hs(parts[1], kid, Encoding.UTF8.GetBytes(jwks)),
            ["RS256 other key, same kid"] = otherKeyInput + "." + Pkce.B64Url(rsa.SignData(Encoding.ASCII.GetBytes(otherKeyInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            ["tampered payload"] = parts[0] + "." + tamperedPayload + "." + parts[2],
            ["embedded jwk"] = Forge($"{{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"jwk\":{{\"kty\":\"RSA\",\"n\":\"{Pkce.B64Url(rsa.ExportParameters(false).Modulus!)}\",\"e\":\"AQAB\"}}}}", parts[1], Array.Empty<byte>()),
            ["id_token as access token (typ JWT)"] = idt,
            ["access token of this client asked by other client"] = "__other__",
        };
        var results = new List<string>();
        foreach (var (name, t) in forged)
        {
            var r = t == "__other__" ? await StandIn.IntrospectRaw("cash_advance_bff", at) : await StandIn.IntrospectRaw("backoffice_stub", t);
            results.Add($"{name}: {r}");
        }
        foreach (var r in results) TestContext.Current.TestOutputHelper?.WriteLine(r);
        Assert.All(results, r => Assert.EndsWith(": " + Inactive, r));
    }
    static string Hs(string payloadB64, string kid, byte[] key)
    {
        var input = B64($"{{\"alg\":\"HS256\",\"kid\":\"{kid}\",\"typ\":\"at+jwt\"}}") + "." + payloadB64;
        return input + "." + Pkce.B64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(input)));
    }

    [Fact] // 11.2(a) hint checked for signature; (d) without a valid hint a confirm page comes first (logout CSRF)
    public async Task SEC72b_forged_id_token_hint_does_not_sign_out_without_confirm()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var idt = ((string)tok["id_token"]!).Split('.');
        var none = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + idt[1] + ".";
        using var attacker = new Browser(); // cross-site GET carries the victim's cookie in a browser; model it with the victim jar
        var p = await b.Get(StandIn.IssuerPath + "/connect/endsession?id_token_hint=" + none);
        Assert.Equal("screen-signout-confirm", p.TestId);
        var p2 = await b.Get(StandIn.IssuerPath + "/connect/endsession");
        Assert.Equal("screen-signout-confirm", p2.TestId);
        Assert.Contains("\"active\":true", await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
    }

    // ---------- SEC73: revocation (contract 9.1, 10.1) ----------
    [Fact]
    public async Task SEC73_revoked_tokens_introspect_exactly_inactive_and_refresh_dies()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var at = (string)tok["access_token"]!; var rt = (string)tok["refresh_token"]!;
        Assert.Equal(HttpStatusCode.OK, await StandIn.Revoke("backoffice_stub", at));
        Assert.Equal(Inactive, await StandIn.IntrospectRaw("backoffice_stub", at));
        Assert.Equal(HttpStatusCode.OK, await StandIn.Revoke("backoffice_stub", at)); // already revoked -> 200
        Assert.Equal(HttpStatusCode.OK, await StandIn.Revoke("backoffice_stub", "not-a-token"));
        Assert.Equal(HttpStatusCode.OK, await StandIn.Revoke("backoffice_stub", rt));
        var (st, body, _) = await StandIn.Token("backoffice_stub", RefreshForm(rt));
        Assert.Equal("400 invalid_grant", $"{(int)st} {Err(body)}");
        Assert.Equal(Inactive, await StandIn.IntrospectRaw("backoffice_stub", rt));
    }

    [Fact] // a client must not revoke another client's token (RFC 7009 §2.1); contract 9.1 silent on the status code
    public async Task SEC73b_other_client_cannot_revoke_my_tokens()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        await StandIn.Revoke("cash_advance_bff", (string)tok["access_token"]!);
        await StandIn.Revoke("cash_advance_bff", (string)tok["refresh_token"]!);
        Assert.Contains("\"active\":true", await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
        var (st, body, _) = await StandIn.Token("backoffice_stub", RefreshForm((string)tok["refresh_token"]!));
        Assert.Equal(HttpStatusCode.OK, st);
    }

    [Fact] // 7.1/7.6 client auth: wrong secret / no auth / client_id mismatch
    public async Task SEC73c_client_auth_failures_are_invalid_client()
    {
        await Ready();
        var (st, body, _) = await StandIn.BackPost("token", "backoffice_stub", RefreshForm("x"), secretOverride: "wrong");
        Assert.Equal("401 invalid_client", $"{(int)st} {Err(body)}");
        using var h = new HttpClient();
        using var r = await h.PostAsync(StandIn.BackEndpoint("token"), new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "x", ["client_id"] = "backoffice_stub" }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        using var r2 = await h.PostAsync(StandIn.BackEndpoint("introspect"), new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }));
        Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);
    }

    // ---------- SEC74: end session (contract 11.1-11.3) ----------
    [Fact]
    public async Task SEC74_endsession_kills_access_refresh_and_unredeemed_code_once()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var v2 = Pkce.NewVerifier();
        var pending = await b.Authorize("backoffice_stub", Pkce.S256(v2)); // a code issued before sign-out, not yet redeemed
        Assert.NotNull(pending.Code);
        var since = await StandIn.AuditNext();
        var p = await b.Get(StandIn.IssuerPath + "/connect/endsession?id_token_hint=" + tok["id_token"] + "&post_logout_redirect_uri=" + Uri.EscapeDataString("https://backoffice.fixture.invalid/signed-out") + "&state=z");
        Assert.True(p.Status is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther, $"{(int)p.Status}");
        Assert.EndsWith("/l/pci-main", p.Location!.OriginalString.Split('?')[0]); // 11.2(c)
        Assert.Equal(Inactive, await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
        var (st, body, _) = await StandIn.Token("backoffice_stub", RefreshForm((string)tok["refresh_token"]!));
        Assert.Equal("400 invalid_grant", $"{(int)st} {Err(body)}");
        var (st2, body2, _) = await StandIn.Token("backoffice_stub", CodeForm(pending.Code!, v2, StandIn.Redirect("backoffice_stub")));
        Assert.Equal("400 invalid_grant", $"{(int)st2} {Err(body2)}");
        var revoked = (await StandIn.AuditSince(since)).Where(e => (string?)e["event_type"] == "auth.session.revoked").ToList();
        Assert.Single(revoked);
        Assert.Equal("sign_out", (string?)revoked[0]["details"]?["reason"] ?? (string?)revoked[0]["reason"]);
        // the session cookie is dead too: prompt=none -> login_required (4.6)
        var again = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()), extra: "&prompt=none");
        Assert.Equal("login_required", again.Query.GetValueOrDefault("error"));
        // replaying the same hint after sign-out revokes nothing new and writes no second event (11.3)
        await b.Get(StandIn.IssuerPath + "/connect/endsession?id_token_hint=" + tok["id_token"]);
        Assert.Single((await StandIn.AuditSince(since)).Where(e => (string?)e["event_type"] == "auth.session.revoked"));
    }

    [Fact] // 11.2(a): exp is ignored on the hint; G1 31 s past exp -> invalid for bearer/introspect
    public async Task SEC74b_expired_hint_still_signs_out_and_expired_access_token_is_inactive()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        await StandIn.Advance(240);
        Assert.Contains("\"active\":true", await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
        await StandIn.Advance(300 - 240 + 31);
        Assert.Equal(Inactive, await StandIn.IntrospectRaw("backoffice_stub", (string)tok["access_token"]!));
        var p = await b.Get(StandIn.IssuerPath + "/connect/endsession?id_token_hint=" + tok["id_token"]);
        Assert.NotEqual("screen-signout-confirm", p.TestId);
        Assert.NotNull(p.Location);
    }

    // ---------- SEC75: refresh rotation and reuse (contract 7.2) ----------
    [Fact]
    public async Task SEC75_refresh_reuse_after_leeway_revokes_family_and_audits()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var rt0 = (string)tok["refresh_token"]!;
        var (s1, r1, _) = await StandIn.Token("backoffice_stub", RefreshForm(rt0));
        Assert.Equal(HttpStatusCode.OK, s1);
        var rt1 = (string)r1!["refresh_token"]!; var at1 = (string)r1["access_token"]!;
        Assert.NotEqual(rt0, rt1);
        var since = await StandIn.AuditNext();
        await StandIn.Advance(31);
        var (s2, r2, _) = await StandIn.Token("backoffice_stub", RefreshForm(rt0));
        Assert.Equal("400 invalid_grant", $"{(int)s2} {Err(r2)}");
        var (s3, r3, _) = await StandIn.Token("backoffice_stub", RefreshForm(rt1)); // chain revoked
        Assert.Equal("400 invalid_grant", $"{(int)s3} {Err(r3)}");
        var ev = await StandIn.AuditSince(since);
        Assert.Contains(ev, e => (string?)e["event_type"] == "auth.refresh.reuse_detected");
        TestContext.Current.TestOutputHelper?.WriteLine("access token from rotated chain after reuse: " + await StandIn.IntrospectRaw("backoffice_stub", at1));
    }

    /// 7.2 says reuse AFTER the leeway is refused; it does not say what reuse WITHIN the leeway returns
    /// (the same new token, or a fresh pair each time). The stand-in mints a fresh live pair per replay (OAuth.cs, RotatedAt ??= now).
    [Fact]
    public async Task SEC75b_refresh_replay_within_leeway_probe()
    {
        await Ready();
        using var b = new Browser();
        var (tok, _, _) = await b.SignInAndGetTokens();
        var rt0 = (string)tok["refresh_token"]!;
        var minted = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var (s, r, _) = await StandIn.Token("backoffice_stub", RefreshForm(rt0));
            minted.Add($"{(int)s} {(r?["refresh_token"] is null ? Err(r) : "rt#" + ((string)r["refresh_token"]!).GetHashCode())}");
        }
        var live = 0;
        foreach (var m in minted) if (m.StartsWith("200")) live++;
        Assert.Skip($"PENDING OQ-SSE2-52: 7.2 does not define replay within the 30 s leeway. Observed 3 replays of one refresh token within 0 s: [{string.Join(", ", minted)}] -> {live} distinct live chains.");
    }

    // ---------- SEC76/77: TOTP step (LD L331, L335; contract 6.3 lockout) ----------
    [Fact] // LD L331 "TOTP code single-use"; L335 "TOTP codes single-use (store the last used time-step)"
    public async Task SEC76_totp_code_is_single_use()
    {
        await Ready();
        using var a = new Browser();
        await a.OpenSignIn("pci-main");
        Assert.Equal("screen-totp", (await a.SubmitPassword("cashier_db", StandIn.Password("cashier_db"))).TestId);
        var code = await StandIn.Totp("cashier_db");
        var first = await a.SubmitTotp(code);
        Assert.True(first.TestId == "screen-hub" || first.Location is not null, $"first use failed: {first.TestId}");
        using var c = new Browser();
        await c.OpenSignIn("pci-main");
        Assert.Equal("screen-totp", (await c.SubmitPassword("cashier_db", StandIn.Password("cashier_db"))).TestId);
        var replay = await c.SubmitTotp(code); // same time step, same code
        Assert.True(replay.TestId == "screen-totp" && c.Cookie("standin_session") is null,
            $"replayed TOTP code accepted: {replay.TestId}, session cookie set={c.Cookie("standin_session") is not null}");
    }

    [Fact] // LD L335 "lockoutOnFailure: true on password, TOTP and recovery-code steps"; contract 6.3 threshold 10 (PCI)
    public async Task SEC77_totp_guessing_hits_lockout()
    {
        await Ready();
        using var a = new Browser();
        await a.OpenSignIn("pci-main");
        Assert.Equal("screen-totp", (await a.SubmitPassword("cashier_db", StandIn.Password("cashier_db"))).TestId);
        var right = await StandIn.Totp("cashier_db");
        var wrong = ((int.Parse(right) + 500000) % 1000000).ToString("D6");
        for (var i = 0; i < 11; i++) await a.SubmitTotp(wrong);
        var p = await a.SubmitTotp(await StandIn.Totp("cashier_db"));
        Assert.True(a.Cookie("standin_session") is null, $"after 11 wrong TOTP codes the right code still signs in ({p.TestId}); no lockout/attempt limit on the TOTP step");
    }

    // ---------- SEC78: audit events vs audit-events.schema.json; no secrets ----------
    [Fact]
    public async Task SEC78_every_emitted_audit_event_validates_and_carries_no_secret()
    {
        await Ready();
        var secrets = new List<string> { StandIn.Password("cashier_db"), StandIn.Password("no_role_user"), (string)StandIn.Client("backoffice_stub")["secret"]! };
        using (var b = new Browser())
        {
            var (tok, _, _) = await b.SignInAndGetTokens();
            secrets.AddRange(new[] { (string)tok["access_token"]!, (string)tok["refresh_token"]!, (string)tok["id_token"]! });
            var (_, r1, _) = await StandIn.Token("backoffice_stub", RefreshForm((string)tok["refresh_token"]!));
            await StandIn.Advance(31);
            await StandIn.Token("backoffice_stub", RefreshForm((string)tok["refresh_token"]!)); // reuse_detected
        }
        using (var b = new Browser()) { await b.OpenSignIn("pci-main"); await b.SubmitPassword("cashier_db", "Wrong-Password-1x"); }
        using (var b = new Browser()) { await b.OpenSignIn("pci-main"); await b.SubmitPassword("nobody_here", "Wrong-Password-1x"); }
        using (var b = new Browser()) { await b.OpenSignIn("pci-main"); await b.SubmitPassword("cashier_db", StandIn.Password("cashier_db")); await b.SubmitTotp("000000"); }
        using (var b = new Browser()) { await b.SignIn("no_role_user"); await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier())); }
        using (var b = new Browser()) { await b.SignIn(); await b.Authorize("cash_advance_bff", Pkce.S256(Pkce.NewVerifier())); } // notfromhub
        using (var b = new Browser()) { var (t, _, _) = await b.SignInAndGetTokens(); await b.Get(StandIn.IssuerPath + "/connect/endsession?id_token_hint=" + t["id_token"]); }
        for (var i = 0; i < 3; i++) { using var b = new Browser(); await b.OpenSignIn("nevada-main"); await b.SubmitPassword("nevada_db", "Wrong-Password-1x"); } // nevada lockout 3
        var events = await StandIn.AuditSince(0);
        Assert.NotEmpty(events);
        // negative controls: the validator must reject a broken event, else a pass means nothing
        var broken = (JsonObject)events.First(e => (string?)e["event_type"] == "auth.login.succeeded").DeepClone();
        broken.Remove("scope");
        Assert.NotNull(AuditSchema.Validate(broken));
        var broken2 = (JsonObject)events.First(e => (string?)e["event_type"] == "auth.session.revoked").DeepClone();
        broken2["details"] = new JsonObject(); // details.reason is required
        Assert.NotNull(AuditSchema.Validate(broken2));
        var bad = new List<string>();
        foreach (var e in events)
        {
            var err = AuditSchema.Validate(e);
            if (err is not null) bad.Add($"{e["event_type"]}: {err}");
            var text = e.ToJsonString();
            foreach (var s in secrets) if (s.Length >= 8 && text.Contains(s)) bad.Add($"{e["event_type"]}: contains secret material");
        }
        var types = string.Join(", ", events.GroupBy(e => (string?)e["event_type"]).Select(g => $"{g.Key}x{g.Count()}"));
        TestContext.Current.TestOutputHelper?.WriteLine("events: " + types);
        foreach (var x in bad) TestContext.Current.TestOutputHelper?.WriteLine(x);
        Assert.True(bad.Count == 0, $"{bad.Count}/{events.Count} events fail [{types}]: " + string.Join(" | ", bad.Distinct().Take(8)));
    }

    [Fact] // 8.13: envelope admin_type only on /admin-session events; /l/{slug} sessions never carry it
    public async Task SEC79_location_session_events_never_carry_admin_type()
    {
        await Ready();
        using var b = new Browser();
        await b.SignInAndGetTokens();
        var ev = await StandIn.AuditSince(0);
        Assert.NotEmpty(ev);
        Assert.All(ev, e => Assert.Null(e["admin_type"]));
    }

    // ---------- SEC79b: listener separation (contract 1.4, 1.5) ----------
    [Fact]
    public async Task SEC79b_listener_separation()
    {
        await Ready();
        using var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var rows = new List<string>();
        foreach (var e in new[] { "token", "revoke", "introspect" })
            rows.Add($"front /connect/{e}: {(int)(await h.PostAsync(StandIn.FrontPath("/connect/" + e), new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode}");
        rows.Add($"back /connect/authorize: {(int)(await h.GetAsync(StandIn.Back + StandIn.IssuerPath + "/connect/authorize")).StatusCode}");
        rows.Add($"back /connect/endsession: {(int)(await h.GetAsync(StandIn.Back + StandIn.IssuerPath + "/connect/endsession")).StatusCode}");
        rows.Add($"back /l/pci-main: {(int)(await h.GetAsync(StandIn.Back + StandIn.IssuerPath + "/l/pci-main")).StatusCode}");
        rows.Add($"front /_standin/clock: {(int)(await h.GetAsync(StandIn.Front + "/_standin/clock")).StatusCode}");
        rows.Add($"back /_standin/clock: {(int)(await h.GetAsync(StandIn.Back + "/_standin/clock")).StatusCode}");
        rows.Add($"control /standin/l/pci-main: {(int)(await h.GetAsync(StandIn.Control + StandIn.IssuerPath + "/l/pci-main")).StatusCode}");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", rows));
        Assert.All(rows, r => Assert.EndsWith(": 404", r));
    }

    // ---------- SEC79c: timing of unknown vs known username (OQ-SSE2-11 threshold unruled) ----------
    [Fact]
    public async Task SEC79c_username_timing_probe()
    {
        await Ready();
        async Task<double> One(string user)
        {
            using var b = new Browser();
            await b.OpenSignIn("pci-main");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await b.SubmitPassword(user, "Wrong-Password-1x", callAgent: false);
            return sw.Elapsed.TotalMilliseconds;
        }
        for (var i = 0; i < 5; i++) { await One("cashier_db"); await One("ghost_user"); }
        var known = new List<double>(); var unknown = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            await StandIn.Reset(); // lockout never triggers
            known.Add(await One("cashier_db")); unknown.Add(await One("ghost_user_" + i));
        }
        var mk = Timing.Median(known); var mu = Timing.Median(unknown);
        Assert.Skip($"PENDING OQ-SSE2-11 (threshold unruled). Measured n=40: median known-user wrong password {mk:F1} ms vs unknown user {mu:F1} ms (diff {mk - mu:F1} ms; Mann-Whitney p={Timing.MannWhitneyP(known, unknown):G3}).");
    }
}
