using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
namespace Login.Entra;

/// <summary>Access token for OUR API (proposal §7.1 rule 1, rows 8–12, 19).</summary>
public sealed class EntraAccessTokenValidator(EntraOptions opts, TimeProvider clock)
{
    public sealed record Result(bool Ok, string Detail, JsonWebToken? Token);

    public async Task<Result> ValidateAsync(string? accessToken, EntraLocation loc, IEnumerable<SecurityKey> keys, string idTid, string idOid)
    {
        if (string.IsNullOrEmpty(accessToken)) return new(false, "access_token_missing", null);
        var tvp = new TokenValidationParameters
        {
            TryAllIssuerSigningKeys = false, // unknown kid must fail, not fall through to any key
            ValidIssuer = loc.Authority,
            ValidAudience = loc.ClientId,
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = opts.ClockSkew,
            LifetimeValidator = (nbf, exp, _, p) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return exp is not null && exp.Value.Add(p.ClockSkew) >= now && (nbf is null || nbf.Value.Subtract(p.ClockSkew) <= now);
            },
        };
        var r = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(accessToken, tvp).ConfigureAwait(false);
        if (!r.IsValid || r.SecurityToken is not JsonWebToken t) return new(false, "access_token_invalid:" + r.Exception?.GetType().Name, null);
        string? C(string n) => t.TryGetPayloadValue<object>(n, out var v) ? v?.ToString() : null;
        if (C("ver") != "2.0") return new(false, "ver", t);
        if (C("azp") != loc.ClientId) return new(false, "azp", t);
        var scp = (C("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scp.Contains(opts.RequiredScope, StringComparer.Ordinal)) return new(false, "scp", t);
        if (C("tid") != idTid || C("oid") != idOid) return new(false, "tid_oid_mismatch", t);
        if (string.IsNullOrEmpty(C("uti"))) return new(false, "uti_missing", t);
        return new(true, "", t);
    }
}
