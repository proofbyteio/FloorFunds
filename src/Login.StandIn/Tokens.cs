using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Login.StandIn;

// contract 8.1, 8.6, 8.9: RS256 tokens signed with the in-memory standin- key published in the JWKS (3.1, 3.3).
// [DIVERGENCE D1] the task text said "ES256/JWKS"; the frozen contract says RS256 for tokens (2.1, 2.4, 3.1, 8.1, claims schema
// jose_header_*). The contract wins; ES256 is used only for the fake agent's device attestations (6.5, 6.8).
public sealed class TokenIssuer
{
    public const int AccessLifetime = 300, IdLifetime = 300;
    readonly string _issuer;
    readonly SigningCredentials _cred;
    readonly RsaSecurityKey _key;
    readonly JsonWebTokenHandler _h = new() { SetDefaultTimesOnTokenCreation = false };

    public TokenIssuer(string issuer, SigningKeys keys)
    {
        _issuer = issuer;
        _key = new RsaSecurityKey(keys.Rsa) { KeyId = keys.Kid };
        _cred = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256);
    }

    public static string NewHandle() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    string Sign(Dictionary<string, object> claims, string typ) =>
        _h.CreateToken(new SecurityTokenDescriptor { Claims = claims, SigningCredentials = _cred, TokenType = typ });

    public (string Token, string Jti, long Exp) Access(LoginSession s, OAuthClient c, string scope, DateTimeOffset now, StandInUser u, JsonLookup org)
    {
        long iat = now.ToUnixTimeSeconds();
        // The dev shell is a local session a person keeps open. Other clients stay at AccessLifetime.
        long exp = iat + (c.ClientId == Clients.DevShellClientId ? 3600 : AccessLifetime);
        var jti = NewHandle();
        var cl = new Dictionary<string, object>
        {
            ["iss"] = _issuer, ["sub"] = s.UserId, ["aud"] = c.Aud ?? throw new InvalidOperationException("openid-only client has no access token"), ["exp"] = exp, ["iat"] = iat, ["jti"] = jti, ["scope"] = scope,
            ["client_id"] = c.ClientId, ["login_provider"] = "database", ["amr"] = s.Amr, ["auth_time"] = s.AuthTime.ToUnixTimeSeconds(),
            ["x_standin"] = true, ["session_kind"] = s.Kind,
        };
        if (c.Cachd || c.OrgClaims)
        {
            cl["partner_id"] = org.PartnerId; cl["location_id"] = s.LocationId;
            if (org.CachdClientId is not null) cl["cachd_client_id"] = org.CachdClientId;
        }
        if (c.Cachd) cl["perm_version"] = u.PermVersion;
        // A dev-shell admin session carries the sign-in discriminator only. Authority still comes from the resolver.
        if (s.AdminType == "super") cl["admin_type"] = "super";
        else if (s.AdminType == "partner" && s.PartnerId is not null)
        {
            cl["admin_type"] = "partner";
            cl["partner_id"] = s.PartnerId;
        }
        return (Sign(cl, "at+jwt"), jti, exp);
    }

    public string Id(LoginSession s, OAuthClient c, DateTimeOffset now, string? nonce)
    {
        long iat = now.ToUnixTimeSeconds();
        var cl = new Dictionary<string, object>
        {
            ["iss"] = _issuer, ["sub"] = s.UserId, ["aud"] = c.ClientId, ["azp"] = c.ClientId, ["exp"] = iat + IdLifetime, ["iat"] = iat,
            ["auth_time"] = s.AuthTime.ToUnixTimeSeconds(), ["amr"] = s.Amr, ["x_standin"] = true, ["session_kind"] = s.Kind,
        };
        if (nonce is not null) cl["nonce"] = nonce; // 8.6: code grant only.
        return Sign(cl, "JWT");
    }

    // Signature, iss and typ; lifetime optional (11.2a ignores exp for id_token_hint). Returns the payload or null.
    public async Task<JsonWebToken?> Validate(string token, string typ, bool checkLifetime, DateTimeOffset now)
    {
        var r = await _h.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = _issuer, IssuerSigningKey = _key, ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
            ValidTypes = new[] { typ }, ValidateAudience = false, ValidateLifetime = checkLifetime, ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = checkLifetime ? (nb, ex, _, _) => ex is not null && ex.Value > now.UtcDateTime.AddSeconds(-30) : null,
        });
        return r.IsValid ? (JsonWebToken)r.SecurityToken : null;
    }
}

public sealed record JsonLookup(string PartnerId, string? CachdClientId);
