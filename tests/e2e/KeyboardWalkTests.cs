using Microsoft.Playwright;

namespace Cachd.Login.E2E;

/// <summary>
/// E2E-2x: assists (does not replace) check 12's manual WCAG keyboard walk. Tabs through each screen and asserts
/// every stop is visible, has an accessible name, and the primary action is reachable by keyboard.
/// Screen 8 has no file in the mockups; 07?state=remote and 02?state=remote are used as its stand-ins (OQ-SSE2-27).
/// </summary>
[Collection("mockups")]
public class KeyboardWalkTests(MockupFixture fx)
{
    record Stop(string Tag, string Name, bool Visible);

    static async Task<List<Stop>> Walk(IPage p, int max = 60)
    {
        await p.EvaluateAsync("() => document.activeElement && document.activeElement.blur()");
        var stops = new List<Stop>();
        for (int i = 0; i < max; i++)
        {
            await p.Keyboard.PressAsync("Tab");
            var j = await p.EvaluateAsync<System.Text.Json.JsonElement?>(@"() => {
              const e = document.activeElement; if (!e || e === document.body) return null;
              const r = e.getBoundingClientRect();
              const lab = e.labels && e.labels.length ? e.labels[0].textContent : '';
              const name = (e.getAttribute('aria-label') || lab || e.textContent || e.getAttribute('title') || e.value || '').replace(/\s+/g,' ').trim();
              return { Tag: e.tagName.toLowerCase() + (e.id ? '#' + e.id : ''), Name: name, Visible: r.width > 0 && r.height > 0 && !e.closest('[hidden]') };
            }");
            if (j is null || j.Value.ValueKind != System.Text.Json.JsonValueKind.Object) break;
            var s = new Stop(j.Value.GetProperty("Tag").GetString()!, j.Value.GetProperty("Name").GetString()!, j.Value.GetProperty("Visible").GetBoolean());
            if (stops.Count > 0 && stops[0] == s) break; // wrapped
            stops.Add(s);
        }
        return stops;
    }

    [Theory, Trait("check", "12")]
    [InlineData("02-db-signin.html?state=default&notes=off", "Sign in")]
    [InlineData("02-db-signin.html?state=wrong&notes=off", "Sign in")]
    [InlineData("06-hub.html?state=cashier&notes=off", "Cash Advance")]
    [InlineData("07-handoff.html?state=late&notes=off", null)]
    [InlineData("07-handoff.html?state=remote&notes=off", "Back to your apps")]
    [InlineData("08-session.html?state=agentdown&notes=off", "Continue")]
    [InlineData("08-session.html?state=remote&notes=off", "Try again")]
    public async Task E2E20_keyboard_walk_reaches_primary_action_all_stops_visible_and_named(string page, string? primary)
    {
        var (p, _) = await fx.Open(page);
        var stops = await Walk(p);
        Assert.NotEmpty(stops);
        Assert.All(stops, s => Assert.True(s.Visible, $"focus on invisible element {s.Tag}"));
        Assert.All(stops, s => Assert.False(string.IsNullOrWhiteSpace(s.Name), $"focus stop without accessible name: {s.Tag}"));
        if (primary is not null) Assert.Contains(stops, s => s.Name.Contains(primary));
    }

    [Fact, Trait("check", "12")]
    public async Task E2E21_hub_first_tab_stop_is_skip_link()
    {
        var (p, _) = await fx.Open("06-hub.html?state=cashier&notes=off");
        var stops = await Walk(p, 3);
        Assert.Contains("Skip to your apps", stops[0].Name);
    }

    [Fact, Trait("check", "12")]
    public async Task E2E22_signin_password_reached_after_username_by_tab()
    {
        var (p, _) = await fx.Open("02-db-signin.html?state=default&notes=off");
        var tags = (await Walk(p)).Select(s => s.Tag).ToList();
        var u = tags.IndexOf("input#username"); var pw = tags.IndexOf("input#password");
        Assert.True(u >= 0 && pw > u, "tab order: " + string.Join(" > ", tags));
    }
}
