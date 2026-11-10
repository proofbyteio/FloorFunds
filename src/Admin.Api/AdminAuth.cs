using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Admin.Api;

/// <summary>
/// Authenticated token identity. AdminType and PartnerId are the sign-in claims.
/// They are not authority. Issuer is the verified iss. The resolver maps (iss, sub) to a user.
/// </summary>
public sealed record AdminIdentity(Guid UserId, string AdminType, Guid? PartnerId, string Issuer = "");

public enum BuiltInRole { None, SuperAdmin, PartnerAdmin }

/// <summary>
/// What the caller holds after the assignment lookup. PartnerIds and LocationIds are the union of active
/// assignments (DEC-199). OrgViewLocations is the injected org:V reader set used by the existing checks.
/// PartnerReasonBoundsWrite is org:partner_reason_bounds:write: Super Admin and ANY Partner Admin, for ANY partner.
/// </summary>
public sealed record AdminGrant(
    BuiltInRole Role,
    bool LoginMethodWrite,
    bool OrgCreateEdit,
    IReadOnlySet<Guid> OrgViewLocations,
    bool PartnerReasonBoundsWrite = false,
    IReadOnlySet<Guid>? PartnerIds = null,
    IReadOnlySet<Guid>? LocationIds = null,
    long PermVersion = 0)
{
    public static readonly AdminGrant Nothing = new(BuiltInRole.None, false, false, new HashSet<Guid>());
    public IReadOnlySet<Guid> AssignedPartners => PartnerIds ?? Empty;
    public IReadOnlySet<Guid> AssignedLocations => LocationIds ?? Empty;
    static readonly HashSet<Guid> Empty = [];
}

/// <summary>
/// Server-side role lookup inside the request transaction. It sees the authenticated identity and the
/// open transaction, never request data. A failure denies.
/// </summary>
public interface IAdminDirectory
{
    Task<AdminGrant> ResolveAsync(AdminIdentity id, Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, CancellationToken ct);
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
    readonly HashSet<string> _kids = o.SigningKeys.Select(k => k.KeyId).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal)!;

    /// <summary>
    /// Step 1 (authn). Null = 401. Exactly one "Authorization: Bearer" header; access_token_admin claim rules.
    /// Unknown kid and duplicate JSON members are rejected. The duplicate-member and kid checks follow
    /// Catalog.ScratchAuth/FrozenAdminTokenGate.cs:19 (AllowDuplicateProperties = false), :90-91 (header and
    /// payload parse), and :123-131 (kid must equal a configured key id).
    /// </summary>
    public async Task<AdminIdentity?> AuthenticateAsync(HttpRequest req)
    {
        var hs = req.Headers.Authorization;
        if (hs.Count != 1 || hs[0] is not { } h || !h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = h[7..].Trim();
        if (token.Length is 0 or > 8192) return null;
        TokenValidationResult r;
        try { r = await _h.ValidateTokenAsync(token, _p); } catch (Exception) { return null; }
        if (!r.IsValid || r.SecurityToken is not JsonWebToken t) return null;
        if (!HeaderAccepted(t)) return null;
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
        var issuer = t.Issuer ?? "";
        return new AdminIdentity(userId, at, partner, issuer);
    }

    bool HeaderAccepted(JsonWebToken t)
    {
        try
        {
            var headerJson = Base64UrlEncoder.Decode(t.EncodedHeader);
            using var header = JsonDocument.Parse(headerJson, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 8 });
            if (!header.RootElement.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String) return false;
            if (!_kids.Contains(kid.GetString() ?? "")) return false;
            using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(t.EncodedPayload), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
            return payload.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
        catch (Exception) { return false; }
    }
}
