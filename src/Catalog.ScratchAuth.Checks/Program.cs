using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Catalog.ScratchAuth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

Console.WriteLine("Catalog.ScratchAuth.Checks: synthetic in-process tokens only. Not a listener, database, credential, or acceptance.");

var pass = 0;
var fail = new List<string>();
void Check(string name, bool ok)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (ok) pass++;
    else
    {
        fail.Add(name);
        Console.WriteLine("  status=" + Captured.StatusCode + " body=" + Captured.Body);
    }
}

var root = RepoRoot();
Check("frozen audit schema hash", Hash(Path.Combine(root, "contract", "audit-events.schema.json")) == "ce0b904fcc5069d9e727e62600efabca311c8a407297ed5dbc96f605ae807b1b");
Check("frozen errors.md hash", Hash(Path.Combine(root, "contract", "errors.md")) == "dd15977e50be88364fa758c8c8c32618539ddfaed43e31205e4b9b65617cd80b");
Check("frozen claims schema hash", Hash(Path.Combine(root, "contract", "claims.schema.json")) == "c792c384eebf198db22d61ca241f43bb3efbbfea1c517ea9ccb0f5632a3d6972");
// REQ-064 moved this pin. AdminAuth.cs now rejects an unknown kid and a duplicate JSON member,
// the same two checks FrozenAdminTokenGate already enforced. The behavioral checks below still cover the gate.
Check("frozen admin token validator hash", Hash(Path.Combine(root, "src", "Admin.Api", "AdminAuth.cs")) == "510bfb20989aa89df263c8601a9d9c1187c3b4b9268a8a09c6d6b68e4b174b3c");
Check("accepted catalog transport hash", Hash(Path.Combine(root, "src", "Catalog.Transport", "CatalogHttpTransport.cs")) == "e5004a949dba148706bfdaf525245d429232aff5298e1aac22b66fdf3a07d060");

var scratchText = string.Join("\n", Directory.GetFiles(Path.Combine(root, "src", "Catalog.ScratchAuth"), "*.cs").Select(File.ReadAllText));
var scratchProject = File.ReadAllText(Path.Combine(root, "src", "Catalog.ScratchAuth", "Catalog.ScratchAuth.csproj"));
Check("scratch boundary does not reference a database, outbox, listener, or admin host",
    !scratchText.Contains("Npgsql", StringComparison.Ordinal)
    && !scratchText.Contains("audit_outbox", StringComparison.Ordinal)
    && !scratchText.Contains("TcpListener", StringComparison.Ordinal)
    && !scratchText.Contains("HttpListener", StringComparison.Ordinal)
    && !scratchText.Contains("UseKestrel", StringComparison.Ordinal)
    && !scratchText.Contains("WebApplication", StringComparison.Ordinal)
    && !scratchText.Contains("Admin.Api", StringComparison.Ordinal)
    && !scratchText.Contains("Auth.Data", StringComparison.Ordinal)
    && !scratchProject.Contains("Auth.Data", StringComparison.Ordinal)
    && scratchProject.Contains("Microsoft.IdentityModel.JsonWebTokens", StringComparison.Ordinal));

const string Issuer = "https://login.scratch.invalid/login";
var user = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
var partner = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
var forged = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
using var rsa = RSA.Create(2048);
using var otherRsa = RSA.Create(2048);
var signKey = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = "scratch-synthetic-1" };
var verifyKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "scratch-synthetic-1" };
var otherKey = new RsaSecurityKey(otherRsa.ExportParameters(true)) { KeyId = "scratch-synthetic-2" };
var minter = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
var host = new CatalogScratchBoundary(Issuer, verifyKey);

string Mint(Action<Dictionary<string, object>>? edit = null, SecurityKey? key = null, string typ = "at+jwt", string? admin = "super", Guid? owner = null)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var claims = new Dictionary<string, object>
    {
        ["iss"] = Issuer,
        ["aud"] = FrozenAdminTokenGate.Audience,
        ["sub"] = user.ToString("D"),
        ["admin_type"] = admin ?? "",
        ["iat"] = now,
        ["nbf"] = now,
        ["exp"] = now + 300,
        ["jti"] = Guid.NewGuid().ToString("D"),
        ["scope"] = "backoffice_api",
        ["client_id"] = "backoffice_stub",
        ["login_provider"] = "database",
        ["amr"] = new List<string> { "pwd", "otp", "mfa" },
        ["auth_time"] = now,
        ["session_kind"] = "normal",
    };
    if (admin is null)
        claims.Remove("admin_type");
    if (owner is { } partnerId)
        claims["partner_id"] = partnerId.ToString("D");
    edit?.Invoke(claims);
    return minter.CreateToken(new SecurityTokenDescriptor
    {
        Claims = claims,
        SigningCredentials = new SigningCredentials(key ?? signKey, SecurityAlgorithms.RsaSha256),
        TokenType = typ,
    });
}

