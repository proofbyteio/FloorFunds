// DRAFT interfaces (TLD-S2B-10, TLD-S2B-17). SSE owns the real ones (P1 spec). Until they land, these mirror SSE's draft
// (ILocationLoginConfigReader + LocationLoginConfig as sent 7:23 AM PT) plus the two interfaces TL asked SSE to add.
// [ASSUMPTION A-S2B-01] names/shapes of IExternalLoginLookup and ILocationAssignmentCheck are SSE2 drafts; swap at plan step 8.
namespace Login.Entra.Contracts;

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

/// <summary>Link lookup on (issuer, tid, oid) only (TLD-S2B-06). Never email/upn/preferred_username.</summary>
public interface IExternalLoginLookup
{
    Task<ExternalLoginUser?> FindAsync(string issuer, Guid tenantId, Guid objectId, CancellationToken ct);
}

public sealed record ExternalLoginUser(Guid UserId, string Username, bool Disabled, bool Locked);

public interface ILocationAssignmentCheck
{
    Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct);
}

/// <summary>Resolves the opaque SecretRef to the client secret (TLD-S2B-19). Host-provided.</summary>
public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
