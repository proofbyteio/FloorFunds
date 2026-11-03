using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Login.StandIn;

// contract 3.1, 3.3: RSA keys made in memory at each start, never written anywhere; kid starts "standin-".
public sealed class SigningKeys : IDisposable
{
    public RSA Rsa { get; } = RSA.Create(2048);
    public string Kid { get; } = "standin-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    public void Dispose() => Rsa.Dispose();
}

public static class Discovery
{
    // The bytes are built once, so the front and 8084 copies are identical (contract 2.1/2.9; TL 9:50 PM reverted the per-listener split).
    // [v1.1 for SA] SSE2 F12: token/revocation URLs listed here 404 on the front listener; how URLs map front <-> back is open.
    public static (byte[] Disco, byte[] Jwks) Build(string issuer, SigningKeys keys)
    {
        // contract 2.1 + 2.4: exactly this key set. 2.3: every URL is under the public issuer.
        var disco = new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = issuer + "/connect/authorize",
            ["token_endpoint"] = issuer + "/connect/token",
            ["revocation_endpoint"] = issuer + "/connect/revoke",
            ["end_session_endpoint"] = issuer + "/connect/endsession",
            ["jwks_uri"] = issuer + "/.well-known/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "private_key_jwt" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["subject_types_supported"] = new[] { "public" },
            ["token_endpoint_auth_signing_alg_values_supported"] = new[] { "RS256" },
            ["revocation_endpoint_auth_methods_supported"] = new[] { "client_secret_basic", "private_key_jwt" },
            ["authorization_response_iss_parameter_supported"] = true,
        };
        var p = keys.Rsa.ExportParameters(false);
        var jwks = new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = keys.Kid,
            n = WebEncoders.Base64UrlEncode(p.Modulus!), e = WebEncoders.Base64UrlEncode(p.Exponent!) } } };
        return (JsonSerializer.SerializeToUtf8Bytes(disco), JsonSerializer.SerializeToUtf8Bytes(jwks));
    }

    public static void Map(WebApplication app, string issuerPath, byte[] disco, byte[] jwks)
    {
        // contract 2.8, 3.4: cache headers.
        app.MapGet(issuerPath + "/.well-known/openid-configuration", (HttpContext c) =>
        { c.Response.Headers.CacheControl = "public, max-age=3600"; return Results.Bytes(disco, "application/json"); });
        app.MapGet(issuerPath + "/.well-known/jwks", (HttpContext c) =>
        { c.Response.Headers.CacheControl = "public, max-age=300"; return Results.Bytes(jwks, "application/json"); });
    }
}
