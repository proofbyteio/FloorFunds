// Deployment Entra values: the ONE source of truth for oidc_authority, oidc_client_id and secret_ref of every entra location
// [TLD 2026-11-04 9:49, D-51] [TLD 2026-11-04 9:50 oidc_*] [TLD 2026-11-04 10:02 Q1] [TLD 2026-11-04 10:12]. A plain carrier:
// it does NOT validate. The rules stay in Admin.Api (AdminApiOptions.Validated, as db8f360 put them, TLD 10:12); the host builds
// this from validated options (AdminApiOptions.Entra()) once at startup and hands it to the readers. No hot reload: a config change
// applies to all entra locations after a restart, with no row rewrite. The per-row copies in auth.location_login_config are kept
// only because the DB CHECK needs them (db/ frozen) and are never read (V11-ROWSECRETCLEANUP).
using Login.Abstractions;

namespace Auth.Data;

public sealed class EntraDeployment
{
    /// <summary>Entra:Authority, already validated by the host (spec §5.2, A-8).</summary>
    public string Authority { get; }
    /// <summary>Entra:ClientId, already validated and lower-case canonical.</summary>
    public string ClientId { get; }
    /// <summary>Entra:SecretRef, opaque (ToString "[redacted]"); only the secret-resolver path calls Reveal().</summary>
    public SecretReference SecretRef { get; }

    /// <summary>Null or empty values throw ArgumentException (fail closed); no other check is made here.</summary>
    public EntraDeployment(string authority, string clientId, SecretReference secretRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(authority);
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(secretRef);
        (Authority, ClientId, SecretRef) = (authority, clientId, secretRef);
    }

    public override string ToString() => "EntraDeployment { [redacted] }";
}
