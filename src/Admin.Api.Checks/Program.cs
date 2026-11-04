// S2a P3 Admin.Api checks (spec §3-§8, §6 matrix, K4). In-process API on a free 127.0.0.1 port in 18130-18139 against a TEST DB
// named in AUTH_CHECKS_DB (ff_sse or ff_kit_*; never ff_sse2). env: AUTH_CHECKS_DB, CONNECTION_STRING (owner: seed reads/cleanup
// only), ADMIN_API_CONNECTION_STRING (the API's role). Optional arg 0: output dir for the schema cross-check corpus (crosscheck.py).
// Test data is SSE2-FAKE-TEST-ONLY (fake tenant/client ids, account 000000000000 ARNs). Never prints connection strings or secrets.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Admin.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

int pass = 0, fail = 0;
void Ok(string name, bool cond, string? detail = null)
{
    if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}{(detail is null ? "" : " :: " + detail)}"); }
}
async Task Check(string name, Func<Task<bool>> f)
{
    try { Ok(name, await f()); } catch (Exception e) { Ok(name, false, $"{e.GetType().Name}: {e.Message}"); }
}

string Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} not set");
var dbName = Environment.GetEnvironmentVariable("AUTH_CHECKS_DB") ?? "";
if (!(dbName == "ff_sse" || System.Text.RegularExpressions.Regex.IsMatch(dbName, "^ff_kit_[a-z0-9_]{1,45}$")))
{ Console.Error.WriteLine("refuse: AUTH_CHECKS_DB must be ff_sse or ff_kit_<name> (never ff_sse2, postgres or a template)"); return 2; }
var ownerCs = Env("CONNECTION_STRING");
var adminCs = Env("ADMIN_API_CONNECTION_STRING");
if (new[] { ownerCs, adminCs }.Any(cs => new NpgsqlConnectionStringBuilder(cs).Database != dbName))
{ Console.Error.WriteLine("refuse: every connection string's Database must equal AUTH_CHECKS_DB"); return 2; }
var outDir = args.Length > 0 ? args[0] : null;

await using var ownerDs = NpgsqlDataSource.Create(ownerCs);
async Task<T> OScalar<T>(string sql, params object?[] ps)
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    return (T)(await cmd.ExecuteScalarAsync())!;
}
async Task<long> Count(string table) => await OScalar<long>($"SELECT count(*) FROM auth.{table}");
string[] Tables = ["audit_outbox", "location_login_config_change", "location_login_config", "location"];
foreach (var t in Tables)
    if (await Count(t) != 0)
    { Console.Error.WriteLine($"refuse: auth.{t} is not empty (run on a migrated, unseeded DB; the checks delete what they create)"); return 2; }
// user_location / session / external_login reference location: refuse if any exist (cleanup would cascade or fail).
foreach (var t in new[] { "user_location", "session", "external_login" })
    if (await Count(t) != 0) { Console.Error.WriteLine($"refuse: auth.{t} is not empty"); return 2; }

// ---------- SSE2-FAKE-TEST-ONLY ids ----------
Guid P_A = Guid.Parse("5f0c3e1a-0001-4000-8000-000000000001"), P_B = Guid.Parse("5f0c3e1a-0002-4000-8000-000000000002");
Guid U_SA = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000a1"), U_PA = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000a2"),
     U_PB = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000b2"), U_RD = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000c1"),
     U_NO = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000c2"), U_CU = Guid.Parse("5f0c3e1a-00ad-4000-8000-0000000000c3");
Guid T1 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa11"), T2 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa12"),
     T3 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa13");
const string ClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc"; // SSE2-FAKE-TEST-ONLY
const string Authority = "https://login.microsoftonline.com";
const string Issuer = "https://admin-login.test.invalid";
string Arn(string name, string region = "us-west-2", string acct = "000000000000") => $"arn:aws:secretsmanager:{region}:{acct}:secret:FAKE-test-only/{name}-AbCdEf";

// ---------- keys, options ----------
using var rsa = RSA.Create(2048);
var signKey = new RsaSecurityKey(rsa) { KeyId = "p3-test" };
var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "p3-test" };
using var otherRsa = RSA.Create(2048);
var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "p3-test" };
var schemaPath = Path.Combine(AppContext.BaseDirectory, "contract", "audit-events.schema.json");
AdminApiOptions Opts(string? cs = null, string? schema = null) => new()
{
    ConnectionString = cs ?? adminCs, EntraAuthority = Authority, EntraClientId = ClientId.ToUpperInvariant(),
    SecretAccountIds = new HashSet<string> { "000000000000" }, SecretRegions = new HashSet<string> { "us-west-2" },
    TokenIssuer = Issuer, SigningKeys = [publicKey], AuditSchemaPath = schema ?? schemaPath,
};

// ---------- startup refusal (fail closed) ----------
void Refuses(string name, Func<AdminApiOptions, AdminApiOptions> mutate, string keyword)
{
    try { mutate(Opts()).Validated(); Ok(name, false, "accepted"); }
    catch (InvalidOperationException e) { Ok(name, e.Message.Contains(keyword, StringComparison.Ordinal), e.Message); }
}
Ok("startup: valid options accepted, client id normalised", Opts().Validated().EntraClientId == ClientId);
foreach (var bad in new[] { "http://login.microsoftonline.com", "https://login.microsoftonline.com/", "https://login.microsoftonline.com/tenant",
                            "https://login.microsoftonline.com?x=1", "https://u:p@login.microsoftonline.com", "https://login.microsoftonline.com#f", "", "login.microsoftonline.com",
                            "https://login.microsoftonline.com:99999", "https://login..microsoftonline.com" })
    Refuses($"startup: Entra:Authority '{bad}' refused", o => o with { EntraAuthority = bad }, "Entra:Authority");
foreach (var bad in new[] { "", "not-a-guid", "00000000-0000-0000-0000-000000000000", "{5f0c3e1a-00cc-4000-8000-0000000000cc}" })
    Refuses($"startup: Entra:ClientId '{bad}' refused", o => o with { EntraClientId = bad }, "Entra:ClientId");
