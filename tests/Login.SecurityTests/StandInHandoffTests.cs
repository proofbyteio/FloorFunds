using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// HO01-HO16: §5.7 / 5.8 / 5.12 / 5.13 hand-off negatives (SSE2, Mon Nov 2 2026 ~9:45 PM PT) [TL Decision 9:26 PM PT (e)]:
/// target feat/si1-standin 10a1ca4 [TL 9:50 PM PT; was 8aab98a at 9:32 PM PT]. Every assertion cites contract-v1 (e363090).
/// TL 9:50 PM PT rulings applied: OQ-56 burn (HO06), OQ-55 frozen ms clock (HO18/18b/18c, HO03b/03c), OQ-58 CSP/XFO (HO10b, HO20,
/// HO22), OQ-57 (HO21 pending, v1.1 UX+SA).
/// Where the contract is silent the test logs and skips with an OQ instead of asserting a guess.
/// Stand-in-only: uses /_standin/* (reset, clock, totp, pcs, audit) and the fake agent (standin-control 2.4, SI1 A22).
/// </summary>
[Trait("check", "handoff-standin"), Trait("target", "standin-only")]
public class StandInHandoffTests
{
    internal static async Task Ready()
    {
        var r = StandIn.SkipReason(); if (r is not null) Assert.Skip(r);
        await StandIn.Reset();
        // Pending (not failed) on a stand-in build without the hand-off (pre-9a14163): no hub_launch on the tile.
        using var b = new Browser();
        var hub = await b.SignIn();
        if (Browser.HubLaunch(hub) is null) Assert.Skip("PENDING OQ-SSE2-53: stand-in has no hub_launch tile (needs feat/si1-standin >= 9a14163)");
        await StandIn.Reset();
    }
    static void Log(string s) => TestContext.Current.TestOutputHelper?.WriteLine(s);
    static List<(string, string?)> Reasons(IEnumerable<JsonObject> ev) =>
        ev.Select(e => ((string)e["event_type"]!, (string?)e["details"]?["reason"])).ToList();
    static readonly (string, string?) NotFromHub = ("auth.app_access.denied", "notfromhub");

