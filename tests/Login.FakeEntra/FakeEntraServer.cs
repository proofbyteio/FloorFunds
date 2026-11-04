// TEST-ONLY fake Microsoft Entra ID v2 issuer. Tenants are marked SSE2-FAKE-TEST-ONLY. Loopback HTTP only.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
namespace Login.FakeEntra;

public sealed class FakeUser
{
    public Guid Oid { get; set; } = Guid.NewGuid();
    public Guid? TidOverride { get; set; }           // token tid ≠ path tenant
    public string? IssTenantOverride { get; set; }   // iss GUID ≠ tid (ENT-02c)
    public string[] Amr { get; set; } = ["pwd", "mfa"];
    public string? Acct { get; set; }
    public string? Idp { get; set; }
    public int AuthTimeOffsetSec { get; set; } = -5;
    public bool OmitAuthTime { get; set; }
    public string Ver { get; set; } = "2.0";
    public string? Scp { get; set; } = "login.verify";
    public string? AzpOverride { get; set; }
    public string? AudOverride { get; set; }
    public bool AccessTokenIsIdToken { get; set; }
    public bool UnknownKid { get; set; }
    public string Alg { get; set; } = "RS256";
    public string? FixedUti { get; set; }
    public string Email { get; set; } = "cashier@fake.invalid";
}

public sealed record FakeFailure(int Status = 0, int DelayMs = 0, string? AuthorizeError = null);
public sealed record FakeLogEntry(DateTimeOffset At, string Endpoint, string Tenant, IReadOnlyDictionary<string, string> Query);

public sealed class FakeEntraServer : IAsyncDisposable
{
    public const string Marker = "SSE2-FAKE-TEST-ONLY";
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public FakeUser NextUser { get; set; } = new();
    public ConcurrentDictionary<string, FakeFailure> Failures { get; } = new(); // discovery | jwks | token | authorize
    public ConcurrentQueue<FakeLogEntry> Log { get; } = new();
    public ConcurrentDictionary<string, string> ClientSecrets { get; } = new(); // client_id → secret
    public string KeyFile { get; private set; } = "";
    private RSA _key = RSA.Create(2048);
    private string _kid = "";
    private readonly ConcurrentDictionary<string, (string Tenant, string ClientId, string Redirect, string Challenge, string? Nonce, FakeUser User)> _codes = new();
    private WebApplication? _app;

    public FakeEntraServer(int port = 18093) { Port = port; RotateKeys(); }

    public void RotateKeys()
    {
        _key = RSA.Create(2048);
        _kid = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var dir = "/tmp/sse2-fake-entra";
        Directory.CreateDirectory(dir);
        KeyFile = Path.Combine(dir, $"{Environment.ProcessId}-{Port}-{_kid}.key.pem");
        File.WriteAllText(KeyFile, "# " + Marker + "\n" + _key.ExportRSAPrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Reset() { NextUser = new(); Failures.Clear(); Log.Clear(); }

    public async Task StartAsync()
    {
        if (Port is 8080 or 8081 or (>= 18080 and <= 18082)) throw new InvalidOperationException("forbidden port");
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls(BaseUrl);
        var app = b.Build();
        app.MapGet("/{tenant}/v2.0/.well-known/openid-configuration", (Delegate)Discovery);
        app.MapGet("/{tenant}/discovery/v2.0/keys", (Delegate)Jwks);
        app.MapGet("/{tenant}/oauth2/v2.0/authorize", (Delegate)Authorize);
        app.MapPost("/{tenant}/oauth2/v2.0/token", (Delegate)Token);
        app.MapGet("/{tenant}/oauth2/v2.0/logout", (Delegate)Logout);
        app.MapGet("/_fake/log", () => Results.Json(Log.ToArray()));
        await app.StartAsync();
        _app = app;
    }

    public async ValueTask DisposeAsync() { if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); } try { File.Delete(KeyFile); } catch { } }

    private void Record(HttpContext c, string ep, string tenant) =>
        Log.Enqueue(new(DateTimeOffset.UtcNow, ep, tenant, c.Request.Query.ToDictionary(k => k.Key, k => k.Value.ToString())));

    private async Task<bool> Fault(HttpContext c, string ep)
    {
        if (!Failures.TryGetValue(ep, out var f)) return false;
        if (f.DelayMs > 0) await Task.Delay(f.DelayMs);
        if (f.Status > 0) { c.Response.StatusCode = f.Status; await c.Response.WriteAsync("fake failure"); return true; }
        return false;
    }

    private string Iss(string tenant) => $"{BaseUrl}/{tenant}/v2.0";

    private async Task Discovery(HttpContext c, string tenant)
    {
        Record(c, "discovery", tenant);
        if (await Fault(c, "discovery")) return;
        var t = $"{BaseUrl}/{tenant}";
        await c.Response.WriteAsJsonAsync(new JsonObject
        {
            ["issuer"] = Iss(tenant),
            ["authorization_endpoint"] = $"{t}/oauth2/v2.0/authorize",
            ["token_endpoint"] = $"{t}/oauth2/v2.0/token",
            ["end_session_endpoint"] = $"{t}/oauth2/v2.0/logout",
            ["jwks_uri"] = $"{t}/discovery/v2.0/keys",
            ["response_types_supported"] = new JsonArray("code"),
            ["id_token_signing_alg_values_supported"] = new JsonArray("RS256"),
            ["code_challenge_methods_supported"] = new JsonArray("S256"),
            ["tenant_region_scope"] = Marker,
        });
    }