Refuses("startup: empty SecretRef:AllowedAccountIds refused", o => o with { SecretAccountIds = new HashSet<string>() }, "AllowedAccountIds");
Refuses("startup: bad account id refused", o => o with { SecretAccountIds = new HashSet<string> { "12345" } }, "AllowedAccountIds");
Refuses("startup: empty SecretRef:AllowedRegions refused", o => o with { SecretRegions = new HashSet<string>() }, "AllowedRegions");
Refuses("startup: bad region refused", o => o with { SecretRegions = new HashSet<string> { "US-WEST-2" } }, "AllowedRegions");
Refuses("startup: no signing keys refused", o => o with { SigningKeys = [] }, "JwksPath");
Refuses("startup: symmetric signing key refused", o => o with { SigningKeys = [new SymmetricSecurityKey(new byte[32])] }, "JwksPath");
Refuses("startup: missing audit schema refused", o => o with { AuditSchemaPath = "/nonexistent/audit.json" }, "audit-events");
await Check("startup: owner role refused by the DB guard (owns auth objects)", async () =>
{
    try { await using var a = await AdminApiHost.BuildAsync(Opts(ownerCs)); return false; }
    catch (InvalidOperationException e) { return e.Message.Contains("owns auth objects"); }
});
await Check("startup: schema with an unknown keyword refused", async () =>
{
    var p = Path.Combine(Path.GetTempPath(), $"p3-badschema-{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(p, """{"type":"object","patternProperties":{"x":{}}}""");
    try { await using var a = await AdminApiHost.BuildAsync(Opts(schema: p)); return false; }
    catch (Exception) { return true; }
    finally { File.Delete(p); }
});
// The real entry point (Program.cs): invalid config -> exit 2 and "admin-api refused to start", value not echoed.
await Check("startup: Admin.Api process exits 2 on invalid Entra:Authority, value not echoed", async () =>
{
    var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Admin.Api", "bin", "Debug", "net10.0", "Admin.Api.dll");
    var jwks = Path.Combine(Path.GetTempPath(), $"p3-jwks-{Guid.NewGuid():N}.json");
    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
    await File.WriteAllTextAsync(jwks, JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } } }));
    try
    {
        var psi = new ProcessStartInfo("dotnet", [dll]) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.Environment["ADMIN_API_CONNECTION_STRING"] = adminCs;
        psi.Environment["Entra__Authority"] = "https://evil.example/SECRETPATH";
        psi.Environment["Entra__ClientId"] = ClientId;
        psi.Environment["SecretRef__AllowedAccountIds"] = "000000000000";
        psi.Environment["SecretRef__AllowedRegions"] = "us-west-2";
        psi.Environment["BackofficeAuth__Issuer"] = Issuer;
        psi.Environment["BackofficeAuth__JwksPath"] = jwks;
        psi.Environment["Urls"] = "http://127.0.0.1:1"; // never reached
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode == 2 && err.Contains("admin-api refused to start: config Entra:Authority") && !err.Contains("SECRETPATH");
    }
    finally { File.Delete(jwks); }
});

// ---------- port ----------
int FreePort(int skip = -1)
{
    for (int port = 18130; port <= 18139; port++)
    {
        if (port == skip) continue;
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return port; } catch (SocketException) { }
    }
    throw new InvalidOperationException("no free port in 18130-18139");
}

// ---------- directory: interim token roles + injected readers/non-admins ----------
var readerLocations = new HashSet<Guid>();
var dir = new TestDirectory(readerLocations, U_RD, U_NO, U_CU);

var port = FreePort();
Console.WriteLine($"INFO port {port}");
var app = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port}" }, dir);
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

// ---------- tokens ----------
var jwtH = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
string Token(string adminType, Guid user, Guid? partner = null, Action<Dictionary<string, object>>? edit = null, SecurityKey? key = null,
    string typ = "at+jwt", string alg = SecurityAlgorithms.RsaSha256)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var claims = new Dictionary<string, object>
    {
        ["iss"] = Issuer, ["aud"] = AdminApiOptions.Audience, ["sub"] = user.ToString("D"), ["admin_type"] = adminType,
        ["iat"] = now, ["nbf"] = now, ["exp"] = now + 600, ["jti"] = Guid.NewGuid().ToString("D"),
    };
    if (partner is { } p) claims["partner_id"] = p.ToString("D");
    edit?.Invoke(claims);
    return jwtH.CreateToken(new SecurityTokenDescriptor { Claims = claims, SigningCredentials = new SigningCredentials(key ?? signKey, alg), TokenType = typ });
}
var SA = Token("super", U_SA);
var PA = Token("partner", U_PA, P_A);
var PB = Token("partner", U_PB, P_B);
var RD = Token("partner", U_RD, P_A);   // org:V reader of listed locations (no admin role)
var NO = Token("partner", U_NO, P_A);   // signed in, no role at all
var CU = Token("partner", U_CU, P_A);   // custom role carrying the write permission (ignored: not a built-in role)

// ---------- HTTP ----------
var allBodies = new List<string>();
var allCorr = new List<string?>();
bool IsCorr(string? v) => v is not null && Guid.TryParseExact(v, "D", out var g) && g.Version == 7;
var responsesForSchema = new List<(string def, string body)>();
async Task<R> Send(HttpMethod m, string path, string? token, string? body = null, string? ifMatch = null, Action<HttpRequestMessage>? edit = null)
{
    using var req = new HttpRequestMessage(m, path);
    if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
    edit?.Invoke(req);
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    allBodies.Add(text);
    if ((int)res.StatusCode is >= 400 and < 500 and not 401 && text.Length > 0) responsesForSchema.Add(("error_response", text));
    string? H(string k) => res.Headers.TryGetValues(k, out var v) ? string.Join(",", v) : res.Content.Headers.TryGetValues(k, out var cv) ? string.Join(",", cv) : null;
    var r = new R((int)res.StatusCode, text, H("ETag"), H("Location"), H("WWW-Authenticate"), H("Cache-Control"), H("Server"), H(LoginConfigApi.CorrelationHeader));
    allCorr.Add(r.Corr);
    return r;
}
Task<R> Get(string path, string? tok) => Send(HttpMethod.Get, path, tok);
Task<R> Put(Guid loc, string? tok, object body, string? ifMatch) => Send(HttpMethod.Put, $"/api/orgs/locations/{loc:D}/login-config", tok, J(body), ifMatch);
Task<R> Post(string? tok, object body) => Send(HttpMethod.Post, "/api/orgs/locations", tok, J(body));
string Cfg(Guid l) => $"/api/orgs/locations/{l:D}/login-config";
static string J(object o) => o is string s ? s : JsonSerializer.Serialize(o);
static JsonObject Obj(string s) => JsonNode.Parse(s)!.AsObject();
bool Err(R r, int status, string code) => r.Status == status && Obj(r.Body)["error"]?.GetValue<string>() == code;
bool HasField(R r, string field, string code) => r.Status == 422 && Obj(r.Body)["fields"]!.AsArray().Any(f => (string)f!["field"]! == field && (string)f["code"]! == code);
void ForSchema(string def, R r) => responsesForSchema.Add((def, r.Body));

// ---------- outbox (read as owner) ----------
var seen = new HashSet<Guid>();
var allEvents = new List<JsonObject>();
async Task<List<JsonObject>> NewEvents()
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT event_id, envelope::text FROM auth.audit_outbox ORDER BY occurred_at, event_id", c);
    var list = new List<JsonObject>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        if (seen.Add(r.GetGuid(0))) { var e = Obj(r.GetString(1)); list.Add(e); allEvents.Add(e); }
    return list;
}
var schemaCases = new List<(string n, JsonObject doc)>();
var lite = JsonSchemaLite.Load(await File.ReadAllTextAsync(schemaPath));
bool Valid(JsonObject e) => lite.Validate(e).Count == 0;
// targetId null = a not_admin create whose body gave no valid partner_id: target.id and scope.partner_id both absent (OQ-P3-10).
// details.location_id: the location on update, JSON null on create [TLD 9:04, OQ-P3-2].
bool Denied(List<JsonObject> evs, string reason, Guid? targetId, string? adminType, string op, params string[] attempted) =>
    evs.Count == 1 && Valid(evs[0]) && (string)evs[0]["event_type"]! == "auth.config.change_denied" && (string)evs[0]["details"]!["reason"]! == reason
    && (string?)evs[0]["target"]!["id"] == targetId?.ToString("D")
    && (op != "create" || (string?)evs[0]["scope"]!["partner_id"] == targetId?.ToString("D"))
    && evs[0]["details"]!.AsObject().ContainsKey("location_id")
    && (op == "create" ? evs[0]["details"]!["location_id"] is null && evs[0]["scope"]!["location_ids"]!.AsArray().Count == 0
                       : (string?)evs[0]["details"]!["location_id"] == targetId?.ToString("D"))
    && (string?)evs[0]["admin_type"] == adminType
    && (string)evs[0]["details"]!["operation"]! == op
    && evs[0]["details"]!["attempted_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(attempted);

Dictionary<string, object?> DbCfg(string display = "Riverbend Casino", bool mfa = true, object? extra = null) => Merge(new Dictionary<string, object?>
{
    ["provider_kind"] = "database", ["display_name"] = display, ["entra_allowed_tenant_ids"] = Array.Empty<string>(),
    ["allow_db_fallback"] = false, ["mfa_required"] = mfa,
}, extra);
Dictionary<string, object?> EntraCfg(Guid tenant, string? secret, bool fallback = false, bool mfa = true, string display = "Riverbend Casino")
{
    var d = new Dictionary<string, object?>
    {
        ["provider_kind"] = "entra", ["display_name"] = display, ["entra_allowed_tenant_ids"] = new[] { tenant.ToString("D") },
        ["allow_db_fallback"] = fallback, ["mfa_required"] = mfa,
    };
    if (secret is not null) d["secret_ref"] = secret;
    return d;
}
static Dictionary<string, object?> Merge(Dictionary<string, object?> d, object? extra)
{
    if (extra is Dictionary<string, object?> x) foreach (var (k, v) in x) d[k] = v;
    return d;
}
Dictionary<string, object?> With(Dictionary<string, object?> d, string k, object? v) { var n = new Dictionary<string, object?>(d) { [k] = v }; return n; }
Dictionary<string, object?> Without(Dictionary<string, object?> d, string k) { var n = new Dictionary<string, object?>(d); n.Remove(k); return n; }
object Create(Guid partner, string name, object cfg) => new Dictionary<string, object?> { ["partner_id"] = partner.ToString("D"), ["name"] = name, ["login_config"] = cfg };
Guid LocOf(R r) => Guid.Parse((string)Obj(r.Body)["location_id"]!);
long Rv(Guid loc) => OScalar<long>("SELECT row_version FROM auth.location_login_config WHERE location_id = $1", loc).GetAwaiter().GetResult();

int exitCode = 1;
try
{
    var missing = Guid.Parse("5f0c3e1a-0bad-4000-8000-00000000dead");

    // ================= 1. authentication (step 1): 401, no body, WWW-Authenticate: Bearer, no event =================
    var bad401 = new (string name, string? tok)[]
    {
        ("no Authorization header", null),
        ("bad signature", Token("super", U_SA, key: otherKey)),
        ("wrong aud", Token("super", U_SA, edit: c => c["aud"] = "login-api")),
        ("aud array", Token("super", U_SA, edit: c => c["aud"] = new[] { "backoffice-api", "other" })),
        ("wrong iss", Token("super", U_SA, edit: c => c["iss"] = "https://evil.invalid")),
        ("expired", Token("super", U_SA, edit: c => { c["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(); c["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(); })),
        ("no exp", Token("super", U_SA, edit: c => c.Remove("exp"))),
        ("typ JWT", Token("super", U_SA, typ: "JWT")),
        ("HS256", Token("super", U_SA, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "p3-test" }, alg: SecurityAlgorithms.HmacSha256)),
        ("alg none", Base64Url(@"{""alg"":""none"",""typ"":""at+jwt""}") + "." + Base64Url(JsonSerializer.Serialize(new { iss = Issuer, aud = "backoffice-api", sub = U_SA.ToString(), admin_type = "super", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })) + "."),
        ("partner without partner_id", Token("partner", U_PA)),
        ("partner with bad partner_id", Token("partner", U_PA, edit: c => c["partner_id"] = "nope")),
        ("super with partner_id", Token("super", U_SA, P_A)),
        ("location_id present", Token("partner", U_PA, P_A, c => c["location_id"] = Guid.NewGuid().ToString())),
        ("sub not a uuid", Token("super", U_SA, edit: c => c["sub"] = "alice")),
        ("admin_type location", Token("location", U_SA)),
        ("recovery re-enrol session", Token("super", U_SA, edit: c => c["session_kind"] = "recovery_reenroll")),
        ("garbage", "not.a.token"),
    };
    foreach (var (name, tok) in bad401)
    {
        foreach (var (m, path, body) in new (HttpMethod, string, string?)[] { (HttpMethod.Get, Cfg(missing), null), (HttpMethod.Put, Cfg(missing), "{}"), (HttpMethod.Post, "/api/orgs/locations", "{}") })
        {
            var r = await Send(m, path, tok, body, m == HttpMethod.Put ? "\"1\"" : null);
            Ok($"401 {m} {name}", r.Status == 401 && r.Body.Length == 0 && r.WwwAuth == "Bearer", $"{r.Status} {r.Body}");
        }
    }
    {
        var r = await Send(HttpMethod.Get, Cfg(missing), null, edit: q => q.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("a:b"))));
        Ok("401 Basic scheme", r.Status == 401 && r.WwwAuth == "Bearer");
        r = await Send(HttpMethod.Get, Cfg(missing), null, edit: q => { q.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SA); q.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SA); });
        Ok("401 two Authorization headers", r.Status == 401);
        r = await Send(HttpMethod.Get, Cfg(missing), "x" + new string('a', 9000));
        Ok("401 oversized token", r.Status == 401);
    }
    Ok("401: no event written", (await NewEvents()).Count == 0);

    // ================= 2. create (POST) =================
    var rA1 = await Post(PA, Create(P_A, "Riverbend Cage 2", DbCfg()));
    var A1 = rA1.Status == 201 ? LocOf(rA1) : Guid.Empty;
    Ok("create: PA own partner database -> 201", rA1.Status == 201, rA1.Body);
    ForSchema("login_config_view_restricted", rA1);
    {
        var b = Obj(rA1.Body);
        Ok("create: ETag \"1\", Location header, Cache-Control no-store, no Server header",
            rA1.ETag == "\"1\"" && rA1.Location == $"/api/orgs/locations/{A1:D}/login-config" && rA1.CacheControl == "no-store" && rA1.Server is null);
        Ok("create: restricted view (no oidc_* keys), secret not set", !b.ContainsKey("oidc_authority") && !b.ContainsKey("oidc_client_id")
            && (bool)b["secret"]!["set"]! == false && b["secret"]!["rotated_at"] is null && (long)b["row_version"]! == 1);
        Ok("create: slug server-generated (4x4 base32)", System.Text.RegularExpressions.Regex.IsMatch((string)b["slug"]!, "^[0-9a-hjkmnp-tv-z]{4}(-[0-9a-hjkmnp-tv-z]{4}){3}$"));
        Ok("create: created_by/updated_by = token sub", (string)b["created_by"]! == U_PA.ToString() && (string)b["updated_by"]! == U_PA.ToString());
        var ev = await NewEvents();
        Ok("create: one valid auth.provider.changed event (action create, admin_type partner, changed_fields vs defaults)",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["event_type"]! == "auth.provider.changed" && (string)ev[0]["action"]! == "create"
            && (string)ev[0]["admin_type"]! == "partner" && (string)ev[0]["scope"]!["partner_id"]! == P_A.ToString()
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "display_name", "mfa_required"])
            && ev[0]["details"]!["before"] is null, ev.Count > 0 ? ev[0].ToJsonString() : "none");
        Ok("create: change row 'created' row_version 1", await OScalar<long>("SELECT count(*) FROM auth.location_login_config_change WHERE location_id=$1 AND change_kind='created' AND row_version=1", A1) == 1);
    }

    var rB1 = await Post(SA, Create(P_B, "North Valley Cage 1", EntraCfg(T1, Arn("nv-main"))));
    var B1 = rB1.Status == 201 ? LocOf(rB1) : Guid.Empty;
    Ok("create: SA entra in partner B -> 201", rB1.Status == 201, rB1.Body);
    ForSchema("login_config_view_super", rB1);
    {
        var b = Obj(rB1.Body);
        Ok("create: super view shows server-filled oidc_* from Entra:Authority / Entra:ClientId",
            (string?)b["oidc_authority"] == Authority && (string?)b["oidc_client_id"] == ClientId && (bool)b["secret"]!["set"]! && b["secret"]!["rotated_at"] is not null);
        var ev = await NewEvents();
        Ok("create: SA event lists oidc_* and secret_ref in changed_fields, no value",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super"
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "display_name", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref", "mfa_required"]));
        Ok("create: secret_ref stored (owner read) but write-only", await OScalar<string>("SELECT secret_ref FROM auth.location_login_config WHERE location_id=$1", B1) == Arn("nv-main"));
    }
    {
        var r = await Post(PA, Create(P_B, "Sneaky", DbCfg()));
        Ok("create: PA other partner -> 404 not_found (identical body)", Err(r, 404, "not_found") && r.Body == """{"error":"not_found"}""");
        Ok("create: PA other partner -> forbidden_scope event (target partner, scope [])", Denied(await NewEvents(), "forbidden_scope", P_B, "partner", "create",
            "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        Ok("create: nothing created for partner B by PA", await OScalar<long>("SELECT count(*) FROM auth.location WHERE partner_id=$1", P_B) == 1);
    }
    {
        var r = await Post(PA, Create(P_A, "No MFA", DbCfg(mfa: false)));
        Ok("create: PA mfa_required=false -> 403 not_super_admin", Err(r, 403, "not_super_admin"));
        Ok("create: not_super_admin event (attempted mfa_required)", Denied(await NewEvents(), "not_super_admin", P_A, "partner", "create", "mfa_required"));
        r = await Post(PA, Create(P_A, "Fallback", EntraCfg(T2, Arn("a-fb"), fallback: true)));
        Ok("create: PA allow_db_fallback=true -> 403 not_super_admin + event", Err(r, 403, "not_super_admin")
            && Denied(await NewEvents(), "not_super_admin", P_A, "partner", "create", "allow_db_fallback"));
    }
    Guid A2;
    {
        var r = await Post(SA, Create(P_A, "Fallback Cage", EntraCfg(T2, Arn("a-fb"), fallback: true)));
        Ok("create: SA fallback without reason -> 400 reason_required, no event", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0);
        r = await Post(SA, Create(P_A, "Fallback Cage", With(EntraCfg(T2, Arn("a-fb"), fallback: true), "reason", "  short  ")));
        Ok("create: SA reason under 10 after trim -> 422 login_config/reason reason_length", HasField(r, "login_config/reason", "reason_length"));
        r = await Post(SA, Create(P_A, "Fallback Cage", With(EntraCfg(T2, Arn("a-fb"), fallback: true), "reason", "   Pilot fallback per OPS-77   ")));
        A2 = r.Status == 201 ? LocOf(r) : Guid.Empty;
        var ev = await NewEvents();
        Ok("create: SA fallback + reason -> 201, reason stored trimmed in event and change row", r.Status == 201 && ev.Count == 1 && Valid(ev[0])
            && (string?)ev[0]["details"]!["reason"] == "Pilot fallback per OPS-77"
            && await OScalar<string>("SELECT reason FROM auth.location_login_config_change WHERE location_id=$1", A2) == "Pilot fallback per OPS-77", r.Body);
    }
    {
        var r = await Post(SA, new Dictionary<string, object?> { ["partner_id"] = P_A.ToString(), ["name"] = "No provider" });
        Ok("create: no login_config -> 422 login_config required (a location needs a provider)", HasField(r, "login_config", "required"));
        r = await Post(SA, Create(P_A, "x", Without(DbCfg(), "provider_kind")));
        Ok("create: no provider_kind -> 422 login_config/provider_kind required", HasField(r, "login_config/provider_kind", "required"));
        r = await Post(SA, new Dictionary<string, object?> { ["partner_id"] = "nope", ["name"] = "  ", ["login_config"] = DbCfg(), ["slug"] = "aaaa-bbbb-cccc-dddd", ["row_version"] = 5, ["created_by"] = U_SA.ToString() });
        Ok("create: partner_id_format, name_invalid, slug/row_version/created_by unknown_field (all collected)",
            HasField(r, "partner_id", "partner_id_format") && HasField(r, "name", "name_invalid") && HasField(r, "slug", "unknown_field")
            && HasField(r, "row_version", "unknown_field") && HasField(r, "created_by", "unknown_field"), r.Body);
        r = await Post(SA, Create(P_A, "x", EntraCfg(T3, null)));
        Ok("create: entra without secret_ref -> secret_ref_required", HasField(r, "login_config/secret_ref", "secret_ref_required"));
        r = await Post(SA, Create(P_A, "x", With(With(EntraCfg(T3, Arn("x")), "oidc_authority", "https://evil.invalid"), "oidc_client_id", ClientId)));
        Ok("create: oidc_* in login_config -> field_not_writable (both)", HasField(r, "login_config/oidc_authority", "field_not_writable") && HasField(r, "login_config/oidc_client_id", "field_not_writable"));
        r = await Post(SA, Create(P_A, "x", "not an object"));
        Ok("create: login_config not an object -> login_config type", HasField(r, "login_config", "type"));
        Ok("create: field errors write no event", (await NewEvents()).Count == 0);
        r = await Post(SA, Create(P_A, "Tenant thief", EntraCfg(T1, Arn("thief"))));
        Ok("create: SA tenant bound to partner B -> 422 tenant_bound_elsewhere", HasField(r, "login_config/entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        Ok("create: tenant_bound_elsewhere event (super, target partner A)", Denied(await NewEvents(), "tenant_bound_elsewhere", P_A, "super", "create", "entra_allowed_tenant_ids"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "[1,2]");
        Ok("create: non-object body -> 400 malformed_body", Err(r, 400, "malformed_body"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, """{"partner_id":"x","partner_id":"y"}""");
        Ok("create: duplicate keys -> 400 malformed_body", Err(r, 400, "malformed_body"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "{\"name\":\"" + new string('a', 70_000) + "\"}");
        Ok("create: body over 64 KiB -> 400 malformed_body", Err(r, 400, "malformed_body"));
        // TLD 9:04 #2: a refused create is audited (location_id null, target = body partner_id). #8: the 403 is decided before the body.
        foreach (var (n, t) in new[] { ("reader", RD), ("no role", NO), ("custom role", CU) })
        {
            r = await Post(t, Create(P_A, "x", DbCfg()));
            Ok($"create: {n} -> 403 forbidden + not_admin event (target partner A, location_id null, no admin_type/actor_partner_id)", Err(r, 403, "forbidden")
                && Denied(await NewEvents(), "not_admin", P_A, null, "create", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        }
        r = await Post(RD, Create(P_B, "x", EntraCfg(T3, Arn("rd"))));
        Ok("create: reader naming partner B -> 403 + not_admin event targeting partner B (from the body)", Err(r, 403, "forbidden")
            && Denied(await NewEvents(), "not_admin", P_B, null, "create", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required"));
        var bigName = new string('a', 2 * 1024 * 1024);
        var unreadable = new (string n, Func<HttpContent> body)[]
        {
            ("malformed JSON", () => new StringContent("{\"partner_id\":", Encoding.UTF8, "application/json")),
            ("non-object", () => new StringContent("[1,2]", Encoding.UTF8, "application/json")),
            ("duplicate keys", () => new StringContent(J(Create(P_A, "x", DbCfg()))[..^1] + ",\"partner_id\":\"" + P_B + "\"}", Encoding.UTF8, "application/json")),
            ("partner_id not a uuid", () => new StringContent(J(new Dictionary<string, object?> { ["partner_id"] = "nope", ["name"] = "x" }), Encoding.UTF8, "application/json")),
            ("over 64 KiB", () => new StringContent(J(Create(P_A, new string('a', 70_000), DbCfg())), Encoding.UTF8, "application/json")),
            ("over 1 MiB (Kestrel limit)", () => new StringContent(J(Create(P_A, bigName, DbCfg())), Encoding.UTF8, "application/json")),
            ("over 1 MiB chunked", () => { var c = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(J(Create(P_A, bigName, DbCfg()))))); return c; }),
        };
        foreach (var (n, body) in unreadable)
        {
            void Body(HttpRequestMessage q) { q.Content = body(); if (n.EndsWith("chunked")) q.Headers.TransferEncodingChunked = true; }
            r = await Send(HttpMethod.Post, "/api/orgs/locations", null, edit: Body);
            Ok($"create #8: unauthenticated + {n} -> 401 (never 400/413), no body, no event", r.Status == 401 && r.Body.Length == 0 && r.WwwAuth == "Bearer" && (await NewEvents()).Count == 0, $"{r.Status}");
            r = await Send(HttpMethod.Post, "/api/orgs/locations", NO, edit: Body);
            Ok($"create #8: no permission + {n} -> 403 forbidden (never 400/413) + not_admin event, partner unknown (OQ-P3-10)", Err(r, 403, "forbidden")
                && Denied(await NewEvents(), "not_admin", null, null, "create"), $"{r.Status} {r.Body}");
            r = await Send(HttpMethod.Put, Cfg(A1), CU, ifMatch: "\"1\"", edit: Body);
            Ok($"put #8: no permission + {n} -> 403 forbidden (never 400/413) + not_admin event (attempted none)", Err(r, 403, "forbidden")
                && Denied(await NewEvents(), "not_admin", A1, null, "update"), $"{r.Status} {r.Body}");
        }
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "{\"name\":\"" + bigName + "\"}");
        Ok("create: SA body over 1 MiB -> 400 malformed_body (handler cap; never 413/500)", Err(r, 400, "malformed_body"), $"{r.Status}");
        Ok("create: oversized SA body writes no event", (await NewEvents()).Count == 0);
        Ok("create: none of the refused creates added a location", await Count("location") == 3);
    }

    // ================= 3. GET =================
    readerLocations.Add(A1);
    {
        var r = await Get(Cfg(A1), PA);
        Ok("get: PA own -> 200 restricted view, ETag", r.Status == 200 && r.ETag == "\"1\"" && !Obj(r.Body).ContainsKey("oidc_authority"));
        ForSchema("login_config_view_restricted", r);
        r = await Get(Cfg(B1), SA);
        Ok("get: SA any -> 200 super view with oidc_*", r.Status == 200 && (string?)Obj(r.Body)["oidc_client_id"] == ClientId);
        ForSchema("login_config_view_super", r);
        var miss = await Get(Cfg(missing), PA);
        var oos = await Get(Cfg(B1), PA);
        Ok("get: PA other partner -> 404, body identical to missing", Err(oos, 404, "not_found") && oos.Body == miss.Body && Err(miss, 404, "not_found"));
        Ok("get: SA missing -> 404", Err(await Get(Cfg(missing), SA), 404, "not_found"));
        Ok("get: bad id -> 404", Err(await Get("/api/orgs/locations/not-a-uuid/login-config", SA), 404, "not_found"));
        r = await Get(Cfg(A1), RD);
        Ok("get: org:V reader of the location -> 200 restricted", r.Status == 200 && !Obj(r.Body).ContainsKey("oidc_authority"));
        ForSchema("login_config_view_restricted", r);
        Ok("get: org:V reader of another location -> 404", Err(await Get(Cfg(A2), RD), 404, "not_found"));
        Ok("get: no role -> 403 forbidden", Err(await Get(Cfg(A1), NO), 403, "forbidden"));
        Ok("get: custom role -> 403 forbidden", Err(await Get(Cfg(A1), CU), 403, "forbidden"));
        Ok("get: reads write no event", (await NewEvents()).Count == 0);
    }

    // ================= 4. PUT =================
    {
        var body = DbCfg("Riverbend Casino Main");
        Ok("put: no If-Match -> 428 precondition_required", Err(await Put(A1, PA, body, null), 428, "precondition_required"));
        foreach (var im in new[] { "*", "W/\"1\"", "\"1\", \"2\"", "\"abc\"", "1", "\"01\"", "\"0\"", "\"99999999999999999999\"" })
            Ok($"put: If-Match {im} -> 400 if_match_invalid", Err(await Put(A1, PA, body, im), 400, "if_match_invalid"));
        var r = await Put(A1, PA, body, "\"7\"");
        Ok("put: stale If-Match -> 409 + current_row_version", Err(r, 409, "concurrency_conflict") && (long)Obj(r.Body)["current_row_version"]! == 1);
        r = await Send(HttpMethod.Put, Cfg(A1), PA, "{not json", "\"7\"");
        Ok("put: step 6 before step 7 (stale + malformed -> 409)", Err(r, 409, "concurrency_conflict"));
        r = await Send(HttpMethod.Put, Cfg(A1), PA, "{not json", "\"1\"");
        Ok("put: malformed body -> 400 malformed_body", Err(r, 400, "malformed_body"));
        r = await Send(HttpMethod.Put, Cfg(A1), PA, """{"provider_kind":"database","provider_kind":"entra"}""", "\"1\"");
        Ok("put: duplicate keys -> 400 malformed_body", Err(r, 400, "malformed_body"));
        Ok("put: steps 5-7 write no event", (await NewEvents()).Count == 0);

        // K4: audit/server-set columns in the body -> 422 unknown_field, all collected.
        var k4 = Merge(DbCfg(), new Dictionary<string, object?>
        {
            ["updated_by"] = U_SA.ToString(), ["updated_at"] = "2020-01-01T00:00:00Z", ["row_version"] = 99, ["created_by"] = U_SA.ToString(),
            ["created_at"] = "2020-01-01T00:00:00Z", ["slug"] = "aaaa-bbbb-cccc-dddd", ["secret"] = new { set = true }, ["location_id"] = A2.ToString(),
            ["partner_id"] = P_B.ToString(), ["oidc_authority"] = "https://evil.invalid", ["reason"] = "short", ["x'; DROP TABLE auth.location; --"] = 1,
        });
        r = await Put(A1, PA, k4, "\"1\"");
        var fields = r.Status == 422 ? Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}").ToHashSet() : [];
        Ok("put K4: audit columns updated_by/updated_at/row_version/created_by/created_at -> 422 unknown_field",
            new[] { "updated_by", "updated_at", "row_version", "created_by", "created_at", "slug", "secret", "location_id", "partner_id" }.All(k => fields.Contains($"{k}:unknown_field")), r.Body);
        Ok("put K4: oidc_authority field_not_writable + reason_length collected in the same 422", fields.Contains("oidc_authority:field_not_writable") && fields.Contains("reason:reason_length"));
        Ok("put K4: unsafe key name not echoed (reported as *)", fields.Contains("*:unknown_field") && !r.Body.Contains("DROP"));
        Ok("put K4: row unchanged after the 422", Rv(A1) == 1 && (await NewEvents()).Count == 0);

        // Field codes.
        r = await Put(A1, PA, new Dictionary<string, object?> { ["provider_kind"] = "Entra", ["display_name"] = new string('d', 101), ["entra_allowed_tenant_ids"] = "x", ["allow_db_fallback"] = "no", ["mfa_required"] = 1 }, "\"1\"");
        Ok("put: provider_kind_invalid, display_name_invalid, type x3", HasField(r, "provider_kind", "provider_kind_invalid") && HasField(r, "display_name", "display_name_invalid")
            && HasField(r, "entra_allowed_tenant_ids", "type") && HasField(r, "allow_db_fallback", "type") && HasField(r, "mfa_required", "type"), r.Body);
        r = await Put(A1, PA, new Dictionary<string, object?>(), "\"1\"");
        Ok("put: empty object -> required on every editable key", new[] { "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required" }.All(k => HasField(r, k, "required")) && !HasField(r, "secret_ref", "required"));
        r = await Put(A1, PA, new Dictionary<string, object?> { ["provider_kind"] = "database", ["display_name"] = " x ", ["entra_allowed_tenant_ids"] = new[] { T3.ToString() }, ["secret_ref"] = Arn("x"), ["allow_db_fallback"] = true, ["mfa_required"] = true }, "\"1\"");
        Ok("put: database + tenant/secret -> field_not_allowed; fallback -> fallback_not_applicable", HasField(r, "entra_allowed_tenant_ids", "field_not_allowed") && HasField(r, "secret_ref", "field_not_allowed") && HasField(r, "allow_db_fallback", "fallback_not_applicable"));
        r = await Put(A1, PA, new Dictionary<string, object?> { ["provider_kind"] = "entra", ["display_name"] = "x", ["entra_allowed_tenant_ids"] = new[] { T3.ToString(), T2.ToString() }, ["secret_ref"] = Arn("x"), ["allow_db_fallback"] = false, ["mfa_required"] = true }, "\"1\"");
        Ok("put: entra with 2 tenants -> tenant_count", HasField(r, "entra_allowed_tenant_ids", "tenant_count"));
        r = await Put(A1, PA, EntraCfg(Guid.Empty, Arn("x")), "\"1\"");
        Ok("put: nil tenant -> tenant_id_format", HasField(r, "entra_allowed_tenant_ids/0", "tenant_id_format"));
        r = await Put(A1, PA, With(EntraCfg(T3, null), "entra_allowed_tenant_ids", new[] { "{" + T3 + "}" }), "\"1\"");
        Ok("put: braced tenant -> tenant_id_format; switch to entra without secret -> secret_ref_required", HasField(r, "entra_allowed_tenant_ids/0", "tenant_id_format") && HasField(r, "secret_ref", "secret_ref_required"));
        r = await Put(A1, PA, With(EntraCfg(T3, null), "secret_ref", 42), "\"1\"");
        Ok("put: secret_ref number -> type", HasField(r, "secret_ref", "type"));
        r = await Put(A1, PA, With(EntraCfg(T3, Arn("x")), "reason", new string('r', 501)), "\"1\"");
        Ok("put: reason 501 chars -> reason_length (also on a non-downgrade)", HasField(r, "reason", "reason_length"));
        r = await Put(A1, PA, With(EntraCfg(T3, Arn("x")), "reason", " " + new string('r', 500) + new string(' ', 1499)), "\"1\"");
        Ok("put: reason 500 after trim (2000 raw) accepted by validation", r.Status == 200, r.Body);
        // That PUT applied (db -> entra T3). Put it back to database for the next steps.
        r = await Put(A1, PA, With(DbCfg(), "secret_ref", null), "\"2\"");
        Ok("put: entra -> database with secret_ref null -> 200, oidc cleared, secret not set", r.Status == 200 && r.ETag == "\"3\"" && (bool)Obj(r.Body)["secret"]!["set"]! == false
            && await OScalar<long>("SELECT count(*) FROM auth.location_login_config WHERE location_id=$1 AND oidc_authority IS NULL AND oidc_client_id IS NULL AND secret_ref IS NULL", A1) == 1, r.Body);
        await NewEvents();

        // K4 allowlist: out-of-allowlist account / region -> 422 secret_ref_format, never echoed.
        foreach (var (n, arn) in new[] { ("account", Arn("x", acct: "111122223333")), ("region", Arn("x", region: "eu-west-1")), ("partition", "arn:aws-cn:secretsmanager:us-west-2:000000000000:secret:FAKE-x"), ("service", "arn:aws:ssm:us-west-2:000000000000:secret:FAKE-x") })
        {
            r = await Put(A1, PA, EntraCfg(T3, arn), "\"3\"");
            Ok($"put K4: secret_ref {n} outside allowlist -> 422 secret_ref_format, value/account/region not echoed",
                HasField(r, "secret_ref", "secret_ref_format") && !r.Body.Contains("111122223333") && !r.Body.Contains("eu-west-1") && !r.Body.Contains("arn:") && !r.Body.Contains("FAKE"), r.Body);
        }
        Ok("put K4: allowlist refusals change nothing", Rv(A1) == 3 && (await NewEvents()).Count == 0);

        // Spec example: PA database -> entra with a secret (applies immediately, oidc_* server-filled).
        r = await Put(A1, PA, EntraCfg(T3, Arn("llc/riverbend")), "\"3\"");
        Ok("put: PA db -> entra -> 200, ETag \"4\", restricted view", r.Status == 200 && r.ETag == "\"4\"" && !Obj(r.Body).ContainsKey("oidc_authority") && (bool)Obj(r.Body)["secret"]!["set"]!, r.Body);
        ForSchema("login_config_view_restricted", r);
        var ev = await NewEvents();
        Ok("put: update event changed_fields provider_kind, tenants, oidc_*, secret_ref; before/after; no secret value",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["action"]! == "update" && (long)ev[0]["details"]!["row_version"]! == 4
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref"])
            && (string)ev[0]["details"]!["before"]!["provider_kind"]! == "database" && !ev[0].ToJsonString().Contains("arn:"), ev.Count > 0 ? ev[0].ToJsonString() : "none");
        Ok("put: oidc_* filled from config in the row", await OScalar<string>("SELECT oidc_client_id FROM auth.location_login_config WHERE location_id=$1", A1) == ClientId);

        // Downgrades.
        r = await Put(A1, PA, EntraCfg(T3, null, mfa: false), "\"4\"");
        Ok("put: PA mfa off -> 403 not_super_admin", Err(r, 403, "not_super_admin"));
        Ok("put: not_super_admin event (attempted mfa_required, actor_partner_id)", Denied(ev = await NewEvents(), "not_super_admin", A1, "partner", "update", "mfa_required")
            && (string?)ev[0]["details"]!["actor_partner_id"] == P_A.ToString());
        r = await Put(A1, PA, EntraCfg(T3, null, fallback: true), "\"4\"");
        Ok("put: PA fallback on -> 403 not_super_admin + event", Err(r, 403, "not_super_admin") && Denied(await NewEvents(), "not_super_admin", A1, "partner", "update", "allow_db_fallback"));
        Ok("put: refused downgrades change nothing", Rv(A1) == 4);
        r = await Put(A1, SA, EntraCfg(T3, null, mfa: false), "\"4\"");
        Ok("put: SA mfa off, no reason -> 400 reason_required, no event", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0);
        r = await Put(A1, SA, With(EntraCfg(T3, null, mfa: false), "reason", "            "), "\"4\"");
        Ok("put: SA mfa off, whitespace reason -> 400 reason_required", Err(r, 400, "reason_required"));
        r = await Put(A1, SA, With(EntraCfg(T3, null, mfa: false), "reason", "Client's Entra conditional access already enforces MFA; ticket OPS-1234"), "\"4\"");
        Ok("put: SA mfa off + reason -> 200 super view", r.Status == 200 && (bool)Obj(r.Body)["mfa_required"]! == false && Obj(r.Body).ContainsKey("oidc_authority"));
        ForSchema("login_config_view_super", r);
        ev = await NewEvents();
        Ok("put: SA downgrade event admin_type super + reason", ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super" && (string?)ev[0]["details"]!["reason"] is { Length: > 10 });
        r = await Put(A1, PA, EntraCfg(T3, null, mfa: false, display: "Riverbend Casino East"), "\"5\"");
        Ok("put: PA edits display_name while mfa already off (unchanged field is not a downgrade) -> 200", r.Status == 200, r.Body);
        r = await Put(A1, PA, EntraCfg(T3, null, mfa: true, display: "Riverbend Casino East"), "\"6\"");
        Ok("put: PA upgrade mfa back on -> 200", r.Status == 200 && (bool)Obj(r.Body)["mfa_required"]!);
        r = await Put(A2, PA, EntraCfg(T2, null, fallback: false), "\"1\"");
        Ok("put: PA upgrade fallback off -> 200", r.Status == 200 && (bool)Obj(r.Body)["allow_db_fallback"]! == false, r.Body);
        await NewEvents();

        // Rotation and no-op.
        var rot0 = await OScalar<DateTime>("SELECT max(at) FROM auth.location_login_config_change WHERE location_id=$1 AND 'secret_ref' = ANY(changed_fields)", A1);
        await Task.Delay(20);
        r = await Put(A1, PA, EntraCfg(T3, Arn("llc/riverbend-v2"), display: "Riverbend Casino East"), "\"7\"");
        ev = await NewEvents();
        Ok("put: rotation -> changed_fields [secret_ref] only, rotated_at advances", r.Status == 200 && ev.Count == 1
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["secret_ref"])
            && DateTime.Parse((string)Obj(r.Body)["secret"]!["rotated_at"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal) > rot0, r.Body);
        r = await Put(A1, PA, EntraCfg(T3, null, display: "  Riverbend Casino East  "), "\"8\"");
        Ok("put: no-op (trimmed same values) -> 200, row_version unchanged, no event (OQ-P3-6)", r.Status == 200 && r.ETag == "\"8\"" && Rv(A1) == 8 && (await NewEvents()).Count == 0);
        r = await Put(A1, PA, DbCfg(), "\"8\"");
        Ok("put: entra (secret set) -> database without secret_ref null -> field_not_allowed", HasField(r, "secret_ref", "field_not_allowed"));

        // Tenant rule (Super Admin too).
        r = await Put(A1, SA, EntraCfg(T1, null), "\"8\"");
        Ok("put: SA tenant bound to partner B -> 422 tenant_bound_elsewhere", HasField(r, "entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        Ok("put: tenant_bound_elsewhere event (super)", Denied(await NewEvents(), "tenant_bound_elsewhere", A1, "super", "update", "entra_allowed_tenant_ids"));
        r = await Put(A2, SA, EntraCfg(T3, null), "\"2\"");
        Ok("put: tenant on another location of the SAME partner is allowed", r.Status == 200, r.Body);
        await NewEvents();

        // Scope.
        var miss = await Put(missing, PA, DbCfg(), "\"1\"");
        Ok("put: missing location -> 404, no event", Err(miss, 404, "not_found") && (await NewEvents()).Count == 0);
        var miss2 = await Put(missing, SA, DbCfg(), "\"1\"");
        Ok("put: SA missing location -> 404, no event (TLD 9:04 #2)", Err(miss2, 404, "not_found") && (await NewEvents()).Count == 0);
        miss2 = await Put(missing, RD, DbCfg(), "\"1\"");
        Ok("put: reader on a missing location -> 403, no event (TLD 9:04 #2)", Err(miss2, 403, "forbidden") && (await NewEvents()).Count == 0);
        r = await Put(B1, PA, DbCfg(), "\"1\"");
        Ok("put: PA other partner -> 404 identical body + forbidden_scope event", Err(r, 404, "not_found") && r.Body == miss.Body
            && Denied(await NewEvents(), "forbidden_scope", B1, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        r = await Put(B1, PA, DbCfg(), null);
        Ok("put: PA other partner without If-Match -> still 404 (step 3-4 before 5)", Err(r, 404, "not_found") && (await NewEvents()).Count == 1);
        r = await Put(A1, RD, With(DbCfg(), "secret_ref", null), "\"8\"");
        ev = await NewEvents();
        Ok("put: org:V reader -> 403 forbidden + not_admin event without admin_type/actor_partner_id", Err(r, 403, "forbidden")
            && Denied(ev, "not_admin", A1, null, "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required")
            && !ev[0]["details"]!.AsObject().ContainsKey("actor_partner_id"));
        r = await Put(A1, CU, DbCfg(), "\"8\"");
        Ok("put: custom role with the permission -> 403 forbidden + not_admin event", Err(r, 403, "forbidden") && Denied(await NewEvents(), "not_admin", A1, null, "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        r = await Put(missing, NO, DbCfg(), "\"8\"");
        Ok("put: no role on a missing location -> 403 forbidden, no event", Err(r, 403, "forbidden") && (await NewEvents()).Count == 0);
        Ok("put: B1 untouched by any partner A caller", Rv(B1) == 1);
    }

    // ================= 5. history =================
    {
        var r = await Get(Cfg(A1) + "/changes", SA);
        var items = r.Status == 200 ? Obj(r.Body)["items"]!.AsArray() : [];
        Ok("history: SA -> 200, newest first, created last, reasons included", r.Status == 200 && items.Count == 8 && (string)items[^1]!["change_kind"]! == "created"
            && (long)items[0]!["row_version"]! == 8 && items.Any(i => (string?)i!["reason"] is { Length: > 0 }), r.Body);
        ForSchema("change_history", r);
        Ok("history: no secret value in items", !r.Body.Contains("arn:") && !r.Body.Contains("FAKE"));
        r = await Get(Cfg(A1) + "/changes", PA);
        Ok("history: PA own partner -> 200", r.Status == 200);
        var miss = await Get(Cfg(missing) + "/changes", PA);
        r = await Get(Cfg(B1) + "/changes", PA);
        Ok("history: PA other partner -> 404 identical to missing", Err(r, 404, "not_found") && r.Body == miss.Body);
        Ok("history: SA missing -> 404", Err(await Get(Cfg(missing) + "/changes", SA), 404, "not_found"));
        Ok("history: org:V reader -> 403 forbidden", Err(await Get(Cfg(A1) + "/changes", RD), 403, "forbidden"));
        Ok("history: no role -> 403 forbidden", Err(await Get(Cfg(A1) + "/changes", NO), 403, "forbidden"));
        Ok("history: reads write no event", (await NewEvents()).Count == 0);
    }

    // ================= 6. K4 crafted cross-partner request =================
    {
        var injected = "x'); SELECT set_config('app.admin_type','super',false); --";
        void Craft(HttpRequestMessage q)
        {
            q.Headers.TryAddWithoutValidation("X-Partner-Id", P_B.ToString());
            q.Headers.TryAddWithoutValidation("X-Admin-Type", "super");
            q.Headers.TryAddWithoutValidation("X-Forwarded-User", U_SA.ToString());
        }
        var qs = $"?app.admin_type=super&app.partner_id={P_B}&partner_id={P_B}&admin_type=super";
        var bodyB = J(new Dictionary<string, object?>
        {
            ["provider_kind"] = "database", ["display_name"] = injected, ["entra_allowed_tenant_ids"] = Array.Empty<string>(), ["secret_ref"] = null,
            ["allow_db_fallback"] = false, ["mfa_required"] = true,
        });
        var r = await Send(HttpMethod.Put, Cfg(B1) + qs, PA, bodyB, "\"1\"", Craft);
        Ok("K4 crafted: PA PUT on partner B with X-Partner-Id/X-Admin-Type headers, app.* query, SQL-ish display_name -> 404",
            Err(r, 404, "not_found") && r.Body == """{"error":"not_found"}""");
        Ok("K4 crafted: recorded as forbidden_scope against partner B (attribution from the server, not the request)",
            Denied(await NewEvents(), "forbidden_scope", B1, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required"));
        Ok("K4 crafted: partner B row unchanged", Rv(B1) == 1 && await OScalar<string>("SELECT display_name FROM auth.location_login_config WHERE location_id=$1", B1) == "Riverbend Casino");
        r = await Send(HttpMethod.Get, Cfg(B1) + qs, PA, edit: Craft);
        Ok("K4 crafted: PA GET partner B with crafted headers/query -> 404", Err(r, 404, "not_found"));
        r = await Send(HttpMethod.Get, Cfg(B1) + "/changes" + qs, PA, edit: Craft);
        Ok("K4 crafted: PA history of partner B with crafted headers/query -> 404", Err(r, 404, "not_found"));
        var withPartner = J(Merge(DbCfg(), new Dictionary<string, object?> { ["partner_id"] = P_B.ToString(), ["admin_type"] = "super" }));
        r = await Send(HttpMethod.Put, Cfg(A2) + qs, PA, withPartner, $"\"{Rv(A2)}\"", Craft);
        Ok("K4 crafted: partner_id / admin_type in an own-location PUT body -> 422 unknown_field", HasField(r, "partner_id", "unknown_field") && HasField(r, "admin_type", "unknown_field"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations" + qs, PA, J(Create(P_B, injected, DbCfg())), edit: Craft);
        Ok("K4 crafted: PA create under partner B with crafted headers -> 404 + forbidden_scope", Err(r, 404, "not_found") && Denied(await NewEvents(), "forbidden_scope", P_B, "partner", "create",
            "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        // Parameterised SQL: the SQL-ish text is stored literally on an own location and changes nothing else.
        var a2v = Rv(A2);
        var a2 = Obj((await Get(Cfg(A2), PA)).Body);
        r = await Send(HttpMethod.Put, Cfg(A2), PA, J(new Dictionary<string, object?>
        {
            ["provider_kind"] = (string)a2["provider_kind"]!, ["display_name"] = injected, ["entra_allowed_tenant_ids"] = a2["entra_allowed_tenant_ids"]!.AsArray().Select(x => (string)x!).ToArray(),
            ["allow_db_fallback"] = (bool)a2["allow_db_fallback"]!, ["mfa_required"] = (bool)a2["mfa_required"]!,
        }), $"\"{a2v}\"");
        Ok("K4 crafted: SQL-ish display_name stored literally (parameterised)", r.Status == 200
            && await OScalar<string>("SELECT display_name FROM auth.location_login_config WHERE location_id=$1", A2) == injected && await Count("location") == 3, r.Body);
        await NewEvents();
        Ok("K4 crafted: PA still cannot see B after the injected text", Err(await Get(Cfg(B1), PA), 404, "not_found"));
        // GUCs never left on pooled connections: open every idle connection of the API's own pool and read app.*.
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        var conns = new List<NpgsqlConnection>();
        var leaked = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var c = await ds.OpenConnectionAsync();
            conns.Add(c);
            await using var cmd = new NpgsqlCommand("SELECT coalesce(current_setting('app.admin_type', true), '') || '|' || coalesce(current_setting('app.partner_id', true), '')", c);
            var v = (string)(await cmd.ExecuteScalarAsync())!;
            if (v != "|") leaked.Add(v);
        }
        foreach (var c in conns) await c.DisposeAsync();
        Ok("K4: no app.* GUC left on any pooled API connection after the run", leaked.Count == 0, string.Join(",", leaked));
        // RLS backstop for the API role: without the GUC, or with partner A, partner B rows are invisible.
        await using (var c = await ds.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using var q0 = new NpgsqlCommand("SELECT count(*) FROM auth.location_login_config", c, tx);
            var none = (long)(await q0.ExecuteScalarAsync())!;
            await using var s = new NpgsqlCommand("SELECT set_config('app.admin_type','partner',true), set_config('app.partner_id',$1,true)", c, tx);
            s.Parameters.Add(new() { Value = P_A.ToString() });
            await s.ExecuteNonQueryAsync();
            await using var q1 = new NpgsqlCommand("SELECT count(*) FROM auth.location_login_config WHERE location_id = $1", c, tx);
            q1.Parameters.Add(new() { Value = B1 });
            var b = (long)(await q1.ExecuteScalarAsync())!;
            await tx.RollbackAsync();
            Ok("K4: RLS backstop (no GUC -> 0 rows; partner A GUC -> partner B row invisible)", none == 0 && b == 0, $"{none} {b}");
        }
    }

    // ================= 7. fail closed: outbox write fails -> 500, nothing saved =================
    {
        var rejectAll = Path.Combine(Path.GetTempPath(), $"p3-reject-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(rejectAll, """{"type":"string"}""");
        var port2 = FreePort(port);
        Console.WriteLine($"INFO fail-closed port {port2}");
        await using var app2 = await AdminApiHost.BuildAsync(Opts(schema: rejectAll) with { Urls = $"http://127.0.0.1:{port2}" }, dir);
        await app2.StartAsync();
        using var h2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port2}") };
        var corr500 = new List<string?>();
        async Task<(int, string)> S2(HttpMethod m, string path, string tok, string body, string? im = null)
        {
            using var q = new HttpRequestMessage(m, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            q.Headers.TryAddWithoutValidation("Authorization", "Bearer " + tok);
            if (im is not null) q.Headers.TryAddWithoutValidation("If-Match", im);
            using var res = await h2.SendAsync(q);
            var t = await res.Content.ReadAsStringAsync();
            allBodies.Add(t);
            if ((int)res.StatusCode == 500) corr500.Add(res.Headers.TryGetValues(LoginConfigApi.CorrelationHeader, out var cv) ? string.Join(",", cv) : null);
            return ((int)res.StatusCode, t);
        }
        var before = (await Count("location"), await Count("location_login_config_change"), Rv(A1));
        var (st, bd) = await S2(HttpMethod.Post, "/api/orgs/locations", SA, J(Create(P_A, "Doomed", DbCfg())));
        Ok("fail closed: create with outbox failure -> 500, no body", st == 500 && bd.Length == 0);
        (st, bd) = await S2(HttpMethod.Put, Cfg(A1), PA, J(EntraCfg(T3, null, display: "Doomed")), $"\"{Rv(A1)}\"");
        Ok("fail closed: update with outbox failure -> 500, no body", st == 500 && bd.Length == 0);
        (st, _) = await S2(HttpMethod.Put, Cfg(B1), PA, J(DbCfg()), "\"1\"");
        Ok("fail closed: denied-event write failure -> 500 (OQ)", st == 500);
        (st, bd) = await S2(HttpMethod.Post, "/api/orgs/locations", NO, J(Create(P_A, "Doomed", DbCfg())));
        Ok("fail closed: refused-create event write failure -> 500, no body (never dropped silently, TLD 9:04 #4)", st == 500 && bd.Length == 0);
        Ok("fail closed #3: every 500 has no body and an X-Correlation-Id (UUIDv7), all distinct", corr500.Count == 4 && corr500.All(IsCorr) && corr500.Distinct().Count() == 4,
            string.Join(",", corr500));
        Ok("fail closed: nothing saved (locations, change rows, row_version, outbox)", (await Count("location"), await Count("location_login_config_change"), Rv(A1)) == before && (await NewEvents()).Count == 0);
        await app2.StopAsync();
        File.Delete(rejectAll);
    }

    // ================= 7b. correlation id (TLD 9:04 #3) =================
    {
        var r = await Send(HttpMethod.Get, Cfg(A1), SA, edit: q => q.Headers.TryAddWithoutValidation(LoginConfigApi.CorrelationHeader, "client-chosen"));
        Ok("correlation: server-generated, a client-sent X-Correlation-Id is not echoed", r.Status == 200 && IsCorr(r.Corr) && r.Corr != "client-chosen", r.Corr);
        Ok("correlation: every API response so far carried a distinct UUIDv7 X-Correlation-Id (200/201/4xx/401)", allCorr.Count > 100 && allCorr.All(IsCorr) && allCorr.Distinct().Count() == allCorr.Count, $"{allCorr.Count}");
    }

    // ================= 7c. schema: location_id null only for a refused create (TLD 9:04 #2) =================
    {
        var createDen = allEvents.First(e => (string)e["event_type"]! == "auth.config.change_denied" && (string)e["details"]!["operation"]! == "create" && e["admin_type"] is null && e["target"]!["id"] is not null);
        var updateDen = allEvents.First(e => (string)e["event_type"]! == "auth.config.change_denied" && (string)e["details"]!["operation"]! == "update" && e["admin_type"] is not null);
        var fsCreate = allEvents.First(e => (string?)e["details"]!["reason"] == "forbidden_scope" && (string)e["details"]!["operation"]! == "create");
        var changed = allEvents.First(e => (string)e["event_type"]! == "auth.provider.changed");
        JsonObject Mut(JsonObject e, Action<JsonObject> f) { var c = e.DeepClone().AsObject(); f(c); return c; }
        var cases = new (string n, JsonObject doc, bool valid)[]
        {
            ("not_admin create, location_id null, scope [] (the one exemption)", createDen, true),
            ("forbidden_scope create, location_id null", fsCreate, true),
            ("not_admin create, partner unknown (no target.id, no scope.partner_id)", Mut(createDen, e => { e["target"]!.AsObject().Remove("id"); e["scope"]!.AsObject().Remove("partner_id"); }), true),
            ("not_admin create, target.id without scope.partner_id", Mut(createDen, e => e["scope"]!.AsObject().Remove("partner_id")), false),
            ("not_admin create, scope.partner_id without target.id", Mut(createDen, e => e["target"]!.AsObject().Remove("id")), false),
            ("not_admin create without details.location_id", Mut(createDen, e => e["details"]!.AsObject().Remove("location_id")), false),
            ("not_admin create with a location_id", Mut(createDen, e => e["details"]!["location_id"] = A1.ToString("D")), false),
            ("not_admin create with scope.location_ids [A1]", Mut(createDen, e => e["scope"]!["location_ids"] = new JsonArray(A1.ToString("D"))), false),
            ("not_admin create relabelled update (location_id null)", Mut(createDen, e => { e["details"]!["operation"] = "update"; e["action"] = "update"; }), false),
            ("not_admin create relabelled auth.provider.changed", Mut(createDen, e => e["event_type"] = "auth.provider.changed"), false),
            ("not_admin create relabelled v1 config setting", Mut(createDen, e => e["details"]!["setting"] = "LoginService:Session:AbsoluteLifetime"), false),
            ("forbidden_scope create without target.id/scope.partner_id (admins always name the partner)", Mut(fsCreate, e => { e["target"]!.AsObject().Remove("id"); e["scope"]!.AsObject().Remove("partner_id"); }), false),
            ("forbidden_scope create with a location_id", Mut(fsCreate, e => e["details"]!["location_id"] = A1.ToString("D")), false),
            ("change_denied update with location_id null", Mut(updateDen, e => e["details"]!["location_id"] = null), false),
            ("change_denied update without location_id", Mut(updateDen, e => e["details"]!.AsObject().Remove("location_id")), false),
            ("change_denied update without admin_type, scope [], location_id null", Mut(updateDen, e => { e.Remove("admin_type"); e["details"]!["reason"] = "not_admin"; e["details"]!.AsObject().Remove("actor_partner_id"); e["details"]!["location_id"] = null; e["scope"]!["location_ids"] = new JsonArray(); }), false),
            ("auth.provider.changed with location_id null", Mut(changed, e => e["details"]!["location_id"] = null), false),
            ("auth.provider.changed no admin_type + scope [] + location_id null", Mut(changed, e => { e.Remove("admin_type"); e["details"]!["location_id"] = null; e["scope"]!["location_ids"] = new JsonArray(); }), false),
        };
        foreach (var (n, doc, valid) in cases)
        {
            Ok($"schema 9:04: {n} -> {(valid ? "valid" : "refused")}", Valid(doc) == valid, string.Join("; ", lite.Validate(doc)));
            schemaCases.Add((n, doc));
        }
    }

    // ================= 8. secrets never leave =================
    {
        Ok("secret: no response body contains an ARN or the FAKE secret names", allBodies.All(b => !b.Contains("arn:aws") && !b.Contains("FAKE-test-only") && !b.Contains("secretsmanager")));
        Ok("secret: no outbox envelope contains an ARN", await OScalar<long>("SELECT count(*) FROM auth.audit_outbox WHERE envelope::text ~ 'arn:|secretsmanager|FAKE-test'") == 0);
        Ok("secret: no change row (before/after/reason/changed_fields) contains an ARN", await OScalar<long>(
            "SELECT count(*) FROM auth.location_login_config_change WHERE coalesce(before::text,'') || after::text || coalesce(reason,'') || array_to_string(changed_fields,',') ~ 'arn:|secretsmanager|FAKE-test'") == 0);
        Ok("secret: every outbox envelope valid against audit-events.schema.json (JsonSchemaLite)", allEvents.Count > 0 && allEvents.All(Valid), $"{allEvents.Count}");
        Ok("events: every outbox row read by the checks", await Count("audit_outbox") == allEvents.Count);
    }

    // ================= 9. cross-check corpus (crosscheck.py: Python jsonschema Draft 2020-12) =================
    if (outDir is not null)
    {
        Directory.CreateDirectory(outDir);
        var corpus = new List<string>();
        void Add(JsonNode doc, string label) => corpus.Add(new JsonObject { ["label"] = label, ["lite_valid"] = lite.Validate(doc).Count == 0, ["doc"] = doc.DeepClone() }.ToJsonString());
        int k = 0;
        foreach (var e in allEvents)
        {
            Add(e, $"ev{k}");
            var m1 = e.DeepClone().AsObject(); m1.Remove("event_id"); Add(m1, $"ev{k}-no-event_id");
            var m2 = e.DeepClone().AsObject(); m2["occurred_at"] = "yesterday"; Add(m2, $"ev{k}-bad-date");
            var m3 = e.DeepClone().AsObject(); m3["details"]!["sneaky"] = 1; Add(m3, $"ev{k}-extra-detail");
            var m4 = e.DeepClone().AsObject(); m4["admin_type"] = "location"; Add(m4, $"ev{k}-admin-location");
            var m5 = e.DeepClone().AsObject(); m5.Remove("admin_type"); Add(m5, $"ev{k}-no-admin_type");
            var m6 = e.DeepClone().AsObject(); m6["scope"]!["location_ids"] = new JsonArray(); Add(m6, $"ev{k}-empty-scope");
            var m7 = e.DeepClone().AsObject(); m7["actor"]!["user_id"] = "nope"; Add(m7, $"ev{k}-bad-uuid");
            var m8 = e.DeepClone().AsObject(); m8["details"]!["reason"] = "wrong_partner"; Add(m8, $"ev{k}-reason-enum");
            var m9 = e.DeepClone().AsObject(); m9["details"]!["secret_ref"] = Arn("leak"); Add(m9, $"ev{k}-secret");
            k++;
        }
        foreach (var (n, doc) in schemaCases) Add(doc, "schema904: " + n);
        await File.WriteAllLinesAsync(Path.Combine(outDir, "corpus.jsonl"), corpus);
        await File.WriteAllLinesAsync(Path.Combine(outDir, "responses.jsonl"), responsesForSchema.Select(x => new JsonObject { ["def"] = x.def, ["body"] = JsonNode.Parse(x.body) }.ToJsonString()));
        Console.WriteLine($"INFO corpus {corpus.Count} docs, {responsesForSchema.Count} responses -> {outDir}");
    }
    exitCode = fail == 0 ? 0 : 1;
}
finally
{
    await app.StopAsync();
    await app.DisposeAsync();
    // Cleanup as the owner, children first (the checks started on empty tables).
    await using var c = await ownerDs.OpenConnectionAsync();
    foreach (var t in Tables) { await using var d = new NpgsqlCommand($"DELETE FROM auth.{t}", c); await d.ExecuteNonQueryAsync(); }
    var left = 0L;
    foreach (var t in Tables) left += await Count(t);
    Ok("cleanup: auth.audit_outbox, change, llc, location empty again", left == 0);
}
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? exitCode : 1;

static string Base64Url(string s) => Base64UrlEncoder.Encode(s);

sealed record R(int Status, string Body, string? ETag, string? Location, string? WwwAuth, string? CacheControl, string? Server, string? Corr = null);

/// <summary>Interim token roles for admins; injected non-admin callers (org:V reader, no role, custom role) for §6 rows 4-5.</summary>
sealed class TestDirectory(HashSet<Guid> readerLocations, Guid reader, Guid nobody, Guid custom) : IAdminDirectory
{
    readonly TokenAdminDirectory _interim = new();
    public Task<AdminGrant> ResolveAsync(AdminIdentity id, CancellationToken ct) =>
        id.UserId == reader ? Task.FromResult(AdminGrant.Nothing with { OrgViewLocations = readerLocations })
        : id.UserId == nobody ? Task.FromResult(AdminGrant.Nothing)
        // A custom role carrying org:location_login_method:write is ignored: only built-in roles count [LD L172].
        : id.UserId == custom ? Task.FromResult(AdminGrant.Nothing with { LoginMethodWrite = true, OrgCreateEdit = true })
        : _interim.ResolveAsync(id, ct);
}
