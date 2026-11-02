using Microsoft.Playwright;

namespace Cachd.Login.E2E;

/// <summary>
/// E2E-xx: the UX mockups (DEC-081 "clickable screens") behave as the plan / login doc rev 11:25 require.
/// These test the MOCKUP only; the same assertions move to the stand-in pages (code/ui/) once they exist.
/// Annotation tags (.tag) and the design-notes panel are excluded from user-visible text checks.
/// </summary>
[Collection("mockups")]
public class MockupFlowTests(MockupFixture fx)
{
    static Task<string> VisibleText(IPage p, string selector) => p.EvalOnSelectorAsync<string>(selector,
        "el => { const c = el.cloneNode(true); c.querySelectorAll('.tag,.annot').forEach(t => t.remove()); return c.textContent.replace(/\\s+/g,' ').trim(); }");

    static async Task<string[]> VisibleTiles(IPage p) =>
        (await p.Locator(".tile-grid .tile:visible .tile__name").AllInnerTextsAsync()).Select(s => s.Trim()).ToArray();

    public static TheoryData<string> AllStates => new()
    {
        "02-db-signin.html?state=default", "02-db-signin.html?state=loading", "02-db-signin.html?state=missing", "02-db-signin.html?state=wrong",
        "02-db-signin.html?state=expired", "02-db-signin.html?state=signedout", "02-db-signin.html?state=remote", "02-db-signin.html?state=pwchange",
        "02-db-signin.html?state=pwchange-nv", "02-db-signin.html?state=pwexpired", "02-db-signin.html?state=pwchange-error", "02-db-signin.html?state=pwchanged",
        "06-hub.html?state=cashier", "06-hub.html?state=office", "06-hub.html?state=admin", "06-hub.html?state=noaccess", "06-hub.html?state=loading",
        "06-hub.html?state=error", "06-hub.html?state=expired",
        "07-handoff.html?state=simulator", "07-handoff.html?state=launching", "07-handoff.html?state=fresh", "07-handoff.html?state=late",
        "07-handoff.html?state=sso", "07-handoff.html?state=nomfa", "07-handoff.html?state=second", "07-handoff.html?state=notfromhub",
        "07-handoff.html?state=entra", "07-handoff.html?state=remote", "07-handoff.html?state=otherapp", "07-handoff.html?state=agentdown",
        "08-session.html?state=warning", "08-session.html?state=remote", "08-session.html?state=agentdown", "08-session.html?state=expired", "index.html",
    };

    [Theory, MemberData(nameof(AllStates)), Trait("check", "12")]
    public async Task E2E01_every_mockup_state_makes_no_network_request_and_has_one_h1(string page)
    {
        var (p, net) = await fx.Open(page);
        await p.WaitForLoadStateAsync(LoadState.NetworkIdle);
        Assert.True(net.Count == 0, "non-file requests: " + string.Join(", ", net));
        if (!page.StartsWith("index")) Assert.True(await p.Locator("h1:visible").CountAsync() >= 1, "no visible h1");
    }

    [Fact, Trait("check", "10"), Trait("threat", "I-1")]
    public async Task E2E02_wrong_state_is_one_generic_message_without_lockout_detail()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=wrong");
        var alerts = p.Locator("[role=alert]:visible");
        Assert.Equal(1, await alerts.CountAsync());
        var t = await VisibleText(p, "#err-wrong");
        Assert.StartsWith("Sign-in didn't work.", t.Trim().TrimStart('⚠').Trim());
        foreach (var leak in new[] { "locked", "lock", "attempt", "tries", "unknown", "not found", "does not exist", "minute", "try again in", "too many", "disabled" })
            Assert.DoesNotContain(leak, t, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\d", t);
    }

