// DEC-213. Verifies an access token and opens one database session for its jti.
// Credentials and signing keys are read from this process only.
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var listen = args.Length > 0 ? args[0] : "http://127.0.0.1:8766";
var connectionString = Environment.GetEnvironmentVariable("ISSUER_CONNECTION_STRING");
var iss = Environment.GetEnvironmentVariable("ISSUER_ISS");
var jwksPath = Environment.GetEnvironmentVariable("ISSUER_JWKS");
if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(iss) || string.IsNullOrEmpty(jwksPath))
{
    Console.Error.WriteLine("issuer: ISSUER_CONNECTION_STRING, ISSUER_ISS and ISSUER_JWKS are required");
    return 2;
}

var keys = new JsonWebKeySet(File.ReadAllText(jwksPath)).GetSigningKeys().ToList();
if (keys.Count == 0 || keys.Any(k => k is not (RsaSecurityKey or JsonWebKey { Kty: "RSA" })))
{
    Console.Error.WriteLine("issuer: ISSUER_JWKS must hold at least one RSA key");
    return 2;
}
var kids = keys.Select(k => k.KeyId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
var parameters = new TokenValidationParameters
{
    ValidIssuer = iss,
    IssuerSigningKeys = keys,
    ValidAudience = "backoffice-api",
    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
    ValidTypes = ["at+jwt"],
    RequireExpirationTime = true,
    RequireSignedTokens = true,
    ValidateLifetime = true,
    ValidateIssuerSigningKey = true,
    ClockSkew = TimeSpan.FromSeconds(30),
};
var handler = new JsonWebTokenHandler { MapInboundClaims = false };
await using var db = NpgsqlDataSource.Create(connectionString);
var cache = new ConcurrentDictionary<Guid, string>();
var gates = new ConcurrentDictionary<Guid, SemaphoreSlim>();

var listener = new HttpListener();
listener.Prefixes.Add(listen.TrimEnd('/') + "/");
listener.Start();
Console.WriteLine("issuer: listening");
Console.Out.Flush();

while (listener.IsListening)
{
    var ctx = await listener.GetContextAsync();
    try { await Handle(ctx); }
    catch (Exception)
    {
        try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
    }
}
return 0;

async Task Handle(HttpListenerContext ctx)
{
    if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url?.AbsolutePath != "/v1/session")
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        return;
    }
    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
    var raw = await reader.ReadToEndAsync();
    if (raw.Length is 0 or > 8192 || await TryAccept(raw) is not { } accepted)
    {
        ctx.Response.StatusCode = 401;
        ctx.Response.Close();
        return;
    }
    var gate = gates.GetOrAdd(accepted.Jti, _ => new SemaphoreSlim(1, 1));
    await gate.WaitAsync();
    string session;
    try
    {
        if (cache.TryGetValue(accepted.Jti, out var cached))
            session = cached;
        else
        {
            try { session = await Open(accepted.Token.Issuer, accepted.User.ToString("D"), accepted.Jti, accepted.Exp); }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "23505")
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == "54000")
            {
                ctx.Response.StatusCode = 503;
                ctx.Response.Close();
                return;
            }
            cache[accepted.Jti] = session;
        }
    }
    finally { gate.Release(); }

    var body = JsonSerializer.Serialize(new Dictionary<string, string?>
    {
        ["session"] = session,
        ["iss"] = accepted.Token.Issuer,
        ["sub"] = accepted.User.ToString("D"),
        ["admin_type"] = accepted.AdminType,
        ["partner_id"] = accepted.Partner?.ToString("D"),
    });
    var bytes = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
}

async Task<Accepted?> TryAccept(string raw)
{
    TokenValidationResult result;
    try { result = await handler.ValidateTokenAsync(raw, parameters); }
    catch (Exception) { return null; }
    if (!result.IsValid || result.SecurityToken is not JsonWebToken t) return null;
    if (!HeaderAccepted(t)) return null;
    if (t.Audiences.Count() != 1 || t.TryGetPayloadValue<object>("location_id", out _)) return null;
    if (t.TryGetPayloadValue<object>("aud", out var audRaw) && audRaw is not string) return null;
    if (!t.TryGetPayloadValue<string>("sub", out var sub) || !Guid.TryParseExact(sub, "D", out var user)) return null;
    if (!t.TryGetPayloadValue<string>("admin_type", out var adminType) || adminType is not ("super" or "partner")) return null;
    if (t.TryGetPayloadValue<string>("session_kind", out var sk) && sk == "recovery_reenroll") return null;
    if (!t.TryGetPayloadValue<string>("jti", out var jtiText) || !Guid.TryParseExact(jtiText, "D", out var jti)) return null;
    var hasPartner = t.TryGetPayloadValue<object>("partner_id", out _);
    Guid? partner = null;
    if (adminType == "partner")
    {
        if (!t.TryGetPayloadValue<string>("partner_id", out var ps) || !Guid.TryParseExact(ps, "D", out var pid) || pid == Guid.Empty) return null;
        partner = pid;
    }
    else if (hasPartner) return null;
    return new Accepted(t, user, jti, DateTime.SpecifyKind(t.ValidTo, DateTimeKind.Utc), adminType, partner);
}

bool HeaderAccepted(JsonWebToken t)
{
    try
    {
        var headerJson = Base64UrlEncoder.Decode(t.EncodedHeader);
        using var header = JsonDocument.Parse(headerJson, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
        if (!header.RootElement.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String) return false;
        if (!kids.Contains(kid.GetString() ?? "")) return false;
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(t.EncodedPayload), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
        return payload.RootElement.ValueKind == JsonValueKind.Object;
    }
    catch (JsonException) { return false; }
    catch (Exception) { return false; }
}

async Task<string> Open(string tokenIss, string sub, Guid jti, DateTime exp)
{
    await using var c = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT auth.open_session($1, $2, $3, $4)", c);
    cmd.Parameters.Add(new NpgsqlParameter { Value = tokenIss });
    cmd.Parameters.Add(new NpgsqlParameter { Value = sub });
    cmd.Parameters.Add(new NpgsqlParameter { Value = jti });
    cmd.Parameters.Add(new NpgsqlParameter { Value = exp });
    return (string)(await cmd.ExecuteScalarAsync())!;
}

sealed record Accepted(JsonWebToken Token, Guid User, Guid Jti, DateTime Exp, string AdminType, Guid? Partner);
