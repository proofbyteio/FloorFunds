// DRAFT interfaces (TLD-S2B-10, TLD-S2B-17). SSE owns the real ones (P1 spec). Until they land, these mirror SSE's draft
// (ILocationLoginConfigReader + LocationLoginConfig as sent 7:23 AM PT) plus the two interfaces TL asked SSE to add.
// [ASSUMPTION A-S2B-01] names/shapes of IExternalLoginLookup and ILocationAssignmentCheck are SSE2 drafts; swap at plan step 8.
// TODO(S2b step 8): delete this file and switch to SSE's src/Login.Abstractions (SSE P2 commit; SSE owns it).
// SSE2 must NOT create src/Login.Abstractions. SSE confirmed (7:45 AM PT): readers THROW on DB errors and on
// cancellation; null means "no row" only. external_login uses the LD column names with the oid stored as subject;
// assignments are auth.user_location. F1 (user table) is pending with TL: modelled on SSE's recommendation
// auth.app_user + FindUserAsync -> (UserId, PartnerId, Status, LockedUntil). Unknown Status is refused.
namespace Login.Entra.Drafts;

public enum LoginProviderKind { Database, Entra }

public sealed record LocationLoginConfig(
    Guid LocationId,
    LoginProviderKind ProviderKind,
    string DisplayName,
    IReadOnlyList<Guid> EntraAllowedTenantIds,
    string? OidcAuthority,   // base URL only; authority = base + "/{tid}/v2.0" (TLD-S2B-18)
    string? OidcClientId,
    string? SecretRef,       // opaque (TLD-S2B-19)
    bool AllowDbFallback,
    bool MfaRequired,
    long RowVersion);

public interface ILocationLoginConfigReader
{
    Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct);
    Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct);
}

/// <summary>external_login lookup on (issuer, tid, subject=oid) only (TLD-S2B-06). Never email/upn/preferred_username.
/// Returns the linked user id, or null when no row. Throws on DB error / cancellation.</summary>
public interface IExternalLoginLookup
{
    Task<Guid?> FindAsync(string issuer, Guid tenantId, Guid subjectOid, CancellationToken ct);
}

/// <summary>[DRAFT, F1 pending] auth.app_user reader. Null = no row; throws on DB error / cancellation.</summary>
public interface IAppUserReader
{
    Task<AppUserRecord?> FindUserAsync(Guid userId, CancellationToken ct);
}

public sealed record AppUserRecord(Guid UserId, Guid PartnerId, string Status, DateTimeOffset? LockedUntil);

/// <summary>Known app_user statuses. Anything else is refused (fail closed).</summary>
public static class AppUserStatus
{
    public const string Active = "active", Disabled = "disabled";
}

/// <summary>auth.user_location. Throws on DB error / cancellation.</summary>
public interface ILocationAssignmentCheck
{
    Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct);
}

/// <summary>Resolves the opaque SecretRef to the client secret (TLD-S2B-19). Host-provided.</summary>
public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
