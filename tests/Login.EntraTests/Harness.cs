using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Login.Entra;
using Login.Entra.Contracts;
using Login.Abstractions;
using Login.Entra.Fixtures;
using Login.FakeEntra;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Login.EntraTests;

/// <summary>Minimal host standing in for the login service: it implements only the IEntraHost hooks.</summary>
public sealed class RecordingHost : IEntraHost
{
    public ConcurrentQueue<EntraAuditEvent> Audit { get; } = new();
    public ConcurrentQueue<(DateTimeOffset At, string What)> Timeline { get; } = new();
    public ConcurrentDictionary<string, EntraSignInResult> Sessions { get; } = new();
    public Task WriteAuditAsync(EntraAuditEvent ev, CancellationToken ct) { Audit.Enqueue(ev); Timeline.Enqueue((DateTimeOffset.UtcNow, ev.EventType)); return Task.CompletedTask; }
    public async Task CompleteSignInAsync(HttpContext http, EntraSignInResult r)
    {
        var sid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Sessions[sid] = r;
        http.Response.Cookies.Append("test_sid", sid, new CookieOptions { HttpOnly = true, Path = "/" });
        http.Response.StatusCode = 200; await http.Response.WriteAsync("SIGNED-IN");
    }
    public async Task RenderErrorAsync(HttpContext http, Guid? locationId) { http.Response.StatusCode = 503; http.Response.ContentType = "text/html"; await http.Response.WriteAsync("<html><body><h1>Sign-in is unavailable</h1></body></html>"); }
    public async Task RenderDatabaseFormAsync(HttpContext http, LocationLoginConfig c, bool viaFallback)
    { http.Response.ContentType = "text/html"; await http.Response.WriteAsync($"<html><body>{(viaFallback ? "<p class=fallback>FALLBACK</p>" : "")}<form method=post><input name=username><input type=password name=password></form></body></html>"); }
}

public sealed class ListLogger : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string c) => new L(this);
    public void Dispose() { }
    private sealed class L(ListLogger p) : ILogger
    {
        public IDisposable? BeginScope<T>(T s) where T : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<T>(LogLevel l, EventId id, T s, Exception? e, Func<T, Exception?, string> f) => p.Lines.Enqueue(f(s, e));
    }
}

