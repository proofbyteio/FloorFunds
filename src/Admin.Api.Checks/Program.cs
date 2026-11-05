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
using Auth.Data.Readers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
var loginCs = Env("LOGIN_CONNECTION_STRING"); // <db>_login, read-only: the login readers (TLD 10:02 Q1 restart check)
if (new[] { ownerCs, adminCs, loginCs }.Any(cs => new NpgsqlConnectionStringBuilder(cs).Database != dbName))
{ Console.Error.WriteLine("refuse: every connection string's Database must equal AUTH_CHECKS_DB"); return 2; }
// S2a P4 (F-A3): `--ui-host <ui/orgs dir>` runs the TEST-ONLY screen-9 host for ui/orgs/tests/e2e.mjs instead of the checks (UiHost.cs).
var uiDir = args.Length > 1 && args[0] == "--ui-host" ? args[1] : null;
var outDir = uiDir is null && args.Length > 0 ? args[0] : null;

await using var ownerDs = NpgsqlDataSource.Create(ownerCs);
async Task<T> OScalar<T>(string sql, params object?[] ps)
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    return (T)(await cmd.ExecuteScalarAsync())!;
}
async Task OExec(string sql, params object?[] ps) // owner-side fixture edit (test rows only)
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, c);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("fixture edit hit != 1 row");
}
async Task<long> Count(string table) => await OScalar<long>($"SELECT count(*) FROM auth.{table}");
string[] Tables = ["audit_outbox", "location_login_config_change", "location_login_config", "location", "partner_reason_bounds"]; // Slice A: + partner_reason_bounds (no FK)
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
// The deployment's one app-registration secret reference, Entra:SecretRef [TLD 2026-11-04 9:49, D-51]. Fake account 000000000000.
string DeployArn = Arn("deploy/app-reg");

// ---------- keys, options ----------
using var rsa = RSA.Create(2048);
var signKey = new RsaSecurityKey(rsa) { KeyId = "p3-test" };
var publicKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "p3-test" };
using var otherRsa = RSA.Create(2048);
var otherKey = new RsaSecurityKey(otherRsa) { KeyId = "p3-test" };
var schemaPath = Path.Combine(AppContext.BaseDirectory, "contract", "audit-events.schema.json");
AdminApiOptions Opts(string? cs = null, string? schema = null) => new()
{
    ConnectionString = cs ?? adminCs, EntraAuthority = Authority, EntraClientId = ClientId.ToUpperInvariant(), EntraSecretRef = DeployArn,
    SecretAccountIds = new HashSet<string> { "000000000000" }, SecretRegions = new HashSet<string> { "us-west-2" },
    TokenIssuer = Issuer, SigningKeys = [publicKey], AuditSchemaPath = schema ?? schemaPath,
};

// DEC-107: the default reason bounds, from the one named constant each (never retyped in the tests below).
const int Mn = AdminApiOptions.DefaultReasonMinLength, Mx = AdminApiOptions.DefaultReasonMaxLength;

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
// [TLD 2026-11-04 10:58 F-A1, clarified 10:59] EntraDeployment: internal constructor, EntraDeployment.Create the only (validated) way in,
// no InternalsVisibleTo anywhere. Values: SSE2-FAKE-TEST-ONLY (account 000000000000, FAKE ARNs).
{
    var t = typeof(Auth.Data.EntraDeployment);
    var bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
    Ok("F-A1: EntraDeployment has NO public constructor (reflection); every constructor is internal",
        t.GetConstructors().Length == 0 && t.GetConstructors(bf).Length > 0 && t.GetConstructors(bf).All(c => c.IsAssembly && !c.IsPublic),
        string.Join(", ", t.GetConstructors(bf).Select(c => $"public={c.IsPublic} internal={c.IsAssembly}")));
    Ok("F-A1: no InternalsVisibleTo on Auth.Data or Admin.Api (TLD 10:59: none anywhere, incl. tests)",
        !t.Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false).Any()
        && !typeof(AdminApiOptions).Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false).Any());
    IReadOnlySet<string> acc = new HashSet<string> { "000000000000" }, reg = new HashSet<string> { "us-west-2" };
    var bad = Auth.Data.EntraDeployment.Create(Authority, ClientId, "not-an-arn", acc, reg);
    Ok("F-A1 bypass: Create(..., \"not-an-arn\") refused with reason entra_secret_ref_not_allowed; the reason and ToString never contain the value",
        !bad.Ok && bad.Value is null && bad.Reason == Auth.Data.EntraDeployment.Reasons.SecretRefNotAllowed
        && !bad.Reason.Contains("not-an-arn") && !bad.ToString().Contains("not-an-arn"), $"{bad.Reason}");
    var good = Auth.Data.EntraDeployment.Create(Authority, ClientId.ToUpperInvariant(), DeployArn, acc, reg);
    Ok("F-A1: Create with allowlisted fakes -> value (client id lower-case canonical, SecretRef = the ARN, ToString redacted)",
        good.Ok && good.Reason is null && good.Value!.ClientId == ClientId && good.Value.Authority == Authority && good.Value.SecretRef.Reveal() == DeployArn
        && !good.Value.ToString().Contains("arn:") && !good.ToString().Contains("arn:"));
    foreach (var (n, r, want) in new (string, Auth.Data.EntraDeployment.Result, string)[] {
        ("authority http", Auth.Data.EntraDeployment.Create("http://login.microsoftonline.com", ClientId, DeployArn, acc, reg), Auth.Data.EntraDeployment.Reasons.AuthorityInvalid),
        ("authority null", Auth.Data.EntraDeployment.Create(null, ClientId, DeployArn, acc, reg), Auth.Data.EntraDeployment.Reasons.AuthorityInvalid),
        ("client id not a GUID", Auth.Data.EntraDeployment.Create(Authority, "not-a-guid", DeployArn, acc, reg), Auth.Data.EntraDeployment.Reasons.ClientIdInvalid),
        ("account list bad", Auth.Data.EntraDeployment.Create(Authority, ClientId, DeployArn, new HashSet<string> { "12345" }, reg), Auth.Data.EntraDeployment.Reasons.AllowedAccountIdsInvalid),
        ("region list empty", Auth.Data.EntraDeployment.Create(Authority, ClientId, DeployArn, acc, new HashSet<string>()), Auth.Data.EntraDeployment.Reasons.AllowedRegionsInvalid),
        ("ARN account outside allowlist", Auth.Data.EntraDeployment.Create(Authority, ClientId, Arn("x", acct: "111122223333"), acc, reg), Auth.Data.EntraDeployment.Reasons.SecretRefNotAllowed),
        ("ARN null", Auth.Data.EntraDeployment.Create(Authority, ClientId, null, acc, reg), Auth.Data.EntraDeployment.Reasons.SecretRefNotAllowed) })
        Ok($"F-A1: Create refuses {n} -> {want}, no value echoed", !r.Ok && r.Reason == want && !r.Reason!.Contains("arn:") && !r.Reason.Contains("111122223333") && !r.Reason.Contains("not-a-guid"), $"{r.Reason}");
    // [SSE2 review 06dc8ac..ba85c77 (3fac1d52) F-C1] Auth.Data's EntraDeployment public surface = the type + Create + Reasons (5
    // consts) + Result (Value / Reason / Ok / ToString) + the instance getters and redacted ToString. Anything else public fails.
    // [SSE2 review 04dba71d Info] the read-only getters Authority / ClientId / SecretRef and the redacted ToString are allowed
    // (spec §13 "Allowed public getters"); this check allows exactly those: a setter, a public constructor or any other member fails.
    {
        const System.Reflection.BindingFlags pub = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        string Surf(Type x) => string.Join(",", x.GetMembers(pub).Select(m => m.MemberType + ":" + m.Name).OrderBy(s => s, StringComparer.Ordinal));
        var res = typeof(Auth.Data.EntraDeployment.Result); var rsn = typeof(Auth.Data.EntraDeployment.Reasons);
        var got = $"T[{Surf(t)}] Reasons[{Surf(rsn)}] Result[{Surf(res)}]";
        var want = "T[Method:Create,Method:ToString,Method:get_Authority,Method:get_ClientId,Method:get_SecretRef,NestedType:Reasons,NestedType:Result,Property:Authority,Property:ClientId,Property:SecretRef]"
                 + " Reasons[Field:AllowedAccountIdsInvalid,Field:AllowedRegionsInvalid,Field:AuthorityInvalid,Field:ClientIdInvalid,Field:SecretRefNotAllowed]"
                 + " Result[Method:ToString,Method:get_Ok,Method:get_Reason,Method:get_Value,Property:Ok,Property:Reason,Property:Value]";
        Ok("F-C1: EntraDeployment public surface is exactly {type, Create, Reasons+consts, Result+members, read-only getters Authority/ClientId/SecretRef + redacted ToString (allowed, SSE2 04dba71d)} (reflection; IsValidAuthority / SecretRefAllowed internal)",
            got == want && t.IsPublic && rsn.IsNestedPublic && res.IsNestedPublic, got);
        string[] auths = { Authority, "http://login.microsoftonline.com", "https://login.microsoftonline.com/", "https://login.microsoftonline.com/tenant",
                           "https://login.microsoftonline.com?x=1", "https://u:p@login.microsoftonline.com", "https://login.microsoftonline.com#f", "",
                           "login.microsoftonline.com", "https://login.microsoftonline.com:99999", "https://login..microsoftonline.com", "https://h:8443" };
        Ok("F-C1: AdminApiOptions.IsValidAuthority via Create: true only for the valid authorities, null still throws ArgumentNullException",
            auths.All(a => AdminApiOptions.IsValidAuthority(a) == (a == Authority || a == "https://h:8443"))
            && Throws<ArgumentNullException>(() => AdminApiOptions.IsValidAuthority(null!)), string.Join(" ", auths.Select(AdminApiOptions.IsValidAuthority)));
        string[] refs = { DeployArn, "", "my-client-secret", Arn("x", acct: "111122223333"), Arn("x", region: "eu-west-1"), "arn:aws-cn:secretsmanager:us-west-2:000000000000:secret:FAKE-x",
                          "arn:aws:ssm:us-west-2:000000000000:secret:FAKE-x", DeployArn + "\n", "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-" + new string('a', 2048) };
        Ok("F-C1: AdminApiOptions.SecretRefAllowed via Create: true only for the allowlisted ARN; null still throws NullReferenceException",
            refs.All(r => Opts().SecretRefAllowed(r) == (r == DeployArn)) && Throws<NullReferenceException>(() => Opts().SecretRefAllowed(null!)),
            string.Join(" ", refs.Select(r => Opts().SecretRefAllowed(r))));
        static bool Throws<TE>(Action a) where TE : Exception { try { a(); return false; } catch (TE) { return true; } catch { return false; } }
    }
    try { _ = (Opts() with { EntraSecretRef = "not-an-arn" }).Entra(); Ok("F-A1: AdminApiOptions.Entra() goes through Create: not-an-arn refused (key named, value not echoed)", false, "accepted"); }
    catch (InvalidOperationException e) { Ok("F-A1: AdminApiOptions.Entra() goes through Create: not-an-arn refused (key named, value not echoed)", e.Message.Contains("config Entra:SecretRef") && !e.Message.Contains("not-an-arn"), e.Message); }
}
// TLD 2026-11-04 9:49 (D-51): Entra:SecretRef validated against the allowlists at startup; the refusal names the key, never the value.
foreach (var (n, bad) in new[] { ("empty", ""), ("not an ARN", "my-client-secret"), ("account outside allowlist", Arn("x", acct: "111122223333")),
                                  ("region outside allowlist", Arn("x", region: "eu-west-1")), ("partition aws-cn", "arn:aws-cn:secretsmanager:us-west-2:000000000000:secret:FAKE-x"),
                                  ("service ssm", "arn:aws:ssm:us-west-2:000000000000:secret:FAKE-x"), ("trailing newline", DeployArn + "\n"),
                                  ("over 2048 chars", "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-" + new string('a', 2048)) })
{
    try { (Opts() with { EntraSecretRef = bad }).Validated(); Ok($"startup: Entra:SecretRef {n} refused", false, "accepted"); }
    catch (InvalidOperationException e)
    {
        Ok($"startup: Entra:SecretRef {n} refused, key named, value/account/region not echoed", e.Message.Contains("config Entra:SecretRef", StringComparison.Ordinal)
            && !e.Message.Contains("arn:") && !e.Message.Contains("FAKE") && !e.Message.Contains("111122223333") && !e.Message.Contains("eu-west-1") && !e.Message.Contains("client-secret"), e.Message);
    }
}
Ok("startup: Entra:SecretRef in the allowlist accepted (unchanged)", Opts().Validated().EntraSecretRef == DeployArn);
Ok("startup: AdminApiOptions.ToString prints neither Entra:SecretRef nor the connection string", !Opts().ToString().Contains("arn:") && !Opts().ToString().Contains(adminCs));
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
async Task<(int code, string err)> RunAdminApi(Action<IDictionary<string, string?>> edit)
{
    var dll = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Admin.Api", "bin", "Debug", "net10.0", "Admin.Api.dll");
    var jwks = Path.Combine(Path.GetTempPath(), $"p3-jwks-{Guid.NewGuid():N}.json");
    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
    await File.WriteAllTextAsync(jwks, JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } } }));
    try
    {
        var psi = new ProcessStartInfo("dotnet", [dll]) { RedirectStandardError = true, RedirectStandardOutput = true };
        psi.Environment["ADMIN_API_CONNECTION_STRING"] = adminCs;
        psi.Environment["Entra__Authority"] = Authority;
        psi.Environment["Entra__ClientId"] = ClientId;
        psi.Environment["Entra__SecretRef"] = DeployArn;
        psi.Environment["SecretRef__AllowedAccountIds"] = "000000000000";
        psi.Environment["SecretRef__AllowedRegions"] = "us-west-2";
        psi.Environment["BackofficeAuth__Issuer"] = Issuer;
        psi.Environment["BackofficeAuth__JwksPath"] = jwks;
        psi.Environment["Urls"] = "http://127.0.0.1:1"; // never reached
        edit(psi.Environment);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        var outp = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, err + outp);
    }
    finally { File.Delete(jwks); }
}
await Check("startup: Admin.Api process exits 2 on invalid Entra:Authority, value not echoed", async () =>
{
    var (code, err) = await RunAdminApi(e => e["Entra__Authority"] = "https://evil.example/SECRETPATH");
    return code == 2 && err.Contains("admin-api refused to start: config Entra:Authority") && !err.Contains("SECRETPATH");
});
foreach (var (n, v) in new[] { ("account outside allowlist", Arn("procx", acct: "111122223333")), ("region outside allowlist", Arn("procx", region: "eu-west-1")), ("not an ARN", "my-client-secret-PROCX") })
    await Check($"startup: Admin.Api process exits 2 on Entra:SecretRef {n} (TLD 9:49), value not echoed", async () =>
    {
        var (code, err) = await RunAdminApi(e => e["Entra__SecretRef"] = v);
        return code == 2 && err.Contains("admin-api refused to start: config Entra:SecretRef") && !err.Contains("PROCX") && !err.Contains("procx")
            && !err.Contains("111122223333") && !err.Contains("eu-west-1") && !err.Contains("arn:");
    });
await Check("startup: Admin.Api process exits 2 on missing Entra:SecretRef (TLD 9:49)", async () =>
{
    var (code, err) = await RunAdminApi(e => e.Remove("Entra__SecretRef"));
    return code == 2 && err.Contains("admin-api refused to start: config Entra:SecretRef");
});

// ---------- DEC-107 [PM t149u/t150u; TLD 2026-11-04 16:32 Q1=A]: reason length config, snapshotted at startup ----------
Ok($"DEC-107 startup: code defaults are one constant each (min {Mn}, max {Mx}; DEC-108 pin: 10 / 500, Confirmed DEC-108, PM Nov 5 2026 6:47 AM PT, Mohanraj verbatim \"10 to 500\"); hard bounds = DB CHECK llcc_reason 10..500 (db/migrations.sql L94)",
    Mn == 10 && Mx == 500 && AdminApiOptions.ReasonFloor == 10 && AdminApiOptions.ReasonCeiling == 500 && Opts().Validated() is { ReasonMinLength: Mn, ReasonMaxLength: Mx });
bool ReasonCfgAccepted(int mn, int mx) { try { var v = (Opts() with { ReasonMinLength = mn, ReasonMaxLength = mx }).Validated(); return v.ReasonMinLength == mn && v.ReasonMaxLength == mx; } catch (InvalidOperationException) { return false; } }
Ok("DEC-107 startup: 10..500, 50..200, 10..10 and 500..500 accepted (10 <= min <= max <= 500)", ReasonCfgAccepted(10, 500) && ReasonCfgAccepted(50, 200) && ReasonCfgAccepted(10, 10) && ReasonCfgAccepted(500, 500));
foreach (var (rn, mn, mx) in new[] { ("min 9 (below the DB floor)", 9, 500), ("min 0", 0, 500), ("min -1", -1, 500), ("max 501 (above the DB ceiling)", 10, 501),
                                     ("max 2000", 10, 2000), ("min > max (201 > 200)", 201, 200), ("min 500 > max 10", 500, 10), ("int.MinValue / int.MaxValue", int.MinValue, int.MaxValue) })
    Refuses($"DEC-107 startup: LoginConfig {rn} refused (Q1=A)", o => o with { ReasonMinLength = mn, ReasonMaxLength = mx },
        "config LoginConfig:ReasonMinLength / LoginConfig:ReasonMaxLength must satisfy 10 <= min <= max <= 500 (DB CHECK llcc_reason)");
