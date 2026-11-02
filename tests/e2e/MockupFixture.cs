using Cachd.Login.TestKit;
using Microsoft.Playwright;

namespace Cachd.Login.E2E;

/// <summary>
/// One headless Chrome for the run. Uses the box's /usr/bin/google-chrome (no Playwright browser download; decision SSE2-D3).
/// Mockups are opened read-only via file:// from ../login-hub-mockups (UX-owned).
/// </summary>
public sealed class MockupFixture : IAsyncLifetime
{
    public IPlaywright? Pw { get; private set; }
    public IBrowser? Browser { get; private set; }
    public string? SkipReason { get; private set; }

    public static string ChromePath => Environment.GetEnvironmentVariable("CACHD_CHROME") ?? "/usr/bin/google-chrome";

    public async ValueTask InitializeAsync()
    {
        if (!Directory.Exists(Paths.Mockups)) { SkipReason = $"mockups folder not found at {Paths.Mockups}"; return; }
        if (!File.Exists(ChromePath)) { SkipReason = $"Chrome not found at {ChromePath} (set CACHD_CHROME)"; return; }
        Pw = await Playwright.CreateAsync();
        Browser = await Pw.Chromium.LaunchAsync(new() { ExecutablePath = ChromePath, Headless = true, Args = new[] { "--no-sandbox" } });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        Pw?.Dispose();
    }

    public string Url(string page) => new Uri(Path.Combine(Paths.Mockups, page.Split('?')[0])).AbsoluteUri + (page.Contains('?') ? "?" + page.Split('?', 2)[1] : "");

    /// <summary>Opens a page; records every non-file request so tests can assert "no network".</summary>
    public async Task<(IPage page, List<string> net)> Open(string page)
    {
        if (SkipReason is not null) Assert.Skip(SkipReason);
        var ctx = await Browser!.NewContextAsync();
        var p = await ctx.NewPageAsync();
        var net = new List<string>();
        await p.RouteAsync("**/*", async r =>
        {
            if (!r.Request.Url.StartsWith("file:")) { lock (net) net.Add(r.Request.Url); await r.AbortAsync(); }
            else await r.ContinueAsync();
        });
        await p.GotoAsync(Url(page));
        return (p, net);
    }
}

[CollectionDefinition("mockups")]
public class MockupCollection : ICollectionFixture<MockupFixture> { }