    [Fact, Trait("check", "10"), Trait("threat", "I-1")]
    public async Task E2E03_no_separate_locked_or_ratelimited_states_exist()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=default");
        var shows = await p.EvalOnSelectorAllAsync<string[]>("[data-show]", "els => els.map(e => e.getAttribute('data-show'))");
        Assert.DoesNotContain(shows, s => s.Split(' ').Any(x => x is "locked" or "ratelimited" or "unknown" or "disabled"));
    }

    [Fact, Trait("threat", "I-4")]
    public async Task E2E04_autocomplete_and_password_length_attributes()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=default");
        Assert.Equal("off", await p.GetAttributeAsync("#username", "autocomplete"));          // Q20
        Assert.Equal("current-password", await p.GetAttributeAsync("#password", "autocomplete"));
        var (p2, _) = await fx.Open("02-db-signin.html?state=pwchange");
        Assert.Equal("128", await p2.GetAttributeAsync("#newpw", "maxlength"));                // Q28: max 128
    }

    [Fact, Trait("check", "4")]
    public async Task E2E05_signin_remote_state_blocks_submit()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=remote");
        Assert.True(await p.Locator("#err-remote").IsVisibleAsync());
        Assert.Equal(0, await p.Locator("button[type=submit]:visible").CountAsync());
        Assert.True(await p.Locator("button:visible:has-text('Sign in (blocked)')").IsDisabledAsync());
    }

    [Fact, Trait("check", "2")]
    public async Task E2E06_signin_submit_goes_to_hub_cashier()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=default");
        await p.FillAsync("#username", "test-cashier");
        await p.FillAsync("#password", "TEST-ONLY-not-a-password");
        await p.ClickAsync("button[type=submit]:visible");
        // Since ~11:35 PT the mock goes 02 -> 03 (one-time code, MFA per plan) -> 06 hub.
        await p.WaitForURLAsync(u => u.Contains("03-totp.html") || u.Contains("06-hub.html"));
        if (p.Url.Contains("03-totp.html"))
        {
            await p.FillAsync("#totp", "123456");
            await p.ClickAsync("form:visible button[type=submit]");
            await p.WaitForURLAsync("**/06-hub.html?state=cashier*");
        }
        Assert.Equal(new[] { "Cash Advance" }, await VisibleTiles(p));
    }

    [Theory, Trait("check", "1")]
    [InlineData("cashier", new[] { "Cash Advance" })]
    [InlineData("office", new[] { "Back office", "Reports" })]
    [InlineData("admin", new[] { "Cash Advance", "Back office", "Reports" })]
    [InlineData("noaccess", new string[0])]
    public async Task E2E07_hub_shows_only_tiles_for_roles(string state, string[] expected)
    {
        var (p, _) = await fx.Open($"06-hub.html?state={state}");
        Assert.Equal(expected, await VisibleTiles(p));
        if (state == "noaccess") Assert.True(await p.Locator("h1:has-text('No apps available yet')").IsVisibleAsync());
    }

    [Fact, Trait("check", "2")]
    public async Task E2E08_cash_advance_tile_opens_without_password_prompt()
    {
        var (p, _) = await fx.Open("06-hub.html?state=cashier");
        await p.ClickAsync(".tile__name:has-text('Cash Advance')");
        await p.WaitForURLAsync("**/07-handoff.html?state=fresh*");
        Assert.True(await p.Locator(".banner__title:has-text('Cash Advance is open')").IsVisibleAsync());
        Assert.Equal(0, await p.Locator("input[type=password]:visible").CountAsync());
    }

    [Theory, Trait("check", "3")]
    [InlineData("late")] [InlineData("second")] [InlineData("notfromhub")] [InlineData("sso")]
    public async Task E2E09_handoff_reprompt_states_ask_for_password(string state)
    {
        var (p, _) = await fx.Open($"07-handoff.html?state={state}");
        Assert.True(await p.Locator("h1:visible:has-text('Sign in to Cash Advance')").IsVisibleAsync());
        Assert.Equal(1, await p.Locator("input[type=password]:visible").CountAsync());
        Assert.False(await p.Locator(".banner__title:has-text('Cash Advance is open')").IsVisibleAsync());
    }

    [Fact, Trait("check", "4")]
    public async Task E2E10_handoff_remote_state_blocks_and_offers_no_password()
    {
        var (p, _) = await fx.Open("07-handoff.html?state=remote");
        Assert.True(await p.Locator("h1:visible:has-text(\"Cash Advance can't open right now\")").IsVisibleAsync());
        Assert.Equal(0, await p.Locator("input[type=password]:visible").CountAsync());
        Assert.True(await p.Locator("a:visible:has-text('Back to your apps')").IsVisibleAsync());
    }

    public static TheoryData<string, string, string, string, string, string> SimCases => new()
    {
        // how, minutes, used, from, remote -> expected target state
        { "interactive", "2", "no", "hub", "no", "fresh" },
        { "interactive", "6", "no", "hub", "no", "late" },
        { "interactive", "5", "no", "hub", "no", "fresh" },      // boundary: <= 5 min passes
        { "sso", "1", "no", "hub", "no", "sso" },
        { "wiaormfa", "1", "no", "hub", "no", "nomfa" },
        { "entra-mfa", "9", "no", "hub", "no", "entra" },
        { "interactive", "2", "yes", "hub", "no", "second" },
        { "interactive", "2", "no", "direct", "no", "notfromhub" },
        { "interactive", "2", "no", "hub", "yes", "remote" },
    };

    [Theory, MemberData(nameof(SimCases)), Trait("check", "3")]
    public async Task E2E11_rule_simulator_routes_each_case(string how, string mins, string used, string from, string remote, string expected)
    {
        var (p, _) = await fx.Open("07-handoff.html?state=simulator");
        await p.CheckAsync($"input[name=how][value={how}]");
        await p.Locator("#mins").FillAsync(mins);
        await p.CheckAsync($"input[name=used][value={used}]");
        await p.CheckAsync($"input[name=from][value={from}]");
        await p.CheckAsync($"input[name=remote][value={remote}]");
        await p.ClickAsync("#sim button[type=submit]");
        var href = await p.Locator("#sim-result a.button").GetAttributeAsync("href");
        Assert.Equal($"?state={expected}", href);
    }

    // Check 4a on the mockup: an invalid/missing attestation must not open Cash Advance password-free; the simulator
    // routes it to "agentdown" (fallback sign-in done, cage actions blocked). UX added this at ~11:37 PT.
    [Theory, Trait("check", "4a")]
    [InlineData("no", "agentdown")]
    [InlineData("yes", "agentdown")]   // invalid attestation stops before the remote rule: its remote flag can't be trusted (UX note; contract: OQ-SSE2-09)
    public async Task E2E12_simulator_attestation_invalid_fails_closed(string remote, string expected)
    {
        var (p, _) = await fx.Open("07-handoff.html?state=simulator");
        if (await p.Locator("#sim input[name=attest]").CountAsync() == 0)
            Assert.Skip("PENDING OQ-SSE2-10 (UX gap): mockup 07 has no attestation input.");
        await p.CheckAsync("#sim input[name=attest][value=invalid]");
        await p.CheckAsync($"#sim input[name=remote][value={remote}]");
        await p.ClickAsync("#sim button[type=submit]");
        var href = await p.Locator("#sim-result a.button").GetAttributeAsync("href");
        Assert.DoesNotContain("state=fresh", href);
        Assert.EndsWith($"state={expected}", href);   // 08-session.html?state=agentdown: fallback sign-in, then cage actions blocked
    }

    const string AgentDownText = "This PC's security software isn't responding. Contact IT.";

    static string Need08(MockupFixture fx) => File.Exists(Path.Combine(Cachd.Login.TestKit.Paths.Mockups, "08-session.html")) ? "" : "mockup 08-session.html not present";

    [Fact, Trait("check", "4b")]
    public async Task E2E14_screen8_agentdown_variant_offers_fallback_signin_and_leads_to_blocked_cage_actions()
    {
        var r = Need08(fx); if (r != "") Assert.Skip(r);
        var (p, _) = await fx.Open("08-session.html?state=agentdown");
        Assert.Equal(AgentDownText, (await p.Locator("#agent-why .banner__title").InnerTextAsync()).Trim());
        Assert.Equal(1, await p.Locator("input[type=password]:visible").CountAsync());
        await p.FillAsync("#username", "test-cashier");
        await p.FillAsync("#password", "TEST-ONLY-not-a-password");
        await p.ClickAsync("form:visible button[type=submit]");
        await p.WaitForURLAsync("**/07-handoff.html?state=agentdown*");
    }

    [Fact, Trait("check", "4b")]
    public async Task E2E15_cash_advance_after_fallback_blocks_payment_id_scan_print()
    {
        var (p, _) = await fx.Open("07-handoff.html?state=agentdown");
        if (await p.Locator("#agent-reason").CountAsync() == 0) Assert.Skip("PENDING OQ-SSE2-10: mockup 07 has no agentdown state.");
        Assert.True(await p.Locator("h1:visible:has-text('Cash Advance')").IsVisibleAsync());
        Assert.Equal(AgentDownText, (await p.Locator("#agent-reason .banner__title").InnerTextAsync()).Trim());
        foreach (var want in new[] { "Take payment", "Scan ID", "Print" })
        {
            var a = p.Locator($".cage-action:visible:has-text('{want}')");
            Assert.Equal(1, await a.CountAsync());
            Assert.Equal("true", await a.GetAttributeAsync("aria-disabled"));
            Assert.Contains("Blocked", await a.InnerTextAsync());
        }
    }

    [Fact, Trait("check", "4")]
    public async Task E2E16_screen8_remote_variant_refuses_signin_without_password_field()
    {
        var r = Need08(fx); if (r != "") Assert.Skip(r);
        var (p, _) = await fx.Open("08-session.html?state=remote");
        Assert.True(await p.Locator("h1:visible:has-text('Sign-in is blocked on this computer')").IsVisibleAsync());
        Assert.Equal(0, await p.Locator("input[type=password]:visible").CountAsync());
    }

    [Fact, Trait("check", "6")]
    public async Task E2E13_idle_signout_states_exist_on_signin_and_hub()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=expired");
        Assert.True(await p.Locator("[role=alert]:visible, [role=status]:visible").CountAsync() >= 1);
        var (h, _) = await fx.Open("06-hub.html?state=expired");
        Assert.True(await h.Locator("h1:visible:has-text('You were signed out')").IsVisibleAsync());
    }
}