await Check("DEC-107 startup: FromConfiguration reads LoginConfig:ReasonMinLength / ReasonMaxLength (50/200), absent -> the constants; a later config change does not reach the options (snapshot)", async () =>
{
    var jwks = Path.Combine(Path.GetTempPath(), $"p4j-jwks-{Guid.NewGuid():N}.json");
    var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
    await File.WriteAllTextAsync(jwks, JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } } }));
    try
    {
        Dictionary<string, string?> Base() => new() { ["ADMIN_API_CONNECTION_STRING"] = adminCs, ["Entra:Authority"] = Authority, ["Entra:ClientId"] = ClientId, ["Entra:SecretRef"] = DeployArn,
            ["SecretRef:AllowedAccountIds"] = "000000000000", ["SecretRef:AllowedRegions"] = "us-west-2", ["BackofficeAuth:Issuer"] = Issuer, ["BackofficeAuth:JwksPath"] = jwks };
        var o0 = AdminApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(Base()).Build()).Validated();
        var d = Base(); d["LoginConfig:ReasonMinLength"] = "50"; d["LoginConfig:ReasonMaxLength"] = "200";
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(d).Build();
        var o = AdminApiOptions.FromConfiguration(cfg).Validated();
        cfg["LoginConfig:ReasonMinLength"] = "60"; cfg["LoginConfig:ReasonMaxLength"] = "70";
        return o0.ReasonMinLength == Mn && o0.ReasonMaxLength == Mx && o.ReasonMinLength == 50 && o.ReasonMaxLength == 200 && cfg["LoginConfig:ReasonMinLength"] == "60";
    }
    finally { File.Delete(jwks); }
});
foreach (var (pn, vals, key) in new (string, (string k, string v)[], string)[]
{
    ("LoginConfig:ReasonMinLength 5 (below the DB floor)", [("LoginConfig__ReasonMinLength", "5")], "must satisfy 10 <= min <= max <= 500"),
    ("LoginConfig:ReasonMaxLength 501 (above the DB ceiling)", [("LoginConfig__ReasonMaxLength", "501")], "must satisfy 10 <= min <= max <= 500"),
    ("min 300 > max 200", [("LoginConfig__ReasonMinLength", "300"), ("LoginConfig__ReasonMaxLength", "200")], "must satisfy 10 <= min <= max <= 500"),
    ("LoginConfig:ReasonMinLength not an integer", [("LoginConfig__ReasonMinLength", "TENX")], "config LoginConfig:ReasonMinLength must be an integer"),
    ("LoginConfig:ReasonMaxLength not an integer (12.5)", [("LoginConfig__ReasonMaxLength", "12.5")], "config LoginConfig:ReasonMaxLength must be an integer"),
})
    await Check($"DEC-107 startup: Admin.Api process exits 2 on {pn}, message names the key, value not echoed", async () =>
    {
        var (code, err) = await RunAdminApi(e => { foreach (var (k, v) in vals) e[k] = v; });
        return code == 2 && err.Contains("admin-api refused to start: config LoginConfig:Reason") && err.Contains(key) && !err.Contains("TENX") && !err.Contains("12.5");
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
// F-ASTRAL [TLD 2026-11-04 15:03]: the main API's Error log, so 7g can show no request failed (no InvalidOperationException out of the outbox).
var mainLog = new CaptureLogger();
var app = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port}" }, dir, extraLogger: mainLog);
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
var oldHeader = 0; // responses carrying the pre-9:20 X-Correlation-Id header
bool IsCorr(string? v) => v is not null && Guid.TryParseExact(v, "D", out var g) && g.Version == 7;
var responsesForSchema = new List<(string def, string body)>();
const string HarnessErrorBody = "{\"error\":\"harness_transport_error\",\"fields\":[]}";
async Task<R> Send(HttpMethod m, string path, string? token, string? body = null, string? ifMatch = null, Action<HttpRequestMessage>? edit = null, HttpClient? via = null)
{
    using var req = new HttpRequestMessage(m, path);
    if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
    edit?.Invoke(req);
    HttpResponseMessage res0; string text;
    // [SSE2 review 06dc8ac..ba85c77 (3fac1d52) Info, MD4] A transport failure (broken pipe, reset) on one request records a FAIL with the
    // exception types and returns a placeholder response, so the run carries on and ends with a clean FAIL count instead of aborting.
    try { res0 = await (via ?? http).SendAsync(req); text = await res0.Content.ReadAsStringAsync(); }
    catch (Exception e) when (e is IOException or SocketException || e is HttpRequestException { InnerException: IOException or SocketException })
    {
        Ok($"transport: {m} {path} got a response", false, $"{e.GetType().Name}{(e.InnerException is { } ie ? "/" + ie.GetType().Name + ": " + ie.Message : ": " + e.Message)}");
        return new R(0, HarnessErrorBody, null, null, null, null, null, null);
    }
    using var res = res0;
    allBodies.Add(text);
    // TLD 13:27 item 1: reason-bounds error bodies are checked against prb_error_response (the only place current_row_version may be 0);
    // every other endpoint stays on error_response (current_row_version >= 1).
    if ((int)res.StatusCode is >= 400 and < 500 and not 401 && text.Length > 0) responsesForSchema.Add((path.Contains("/login-config/reason-bounds") ? "prb_error_response" : "error_response", text));
    string? H(string k) => res.Headers.TryGetValues(k, out var v) ? string.Join(",", v) : res.Content.Headers.TryGetValues(k, out var cv) ? string.Join(",", cv) : null;
    var r = new R((int)res.StatusCode, text, H("ETag"), H("Location"), H("WWW-Authenticate"), H("Cache-Control"), H("Server"), H(LoginConfigApi.RequestIdHeader));
    allCorr.Add(r.Corr);
    if (H("X-Correlation-Id") is not null) oldHeader++;
    return r;
}
Task<R> Get(string path, string? tok) => Send(HttpMethod.Get, path, tok);
Task<R> Put(Guid loc, string? tok, object body, string? ifMatch) => Send(HttpMethod.Put, $"/api/orgs/locations/{loc:D}/login-config", tok, J(body), ifMatch);
Task<R> Post(string? tok, object body) => Send(HttpMethod.Post, "/api/orgs/locations", tok, J(body));
string Cfg(Guid l) => $"/api/orgs/locations/{l:D}/login-config";
static string J(object o) => o is string s ? s : JsonSerializer.Serialize(o);
// [SSE2 review 3fac1d52 Info, MD4b] A body that is not a JSON object records a FAIL and yields the placeholder, so the run carries on.
JsonObject Obj(string s)
{
    try { return JsonNode.Parse(s)!.AsObject(); }
    catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException)
    { Ok("response body is a JSON object", false, $"{e.GetType().Name}, {s.Length} chars"); return JsonNode.Parse(HarnessErrorBody)!.AsObject(); }
}
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
// SSE2 follow-up (1): scopePartner = the expected scope.partner_id (create: the body's partner = targetId; update: the LOCATION's
// partner), actorPartner = the caller's partner in details.actor_partner_id (null = key absent: Super Admin or not_admin).
bool Denied(List<JsonObject> evs, string reason, Guid? targetId, Guid? scopePartner, Guid? actorPartner, string? adminType, string op, params string[] attempted) =>
    evs.Count == 1 && Valid(evs[0]) && (string)evs[0]["event_type"]! == "auth.config.change_denied" && (string)evs[0]["details"]!["reason"]! == reason
    && (string?)evs[0]["target"]!["id"] == targetId?.ToString("D")
    && (op != "create" || scopePartner == targetId)
    && (string?)evs[0]["scope"]!["partner_id"] == scopePartner?.ToString("D")
    && (actorPartner is { } ap ? (string?)evs[0]["details"]!["actor_partner_id"] == ap.ToString("D") : !evs[0]["details"]!.AsObject().ContainsKey("actor_partner_id"))
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
// TLD 2026-11-04 9:49 (D-51): an Entra body carries only the tenant; secret_ref comes from Entra:SecretRef.
Dictionary<string, object?> EntraCfg(Guid tenant, bool fallback = false, bool mfa = true, string display = "Riverbend Casino") => new()
{
    ["provider_kind"] = "entra", ["display_name"] = display, ["entra_allowed_tenant_ids"] = new[] { tenant.ToString("D") },
    ["allow_db_fallback"] = fallback, ["mfa_required"] = mfa,
};
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
    if (uiDir is not null)
    {
        var personas = new Dictionary<string, UiHost.Persona>
        { ["sa"] = new(SA, "super"), ["pa"] = new(PA, "partner"), ["pb"] = new(PB, "partner"), ["rd"] = new(RD, "partner"), ["anon"] = new("", "partner") };
        // DEC-107 [TLD 2026-11-04 16:32]: a second API with LoginConfig 50/200 behind /c50/..., so the e2e can show the page takes
        // the numbers from the API (limits in Create, the GET in Edit), not from its own code. Same DB and directory; started first
        // so FreePort skips its port for the UI host.
        var c50Port = FreePort(port);
        var c50 = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{c50Port}", ReasonMinLength = 50, ReasonMaxLength = 200 }, dir);
        try
        {
            await c50.StartAsync();
            exitCode = await UiHost.RunAsync(uiDir, port, FreePort(port), personas, readerLocations, P_A, P_B, [T1, T2, T3], ownerDs, c50Port);
            await c50.StopAsync();
        }
        finally { await c50.DisposeAsync(); }
        goto uiHostDone; // leaves the try: the finally below stops the API and empties the tables
    }
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
    ForSchema("login_config_saved_restricted", rA1); // DEC-107: POST 201 carries no reason bounds
    {
        var b = Obj(rA1.Body);
        Ok("create: ETag \"1\", Location header, Cache-Control no-store, no Server header",
            rA1.ETag == "\"1\"" && rA1.Location == $"/api/orgs/locations/{A1:D}/login-config" && rA1.CacheControl == "no-store" && rA1.Server is null);
        Ok("create: restricted view (no oidc_* keys)", !b.ContainsKey("oidc_authority") && !b.ContainsKey("oidc_client_id") && (long)b["row_version"]! == 1);
        Ok("Q2 create: PA 201 response has NO secret key (omitted, not null) [TLD 10:02 Q2, mockup cbb3ab4b]", !b.ContainsKey("secret"), rA1.Body);
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

    var rB1 = await Post(SA, Create(P_B, "North Valley Cage 1", EntraCfg(T1)));
    var B1 = rB1.Status == 201 ? LocOf(rB1) : Guid.Empty;
    Ok("create: SA entra in partner B -> 201", rB1.Status == 201, rB1.Body);
    ForSchema("login_config_saved_super", rB1);
    {
        var b = Obj(rB1.Body);
        Ok("create: super view shows server-filled oidc_* from Entra:Authority / Entra:ClientId",
            (string?)b["oidc_authority"] == Authority && (string?)b["oidc_client_id"] == ClientId);
        Ok("Q2 create: SA 201 response has secret {set: true} and nothing else (no rotated_at) [TLD 10:02 Q2, TLD 10:42]", b["secret"] is JsonObject so && so.Count == 1 && (bool)so["set"]! && !so.ContainsKey("rotated_at"), rB1.Body);
        var ev = await NewEvents();
        Ok("create: SA event lists oidc_* and secret_ref in changed_fields, no value",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super"
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "display_name", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref", "mfa_required"]));
        Ok("create: entra body with only the tenant -> secret_ref stored = Entra:SecretRef (owner read; TLD 9:49, D-51)", await OScalar<string>("SELECT secret_ref FROM auth.location_login_config WHERE location_id=$1", B1) == DeployArn);
    }
    {
        var r = await Post(PA, Create(P_B, "Sneaky", DbCfg()));
        Ok("create: PA other partner -> 404 not_found (identical body)", Err(r, 404, "not_found") && r.Body == """{"error":"not_found"}""");
        Ok("create: PA other partner -> forbidden_scope event (target partner, scope [])", Denied(await NewEvents(), "forbidden_scope", P_B, P_B, P_A, "partner", "create",
            "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        Ok("create: nothing created for partner B by PA", await OScalar<long>("SELECT count(*) FROM auth.location WHERE partner_id=$1", P_B) == 1);
    }
    {
        var r = await Post(PA, Create(P_A, "No MFA", DbCfg(mfa: false)));
        Ok("create: PA mfa_required=false -> 403 not_super_admin", Err(r, 403, "not_super_admin"));
        Ok("create: not_super_admin event (attempted mfa_required)", Denied(await NewEvents(), "not_super_admin", P_A, P_A, P_A, "partner", "create", "mfa_required"));
        r = await Post(PA, Create(P_A, "Fallback", EntraCfg(T2, fallback: true)));
        Ok("create: PA allow_db_fallback=true -> 403 not_super_admin + event", Err(r, 403, "not_super_admin")
            && Denied(await NewEvents(), "not_super_admin", P_A, P_A, P_A, "partner", "create", "allow_db_fallback"));
    }
    Guid A2;
    {
        var r = await Post(SA, Create(P_A, "Fallback Cage", EntraCfg(T2, fallback: true)));
        Ok("create: SA fallback without reason -> 400 reason_required, no event", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0);
        r = await Post(SA, Create(P_A, "Fallback Cage", With(EntraCfg(T2, fallback: true), "reason", "  short  ")));
        Ok("create: SA reason under 10 after trim -> 422 login_config/reason reason_length", HasField(r, "login_config/reason", "reason_length"));
        r = await Post(SA, Create(P_A, "Fallback Cage", With(EntraCfg(T2, fallback: true), "reason", "   Pilot fallback per OPS-77   ")));
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
        // TLD 2026-11-04 9:49 (D-51): secret_ref in a create body, any value, -> 422 field_not_writable, value never echoed.
        foreach (var (n, v) in new (string, object?)[] { ("allowed ARN", Arn("create-x")), ("other account ARN", Arn("create-x", acct: "111122223333")), ("null", null), ("number", 42), ("plain string", "client-secret-CRX") })
        {
            r = await Post(SA, Create(P_A, "x", With(EntraCfg(T3), "secret_ref", v)));
            Ok($"create: entra login_config + secret_ref {n} -> 422 login_config/secret_ref field_not_writable, not echoed", HasField(r, "login_config/secret_ref", "field_not_writable")
                && !r.Body.Contains("arn:") && !r.Body.Contains("FAKE") && !r.Body.Contains("CRX") && !r.Body.Contains("111122223333"), r.Body);
        }
        r = await Post(SA, Create(P_A, "x", With(DbCfg(), "secret_ref", null)));
        Ok("create: database login_config + secret_ref null -> 422 field_not_writable", HasField(r, "login_config/secret_ref", "field_not_writable"), r.Body);
        Ok("create: no secret_ref_required code exists any more", !r.Body.Contains("secret_ref_required"));
        r = await Post(SA, Create(P_A, "x", With(With(EntraCfg(T3), "oidc_authority", "https://evil.invalid"), "oidc_client_id", ClientId)));
        Ok("create: oidc_* in login_config -> field_not_writable (both)", HasField(r, "login_config/oidc_authority", "field_not_writable") && HasField(r, "login_config/oidc_client_id", "field_not_writable"));
        r = await Post(SA, Create(P_A, "x", "not an object"));
        Ok("create: login_config not an object -> login_config type", HasField(r, "login_config", "type"));
        Ok("create: field errors write no event", (await NewEvents()).Count == 0);
        r = await Post(SA, Create(P_A, "Tenant thief", EntraCfg(T1)));
        Ok("create: SA tenant bound to partner B -> 422 tenant_bound_elsewhere", HasField(r, "login_config/entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        Ok("create: tenant_bound_elsewhere event (super, target partner A)", Denied(await NewEvents(), "tenant_bound_elsewhere", P_A, P_A, null, "super", "create", "entra_allowed_tenant_ids"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "[1,2]");
        Ok("create: non-object body -> 400 malformed_body", Err(r, 400, "malformed_body"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, """{"partner_id":"x","partner_id":"y"}""");
        Ok("create: duplicate keys -> 400 malformed_body", Err(r, 400, "malformed_body"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "{\"name\":\"" + new string('a', 70_000) + "\"}");
        Ok("create: body over 64 KiB -> 413 payload_too_large (TLD 9:28 P3-2)", Err(r, 413, "payload_too_large"), $"{r.Status}");
        // TLD 9:04 #2: a refused create is audited (location_id null, target = body partner_id). #8: the 403 is decided before the body.
        foreach (var (n, t) in new[] { ("reader", RD), ("no role", NO), ("custom role", CU) })
        {
            r = await Post(t, Create(P_A, "x", DbCfg()));
            Ok($"create: {n} -> 403 forbidden + not_admin event (target partner A, location_id null, no admin_type/actor_partner_id)", Err(r, 403, "forbidden")
                && Denied(await NewEvents(), "not_admin", P_A, P_A, null, null, "create", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        }
        r = await Post(RD, Create(P_B, "x", With(EntraCfg(T3), "secret_ref", Arn("rd"))));
        Ok("create: reader naming partner B -> 403 + not_admin event targeting partner B (from the body)", Err(r, 403, "forbidden")
            && Denied(await NewEvents(), "not_admin", P_B, P_B, null, null, "create", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required"));
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
                && Denied(await NewEvents(), "not_admin", null, null, null, null, "create"), $"{r.Status} {r.Body}");
            r = await Send(HttpMethod.Put, Cfg(A1), CU, ifMatch: "\"1\"", edit: Body);
            Ok($"put #8: no permission + {n} -> 403 forbidden (never 400/413) + not_admin event (attempted none)", Err(r, 403, "forbidden")
                && Denied(await NewEvents(), "not_admin", A1, P_A, null, null, "update"), $"{r.Status} {r.Body}");
        }
        r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, "{\"name\":\"" + bigName + "\"}");
        Ok("create: SA body over 1 MiB -> 413 payload_too_large (handler cap, TLD 9:28 P3-2; never 400/500)", Err(r, 413, "payload_too_large"), $"{r.Status}");
        Ok("create: oversized SA body writes no event", (await NewEvents()).Count == 0);
        Ok("create: none of the refused creates added a location", await Count("location") == 3);
    }

    // ================= 3. GET =================
    readerLocations.Add(A1);
    {
        var r = await Get(Cfg(A1), PA);
        Ok("get: PA own -> 200 restricted view, ETag", r.Status == 200 && r.ETag == "\"1\"" && !Obj(r.Body).ContainsKey("oidc_authority"));
        Ok("Q2 get: PA -> no secret key (omitted, not null) [TLD 10:02 Q2]", r.Status == 200 && !Obj(r.Body).ContainsKey("secret"), r.Body);
        ForSchema("login_config_get_restricted", r);
        Ok($"DEC-107 get: PA -> reason_min_length {Mn}, reason_max_length {Mx} (startup snapshot)", (int?)Obj(r.Body)["reason_min_length"] == Mn && (int?)Obj(r.Body)["reason_max_length"] == Mx, r.Body);
        r = await Get(Cfg(B1), SA);
        Ok("get: SA any -> 200 super view with oidc_*", r.Status == 200 && (string?)Obj(r.Body)["oidc_client_id"] == ClientId);
        Ok("Q2 get: SA -> secret {set: true} present, nothing else (no rotated_at) [TLD 10:02 Q2, TLD 10:42]", Obj(r.Body)["secret"] is JsonObject gs && gs.Count == 1 && (bool)gs["set"]! && !gs.ContainsKey("rotated_at"), r.Body);
        var saDb = await Get(Cfg(A1), SA);
        Ok("Q2 get: SA on a database location -> secret {set: false} present, nothing else [TLD 10:42]", Obj(saDb.Body)["secret"] is JsonObject ds && ds.Count == 1 && !(bool)ds["set"]!, saDb.Body);
        ForSchema("login_config_get_super", saDb);
        ForSchema("login_config_get_super", r);
        Ok($"DEC-107 get: SA (entra and database) -> reason_min_length {Mn}, reason_max_length {Mx}", new[] { r, saDb }.All(x => (int?)Obj(x.Body)["reason_min_length"] == Mn && (int?)Obj(x.Body)["reason_max_length"] == Mx), r.Body);
        var miss = await Get(Cfg(missing), PA);
        var oos = await Get(Cfg(B1), PA);
        Ok("get: PA other partner -> 404, body identical to missing", Err(oos, 404, "not_found") && oos.Body == miss.Body && Err(miss, 404, "not_found"));
        Ok("get: SA missing -> 404", Err(await Get(Cfg(missing), SA), 404, "not_found"));
        Ok("get: bad id -> 404", Err(await Get("/api/orgs/locations/not-a-uuid/login-config", SA), 404, "not_found"));
        r = await Get(Cfg(A1), RD);
        Ok("get: org:V reader of the location -> 200 restricted", r.Status == 200 && !Obj(r.Body).ContainsKey("oidc_authority"));
        Ok("Q2 get: org:V -> no secret key (omitted, not null) [TLD 10:02 Q2]", r.Status == 200 && !Obj(r.Body).ContainsKey("secret"), r.Body);
        ForSchema("login_config_get_restricted", r);
        Ok($"DEC-107 get: org:V -> reason_min_length {Mn}, reason_max_length {Mx}", (int?)Obj(r.Body)["reason_min_length"] == Mn && (int?)Obj(r.Body)["reason_max_length"] == Mx, r.Body);
        Ok("get: org:V reader of another location -> 404", Err(await Get(Cfg(A2), RD), 404, "not_found"));
        Ok("get: no role -> 403 forbidden", Err(await Get(Cfg(A1), NO), 403, "forbidden"));
        Ok("get: custom role -> 403 forbidden", Err(await Get(Cfg(A1), CU), 403, "forbidden"));
        Ok("get: reads write no event", (await NewEvents()).Count == 0);
        Ok("Q2 create/PUT: org:V never gets a view body (403 forbidden, no secret key; 2 not_admin events)", Err(await Post(RD, Create(P_A, "RD try", DbCfg())), 403, "forbidden")
            && Err(await Put(A1, RD, DbCfg(), "\"1\""), 403, "forbidden") && (await NewEvents()).Count == 2);
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
        Ok("put: empty object -> required on every editable key (not secret_ref)", new[] { "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required" }.All(k => HasField(r, k, "required")) && !r.Body.Contains("secret_ref"));
        r = await Put(A1, PA, new Dictionary<string, object?> { ["provider_kind"] = "database", ["display_name"] = " x ", ["entra_allowed_tenant_ids"] = new[] { T3.ToString() }, ["secret_ref"] = Arn("x"), ["allow_db_fallback"] = true, ["mfa_required"] = true }, "\"1\"");
        Ok("put: database + tenant -> field_not_allowed; secret_ref -> field_not_writable (TLD 9:49); fallback -> fallback_not_applicable", HasField(r, "entra_allowed_tenant_ids", "field_not_allowed") && HasField(r, "secret_ref", "field_not_writable") && !HasField(r, "secret_ref", "field_not_allowed") && HasField(r, "allow_db_fallback", "fallback_not_applicable") && !r.Body.Contains("arn:"), r.Body);
        r = await Put(A1, PA, new Dictionary<string, object?> { ["provider_kind"] = "entra", ["display_name"] = "x", ["entra_allowed_tenant_ids"] = new[] { T3.ToString(), T2.ToString() }, ["allow_db_fallback"] = false, ["mfa_required"] = true }, "\"1\"");
        Ok("put: entra with 2 tenants -> tenant_count", HasField(r, "entra_allowed_tenant_ids", "tenant_count"));
        r = await Put(A1, PA, EntraCfg(Guid.Empty), "\"1\"");
        Ok("put: nil tenant -> tenant_id_format", HasField(r, "entra_allowed_tenant_ids/0", "tenant_id_format"));
        r = await Put(A1, PA, With(EntraCfg(T3), "entra_allowed_tenant_ids", new[] { "{" + T3 + "}" }), "\"1\"");
        Ok("put: braced tenant -> tenant_id_format; switch to entra with only the tenant -> no secret_ref error (TLD 9:49)", HasField(r, "entra_allowed_tenant_ids/0", "tenant_id_format") && !r.Body.Contains("secret_ref"));
        r = await Put(A1, PA, With(EntraCfg(T3), "secret_ref", 42), "\"1\"");
        Ok("put: secret_ref number -> field_not_writable (not type)", HasField(r, "secret_ref", "field_not_writable") && !HasField(r, "secret_ref", "type"));
        r = await Put(A1, PA, With(EntraCfg(T3), "reason", new string('r', Mx + 1)), "\"1\"");
        Ok($"put: reason {Mx + 1} chars (max + 1) -> reason_length (also on a non-downgrade)", HasField(r, "reason", "reason_length"));
        r = await Put(A1, PA, With(EntraCfg(T3), "reason", " " + new string('r', Mx) + new string(' ', 1999 - Mx)), "\"1\"");
        Ok($"put: reason {Mx} (max) after trim (2000 raw) accepted by validation", r.Status == 200, r.Body);
        Ok("put: db -> entra (tenant only) stores secret_ref = Entra:SecretRef (TLD 9:49); PA response has no secret key (Q2)", r.Status == 200 && !Obj(r.Body).ContainsKey("secret")
            && await OScalar<string>("SELECT secret_ref FROM auth.location_login_config WHERE location_id=$1", A1) == DeployArn, r.Body);
        // That PUT applied (db -> entra T3). Put it back to database for the next steps.
        await NewEvents();
        r = await Put(A1, PA, DbCfg(), "\"2\"");
        Ok("put: entra -> database (no secret_ref in body) -> 200, oidc + secret cleared by the server (TLD 9:49)", r.Status == 200 && r.ETag == "\"3\"" && !Obj(r.Body).ContainsKey("secret")
            && await OScalar<long>("SELECT count(*) FROM auth.location_login_config WHERE location_id=$1 AND oidc_authority IS NULL AND oidc_client_id IS NULL AND secret_ref IS NULL", A1) == 1, r.Body);
        var saAfterDb = Obj((await Get(Cfg(A1), SA)).Body);
        Ok("Q2 get: SA on a database location with past secret_ref change rows -> secret {set: false} only [TLD 10:42]",
            saAfterDb["secret"] is JsonObject dbs && dbs.Count == 1 && !(bool)dbs["set"]!, saAfterDb.ToJsonString());
        var evDb = await NewEvents();
        Ok("put: entra -> database event changed_fields lists secret_ref (cleared), no value", evDb.Count == 1 && Valid(evDb[0])
            && evDb[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref"])
            && !evDb[0].ToJsonString().Contains("arn:"), evDb.Count > 0 ? evDb[0].ToJsonString() : "none");

        // TLD 2026-11-04 9:49 (D-51): any secret_ref in a PUT body -> 422 field_not_writable, value never echoed (the allowlist is startup-only now).
        foreach (var (n, v) in new (string, object?)[] { ("allowed ARN", Arn("x")), ("account outside allowlist", Arn("x", acct: "111122223333")), ("region outside allowlist", Arn("x", region: "eu-west-1")),
                                                         ("partition aws-cn", "arn:aws-cn:secretsmanager:us-west-2:000000000000:secret:FAKE-x"), ("null", null), ("empty", ""), ("object", new { arn = Arn("x") }) })
        {
            r = await Put(A1, PA, With(EntraCfg(T3), "secret_ref", v), "\"3\"");
            Ok($"put: entra body + secret_ref {n} -> 422 field_not_writable, value/account/region not echoed",
                HasField(r, "secret_ref", "field_not_writable") && !r.Body.Contains("111122223333") && !r.Body.Contains("eu-west-1") && !r.Body.Contains("arn:") && !r.Body.Contains("FAKE"), r.Body);
        }
        r = await Put(A1, SA, With(DbCfg(), "secret_ref", null), "\"3\"");
        Ok("put: SA database body + secret_ref null -> 422 field_not_writable", HasField(r, "secret_ref", "field_not_writable"), r.Body);
        Ok("put: secret_ref refusals change nothing, no event", Rv(A1) == 3 && (await NewEvents()).Count == 0);

        // Spec example: PA database -> entra with a secret (applies immediately, oidc_* server-filled).
        r = await Put(A1, PA, EntraCfg(T3), "\"3\"");
        Ok("put: PA db -> entra -> 200, ETag \"4\", restricted view", r.Status == 200 && r.ETag == "\"4\"" && !Obj(r.Body).ContainsKey("oidc_authority") && !Obj(r.Body).ContainsKey("secret"), r.Body);
        ForSchema("login_config_saved_restricted", r); // DEC-107: PUT 200 carries no reason bounds
        var ev = await NewEvents();
        Ok("put: update event changed_fields provider_kind, tenants, oidc_*, secret_ref; before/after; no secret value",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["action"]! == "update" && (long)ev[0]["details"]!["row_version"]! == 4
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["provider_kind", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref"])
            && (string)ev[0]["details"]!["before"]!["provider_kind"]! == "database" && !ev[0].ToJsonString().Contains("arn:"), ev.Count > 0 ? ev[0].ToJsonString() : "none");
        Ok("put: oidc_* and secret_ref filled from config in the row", await OScalar<string>("SELECT oidc_client_id FROM auth.location_login_config WHERE location_id=$1", A1) == ClientId
            && await OScalar<string>("SELECT secret_ref FROM auth.location_login_config WHERE location_id=$1", A1) == DeployArn);

        // Downgrades.
        r = await Put(A1, PA, EntraCfg(T3, mfa: false), "\"4\"");
        Ok("put: PA mfa off -> 403 not_super_admin", Err(r, 403, "not_super_admin"));
        Ok("put: not_super_admin event (attempted mfa_required, actor_partner_id)", Denied(ev = await NewEvents(), "not_super_admin", A1, P_A, P_A, "partner", "update", "mfa_required")
            && (string?)ev[0]["details"]!["actor_partner_id"] == P_A.ToString());
        r = await Put(A1, PA, EntraCfg(T3, fallback: true), "\"4\"");
        Ok("put: PA fallback on -> 403 not_super_admin + event", Err(r, 403, "not_super_admin") && Denied(await NewEvents(), "not_super_admin", A1, P_A, P_A, "partner", "update", "allow_db_fallback"));
        Ok("put: refused downgrades change nothing", Rv(A1) == 4);
        r = await Put(A1, SA, EntraCfg(T3, mfa: false), "\"4\"");
        Ok("put: SA mfa off, no reason -> 400 reason_required, no event", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0);
        r = await Put(A1, SA, With(EntraCfg(T3, mfa: false), "reason", "            "), "\"4\"");
        Ok("put: SA mfa off, whitespace reason -> 400 reason_required", Err(r, 400, "reason_required"));
        r = await Put(A1, SA, With(EntraCfg(T3, mfa: false), "reason", "Client's Entra conditional access already enforces MFA; ticket OPS-1234"), "\"4\"");
        Ok("put: SA mfa off + reason -> 200 super view", r.Status == 200 && (bool)Obj(r.Body)["mfa_required"]! == false && Obj(r.Body).ContainsKey("oidc_authority"));
        Ok("Q2 put: SA 200 response has secret {set: true} and nothing else (no rotated_at) [TLD 10:02 Q2, TLD 10:42]", Obj(r.Body)["secret"] is JsonObject ps && ps.Count == 1 && (bool)ps["set"]! && !ps.ContainsKey("rotated_at"), r.Body);
        ForSchema("login_config_saved_super", r);
        ev = await NewEvents();
        Ok("put: SA downgrade event admin_type super + reason", ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super" && (string?)ev[0]["details"]!["reason"] is { Length: > 10 });
        r = await Put(A1, PA, EntraCfg(T3, mfa: false, display: "Riverbend Casino East"), "\"5\"");
        Ok("put: PA edits display_name while mfa already off (unchanged field is not a downgrade) -> 200", r.Status == 200, r.Body);
        r = await Put(A1, PA, EntraCfg(T3, mfa: true, display: "Riverbend Casino East"), "\"6\"");
        Ok("put: PA upgrade mfa back on -> 200", r.Status == 200 && (bool)Obj(r.Body)["mfa_required"]!);
        r = await Put(A2, PA, EntraCfg(T2, fallback: false), "\"1\"");
        Ok("put: PA upgrade fallback off -> 200", r.Status == 200 && (bool)Obj(r.Body)["allow_db_fallback"]! == false, r.Body);
        await NewEvents();

        // Entra -> Entra keeps the stored secret_ref (A-21 analogue, TLD 9:49) even if the row differs from Entra:SecretRef; and no-op.
        await OExec("UPDATE auth.location_login_config SET secret_ref = $2 WHERE location_id = $1", A1, Arn("llc/pre-d51-row"));
        r = await Put(A1, PA, EntraCfg(T3, display: "Riverbend Casino North"), "\"7\"");
        ev = await NewEvents();
        Ok("put: entra -> entra edit -> changed_fields [display_name] only, stored secret_ref kept, SA view still secret {set: true} only", r.Status == 200 && ev.Count == 1
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["display_name"])
            && !Obj(r.Body).ContainsKey("secret") && Obj((await Get(Cfg(A1), SA)).Body)["secret"] is JsonObject ks && ks.Count == 1 && (bool)ks["set"]!
            && await OScalar<string>("SELECT secret_ref FROM auth.location_login_config WHERE location_id=$1", A1) == Arn("llc/pre-d51-row"), r.Body);
        await OExec("UPDATE auth.location_login_config SET secret_ref = $2 WHERE location_id = $1", A1, DeployArn);
        r = await Put(A1, PA, EntraCfg(T3, display: "  Riverbend Casino North  "), "\"8\"");
        Ok("put: no-op (trimmed same values) -> 200, row_version unchanged, no event (OQ-P3-6)", r.Status == 200 && r.ETag == "\"8\"" && Rv(A1) == 8 && (await NewEvents()).Count == 0);

        // Tenant rule (Super Admin too).
        r = await Put(A1, SA, EntraCfg(T1), "\"8\"");
        Ok("put: SA tenant bound to partner B -> 422 tenant_bound_elsewhere", HasField(r, "entra_allowed_tenant_ids/0", "tenant_bound_elsewhere"));
        Ok("put: tenant_bound_elsewhere event (super)", Denied(await NewEvents(), "tenant_bound_elsewhere", A1, P_A, null, "super", "update", "entra_allowed_tenant_ids"));
        r = await Put(A2, SA, EntraCfg(T3), "\"2\"");
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
            && Denied(await NewEvents(), "forbidden_scope", B1, P_B, P_A, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        r = await Put(B1, PA, DbCfg(), null);
        Ok("put: PA other partner without If-Match -> still 404 (step 3-4 before 5)", Err(r, 404, "not_found") && (await NewEvents()).Count == 1);
        r = await Put(A1, RD, With(DbCfg(), "secret_ref", null), "\"8\"");
        ev = await NewEvents();
        Ok("put: org:V reader -> 403 forbidden + not_admin event without admin_type/actor_partner_id", Err(r, 403, "forbidden")
            && Denied(ev, "not_admin", A1, P_A, null, null, "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required")
            && !ev[0]["details"]!.AsObject().ContainsKey("actor_partner_id"));
        r = await Put(A1, CU, DbCfg(), "\"8\"");
        Ok("put: custom role with the permission -> 403 forbidden + not_admin event", Err(r, 403, "forbidden") && Denied(await NewEvents(), "not_admin", A1, P_A, null, null, "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"));
        r = await Put(missing, NO, DbCfg(), "\"8\"");
        Ok("put: no role on a missing location -> 403 forbidden, no event", Err(r, 403, "forbidden") && (await NewEvents()).Count == 0);
        Ok("put: B1 untouched by any partner A caller", Rv(B1) == 1);
    }

    // ================= 4b. config is the source of truth (TLD 2026-11-04 10:02 Q1) =================
    // Entra location B1 was created under Entra:SecretRef = DeployArn. "Restart" with a different allowlisted Entra:SecretRef and
    // Entra:ClientId: what the login reader feeds to ISecretResolver (LocationLoginConfig.SecretRef) and the Super Admin view follow
    // the new config, and the row is not rewritten (its copy still says DeployArn; nothing reads it; V11-ROWSECRETCLEANUP).
    {
        await using var loginDs = NpgsqlDataSource.Create(loginCs);
        string RotArn = Arn("deploy/app-reg-rotated"), RotClient = "5f0c3e1a-00cc-4000-8000-0000000000dd"; // SSE2-FAKE-TEST-ONLY
        async Task<string> RowSnap() => await OScalar<string>(
            "SELECT concat_ws('|', secret_ref, oidc_authority, oidc_client_id, row_version::text, updated_at::text, updated_by::text) FROM auth.location_login_config WHERE location_id=$1", B1);
        var snap0 = await RowSnap();
        var changes0 = await Count("location_login_config_change");
        var before = await new NpgsqlLocationLoginConfigReader(loginDs, Opts().Validated().Entra()).GetAsync(B1, default);
        Ok("Q1: reader under the first Entra:SecretRef -> SecretRef = DeployArn, client id from Entra:ClientId", before?.SecretRef?.Reveal() == DeployArn && before.OidcClientId == ClientId);
        var opts2 = Opts() with { EntraSecretRef = RotArn, EntraClientId = RotClient.ToUpperInvariant() };
        var port2 = FreePort(port);
        await using (var app2 = await AdminApiHost.BuildAsync(opts2 with { Urls = $"http://127.0.0.1:{port2}" }, dir))
        {
            await app2.StartAsync();
            using var http2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port2}") };
            using var req = new HttpRequestMessage(HttpMethod.Get, Cfg(B1));
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SA);
            using var resp = await http2.SendAsync(req);
            var body = Obj(await resp.Content.ReadAsStringAsync());
            Ok("Q1: after restart with a new allowlisted Entra:SecretRef / Entra:ClientId, SA view shows the new client id and secret.set",
                (int)resp.StatusCode == 200 && (string?)body["oidc_client_id"] == RotClient && (string?)body["oidc_authority"] == Authority && (bool)body["secret"]!["set"]!, body.ToJsonString());
            await app2.StopAsync();
        }
        var after = await new NpgsqlLocationLoginConfigReader(loginDs, opts2.Validated().Entra()).GetAsync(B1, default);
        Ok("Q1: after restart the reader (resolver input) returns the NEW Entra:SecretRef and client id, not the row copy",
            after?.SecretRef?.Reveal() == RotArn && after.OidcClientId == RotClient && after.OidcAuthority == Authority && after.RowVersion == before!.RowVersion);
        Ok("Q1: the row was not rewritten (secret_ref copy still DeployArn, row_version/updated_* same), no change row, no event",
            await RowSnap() == snap0 && snap0.StartsWith(DeployArn + "|", StringComparison.Ordinal) && await Count("location_login_config_change") == changes0
            && (await NewEvents()).Count == 0);
        Ok("Q1: EntraDeployment prints [redacted] (no ARN)", !opts2.Validated().Entra().ToString().Contains("arn:") && !after!.ToString().Contains("arn:"));
        await Check("Q1: reader refuses a null EntraDeployment (fail closed)", () =>
        {
            try { _ = new NpgsqlLocationLoginConfigReader(loginDs, null!); return Task.FromResult(false); }
            catch (ArgumentNullException) { return Task.FromResult(true); }
        });
        // TLD 10:12: the rules stay in Admin.Api (AdminApiOptions.Validated); Entra() runs them, so a non-allowlisted value never
        // reaches a reader, and the refusal names the key only.
        await Check("Q1/TLD 10:12: Entra() runs the Admin.Api validator (non-allowlisted Entra:SecretRef -> refused, key named, no ARN)", () =>
        {
            var bad = "arn:aws:secretsmanager:us-west-2:111111111111:secret:FAKE-not-allowlisted-AbCdEf"; // SSE2-FAKE-TEST-ONLY
            try { _ = (Opts() with { EntraSecretRef = bad }).Entra(); return Task.FromResult(false); }
            catch (InvalidOperationException e) { return Task.FromResult(e.Message.Contains("Entra:SecretRef") && !e.Message.Contains("arn:") && !e.Message.Contains("111111111111")); }
        });
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
            Denied(await NewEvents(), "forbidden_scope", B1, P_B, P_A, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "secret_ref", "allow_db_fallback", "mfa_required"));
        Ok("K4 crafted: partner B row unchanged", Rv(B1) == 1 && await OScalar<string>("SELECT display_name FROM auth.location_login_config WHERE location_id=$1", B1) == "Riverbend Casino");
        r = await Send(HttpMethod.Get, Cfg(B1) + qs, PA, edit: Craft);
        Ok("K4 crafted: PA GET partner B with crafted headers/query -> 404", Err(r, 404, "not_found"));
        r = await Send(HttpMethod.Get, Cfg(B1) + "/changes" + qs, PA, edit: Craft);
        Ok("K4 crafted: PA history of partner B with crafted headers/query -> 404", Err(r, 404, "not_found"));
        var withPartner = J(Merge(DbCfg(), new Dictionary<string, object?> { ["partner_id"] = P_B.ToString(), ["admin_type"] = "super" }));
        r = await Send(HttpMethod.Put, Cfg(A2) + qs, PA, withPartner, $"\"{Rv(A2)}\"", Craft);
        Ok("K4 crafted: partner_id / admin_type in an own-location PUT body -> 422 unknown_field", HasField(r, "partner_id", "unknown_field") && HasField(r, "admin_type", "unknown_field"));
        r = await Send(HttpMethod.Post, "/api/orgs/locations" + qs, PA, J(Create(P_B, injected, DbCfg())), edit: Craft);
        Ok("K4 crafted: PA create under partner B with crafted headers -> 404 + forbidden_scope", Err(r, 404, "not_found") && Denied(await NewEvents(), "forbidden_scope", P_B, P_B, P_A, "partner", "create",
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
        var errLog = new CaptureLogger();
        await using var app2 = await AdminApiHost.BuildAsync(Opts(schema: rejectAll) with { Urls = $"http://127.0.0.1:{port2}" }, dir, extraLogger: errLog);
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
            if (res.Headers.Contains("X-Correlation-Id")) oldHeader++;
            if ((int)res.StatusCode == 500) corr500.Add(res.Headers.TryGetValues(LoginConfigApi.RequestIdHeader, out var cv) ? string.Join(",", cv) : null);
            return ((int)res.StatusCode, t);
        }
        var before = (await Count("location"), await Count("location_login_config_change"), Rv(A1));
        var (st, bd) = await S2(HttpMethod.Post, "/api/orgs/locations", SA, J(Create(P_A, "Doomed", DbCfg())));
        Ok("fail closed: create with outbox failure -> 500, no body", st == 500 && bd.Length == 0);
        (st, bd) = await S2(HttpMethod.Put, Cfg(A1), PA, J(EntraCfg(T3, display: "Doomed")), $"\"{Rv(A1)}\"");
        Ok("fail closed: update with outbox failure -> 500, no body", st == 500 && bd.Length == 0);
        (st, _) = await S2(HttpMethod.Put, Cfg(B1), PA, J(DbCfg()), "\"1\"");
        Ok("fail closed: denied-event write failure -> 500 (OQ)", st == 500);
        (st, bd) = await S2(HttpMethod.Post, "/api/orgs/locations", NO, J(Create(P_A, "Doomed", DbCfg())));
        Ok("fail closed: refused-create event write failure -> 500, no body (never dropped silently, TLD 9:04 #4)", st == 500 && bd.Length == 0);
        Ok("fail closed #3: every 500 has no body and an X-Request-ID (UUIDv7), all distinct", corr500.Count == 4 && corr500.All(IsCorr) && corr500.Distinct().Count() == 4,
            string.Join(",", corr500));
        // TLD 9:20: the error log line carries the same value as the 500's X-Request-ID (and no other id name).
        var errLines = errLog.Lines.Where(l => l.StartsWith("admin-api request failed:")).ToList();
        Ok("fail closed 9:20: one error log line per 500, each 'request_id=<the 500's X-Request-ID>'", errLines.Count == 4
            && corr500.All(id => errLines.Count(l => l.EndsWith(" request_id=" + id)) == 1), string.Join(" | ", errLines));
        Ok("fail closed 9:20: no error log line says correlation / X-Correlation-Id", errLog.Lines.All(l => !l.Contains("orrelation", StringComparison.Ordinal)));
        Ok("fail closed: nothing saved (locations, change rows, row_version, outbox)", (await Count("location"), await Count("location_login_config_change"), Rv(A1)) == before && (await NewEvents()).Count == 0);
        await app2.StopAsync();
        File.Delete(rejectAll);
    }

    // ================= 7b. X-Request-ID (TLD 9:04 #3; renamed TLD 9:20, as v1 errors.md L67) =================
    {
        Ok("X-Request-ID: header name is exactly X-Request-ID", LoginConfigApi.RequestIdHeader == "X-Request-ID");
        var r = await Send(HttpMethod.Get, Cfg(A1), SA, edit: q => q.Headers.TryAddWithoutValidation(LoginConfigApi.RequestIdHeader, "client-chosen"));
        Ok("X-Request-ID: server-generated, a client-sent X-Request-ID is not echoed", r.Status == 200 && IsCorr(r.Corr) && r.Corr != "client-chosen", r.Corr);
        var clientV7 = Guid.CreateVersion7().ToString("D");
        r = await Send(HttpMethod.Post, "/api/orgs/locations", NO, J(Create(P_A, "ReqId refused", DbCfg())),
            edit: q => { q.Headers.TryAddWithoutValidation(LoginConfigApi.RequestIdHeader, clientV7); q.Headers.TryAddWithoutValidation("X-Correlation-Id", clientV7); });
        var ev = await NewEvents();
        Ok("X-Request-ID: a client-sent well-formed UUIDv7 is ignored (not in the header, not in the event)", r.Status == 403 && IsCorr(r.Corr) && r.Corr != clientV7
            && ev.Count == 1 && (string?)ev[0]["correlation_id"] != clientV7, r.Corr);
        Ok("X-Request-ID = correlation_id of the stored event: refused create (not_admin, location_id null)", ev.Count == 1 && (string?)ev[0]["details"]!["reason"] == "not_admin"
            && ev[0]["details"]!["location_id"] is null && (string?)ev[0]["correlation_id"] == r.Corr, $"{r.Corr} {(ev.Count > 0 ? ev[0]["correlation_id"] : null)}");
        r = await Put(B1, PA, DbCfg(), "\"1\"");
        ev = await NewEvents();
        Ok("X-Request-ID = correlation_id of the stored event: denial (forbidden_scope, 404)", r.Status == 404 && ev.Count == 1 && (string?)ev[0]["details"]!["reason"] == "forbidden_scope"
            && (string?)ev[0]["correlation_id"] == r.Corr, $"{r.Corr} {(ev.Count > 0 ? ev[0]["correlation_id"] : null)}");
        r = await Post(SA, Create(P_A, "ReqId Cage", DbCfg()));
        ev = await NewEvents();
        Ok("X-Request-ID = correlation_id of the stored event: success (create, auth.provider.changed)", r.Status == 201 && ev.Count == 1 && (string?)ev[0]["event_type"] == "auth.provider.changed"
            && (string?)ev[0]["correlation_id"] == r.Corr, $"{r.Corr} {(ev.Count > 0 ? ev[0]["correlation_id"] : null)}");
        Ok("X-Request-ID: no response (incl. the 500s) carried an X-Correlation-Id header or mentions it in a body", oldHeader == 0 && !allBodies.Any(b => b.Contains("X-Correlation-Id")), $"{oldHeader}");
        Ok("X-Request-ID: every API response so far carried a distinct UUIDv7 X-Request-ID (200/201/4xx/401)", allCorr.Count > 100 && allCorr.All(IsCorr) && allCorr.Distinct().Count() == allCorr.Count, $"{allCorr.Count}");
        var corrSet = allCorr.ToHashSet();
        Ok("X-Request-ID: every stored event so far has a UUIDv7 correlation_id equal to the X-Request-ID of the response that wrote it", allEvents.Count > 10
            && allEvents.All(e => IsCorr((string?)e["correlation_id"]) && corrSet.Contains((string?)e["correlation_id"])), $"{allEvents.Count}");
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

    // ================= 7d. oversize -> 413 + Connection: close, no reset, nothing written (TLD 9:28 P3-2); X-Request-ID middleware (P3-4) =================
    {
        // Raw HTTP/1.1 client: the body is written by a separate task (errors ignored) while the response is read, so a reset
        // before the status line/headers/body arrive is visible as an exception on the read side.
        async Task<(int Status, Dictionary<string, string> H, string Body, string? ReadErr, long Sent, long ClosedMs)> Raw(string method, string path, string? token,
            long size, bool chunked, string? ifMatch = null, int trickleMs = 0)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var st = tcp.GetStream();
            var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\n");
            if (token is not null) head.Append($"Authorization: Bearer {token}\r\n");
            if (ifMatch is not null) head.Append($"If-Match: {ifMatch}\r\n");
            head.Append(chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {size}\r\n").Append("\r\n");
            await st.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            long sent = 0;
            var writer = Task.Run(async () =>
            {
                var piece = Encoding.ASCII.GetBytes("{\"name\":\"" + new string('a', 16 * 1024 - 9));
                var fill = Encoding.ASCII.GetBytes(new string('a', 16 * 1024));
                try
                {
                    for (var first = true; sent < size; first = false)
                    {
                        var p = first ? piece : fill;
                        var n = (int)Math.Min(trickleMs > 0 && sent >= 80 * 1024 ? 1024 : p.Length, size - sent);
                        if (trickleMs > 0 && sent >= 80 * 1024) await Task.Delay(trickleMs);
                        if (chunked) await st.WriteAsync(Encoding.ASCII.GetBytes($"{n:x}\r\n"));
                        await st.WriteAsync(p.AsMemory(0, n));
                        if (chunked) await st.WriteAsync("\r\n"u8.ToArray());
                        sent += n;
                    }
                    if (chunked) await st.WriteAsync("0\r\n\r\n"u8.ToArray());
                }
                catch (Exception x) when (x is IOException or SocketException or ObjectDisposedException) { }
            });
            var acc = new List<byte>();
            var buf = new byte[8192];
            string? err = null;
            int status = 0; var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var body = "";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                int hdrEnd = -1;
                while (hdrEnd < 0)
                {
                    var n = await st.ReadAsync(buf, cts.Token);
                    if (n == 0) throw new IOException("EOF before headers");
                    acc.AddRange(buf.AsSpan(0, n).ToArray());
                    hdrEnd = Encoding.ASCII.GetString(acc.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                }
                var lines = Encoding.ASCII.GetString(acc.ToArray(), 0, hdrEnd).Split("\r\n");
                status = int.Parse(lines[0].Split(' ')[1]);
                foreach (var l in lines.Skip(1)) { var i = l.IndexOf(':'); h[l[..i].Trim()] = l[(i + 1)..].Trim(); }
                var want = h.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
                while (acc.Count - (hdrEnd + 4) < want)
                {
                    var n = await st.ReadAsync(buf, cts.Token);
                    if (n == 0) throw new IOException("EOF before body end");
                    acc.AddRange(buf.AsSpan(0, n).ToArray());
                }
                body = Encoding.UTF8.GetString(acc.ToArray(), hdrEnd + 4, want);
            }
            catch (Exception x) when (x is IOException or SocketException or OperationCanceledException) { err = x.InnerException?.Message ?? x.Message; }
            // After the response: how long until the server closes (EOF or reset) = the bounded drain.
            var sw = Stopwatch.StartNew();
            try { using var c2 = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (err is null && await st.ReadAsync(buf, c2.Token) > 0) { } }
            catch (Exception x) when (x is IOException or SocketException or OperationCanceledException) { }
            var closedMs = sw.ElapsedMilliseconds;
            tcp.Close();
            await Task.WhenAny(writer, Task.Delay(5000));
            if (status >= 400 && body.Length > 0) responsesForSchema.Add(("error_response", body));
            if (h.TryGetValue(LoginConfigApi.RequestIdHeader, out var rid)) allCorr.Add(rid);
            return (status, h, body, err, sent, closedMs);
        }
        bool Is413(string n, (int Status, Dictionary<string, string> H, string Body, string? ReadErr, long Sent, long ClosedMs) x)
        {
            var ok = x.ReadErr is null && x.Status == 413 && x.Body == "{\"error\":\"payload_too_large\"}" && x.H.TryGetValue("Connection", out var c) && c == "close"
                && IsCorr(x.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader));
            Console.WriteLine($"INFO 413 {n}: status {x.Status} err={x.ReadErr ?? "none"} client wrote {x.Sent} bytes, server closed {x.ClosedMs} ms after the response");
            return ok;
        }
        var locs = await Count("location"); var changes = await Count("location_login_config_change"); var rvA1 = Rv(A1);
        const long KiB400 = 400 * 1024, MiB8 = 8L << 20;
        Ok("413: SA PUT declared Content-Length 400 KiB (current If-Match) -> 413 payload_too_large, Connection: close, X-Request-ID, no reset",
            Is413("put declared 400K", await Raw("PUT", Cfg(A1), SA, KiB400, false, $"\"{rvA1}\"")));
        Ok("413: PA PUT own location declared Content-Length 8 MiB -> 413, Connection: close, X-Request-ID, no reset",
            Is413("put declared 8M", await Raw("PUT", Cfg(A1), PA, MiB8, false, $"\"{rvA1}\"")));
        Ok("413: SA PUT chunked 8 MiB (no Content-Length) -> 413, Connection: close, X-Request-ID, no reset",
            Is413("put chunked 8M", await Raw("PUT", Cfg(A1), SA, MiB8, true, $"\"{rvA1}\"")));
        Ok("413: SA POST declared Content-Length 8 MiB -> 413, Connection: close, X-Request-ID, no reset",
            Is413("post declared 8M", await Raw("POST", "/api/orgs/locations", SA, MiB8, false)));
        Ok("413: PA POST chunked 8 MiB -> 413, Connection: close, X-Request-ID, no reset",
            Is413("post chunked 8M", await Raw("POST", "/api/orgs/locations", PA, MiB8, true)));
        Ok("413: SA POST chunked 200 KiB -> 413, Connection: close, X-Request-ID, no reset",
            Is413("post chunked 200K", await Raw("POST", "/api/orgs/locations", SA, 200 * 1024, true)));
        var slow = await Raw("POST", "/api/orgs/locations", SA, MiB8, false, trickleMs: 50);
        Ok("413: bounded read: a client trickling the rest of a declared 8 MiB body still gets the 413 and the server closes within 4 s (drain <= 2 s)",
            Is413("post declared 8M trickle", slow) && slow.ClosedMs < 4000 && slow.Sent < 1 << 20, $"{slow.ClosedMs} ms, {slow.Sent} bytes");
        var hc = await Send(HttpMethod.Post, "/api/orgs/locations", SA, edit: q =>
        {
            q.Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(J(Create(P_A, new string('a', 300_000), DbCfg())))));
            q.Content.Headers.ContentType = new("application/json");
            q.Headers.TransferEncodingChunked = true;
        });
        Ok("413: HttpClient chunked 300 KB POST -> 413 payload_too_large with X-Request-ID (no HttpRequestException)", Err(hc, 413, "payload_too_large") && IsCorr(hc.Corr), $"{hc.Status}");
        Ok("413: nothing written by the oversize requests (0 outbox events, location count, change rows and A1 row_version unchanged)",
            (await NewEvents()).Count == 0 && await Count("location") == locs && await Count("location_login_config_change") == changes && Rv(A1) == rvA1);
        // TLD 9:04 #8 still first: the same oversize bodies from an unauthenticated / unpermitted caller -> 401 / 403, never 413.
        var u = await Raw("POST", "/api/orgs/locations", null, MiB8, true);
        Ok("413 vs #8: unauthenticated chunked 8 MiB -> 401 (never 413), Connection: close, X-Request-ID, no reset", u.ReadErr is null && u.Status == 401
            && u.H.GetValueOrDefault("Connection") == "close" && IsCorr(u.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)) && (await NewEvents()).Count == 0, $"{u.Status} {u.ReadErr}");
        var f = await Raw("PUT", Cfg(A1), CU, MiB8, false, $"\"{rvA1}\"");
        Ok("413 vs #8: unpermitted PUT declared 8 MiB -> 403 forbidden (never 413) + not_admin event (attempted none), no reset", f.ReadErr is null && f.Status == 403
            && f.Body == "{\"error\":\"forbidden\"}" && Denied(await NewEvents(), "not_admin", A1, P_A, null, null, "update"), $"{f.Status} {f.ReadErr}");
        f = await Raw("POST", "/api/orgs/locations", NO, MiB8, true);
        Ok("413 vs #8: unpermitted POST chunked 8 MiB -> 403 forbidden (never 413) + not_admin event (partner unknown), no reset", f.ReadErr is null && f.Status == 403
            && Denied(await NewEvents(), "not_admin", null, null, null, null, "create"), $"{f.Status} {f.ReadErr}");
        // P3-3 unchanged: steps 5-6 still answer before the size (stale If-Match + oversize -> 409).
        var st409 = await Raw("PUT", Cfg(A1), SA, KiB400, false, "\"99\"");
        Ok("P3-3 unchanged: stale If-Match + declared 400 KiB -> 409 concurrency_conflict, Connection: close, no reset", st409.ReadErr is null && st409.Status == 409
            && st409.H.GetValueOrDefault("Connection") == "close" && (await NewEvents()).Count == 0, $"{st409.Status} {st409.ReadErr}");
        // [TLD 2026-11-04 11:00] spec §5 order: on PUT the 413 is step 7, after 404/428/409 (P3-3, TLD 9:28, stands); on create 413 comes first.
        var o404 = await Raw("PUT", Cfg(Guid.CreateVersion7()), SA, KiB400, false, $"\"{rvA1}\"");
        Ok("TLD 11:00 §5 order: PUT missing location + declared 400 KiB -> 404 not_found (404 before 413), Connection: close, no reset", o404.ReadErr is null && o404.Status == 404
            && o404.Body == "{\"error\":\"not_found\"}" && o404.H.GetValueOrDefault("Connection") == "close" && (await NewEvents()).Count == 0, $"{o404.Status} {o404.ReadErr}");
        var o428 = await Raw("PUT", Cfg(A1), SA, KiB400, false);
        Ok("TLD 11:00 §5 order: PUT without If-Match + declared 400 KiB -> 428 precondition_required (428 before 413), Connection: close, no reset", o428.ReadErr is null && o428.Status == 428
            && o428.Body == "{\"error\":\"precondition_required\"}" && o428.H.GetValueOrDefault("Connection") == "close" && (await NewEvents()).Count == 0, $"{o428.Status} {o428.ReadErr}");
        var oc = await Raw("POST", "/api/orgs/locations", PA, KiB400, false);
        Ok("TLD 11:00 §5 order: create, PA, declared 400 KiB (partner unreadable) -> 413 first (never 404), no forbidden_scope event", Is413("post PA declared 400K", oc) && (await NewEvents()).Count == 0);
        var keep = await Send(HttpMethod.Get, Cfg(A1), SA);
        Ok("413: a normal request after the 413s still works (200, ETag unchanged)", keep.Status == 200 && keep.ETag == $"\"{rvA1}\"");

        // P3-4: X-Request-ID from middleware, so responses outside the handlers carry it too.
        foreach (var m in new[] { HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Post })
        {
            var x = await Send(m, Cfg(A1), SA, edit: q => q.Headers.TryAddWithoutValidation(LoginConfigApi.RequestIdHeader, "client-chosen"));
            Ok($"P3-4: {m} on the login-config path -> 405 with a server UUIDv7 X-Request-ID (client value ignored)", x.Status == 405 && IsCorr(x.Corr) && x.Corr != "client-chosen", $"{x.Status} {x.Corr}");
        }
        var clientV7b = Guid.CreateVersion7().ToString("D");
        foreach (var path in new[] { "/nope", "/api/orgs/locations/x/login-config/nope", "/" })
        {
            var x = await Send(HttpMethod.Get, path, null, edit: q => q.Headers.TryAddWithoutValidation(LoginConfigApi.RequestIdHeader, clientV7b));
            Ok($"P3-4: unrouted GET {path} -> 404 with a server UUIDv7 X-Request-ID (client UUIDv7 ignored)", x.Status == 404 && IsCorr(x.Corr) && x.Corr != clientV7b, $"{x.Status} {x.Corr}");
        }
        var ur = await Raw("POST", "/nope", SA, MiB8, true);
        Ok("P3-4: unrouted POST /nope chunked 8 MiB -> 404 with X-Request-ID, Connection: close, no reset", ur.ReadErr is null && ur.Status == 404
            && IsCorr(ur.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)) && ur.H.GetValueOrDefault("Connection") == "close", $"{ur.Status} {ur.ReadErr}");
        Ok("P3-4: every response (incl. 405/404/413/raw) carried a distinct UUIDv7 X-Request-ID", allCorr.All(IsCorr) && allCorr.Distinct().Count() == allCorr.Count, $"{allCorr.Count}");
        Ok("P3-4/P3-2: no events from 405/404 either", (await NewEvents()).Count == 0);
    }

    // ================= 7e. 408 request_timeout (TLD 9:53/10:02 P3-7), drain Warning + Expect: 100-continue (P3-8), no global limit (P3-10) =================
    {
        var port3 = FreePort(port);
        Console.WriteLine($"INFO body-timeout port {port3}");
        var wlog = new CaptureLogger(LogLevel.Warning);
        await using var app3 = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port3}", BodyReadTimeout = TimeSpan.FromSeconds(1) }, dir, extraLogger: wlog);
        await app3.StartAsync();
        var kopt = app3.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>().Value;
        Ok("P3-10: no global Kestrel body limit set (MaxRequestBodySize is the framework default 30000000, not 1 MiB)", kopt.Limits.MaxRequestBodySize == 30_000_000, $"{kopt.Limits.MaxRequestBodySize}");
        Ok("P3-7: BodyReadTimeout default is 10 s (code default, DRAFT)", new AdminApiOptions { ConnectionString = "x", EntraAuthority = "x", EntraClientId = "x", EntraSecretRef = "x", SecretAccountIds = new HashSet<string>(), SecretRegions = new HashSet<string>(), TokenIssuer = "x", SigningKeys = [], AuditSchemaPath = "x" }.BodyReadTimeout == TimeSpan.FromSeconds(10));
        // Raw client: sends the head, then `first` body bytes, then stalls (never sends the rest) until the response is read.
        // expect = send Expect: 100-continue and NO body bytes; any "HTTP/1.1 100" interim line is reported.
        string? stallCloseErr = null; // how the last Stall connection ended after the response: null = clean EOF (FIN), else the error (reset)
        async Task<(int Status, Dictionary<string, string> H, string Body, string? ReadErr, long Ms, bool Saw100, long ClosedMs)> Stall(string method, string path, string? token,
            long contentLength, int first, bool expect = false, string? ifMatch = null)
        {
            using var tcp = new TcpClient();
            stallCloseErr = null;
            await tcp.ConnectAsync(IPAddress.Loopback, port3);
            var st = tcp.GetStream();
            var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1:{port3}\r\nContent-Type: application/json\r\nContent-Length: {contentLength}\r\n");
            if (token is not null) head.Append($"Authorization: Bearer {token}\r\n");
            if (ifMatch is not null) head.Append($"If-Match: {ifMatch}\r\n");
            if (expect) head.Append("Expect: 100-continue\r\n");
            head.Append("\r\n");
            var sw = Stopwatch.StartNew();
            await st.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
            if (!expect && first > 0) await st.WriteAsync(Encoding.ASCII.GetBytes("{\"name\":\"" + new string('a', first - 9)));
            var acc = new List<byte>(); var buf = new byte[8192]; string? err = null; int status = 0; long ms = 0;
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var body = "";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                int hdrEnd = -1;
                while (hdrEnd < 0)
                {
                    var n = await st.ReadAsync(buf, cts.Token);
                    if (n == 0) throw new IOException("EOF before headers");
                    acc.AddRange(buf.AsSpan(0, n).ToArray());
                    hdrEnd = Encoding.ASCII.GetString(acc.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                }
                ms = sw.ElapsedMilliseconds;
                var lines = Encoding.ASCII.GetString(acc.ToArray(), 0, hdrEnd).Split("\r\n");
                status = int.Parse(lines[0].Split(' ')[1]);
                foreach (var l in lines.Skip(1)) { var i = l.IndexOf(':'); h[l[..i].Trim()] = l[(i + 1)..].Trim(); }
                var want = h.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
                while (acc.Count - (hdrEnd + 4) < want)
                {
                    var n = await st.ReadAsync(buf, cts.Token);
                    if (n == 0) throw new IOException("EOF before body end");
                    acc.AddRange(buf.AsSpan(0, n).ToArray());
                }
                body = Encoding.UTF8.GetString(acc.ToArray(), hdrEnd + 4, want);
            }
            catch (Exception x) when (x is IOException or SocketException or OperationCanceledException) { err = x.InnerException?.Message ?? x.Message; }
            var sw2 = Stopwatch.StartNew();
            try { using var c2 = new CancellationTokenSource(TimeSpan.FromSeconds(10)); int n; while (err is null && (n = await st.ReadAsync(buf, c2.Token)) > 0) acc.AddRange(buf.AsSpan(0, n).ToArray()); }
            catch (Exception x) when (x is IOException or SocketException or OperationCanceledException) { stallCloseErr = x.InnerException?.Message ?? x.Message; }
            var closedMs = sw2.ElapsedMilliseconds;
            var saw100 = Encoding.ASCII.GetString(acc.ToArray()).Contains("HTTP/1.1 100", StringComparison.Ordinal);
            if (status >= 400 && body.Length > 0) responsesForSchema.Add(("error_response", body));
            if (h.TryGetValue(LoginConfigApi.RequestIdHeader, out var rid)) allCorr.Add(rid);
            return (status, h, body, err, ms, saw100, closedMs);
        }
        // The warnings logged since `mark`; each must be one line, no exception object, no stack trace text.
        List<CaptureLogger.Entry> WarnSince(int mark) => wlog.Entries.Skip(mark).ToList();
        bool Clean(CaptureLogger.Entry e) => e.Level == LogLevel.Warning && !e.HasException && !e.Text.Contains('\n') && !e.Text.Contains(" at ");
        async Task<List<CaptureLogger.Entry>> Settle(int mark) { await Task.Delay(300); return WarnSince(mark); }
        bool Is408((int Status, Dictionary<string, string> H, string Body, string? ReadErr, long Ms, bool Saw100, long ClosedMs) x) =>
            x.ReadErr is null && x.Status == 408 && x.Body == "{\"error\":\"request_timeout\"}" && x.H.GetValueOrDefault("Connection") == "close"
            && IsCorr(x.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)) && x.Ms >= 900 && !x.Saw100;
        var locs = await Count("location"); var changes = await Count("location_login_config_change"); var rvA1 = Rv(A1);
        _ = await NewEvents();

        var mark = wlog.Entries.Count;
        var s = await Stall("PUT", Cfg(A1), SA, 4000, 100, ifMatch: $"\"{rvA1}\"");
        var w = await Settle(mark);
        Ok("P3-7: SA PUT, body under the limit sent slowly (100 of 4000 bytes, then stalls) -> 408 request_timeout after the deadline, Connection: close, X-Request-ID, no reset",
            Is408(s), $"{s.Status} {s.Body} {s.ReadErr} {s.Ms} ms conn={s.H.GetValueOrDefault("Connection")}");
        Ok("P3-7: the PUT 408 logs exactly ONE Warning line (request_id = the X-Request-ID, no exception, no stack trace); the drain does not log again",
            w.Count == 1 && Clean(w[0]) && w[0].Text.Contains("request_id=" + s.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)) && w[0].Text.Contains("408 request_timeout"),
            string.Join(" | ", w.Select(e => $"{e.Level} {e.Text}")));
        Ok("P3-7: PUT 408 wrote nothing (no event, no change row, A1 row_version unchanged)",
            (await NewEvents()).Count == 0 && await Count("location_login_config_change") == changes && Rv(A1) == rvA1);

        mark = wlog.Entries.Count;
        s = await Stall("POST", "/api/orgs/locations", PA, 6000, 2000);
        w = await Settle(mark);
        Ok("P3-7: PA POST, body under the limit sent slowly -> 408 request_timeout, Connection: close, X-Request-ID, no reset", Is408(s), $"{s.Status} {s.Body} {s.ReadErr} {s.Ms} ms");
        Ok("P3-7: the POST 408 logs exactly ONE Warning line, no exception, no stack trace", w.Count == 1 && Clean(w[0]) && w[0].Text.Contains("408 request_timeout"),
            string.Join(" | ", w.Select(e => $"{e.Level} {e.Text}")));
        Ok("P3-7: POST 408 wrote nothing (no location, no event, no change row)",
            (await NewEvents()).Count == 0 && await Count("location") == locs && await Count("location_login_config_change") == changes);

        // [TLD 2026-11-04 11:00] 408 comes first: a slow PUT is answered 408 before steps 3-6 (404 / 428 / 409); a slow create before 404.
        foreach (var (n, path, im, also) in new[] { ("missing location", Cfg(Guid.CreateVersion7()), (string?)$"\"{rvA1}\"", "404"), ("no If-Match", Cfg(A1), null, "428"),
                                                    ("stale If-Match", Cfg(A1), "\"99\"", "409") })
        {
            mark = wlog.Entries.Count;
            s = await Stall("PUT", path, SA, 4000, 100, ifMatch: im);
            w = await Settle(mark);
            Ok($"TLD 11:00 §5 order: SA slow PUT, {n} -> 408 request_timeout (408 before {also})", Is408(s) && w.Count == 1 && (await NewEvents()).Count == 0, $"{s.Status} {s.Body} {s.Ms} ms");
        }
        mark = wlog.Entries.Count;
        s = await Stall("POST", "/api/orgs/locations", PA, 6000, 9);
        w = await Settle(mark);
        Ok("TLD 11:00 §5 order: PA slow create (partner not yet readable) -> 408 (408 before 404), no event", Is408(s) && w.Count == 1 && (await NewEvents()).Count == 0, $"{s.Status} {s.Body} {s.Ms} ms");

        // 401/403 still come first with a slow body.
        mark = wlog.Entries.Count;
        s = await Stall("PUT", Cfg(A1), null, 4000, 100, ifMatch: $"\"{rvA1}\"");
        Ok("P3-7 vs #8: unauthenticated slow PUT -> 401 at once (never 408), Connection: close, X-Request-ID, nothing written",
            s.ReadErr is null && s.Status == 401 && s.Ms < 900 && s.H.GetValueOrDefault("Connection") == "close" && IsCorr(s.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader))
            && (await NewEvents()).Count == 0, $"{s.Status} {s.Ms} ms");
        s = await Stall("PUT", Cfg(A1), CU, 4000, 100, ifMatch: $"\"{rvA1}\"");
        Ok("P3-7 vs #8: unpermitted slow PUT -> 403 forbidden (never 408) + not_admin event (attempted none)",
            s.ReadErr is null && s.Status == 403 && s.Body == "{\"error\":\"forbidden\"}" && Denied(await NewEvents(), "not_admin", A1, P_A, null, null, "update"), $"{s.Status} {s.Body}");
        s = await Stall("POST", "/api/orgs/locations", NO, 6000, 2000);
        Ok("P3-7 vs #8: unpermitted slow POST -> 403 forbidden (never 408) + not_admin event (partner unknown)",
            s.ReadErr is null && s.Status == 403 && Denied(await NewEvents(), "not_admin", null, null, null, null, "create"), $"{s.Status} {s.Body}");
        w = await Settle(mark);
        Ok("P3-7 vs #8: the 401/403 slow-body requests log no 408 Warning", w.All(e => !e.Text.Contains("408")), string.Join(" | ", w.Select(e => e.Text)));

        // P3-8: Expect: 100-continue + Content-Length over 64 KiB -> 413 at once, no 100 Continue, no drain, no log.
        // [TLD 2026-11-04 10:42] the server closes the connection right after the 413 is sent: closed < 1.5 s after the response (Kestrel's own
        // close was ~6 s). ctx.Abort() right after CompleteAsync lost the 413 (reset) on 4+ of 42 requests (POSTs; mutation MC2), so the host
        // sets THIS request's MaxRequestBodySize to 0 and Kestrel closes after the response [TLD 2026-11-04 11:00: approved]; the 200 repeats
        // below guard that no 413 is lost, and the checks after them that the limit applies to that one request only.
        foreach (var (m, p, im) in new[] { ("PUT", Cfg(A1), (string?)$"\"{rvA1}\""), ("POST", "/api/orgs/locations", null) })
        {
            mark = wlog.Entries.Count;
            var e = await Stall(m, p, SA, 8L << 20, 0, expect: true, ifMatch: im);
            w = await Settle(mark);
            Ok($"P3-8: SA {m} Expect: 100-continue + Content-Length 8 MiB -> 413 payload_too_large at once, NO 100 Continue, Connection: close, X-Request-ID",
                e.ReadErr is null && e.Status == 413 && e.Body == "{\"error\":\"payload_too_large\"}" && !e.Saw100 && e.Ms < 900
                && e.H.GetValueOrDefault("Connection") == "close" && IsCorr(e.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)),
                $"{e.Status} saw100={e.Saw100} {e.Ms} ms closed {e.ClosedMs} ms");
            Console.WriteLine($"INFO P3-8 Expect {m}: 413 after {e.Ms} ms, server closed {e.ClosedMs} ms after the response (TLD 10:42 close after the 413; bound < 1500 ms)");
            Ok($"TLD 10:42: the {m} Expect 413 connection is closed by the server < 1.5 s after the response (closed right after the 413 is sent)", e.ClosedMs < 1500, $"closed {e.ClosedMs} ms");
            Ok($"P3-8: the {m} Expect 413 logs nothing (no drain) and writes nothing", w.Count == 0 && (await NewEvents()).Count == 0, string.Join(" | ", w.Select(x => x.Text)));
        }
        // TLD 10:42 / 11:00: the close must never lose the 413: 100 more of each (200), every one a full 413, closed < 1.5 s, by a clean close (no reset).
        {
            mark = wlog.Entries.Count;
            var reps = new List<(int Status, Dictionary<string, string> H, string Body, string? ReadErr, long Ms, bool Saw100, long ClosedMs)>();
            var resets = new List<string>();
            for (var i = 0; i < 100; i++)
            {
                reps.Add(await Stall("PUT", Cfg(A1), SA, 8L << 20, 0, expect: true, ifMatch: $"\"{rvA1}\""));
                if (stallCloseErr is not null) resets.Add("PUT " + stallCloseErr);
                reps.Add(await Stall("POST", "/api/orgs/locations", SA, 8L << 20, 0, expect: true));
                if (stallCloseErr is not null) resets.Add("POST " + stallCloseErr);
            }
            var bad = reps.Where(x => !(x.ReadErr is null && x.Status == 413 && x.Body == "{\"error\":\"payload_too_large\"}" && !x.Saw100 && x.ClosedMs < 1500)).ToList();
            Console.WriteLine($"INFO TLD 10:42/11:00 Expect 413 x{reps.Count} (resets {resets.Count}, lost {bad.Count}): close after response max {reps.Max(x => x.ClosedMs)} ms, median {reps.Select(x => x.ClosedMs).Order().ElementAt(reps.Count / 2)} ms");
            Ok($"TLD 11:00: {reps.Count} repeated Expect 413s (100 PUT + 100 POST): every response complete (no reset, no lost 413, no 100) and each closed < 1.5 s",
                reps.Count == 200 && bad.Count == 0, string.Join(" | ", bad.Take(3).Select(x => $"{x.Status} err={x.ReadErr} closed {x.ClosedMs}")));
            Ok($"TLD 11:00: every one of the {reps.Count} Expect 413 connections ends with a clean close (EOF / FIN), never a reset", resets.Count == 0, string.Join(" | ", resets.Take(3)));
            w = await Settle(mark);
            Ok("TLD 10:42: the repeated Expect 413s log nothing and write nothing", w.Count == 0 && (await NewEvents()).Count == 0, string.Join(" | ", w.Select(x => x.Text)));
        }
        // [TLD 2026-11-04 11:00] MaxRequestBodySize = 0 is for THAT request only: later requests on the same server, each on a fresh
        // connection, still get the normal 64 KiB limit (just under: read and evaluated, 422 here; just over: 413 as before).
        {
            async Task<(int Status, string Body)> Fresh(HttpMethod m, string path, int bytes, string? im = null)
            {
                using var hc3 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port3}") }; // new client = new connection
                using var q = new HttpRequestMessage(m, path);
                q.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SA);
                if (im is not null) q.Headers.TryAddWithoutValidation("If-Match", im);
                var json = "{\"zz_pad\":\"" + new string('a', bytes - 12) + "\"}";
                q.Content = new ByteArrayContent(Encoding.ASCII.GetBytes(json));
                q.Content.Headers.ContentType = new("application/json");
                using var r = await hc3.SendAsync(q);
                var b = await r.Content.ReadAsStringAsync();
                if ((int)r.StatusCode >= 400 && b.Length > 0) responsesForSchema.Add(("error_response", b));
                return ((int)r.StatusCode, b);
            }
            const int Under = 64 * 1024 - 64, Over = 64 * 1024 + 64;
            var pu = await Fresh(HttpMethod.Put, Cfg(A1), Under, $"\"{rvA1}\"");
            var po = await Fresh(HttpMethod.Post, "/api/orgs/locations", Under);
            Console.WriteLine($"INFO TLD 11:00 limit scope: after the Expect 413s, fresh-connection PUT {Under} B -> {pu.Status}, POST {Under} B -> {po.Status}");
            Ok($"TLD 11:00: after the Expect 413s, a fresh-connection normal PUT of {Under} bytes (under 64 KiB) is read and evaluated (422 validation_failed zz_pad unknown_field, never 413)",
                pu.Status == 422 && pu.Body.Contains("{\"field\":\"zz_pad\",\"code\":\"unknown_field\"}"), $"{pu.Status} {pu.Body[..Math.Min(120, pu.Body.Length)]}");
            Ok($"TLD 11:00: after the Expect 413s, a fresh-connection normal POST of {Under} bytes (under 64 KiB) is read and evaluated (422 validation_failed zz_pad unknown_field, never 413)",
                po.Status == 422 && po.Body.Contains("{\"field\":\"zz_pad\",\"code\":\"unknown_field\"}"), $"{po.Status} {po.Body[..Math.Min(120, po.Body.Length)]}");
            var pu2 = await Fresh(HttpMethod.Put, Cfg(A1), Over, $"\"{rvA1}\"");
            var po2 = await Fresh(HttpMethod.Post, "/api/orgs/locations", Over);
            Ok($"TLD 11:00: the 64 KiB limit is unchanged too: fresh-connection PUT / POST of {Over} bytes -> 413 payload_too_large",
                pu2.Status == 413 && po2.Status == 413 && pu2.Body == "{\"error\":\"payload_too_large\"}" && po2.Body == pu2.Body, $"{pu2.Status} {po2.Status}");
            Ok("TLD 11:00: the limit-scope requests wrote nothing", (await NewEvents()).Count == 0 && Rv(A1) == rvA1);
        }
        Ok("P3-8: Expect 413s wrote nothing (location count, change rows, A1 row_version unchanged)",
            await Count("location") == locs && await Count("location_login_config_change") == changes && Rv(A1) == rvA1);

        // P3-8: a drain that hits its 2 s bound logs once at Warning, no stack trace. A 401 leaves a declared 8 MiB body unread; the
        // client then stalls (sends nothing), so the drain waits its full 2 s.
        mark = wlog.Entries.Count;
        s = await Stall("POST", "/api/orgs/locations", null, 8L << 20, 1000);
        w = await Settle(mark);
        Ok("P3-8: drain timeout (401, declared 8 MiB, client stalls) -> server closes after the 2 s drain bound",
            s.ReadErr is null && s.Status == 401 && s.ClosedMs is >= 1500 and < 4000, $"{s.Status} closed {s.ClosedMs} ms");
        Ok("P3-8: the drain timeout logs exactly ONE Warning line (request_id, no exception, no stack trace)",
            w.Count == 1 && Clean(w[0]) && w[0].Text.Contains("drain timed out") && w[0].Text.Contains("request_id=" + s.H.GetValueOrDefault(LoginConfigApi.RequestIdHeader)),
            string.Join(" | ", w.Select(e => $"{e.Level} {e.Text}")));
        var keep3 = await new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port3}") }.SendAsync(new HttpRequestMessage(HttpMethod.Get, Cfg(A1)) { Headers = { { "Authorization", "Bearer " + SA } } });
        Ok("P3-7/P3-8: a normal request after the 408s/413s still works (200)", (int)keep3.StatusCode == 200);
        Ok("P3-7/P3-8: no Error-level log line from any of these requests", wlog.Entries.All(e => e.Level < LogLevel.Error), string.Join(" | ", wlog.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => $"{e.ExType}: {e.Text}")));
        await app3.StopAsync();
    }

    // ================= 7f. F-B1 [TLD 2026-11-04 10:58]: the body deadline is a TOTAL deadline, not an idle timeout =================
    {
        // UNDER-limit slow body [TLD 2026-11-04 10:58 F-B1; 11:10 TL/SSE2: the 8 MiB trickle (7d, kept) alone does not cover this].
        // Default host (BodyReadTimeout = code default 10 s). Content-Length 100 (well under 64 KiB, larger than what ever arrives, so the
        // body never finishes); the client sends the head, then ONE body byte about every 2.9 s: each gap is under the 10 s deadline, so an
        // idle-only (reset-per-read) deadline would never trip; the total deadline, started when the body read starts, must answer 408 at
        // ~10 s. Stated bound: 408 headers arrive in [9500, 11500] ms after the head was sent (the body read starts right after the head).
        var port4 = FreePort(port);
        var wlog4 = new CaptureLogger(LogLevel.Warning);
        await using var app4 = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port4}" }, dir, extraLogger: wlog4);
        await app4.StartAsync();
        var rvA1 = Rv(A1); var changes = await Count("location_login_config_change");
        _ = await NewEvents();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port4);
        var st = tcp.GetStream();
        var head = $"PUT {Cfg(A1)} HTTP/1.1\r\nHost: 127.0.0.1:{port4}\r\nContent-Type: application/json\r\nAuthorization: Bearer {SA}\r\nIf-Match: \"{rvA1}\"\r\nContent-Length: 100\r\n\r\n";
        var sw = Stopwatch.StartNew();
        await st.WriteAsync(Encoding.ASCII.GetBytes(head));
        var sent = 0; using var stop = new CancellationTokenSource();
        var trickle = Task.Run(async () =>
        {
            try { while (sent < 100 && !stop.IsCancellationRequested) { await st.WriteAsync("{"u8.ToArray().AsMemory(0, 1)); sent++; await Task.Delay(2900, stop.Token); } }
            catch (Exception x) when (x is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        });
        var acc = new List<byte>(); var buf = new byte[8192]; string? err = null; long ms = -1; int status = 0; var hh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var body = "";
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
        {
            try
            {
                int hdrEnd = -1;
                while (hdrEnd < 0)
                {
                    var n = await st.ReadAsync(buf, cts.Token);
                    if (n == 0) throw new IOException("EOF before headers");
                    acc.AddRange(buf.AsSpan(0, n).ToArray());
                    hdrEnd = Encoding.ASCII.GetString(acc.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
                }
                ms = sw.ElapsedMilliseconds;
                var lines = Encoding.ASCII.GetString(acc.ToArray(), 0, hdrEnd).Split("\r\n");
                status = int.Parse(lines[0].Split(' ')[1]);
                foreach (var l in lines.Skip(1)) { var i = l.IndexOf(':'); hh[l[..i].Trim()] = l[(i + 1)..].Trim(); }
                var want = hh.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
                while (acc.Count - (hdrEnd + 4) < want) { var n = await st.ReadAsync(buf, cts.Token); if (n == 0) throw new IOException("EOF before body end"); acc.AddRange(buf.AsSpan(0, n).ToArray()); }
                body = Encoding.UTF8.GetString(acc.ToArray(), hdrEnd + 4, want);
            }
            catch (Exception x) when (x is IOException or SocketException or OperationCanceledException) { err = x.InnerException?.Message ?? x.Message; ms = sw.ElapsedMilliseconds; }
        }
        stop.Cancel(); var sentAtAnswer = sent;
        await Task.WhenAny(trickle, Task.Delay(5000));
        tcp.Close();
        await Task.Delay(300);
        if (status >= 400 && body.Length > 0) responsesForSchema.Add(("error_response", body));
        if (hh.TryGetValue(LoginConfigApi.RequestIdHeader, out var rid4)) allCorr.Add(rid4);
        var w4 = wlog4.Entries.ToList();
        Console.WriteLine($"INFO F-B1 under-limit trickle (Content-Length 100): 1 byte / 2900 ms, deadline 10000 ms (total): {status} after {ms} ms, {sentAtAnswer} body bytes sent, err={err ?? "none"} (bound [9500, 11500] ms)");
        Ok("F-B1: under-limit slow body (Content-Length 100, never completes; 1 byte every ~2.9 s, each gap under the 10 s deadline) -> 408 request_timeout at the TOTAL deadline, in [9500, 11500] ms, Connection: close, X-Request-ID",
            err is null && status == 408 && body == "{\"error\":\"request_timeout\"}" && hh.GetValueOrDefault("Connection") == "close" && IsCorr(hh.GetValueOrDefault(LoginConfigApi.RequestIdHeader))
            && ms is >= 9500 and <= 11500 && sentAtAnswer is >= 3 and <= 5 && sentAtAnswer < 100, $"{status} {body} err={err} {ms} ms, {sentAtAnswer} bytes");
        Ok("F-B1: the trickle 408 logs exactly ONE Warning and writes nothing (no event, no change row, A1 row_version unchanged)",
            w4.Count == 1 && w4[0].Text.Contains("408 request_timeout") && !w4[0].HasException && (await NewEvents()).Count == 0
            && await Count("location_login_config_change") == changes && Rv(A1) == rvA1, string.Join(" | ", w4.Select(e => e.Text)));
        await app4.StopAsync();
    }

    // ================= 7g. F-ASTRAL + B1 [TLD 2026-11-04 15:03] =================
    // F-ASTRAL: JsonSchemaLite matches "pattern" over code points, so a reason the API accepts (10..500 code points after trim) is
    // never refused by the envelope check (before the fix: 251+ astral characters -> outbox InvalidOperationException -> empty 500).
    // B1: a required reason (DEC-096 fallback on, DEC-098 MFA off; spec §4 step 10) that is absent/null/empty/whitespace-only -> the
    // existing 400 reason_required; where the reason is optional, blank is stored as null (spec §3.2 "Reason", OQ-S2a-7).
    {
        var logMark = mainLog.Entries.Count;
        string Rep(string u, int n) => string.Concat(Enumerable.Repeat(u, n));
        const string Devil = "\U0001F608"; // 1 code point, 2 UTF-16 units, 4 UTF-8 bytes
        static string Hex(string? x) => x is null ? "null" : Convert.ToHexString(Encoding.UTF8.GetBytes(x)).ToLowerInvariant();
        // Direct JsonSchemaLite unit checks (code points, as JSON Schema defines and Python jsonschema does).
        var unit = JsonSchemaLite.Load("""{"type":"object","properties":{"r":{"type":"string","pattern":"^\\s*\\S[\\s\\S]{8,498}\\S\\s*$"},"t":{"type":"string","pattern":"^.{3}$"}}}""");
        JsonObject U(string? r = null, string? t = null) { var o = new JsonObject(); if (r is not null) o["r"] = r; if (t is not null) o["t"] = t; return o; }
        Ok("F-ASTRAL unit: reason pattern accepts 10, 251, 300 and 500 astral code points", new[] { 10, 251, 300, 500 }.All(n => unit.Validate(U(r: Rep(Devil, n))).Count == 0));
        Ok("F-ASTRAL unit: reason pattern refuses 9 and 501 astral code points", new[] { 9, 501 }.All(n => unit.Validate(U(r: Rep(Devil, n))).Count > 0));
        Ok("F-ASTRAL unit: BMP boundaries unchanged (9 no, 10 yes, 500 yes, 501 no)", unit.Validate(U(r: Rep("x", 9))).Count > 0 && unit.Validate(U(r: Rep("x", 10))).Count == 0
            && unit.Validate(U(r: Rep("x", 500))).Count == 0 && unit.Validate(U(r: Rep("x", 501))).Count > 0);
        Ok("F-ASTRAL unit: '.' takes one astral code point as one character ('^.{3}$': 3 astral yes, 2 or 4 no; mixed 'a' + astral + 'b' yes)",
            unit.Validate(U(t: Rep(Devil, 3))).Count == 0 && unit.Validate(U(t: Rep(Devil, 2))).Count > 0 && unit.Validate(U(t: Rep(Devil, 4))).Count > 0 && unit.Validate(U(t: "a" + Devil + "b")).Count == 0);
        bool Refused(string pat) { try { JsonSchemaLite.Load("{\"type\":\"string\",\"pattern\":" + JsonSerializer.Serialize(pat) + "}"); return false; } catch (InvalidOperationException) { return true; } }
        Ok("F-ASTRAL unit: a pattern naming a surrogate or U+FFFF (the stand-in) is refused at load, fail closed", Refused(@"^\uD83D") && Refused(@"[\u0000-\uFFFF]") && Refused("\uFFFF") && Refused(@"\p{Cs}") && !Refused(@"^\S+$"));

        // API: SA updates (display_name change, not a downgrade) on a fresh partner-B location, reasons given, then a create.
        var rC = await Post(SA, Create(P_B, "F-ASTRAL probe", DbCfg()));
        var LF = rC.Status == 201 ? LocOf(rC) : Guid.Empty;
        await NewEvents();
        var shown = new List<string>();
        int k = 0;
        async Task<bool> Accepted(string label, string reason)
        {
            var rv = Rv(LF);
            var r = await Put(LF, SA, With(DbCfg(display: $"F-ASTRAL {k++}"), "reason", reason), $"\"{rv}\"");
            var ev = await NewEvents();
            var want = reason.Trim();
            var hist = await Get(Cfg(LF) + "/changes", SA);
            var top = hist.Status == 200 ? Obj(hist.Body)["items"]!.AsArray()[0] : null;
            var rowHex = await OScalar<string>("SELECT coalesce(encode(convert_to(reason,'UTF8'),'hex'), '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 ORDER BY at DESC, change_id LIMIT 1", LF);
            var obHex = await OScalar<string>("SELECT coalesce(encode(convert_to(envelope->'details'->>'reason','UTF8'),'hex'), '<SQL NULL>') FROM auth.audit_outbox WHERE envelope->'target'->>'id' = $1 ORDER BY occurred_at DESC LIMIT 1", LF.ToString("D"));
            var ok = r.Status == 200 && ev.Count == 1 && Valid(ev[0]) && (string?)ev[0]["details"]!["reason"] == want
                && (string?)top?["reason"] == want && rowHex == Hex(want) && obHex == Hex(want);
            shown.Add($"{label}: {r.Status}");
            Ok($"F-ASTRAL change path (audit-events L374): {label} -> 200; history, change row and outbox details.reason = the sent reason trimmed, byte-for-byte (UTF-8 hex)", ok,
                $"{r.Status} ev={ev.Count} row={rowHex?.Length} ob={obHex?.Length} want={Hex(want).Length}");
            return ok;
        }
        async Task RefusedLen(string label, string reason)
        {
            var rv = Rv(LF); var ch = await Count("location_login_config_change");
            var r = await Put(LF, SA, With(DbCfg(display: $"F-ASTRAL {k++}"), "reason", reason), $"\"{rv}\"");
            shown.Add($"{label}: {r.Status}");
            Ok($"F-ASTRAL change path (audit-events L374): {label} -> 422 validation_failed, exactly reason:reason_length; nothing written", r.Status == 422
                && Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}").SequenceEqual(["reason:reason_length"])
                && (await NewEvents()).Count == 0 && Rv(LF) == rv && await Count("location_login_config_change") == ch, r.Body);
        }
        await Accepted("249 astral + 3 ASCII (252 code points, 501 UTF-16 units)", Rep(Devil, 249) + "abc");
        await Accepted("251 astral", Rep(Devil, 251));
        await Accepted("300 astral", Rep(Devil, 300));
        await Accepted("500 astral (1000 UTF-16 units, 2000 UTF-8 bytes)", Rep(Devil, 500));
        await Accepted("499 BMP + 1 astral (500 code points, 501 UTF-16 units)", Rep("r", 499) + Devil);
        await Accepted("500 astral padded with spaces (trimmed)", "   " + Rep(Devil, 500) + "  ");
        await RefusedLen("501 astral", Rep(Devil, 501));
        await RefusedLen($"{Mn - 1} astral (min - 1)", Rep(Devil, Mn - 1));
        await Accepted($"{Mn} astral (min, DEC-107)", Rep(Devil, Mn));
        await Accepted("BMP 10", Rep("b", 10));
        await Accepted("BMP 500", Rep("b", 500));
        await RefusedLen("BMP 9", Rep("b", 9));
        await RefusedLen("BMP 501", Rep("b", 501));
        {
            var r = await Post(SA, Create(P_B, "F-ASTRAL create", With(DbCfg(), "reason", Rep(Devil, 300))));
            var ev = await NewEvents();
            var lc = r.Status == 201 ? LocOf(r) : Guid.Empty;
            Ok("F-ASTRAL change path (audit-events L374): create with a 300-astral reason -> 201; event and change row reason byte-for-byte", r.Status == 201 && ev.Count == 1 && Valid(ev[0])
                && (string?)ev[0]["details"]!["reason"] == Rep(Devil, 300)
                && await OScalar<string>("SELECT coalesce(encode(convert_to(reason,'UTF8'),'hex'), '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1", lc) == Hex(Rep(Devil, 300)), $"{r.Status}");
            r = await Post(SA, Create(P_B, "F-ASTRAL create 501", With(DbCfg(), "reason", Rep(Devil, 501))));
            Ok("F-ASTRAL change path (audit-events L374): create with a 501-astral reason -> 422, exactly login_config/reason:reason_length", r.Status == 422
                && Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}").SequenceEqual(["login_config/reason:reason_length"]) && (await NewEvents()).Count == 0, r.Body);
        }
        // Downgrade path [TLD 2026-11-04 15:10 (1)]: the envelope's downgrade_rule (contract/audit-events.schema.json L393, read only)
        // carries the same reason pattern; probed on an MFA-off update (true -> false) and an MFA-off create (vs "none", A-5).
        // Unit: Unicode code points, per spec L131 "10–500 characters after trimming" (OQ-S2a-7).
        var rG = await Post(SA, Create(P_B, "F-ASTRAL downgrade probe", DbCfg()));
        var LG = rG.Status == 201 ? LocOf(rG) : Guid.Empty;
        await NewEvents();
        var downCases = new (string label, string reason, bool ok)[]
        {
            ("BMP 10", Rep("d", 10), true), ("BMP 500", Rep("d", 500), true), ("BMP 9", Rep("d", 9), false), ("BMP 501", Rep("d", 501), false),
            ("249 astral + 3 ASCII (252 code points, 501 UTF-16 units)", Rep(Devil, 249) + "abc", true), ("251 astral", Rep(Devil, 251), true),
            ("300 astral", Rep(Devil, 300), true), ("500 astral", Rep(Devil, 500), true), ("499 BMP + 1 astral", Rep("d", 499) + Devil, true),
            ("501 astral", Rep(Devil, 501), false), ("500 code points mixed, padded", "  " + Rep(Devil + "d", 250) + " ", true),
            ($"{Mn - 1} astral", Rep(Devil, Mn - 1), false), ($"{Mn} astral", Rep(Devil, Mn), true), // SSE2 9b3b546d Info 2; DEC-107: min - 1 / min from the constant
        };
        foreach (var (dl, reason, okWant) in downCases)
        {
            var want = reason.Trim();
            // update: MFA back on first (an upgrade, no reason), then MFA off with the reason.
            var up = await Put(LG, SA, DbCfg(), $"\"{Rv(LG)}\"");
            await NewEvents();
            var rv = Rv(LG); var ch = await Count("location_login_config_change");
            var r = await Put(LG, SA, With(DbCfg(mfa: false), "reason", reason), $"\"{rv}\"");
            var ev = await NewEvents();
            shown.Add($"downgrade update {dl}: {r.Status}");
            if (okWant)
            {
                var hist = await Get(Cfg(LG) + "/changes", SA);
                var top = hist.Status == 200 ? Obj(hist.Body)["items"]!.AsArray()[0] : null;
                var rowHex = await OScalar<string>("SELECT coalesce(encode(convert_to(reason,'UTF8'),'hex'), '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 ORDER BY at DESC, change_id LIMIT 1", LG);
                var obHex = await OScalar<string>("SELECT coalesce(encode(convert_to(envelope->'details'->>'reason','UTF8'),'hex'), '<SQL NULL>') FROM auth.audit_outbox WHERE envelope->'target'->>'id' = $1 ORDER BY occurred_at DESC LIMIT 1", LG.ToString("D"));
                Ok($"F-ASTRAL downgrade path (audit-events L393): update MFA off, {dl} -> 200; history, change row and outbox details.reason = sent trimmed, byte-for-byte",
                    up.Status == 200 && r.Status == 200 && ev.Count == 1 && Valid(ev[0]) && (bool?)ev[0]["details"]!["after"]!["mfa_required"] == false
                    && (string?)ev[0]["details"]!["reason"] == want && (string?)top?["reason"] == want && rowHex == Hex(want) && obHex == Hex(want), $"{up.Status}/{r.Status} ev={ev.Count}");
            }
            else
                Ok($"F-ASTRAL downgrade path (audit-events L393): update MFA off, {dl} -> 422 exactly reason:reason_length; nothing written", up.Status == 200 && r.Status == 422
                    && Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}").SequenceEqual(["reason:reason_length"])
                    && ev.Count == 0 && Rv(LG) == rv && await Count("location_login_config_change") == ch, r.Body);
            // create: MFA off on create = a downgrade vs "none" (A-5).
            var locs = await Count("location");
            var rc = await Post(SA, Create(P_B, $"F-ASTRAL downgrade create {k++}", With(DbCfg(mfa: false), "reason", reason)));
            var evc = await NewEvents();
            shown.Add($"downgrade create {dl}: {rc.Status}");
            if (okWant)
                Ok($"F-ASTRAL downgrade path (audit-events L393): create MFA off, {dl} -> 201; event and change row reason byte-for-byte", rc.Status == 201 && evc.Count == 1 && Valid(evc[0])
                    && (string?)evc[0]["details"]!["reason"] == want
                    && await OScalar<string>("SELECT coalesce(encode(convert_to(reason,'UTF8'),'hex'), '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1", LocOf(rc)) == Hex(want), $"{rc.Status}");
            else
                Ok($"F-ASTRAL downgrade path (audit-events L393): create MFA off, {dl} -> 422 exactly login_config/reason:reason_length; nothing written", rc.Status == 422
                    && Obj(rc.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}").SequenceEqual(["login_config/reason:reason_length"])
                    && evc.Count == 0 && await Count("location") == locs, rc.Body);
        }
        Console.WriteLine("INFO F-ASTRAL " + string.Join("; ", shown));

        // B1 probe: every blank form on every required-reason action (SA; a PA downgrade is 403 before the reason rule, step 9).
        var blanks = new (string label, object? v, bool send)[]
        {
            ("absent", null, false), ("null", null, true), ("\"\"", "", true), ("spaces", "            ", true), ("tab/CR/LF/space", "\t\r\n \t\r\n ", true),
            ("NBSP x12", Rep("\u00A0", 12), true), ("U+3000 x12", Rep("\u3000", 12), true), ("U+2028/U+2029/U+0085 mix", "\u2028\u2029\u0085\u2028\u2029\u0085\u2028\u2029\u0085\u2028", true),
        };
        Dictionary<string, object?> WithBlank(Dictionary<string, object?> cfg, (string label, object? v, bool send) b) => b.send ? With(cfg, "reason", b.v) : cfg;
        Guid T4 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa14"), T5 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa15");
        var b1 = new List<string>();
        var rDb = await Post(SA, Create(P_B, "B1 db", DbCfg()));
        var rEn = await Post(SA, Create(P_B, "B1 entra", EntraCfg(T4)));
        Guid LD = rDb.Status == 201 ? LocOf(rDb) : Guid.Empty, LE = rEn.Status == 201 ? LocOf(rEn) : Guid.Empty;
        await NewEvents();
        var actions = new (string label, Func<(string, object?, bool), Task<R>> send)[]
        {
            ("create, MFA off (DEC-098)", b => Post(SA, Create(P_B, "B1 new", WithBlank(DbCfg(mfa: false), b)))),
            ("create, fallback on (DEC-096)", b => Post(SA, Create(P_B, "B1 new", WithBlank(EntraCfg(T5, fallback: true), b)))),
            ("create, MFA off + fallback on", b => Post(SA, Create(P_B, "B1 new", WithBlank(EntraCfg(T5, fallback: true, mfa: false), b)))),
            ("update, MFA off (DEC-098)", b => Put(LD, SA, WithBlank(DbCfg(mfa: false), b), "\"1\"")),
            ("update, fallback on (DEC-096)", b => Put(LE, SA, WithBlank(EntraCfg(T4, fallback: true), b), "\"1\"")),
            ("update, MFA off + fallback on", b => Put(LE, SA, WithBlank(EntraCfg(T4, fallback: true, mfa: false), b), "\"1\"")),
        };
        foreach (var (al, send) in actions)
        {
            var before = (await Count("location"), await Count("location_login_config_change"), Rv(LD), Rv(LE));
            var got = new List<string>();
            foreach (var bl in blanks) { var r = await send(bl); got.Add($"{bl.label}={r.Status}:{(r.Status >= 400 ? Obj(r.Body)["error"] : "")}"); }
            b1.Add($"{al} [{string.Join(", ", got)}]");
            Ok($"B1: {al}: every blank reason (absent, null, \"\", spaces, tab/CR/LF, NBSP, U+3000, U+2028/2029/0085) -> 400 reason_required; no event, nothing written",
                got.All(g => g.EndsWith("=400:reason_required")) && (await NewEvents()).Count == 0
                && (await Count("location"), await Count("location_login_config_change"), Rv(LD), Rv(LE)) == before, string.Join(", ", got));
        }
        // Optional reason (not a downgrade): blank -> accepted, stored as null (spec §3.2 "Reason"; recorded, unchanged).
        {
            var nulls = new List<string>();
            foreach (var bl in blanks)
            {
                var r = await Put(LD, SA, WithBlank(DbCfg(display: "B1 optional " + bl.label), bl), $"\"{Rv(LD)}\"");
                var ev = await NewEvents();
                var row = await OScalar<string>("SELECT coalesce(reason, '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 ORDER BY at DESC, change_id LIMIT 1", LD);
                nulls.Add($"{bl.label}={r.Status}/{(row == "<SQL NULL>" ? "null" : "text")}/{(ev.Count == 1 && ev[0]["details"]!["reason"] is null ? "null" : "?")}");
            }
            b1.Add($"optional (update display_name, no downgrade) [{string.Join(", ", nulls)}]");
            Ok("B1: optional reason (display_name update, no downgrade): every blank form -> 200, change row and event reason null", nulls.All(n => n.EndsWith("=200/null/null")), string.Join(", ", nulls));
            // TLD 15:10 (2): a plain create (MFA on, fallback off) with a missing, empty or whitespace-only reason -> 201, reason null (spec L131).
            var plain = new List<string>();
            foreach (var (pl, cfgP) in new (string, Dictionary<string, object?>)[] { ("absent", DbCfg()), ("\"\"", With(DbCfg(), "reason", "")), ("whitespace-only", With(DbCfg(), "reason", "   \t ")) })
            {
                var r2 = await Post(SA, Create(P_B, "B1 optional create " + pl, cfgP));
                var ev2 = await NewEvents();
                var row2 = r2.Status == 201 ? await OScalar<string>("SELECT coalesce(reason, '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1", LocOf(r2)) : "?";
                plain.Add($"{pl}={r2.Status}/{(row2 == "<SQL NULL>" ? "null" : row2)}/{(ev2.Count == 1 && ev2[0]["details"]!["reason"] is null ? "null" : "?")}");
            }
            b1.Add($"plain create (MFA on, fallback off) [{string.Join(", ", plain)}]");
            Ok("B1: plain create (MFA on, fallback off): missing, \"\" and whitespace-only reason -> 201, change row and event reason null (spec L131)", plain.All(x => x.EndsWith("=201/null/null")), string.Join(", ", plain));
        }
        // Invisible but not whitespace (U+200B, U+FEFF): .NET Trim keeps them, so ten of them are a 10-character reason. Recorded (OQ), not changed.
        {
            var r = await Put(LD, SA, With(DbCfg(mfa: false), "reason", Rep("\u200B", 10)), $"\"{Rv(LD)}\"");
            await NewEvents();
            b1.Add($"MFA off with 10 x U+200B -> {r.Status}");
            Ok("B1 (observed, OQ): MFA off with a reason of 10 x U+200B (not whitespace) is not a 500", r.Status is 200 or 400 or 422, $"{r.Status}");
        }
        // OQ-S2a-14 / OQ-S2a-15 (pending Mohanraj via TL): observed and logged only; the API is not changed.
        {
            var obs = new List<string>();
            var r = await Put(LD, SA, With(DbCfg(display: "\u200BCasino ZW\u200B"), "reason", null), $"\"{Rv(LD)}\"");
            await NewEvents();
            obs.Add($"display_name U+200B padding -> {r.Status}, stored hex {await OScalar<string>("SELECT encode(convert_to(display_name,'UTF8'),'hex') FROM auth.location_login_config WHERE location_id=$1", LD)}");
            r = await Post(SA, Create(P_B, "\u200BB1 zw name\u200B", DbCfg()));
            await NewEvents();
            obs.Add($"name U+200B padding -> {r.Status}" + (r.Status == 201 ? $", stored hex {await OScalar<string>("SELECT encode(convert_to(name,'UTF8'),'hex') FROM auth.location WHERE location_id=$1", LocOf(r))}" : ""));
            r = await Put(LD, SA, With(DbCfg(display: "Casino\tTab"), "reason", null), $"\"{Rv(LD)}\"");
            await NewEvents();
            obs.Add($"display_name interior tab -> {r.Status}");
            r = await Put(LD, SA, With(DbCfg(display: "Casino\nNewline"), "reason", null), $"\"{Rv(LD)}\"");
            await NewEvents();
            obs.Add($"display_name interior newline -> {r.Status}" + (r.Status >= 400 ? $" {Obj(r.Body)["error"]}" : ""));
            r = await Post(SA, Create(P_B, "B1 name\nnewline", DbCfg()));
            await NewEvents();
            obs.Add($"name interior newline -> {r.Status}" + (r.Status >= 400 ? $" {Obj(r.Body)["error"]}" : ""));
            r = await Post(SA, Create(P_B, "B1 name\ttab", DbCfg()));
            await NewEvents();
            obs.Add($"name interior tab -> {r.Status}" + (r.Status >= 400 ? $" {Obj(r.Body)["error"]}" : ""));
            foreach (var o in obs) Console.WriteLine("INFO OQ-S2a-14/15 " + o);
            Ok("OQ-S2a-14/15 observations ran without a 500 (behaviour recorded, not asserted; pending Mohanraj via TL)", obs.All(o => !o.Contains("-> 500")), string.Join("; ", obs));
        }
        foreach (var l in b1) Console.WriteLine("INFO B1 " + l);
        var errs7g = mainLog.Entries.Skip(logMark).ToList();
        Ok("F-ASTRAL/B1: no request in 7g failed (no Error log line, no InvalidOperationException out of the outbox, no 500)",
            errs7g.Count == 0 && !shown.Any(x => x.EndsWith(": 500")), string.Join(" | ", errs7g.Select(e => $"{e.ExType}: {e.Text}")));
    }

    // ================= 7h. F-SURR [SSE2 9b3b546d, TLD 2026-11-04 15:58] =================
    // An unpaired or reversed surrogate JSON escape anywhere in the body (string value or key name) is a malformed body:
    // 400 malformed_body (spec v1.1-S2a §4 step 7 L259 "valid JSON object ... else 400 malformed_body"; error table L283;
    // location-login-config.schema.json enum L510). Before the fix: GetString() threw InvalidOperationException -> empty-body 500.
    {
        var logMark = mainLog.Entries.Count;
        var shownS = new List<string>();
        var forms = new (string label, string raw)[]
        {
            ("lone high", "Surrogate probe \\ud83d here"), ("lone low", "Surrogate probe \\ude00 here"),
            ("reversed pair", "Surrogate probe \\ude00\\ud83d here"), ("trailing high", "Surrogate probe here \\ud83d"),
        };
        const string Ph = "@@F-SURR@@";
        string Splice(object body, string raw) => J(body).Replace("\"" + Ph + "\"", "\"" + raw + "\"");
        async Task<(long loc, long llc, long ch)> Rows() => (await Count("location"), await Count("location_login_config"), await Count("location_login_config_change"));
        var rS = await Post(SA, Create(P_B, "F-SURR probe", DbCfg()));
        var LS = rS.Status == 201 ? LocOf(rS) : Guid.Empty;
        await NewEvents();
        const string GoodReason = "F-SURR downgrade reason, ticket OPS-1";
        // Every string the S2a write endpoints read: create = partner_id, name, login_config/{provider_kind, display_name,
        // entra_allowed_tenant_ids/0, reason}; PUT = provider_kind, display_name, entra_allowed_tenant_ids/0, reason. Plus a key
        // name and a server-filled string (secret_ref) whose value is never read: the whole body is decoded once.
        Dictionary<string, object?> Cfg0(bool down) => down ? With(DbCfg(mfa: false), "reason", GoodReason) : DbCfg();
        var createCases = new List<(string field, Func<bool, object> body, bool keyProbe)>
        {
            ("partner_id", d => new Dictionary<string, object?> { ["partner_id"] = Ph, ["name"] = "F-SURR c", ["login_config"] = Cfg0(d) }, false),
            ("name", d => new Dictionary<string, object?> { ["partner_id"] = P_B.ToString("D"), ["name"] = Ph, ["login_config"] = Cfg0(d) }, false),
            ("login_config/provider_kind", d => Create(P_B, "F-SURR c", With(Cfg0(d), "provider_kind", Ph)), false),
            ("login_config/display_name", d => Create(P_B, "F-SURR c", With(Cfg0(d), "display_name", Ph)), false),
            ("login_config/entra_allowed_tenant_ids/0", d => Create(P_B, "F-SURR c", With(Cfg0(d), "entra_allowed_tenant_ids", new[] { Ph })), false),
            ("login_config/reason", d => Create(P_B, "F-SURR c", With(Cfg0(d), "reason", Ph)), false),
            ("login_config/secret_ref (never read)", d => Create(P_B, "F-SURR c", With(Cfg0(d), "secret_ref", Ph)), false),
            ("a key name", d => Create(P_B, "F-SURR c", With(Cfg0(d), Ph, "x")), true),
        };
        var putCases = new List<(string field, Func<bool, object> body, bool keyProbe)>
        {
            ("provider_kind", d => With(Cfg0(d), "provider_kind", Ph), false),
            ("display_name", d => With(Cfg0(d), "display_name", Ph), false),
            ("entra_allowed_tenant_ids/0", d => With(Cfg0(d), "entra_allowed_tenant_ids", new[] { Ph }), false),
            ("reason", d => With(Cfg0(d), "reason", Ph), false),
            ("secret_ref (never read)", d => With(Cfg0(d), "secret_ref", Ph), false),
            ("a key name", d => With(Cfg0(d), Ph, "x"), true),
        };
        bool Malformed(R r) => Err(r, 400, "malformed_body") && (Obj(r.Body)["fields"] is not JsonArray fa || fa.Count == 0);
        int nCase = 0;
        foreach (var down in new[] { false, true })
            foreach (var (field, body, _) in createCases)
                foreach (var (fl, raw) in forms)
                {
                    var before = await Rows();
                    var r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, Splice(body(down), raw));
                    var ev = await NewEvents(); nCase++;
                    shownS.Add($"create {field} {fl}: {r.Status}");
                    Ok($"F-SURR: {(down ? "downgrade create (MFA off, A-5)" : "create")}, {fl} surrogate in {field} -> 400 malformed_body; 0 rows, no event",
                        Malformed(r) && ev.Count == 0 && await Rows() == before, $"{r.Status} {r.Body}");
                }
        foreach (var down in new[] { false, true })
            foreach (var (field, body, _) in putCases)
                foreach (var (fl, raw) in forms)
                {
                    var rv = Rv(LS); var before = await Rows();
                    var r = await Send(HttpMethod.Put, Cfg(LS), SA, Splice(body(down), raw), $"\"{rv}\"");
                    var ev = await NewEvents(); nCase++;
                    shownS.Add($"put {field} {fl}: {r.Status}");
                    Ok($"F-SURR: {(down ? "downgrade PUT (MFA off)" : "PUT")}, {fl} surrogate in {field} -> 400 malformed_body; 0 rows, row_version unchanged, no event",
                        Malformed(r) && ev.Count == 0 && await Rows() == before && Rv(LS) == rv, $"{r.Status} {r.Body}");
                }
        // Refused callers (step 2, body read only to name the attempted fields): still 403 + one not_admin event, never a 500.
        foreach (var (fl, raw) in forms)
        {
            var r = await Send(HttpMethod.Post, "/api/orgs/locations", RD, Splice(Create(P_B, "F-SURR c", With(DbCfg(), Ph, "x")), raw));
            var ev = await NewEvents();
            shownS.Add($"not_admin create key {fl}: {r.Status}");
            Ok($"F-SURR: not_admin create, {fl} surrogate in a key name -> 403 forbidden, one valid not_admin event (body unreadable: no attempted fields)",
                Err(r, 403, "forbidden") && Denied(ev, "not_admin", null, null, null, null, "create"), $"{r.Status} ev={ev.Count}");
            var rv = Rv(LS);
            r = await Send(HttpMethod.Put, Cfg(LS), RD, Splice(With(DbCfg(), Ph, "x"), raw), $"\"{rv}\"");
            ev = await NewEvents();
            shownS.Add($"not_admin put key {fl}: {r.Status}");
            Ok($"F-SURR: not_admin PUT, {fl} surrogate in a key name -> 403 forbidden, one valid not_admin event; row unchanged",
                Err(r, 403, "forbidden") && Denied(ev, "not_admin", LS, P_B, null, null, "update") && Rv(LS) == rv, $"{r.Status} ev={ev.Count}");
        }
        // Controls: a VALID escaped pair is still text (stored byte-for-byte), so the check refuses only undecodable strings.
        {
            var r = await Send(HttpMethod.Post, "/api/orgs/locations", SA, Splice(Create(P_B, "F-SURR c", With(DbCfg(), "display_name", Ph)), "\\ud83d\\ude08 Pair ok"));
            var ev = await NewEvents();
            Ok("F-SURR control: create with a valid escaped pair in display_name -> 201, stored U+1F608 + text", r.Status == 201 && ev.Count == 1 && Valid(ev[0])
                && await OScalar<string>("SELECT display_name FROM auth.location_login_config WHERE location_id=$1", LocOf(r)) == "\U0001F608 Pair ok", $"{r.Status}");
            var rv = Rv(LS);
            r = await Send(HttpMethod.Put, Cfg(LS), SA, Splice(With(DbCfg(mfa: false), "reason", Ph), "\\ud83d\\ude08 valid pair, ticket OPS-2"), $"\"{rv}\"");
            ev = await NewEvents();
            Ok("F-SURR control: downgrade PUT with a valid escaped pair in reason -> 200, change row reason byte-for-byte", r.Status == 200 && ev.Count == 1 && Valid(ev[0])
                && await OScalar<string>("SELECT coalesce(reason, '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 ORDER BY at DESC, change_id LIMIT 1", LS) == "\U0001F608 valid pair, ticket OPS-2", $"{r.Status}");
        }
        Console.WriteLine($"INFO F-SURR {nCase} write probes: " + string.Join("; ", shownS.GroupBy(x => x[(x.LastIndexOf(':') + 2)..]).Select(g => $"{g.Key} x{g.Count()}")));
        var errs7h = mainLog.Entries.Skip(logMark).ToList();
        Ok("F-SURR: no request in 7h failed (no Error log line, no InvalidOperationException, no 500)",
            errs7h.Count == 0 && !shownS.Any(x => x.EndsWith(": 500")), string.Join(" | ", errs7h.Select(e => $"{e.ExType}: {e.Text}")));
    }

    // ================= 7i. DEC-107 [PM t149u/t150u; TLD 2026-11-04 16:32 Q1=A, Q2=(ii)] =================
    // The reason length comes from LoginConfig:ReasonMinLength / ReasonMaxLength (startup snapshot, inside the DB's 10..500).
    // Boundaries are computed from the constants (this host) and from a second host started with 50/200. The location GET carries
    // the bounds, PUT 200 / POST 201 bodies do not; GET /api/orgs/login-config/limits serves Create. No new error code.
    {
        string Rep(string u, int n) => string.Concat(Enumerable.Repeat(u, n));
        const string Devil = "\U0001F608";
        const string Limits = "/api/orgs/login-config/limits";
        string Fields(R r) => r.Status == 422 ? string.Join(",", Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}")) : "";
        bool NoBounds(R r) => r.Body.Length > 0 && !Obj(r.Body).ContainsKey("reason_min_length") && !Obj(r.Body).ContainsKey("reason_max_length");
        int k = 0;

        // ---- limits endpoint on this host (defaults), permissions as the location GET ----
        await NewEvents();
        foreach (var (who, tok) in new[] { ("SA", SA), ("PA", PA), ("PB", PB), ("RD (org:V reader)", RD) })
        {
            var r = await Get(Limits, tok);
            var o = r.Status == 200 ? Obj(r.Body) : new JsonObject();
            Ok($"DEC-107 limits: {who} -> 200 exactly {{reason_min_length: {Mn}, reason_max_length: {Mx}}}", r.Status == 200 && o.Count == 2
                && (int?)o["reason_min_length"] == Mn && (int?)o["reason_max_length"] == Mx && r.CacheControl == "no-store", r.Body);
            if (r.Status == 200) ForSchema("login_config_limits", r);
        }
        {
            var r = await Get(Limits, null);
            Ok("DEC-107 limits: no token -> 401, no body, WWW-Authenticate: Bearer", r.Status == 401 && r.Body.Length == 0 && r.WwwAuth is { } wa && wa.StartsWith("Bearer", StringComparison.Ordinal), $"{r.Status} {r.WwwAuth}");
            r = await Get(Limits, Token("super", U_SA, edit: c => c["aud"] = "login-api"));
            Ok("DEC-107 limits: wrong aud -> 401", r.Status == 401 && r.Body.Length == 0);
            Ok("DEC-107 limits: no role -> 403 forbidden", Err(await Get(Limits, NO), 403, "forbidden"));
            Ok("DEC-107 limits: custom role -> 403 forbidden", Err(await Get(Limits, CU), 403, "forbidden"));
            Ok("DEC-107 limits: POST / PUT on the limits path are not routes (405/404, never 200)", (await Send(HttpMethod.Post, Limits, SA, "{}")).Status is 404 or 405
                && (await Send(HttpMethod.Put, Limits, SA, "{}")).Status is 404 or 405);
            Ok("DEC-107 limits: reads write no event", (await NewEvents()).Count == 0);
        }

        // ---- a write naming reason_min_length / reason_max_length -> the existing 422 unknown_field ----
        {
            var rC = await Post(SA, Create(P_B, "DEC-107 probe", DbCfg()));
            var LL = rC.Status == 201 ? LocOf(rC) : Guid.Empty;
            Ok("DEC-107 create: 201 body carries no reason bounds", rC.Status == 201 && NoBounds(rC), rC.Body);
            ForSchema("login_config_saved_super", rC);
            await NewEvents();
            var rv = Rv(LL); var ch = await Count("location_login_config_change");
            var r = await Put(LL, SA, With(With(DbCfg(display: "DEC-107 x"), "reason_min_length", 50), "reason_max_length", 200), $"\"{rv}\"");
            Ok("DEC-107 PUT: reason_min_length / reason_max_length in the body -> 422 unknown_field (both), nothing written",
                HasField(r, "reason_min_length", "unknown_field") && HasField(r, "reason_max_length", "unknown_field") && Rv(LL) == rv && (await NewEvents()).Count == 0, r.Body);
            r = await Post(SA, Create(P_B, "DEC-107 probe 2", With(DbCfg(), "reason_max_length", 200)));
            Ok("DEC-107 create: login_config.reason_max_length -> 422 login_config/reason_max_length unknown_field", HasField(r, "login_config/reason_max_length", "unknown_field"), r.Body);
            r = await Post(SA, new Dictionary<string, object?> { ["partner_id"] = P_B.ToString("D"), ["name"] = "DEC-107 probe 3", ["login_config"] = DbCfg(), ["reason_min_length"] = 50 });
            Ok("DEC-107 create: top-level reason_min_length -> 422 reason_min_length unknown_field; nothing written", HasField(r, "reason_min_length", "unknown_field")
                && (await NewEvents()).Count == 0 && await Count("location_login_config_change") == ch, r.Body);
        }

        // ---- boundaries: (this host, constants) and (second host, 50/200); API response + change row + outbox ----
        async Task Bounds(string tag, HttpClient? via, int mn, int mx)
        {
            Task<R> PutV(Guid loc, object body, string ifm) => Send(HttpMethod.Put, Cfg(loc), SA, J(body), ifm, via: via);
            Task<R> PostV(object body) => Send(HttpMethod.Post, "/api/orgs/locations", SA, J(body), via: via);
            var lim = await Send(HttpMethod.Get, Limits, RD, via: via);
            Ok($"DEC-107 {tag}: limits (org:V) -> exactly {mn}/{mx}", lim.Status == 200 && Obj(lim.Body).Count == 2 && (int?)Obj(lim.Body)["reason_min_length"] == mn && (int?)Obj(lim.Body)["reason_max_length"] == mx, lim.Body);
            if (lim.Status == 200) ForSchema("login_config_limits", lim);
            var rc = await PostV(Create(P_B, $"DEC-107 {tag} bounds", DbCfg()));
            var L = rc.Status == 201 ? LocOf(rc) : Guid.Empty;
            await NewEvents();
            var g = await Send(HttpMethod.Get, Cfg(L), SA, via: via);
            Ok($"DEC-107 {tag}: location GET carries reason_min_length {mn}, reason_max_length {mx}", g.Status == 200 && (int?)Obj(g.Body)["reason_min_length"] == mn && (int?)Obj(g.Body)["reason_max_length"] == mx, g.Body);
            if (g.Status == 200) ForSchema("login_config_get_super", g);
            async Task Write(string label, string reason, bool down, string want) // want: "200:<trimmed>" | "200:null" | "422" | "400"
            {
                var rv = Rv(L); var ch = await Count("location_login_config_change");
                if (down)
                {   // MFA back on first (an upgrade, no reason) so the next write is a downgrade
                    if (!await OScalar<bool>("SELECT mfa_required FROM auth.location_login_config WHERE location_id=$1", L))
                    { await PutV(L, DbCfg(display: $"DEC-107 {k++}"), $"\"{rv}\""); await NewEvents(); rv = Rv(L); ch = await Count("location_login_config_change"); }
                }
                var r = await PutV(L, With(DbCfg(display: $"DEC-107 {k++}", mfa: !down), "reason", reason), $"\"{rv}\"");
                var ev = await NewEvents();
                bool ok;
                if (want.StartsWith("200:", StringComparison.Ordinal))
                {
                    string? w = want == "200:null" ? null : want[4..];
                    var row = await OScalar<string>("SELECT coalesce(reason, '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 ORDER BY at DESC, change_id LIMIT 1", L);
                    var ob = await OScalar<string>("SELECT coalesce(envelope->'details'->>'reason', '<SQL NULL>') FROM auth.audit_outbox WHERE envelope->'target'->>'id' = $1 ORDER BY occurred_at DESC LIMIT 1", L.ToString("D"));
                    ok = r.Status == 200 && NoBounds(r) && ev.Count == 1 && Valid(ev[0]) && (string?)ev[0]["details"]!["reason"] == w
                        && row == (w ?? "<SQL NULL>") && ob == (w ?? "<SQL NULL>") && Rv(L) == rv + 1;
                    if (ok && r.Status == 200) ForSchema("login_config_saved_super", r);
                }
                else if (want == "422")
                    ok = r.Status == 422 && Fields(r) == "reason:reason_length" && ev.Count == 0 && Rv(L) == rv && await Count("location_login_config_change") == ch;
                else ok = Err(r, 400, "reason_required") && ev.Count == 0 && Rv(L) == rv && await Count("location_login_config_change") == ch;
                Ok($"DEC-107 {tag}: PUT {(down ? "MFA off" : "display_name")}, {label} -> {(want.StartsWith("200:") ? (want == "200:null" ? "200, reason null" : "200") : want)}; response, change row, outbox",
                    ok, $"{r.Status} {Fields(r)} ev={ev.Count} {(r.Body.Length > 160 ? r.Body[..160] : r.Body)}");
            }
            string X(int n) => Rep("x", n);
            await Write($"{mn - 1} chars (min - 1)", X(mn - 1), false, "422");
            await Write($"{mn} chars (min)", X(mn), false, "200:" + X(mn));
            await Write($"{mx} chars (max)", X(mx), false, "200:" + X(mx));
            await Write($"{mx + 1} chars (max + 1)", X(mx + 1), false, "422");
            await Write($"{mn} chars padded with spaces/tab (trimmed)", "  \t" + X(mn) + "   ", false, "200:" + X(mn));
            await Write($"{mn - 1} chars padded to {mn + 4} raw (padding not counted)", "  " + X(mn - 1) + "   ", false, "422");
            await Write($"{mx} chars padded (trimmed)", " " + X(mx) + " ", false, "200:" + X(mx));
            await Write($"whitespace-only ({mx + 5} spaces), not a downgrade", Rep(" ", mx + 5), false, "200:null");
            await Write($"{mn} astral (min)", Rep(Devil, mn), false, "200:" + Rep(Devil, mn));
            await Write($"{mn - 1} astral (min - 1)", Rep(Devil, mn - 1), false, "422");
            await Write($"{mx} astral (max)", Rep(Devil, mx), false, "200:" + Rep(Devil, mx));
            await Write($"{mx + 1} astral (max + 1)", Rep(Devil, mx + 1), false, "422");
            await Write($"whitespace-only ({mn} spaces) on a downgrade", Rep(" ", mn), true, "400");
            await Write($"{mn - 1} chars on a downgrade", X(mn - 1), true, "422");
            await Write($"{mn} chars padded on a downgrade", "   " + X(mn) + " ", true, "200:" + X(mn));
            await Write($"{mx + 1} chars on a downgrade", X(mx + 1), true, "422");
            await Write($"{mx} chars on a downgrade", X(mx), true, "200:" + X(mx));
            // create path: login_config.reason
            foreach (var (cl, reason, want) in new[] { ($"{mn - 1}", X(mn - 1), "422"), ($"{mn} padded", "  " + X(mn) + "  ", X(mn)), ($"{mx}", X(mx), X(mx)), ($"{mx + 1}", X(mx + 1), "422"),
                                                       ("whitespace-only", Rep(" ", mn + 1), "null"), ($"{mn} astral", Rep(Devil, mn), Rep(Devil, mn)), ($"{mn - 1} astral", Rep(Devil, mn - 1), "422") })
            {
                var ch = await Count("location_login_config_change");
                var r = await PostV(Create(P_B, $"DEC-107 {tag} create {k++}", With(DbCfg(), "reason", reason)));
                var ev = await NewEvents();
                bool ok;
                if (want == "422") ok = r.Status == 422 && Fields(r) == "login_config/reason:reason_length" && ev.Count == 0 && await Count("location_login_config_change") == ch;
                else
                {
                    string? w = want == "null" ? null : want;
                    ok = r.Status == 201 && NoBounds(r) && ev.Count == 1 && Valid(ev[0]) && (string?)ev[0]["details"]!["reason"] == w
                        && await OScalar<string>("SELECT coalesce(reason, '<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1", LocOf(r)) == (w ?? "<SQL NULL>");
                    if (ok) ForSchema("login_config_saved_super", r);
                }
                Ok($"DEC-107 {tag}: create, reason {cl} -> {(want == "422" ? "422 login_config/reason:reason_length" : want == "null" ? "201, reason null" : "201")}; response, change row, outbox", ok, $"{r.Status} {Fields(r)} ev={ev.Count}");
            }
            // a downgrade create with a blank reason -> 400 reason_required
            {
                var ch = await Count("location_login_config_change");
                var r = await PostV(Create(P_B, $"DEC-107 {tag} create {k++}", With(DbCfg(mfa: false), "reason", Rep(" ", mx))));
                Ok($"DEC-107 {tag}: create MFA off, whitespace-only reason -> 400 reason_required, nothing written", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0 && await Count("location_login_config_change") == ch, r.Body);
            }
        }
        await Bounds($"default {Mn}/{Mx}", null, Mn, Mx);
        var port5 = FreePort(port);
        var log5 = new CaptureLogger();
        await using (var app5 = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port5}", ReasonMinLength = 50, ReasonMaxLength = 200 }, dir, extraLogger: log5))
        {
            await app5.StartAsync();
            using var http5 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port5}") };
            await Bounds("config 50/200", http5, 50, 200);
            var r = await Send(HttpMethod.Get, Cfg(A1), RD, via: http5);
            Ok("DEC-107 config 50/200: org:V GET -> restricted view with reason_min_length 50, reason_max_length 200", r.Status == 200 && !Obj(r.Body).ContainsKey("oidc_authority")
                && (int?)Obj(r.Body)["reason_min_length"] == 50 && (int?)Obj(r.Body)["reason_max_length"] == 200, r.Body);
            if (r.Status == 200) ForSchema("login_config_get_restricted", r);
            Ok("DEC-107 config 50/200: limits no token -> 401; no role -> 403 forbidden", (await Send(HttpMethod.Get, Limits, null, via: http5)).Status == 401
                && Err(await Send(HttpMethod.Get, Limits, NO, via: http5), 403, "forbidden"));
            Ok("DEC-107 config 50/200: no Error log line on the second host", log5.Entries.Count == 0, string.Join(" | ", log5.Entries.Select(e => $"{e.ExType}: {e.Text}")));
            await app5.StopAsync();
        }
        var lim0 = await Get(Limits, SA);
        Ok($"DEC-107: this host still answers {Mn}/{Mx} after the 50/200 host ran (per-host snapshot)", (int?)Obj(lim0.Body)["reason_min_length"] == Mn && (int?)Obj(lim0.Body)["reason_max_length"] == Mx, lim0.Body);
    }

    // ================= 7j. DEC-106 [Mohanraj, PM t145u; UX D-106 md5 2b17464c] =================
    // Partner Admins and Super Admins can both turn MFA back on (create and PUT), no reason needed; a given reason follows §3.2
    // (blank -> null, else reason_length). MFA off stays Super Admin only with a reason (403 not_super_admin / 400 reason_required).
    // Another partner's location -> 404 not_found + forbidden_scope. Partner Admin turning backup sign-in back off: Confirmed DEC-118
    // (PM Nov 5 2026 6:58 AM PT), own partner only (Confirmed DEC-120), no reason needed (Confirmed DEC-121); the current behaviour (200) is pinned here unchanged. No new error code.
    {
        const string Why = "Client's Entra conditional access already enforces MFA; ticket OPS-1234";
        var cr = await Post(SA, Create(P_A, "DEC-106 MFA off", With(DbCfg(mfa: false), "reason", Why)));
        Guid M1 = cr.Status == 201 ? LocOf(cr) : Guid.Empty;
        Ok("DEC-106 setup: SA creates a P_A database location with MFA off + reason -> 201", cr.Status == 201, cr.Body);
        await NewEvents();
        async Task<bool> Mfa(Guid l) => await OScalar<bool>("SELECT mfa_required FROM auth.location_login_config WHERE location_id=$1", l);
        string Tag(R r) => $"{r.Status} {r.Body}";

        // PA, other partner: 404 + forbidden_scope, nothing changes.
        var rv = Rv(M1); var ch = await Count("location_login_config_change");
        var r = await Put(M1, PB, DbCfg(mfa: true), $"\"{rv}\"");
        Ok("DEC-106 put: PB (other partner) MFA on -> 404 not_found + forbidden_scope event, nothing changed", Err(r, 404, "not_found")
            && Denied(await NewEvents(), "forbidden_scope", M1, P_A, P_B, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required")
            && Rv(M1) == rv && !await Mfa(M1) && await Count("location_login_config_change") == ch, Tag(r));

        // PA own, MFA on, reason given but invalid length -> the existing 422 reason_length, nothing changed.
        r = await Put(M1, PA, With(DbCfg(mfa: true), "reason", new string('x', Mn - 1)), $"\"{rv}\"");
        Ok($"DEC-106 put: PA own MFA on with a {Mn - 1}-char reason -> 422 reason:reason_length (DEC-107 applies), nothing changed",
            HasField(r, "reason", "reason_length") && Rv(M1) == rv && !await Mfa(M1) && await Count("location_login_config_change") == ch, Tag(r));
        await NewEvents();

        // PA own, MFA on, no reason -> 200; row, change row, event.
        r = await Put(M1, PA, DbCfg(mfa: true), $"\"{rv}\"");
        var ev = await NewEvents();
        Ok("DEC-106 put: PA own MFA on, no reason -> 200 restricted view, mfa_required true", r.Status == 200 && (bool)Obj(r.Body)["mfa_required"]! && !Obj(r.Body).ContainsKey("secret") && !Obj(r.Body).ContainsKey("oidc_authority"), Tag(r));
        if (r.Status == 200) ForSchema("login_config_saved_restricted", r);
        Ok("DEC-106 put: PA own MFA on -> row mfa_required true, row_version +1, one change row (reason SQL NULL, changed_fields {mfa_required})",
            await Mfa(M1) && Rv(M1) == rv + 1 && await Count("location_login_config_change") == ch + 1
            && await OScalar<string>("SELECT coalesce(reason,'<SQL NULL>') || '|' || array_to_string(changed_fields,',') FROM auth.location_login_config_change WHERE location_id=$1 AND row_version=$2", M1, rv + 1) == "<SQL NULL>|mfa_required");
        Ok("DEC-106 put: PA own MFA on -> one auth.provider.changed (partner, actor U_PA, changed_fields [mfa_required], before false -> after true, reason null), schema-valid",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["event_type"]! == "auth.provider.changed" && (string)ev[0]["admin_type"]! == "partner"
            && (string)ev[0]["actor"]!["user_id"]! == U_PA.ToString("D") && (string)ev[0]["target"]!["id"]! == M1.ToString("D")
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["mfa_required"])
            && (bool)ev[0]["details"]!["before"]!["mfa_required"]! == false && (bool)ev[0]["details"]!["after"]!["mfa_required"]! == true
            && ev[0]["details"]!.AsObject().ContainsKey("reason") && ev[0]["details"]!["reason"] is null, ev.Count > 0 ? ev[0].ToJsonString() : "none");

        // PA own, MFA off again: with and without a reason -> 403 not_super_admin + event, nothing changed.
        rv = Rv(M1); ch = await Count("location_login_config_change");
        foreach (var (lbl, body) in new[] { ("no reason", (object)DbCfg(mfa: false)), ("with a reason", With(DbCfg(mfa: false), "reason", Why)) })
        {
            r = await Put(M1, PA, body, $"\"{rv}\"");
            Ok($"DEC-106 put: PA own MFA off, {lbl} -> 403 not_super_admin + not_super_admin event (mfa_required), nothing changed", Err(r, 403, "not_super_admin")
                && Denied(await NewEvents(), "not_super_admin", M1, P_A, P_A, "partner", "update", "mfa_required") && Rv(M1) == rv && await Mfa(M1)
                && await Count("location_login_config_change") == ch, Tag(r));
        }

        // SA MFA off: no reason -> 400 reason_required (no event); with a reason -> 200.
        r = await Put(M1, SA, DbCfg(mfa: false), $"\"{rv}\"");
        Ok("DEC-106 put: SA MFA off, no reason -> 400 reason_required, no event, nothing changed", Err(r, 400, "reason_required") && (await NewEvents()).Count == 0 && Rv(M1) == rv && await Mfa(M1), Tag(r));
        r = await Put(M1, SA, With(DbCfg(mfa: false), "reason", Why), $"\"{rv}\"");
        ev = await NewEvents();
        Ok("DEC-106 put: SA MFA off + reason -> 200, super event with the reason", r.Status == 200 && !await Mfa(M1) && ev.Count == 1 && Valid(ev[0])
            && (string)ev[0]["admin_type"]! == "super" && (string?)ev[0]["details"]!["reason"] == Why, Tag(r));

        // PA own, MFA on with a whitespace-only reason -> 200, reason null; then SA off, PA on with a valid reason -> stored.
        rv = Rv(M1);
        r = await Put(M1, PA, With(DbCfg(mfa: true), "reason", "            "), $"\"{rv}\"");
        ev = await NewEvents();
        Ok("DEC-106 put: PA own MFA on, whitespace-only reason -> 200, reason null (blank -> null, §3.2)", r.Status == 200 && await Mfa(M1) && ev.Count == 1 && Valid(ev[0])
            && ev[0]["details"]!["reason"] is null && await OScalar<string>("SELECT coalesce(reason,'<SQL NULL>') FROM auth.location_login_config_change WHERE location_id=$1 AND row_version=$2", M1, rv + 1) == "<SQL NULL>", Tag(r));
        r = await Put(M1, SA, With(DbCfg(mfa: false), "reason", Why), $"\"{Rv(M1)}\"");
        await NewEvents();
        rv = Rv(M1);
        r = await Put(M1, PA, With(DbCfg(mfa: true), "reason", "  Back on after the OPS-1234 review  "), $"\"{rv}\"");
        ev = await NewEvents();
        Ok("DEC-106 put: PA own MFA on with a valid reason -> 200, trimmed reason stored in the change row and the event", r.Status == 200 && await Mfa(M1) && ev.Count == 1 && Valid(ev[0])
            && (string?)ev[0]["details"]!["reason"] == "Back on after the OPS-1234 review"
            && await OScalar<string>("SELECT reason FROM auth.location_login_config_change WHERE location_id=$1 AND row_version=$2", M1, rv + 1) == "Back on after the OPS-1234 review", Tag(r));

        // SA MFA on -> 200 (super event, no reason).
        await Put(M1, SA, With(DbCfg(mfa: false), "reason", Why), $"\"{Rv(M1)}\"");
        await NewEvents();
        r = await Put(M1, SA, DbCfg(mfa: true), $"\"{Rv(M1)}\"");
        ev = await NewEvents();
        Ok("DEC-106 put: SA MFA on, no reason -> 200 super view, super event changed_fields [mfa_required], reason null", r.Status == 200 && Obj(r.Body).ContainsKey("oidc_authority") && await Mfa(M1)
            && ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super" && (string)ev[0]["actor"]!["user_id"]! == U_SA.ToString("D")
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["mfa_required"]) && ev[0]["details"]!["reason"] is null, Tag(r));
        if (r.Status == 200) ForSchema("login_config_saved_super", r);

        // Create: PA with MFA on -> 201 (already §4 A-5); PA with MFA off -> 403 not_super_admin (partner-create keeps MFA locked, D-106).
        var locs = await Count("location");
        r = await Post(PA, Create(P_A, "DEC-106 PA create MFA off", With(DbCfg(mfa: false), "reason", Why)));
        Ok("DEC-106 create: PA with MFA off (+ reason) -> 403 not_super_admin + event, no location", Err(r, 403, "not_super_admin")
            && Denied(await NewEvents(), "not_super_admin", P_A, P_A, P_A, "partner", "create", "mfa_required") && await Count("location") == locs, Tag(r));
        r = await Post(PA, Create(P_A, "DEC-106 PA create MFA on", DbCfg(mfa: true)));
        ev = await NewEvents();
        Ok("DEC-106 create: PA with MFA on, no reason -> 201, created event (partner, reason null)", r.Status == 201 && ev.Count == 1 && Valid(ev[0])
            && (string)ev[0]["admin_type"]! == "partner" && ev[0]["details"]!["reason"] is null, Tag(r));

        // Confirmed DEC-118 (PM Nov 5 2026 6:58 AM PT): Partner Admin turning backup sign-in back off. Current behaviour pinned, unchanged: 200, no reason.
        // Confirmed DEC-120: own partner only; another partner's PA gets 404 not_found + forbidden_scope (DEC-111's exception covers reason-length bounds only).
        // Confirmed DEC-121: no reason needed; a reason that is sent still follows §3.2 (422 reason_length).
        Guid T6 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa16");
        cr = await Post(SA, Create(P_A, "DEC-106 fallback on", With(EntraCfg(T6, fallback: true), "reason", "Pilot fallback per OPS-77")));
        Guid F1 = cr.Status == 201 ? LocOf(cr) : Guid.Empty;
        await NewEvents();
        rv = Rv(F1); ch = await Count("location_login_config_change");
        // SSE2 follow-up (5): the cross-partner 404 must be byte-identical to a missing location's 404 (no existence oracle).
        var miss120 = await Put(missing, PB, EntraCfg(T6, fallback: false), "\"1\"");
        Ok("DEC-120 put: PB on a missing location -> 404, no event (reference body)", Err(miss120, 404, "not_found") && (await NewEvents()).Count == 0, Tag(miss120));
        r = await Put(F1, PB, EntraCfg(T6, fallback: false), $"\"{rv}\"");
        Ok("DEC-120 put: PB (other partner) fallback back off -> 404 not_found (body identical to the missing-location 404) + forbidden_scope event, fallback stays on, nothing changed (Confirmed DEC-120)", cr.Status == 201 && Err(r, 404, "not_found") && r.Body == miss120.Body
            && Denied(await NewEvents(), "forbidden_scope", F1, P_A, P_B, "partner", "update", "provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required")
            && Rv(F1) == rv && await OScalar<bool>("SELECT allow_db_fallback FROM auth.location_login_config WHERE location_id=$1", F1) && await Count("location_login_config_change") == ch, Tag(r));
        r = await Put(F1, PA, With(EntraCfg(T6, fallback: false), "reason", new string('x', Mn - 1)), $"\"{rv}\"");
        Ok($"DEC-121 put: PA own fallback back off with a {Mn - 1}-char reason -> 422 reason:reason_length (§3.2), fallback stays on, nothing changed", HasField(r, "reason", "reason_length")
            && Rv(F1) == rv && await OScalar<bool>("SELECT allow_db_fallback FROM auth.location_login_config WHERE location_id=$1", F1) && await Count("location_login_config_change") == ch, Tag(r));
        await NewEvents();
        r = await Put(F1, PA, EntraCfg(T6, fallback: false), $"\"{Rv(F1)}\"");
        ev = await NewEvents();
        Ok("DEC-118 + DEC-121 pin (Confirmed DEC-118/121): PA own fallback back off, no reason -> 200 (behaviour unchanged), partner event changed_fields [allow_db_fallback]", cr.Status == 201 && r.Status == 200
            && (bool)Obj(r.Body)["allow_db_fallback"]! == false && ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "partner"
            && ev[0]["details"]!["changed_fields"]!.AsArray().Select(x => (string)x!).SequenceEqual(["allow_db_fallback"]), Tag(r));
        r = await Put(F1, PA, EntraCfg(T6, fallback: true), $"\"{Rv(F1)}\"");
        Ok("DEC-106: PA fallback on again -> 403 not_super_admin (unchanged, DEC-096)", Err(r, 403, "not_super_admin") && Denied(await NewEvents(), "not_super_admin", F1, P_A, P_A, "partner", "update", "allow_db_fallback"), Tag(r));
    }

    // ================= 7k. v1.1 Slice A: per-partner reason bounds [Confirmed DEC-109..117] [TLD 06:59, 08:00 Q1-Q3, 08:00b] [LD 12b1d9cd] =================
    // GET/PUT /api/orgs/partners/{partner_id}/login-config/reason-bounds; the resolver (row, else LoginConfig:*) behind limits, the location
    // GET, the location PUT/POST reason; event auth.partner_reason_bounds.changed in the same transaction. Test partners are SSE2-FAKE-TEST-ONLY.
    {
        Guid P_C = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000003"), P_D = Guid.Parse("5f0c3e1a-0004-4000-8000-000000000004"),
             P_E = Guid.Parse("5f0c3e1a-0005-4000-8000-000000000005"), P_F = Guid.Parse("5f0c3e1a-0006-4000-8000-000000000006"),
             P_G = Guid.Parse("5f0c3e1a-0009-4000-8000-000000000009"), P_H = Guid.Parse("5f0c3e1a-000a-4000-8000-00000000000a");
        // TLD 11:23 identity scope (SSE2 relay; interim pending LS-OQ-61 / PQ-240): updated_by is omitted ONLY for a PA reading ANOTHER partner.
        bool HasBy(R r, Guid? by) => r.Status == 200 && (by is { } u ? (string?)Obj(r.Body)["updated_by"] == u.ToString("D") : !Obj(r.Body).ContainsKey("updated_by"));
        // SSE2 review 03398b18 INFO-2: count only lock waits of the API's own role in this database (distinct backends), not every
        // ungranted lock on the shared cluster, so the barrier releases only when the API requests themselves are the contenders.
        var apiRole = new NpgsqlConnectionStringBuilder(adminCs).Username!;
        async Task<long> ApiWaiters() => await OScalar<long>("SELECT count(DISTINCT l.pid) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE NOT l.granted AND a.datname = current_database() AND a.usename = $1", apiRole);
        // ---- TLD 13:27 item 2: updated_by by ALLOWLIST (super, or partner admin of THIS partner); unknown / future admin types fail closed ----
        Ok("Slice A identity allowlist (TLD 13:27 item 2): super (no partner / any partner_id claim) and partner-own -> shown; partner-other, partner without partner_id, client, unknown, empty, case variants -> hidden",
            LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_SA, "super", null), P_C) && LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_SA, "super", P_A), P_C)
            && LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "partner", P_C), P_C)
            && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "partner", P_A), P_C) && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "partner", null), P_C)
            && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "client", P_C), P_C) && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "client", null), P_C)
            && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "client_admin", P_C), P_C) && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "future_admin", P_C), P_C)
            && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "", P_C), P_C) && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "Super", null), P_C)
            && !LoginConfigApi.ShowsBoundsEditor(new AdminIdentity(U_PA, "Partner", P_C), P_C));
        string Prb(Guid p) => $"/api/orgs/partners/{p:D}/login-config/reason-bounds";
        Task<R> PrbPut(Guid p, string? tok, object body, string? im, HttpClient? via = null) => Send(HttpMethod.Put, Prb(p), tok, J(body), im, via: via);
        object B(object mn, object mx) => new Dictionary<string, object?> { ["reason_min_length"] = mn, ["reason_max_length"] = mx };
        async Task<long> PrbRows() => await Count("partner_reason_bounds");
        async Task<string> PrbSnap(Guid p) => await OScalar<string>("SELECT coalesce((SELECT reason_min_length || '/' || reason_max_length || '/v' || row_version FROM auth.partner_reason_bounds WHERE partner_id = $1), '<none>')", p);
        string Fs(R r) => r.Status == 422 ? string.Join(",", Obj(r.Body)["fields"]!.AsArray().Select(f => $"{f!["field"]}:{f["code"]}")) : "";
        string Tg(R r) => $"{r.Status} {r.ETag} {(r.Body.Length > 200 ? r.Body[..200] : r.Body)}";
        const string LimitsP = "/api/orgs/login-config/limits";
        bool Lim(R r, int mn, int mx) => r.Status == 200 && Obj(r.Body).Count == 2 && (int?)Obj(r.Body)["reason_min_length"] == mn && (int?)Obj(r.Body)["reason_max_length"] == mx;
        await NewEvents();
        Ok("Slice A setup: no partner_reason_bounds rows yet", await PrbRows() == 0);

        // ---- GET, unset partner: default (config) bounds, no ETag [TLD 08:00 Q2/Q3, DEC-115] ----
        var g = await Get(Prb(P_C), SA);
        Ok($"Slice A GET unset partner (SA): 200 default {Mn}/{Mx}, source default, no ETag, no row_version (DEC-115)", g.Status == 200 && g.ETag is null
            && (string?)Obj(g.Body)["source"] == "default" && (int?)Obj(g.Body)["reason_min_length"] == Mn && (int?)Obj(g.Body)["reason_max_length"] == Mx
            && (string?)Obj(g.Body)["partner_id"] == P_C.ToString("D") && !Obj(g.Body).ContainsKey("row_version") && g.CacheControl == "no-store", Tg(g));
        if (g.Status == 200) ForSchema("partner_reason_bounds_view", g);
        g = await Get(Prb(P_C), PA);
        Ok("Slice A GET other partner (PA of P_A reads P_C): 200 (cross-partner read, TL interim, TO CONFIRM LS-OQ-61)", g.Status == 200 && (string?)Obj(g.Body)["source"] == "default", Tg(g));
        Ok("Slice A GET: no token -> 401 + WWW-Authenticate Bearer, no body", (await Get(Prb(P_C), null)) is { Status: 401, Body: "" } u && u.WwwAuth == "Bearer");
        Ok("Slice A GET: org:V reader -> 403 forbidden; no role -> 403; custom role -> 403", Err(await Get(Prb(P_A), RD), 403, "forbidden")
            && Err(await Get(Prb(P_A), NO), 403, "forbidden") && Err(await Get(Prb(P_A), CU), 403, "forbidden"));
        Ok("Slice A GET: malformed / nil partner id -> 404 not_found", Err(await Get("/api/orgs/partners/not-a-uuid/login-config/reason-bounds", SA), 404, "not_found")
            && Err(await Get(Prb(Guid.Empty), SA), 404, "not_found"));
        Ok("Slice A GET: no event, no row", (await NewEvents()).Count == 0 && await PrbRows() == 0);

        // ---- PUT refusals in step order [TLD 08:00 Q1]; nothing written, no event ----
        async Task Refused(string label, Func<Task<R>> f, Func<R, bool> want)
        {
            var r = await f();
            Ok($"Slice A PUT {label}; no row, no event", want(r) && await PrbSnap(P_C) == "<none>" && (await NewEvents()).Count == 0, Tg(r));
        }
        await Refused("no token -> 401 Bearer, no body", () => PrbPut(P_C, null, B(20, 300), "\"0\""), r => r.Status == 401 && r.Body == "" && r.WwwAuth == "Bearer");
        await Refused("org:V reader -> 403 forbidden", () => PrbPut(P_C, RD, B(20, 300), "\"0\""), r => Err(r, 403, "forbidden"));
        await Refused("no role -> 403 forbidden", () => PrbPut(P_C, NO, B(20, 300), "\"0\""), r => Err(r, 403, "forbidden"));
        await Refused("custom role (LoginMethodWrite + OrgCreateEdit, not a built-in role) -> 403 forbidden", () => PrbPut(P_C, CU, B(20, 300), "\"0\""), r => Err(r, 403, "forbidden"));
        await Refused("403 wins over a bad body (decided before the body is read)", () => PrbPut(P_C, RD, "{nope", null), r => Err(r, 403, "forbidden"));
        await Refused("malformed partner id -> 404 not_found", () => Send(HttpMethod.Put, "/api/orgs/partners/xyz/login-config/reason-bounds", SA, J(B(20, 300)), "\"0\""), r => Err(r, 404, "not_found"));
        await Refused("nil partner id -> 404 not_found (before If-Match)", () => PrbPut(Guid.Empty, SA, B(20, 300), null), r => Err(r, 404, "not_found"));
        await Refused("no If-Match -> 428 precondition_required", () => PrbPut(P_C, SA, B(20, 300), null), r => Err(r, 428, "precondition_required"));
        foreach (var bad in new[] { "*", "W/\"0\"", "\"0\", \"1\"", "0", "\"-1\"", "\"00\"", "\"01\"", "\"abc\"" })
            await Refused($"If-Match {bad} -> 400 if_match_invalid", () => PrbPut(P_C, SA, B(20, 300), bad), r => Err(r, 400, "if_match_invalid"));
        await Refused("unset partner, If-Match \"2\" -> 409 concurrency_conflict, current_row_version 0 (unset, TLD 11:23 A)",
            () => PrbPut(P_C, SA, B(20, 300), "\"2\""), r => Err(r, 409, "concurrency_conflict") && (long?)Obj(r.Body)["current_row_version"] == 0);
        await Refused("unset partner, If-Match \"1\" (the retired 08:00 create constant) -> 409 concurrency_conflict, current_row_version 0 (TLD 11:23 A)",
            () => PrbPut(P_C, SA, B(20, 300), "\"1\""), r => Err(r, 409, "concurrency_conflict") && (long?)Obj(r.Body)["current_row_version"] == 0);
        await Refused("stale If-Match + malformed body -> 409 (concurrency before body)", () => PrbPut(P_C, SA, "{nope", "\"7\""), r => Err(r, 409, "concurrency_conflict"));
        await Refused("If-Match \"0\" + malformed JSON -> 400 malformed_body", () => PrbPut(P_C, SA, "{nope", "\"0\""), r => Err(r, 400, "malformed_body"));
        await Refused("If-Match \"0\" + a JSON array -> 400 malformed_body", () => PrbPut(P_C, SA, "[]", "\"0\""), r => Err(r, 400, "malformed_body"));
        await Refused("duplicate key -> 400 malformed_body", () => PrbPut(P_C, SA, "{\"reason_min_length\":20,\"reason_min_length\":30,\"reason_max_length\":300}", "\"0\""), r => Err(r, 400, "malformed_body"));
        await Refused("over 64 KiB -> 413 payload_too_large", () => PrbPut(P_C, SA, "{\"reason_min_length\":20,\"reason_max_length\":300,\"pad\":\"" + new string('x', 70_000) + "\"}", "\"0\""),
            r => Err(r, 413, "payload_too_large"));
        foreach (var (lbl, body, want) in new (string, object, string)[]
        {
            ("min 9", B(9, 300), "reason_min_length:reason_bound_range"),
            ("max 501", B(20, 501), "reason_max_length:reason_bound_range"),
            ("min 0 and max 1000", B(0, 1000), "reason_min_length:reason_bound_range,reason_max_length:reason_bound_range"),
            ("min -5", B(-5, 300), "reason_min_length:reason_bound_range"),
            ("min 301 > max 300", B(301, 300), "reason_min_length:reason_bound_order"),
            ("min 500 > max 10", B(500, 10), "reason_min_length:reason_bound_order"),
            ("min a string", B("20", 300), "reason_min_length:type"),
            ("max a float 12.5", B(20, 12.5), "reason_max_length:type"),
            ("max null", B(20, null!), "reason_max_length:type"),
            ("min missing", new Dictionary<string, object?> { ["reason_max_length"] = 300 }, "reason_min_length:required"),
            ("reason field (DEC-113: no reason)", new Dictionary<string, object?> { ["reason_min_length"] = 20, ["reason_max_length"] = 300, ["reason"] = "because the ops team said so" }, "reason:unknown_field"),
            ("row_version in the body", new Dictionary<string, object?> { ["reason_min_length"] = 20, ["reason_max_length"] = 300, ["row_version"] = 1 }, "row_version:unknown_field"),
            ("empty object", new Dictionary<string, object?>(), "reason_min_length:required,reason_max_length:required"),
        })
            await Refused($"{lbl} -> 422 {want}", () => PrbPut(P_C, SA, body, "\"0\""), r => r.Status == 422 && Err(r, 422, "validation_failed") && Fs(r) == want);

        // ---- create (unset partner, If-Match "0" = expect unset, TLD 11:23 A) by a Partner Admin of ANOTHER partner: DEC-111 cross-partner write ----
        var r = await PrbPut(P_C, PA, B(20, 300), "\"0\"");
        var ev = await NewEvents();
        Ok("Slice A PUT create: PA (P_A) on P_C with If-Match \"0\" -> 200, ETag \"1\", source partner, 20/300, row_version 1, updated_at, NO updated_by (PA on another partner, TLD 11:23 identity) (DEC-111)", r.Status == 200 && r.ETag == "\"1\""
            && (string?)Obj(r.Body)["source"] == "partner" && (int?)Obj(r.Body)["reason_min_length"] == 20 && (int?)Obj(r.Body)["reason_max_length"] == 300
            && (long?)Obj(r.Body)["row_version"] == 1 && HasBy(r, null) && Obj(r.Body).ContainsKey("updated_at") && await PrbSnap(P_C) == "20/300/v1", Tg(r));
        if (r.Status == 200) ForSchema("partner_reason_bounds_view", r);
        Ok("Slice A event (create, cross-partner PA): one auth.partner_reason_bounds.changed, schema-valid; old = config, new = 20/300; actor P_A, target P_C, cross_partner true; no reason",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["event_type"]! == "auth.partner_reason_bounds.changed" && (string)ev[0]["admin_type"]! == "partner"
            && (string)ev[0]["actor"]!["user_id"]! == U_PA.ToString("D") && (string)ev[0]["target"]!["type"]! == "partner" && (string)ev[0]["target"]!["id"]! == P_C.ToString("D")
            && (string)ev[0]["scope"]!["partner_id"]! == P_C.ToString("D") && (string)ev[0]["action"]! == "create" && (string)ev[0]["details"]!["change_kind"]! == "created"
            && (long)ev[0]["details"]!["row_version"]! == 1
            && (int)ev[0]["details"]!["old_reason_min_length"]! == Mn && (int)ev[0]["details"]!["old_reason_max_length"]! == Mx
            && (int)ev[0]["details"]!["new_reason_min_length"]! == 20 && (int)ev[0]["details"]!["new_reason_max_length"]! == 300
            && (string)ev[0]["details"]!["actor_partner_id"]! == P_A.ToString("D") && (string)ev[0]["details"]!["target_partner_id"]! == P_C.ToString("D")
            && (bool)ev[0]["details"]!["cross_partner"]! && !ev[0]["details"]!.AsObject().ContainsKey("reason") && IsCorr((string?)ev[0]["correlation_id"])
            && (string?)ev[0]["correlation_id"] == r.Corr, ev.Count > 0 ? ev[0].ToJsonString() : "none");
        g = await Get(Prb(P_C), PB);
        Ok("Slice A GET set partner (PB reads P_C): 200, ETag \"1\" = row_version, source partner, 20/300, updated_at, NO updated_by (PA cross-partner read, TLD 11:23 identity)", g.Status == 200 && g.ETag == "\"1\"" && (string?)Obj(g.Body)["source"] == "partner"
            && (long?)Obj(g.Body)["row_version"] == 1 && (int?)Obj(g.Body)["reason_min_length"] == 20 && HasBy(g, null) && Obj(g.Body).ContainsKey("updated_at"), Tg(g));
        if (g.Status == 200) ForSchema("partner_reason_bounds_view", g);
        g = await Get(Prb(P_C), SA);
        Ok("Slice A GET set partner (SA reads P_C, actor partner null): updated_by U_PA kept (every Super Admin read keeps identity, TLD 11:23 identity)", g.Status == 200 && g.ETag == "\"1\"" && HasBy(g, U_PA), Tg(g));
        if (g.Status == 200) ForSchema("partner_reason_bounds_view", g);
        // ---- TLD 11:23 A regression: sequential stale create. A delayed second "0" on the now-set partner -> 409 with the current version, never an update ----
        {
            var rows0 = await PrbRows();
            var late = await PrbPut(P_C, PB, B(30, 330), "\"0\"");
            Ok("Slice A PUT late second create (PB, If-Match \"0\" after the row exists) -> 409 concurrency_conflict, current_row_version 1; row 20/300/v1 unchanged, row count unchanged, no event (TLD 11:23 A)",
                Err(late, 409, "concurrency_conflict") && (long?)Obj(late.Body)["current_row_version"] == 1 && await PrbSnap(P_C) == "20/300/v1" && await PrbRows() == rows0
                && (await NewEvents()).Count == 0, Tg(late));
            if (late.Status == 409) ForSchema("prb_error_response", late);
            late = await PrbPut(P_C, SA, B(20, 300), "\"0\"");
            Ok("Slice A PUT late second create with the SAME values (SA, \"0\") -> 409 current_row_version 1 too (precondition before the no-op), no event", Err(late, 409, "concurrency_conflict")
                && (long?)Obj(late.Body)["current_row_version"] == 1 && await PrbSnap(P_C) == "20/300/v1" && (await NewEvents()).Count == 0, Tg(late));
        }

        // ---- "1" on the now-set partner = an update v1 -> v2 (If-Match equals the current version) ----
        r = await PrbPut(P_C, SA, B(25, 250), "\"1\"");
        ev = await NewEvents();
        Ok("Slice A PUT update: SA, If-Match \"1\" (= current) -> 200, ETag \"2\", 25/250, row_version 2, updated_by U_SA (SA keeps identity)", r.Status == 200 && r.ETag == "\"2\"" && (long?)Obj(r.Body)["row_version"] == 2
            && HasBy(r, U_SA) && await PrbSnap(P_C) == "25/250/v2", Tg(r));
        Ok("Slice A event (update, SA): change_kind updated, action update, row_version 2, old 20/300 -> new 25/250, actor_partner_id null, cross_partner true",
            ev.Count == 1 && Valid(ev[0]) && (string)ev[0]["admin_type"]! == "super" && (string)ev[0]["action"]! == "update" && (string)ev[0]["details"]!["change_kind"]! == "updated"
            && (long)ev[0]["details"]!["row_version"]! == 2 && (int)ev[0]["details"]!["old_reason_min_length"]! == 20 && (int)ev[0]["details"]!["old_reason_max_length"]! == 300
            && (int)ev[0]["details"]!["new_reason_min_length"]! == 25 && (int)ev[0]["details"]!["new_reason_max_length"]! == 250
            && ev[0]["details"]!.AsObject().ContainsKey("actor_partner_id") && ev[0]["details"]!["actor_partner_id"] is null && (bool)ev[0]["details"]!["cross_partner"]!, ev.Count > 0 ? ev[0].ToJsonString() : "none");
        await Refused2("third PUT with If-Match \"1\" (stale) -> 409 concurrency_conflict, current_row_version 2", PrbPut(P_C, PB, B(30, 200), "\"1\""),
            x => Err(x, 409, "concurrency_conflict") && (long?)Obj(x.Body)["current_row_version"] == 2, "25/250/v2");
        await Refused2("stale If-Match + oversize body -> 409 (not 413)", PrbPut(P_C, SA, "{\"pad\":\"" + new string('x', 70_000) + "\"}", "\"1\""),
            x => Err(x, 409, "concurrency_conflict"), "25/250/v2");
        await Refused2("current If-Match + invalid order -> 422 reason_bound_order, row unchanged", PrbPut(P_C, SA, B(260, 250), "\"2\""),
            x => Fs(x) == "reason_min_length:reason_bound_order", "25/250/v2");
        async Task Refused2(string label, Task<R> t, Func<R, bool> want, string snap)
        {
            var x = await t;
            Ok($"Slice A PUT {label}; no event", want(x) && await PrbSnap(P_C) == snap && (await NewEvents()).Count == 0, Tg(x));
        }
        // no-op: same values -> 200, nothing written, no event, no bump [TLD 11:23 B: explicitly approved]
        r = await PrbPut(P_C, PA, B(25, 250), "\"2\"");
        Ok("Slice A PUT no-op (same values, TLD 11:23 B) -> 200, ETag \"2\", row_version unchanged, no event; PA on another partner: no updated_by in the PUT view either", r.Status == 200 && r.ETag == "\"2\"" && (long?)Obj(r.Body)["row_version"] == 2
            && HasBy(r, null) && await PrbSnap(P_C) == "25/250/v2" && (await NewEvents()).Count == 0, Tg(r));
        if (r.Status == 200) ForSchema("partner_reason_bounds_view", r);
        // own partner: PA on P_A -> cross_partner false; 10/10 and 500/500 edges accepted
        r = await PrbPut(P_A, PA, B(10, 10), "\"0\"");
        ev = await NewEvents();
        Ok("Slice A PUT create own partner (PA on P_A, 10/10 edge, \"0\") -> 200, updated_by U_PA kept (own partner); event cross_partner false, actor_partner_id = target = P_A", r.Status == 200 && HasBy(r, U_PA) && ev.Count == 1 && Valid(ev[0])
            && !(bool)ev[0]["details"]!["cross_partner"]! && (string)ev[0]["details"]!["actor_partner_id"]! == P_A.ToString("D") && (string)ev[0]["details"]!["target_partner_id"]! == P_A.ToString("D"), Tg(r));
        r = await PrbPut(P_A, PA, B(500, 500), "\"1\"");
        Ok("Slice A PUT own partner 500/500 edge -> 200 v2", r.Status == 200 && r.ETag == "\"2\"" && await PrbSnap(P_A) == "500/500/v2" && (await NewEvents()).Count == 1, Tg(r));
        r = await PrbPut(P_A, PA, B(40, 120), "\"2\"");
        Ok("Slice A PUT own partner 40/120 -> 200 v3", r.Status == 200 && await PrbSnap(P_A) == "40/120/v3" && (await NewEvents()).Count == 1, Tg(r));
        // TLD 11:23 identity: PA own read keeps updated_by; SA read of a PA-written row keeps it; PB (another partner) does not get it
        g = await Get(Prb(P_A), PA);
        Ok("Slice A GET own partner (PA reads P_A): updated_by U_PA kept (TLD 11:23 identity)", g.Status == 200 && g.ETag == "\"3\"" && HasBy(g, U_PA), Tg(g));
        g = await Get(Prb(P_A), SA);
        Ok("Slice A GET (SA reads P_A, last written by PA): updated_by U_PA kept (TLD 11:23 identity)", g.Status == 200 && g.ETag == "\"3\"" && HasBy(g, U_PA), Tg(g));
        {
            // TLD 13:27 item 2: a future admin type (Client Admin, DEC-130) gets no access and no identity; endpoint authorization unchanged
            var CT = Token("client", U_PA, P_A);
            var gc = await Get(Prb(P_A), CT); var pc = await PrbPut(P_A, CT, B(41, 121), "\"3\"");
            Ok("Slice A client-admin token (admin_type client, partner P_A): GET and PUT refused (401/403, not widened), no updated_by / user id in either body, row 40/120/v3 unchanged, no event",
                gc.Status is 401 or 403 && pc.Status is 401 or 403 && !gc.Body.Contains("updated_by") && !pc.Body.Contains("updated_by") && !gc.Body.Contains(U_PA.ToString("D"))
                && await PrbSnap(P_A) == "40/120/v3" && (await NewEvents()).Count == 0, $"{Tg(gc)} || {Tg(pc)}");
        }
        g = await Get(Prb(P_A), PB);
        Ok("Slice A GET (PB reads P_A): bounds 40/120, ETag \"3\", row_version 3, updated_at kept, NO updated_by (TLD 11:23 identity)", g.Status == 200 && g.ETag == "\"3\"" && HasBy(g, null)
            && (long?)Obj(g.Body)["row_version"] == 3 && (int?)Obj(g.Body)["reason_min_length"] == 40 && (int?)Obj(g.Body)["reason_max_length"] == 120 && Obj(g.Body).ContainsKey("updated_at"), Tg(g));
        if (g.Status == 200) ForSchema("partner_reason_bounds_view", g);

        // ---- the resolver everywhere [TLD 08:00 Q3]: limits, location GET, location PUT reason, POST reason ----
        Ok("Slice A limits: PA (own P_A, no param) -> 40/120 (row)", Lim(await Get(LimitsP, PA), 40, 120));
        Ok($"Slice A limits: PB (own P_B, no row, no param) -> config {Mn}/{Mx}", Lim(await Get(LimitsP, PB), Mn, Mx));
        Ok($"Slice A limits: SA and org:V reader without partner_id -> config {Mn}/{Mx}", Lim(await Get(LimitsP, SA), Mn, Mx) && Lim(await Get(LimitsP, RD), Mn, Mx));
        Ok("Slice A limits: SA ?partner_id=P_C -> 25/250; PB ?partner_id=P_A -> 40/120 (cross-partner read, LS-OQ-61)", Lim(await Get($"{LimitsP}?partner_id={P_C:D}", SA), 25, 250)
            && Lim(await Get($"{LimitsP}?partner_id={P_A:D}", PB), 40, 120));
        Ok($"Slice A limits: SA ?partner_id=<unset P_F> -> config {Mn}/{Mx}", Lim(await Get($"{LimitsP}?partner_id={P_F:D}", SA), Mn, Mx));
        Ok("Slice A limits: org:V reader ?partner_id=P_A -> 404 not_found (the read check point)", Err(await Get($"{LimitsP}?partner_id={P_A:D}", RD), 404, "not_found"));
        foreach (var q in new[] { "partner_id=nope", "partner_id=", $"partner_id={Guid.Empty:D}", $"partner_id={P_A:D}&partner_id={P_C:D}" })
        {
            var lr = await Get($"{LimitsP}?{q}", SA);
            Ok($"Slice A limits: ?{q} -> 422 partner_id:partner_id_format", Fs(lr) == "partner_id:partner_id_format", Tg(lr));
        }
        Ok("Slice A limits: no role -> 403 even with partner_id; no token -> 401", Err(await Get($"{LimitsP}?partner_id={P_A:D}", NO), 403, "forbidden") && (await Get($"{LimitsP}?partner_id={P_A:D}", null)).Status == 401);
        var cr = await Post(SA, Create(P_A, "Slice A resolver", DbCfg()));
        Guid LA = cr.Status == 201 ? LocOf(cr) : Guid.Empty;
        await NewEvents();
        g = await Get(Cfg(LA), PA);
        Ok("Slice A location GET (P_A location, PA) -> reason_min_length 40, reason_max_length 120 (the partner's row)", g.Status == 200 && (int?)Obj(g.Body)["reason_min_length"] == 40 && (int?)Obj(g.Body)["reason_max_length"] == 120, Tg(g));
        if (g.Status == 200) ForSchema("login_config_get_restricted", g);
        readerLocations.Add(LA);
        g = await Get(Cfg(LA), RD);
        Ok("Slice A location GET (org:V reader of the P_A location) -> 40/120", g.Status == 200 && (int?)Obj(g.Body)["reason_min_length"] == 40 && (int?)Obj(g.Body)["reason_max_length"] == 120, Tg(g));
        readerLocations.Remove(LA);
        g = await Get(Cfg(A1), SA);
        Ok("Slice A location GET (SA) of the P_A location A1 -> 40/120 too (resolver keys on the location's partner)", g.Status == 200 && (int?)Obj(g.Body)["reason_min_length"] == 40, Tg(g));
        r = await Put(LA, PA, With(DbCfg(display: "Slice A 1"), "reason", new string('x', 39)), $"\"{Rv(LA)}\"");
        Ok("Slice A location PUT (P_A): 39-char reason -> 422 reason:reason_length (partner min 40)", HasField(r, "reason", "reason_length") && (await NewEvents()).Count == 0, Tg(r));
        r = await Put(LA, PA, With(DbCfg(display: "Slice A 2"), "reason", new string('x', 121)), $"\"{Rv(LA)}\"");
        Ok("Slice A location PUT (P_A): 121-char reason -> 422 (partner max 120)", HasField(r, "reason", "reason_length") && (await NewEvents()).Count == 0, Tg(r));
        r = await Put(LA, PA, With(DbCfg(display: "Slice A 3"), "reason", new string('x', 120)), $"\"{Rv(LA)}\"");
        ev = await NewEvents();
        Ok("Slice A location PUT (P_A): 120-char reason -> 200, stored", r.Status == 200 && ev.Count == 1 && (string?)ev[0]["details"]!["reason"] == new string('x', 120), Tg(r));
        r = await Post(SA, Create(P_A, "Slice A create short", With(DbCfg(), "reason", new string('x', 39))));
        Ok("Slice A POST (body partner P_A): 39-char reason -> 422 login_config/reason:reason_length (partner min 40)", HasField(r, "login_config/reason", "reason_length") && (await NewEvents()).Count == 0, Tg(r));
        r = await Post(SA, Create(P_A, "Slice A create ok", With(DbCfg(), "reason", new string('x', 40))));
        Ok("Slice A POST (body partner P_A): 40-char reason -> 201", r.Status == 201 && (await NewEvents()).Count == 1, Tg(r));
        r = await Post(SA, Create(P_B, "Slice A create P_B", With(DbCfg(), "reason", new string('x', Mn))));
        Ok($"Slice A POST (body partner P_B, no row): {Mn}-char reason -> 201 (config bounds)", r.Status == 201 && (await NewEvents()).Count == 1, Tg(r));

        // ---- deterministic race [TLD 08:00b(1)]: a first PUT blocked on the PK while another "first PUT" commits -> 409 + winner's version ----
        {
            await using var oc = await ownerDs.OpenConnectionAsync();
            await using var otx = await oc.BeginTransactionAsync();
            await using (var ins = new NpgsqlCommand("INSERT INTO auth.partner_reason_bounds (partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version) VALUES ($1, 33, 333, $2, now(), 1)", oc, otx))
            { ins.Parameters.Add(new() { Value = P_D }); ins.Parameters.Add(new() { Value = U_SA }); await ins.ExecuteNonQueryAsync(); }
            var racer = PrbPut(P_D, PB, B(44, 444), "\"0\"");
            var blocked = false;
            for (int i = 0; i < 100 && !blocked; i++)
            {
                await Task.Delay(100);
                blocked = await ApiWaiters() >= 1;
                if (racer.IsCompleted) break;
            }
            Ok("Slice A race: the API's first PUT is blocked on the uncommitted winner's primary key (an API-role backend in this database has a NOT granted lock)", blocked && !racer.IsCompleted);
            await otx.CommitAsync();
            var rr = await racer;
            Ok("Slice A race (TLD 08:00b(1)): the losing first PUT -> 409 concurrency_conflict, current_row_version 1 (the winner's); row is the winner's 33/333 v1; no event",
                Err(rr, 409, "concurrency_conflict") && (long?)Obj(rr.Body)["current_row_version"] == 1 && await PrbSnap(P_D) == "33/333/v1" && (await NewEvents()).Count == 0, Tg(rr));
            if (rr.Status == 409) ForSchema("prb_error_response", rr);
            var r2 = await PrbPut(P_D, PB, B(44, 444), "\"1\"");
            Ok("Slice A race: the loser retries with the reported version \"1\" -> 200 update v2, one event (change_kind updated)", r2.Status == 200 && r2.ETag == "\"2\"" && await PrbSnap(P_D) == "44/444/v2"
                && (await NewEvents()) is { Count: 1 } e2 && (string)e2[0]["details"]!["change_kind"]! == "updated", Tg(r2));
        }
        // two concurrent API first PUTs, If-Match "0" each: with "0" = expect unset the outcome no longer depends on the order [TLD 11:23 A]:
        // raced (23505) or sequential (row seen), the loser is 409 current_row_version 1; exactly one create
        {
            var a = PrbPut(P_F, SA, B(15, 150), "\"0\""); var b = PrbPut(P_F, PA, B(16, 160), "\"0\"");
            var res = await Task.WhenAll(a, b);
            var evs = await NewEvents();
            Ok("Slice A race (2 API first PUTs, If-Match \"0\" each): exactly one 200 (ETag \"1\") and one 409 concurrency_conflict current_row_version 1, no 5xx, one row at v1 = the winner's values, one create event",
                res.Count(x => x.Status == 200 && x.ETag == "\"1\"") == 1 && res.Count(x => Err(x, 409, "concurrency_conflict") && (long?)Obj(x.Body)["current_row_version"] == 1) == 1
                && await PrbSnap(P_F) == (res[0].Status == 200 ? "15/150/v1" : "16/160/v1") && evs.Count == 1 && (string?)evs[0]["details"]?["change_kind"] == "created",
                string.Join(" | ", res.Select(Tg)));
        }

        // ---- TLD 11:23 C: an explicit first PUT of the inherited default values still creates v1 + a create event ----
        {
            var r0 = await PrbPut(P_G, SA, B(Mn, Mx), "\"0\"");
            var e0 = await NewEvents();
            Ok($"Slice A PUT explicit first {Mn}/{Mx} (= the inherited defaults) on unset P_G -> 200 ETag \"1\", source partner, row {Mn}/{Mx}/v1, one create event old = new = {Mn}/{Mx} (TLD 11:23 C)",
                r0.Status == 200 && r0.ETag == "\"1\"" && (string?)Obj(r0.Body)["source"] == "partner" && await PrbSnap(P_G) == $"{Mn}/{Mx}/v1" && e0.Count == 1 && Valid(e0[0])
                && (string)e0[0]["details"]!["change_kind"]! == "created" && (int)e0[0]["details"]!["old_reason_min_length"]! == Mn && (int)e0[0]["details"]!["new_reason_max_length"]! == Mx, Tg(r0));
        }

        // ---- TLD 11:23 D: existing-row parallel update. Two DIFFERENT changes from the same version; a harness row lock (SELECT ... FOR UPDATE,
        // rolled back; writes nothing) holds both API requests until both are waiting, then releases them together ----
        {
            var c0 = await PrbPut(P_H, SA, B(20, 200), "\"0\"");
            await NewEvents();
            Ok("Slice A parallel update setup: P_H created at v1", c0.Status == 200 && await PrbSnap(P_H) == "20/200/v1", Tg(c0));
            await using var oc = await ownerDs.OpenConnectionAsync();
            await using var otx = await oc.BeginTransactionAsync();
            await using (var lk = new NpgsqlCommand("SELECT 1 FROM auth.partner_reason_bounds WHERE partner_id = $1 FOR UPDATE", oc, otx))
            { lk.Parameters.Add(new() { Value = P_H }); await lk.ExecuteScalarAsync(); }
            var ua = PrbPut(P_H, SA, B(30, 300), "\"1\""); var ub = PrbPut(P_H, PA, B(40, 400), "\"1\"");
            var both = false;
            for (int i = 0; i < 100 && !both; i++)
            {
                await Task.Delay(100);
                both = await ApiWaiters() >= 2;
                if (ua.IsCompleted || ub.IsCompleted) break;
            }
            Ok("Slice A parallel update: both API PUTs are waiting on the row at the same time (2 distinct API-role backends in this database with a NOT granted lock), neither answered yet", both && !ua.IsCompleted && !ub.IsCompleted);
            await otx.RollbackAsync();
            var res = await Task.WhenAll(ua, ub);
            var evs = await NewEvents();
            var win = res.Count(x => x.Status == 200) == 1 ? (res[0].Status == 200 ? "30/300/v2" : "40/400/v2") : "?";
            Ok("Slice A parallel update (TLD 11:23 D): exactly one 200 (ETag \"2\"), the other 409 concurrency_conflict current_row_version 2 (v+1), no 5xx, row = the winner's values at v2, one updated event",
                res.Count(x => x.Status == 200 && x.ETag == "\"2\"") == 1 && res.Count(x => Err(x, 409, "concurrency_conflict") && (long?)Obj(x.Body)["current_row_version"] == 2) == 1
                && await PrbSnap(P_H) == win && evs.Count == 1 && (string?)evs[0]["details"]?["change_kind"] == "updated" && (long?)evs[0]["details"]?["row_version"] == 2,
                string.Join(" | ", res.Select(Tg)));
            // ---- max-only update (min unchanged) is a real change, not a no-op: v2 -> v3 + one event ----
            var mn = win.StartsWith("30/") ? 30 : 40;
            var mo = await PrbPut(P_H, SA, B(mn, 450), "\"2\"");
            var emo = await NewEvents();
            Ok("Slice A PUT max-only change (same min, max -> 450) -> 200 ETag \"3\", row max 450 at v3, one event old max -> new max 450 (not a no-op)",
                mo.Status == 200 && mo.ETag == "\"3\"" && await PrbSnap(P_H) == $"{mn}/450/v3" && emo.Count == 1 && (int)emo[0]["details"]!["new_reason_max_length"]! == 450
                && (int)emo[0]["details"]!["new_reason_min_length"]! == mn, Tg(mo));
        }

        // ---- fail closed: outbox write fails -> 500, no row, no event (same transaction) ----
        {
            var rejectAll = Path.Combine(Path.GetTempPath(), $"sa-reject-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(rejectAll, """{"type":"string"}""");
            var portF = FreePort(port);
            await using var appF = await AdminApiHost.BuildAsync(Opts(schema: rejectAll) with { Urls = $"http://127.0.0.1:{portF}" }, dir, extraLogger: new CaptureLogger());
            await appF.StartAsync();
            using var hF = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{portF}") };
            var f1 = await PrbPut(P_E, SA, B(20, 300), "\"0\"", hF);
            var f2 = await PrbPut(P_C, PA, B(26, 260), "\"2\"", hF);
            Ok("Slice A fail closed: create and update with an outbox failure -> 500, no body; no P_E row, P_C unchanged, no event",
                f1.Status == 500 && f1.Body == "" && f2.Status == 500 && f2.Body == "" && await PrbSnap(P_E) == "<none>" && await PrbSnap(P_C) == "25/250/v2" && (await NewEvents()).Count == 0,
                $"{Tg(f1)} || {Tg(f2)}");
            await appF.StopAsync();
            File.Delete(rejectAll);
        }

        // ---- the 50/200 config host: unset partner GET and the resolver default follow the config [TLD 08:00 Q3] ----
        {
            var port5 = FreePort(port);
            await using var app5 = await AdminApiHost.BuildAsync(Opts() with { Urls = $"http://127.0.0.1:{port5}", ReasonMinLength = 50, ReasonMaxLength = 200 }, dir, extraLogger: new CaptureLogger());
            await app5.StartAsync();
            using var h5 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port5}") };
            var d5 = await Send(HttpMethod.Get, Prb(P_E), SA, via: h5);
            Ok("Slice A config 50/200: GET unset partner -> 50/200 source default, no ETag", d5.Status == 200 && d5.ETag is null && (int?)Obj(d5.Body)["reason_min_length"] == 50
                && (int?)Obj(d5.Body)["reason_max_length"] == 200 && (string?)Obj(d5.Body)["source"] == "default", Tg(d5));
            Ok("Slice A config 50/200: a set partner (P_A) still answers its row 40/120 (row wins over config)", Lim(await Send(HttpMethod.Get, LimitsP, PA, via: h5), 40, 120)
                && Lim(await Send(HttpMethod.Get, $"{LimitsP}?partner_id={P_E:D}", SA, via: h5), 50, 200));
            var c5 = await PrbPut(P_E, SA, B(60, 70), "\"0\"", h5);
            var e5 = await NewEvents();
            Ok("Slice A config 50/200: create on P_E -> event old = 50/200 (the config in force), new 60/70", c5.Status == 200 && e5.Count == 1 && Valid(e5[0])
                && (int)e5[0]["details"]!["old_reason_min_length"]! == 50 && (int)e5[0]["details"]!["old_reason_max_length"]! == 200, Tg(c5));
            await app5.StopAsync();
        }

        // ---- 08:00b(2): the config must satisfy 10 <= min <= max <= 500 or the API refuses to start (unchanged rule, tagged here) ----
        Ok("Slice A 08:00b(2) startup: 10/500 and 50/200 accepted; 9/500, 10/501, 300/200 refused (fail fast, same message as DEC-107)",
            ReasonCfgAccepted(10, 500) && ReasonCfgAccepted(50, 200) && !ReasonCfgAccepted(9, 500) && !ReasonCfgAccepted(10, 501) && !ReasonCfgAccepted(300, 200));
        await Check("Slice A 08:00b(2) startup: Admin.Api process exits 2 on LoginConfig 600/700 (outside 10..500)", async () =>
        {
            var (code, err) = await RunAdminApi(e => { e["LoginConfig__ReasonMinLength"] = "600"; e["LoginConfig__ReasonMaxLength"] = "700"; });
            return code == 2 && err.Contains("must satisfy 10 <= min <= max <= 500");
        });

        // ---- DB CHECK stays a hard 10..500 [DEC-114] (owner probe in a rolled-back transaction) ----
        await Check("Slice A DB: CHECK prb_bounds refuses 9/500 and 10/501 even for the owner (rolled back)", async () =>
        {
            async Task<bool> Refuses(int mn, int mx)
            {
                await using var oc = await ownerDs.OpenConnectionAsync();
                await using var otx = await oc.BeginTransactionAsync();
                try
                {
                    await using var ins = new NpgsqlCommand("INSERT INTO auth.partner_reason_bounds VALUES ($1, $2, $3, $4, now(), 1)", oc, otx);
                    ins.Parameters.Add(new() { Value = Guid.NewGuid() }); ins.Parameters.Add(new() { Value = mn }); ins.Parameters.Add(new() { Value = mx }); ins.Parameters.Add(new() { Value = U_SA });
                    await ins.ExecuteNonQueryAsync(); return false;
                }
                catch (PostgresException x) { return x.SqlState == PostgresErrorCodes.CheckViolation; }
                finally { await otx.RollbackAsync(); }
            }
            return await Refuses(9, 500) && await Refuses(10, 501) && await Refuses(300, 200);
        });
        Ok("Slice A: every reason-bounds event was schema-valid and the outbox has no reason key on them", allEvents.Where(e => (string?)e["event_type"] == "auth.partner_reason_bounds.changed").All(e => Valid(e) && !e["details"]!.AsObject().ContainsKey("reason"))
            && allEvents.Count(e => (string?)e["event_type"] == "auth.partner_reason_bounds.changed") >= 8);
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
// [SSE2 review 3fac1d52 Info, MD4b] Any other exception out of the inline check sequence (e.g. a null dereference after a mutant's
// empty 201) is one FAIL with its type; cleanup still runs and the run ends with the "N passed, M failed" line, never an unhandled crash.
catch (Exception e)
{
    Ok("harness: check sequence completed (an exception stopped it; the remaining checks did not run)", false, $"{e.GetType().Name}: {e.Message}");
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
    Ok("cleanup: auth.audit_outbox, change, llc, location, partner_reason_bounds empty again", left == 0);
}
uiHostDone:
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
        // Slice A: it does not carry PartnerReasonBoundsWrite either; Run's normalisation would clear it anyway (non-built-in role).
        : id.UserId == custom ? Task.FromResult(AdminGrant.Nothing with { LoginMethodWrite = true, OrgCreateEdit = true })
        : _interim.ResolveAsync(id, ct);
}

/// <summary>Captures the API's own log lines (formatted), so the checks can read the 500 error line [TLD 9:20].
/// From Warning when asked (P3-7/P3-8: one Warning line, no exception object, no stack trace).</summary>
sealed class CaptureLogger(LogLevel min = LogLevel.Error) : ILoggerProvider, ILogger
{
    public sealed record Entry(LogLevel Level, string Text, bool HasException, string? ExType = null);
    readonly List<Entry> _entries = new();
    public IReadOnlyList<string> Lines { get { lock (_entries) return _entries.Select(e => e.Text).ToList(); } }
    public IReadOnlyList<Entry> Entries { get { lock (_entries) return _entries.ToList(); } }
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= min;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { if (IsEnabled(logLevel)) lock (_entries) _entries.Add(new(logLevel, formatter(state, exception), exception is not null, exception?.GetType().Name)); }
    public void Dispose() { }
}