public sealed class Env : IAsyncDisposable
{
    // SSE2-FAKE-TEST-ONLY tenants (never real). 'f4ce' prefix = fake.
    public static readonly Guid TenantA = Guid.Parse("f4ce0000-5e20-4a00-8000-00000000000a");
    public static readonly Guid TenantB = Guid.Parse("f4ce0000-5e20-4a00-8000-00000000000b");
    public static readonly Guid LocA = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    public static readonly Guid LocB = Guid.Parse("00000000-0000-4000-8000-0000000000b1");
    public static readonly Guid LocDb = Guid.Parse("00000000-0000-4000-8000-0000000000d1");
    public static readonly Guid LocNoFb = Guid.Parse("00000000-0000-4000-8000-0000000000a2");
    public static readonly int HostPort = PortFromEnv("ENT_STANDIN_PORT", 18090);
    public static readonly int FakePort = PortFromEnv("ENT_FAKEENTRA_PORT", 18093);
    static int PortFromEnv(string name, int dflt)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return dflt;
        if (!int.TryParse(v, out var p)) throw new InvalidOperationException($"{name}='{v}' is not a port number");
        return p;
    }
    public string HostUrl => $"http://127.0.0.1:{HostPort}";

    public FakeEntraServer Fake { get; private set; } = null!;
    public FixtureEntraStore Store { get; private set; } = null!;
    public static readonly Guid PartnerId = Guid.Parse("f4ce0000-5e20-4a00-8000-0000000000c1"); // SSE2-FAKE-TEST-ONLY
    public RecordingHost Host { get; } = new();
    public ListLogger Logs { get; } = new();
    public Guid UserA = Guid.NewGuid(), UserB = Guid.NewGuid();
    public Guid OidA = Guid.NewGuid(), OidB = Guid.NewGuid();
    /// <summary>The deployment's secret ref (fake, SSE2-FAKE-TEST-ONLY; "9q7z" is the ENT_19 leak marker).</summary>
    public const string DeploymentRef = "arn:sse2-fake-test-only:secret:deployment-9q7z";
    public string DeploymentClientId { get; private set; } = "";
    /// <summary>Generated per run, test-only; never a real secret.</summary>
    public static string NewFakeSecret() => "fixture-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private WebApplication? _app;

    public static async Task<Env> StartAsync(Action<EntraOptions>? tweak = null, bool fakeUp = true, TimeProvider? clock = null)
    {
        var e = new Env();
        PortGuard.Check(HostPort);
        PortGuard.Check(FakePort);
        e.Fake = new FakeEntraServer(FakePort);
        if (fakeUp) await e.Fake.StartAsync();
        static string Sec() => NewFakeSecret(); // generated, test-only
        // One deployment-level client and secret ref for every entra location (TLD 9:50, TLD 10:12, D-51). SSE2-FAKE-TEST-ONLY.
        e.DeploymentClientId = Guid.NewGuid().ToString("D");
        var f = new EntraFixture
        {
            Deployment = new() { Authority = e.Fake.BaseUrl, ClientId = e.DeploymentClientId, SecretRef = DeploymentRef },
            Locations =
            [
                new("loc-a", FixtureConfig.Build(LocA, PartnerId, "loc-a", ProviderKind.Entra, "A (SSE2-FAKE-TEST-ONLY)", [TenantA], allowDbFallback: true, mfaRequired: true)),
                new("loc-b", FixtureConfig.Build(LocB, PartnerId, "loc-b", ProviderKind.Entra, "B (SSE2-FAKE-TEST-ONLY)", [TenantA], allowDbFallback: true, mfaRequired: false)),
                new("loc-nofb", FixtureConfig.Build(LocNoFb, PartnerId, "loc-nofb", ProviderKind.Entra, "A2 no fallback", [TenantB], allowDbFallback: false, mfaRequired: true)),
                new("loc-db", FixtureConfig.Build(LocDb, PartnerId, "loc-db", ProviderKind.Database, "DB", [], allowDbFallback: false, mfaRequired: true)),
            ],
            Secrets = new() { [DeploymentRef] = Sec() },
        };
        var issA = $"{e.Fake.BaseUrl}/{TenantA:D}/v2.0";
        f.Links.Add(new(issA, TenantA, e.OidA, e.UserA, "same@fake.invalid"));
        f.Links.Add(new($"{e.Fake.BaseUrl}/{TenantB:D}/v2.0", TenantB, e.OidB, e.UserB, null));
        f.Users.Add(new(e.UserA, PartnerId, "active", null));
        f.Users.Add(new(e.UserB, PartnerId, "active", null));
        f.Assignments.Add(new(e.UserA, LocA));
        f.Assignments.Add(new(e.UserB, LocNoFb));
        e.Fake.ClientSecrets[e.DeploymentClientId] = f.Secrets[DeploymentRef];
        e.Store = new FixtureEntraStore(f);
        e.Fake.NextUser = new FakeUser { Oid = e.OidA };

        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        b.Logging.ClearProviders(); b.Logging.AddProvider(e.Logs);
        b.WebHost.UseUrls(e.HostUrl);
        b.Services.AddSingleton<ILocationLoginConfigReader>(e.Store);
        b.Services.AddSingleton<IExternalLoginReader>(e.Store);
        b.Services.AddSingleton<IUserLocationReader>(e.Store);
        b.Services.AddSingleton<ISecretResolver>(e.Store);
        b.Services.AddSingleton<IEntraHost>(e.Host);
        if (clock is not null) b.Services.AddSingleton(clock);
        b.Services.AddAuthentication().AddEntraLocationSignIn(b.Environment, o => { o.AllowInsecureTestAuthority = true; tweak?.Invoke(o); });
        var app = b.Build();
        app.UseAuthentication();
        app.MapGet("/l/{slug}", async (HttpContext c, string slug, EntraLocationEndpoint ep) => { await ep.HandleAsync(c, slug, c.Request.Query["returnUrl"]); });
        app.MapGet("/logout", async (HttpContext c, EntraSignOut so) =>
        {
            var loc = Guid.Parse(c.Request.Query["location"]!);
            var sid = c.Request.Cookies["test_sid"];
            var url = await so.SignOutAsync(c, loc, $"{e.HostUrl}/l/loc-a", () =>
            {
                if (sid is not null) e.Host.Sessions.TryRemove(sid, out _);
                e.Host.Timeline.Enqueue((DateTimeOffset.UtcNow, "auth.session.revoked"));
                e.Host.Audit.Enqueue(new("auth.session.revoked", "entra", "sign_out", "", loc, null, null, null));
                return Task.CompletedTask;
            });
            c.Response.Cookies.Delete("test_sid");
            c.Response.Redirect(url ?? "/l/loc-a");
        });
        await app.StartAsync();
        e._app = app;
        return e;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        await Fake.DisposeAsync();
    }

    public sealed record Flow(HttpStatusCode FinalStatus, string FinalBody, string? AuthorizeUrl, List<string> Hops, CookieContainer Jar,
        byte[]? FinalBytes = null, IReadOnlyList<string>? FinalHeaders = null);

    /// <summary>Browser-like flow: /l/{slug} → fake authorize → callback → result. No auto-redirect; every hop recorded.</summary>
    public async Task<Flow> SignInAsync(string slug, Func<string, string>? tamperCallback = null, CookieContainer? jar = null)
    {
        jar ??= new CookieContainer();
        using var h = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = jar, UseCookies = true };
        using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
        var hops = new List<string>();
        string? authorize = null;
        var url = $"{HostUrl}/l/{slug}";
        for (var i = 0; i < 8; i++)
        {
            hops.Add(url);
            var r = await c.GetAsync(url);
            if ((int)r.StatusCode is >= 300 and < 400 && r.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(url), loc).ToString();
                if (next.Contains("/oauth2/v2.0/authorize")) authorize = next;
                if (next.StartsWith(HostUrl + "/signin-entra") && tamperCallback is not null) next = tamperCallback(next);
                url = next; continue;
            }
            var bytes = await r.Content.ReadAsByteArrayAsync();
            var hdrs = r.Headers.Concat(r.Content.Headers).SelectMany(kv => kv.Value.Select(v => kv.Key.ToLowerInvariant() + ": " + v)).ToList();
            return new(r.StatusCode, System.Text.Encoding.UTF8.GetString(bytes), authorize, hops, jar, bytes, hdrs);
        }
        throw new InvalidOperationException("too many redirects");
    }

    public IEnumerable<EntraAuditEvent> Failed => Host.Audit.Where(a => a.EventType == "auth.login.failed");
    public IEnumerable<EntraAuditEvent> Succeeded => Host.Audit.Where(a => a.EventType == "auth.login.succeeded");
    public bool LogHas(string s) => Logs.Lines.Any(l => l.Contains(s, StringComparison.Ordinal));
}