var super = Mint();
var partnerToken = Mint(admin: "partner", owner: partner);
Send("GET", "/admin/catalogs/fee_type/entries", null, null);
Check("a missing token is 401 and writes nothing", Denied(Captured.Body, Captured.StatusCode, ""));
Send("GET", "/admin/catalogs/fee_type/entries", null, null, ("Authorization", "Basic c2NyYXRjaA=="));
Check("basic authentication is not a bearer token", Denied(Captured.Body, Captured.StatusCode, "c2NyYXRjaA=="));
Send("GET", "/admin/catalogs/fee_type/entries", null, "");
Check("an empty bearer is 401", Captured.StatusCode == 401 && Captured.Body.Length == 0 && Quiet());
Send("GET", "/admin/catalogs/fee_type/entries", null, null, ("Authorization", "Bearer " + super), ("Authorization", "Bearer " + partnerToken));
Check("two authorization headers are 401", Denied(Captured.Body, Captured.StatusCode, super) && Denied(Captured.Body, Captured.StatusCode, partnerToken));
Send("GET", "/admin/catalogs/fee_type/entries", null, "not-a-token");
Check("a malformed token is 401", Denied(Captured.Body, Captured.StatusCode, "not-a-token"));
var wrongKey = Mint(key: otherKey);
Send("GET", "/admin/catalogs/fee_type/entries", null, wrongKey);
Check("a token signed by another key is 401", Denied(Captured.Body, Captured.StatusCode, wrongKey));
var acceptedParts = super.Split('.');
var acceptedHeader = Base64UrlEncoder.Decode(acceptedParts[0]);
var acceptedPayload = Base64UrlEncoder.Decode(acceptedParts[1]);
const string secondSubject = "22222222-2222-4222-8222-222222222222";
var duplicateSub = Resign(acceptedHeader, acceptedPayload[..^1] + ",\"sub\":\"" + secondSubject + "\"}");
Send("GET", "/admin/catalogs/fee_type/entries", null, duplicateSub);
Check("a duplicated sub member is 401",
    acceptedPayload.EndsWith('}') && Denied(Captured.Body, Captured.StatusCode, duplicateSub) && !Captured.Raw.Contains(secondSubject, StringComparison.Ordinal));
var duplicateKidHeader = acceptedHeader.Replace("\"kid\":\"scratch-synthetic-1\"", "\"kid\":\"scratch-synthetic-1\",\"kid\":\"scratch-synthetic-1\"", StringComparison.Ordinal);
var duplicateKid = Resign(duplicateKidHeader, acceptedPayload);
Send("GET", "/admin/catalogs/fee_type/entries", null, duplicateKid);
Check("a duplicated header kid is 401",
    duplicateKidHeader.Contains("\"kid\":\"scratch-synthetic-1\",\"kid\":\"scratch-synthetic-1\"", StringComparison.Ordinal)
    && Denied(Captured.Body, Captured.StatusCode, duplicateKid));
