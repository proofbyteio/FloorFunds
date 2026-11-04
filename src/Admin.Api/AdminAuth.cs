using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Admin.Api;

/// <summary>The server-side authenticated identity: taken ONLY from a validated access_token_admin (claims.schema.json).</summary>
public sealed record AdminIdentity(Guid UserId, string AdminType, Guid? PartnerId);

public enum BuiltInRole { None, SuperAdmin, PartnerAdmin }

/// <summary>
/// What the caller holds (spec §4 step 2). Permissions count only through a built-in Super Admin or Partner Admin role
/// [SRC: LD L172]; OrgViewLocations is org:V on specific locations (a reader with no admin role).
/// </summary>
public sealed record AdminGrant(BuiltInRole Role, bool LoginMethodWrite, bool OrgCreateEdit, IReadOnlySet<Guid> OrgViewLocations)
{
    public static readonly AdminGrant Nothing = new(BuiltInRole.None, false, false, new HashSet<Guid>());
}

/// <summary>Server-side role lookup. It sees only the authenticated identity, never request data.</summary>
public interface IAdminDirectory
{
    Task<AdminGrant> ResolveAsync(AdminIdentity id, CancellationToken ct);
}

/// <summary>
/// INTERIM (OQ-S2a-1 open; S2a has no role store): an /admin sign-in (admin_type super|partner) IS the built-in Super Admin /
/// Partner Admin role, which holds org:V, org:location_login_method:write and org create/edit. org:V-only readers and custom-role
/// callers cannot reach backoffice-api yet; the checks inject a directory to exercise those paths.
/// </summary>
public sealed class TokenAdminDirectory : IAdminDirectory
{
    public Task<AdminGrant> ResolveAsync(AdminIdentity id, CancellationToken ct) => Task.FromResult(id.AdminType switch
    {
        "super" => new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>()),
        "partner" => new AdminGrant(BuiltInRole.PartnerAdmin, true, true, new HashSet<Guid>()),
        _ => AdminGrant.Nothing,
    });
}

public sealed class AdminTokenValidator(AdminApiOptions o)
{
    readonly JsonWebTokenHandler _h = new() { MapInboundClaims = false };
    readonly TokenValidationParameters _p = new()
    {
        ValidIssuer = o.TokenIssuer, IssuerSigningKeys = o.SigningKeys, ValidAudience = AdminApiOptions.Audience,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["at+jwt"], RequireExpirationTime = true,
        RequireSignedTokens = true, ValidateLifetime = true, ValidateIssuerSigningKey = true, ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>Step 1 (authn). Null = 401. Exactly one "Authorization: Bearer" header; access_token_admin claim rules.</summary>
    public async Task<AdminIdentity?> AuthenticateAsync(HttpRequest req)
    {
        var hs = req.Headers.Authorization;
        if (hs.Count != 1 || hs[0] is not { } h || !h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = h[7..].Trim();
        if (token.Length is 0 or > 8192) return null;
        TokenValidationResult r;
        try { r = await _h.ValidateTokenAsync(token, _p); } catch (Exception) { return null; }
        if (!r.IsValid || r.SecurityToken is not JsonWebToken t) return null;
        // aud: one string (access_token_base); no location_id on an /admin token; not a recovery re-enrol session.
        if (t.Audiences.Count() != 1 || t.TryGetPayloadValue<object>("location_id", out _)) return null;
        if (t.TryGetPayloadValue<object>("aud", out var audRaw) && audRaw is not string) return null;
        if (!t.TryGetPayloadValue<string>("sub", out var sub) || !Guid.TryParseExact(sub, "D", out var userId)) return null;
        if (!t.TryGetPayloadValue<string>("admin_type", out var at) || at is not ("super" or "partner")) return null;
        if (t.TryGetPayloadValue<string>("session_kind", out var sk) && sk == "recovery_reenroll") return null;
        var hasPartner = t.TryGetPayloadValue<object>("partner_id", out _);
        Guid? partner = null;
        if (at == "partner")
        {
            if (!t.TryGetPayloadValue<string>("partner_id", out var ps) || !Guid.TryParseExact(ps, "D", out var pid) || pid == Guid.Empty) return null;
            partner = pid;
        }
        else if (hasPartner) return null;
        return new AdminIdentity(userId, at, partner);
    }
}