    /// A signed-in Browser on pci-main with an enrolled PC in the given agent mode (standin-control 2.3/2.4).
    static async Task<(Browser B, string HubLaunch)> SignedIn(string pcMode = "valid", string pc = "standin-sse2-ho")
    {
        var b = new Browser();
        b.SetCookie("standin_pc", await StandIn.PutPc(pc, "valid", "pci-main"));
        var hub = await b.SignIn();
        Assert.Equal("screen-hub", hub.TestId);
        if (pcMode != "valid") await StandIn.PutPc(pc, pcMode, "pci-main");
        return (b, Browser.HubLaunch(hub) ?? throw new InvalidOperationException("no hub_launch on the cash_advance tile"));
    }
    static Task<Browser.CodeResult> CashAdvance(Browser b, string? hubLaunch, string challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", string? extra = null) =>
        b.Authorize("cash_advance_bff", challenge, extra: (hubLaunch is null ? "" : "&hub_launch=" + Uri.EscapeDataString(hubLaunch)) + (extra ?? ""));
    static void AssertRePrompt(Page p)
    {
        // 5.3: re-prompt = the sign-in page again, not an OAuth error; hub_launch never echoed (5.7 URL exception).
        Assert.Equal(HttpStatusCode.Redirect, p.Status);
        var loc = p.Location!.ToString();
        Assert.Contains(StandIn.IssuerPath + "/l/", loc);
        Assert.DoesNotContain("hub_launch", Uri.UnescapeDataString(loc));
        Assert.DoesNotContain("code=", loc.Split('?')[0]);
    }

    // ---------- positive control: without it every negative below could pass on a broken flow ----------
    [Fact] // 5.6, 5.8
    public async Task HO01_valid_handoff_issues_code_with_hub_handoff_event_keeping_auth_time_and_sid()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var _ = b;
        Assert.Matches("^[A-Za-z0-9_-]{22}$", hl); // 5.7 Add: 128 bits, base64url
        var n0 = await StandIn.AuditNext();
        var v = Pkce.NewVerifier();
        var inter = await CashAdvance(b, hl, Pkce.S256(v));
        Assert.Equal(HttpStatusCode.OK, inter.Page.Status);
        Assert.Null(inter.Code);
        var done = await b.CompleteInterstitial();
        var q = Browser.Query(done.Location);
        Assert.True(q.ContainsKey("code"), $"{(int)done.Status} {done.Location}");
        Assert.Equal(StandIn.Issuer, q.GetValueOrDefault("iss"));
        var ev = await StandIn.AuditSince(n0);
        var si = Assert.Single(ev, e => (string)e["event_type"]! == "auth.app.signed_in");
        Assert.Equal("hub_handoff", (string?)si["details"]!["method"]);
        var login = (await StandIn.AuditSince(0)).Last(e => (string)e["event_type"]! == "auth.login.succeeded");
        Assert.Equal((long)login["details"]!["auth_time"]!, (long)si["details"]!["auth_time"]!);
        Assert.Equal((string?)login["actor"]!["session_id"], (string?)si["actor"]!["session_id"]);
        Assert.Null(AuditSchema.Validate(si));
        var (st, body, _) = await StandIn.Token("cash_advance_bff", new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = q["code"], ["redirect_uri"] = StandIn.Redirect("cash_advance_bff"), ["code_verifier"] = v });
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.NotNull(body?["access_token"]);
    }

    // ---------- 5.7 rule 4 negatives: each -> notfromhub event + sign-in page, no code ----------
    [Fact]
    public async Task HO02_missing_hub_launch_is_notfromhub()
    {
        await Ready();
        var (b, _) = await SignedIn(); using var __ = b;
        var n0 = await StandIn.AuditNext();
        var r = await CashAdvance(b, null);
        AssertRePrompt(r.Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
    }

    [Fact] // 5.7 "valid <=60 s"; G1 no skew on single-use handles
    public async Task HO03_expired_hub_launch_61s_is_notfromhub_and_58s_is_not()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        await StandIn.Advance(61);
        var n0 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(b, hl)).Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
        // control: a fresh value at +58 s is still accepted (interstitial). Not +60: the stand-in clock is wall time + offset,
        // so "exactly 60 s" is +60 s plus the request latency (seen 9:31 PM PT: +60 -> re-prompt).
        var hl2 = Browser.HubLaunch(await b.Hub())!;
        await StandIn.Advance(58);
        var at60 = (await CashAdvance(b, hl2)).Page;
        Log($"HO03 +58 s: {(int)at60.Status} {at60.Location} {at60.TestId}");
        Assert.Equal(HttpStatusCode.OK, at60.Status);
    }

    [Fact] // 5.7 single use; consumed on the first check
    public async Task HO04_reused_hub_launch_is_notfromhub()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        Assert.Equal(HttpStatusCode.OK, (await CashAdvance(b, hl)).Page.Status); // first check consumes it (interstitial shown)
        var n0 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(b, hl)).Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
    }

    [Fact] // 5.7 "bound to another session -> notfromhub, consumed"
    public async Task HO05_hub_launch_from_another_session_is_notfromhub_and_consumed()
    {
        await Ready();
        var (a, hlA) = await SignedIn(pc: "standin-sse2-hoa"); using var _a = a;
        var (b, _) = await SignedIn(pc: "standin-sse2-hob"); using var _b = b;
        var n0 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(b, hlA)).Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
        // consumed: A can no longer use its own value
        var n1 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(a, hlA)).Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n1)));
    }

    [Fact] // 5.7 "clients without the hand-off ignore it" + TL 9:50 PM PT OQ-56 (replaces SI1 A41): presented at another client it is BURNED and ignored
    public async Task HO06_hub_launch_presented_at_back_office_is_ignored_there_and_burned_for_cash_advance()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var n0 = await StandIn.AuditNext();
        var r = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()), extra: "&hub_launch=" + hl);
        Assert.NotNull(r.Code); // ignored there: normal SSO
        var ev = await StandIn.AuditSince(n0);
        Assert.DoesNotContain(NotFromHub, Reasons(ev));
        Assert.DoesNotContain(ev, e => (string?)e["details"]?["method"] == "hub_handoff");
        Assert.NotNull((await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()))).Code); // session not cage_consumed
        // OQ-56: the same value at the correct client is refused (burned) -> notfromhub + re-prompt, no code
        var n1 = await StandIn.AuditNext();
        var ca = await CashAdvance(b, hl);
        Log($"HO06 same hub_launch at cash_advance_bff after back office: {(int)ca.Page.Status} {ca.Page.Location}");
        AssertRePrompt(ca.Page);
        Assert.Null(ca.Code);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n1)));
        Assert.Null(b.Cookie("standin_hx"));
        // control: a fresh value straight from the hub still works (the burn is per value, not per session)
        Assert.Equal(HttpStatusCode.OK, (await CashAdvance(b, Browser.HubLaunch(await b.Hub()))).Page.Status);
    }

    [Fact] // 5.7 "consumed on the first check even if another rule fails"; SI1 A40 extends this to no session
    public async Task HO07_hub_launch_presented_without_a_session_is_consumed()
    {
        await Ready();
        var (a, hl) = await SignedIn(); using var _a = a;
        using var anon = new Browser();
        var r = await CashAdvance(anon, hl);
        Assert.Equal(HttpStatusCode.Redirect, r.Page.Status);
        Assert.Null(r.Code);
        var n0 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(a, hl)).Page);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
    }

    // ---------- 5.12 / 5.13 refusals before rules 1-6 ----------
    [Fact]
    public async Task HO08_admin_session_is_refused_not_location_user_and_never_gets_hub_handoff()
    {
        await Ready();
        using var b = new Browser();
        var hub = await b.AdminSignIn();
        Assert.NotNull(b.Cookie("standin_session"));
        Assert.Null(Browser.HubLaunch(hub)); // admin hub shows no hand-off tile
        var n0 = await StandIn.AuditNext();
        var r = await CashAdvance(b, "AAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal("access_denied", r.Query.GetValueOrDefault("error"));
        Assert.False(r.Query.ContainsKey("error_description") || r.Query.ContainsKey("reason")); // G2: no reason to the client
        var ev = await StandIn.AuditSince(n0);
        var d = Assert.Single(ev);
        Assert.Equal(("auth.app_access.denied", "not_location_user"), Reasons(ev)[0]);
        Assert.Equal("partner", (string?)d["admin_type"]);
        Assert.Null(AuditSchema.Validate(d));
        // also for a non-hand-off client (SI1 A35, fail closed) and prompt=none
        var r2 = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()), extra: "&prompt=none");
        Assert.Equal("access_denied", r2.Query.GetValueOrDefault("error"));
        Assert.DoesNotContain(await StandIn.AuditSince(0), e => (string)e["event_type"]! == "auth.app.signed_in" && e["admin_type"] is not null);
    }

    [Fact]
    public async Task HO09_recovery_session_is_refused_recovery_session()
    {
        await Ready();
        using var b = new Browser();
        await b.AdminSignIn(recoveryIndex: 0);
        Assert.NotNull(b.Cookie("standin_session"));
        var n0 = await StandIn.AuditNext();
        var r = await CashAdvance(b, null);
        Assert.Equal("access_denied", r.Query.GetValueOrDefault("error"));
        var ev = await StandIn.AuditSince(n0);
        Assert.Equal(new[] { ("auth.app_access.denied", (string?)"recovery_session") }, Reasons(ev));
        Assert.Equal("recovery_reenroll", (string?)ev[0]["session_kind"]);
        Assert.Null(AuditSchema.Validate(ev[0]));
        // recovery code is single use (LD recovery codes; stand-in A33)
        using var c = new Browser();
        var again = await c.AdminSignIn(recoveryIndex: 0);
        Assert.Null(c.Cookie("standin_session"));
        Assert.Equal("page-admin-sign-in", again.TestId);
    }

    // ---------- 5.8 interstitial ----------
    [Fact] // 5.8(c)
    public async Task HO10_interstitial_headers_and_antiforgery()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var p = (await CashAdvance(b, hl)).Page;
        Assert.Equal(HttpStatusCode.OK, p.Status);
        Log("HO10 interstitial headers: " + string.Join(" | ", p.Headers.Select(h => h.Key + ": " + h.Value)));
        Assert.Contains("no-store", p.Header("Cache-Control"));
        var csp = p.Header("Content-Security-Policy");
        Assert.Contains("frame-ancestors 'none'", csp);
        var connect = csp.Split(';').Select(x => x.Trim()).Single(x => x.StartsWith("connect-src"));
        var agent = new Uri(StandIn.Control); // SI1 A22: fake agent on the control listener, addressed as localhost
        Assert.Equal($"connect-src 'self' http://localhost:{agent.Port}", connect);
        Assert.Contains("method=\"post\"", p.Body);
        // antiforgery: nonce and completion POSTs without the token are refused
        Assert.Null(await b.HandoffNonce(withAf: false));
        var noAf = await b.CompleteInterstitial(attestation: "", withAf: false);
        Assert.Equal(HttpStatusCode.BadRequest, noAf.Status);
    }

    [Fact] // 5.8(a) device_attestation never in a URL; (b) the POST must match the tx; hub_launch is not re-sent by the page
    public async Task HO11_attestation_in_url_is_ignored_and_hub_launch_in_post_body_is_attestation_invalid()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var r = await CashAdvance(b, hl, extra: "&device_attestation=eyJhbGciOiJFUzI1NiJ9.e30.x");
        Assert.Equal(HttpStatusCode.OK, r.Page.Status); // still the interstitial: the URL value is not used
        Assert.Null(r.Code);
        var n0 = await StandIn.AuditNext();
        var p = await b.CompleteInterstitial(extra: new Dictionary<string, string> { ["hub_launch"] = hl });
        Assert.Equal(HttpStatusCode.Redirect, p.Status);
        Assert.False(Browser.Query(p.Location).ContainsKey("code"));
        Assert.Equal(new[] { ("auth.app_access.denied", (string?)"attestation_invalid") }, Reasons(await StandIn.AuditSince(n0)));
    }

    [Fact] // 5.8(b) one transaction, one completion
    public async Task HO12_interstitial_completion_cannot_be_replayed()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        await CashAdvance(b, hl);
        var af = b.LastAf; var hx = b.Cookie("standin_hx");
        var n = await b.HandoffNonce(); var jws = await Browser.Agent(n!);
        var first = await b.CompleteInterstitial(attestation: jws);
        Assert.True(Browser.Query(first.Location).ContainsKey("code"));
        b.SetCookie("standin_hx", hx!); // replay: same tx cookie, same body
        var again = await b.Post(StandIn.IssuerPath + "/connect/authorize", new Dictionary<string, string> { ["device_attestation"] = jws!, ["__RequestVerificationToken"] = af! });
        Assert.False(again.Location is not null && Browser.Query(again.Location).ContainsKey("code"), $"{(int)again.Status} {again.Location}");
    }

    [Fact] // 5.8(b) the tx is bound to the login session
    public async Task HO13_interstitial_tx_from_another_session_is_attestation_invalid()
    {
        await Ready();
        var (a, hl) = await SignedIn(pc: "standin-sse2-hoa"); using var _a = a;
        var (b, _) = await SignedIn(pc: "standin-sse2-hob"); using var _b = b;
        await CashAdvance(a, hl);
        var hx = a.Cookie("standin_hx")!;
        var n = await a.HandoffNonce(); var jws = await Browser.Agent(n!);
        // B carries A's tx cookie; B's own antiforgery token (from B's hub) is sent
        await b.Hub(); b.SetCookie("standin_hx", hx);
        var n0 = await StandIn.AuditNext();
        var p = await b.CompleteInterstitial(attestation: jws);
        Assert.False(Browser.Query(p.Location).ContainsKey("code"), $"{(int)p.Status} {p.Location}");
        var reasons = Reasons(await StandIn.AuditSince(n0));
        Log("HO13 events: " + string.Join(",", reasons));
        Assert.True(p.Status == HttpStatusCode.BadRequest || reasons.SequenceEqual(new[] { ("auth.app_access.denied", (string?)"attestation_invalid") }));
    }

    [Theory] // 5.4, 5.5
    [InlineData("missing", "attestation_invalid")]
    [InlineData("bad_signature", "attestation_invalid")]
    [InlineData("expired", "attestation_invalid")]
    [InlineData("replayed", "attestation_invalid")]
    [InlineData("unknown_device", "attestation_invalid")]
    [InlineData("remote_session", "remote_session")]
    public async Task HO14_bad_attestation_at_handoff_never_issues_a_code(string mode, string reason)
    {
        await Ready();
        var (b, hl) = await SignedIn(mode); using var __ = b;
        await CashAdvance(b, hl);
        var n0 = await StandIn.AuditNext();
        var p = await b.CompleteInterstitial();
        Assert.False(Browser.Query(p.Location).ContainsKey("code"), $"{(int)p.Status} {p.Location}");
        Assert.Equal(new[] { ("auth.app_access.denied", (string?)reason) }, Reasons(await StandIn.AuditSince(n0)));
        if (mode == "remote_session") Assert.Equal("screen-8a", p.TestId); // 5.4 screen 8 variant A (ui-driver id per SI1)
        else AssertRePrompt(p); // 5.5 normal password + MFA sign-in
    }

    [Fact] // 5.1 rules 3 and 6; 5.10 no event for rules 1-3,5,6
    public async Task HO15_second_handoff_and_sso_after_handoff_are_refused_without_event()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        await CashAdvance(b, hl); var ok = await b.CompleteInterstitial();
        Assert.True(Browser.Query(ok.Location).ContainsKey("code"));
        var hl2 = Browser.HubLaunch(await b.Hub());
        var n0 = await StandIn.AuditNext();
        if (hl2 is not null) AssertRePrompt((await CashAdvance(b, hl2)).Page);
        Assert.Empty(await StandIn.AuditSince(n0));
        // rule 6: cage_consumed session is not SSO-eligible for anything
        var bo = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()));
        Assert.Null(bo.Code);
        var none = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()), extra: "&prompt=none");
        Assert.Equal("interaction_required", none.Query.GetValueOrDefault("error"));
    }

    [Fact] // 5.1 rule 1: auth_time <= 5 min
    public async Task HO16_session_older_than_5_minutes_is_re_prompted_without_event()
    {
        await Ready();
        var (b, _) = await SignedIn(); using var __ = b;
        await StandIn.Advance(301);
        var hl = Browser.HubLaunch(await b.Hub());
        Assert.NotNull(hl);
        var n0 = await StandIn.AuditNext();
        AssertRePrompt((await CashAdvance(b, hl)).Page);
        Assert.Empty(await StandIn.AuditSince(n0));
    }

    [Fact] // 5.7 URL exception: "ALB and app logs redact hub_launch, code and state values on both hosts" (+ code_challenge, TL 9:50 PM PT)
    public async Task HO17_app_log_does_not_contain_hub_launch_code_code_challenge_or_state()
    {
        await Ready();
        var log = Environment.GetEnvironmentVariable("LOGIN_STANDIN_LOG");
        if (log is null || !File.Exists(log)) Assert.Skip("PENDING: set LOGIN_STANDIN_LOG to the stand-in's stdout log to run this check");
        var (b, hl) = await SignedIn(); using var __ = b;
        var state = "sse2st" + Guid.NewGuid().ToString("N")[..16];
        var verifier = Pkce.NewVerifier(); var challenge = Pkce.S256(verifier);
        await b.Authorize("cash_advance_bff", challenge, state: state, extra: "&hub_launch=" + Uri.EscapeDataString(hl));
        var ok = await b.CompleteInterstitial();
        var q = Browser.Query(ok.Location);
        var code = q["code"];
        Assert.Equal(state, q.GetValueOrDefault("state"));
        // also a refused path (notfromhub logs) and a back-office SSO with its own state/challenge
        var state2 = "sse2st" + Guid.NewGuid().ToString("N")[..16]; var ch2 = Pkce.S256(Pkce.NewVerifier());
        await b.Authorize("backoffice_stub", ch2, state: state2);
        var (st, body, _) = await StandIn.Token("cash_advance_bff", new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = StandIn.Redirect("cash_advance_bff"), ["code_verifier"] = verifier });
        await Task.Delay(500);
        var text = File.ReadAllText(log);
        var secrets = new Dictionary<string, string> { ["hub_launch"] = hl, ["code"] = code, ["code_challenge"] = challenge, ["code_verifier"] = verifier,
            ["state"] = state, ["state(back office)"] = state2, ["code_challenge(back office)"] = ch2 };
        if (body?["refresh_token"] is { } rt) secrets["refresh_token"] = (string)rt!;
        if (body?["access_token"] is { } at) secrets["access_token"] = (string)at!;
        foreach (var (k, v) in secrets) Log($"HO17 {k}: {(text.Contains(v) ? "FOUND in log" : "absent")} ({v.Length} chars)");
        Log($"HO17 log {log}: {text.Length} chars; token rc {(int)st}");
        Assert.All(secrets, kv => Assert.False(text.Contains(kv.Value), $"{kv.Key} value is in the stand-in log"));
    }
    // ================= TL 9:32 PM PT additions (target 8aab98a) =================
    static async Task<DateTimeOffset> AuthTime() =>
        DateTimeOffset.Parse((string)(await StandIn.AuditSince(0)).Last(e => (string)e["event_type"]! == "auth.login.succeeded")["occurred_at"]!);
    static async Task<DateTimeOffset> ClockNow() => DateTimeOffset.Parse(await StandIn.Now());

    // OQ-55 (TL 9:50 PM PT, SSE 10a1ca4): frozen clock (POST /_standin/clock/freeze {"frozen":true}); only advance moves it, in ms.
    // Rule 1 compares in ms with no rounding down (TL 9:32 PM): 300000 ms accepted, 300001 ms re-prompt. hub_launch: 60000 / 60001 ms.
    // The hub_launch is fetched so that it is exactly 1000 ms old at authorize, so rule 4 always holds in the auth-age tests.
    static async Task<Page> AtAuthAgeMs(Browser b, long ageMs)
    {
        await StandIn.AdvanceMs(ageMs - 1000);
        var hl = Browser.HubLaunch(await b.Hub())!;
        await StandIn.AdvanceMs(1000);
        var p = (await CashAdvance(b, hl)).Page;
        Log($"auth age {ageMs} ms (frozen clock {await StandIn.Now()}) -> {(int)p.Status} {p.Location} {p.TestId}");
        return p;
    }
    static async Task<Browser> FrozenSignedIn()
    {
        await Ready();
        var f = await StandIn.Freeze(true);
        Assert.True((bool)f["frozen"]!);
        var (b, _) = await SignedIn();
        var t0 = await StandIn.Now(); await Task.Delay(1100);
        Assert.Equal(t0, await StandIn.Now()); // really frozen: wall time does not move it
        return b;
    }

    [Fact] // 5.1 rule 1 + TL 9:32 PM ms ruling + OQ-55 frozen clock: 300001 ms is refused
    public async Task HO18_auth_age_300001_ms_is_refused()
    {
        using var b = await FrozenSignedIn();
        var n0 = await StandIn.AuditNext();
        var p = await AtAuthAgeMs(b, 300_001);
        AssertRePrompt(p);                             // rule 1 failure = re-prompt (5.3), no event (5.10)
        Assert.Empty(await StandIn.AuditSince(n0));
    }

    [Fact] // positive control just inside: 299999 ms accepted (interstitial)
    public async Task HO18b_auth_age_299999_ms_is_accepted()
    {
        using var b = await FrozenSignedIn();
        Assert.Equal(HttpStatusCode.OK, (await AtAuthAgeMs(b, 299_999)).Status);
    }

    [Fact] // OQ-55 (TL 9:50 PM PT): exactly 300000 ms is accepted ("<= 5 min")
    public async Task HO18c_auth_age_exactly_300000_ms_is_accepted()
    {
        using var b = await FrozenSignedIn();
        var p = await AtAuthAgeMs(b, 300_000);
        Assert.Equal(HttpStatusCode.OK, p.Status);
        Assert.NotNull(b.Cookie("standin_hx"));
    }

    static async Task<Page> AtLaunchAgeMs(Browser b, long ageMs)
    {
        var hl = Browser.HubLaunch(await b.Hub())!;
        await StandIn.AdvanceMs(ageMs);
        var p = (await CashAdvance(b, hl)).Page;
        Log($"hub_launch age {ageMs} ms -> {(int)p.Status} {p.Location} {p.TestId}");
        return p;
    }

    [Fact] // 5.7 "valid <=60 s" + TL 9:32 PM ms ruling + OQ-55: 60001 ms is notfromhub
    public async Task HO03b_hub_launch_60001_ms_is_notfromhub()
    {
        using var b = await FrozenSignedIn();
        var n0 = await StandIn.AuditNext();
        var p = await AtLaunchAgeMs(b, 60_001);
        AssertRePrompt(p);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
    }

    [Fact] // OQ-55 (TL 9:50 PM PT): exactly 60000 ms is accepted
    public async Task HO03c_hub_launch_exactly_60000_ms_is_accepted()
    {
        using var b = await FrozenSignedIn();
        var n0 = await StandIn.AuditNext();
        var p = await AtLaunchAgeMs(b, 60_000);
        Assert.Equal(HttpStatusCode.OK, p.Status);
        Assert.DoesNotContain(NotFromHub, Reasons(await StandIn.AuditSince(n0)));
    }

    [Theory] // 5.1 rule 2: amr [pwd] and [wiaormfa] never count (8.10); 5.10 no event; SI1 FIXTURE users (8aab98a)
    [InlineData("cashier_pwd")]
    [InlineData("cashier_wia")]
    public async Task HO19_amr_without_mfa_is_refused_without_event(string user)
    {
        await Ready();
        using var b = new Browser();
        b.SetCookie("standin_pc", await StandIn.PutPc("standin-sse2-amr", "valid", "pci-main"));
        var hub = await b.SignIn(user);
        if (hub.TestId != "screen-hub") Assert.Skip($"PENDING OQ-SSE2-53: fixture user {user} missing on this stand-in build");
        var hl = Browser.HubLaunch(hub);
        Assert.NotNull(hl);
        var n0 = await StandIn.AuditNext();
        var p = (await CashAdvance(b, hl)).Page;
        AssertRePrompt(p);
        Assert.Empty(await StandIn.AuditSince(n0));
        // and the completion path cannot be reached at all
        Assert.Null(b.Cookie("standin_hx"));
    }

    // 5.8(c) regression pin [SSE 9:32 PM PT: "the hand-off page was missing its CSP; 8aab98a fixes it"] + TL 9:32 PM item 6:
    // CSP with frame-ancestors 'none' and Cache-Control no-store on every HTML page the login service renders.
    public static IEnumerable<object[]> Pages() => new[] { "signin", "totp", "hub", "apps", "admin", "admin-mfa", "interstitial", "resign-in", "screen-8a" }.Select(x => new object[] { x });
    [Theory, MemberData(nameof(Pages))]
    public async Task HO20_every_html_page_has_csp_frame_ancestors_none_and_no_store(string page)
    {
        await Ready();
        using var b = new Browser();
        b.SetCookie("standin_pc", await StandIn.PutPc("standin-sse2-csp", page == "screen-8a" ? "valid" : "valid", "pci-main"));
        Page p;
        switch (page)
        {
            case "signin": p = await b.OpenSignIn("pci-main"); break;
            case "totp": await b.OpenSignIn("pci-main"); p = await b.SubmitPassword("cashier_db", StandIn.Password("cashier_db")); break;
            case "hub": p = await b.SignIn(); break;
            case "apps": await b.SignIn(); p = await b.Hub(); break;
            case "admin": p = await b.Get(StandIn.IssuerPath + "/admin"); break;
            case "admin-mfa": await b.Get(StandIn.IssuerPath + "/admin"); p = await b.Post(StandIn.IssuerPath + "/admin", new Dictionary<string, string> { ["username"] = "partner_admin", ["password"] = StandIn.Password("partner_admin") }); break;
            case "interstitial": { var h = await b.SignIn(); p = (await CashAdvance(b, Browser.HubLaunch(h))).Page; break; }
            case "resign-in": { await b.SignIn(); var r = (await CashAdvance(b, null)).Page; p = await b.Get(r.Location!.ToString()); break; }
            default: { var h = await b.SignIn(); await CashAdvance(b, Browser.HubLaunch(h)); await StandIn.PutPc("standin-sse2-csp", "remote_session", "pci-main"); p = await b.CompleteInterstitial(); break; }
        }
        Log($"HO20 {page}: {(int)p.Status} testid={p.TestId} CSP=[{p.Header("Content-Security-Policy")}] CC=[{p.Header("Cache-Control")}] XFO=[{p.Header("X-Frame-Options")}]");
        Assert.Equal(HttpStatusCode.OK, p.Status);
        Assert.Contains("text/html", p.Header("Content-Type"));
        var csp = p.Header("Content-Security-Policy").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains(csp, d => d.StartsWith("default-src "));
        Assert.Contains("no-store", p.Header("Cache-Control"));
        AssertOq58(p.Header("Content-Security-Policy"), p.Header("X-Frame-Options"), page);
        // TL 9:38 PM (SSE c4da996): no inline script / style / handler, so the CSP needs no 'unsafe-inline' or nonce
        Assert.DoesNotMatch("<script(?![^>]*\\ssrc=)[^>]*>", p.Body);
        Assert.DoesNotMatch("\\sstyle=\"", p.Body);
        Assert.DoesNotMatch("\\son[a-z]+=\"", p.Body);
    }
    /// OQ-58 (TL 9:50 PM PT): no 'unsafe-inline' anywhere in any CSP; X-Frame-Options DENY or absent.
    internal static void AssertOq58(string csp, string xfo, string what)
    {
        Assert.False(csp.Contains("unsafe-inline", StringComparison.OrdinalIgnoreCase), $"{what}: 'unsafe-inline' in CSP [{csp}]");
        Assert.True(xfo.Length == 0 || xfo.Trim().Equals("DENY", StringComparison.OrdinalIgnoreCase), $"{what}: X-Frame-Options [{xfo}] (DENY or absent)");
    }

    // OQ-58 sweep over non-page responses on all three listeners: assets, discovery, JWKS, errors, 404, 405, back-channel errors.
    public static IEnumerable<object[]> Responses() => new[]
    {
        "F GET /ui/theme.css", "F GET /ui/js/signin.js", "F GET /ui/js/handoff.js", "F GET /.well-known/openid-configuration", "F GET /.well-known/jwks.json",
        "F GET /nope", "F GET /l/no-such-slug", "F POST /l/pci-main", "F GET /connect/authorize?client_id=x", "F GET /connect/endsession",
        "B GET /.well-known/openid-configuration", "B POST /connect/token", "B GET /connect/token", "B POST /connect/introspect", "B GET /nope",
    }.Select(x => new object[] { x });
    [Theory, MemberData(nameof(Responses))]
    public async Task HO22_every_response_has_no_unsafe_inline_and_xfo_deny_or_absent(string req)
    {
        await Ready();
        var parts = req.Split(' ');
        var host = parts[0] == "F" ? StandIn.Front : StandIn.Back;
        using var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var m = new HttpRequestMessage(new HttpMethod(parts[1]), host + StandIn.IssuerPath + parts[2]);
        if (parts[1] == "POST") m.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" });
        using var r = await h.SendAsync(m);
        var csp = r.Headers.TryGetValues("Content-Security-Policy", out var c) ? string.Join(", ", c) : "";
        var xfo = r.Headers.TryGetValues("X-Frame-Options", out var x) ? string.Join(", ", x) : "";
        Log($"HO22 {req}: {(int)r.StatusCode} CSP=[{csp}] XFO=[{xfo}]");
        AssertOq58(csp, xfo, req);
    }

    [Fact] // 5.8(c) exact pin of the hand-off page CSP (regression for the 9a14163 omission)
    public async Task HO10b_interstitial_csp_pinned()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var p = (await CashAdvance(b, hl)).Page;
        var agent = new Uri(StandIn.Control).Port;
        var csp = p.Header("Content-Security-Policy");
        Log($"HO10b CSP=[{csp}] XFO=[{p.Header("X-Frame-Options")}]");
        // pinned at 10a1ca4 (c4da996: scripts in ui/login-hub/js, no 'unsafe-inline'; OQ-58 TL 9:50 PM PT)
        Assert.Equal($"default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; connect-src 'self' http://localhost:{agent}; base-uri 'none'; object-src 'none'; frame-ancestors 'none'", csp);
        // single CSP header (two headers would intersect, a second one could hide a weaker policy)
        Assert.DoesNotContain(", default-src", csp);
        AssertOq58(csp, p.Header("X-Frame-Options"), "interstitial");
    }

    [Fact(Skip = "PENDING OQ-SSE2-57 [TL 9:50 PM PT: confirmed no interstitial ui-driver id in v1; v1.1 for UX + SA]. Not guessed. Assert the screen testid once UX/SA name it")]
    public void HO21_interstitial_has_its_ui_driver_id() { }
}