var unknownKid = Resign("{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"kid\":\"not-the-scratch-key\"}", acceptedPayload);
Send("GET", "/admin/catalogs/fee_type/entries", null, unknownKid);
Check("an unknown kid on the configured scratch key is 401", Denied(Captured.Body, Captured.StatusCode, unknownKid));
var unsigned = Unsigned();
Send("GET", "/admin/catalogs/fee_type/entries", null, unsigned);
Check("an unsigned token is 401", Denied(Captured.Body, Captured.StatusCode, unsigned));
var expired = Mint(claims =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    claims["iat"] = now - 331;
    claims["nbf"] = now - 331;
    claims["auth_time"] = now - 331;
    claims["exp"] = now - 31;
});
Send("GET", "/admin/catalogs/fee_type/entries", null, expired);
Check("a token 31 seconds past exp is 401", Denied(Captured.Body, Captured.StatusCode, expired));
var notYet = Mint(claims =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    claims["nbf"] = now + 60;
    claims["exp"] = now + 360;
});
Send("GET", "/admin/catalogs/fee_type/entries", null, notYet);
Check("a token whose nbf is outside the 30 second skew is 401", Denied(Captured.Body, Captured.StatusCode, notYet));
var wrongAudience = Mint(claims => claims["aud"] = "cash-advance-api");
Send("GET", "/admin/catalogs/fee_type/entries", null, wrongAudience);
Check("a cash-advance audience is 401 on this backoffice boundary", Denied(Captured.Body, Captured.StatusCode, wrongAudience));
var idToken = Mint(typ: "JWT");
Send("GET", "/admin/catalogs/fee_type/entries", null, idToken);
Check("an ID token presented as a bearer is 401", Denied(Captured.Body, Captured.StatusCode, idToken));
var wrongIssuer = Mint(claims => claims["iss"] = "https://login.other.invalid/login");
Send("GET", "/admin/catalogs/fee_type/entries", null, wrongIssuer);
Check("a different issuer is 401", Denied(Captured.Body, Captured.StatusCode, wrongIssuer));
var missingAdmin = Mint(admin: null);
Send("GET", "/admin/catalogs/fee_type/entries", null, missingAdmin);
Check("a token without admin_type is 401", Denied(Captured.Body, Captured.StatusCode, missingAdmin));
var otherAdmin = Mint(claims => claims["admin_type"] = "cashier");
Send("GET", "/admin/catalogs/fee_type/entries", null, otherAdmin);
Check("an admin_type outside super or partner is 401", Denied(Captured.Body, Captured.StatusCode, otherAdmin));
var partnerMissing = Mint(admin: "partner");
Send("GET", "/admin/catalogs/fee_type/entries", null, partnerMissing);
Check("a partner token without partner_id is 401", Denied(Captured.Body, Captured.StatusCode, partnerMissing));
var superWithPartner = Mint(owner: partner);
Send("GET", "/admin/catalogs/fee_type/entries", null, superWithPartner);
Check("a super token with partner_id is 401", Denied(Captured.Body, Captured.StatusCode, superWithPartner));
var located = Mint(claims => claims["location_id"] = forged.ToString("D"));
Send("GET", "/admin/catalogs/fee_type/entries", null, located);
Check("a backoffice token with location_id is 401", Denied(Captured.Body, Captured.StatusCode, located) && !Captured.Body.Contains(forged.ToString("D"), StringComparison.Ordinal));
var recovery = Mint(claims => claims["session_kind"] = "recovery_reenroll");
Send("GET", "/admin/catalogs/fee_type/entries", null, recovery);
Check("a recovery_reenroll token is 401", Denied(Captured.Body, Captured.StatusCode, recovery));
var roleClaim = Mint(claims => claims["role"] = "SuperAdmin");
Send("GET", "/admin/catalogs/fee_type/entries", null, roleClaim);
Check("a role claim is 401", Denied(Captured.Body, Captured.StatusCode, roleClaim) && !Captured.Raw.Contains("SuperAdmin", StringComparison.Ordinal));
var missingScope = Mint(claims => claims.Remove("scope"));
Send("GET", "/admin/catalogs/fee_type/entries", null, missingScope);
Check("a token missing the required scope claim is 401", Denied(Captured.Body, Captured.StatusCode, missingScope));
var scopeArray = Mint(claims => claims["scope"] = new List<string> { "backoffice_api" });
Send("GET", "/admin/catalogs/fee_type/entries", null, scopeArray);
Check("a scope array is 401", Denied(Captured.Body, Captured.StatusCode, scopeArray));
var badSub = Mint(claims => claims["sub"] = "not-a-uuid");
Send("GET", "/admin/catalogs/fee_type/entries", null, badSub);
Check("a subject that is not a UUID is 401", Denied(Captured.Body, Captured.StatusCode, badSub));
var huge = Mint(claims => claims["jti"] = new string('a', 9000));
Send("GET", "/admin/catalogs/fee_type/entries", null, huge);
Check("a token longer than 8192 characters is 401", huge.Length > 8192 && Denied(Captured.Body, Captured.StatusCode, huge));

var fresh = Mint(claims =>
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    claims["iat"] = now - 310;
    claims["nbf"] = now - 310;
    claims["auth_time"] = now - 310;
    claims["exp"] = now - 10;
});
Send("GET", "/admin/catalogs/fee_type/entries", null, fresh);
Check("a token inside the 30 second skew is authenticated and still has no catalog grant",
    Captured.StatusCode == 403 && Field("code") == "no_management_grant" && Field("wrote") == "false" && !Captured.Raw.Contains(fresh, StringComparison.Ordinal) && Quiet());

var unknownScope = Mint(claims => claims["scope"] = "not_in_oq6");
Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", unknownScope, ("Idempotency-Key", "unknown-scope"));
Check("an unknown scope value is not turned into a resource-server 401",
    Captured.StatusCode == 403 && Field("code") == "no_management_grant" && Field("wrote") == "false" && Quiet());

Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", super, ("Idempotency-Key", "super-create"));
Check("a validated super token is not promoted to a catalog management role",
    Captured.StatusCode == 403 && Field("code") == "no_management_grant" && Field("wrote") == "false" && Quiet());
Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", partnerToken, ("Idempotency-Key", "partner-create"));
Check("a validated partner token does not become a partner-admin write",
    Captured.StatusCode == 403 && Field("code") == "no_management_grant" && !Captured.Body.Contains(partner.ToString("D"), StringComparison.Ordinal) && Quiet());
Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\",\"owner_partner_id\":\"" + forged.ToString("D") + "\"}", super, ("Idempotency-Key", "forged-body"));
Check("a forged owner in the body is rejected and stored nowhere",
    Captured.StatusCode == 400 && Field("code") == "authority_field_rejected" && !Captured.Body.Contains(forged.ToString("D"), StringComparison.Ordinal) && Quiet());
Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", super, ("Idempotency-Key", "forged-header"), ("X-Actor-User-Id", forged.ToString("D")));
Check("a forged actor header does not become the principal",
    Captured.StatusCode == 400 && Field("code") == "unknown_header" && Field("detail") == "x-actor-user-id" && !Captured.Body.Contains(forged.ToString("D"), StringComparison.Ordinal) && Quiet());
