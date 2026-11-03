using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cachd.Login.TestKit;

/// <summary>
/// Live driver for the stand-in (SSE2, Mon Nov 2 2026 ~9:30 PM PT). Uses only what contract-v1 publishes:
/// contract §1.3-1.5 listeners, standin-control.md §2 control API, ui-driver.md §1-2 form fields / testids.
/// Client secrets come from the stand-in fixture file (standin-control §4: "SSE2 reads them from the fixture file").
/// Env: LOGIN_FRONT_URL, LOGIN_BACK_URL, LOGIN_STANDIN_CONTROL_URL (OQ-SSE2-03), LOGIN_STANDIN_FIXTURES (optional path).
/// </summary>
public static class StandIn
{
    public static string Front => Target.FrontUrl ?? throw new InvalidOperationException("LOGIN_FRONT_URL unset");
    public static string Back => Target.BackUrl ?? throw new InvalidOperationException("LOGIN_BACK_URL unset");
    public static string Control => Target.ControlUrl ?? throw new InvalidOperationException("LOGIN_STANDIN_CONTROL_URL unset");

    static readonly HttpClient Ctl = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };

    static JsonObject? _disco;
    public static JsonObject Discovery
    {
        get
        {
            if (_disco is not null) return _disco;
            // contract 1.2: issuer path contains /standin; find it from the front root discovery path used by SI1.
            foreach (var p in new[] { "/standin/.well-known/openid-configuration", "/.well-known/openid-configuration" })
            {
                using var r = Ctl.GetAsync(Front + p).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return _disco = JsonNode.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsObject();
            }
            throw new InvalidOperationException("no discovery document on the front listener");
        }
    }
    public static string Issuer => (string)Discovery["issuer"]!;
    public static string IssuerPath => new Uri(Issuer).AbsolutePath.TrimEnd('/');
    /// contract 1.6: token/revoke/introspect are taken from the back-channel base URL, never from discovery.
    public static string BackEndpoint(string name) => Back + IssuerPath + "/connect/" + name;
    public static string FrontPath(string rel) => Front + IssuerPath + rel;

    public static string? SkipReason() => Target.StandInOnlySkipReason() ?? (Target.BackUrl is null || !Target.Reachable(Target.BackUrl) ? "back listener not set/reachable" : null);

    // ---- control API (standin-control.md §2) ----
    public static async Task Reset() { using var r = await Ctl.PostAsync(Control + "/_standin/reset", null); r.EnsureSuccessStatusCode(); }
    public static async Task<JsonObject> Advance(long seconds) => await CtlJson(HttpMethod.Post, "/_standin/clock/advance", new JsonObject { ["seconds"] = seconds });
    public static async Task<string> Now() => (string)(await CtlJson(HttpMethod.Get, "/_standin/clock", null))["now"]!;
    // OQ-55 (SSE 10a1ca4, v1.1 control addition): frozen clock; while frozen only advance moves time. Advance takes exactly one of seconds|ms.
    public static async Task<JsonObject> AdvanceMs(long ms) => await CtlJson(HttpMethod.Post, "/_standin/clock/advance", new JsonObject { ["ms"] = ms });
    public static async Task<JsonObject> Freeze(bool on) => await CtlJson(HttpMethod.Post, "/_standin/clock/freeze", new JsonObject { ["frozen"] = on });
    // SEC76 fix (10a1ca4): the control TOTP is the earliest UNUSED step's code, 409 {"error":"no_unused_step"} when the window is spent.
    public static async Task<(HttpStatusCode Status, string? Code)> TotpRaw(string user)
    {
        using var r = await Send(HttpMethod.Get, $"/_standin/users/{user}/totp", null);
        var t = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, r.IsSuccessStatusCode ? (string?)JsonNode.Parse(t)!["code"] : null);
    }
    /// Code for the earliest unused step; on 409 advances 30 s once (as SSE's driver does) and asks again.
    public static async Task<string> Totp(string user)
    {
        var (s, c) = await TotpRaw(user);
        if (s == HttpStatusCode.Conflict) { await Advance(30); (s, c) = await TotpRaw(user); }
        return c ?? throw new InvalidOperationException($"control totp {user}: {(int)s}");
    }
    public static async Task<string> PutPc(string pcId, string mode, string slug) =>
        (string)(await CtlJson(HttpMethod.Put, $"/_standin/pcs/{pcId}", new JsonObject { ["mode"] = mode, ["location_slug"] = slug }))["cookie"]!["value"]!;
    public static async Task Faults(string? outbox = null, string? registry = null)
    {
        var o = new JsonObject(); if (outbox is not null) o["outbox"] = outbox; if (registry is not null) o["device_registry"] = registry;
        using var r = await Send(HttpMethod.Put, "/_standin/faults", o); r.EnsureSuccessStatusCode();
    }
    public static async Task<(HttpStatusCode, JsonObject?)> PutUser(string user, JsonObject body)
    {
        using var r = await Send(HttpMethod.Put, $"/_standin/users/{user}", body);
        var t = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, t.Length > 0 ? JsonNode.Parse(t) as JsonObject : null);
    }
    public static async Task<int> AuditNext() => (int)(await CtlJson(HttpMethod.Get, "/_standin/audit?since=0", null))["next"]!;
    public static async Task<List<JsonObject>> AuditSince(int since) =>
        (await CtlJson(HttpMethod.Get, $"/_standin/audit?since={since}", null))["events"]!.AsArray().Select(e => e!.AsObject()).ToList();

    static async Task<HttpResponseMessage> Send(HttpMethod m, string path, JsonNode? body)
    {
        var req = new HttpRequestMessage(m, Control + path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await Ctl.SendAsync(req);
    }
    static async Task<JsonObject> CtlJson(HttpMethod m, string path, JsonNode? body)
    {
        using var r = await Send(m, path, body);
        var t = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"{m} {path} -> {(int)r.StatusCode} {t}");
        return JsonNode.Parse(t)!.AsObject();
    }

    // ---- fixtures (stand-in-owned file; read only) ----
    public static string FixturePath => Environment.GetEnvironmentVariable("LOGIN_STANDIN_FIXTURES")
        ?? Path.Combine(Paths.CodeRoot, "src", "Login.StandIn", "fixtures.json");
    static JsonObject? _fx;
    public static JsonObject Fixtures => _fx ??= JsonNode.Parse(File.ReadAllText(FixturePath))!.AsObject();
    public static JsonObject Client(string id) => Fixtures["clients"]!.AsArray().First(c => (string)c!["client_id"]! == id)!.AsObject();
    public static string Redirect(string id) => (string)Client(id)["redirect_uris"]![0]!;
    // F14 (SSE 10a1ca4, TL 9:42 PM PT): client secrets and recovery codes are no longer committed; the stand-in generates them at
    // start into <state dir>/fixture-secrets.json (0600). Read from LOGIN_STANDIN_SECRETS, else /tmp/sse-d0/standin-{front port}/.
    public static string SecretsPath => Environment.GetEnvironmentVariable("LOGIN_STANDIN_SECRETS")
        ?? $"/tmp/sse-d0/standin-{new Uri(Front).Port}/fixture-secrets.json";
    static JsonObject? _sx;
    public static JsonObject GeneratedSecrets => _sx ??= JsonNode.Parse(File.ReadAllText(SecretsPath))!.AsObject();
    public static string Secret(string clientId)
    {
        var v = (string?)Client(clientId)["secret"];
        if (v is not null && v != "GENERATED") return v;   // pre-F14 builds
        var g = GeneratedSecrets["clients"]![clientId]!;
        return g is JsonValue ? (string)g! : (string)g["secret"]!;
    }
    public static string Password(string user) => (string)Fixtures["users"]!.AsArray().First(u => (string)u!["username"]! == user)!["password"]!;

    // ---- back channel (contract §7, §9, §10) ----
    public static async Task<(HttpStatusCode Status, JsonObject? Body, HttpResponseHeaders Headers)> BackPost(string endpoint, string clientId, IDictionary<string, string> form, string? secretOverride = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BackEndpoint(endpoint)) { Content = new FormUrlEncodedContent(form) };
        var secret = secretOverride ?? Secret(clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(secret))));
        using var r = await Ctl.SendAsync(req);
        var t = await r.Content.ReadAsStringAsync();
        JsonObject? body = null; try { body = t.Length > 0 ? JsonNode.Parse(t) as JsonObject : null; } catch (System.Text.Json.JsonException) { }
        return (r.StatusCode, body, r.Headers);
    }
    public static Task<(HttpStatusCode Status, JsonObject? Body, HttpResponseHeaders Headers)> Token(string clientId, IDictionary<string, string> form) => BackPost("token", clientId, form);
    public static async Task<string> IntrospectRaw(string clientId, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BackEndpoint("introspect")) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + Uri.EscapeDataString(Secret(clientId)))));
        using var r = await Ctl.SendAsync(req);
        return $"{(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}";
    }
    // ---- private_key_jwt (contract 7.6; SSE B2 [TL Decision 9:16 PM]: FIXTURE keys only under /tmp, never in git) ----
    public static string? ClientKeyDir => Environment.GetEnvironmentVariable("LOGIN_STANDIN_CLIENT_KEY_DIR");
    public static System.Security.Cryptography.RSA ClientKey(string clientId)
    {
        var dir = ClientKeyDir ?? throw new InvalidOperationException("LOGIN_STANDIN_CLIENT_KEY_DIR unset");
        if (!Path.GetFullPath(dir).StartsWith("/tmp/", StringComparison.Ordinal)) throw new InvalidOperationException("client key dir must be under /tmp");
        var rsa = System.Security.Cryptography.RSA.Create(); rsa.ImportFromPem(File.ReadAllText(Path.Combine(dir, clientId + ".FIXTURE.key.pem"))); return rsa;
    }
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string B64(string s) => B64(Encoding.UTF8.GetBytes(s));
    /// Builds a client assertion. Defaults are the 7.6 happy path; the mutators make the negatives.
    public static string Assertion(string clientId, Action<JsonObject>? claims = null, Action<JsonObject>? header = null,
        System.Security.Cryptography.RSA? key = null, long? nowUnix = null, Func<string, string>? sign = null)
    {
        var n = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var h = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" }; header?.Invoke(h);
        var c = new JsonObject { ["iss"] = clientId, ["sub"] = clientId, ["aud"] = Issuer, ["jti"] = Guid.NewGuid().ToString("N"), ["iat"] = n, ["exp"] = n + 50 }; claims?.Invoke(c);
        var si = B64(h.ToJsonString()) + "." + B64(c.ToJsonString());
        if (sign is not null) return si + "." + sign(si);
        using var k = key is null ? ClientKey(clientId) : null;
        var sig = (key ?? k!).SignData(Encoding.ASCII.GetBytes(si), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return si + "." + B64(sig);
    }
    /// Back-channel POST with no Basic header unless given (for private_key_jwt and mixed-auth negatives).
    public static async Task<(HttpStatusCode Status, JsonObject? Body)> BackPostRaw(string endpoint, IDictionary<string, string> form, (string Id, string Secret)? basic = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, BackEndpoint(endpoint)) { Content = new FormUrlEncodedContent(form) };
        if (basic is { } b) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(b.Id + ":" + Uri.EscapeDataString(b.Secret))));
        using var r = await Ctl.SendAsync(req);
        var t = await r.Content.ReadAsStringAsync();
        JsonObject? body = null; try { body = t.Length > 0 ? JsonNode.Parse(t) as JsonObject : null; } catch (System.Text.Json.JsonException) { }
        return (r.StatusCode, body);
    }
    public static string RecoveryCode(string user, int i) =>
        Fixtures["users"]!.AsArray().First(u => (string)u!["username"]! == user)!["recovery_codes"] is JsonArray a ? (string)a[i]!
        : (string)GeneratedSecrets["recovery_codes"]![user]![i]!;   // F14

    public static async Task<HttpStatusCode> Revoke(string clientId, string token) =>
        (await BackPost("revoke", clientId, new Dictionary<string, string> { ["token"] = token })).Status;
}

