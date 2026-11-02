using System.Text;
using System.Text.Json;

namespace Cachd.Login.TestKit;

/// <summary>Decodes (does not verify) a compact JWS so tests can inspect claims. Verification is the service's job and is tested black-box.</summary>
public sealed record JwtParts(JsonElement Header, JsonElement Payload, string Signature)
{
    public static JwtParts Parse(string jwt)
    {
        var p = jwt.Split('.');
        if (p.Length != 3) throw new FormatException("not a compact JWS (3 parts)");
        return new(JsonDocument.Parse(Pkce.FromB64Url(p[0])).RootElement, JsonDocument.Parse(Pkce.FromB64Url(p[1])).RootElement, p[2]);
    }
    public bool Has(string claim) => Payload.TryGetProperty(claim, out _);
}

/// <summary>Claim rules from login doc §7 / build plan §3.3 (access token, ID token).</summary>
public static class ClaimRules
{
    /// Never in any token (doc §7: "No roles or permissions in any token"; no sid; no userinfo).
    public static readonly string[] ForbiddenEverywhere = { "role", "roles", "permission", "permissions", "groups", "sid" };
    public static readonly string[] AccessTokenRequired = { "iss", "sub", "aud", "exp", "iat", "jti", "scope", "client_id", "login_provider", "amr", "auth_time" };
    public static readonly string[] IdTokenRequired = { "iss", "sub", "aud", "azp", "exp", "iat", "nonce", "auth_time", "amr" };
    /// Sent only to non-Cachd clients and only with org_claims on.
    public static readonly string[] OrgClaims = { "partner_id", "cachd_client_id", "location_id" };

    public static IEnumerable<string> Violations(JsonElement payload, string[] required, bool cachdClient, bool orgClaimsOn)
    {
        foreach (var r in required)
            if (!payload.TryGetProperty(r, out _)) yield return $"missing required claim '{r}'";
        foreach (var f in ForbiddenEverywhere)
            if (payload.TryGetProperty(f, out _)) yield return $"forbidden claim '{f}' present";
        if (!cachdClient && payload.TryGetProperty("perm_version", out _)) yield return "perm_version present on a non-Cachd client";
        foreach (var o in OrgClaims)
        {
            var has = payload.TryGetProperty(o, out _);
            if (has && cachdClient) yield return $"org claim '{o}' present on a Cachd client";
            if (has && !orgClaimsOn) yield return $"org claim '{o}' present while org_claims is off";
        }
    }
}