/// <summary>
/// DEC-093 (Mohanraj, relayed 9:25 PM PT: "Yes, admins should go to the admin sign-in page"); v1.1 clarification of §7.8, not a blocker.
/// After re-enrolment an admin goes to /admin, a location user to their own /l/{slug}. Negatives: an admin never lands on a
/// location sign-in; a location user never lands on /admin. Target 8aab98a [TL Decision 9:26 PM PT (e); TL 9:32 PM PT].
/// The re-enrolment routes are SI1 step (3), still not built at 8aab98a (A36) (SSE SI1-BUILD-LOG "To be built in step (3)") -> pending.
/// The sign-in-entry halves that exist now are runnable.
/// </summary>
[Trait("check", "dec-093"), Trait("target", "standin-only")]
public class Dec093Tests
{
    [Fact]
    public async Task D93_01_location_user_cannot_get_a_session_from_admin_sign_in()
    {
        await StandInHandoffTests.Ready();
        using var b = new Browser();
        var p = await b.AdminSignIn("cashier_db");
        Assert.Equal("page-admin-sign-in", p.TestId);
        Assert.Null(b.Cookie("standin_session"));
    }

    [Fact]
    public async Task D93_02_admin_cannot_get_a_session_from_a_location_sign_in()
    {
        await StandInHandoffTests.Ready();
        using var b = new Browser();
        var p = await b.SignIn("partner_admin", "pci-main");
        Assert.Null(b.Cookie("standin_session"));
        Assert.NotEqual("screen-hub", p.TestId);
    }