/// <summary>A browser-like client: own cookie jar, no auto-redirects, optional source address (OQ-SSE2-22 / allowlist).</summary>
public sealed class Browser : IDisposable
{
    public CookieContainer Jar { get; } = new();
    readonly HttpClient _h;
    public string? LastAf { get; private set; }
    public Browser(IPAddress? source = null)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = Jar };
        if (source is not null)
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                s.Bind(new IPEndPoint(source, 0));
                await s.ConnectAsync(ctx.DnsEndPoint.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(ctx.DnsEndPoint.Host), ctx.DnsEndPoint.Port, ct);
                return new System.Net.Sockets.NetworkStream(s, true);
            };
        _h = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _h.DefaultRequestHeaders.UserAgent.ParseAdd("sse2-tests/1.0");
    }
    public void Dispose() => _h.Dispose();

    public void SetCookie(string name, string value) => Jar.Add(new Uri(StandIn.Front), new Cookie(name, value, "/"));
    public string? Cookie(string name) => Jar.GetCookies(new Uri(StandIn.FrontPath("/"))).FirstOrDefault(c => c.Name == name)?.Value;

    public async Task<Page> Get(string url) => await Wrap(await _h.GetAsync(Abs(url)));
    public async Task<Page> Post(string url, IDictionary<string, string> form, bool withAf = true)
    {
        var f = new Dictionary<string, string>(form);
        if (withAf && LastAf is not null && !f.ContainsKey("__RequestVerificationToken")) f["__RequestVerificationToken"] = LastAf;
        return await Wrap(await _h.PostAsync(Abs(url), new FormUrlEncodedContent(f)));
    }
    static string Abs(string url) => url.StartsWith("http", StringComparison.Ordinal) ? url : StandIn.Front + url;

    async Task<Page> Wrap(HttpResponseMessage r)
    {
        using (r)
        {
            var body = await r.Content.ReadAsStringAsync();
            var af = Regex.Match(body, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            if (af.Success) LastAf = WebUtility.HtmlDecode(af.Groups[1].Value);
            var headers = r.Headers.Concat(r.Content.Headers).GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            return new Page(r.StatusCode, body, headers, r.Headers.Location);
        }
    }

    // ---- sign-in driver (ui-driver.md §1; contract 6.5) ----
    public string Slug { get; private set; } = "pci-main";
    public Task<Page> OpenSignIn(string slug, string? ret = null) { Slug = slug; return Get(StandIn.IssuerPath + "/l/" + slug + (ret is null ? "" : "?ret=" + Uri.EscapeDataString(ret))); }
    public async Task<string?> AttestNonce()
    {
        var p = await Post(StandIn.IssuerPath + "/l/" + Slug + "/attest-nonce", new Dictionary<string, string>());
        return p.Status == HttpStatusCode.OK ? (string?)JsonNode.Parse(p.Body)!["nonce"] : null;
    }
    /// The fake agent (standin-control 2.4, SI1 assumption A22: on the control listener).
    public static async Task<string?> Agent(string nonce)
    {
        using var h = new HttpClient();
        using var r = await h.PostAsync(StandIn.Control + "/_standin/agent/attest", new StringContent(new JsonObject { ["nonce"] = nonce }.ToJsonString(), Encoding.UTF8, "application/json"));
        return r.IsSuccessStatusCode ? (string?)JsonNode.Parse(await r.Content.ReadAsStringAsync())!["device_attestation"] : null;
    }
    public async Task<Page> SubmitPassword(string user, string password, bool callAgent = true, string? attestation = null)
    {
        string jws = attestation ?? "";
        if (attestation is null && callAgent) { var n = await AttestNonce(); if (n is not null) jws = await Agent(n) ?? ""; }
        return await Post(StandIn.IssuerPath + "/l/" + Slug, new Dictionary<string, string> { ["username"] = user, ["password"] = password, ["device_attestation"] = jws });
    }
    public Task<Page> SubmitTotp(string code) => Post(StandIn.IssuerPath + "/l/" + Slug + "/totp", new Dictionary<string, string> { ["totp_code"] = code });
    /// Full DB sign-in; returns the page after TOTP (hub, or a redirect to ret).
    public async Task<Page> SignIn(string user = "cashier_db", string slug = "pci-main", string? password = null, string? ret = null, bool callAgent = true)
    {
        await OpenSignIn(slug, ret);
        var p = await SubmitPassword(user, password ?? StandIn.Password(user), callAgent);
        if (p.TestId != "screen-totp") return p;
        return await SubmitTotp(await StandIn.Totp(user));
    }

    // ---- hub + hand-off (contract 5.7, 5.8; SI1 9a14163 Handoff.cs) ----
    public Task<Page> Hub() => Get(StandIn.IssuerPath + "/apps");
    /// The tile href for an app (5.7: start_url?hub_launch=), or null when the tile has no link.
    public static string? Tile(Page hub, string app) =>
        Regex.Match(hub.Body, "<a data-testid=\"hub-tile-" + Regex.Escape(app) + "\" href=\"([^\"]+)\"") is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    public static string? HubLaunch(Page hub, string app = "cash_advance") =>
        Tile(hub, app) is { } t && Regex.Match(t, "[?&]hub_launch=([^&]+)") is { Success: true } m ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    /// Step 2 of 5.8: the interstitial's nonce (POST, antiforgery from the interstitial page).
    public async Task<string?> HandoffNonce(bool withAf = true)
    {
        var p = await Post(StandIn.IssuerPath + "/connect/authorize/handoff-nonce", new Dictionary<string, string>(), withAf);
        return p.Status == HttpStatusCode.OK ? (string?)JsonNode.Parse(p.Body)!["nonce"] : null;
    }
    /// Steps 2-4 of 5.8 as the interstitial script does them. attestation: null = ask the fake agent; "" = agent did not answer.
    public async Task<Page> CompleteInterstitial(string? attestation = null, IDictionary<string, string>? extra = null, bool withAf = true)
    {
        var jws = attestation;
        if (jws is null) { var n = await HandoffNonce(); jws = n is null ? "" : await Agent(n) ?? ""; }
        var f = new Dictionary<string, string> { ["device_attestation"] = jws };
        if (extra is not null) foreach (var kv in extra) f[kv.Key] = kv.Value;
        return await Post(StandIn.IssuerPath + "/connect/authorize", f, withAf);
    }
    public static IReadOnlyDictionary<string, string> Query(Uri? loc)
    {
        var q = new Dictionary<string, string>();
        if (loc is not null && loc.IsAbsoluteUri)
            foreach (var kv in loc.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            { var i = kv.IndexOf('='); if (i > 0) q[Uri.UnescapeDataString(kv[..i])] = Uri.UnescapeDataString(kv[(i + 1)..]); }
        return q;
    }
    /// Minimal /admin sign-in (UX 05 ids; SI1 A33). recoveryIndex: use that fixture recovery code instead of TOTP.
    public async Task<Page> AdminSignIn(string user = "partner_admin", string? password = null, int? recoveryIndex = null)
    {
        await Get(StandIn.IssuerPath + "/admin");
        var p = await Post(StandIn.IssuerPath + "/admin", new Dictionary<string, string> { ["username"] = user, ["password"] = password ?? StandIn.Password(user) });
        if (!p.Body.Contains("field-admin-totp")) return p;
        var f = recoveryIndex is { } i ? new Dictionary<string, string> { ["recovery_code"] = StandIn.RecoveryCode(user, i) }
                                       : new Dictionary<string, string> { ["totp_code"] = await StandIn.Totp(user) };
        return await Post(StandIn.IssuerPath + "/admin/mfa", f);
    }

    // ---- OAuth (contract §4) ----
    public sealed record CodeResult(Page Page, string? Code, IReadOnlyDictionary<string, string> Query);
    public async Task<CodeResult> Authorize(string clientId, string challenge, string? scope = null, string? redirect = null, string state = "st-1", string? nonce = "n-1", string? extra = null)
    {
        scope ??= "openid " + (string)StandIn.Client(clientId)["api_scope"]! + " offline_access";
        var url = StandIn.IssuerPath + "/connect/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId)
            + "&redirect_uri=" + Uri.EscapeDataString(redirect ?? StandIn.Redirect(clientId)) + "&scope=" + Uri.EscapeDataString(scope)
            + "&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256" + (nonce is null ? "" : "&nonce=" + nonce) + (extra ?? "");
        var p = await Get(url);
        var q = new Dictionary<string, string>();
        if (p.Location is { } loc && loc.IsAbsoluteUri)
            foreach (var kv in loc.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            { var i = kv.IndexOf('='); q[Uri.UnescapeDataString(kv[..i])] = Uri.UnescapeDataString(kv[(i + 1)..]); }
        return new CodeResult(p, q.GetValueOrDefault("code"), q);
    }
    /// Sign in + authorize + redeem. Returns the token response.
    public async Task<(JsonObject Tokens, string Verifier, string Code)> SignInAndGetTokens(string clientId = "backoffice_stub", string user = "cashier_db", string slug = "pci-main")
    {
        await SignIn(user, slug);
        var v = Pkce.NewVerifier();
        var cr = await Authorize(clientId, Pkce.S256(v));
        if (cr.Code is null) throw new InvalidOperationException($"no code: {(int)cr.Page.Status} {cr.Page.Location} {cr.Page.TestId}");
        var (st, body, _) = await StandIn.Token(clientId, new Dictionary<string, string>
        { ["grant_type"] = "authorization_code", ["code"] = cr.Code!, ["redirect_uri"] = StandIn.Redirect(clientId), ["code_verifier"] = v });
        if (st != HttpStatusCode.OK) throw new InvalidOperationException($"token: {(int)st} {body}");
        return (body!, v, cr.Code!);
    }
}

public sealed record Page(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers, Uri? Location)
{
    // <main data-testid> (stand-in <= 8aab98a) or the first screen-/page- testid on any element (ui/login-hub, f630a8d+; screen-8b wraps page-resign-in).
    public string? TestId => Regex.Match(Body, "<main data-testid=\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value
        : Regex.Match(Body, "data-testid=\"((?:screen|page)-[a-z0-9-]+)\"") is { Success: true } m2 ? m2.Groups[1].Value : null;
    public bool HasTestId(string id) => Body.Contains("data-testid=\"" + id + "\"");
    public string? ErrorText => Regex.Match(Body, "data-testid=\"error-area\"[^>]*>.*?banner__title\">([^<]*)<", RegexOptions.Singleline) is { Success: true } b ? WebUtility.HtmlDecode(b.Groups[1].Value)
        : Regex.Match(Body, "data-testid=\"error-(?:area|admin-sign-in|resign-in)\"[^>]*>([^<]*)<") is { Success: true, Groups: [_, { Length: > 0 }] } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    public string Header(string n) => Headers.TryGetValue(n, out var v) ? v : "";
}