Send("POST", "/admin/catalogs/fee_type/entries", "{\"code\":\"late\",\"display_name\":\"Late\"}", partnerToken, ("Idempotency-Key", "forged-tenant"), ("X-Partner-Id", forged.ToString("D")));
Check("a forged partner header does not retarget the tenant",
    Captured.StatusCode == 400 && Field("code") == "unknown_header" && !Captured.Body.Contains(forged.ToString("D"), StringComparison.Ordinal) && !Captured.Body.Contains(partner.ToString("D"), StringComparison.Ordinal) && Quiet());
var standIn = Mint(claims => claims["x_standin"] = true);
Send("GET", "/admin/catalogs/fee_type/entries", null, standIn);
Check("a signed stand-in marker does not bypass the catalog grant",
    Captured.StatusCode == 403 && Field("code") == "no_management_grant" && Quiet());

Console.WriteLine("RESULT pass=" + pass + " fail=" + fail.Count);
if (fail.Count > 0)
{
    Console.WriteLine(string.Join(", ", fail));
    Environment.ExitCode = 1;
}

void Send(string method, string path, string? json, string? token, params (string Name, string Value)[] extra)
{
    var builder = new StringBuilder();
    builder.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\nHost: catalog.scratch.invalid\r\n");
    if (token is not null)
        builder.Append("Authorization: Bearer ").Append(token).Append("\r\n");
    if (json is not null)
    {
        builder.Append("Content-Type: application/json\r\nContent-Length: ").Append(Encoding.UTF8.GetByteCount(json).ToString()).Append("\r\n");
    }
    foreach (var header in extra)
        builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
    builder.Append("\r\n");
    if (json is not null)
        builder.Append(json);
    var response = host.Handle(builder.ToString());
    Captured.StatusCode = response.StatusCode;
    Captured.Body = response.Body;
    Captured.Raw = response.Raw;
    Captured.Headers = response.Headers;
}

bool Denied(string body, int status, string token) =>
    status == 401
    && body.Length == 0
    && Captured.Headers["WWW-Authenticate"] == "Bearer error=\"invalid_token\""
    && !Captured.Raw.Contains("error_description", StringComparison.Ordinal)
    && (token.Length == 0 || !Captured.Raw.Contains(token, StringComparison.Ordinal))
    && Quiet();

bool Quiet() => host.Repository.EntryCount == 0 && host.Repository.HistoryCount == 0 && host.Repository.IntentCount == 0 && host.Repository.IdempotencyCount == 0;

string Field(string name)
{
    using var document = JsonDocument.Parse(Captured.Body);
    var value = document.RootElement.GetProperty(name);
    return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
}

string Resign(string headerJson, string payloadJson)
{
    var header = Base64UrlEncoder.Encode(headerJson);
    var payload = Base64UrlEncoder.Encode(payloadJson);
    var input = Encoding.ASCII.GetBytes(header + "." + payload);
    var signature = rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    return header + "." + payload + "." + Base64UrlEncoder.Encode(signature);
}

string Unsigned()
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"at+jwt\",\"kid\":\"scratch-synthetic-1\"}");
    var payload = Base64UrlEncoder.Encode("{\"iss\":\"" + Issuer + "\",\"aud\":\"backoffice-api\",\"sub\":\"" + user.ToString("D")
        + "\",\"admin_type\":\"super\",\"iat\":" + now + ",\"nbf\":" + now + ",\"exp\":" + (now + 300)
        + ",\"jti\":\"" + Guid.NewGuid().ToString("D") + "\",\"scope\":\"backoffice_api\",\"client_id\":\"backoffice_stub\",\"login_provider\":\"database\",\"amr\":[\"pwd\"],\"auth_time\":"
        + now + ",\"session_kind\":\"normal\"}");
    return header + "." + payload + ".";
}

static string Hash(string path)
{
    var bytes = File.ReadAllBytes(path);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Catalog.ScratchAuth", "CatalogScratchBoundary.cs")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Repository root was not found from the check output directory.");
}

static class Captured
{
    public static int StatusCode { get; set; }
    public static string Body { get; set; } = "";
    public static string Raw { get; set; } = "";
    public static IReadOnlyDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
}