    private async Task Jwks(HttpContext c, string tenant)
    {
        Record(c, "jwks", tenant);
        if (await Fault(c, "jwks")) return;
        var p = _key.ExportParameters(false);
        await c.Response.WriteAsJsonAsync(new { keys = new[] { new { kty = "RSA", use = "sig", kid = _kid, alg = "RS256", n = B64(p.Modulus!), e = B64(p.Exponent!) } } });
    }

    private async Task<IResult> Authorize(HttpContext c, string tenant)
    {
        Record(c, "authorize", tenant);
        var q = c.Request.Query;
        if (Failures.TryGetValue("authorize", out var f) && f.AuthorizeError is not null)
            return Results.Redirect($"{q["redirect_uri"]}?error={f.AuthorizeError}&state={Uri.EscapeDataString(q["state"]!)}");
        if (q["response_type"] != "code" || q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"]))
            return Results.BadRequest("fake: code + PKCE S256 required");
        var code = B64(RandomNumberGenerator.GetBytes(24));
        _codes[code] = (tenant, q["client_id"]!, q["redirect_uri"]!, q["code_challenge"]!, q["nonce"], NextUser);
        await Task.CompletedTask;
        return Results.Redirect($"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"]!)}");
    }

    private async Task<IResult> Token(HttpContext c, string tenant)
    {
        var form = await c.Request.ReadFormAsync();
        Log.Enqueue(new(DateTimeOffset.UtcNow, "token", tenant, form.ToDictionary(k => k.Key, k => k.Key is "client_secret" or "code" or "code_verifier" ? "(redacted)" : k.Value.ToString())));
        if (Failures.TryGetValue("token", out var f))
        {
            if (f.DelayMs > 0) await Task.Delay(f.DelayMs);
            if (f.Status is >= 400 and < 500) return Results.Json(new { error = "invalid_grant", error_description = "fake" }, statusCode: f.Status);
            if (f.Status > 0) return Results.Text("fake failure", statusCode: f.Status);
        }
        if (!_codes.TryRemove(form["code"].ToString(), out var g) || g.Tenant != tenant) return Bad("invalid_grant");
        var cid = form["client_id"].ToString();
        if (cid != g.ClientId || form["redirect_uri"] != g.Redirect) return Bad("invalid_grant");
        if (!ClientSecrets.TryGetValue(cid, out var sec) || sec != form["client_secret"]) return Bad("invalid_client");
        var verifier = form["code_verifier"].ToString();
        if (string.IsNullOrEmpty(verifier) || B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != g.Challenge) return Bad("invalid_grant");

        var u = g.User; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tid = (u.TidOverride ?? Guid.Parse(tenant)).ToString("D");
        var iss = Iss(u.IssTenantOverride ?? tenant);
        var id = new JsonObject
        {
            ["iss"] = iss, ["aud"] = cid, ["tid"] = tid, ["oid"] = u.Oid.ToString("D"), ["sub"] = "pairwise-" + u.Oid.ToString("N")[..12],
            ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600, ["ver"] = "2.0", ["nonce"] = g.Nonce,
            ["preferred_username"] = u.Email, ["email"] = u.Email, ["name"] = Marker + " user",
        };
        if (!u.OmitAuthTime) id["auth_time"] = now + u.AuthTimeOffsetSec;
        if (u.Acct is not null) id["acct"] = int.Parse(u.Acct);
        if (u.Idp is not null) id["idp"] = u.Idp;
        var at = new JsonObject
        {
            ["iss"] = iss, ["aud"] = u.AudOverride ?? cid, ["azp"] = u.AzpOverride ?? cid, ["tid"] = tid, ["oid"] = u.Oid.ToString("D"),
            ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600, ["ver"] = u.Ver, ["amr"] = new JsonArray(u.Amr.Select(a => (JsonNode)a!).ToArray()),
            ["uti"] = u.FixedUti ?? B64(RandomNumberGenerator.GetBytes(12)),
        };
        if (u.Scp is not null) at["scp"] = u.Scp;
        var idJwt = Sign(id, "RS256", _kid);
        var atJwt = u.AccessTokenIsIdToken ? idJwt : Sign(at, u.Alg, u.UnknownKid ? "unknown-kid" : _kid);
        return Results.Json(new { token_type = "Bearer", expires_in = 3600, id_token = idJwt, access_token = atJwt, scope = form["scope"].ToString() });
    }

    private static IResult Bad(string e) => Results.Json(new { error = e }, statusCode: 400);

    private IResult Logout(HttpContext c, string tenant) { Record(c, "endsession", tenant); return Results.Text("signed out (fake)"); }

    private string Sign(JsonObject payload, string alg, string kid)
    {
        var header = new JsonObject { ["alg"] = alg, ["kid"] = kid, ["typ"] = "JWT" };
        var data = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        byte[] sig = alg == "RS256"
            ? _key.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : HMACSHA256.HashData(Encoding.ASCII.GetBytes(kid), Encoding.ASCII.GetBytes(data)); // wrong alg on purpose (ENT-07)
        return data + "." + B64(sig);
    }

    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