    [Fact]
    public async Task D93_03_admin_session_never_lands_on_a_location_page_through_authorize()
    {
        await StandInHandoffTests.Ready();
        using var b = new Browser();
        await b.AdminSignIn();
        foreach (var c in new[] { "backoffice_stub", "cash_advance_bff" })
        {
            var r = await b.Authorize(c, Pkce.S256(Pkce.NewVerifier()), extra: "&prompt=login");
            Assert.DoesNotContain(StandIn.IssuerPath + "/l/", r.Page.Location?.ToString() ?? "");
            Assert.Equal("access_denied", r.Query.GetValueOrDefault("error"));
        }
    }

    // D93_04/05 (enabled at 3feb66c, SSE2 10:20 PM PT; OQ-SSE2-54 closed): recovery sign-in -> /mfa/reenroll -> codes page once -> Continue.
    static string Base32Hotp(string key, long step)
    {
        var k = key.Replace(" ", "").TrimEnd('=').ToUpperInvariant(); var bytes = new List<byte>(); int buf = 0, bits = 0;
        foreach (var ch in k) { buf = (buf << 5) | "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(ch); bits += 5; if (bits >= 8) { bytes.Add((byte)(buf >> (bits - 8))); bits -= 8; } }
        var msg = BitConverter.GetBytes(step); if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var h = System.Security.Cryptography.HMACSHA1.HashData(bytes.ToArray(), msg); int o = h[^1] & 15;
        return ((((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]) % 1000000).ToString("D6");
    }
    static async Task<(Page Codes, Page Again, Page AfterApps)> Reenrol(Browser b, string expectHome)
    {
        var g = await b.Get(StandIn.IssuerPath + "/mfa/reenroll");
        Assert.Equal(HttpStatusCode.OK, g.Status);
        var key = WebUtility.HtmlDecode(Regex.Match(g.Body, "data-testid=\"reenroll-manual-key\">([^<]+)<").Groups[1].Value);
        var now = DateTimeOffset.Parse(await StandIn.Now()).ToUnixTimeSeconds() / 30;
        var post = await b.Post(StandIn.IssuerPath + "/mfa/reenroll", new Dictionary<string, string> { ["totp_code"] = Base32Hotp(key, now) });
        Assert.Equal(HttpStatusCode.Redirect, post.Status);
        Assert.EndsWith("/mfa/reenroll/recovery-codes", post.Location?.ToString());
        var codes = await b.Get(StandIn.IssuerPath + "/mfa/reenroll/recovery-codes");
        Assert.Equal(HttpStatusCode.OK, codes.Status);
        Assert.Equal(10, Regex.Matches(codes.Body, "<li><code>[0-9a-f]+</code></li>").Count);
        var links = Regex.Matches(codes.Body, "href=\"([^\"]+)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Contains(StandIn.IssuerPath + expectHome, links);
        Assert.DoesNotContain(links, l => l.Contains("/apps") || l.Contains("/cage"));
        return (codes, await b.Get(StandIn.IssuerPath + "/mfa/reenroll/recovery-codes"), await b.Hub());
    }

    [Fact]
    public async Task D93_04_admin_after_reenrolment_goes_to_admin_never_location()
    {
        await StandInHandoffTests.Ready(); await StandIn.Reset();
        using var b = new Browser();
        var p = await b.AdminSignIn("partner_admin", recoveryIndex: 0);
        Assert.True(p.Status is HttpStatusCode.Redirect or HttpStatusCode.OK, $"{p.Status}");
        var (codes, again, apps) = await Reenrol(b, "/admin");
        Assert.DoesNotMatch(StandIn.IssuerPath + "/l/", string.Join(" ", Regex.Matches(codes.Body, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value)));
        Assert.Equal(HttpStatusCode.NotFound, again.Status);       // codes shown once
        Assert.NotEqual(HttpStatusCode.OK, apps.Status);           // DEC-087: session ended
    }

    [Fact]
    public async Task D93_05_location_user_after_reenrolment_goes_to_own_slug_never_admin()
    {
        await StandInHandoffTests.Ready(); await StandIn.Reset();
        using var b = new Browser();
        b.SetCookie("standin_pc", await StandIn.PutPc("standin-sse2-d93", "valid", "nevada-main"));
        await b.OpenSignIn("nevada-main");
        var p = await b.SubmitPassword("nevada_db", StandIn.Password("nevada_db"));
        Assert.Equal("screen-totp", p.TestId);
        await b.Post(StandIn.IssuerPath + "/l/nevada-main/totp", new Dictionary<string, string> { ["recovery_code"] = StandIn.RecoveryCode("nevada_db", 0) });
        var (codes, again, apps) = await Reenrol(b, "/l/nevada-main");
        var links = string.Join(" ", Regex.Matches(codes.Body, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
        Assert.DoesNotContain(StandIn.IssuerPath + "/admin", links);
        Assert.DoesNotContain("/l/pci-main", links);
        Assert.Equal(HttpStatusCode.NotFound, again.Status);
        Assert.NotEqual(HttpStatusCode.OK, apps.Status);
    }
}
