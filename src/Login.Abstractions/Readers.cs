// Contract: contract/v1.1-S2a-location-login-config.md §9 (TLD 2026-11-04 7:39, 7:45, 7:53).
using System.Text;
using System.Text.Json.Serialization;

namespace Login.Abstractions;

/// <summary>DB text is lower case: 'database' / 'entra' (F15). Map with <see cref="ProviderKindText"/>.</summary>
public enum ProviderKind { Database, Entra }

/// <summary>Exactly active|disabled (auth.app_user CHECK). A lock is <see cref="ExternalLoginMatch.LockedUntil"/>, not a status.</summary>
public enum UserStatus { Active, Disabled }

public sealed record LocationLoginConfig(
    Guid LocationId,
    Guid PartnerId,
    string Slug,
    ProviderKind ProviderKind,
    string DisplayName,
    IReadOnlyList<Guid> EntraAllowedTenantIds,
    string? OidcAuthority,
    string? OidcClientId,
    [property: JsonIgnore] string? SecretRef,
    bool AllowDbFallback,
    bool MfaRequired,
    long RowVersion)
{
    // Records print every property by default; SecretRef must never reach a log (F13).
    // Compare configs by RowVersion, not record equality (the list compares by reference).
    private bool PrintMembers(StringBuilder b)
    {
        b.Append("LocationId = ").Append(LocationId)
         .Append(", ProviderKind = ").Append(ProviderKind)
         .Append(", RowVersion = ").Append(RowVersion)
         .Append(", SecretRef = ").Append(SecretRef is null ? "(none)" : "[redacted]");
        return true;
    }
}

/// <summary>PartnerId is null for users with no partner (Super Admin). Status always comes from <see cref="UserStatusText.Parse"/>.</summary>
public sealed record ExternalLoginMatch(Guid UserId, Guid? PartnerId, UserStatus Status, DateTimeOffset? LockedUntil)
{
    /// <summary>Fail-closed user rule (TLD 7:45 F1, 7:53): only Active and not locked at <paramref name="now"/>.</summary>
    public bool MaySignIn(DateTimeOffset now) => Status == UserStatus.Active && (LockedUntil is null || LockedUntil <= now);
}

public interface ILocationLoginConfigReader
{
    /// <summary>null = no row. Throws on DB failure or cancellation (F8).</summary>
    Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct);
    /// <summary>Normalises with <see cref="LocationSlug.TryNormalize"/>; malformed returns null without a query.</summary>
    Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct);
}

public interface IExternalLoginReader
{
    /// <summary>
    /// null exactly when no auth.external_login row matches (no link; not a failure).
    /// providerKind must be Entra (Database throws ArgumentOutOfRangeException). issuer is ordinal-exact.
    /// tenantId = Entra tid, subject = Entra oid. Throws on DB failure, cancellation, or an unknown status value.
    /// </summary>
    Task<ExternalLoginMatch?> FindUserAsync(ProviderKind providerKind, string issuer, Guid tenantId, Guid subject, CancellationToken ct);
}

public interface IUserLocationReader
{
    /// <summary>Row exists = assigned (INTERIM; delete-to-unassign). Throws on DB failure or cancellation.</summary>
    Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct);
}

public static class UserStatusText
{
    /// <summary>'active' → Active, 'disabled' → Disabled; anything else (NULL, case variants, new values) throws. Never maps to Active.</summary>
    public static UserStatus Parse(string? dbValue) => dbValue switch
    {
        "active" => UserStatus.Active,
        "disabled" => UserStatus.Disabled,
        _ => throw new InvalidOperationException("auth.app_user.status has an unknown value; refusing (fail closed)."),
    };
}

public static class ProviderKindText
{
    public static ProviderKind Parse(string? dbValue) => dbValue switch
    {
        "database" => ProviderKind.Database,
        "entra" => ProviderKind.Entra,
        _ => throw new InvalidOperationException("provider_kind has an unknown value; refusing (fail closed)."),
    };

    public static string ToDb(ProviderKind kind) => kind switch
    {
        ProviderKind.Database => "database",
        ProviderKind.Entra => "entra",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public static class LocationSlug
{
    /// <summary>trim → ASCII lower-case → must match ^[a-z0-9](?:[a-z0-9-]{0,62})$; else null (no query).</summary>
    public static string? TryNormalize(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        if (s.Length is 0 or > 63) return null;
        Span<char> buf = stackalloc char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            var ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' || (c == '-' && i > 0);
            if (!ok) return null;
            buf[i] = c;
        }
        return new string(buf);
    }
}
