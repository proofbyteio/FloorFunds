// Host hooks: the only coupling between Login.Entra and its host (stand-in now, real service R1 later).
using Login.Abstractions;
using Microsoft.AspNetCore.Http;
namespace Login.Entra.Contracts;

/// <summary>v1 audit: existing reasons only (TLD-S2B-09). Mapped amr only, never Entra's raw amr (TLD-S2B-12).</summary>
public sealed record EntraAuditEvent(
    string EventType,            // auth.login.failed | auth.login.succeeded
    string ProviderKind,         // "entra"
    string? Reason,              // v1 reasons only: bad_credentials, locked, mfa_missing, tenant_mismatch, stale_auth_time
    string UsernameSubject,      // host computes username_hmac from this (oid@tid, or "" when no identity yet)
    Guid? LocationId,
    Guid? UserId,
    IReadOnlyList<string>? Amr,  // mapped, closed enum
    DateTimeOffset? AuthTime);

public sealed record EntraSignInResult(Guid UserId, Guid? PartnerId, Guid LocationId, Guid TenantId, Guid ObjectId,
    IReadOnlyList<string> Amr, DateTimeOffset AuthTime, string? ReturnUrl);

public interface IEntraHost
{
    /// <summary>Write one audit event (host validates against audit-events.schema.json).</summary>
    Task WriteAuditAsync(EntraAuditEvent ev, CancellationToken ct);
    /// <summary>Create OUR login session (login_provider=entra) and write the response (e.g. 302 to the hub).</summary>
    Task CompleteSignInAsync(HttpContext http, EntraSignInResult result);
    /// <summary>Render the generic "sign-in unavailable" error page: no form, no password field.</summary>
    Task RenderErrorAsync(HttpContext http, Guid? locationId);
    /// <summary>Render the database sign-in form for this location (server-decided fallback, TLD-S2B-15).</summary>
    Task RenderDatabaseFormAsync(HttpContext http, LocationLoginConfig config, bool viaFallback);
}

/// <summary>Resolves the opaque SecretRef to the client secret (TLD-S2B-19). Host-provided.
/// Not in Login.Abstractions (3f3fef9), so it stays a Login.Entra host hook.</summary>
public interface ISecretResolver
{
    Task<string?> ResolveAsync(string secretRef, CancellationToken ct);
}
