using System.Net;
using System.Text.Json.Nodes;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// HO01-HO16: §5.7 / 5.8 / 5.12 / 5.13 hand-off negatives (SSE2, Mon Nov 2 2026 ~9:45 PM PT) [TL Decision 9:26 PM PT (e)]:
/// target feat/si1-standin 8aab98a [TL 9:32 PM PT: replaces 9a14163]. Every assertion cites contract-v1 (e363090).
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

    [Fact] // 5.7 "clients without the hand-off ignore it"
    public async Task HO06_hub_launch_sent_to_a_non_handoff_client_is_ignored()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var n0 = await StandIn.AuditNext();
        var r = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()), extra: "&hub_launch=" + hl);
        Assert.NotNull(r.Code); // normal SSO
        Assert.DoesNotContain(NotFromHub, Reasons(await StandIn.AuditSince(n0)));
        // TL 9:32 PM item 2 "use by a different client: refused": the other client gets no hand-off from it (no hub_handoff,
        // session not cage_consumed). Whether it must also be consumed is OQ-SSE2-56 (contract 5.7 "ignore" vs SI1 A41 "stays usable").
        var ev = await StandIn.AuditSince(n0);
        Assert.DoesNotContain(ev, e => (string?)e["details"]?["method"] == "hub_handoff");
        var after = await b.Authorize("backoffice_stub", Pkce.S256(Pkce.NewVerifier()));
        Assert.NotNull(after.Code); // not cage_consumed
        Log("HO06 (OQ-SSE2-56): same value at Cash Advance afterwards -> " + (int)(await CashAdvance(b, hl)).Page.Status + " (200 = still usable, SI1 A41)");
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

    [Fact] // 5.7 URL exception: "ALB and app logs redact hub_launch, code and state values on both hosts"
    public async Task HO17_app_log_does_not_contain_raw_hub_launch_or_code()
    {
        await Ready();
        var log = Environment.GetEnvironmentVariable("LOGIN_STANDIN_LOG");
        if (log is null || !File.Exists(log)) Assert.Skip("PENDING: set LOGIN_STANDIN_LOG to the stand-in's stdout log to run this check");
        var (b, hl) = await SignedIn(); using var __ = b;
        await CashAdvance(b, hl); var ok = await b.CompleteInterstitial();
        var code = Browser.Query(ok.Location)["code"];
        await Task.Delay(300);
        var text = File.ReadAllText(log);
        Assert.DoesNotContain(hl, text);
        Assert.DoesNotContain(code, text);
        Assert.DoesNotContain("st-1", text.Replace("standin", "")); // state value used by Browser.Authorize
    }
    // ================= TL 9:32 PM PT additions (target 8aab98a) =================
    static async Task<DateTimeOffset> AuthTime() =>
        DateTimeOffset.Parse((string)(await StandIn.AuditSince(0)).Last(e => (string)e["event_type"]! == "auth.login.succeeded")["occurred_at"]!);
    static async Task<DateTimeOffset> ClockNow() => DateTimeOffset.Parse(await StandIn.Now());

    [Fact] // 5.1 rule 1 + TL 9:32 PM ruling: millisecond precision, no rounding down: 300001 ms is refused
    public async Task HO18_auth_age_over_300000_ms_is_refused()
    {
        await Ready();
        var (b, _) = await SignedIn(); using var __ = b;
        var t0 = await AuthTime();
        await StandIn.Advance(299);
        var hl = Browser.HubLaunch(await b.Hub())!;   // fresh hub_launch so rule 4 holds
        await StandIn.Advance(1);
        var lower = (await ClockNow() - t0).TotalMilliseconds; // the authorize below is at least this old
        Log($"HO18 auth age at authorize >= {lower:F0} ms");
        Assert.True(lower > 300000, $"precondition: {lower} ms");
        var n0 = await StandIn.AuditNext();
        var p = (await CashAdvance(b, hl)).Page;
        Log($"HO18 result: {(int)p.Status} {p.Location}");
        AssertRePrompt(p);                             // rule 1 failure = re-prompt (5.3), no event (5.10)
        Assert.Empty(await StandIn.AuditSince(n0));
    }

    [Fact] // positive control for HO18: clearly inside the window (about 299 s + latency) is accepted
    public async Task HO18b_auth_age_below_300000_ms_is_accepted()
    {
        await Ready();
        var (b, _) = await SignedIn(); using var __ = b;
        var t0 = await AuthTime();
        await StandIn.Advance(298);
        var hl = Browser.HubLaunch(await b.Hub())!;
        await StandIn.Advance(1);
        var p = (await CashAdvance(b, hl)).Page;
        var upper = (await ClockNow() - t0).TotalMilliseconds;
        Assert.True(upper < 300000, $"precondition: {upper} ms");
        Assert.Equal(HttpStatusCode.OK, p.Status); // interstitial
    }

    [Fact(Skip = "PENDING OQ-SSE2-55: exactly 300000 ms (accepted) is not reachable: standin-control 2.2 clock = wall time + whole-second offset, no freeze/set. HO18b covers < 300000 ms, HO18 > 300000 ms")]
    public void HO18c_auth_age_exactly_300000_ms_is_accepted() { }

    [Fact] // 5.7 "valid <=60 s" with the TL 9:32 PM ms ruling: 60000 ms + latency is refused
    public async Task HO03b_hub_launch_older_than_60000_ms_is_notfromhub()
    {
        await Ready();
        var (b, _) = await SignedIn(); using var __ = b;
        var hub = await b.Hub(); var t = await ClockNow(); var hl = Browser.HubLaunch(hub)!;
        await StandIn.Advance(60);
        var age = (await ClockNow() - t).TotalMilliseconds;
        Assert.True(age > 60000, $"precondition {age}");
        var n0 = await StandIn.AuditNext();
        var p = (await CashAdvance(b, hl)).Page;
        Log($"HO03b hub_launch age >= {age:F0} ms -> {(int)p.Status}");
        AssertRePrompt(p);
        Assert.Equal(new[] { NotFromHub }, Reasons(await StandIn.AuditSince(n0)));
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
    }

    [Fact] // 5.8(c) exact pin of the hand-off page CSP (regression for the 9a14163 omission)
    public async Task HO10b_interstitial_csp_pinned()
    {
        await Ready();
        var (b, hl) = await SignedIn(); using var __ = b;
        var p = (await CashAdvance(b, hl)).Page;
        var agent = new Uri(StandIn.Control).Port;
        var csp = p.Header("Content-Security-Policy");
        Assert.Equal($"default-src 'self'; script-src 'self' 'unsafe-inline'; connect-src 'self' http://localhost:{agent}; frame-ancestors 'none'", csp);
        // single CSP header (two headers would intersect, a second one could hide a weaker policy)
        Assert.DoesNotContain(", default-src", csp);
        Log("HO10b note: script-src 'unsafe-inline' is present; 5.8(c) asks for 'a strict CSP' -> OQ-SSE2-58 (minor)");
    }

    [Fact(Skip = "PENDING OQ-SSE2-57: no ui-driver id for the hand-off interstitial in contract/ui-driver.md or SSE code (SI1 A38: form id 'handoff', no screen testid). Assert <main data-testid=ID> once the TL/UX names ID")]
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

    [Fact(Skip = "PENDING OQ-SSE2-54: §7.8 re-enrolment completion not built at 8aab98a (SI1 A36, step 3). Assert: admin recovery_reenroll -> after re-enrol, session ended, Location = /admin, never /l/{slug}")]
    public void D93_04_admin_after_reenrolment_goes_to_admin_never_location() { }

    [Fact(Skip = "PENDING OQ-SSE2-54: location-user recovery sign-in not built at 8aab98a (SI1 A36, Gaps). Assert: after re-enrol Location = /l/{own slug}, never /admin, never another location's slug")]
    public void D93_05_location_user_after_reenrolment_goes_to_own_slug_never_admin() { }
}
