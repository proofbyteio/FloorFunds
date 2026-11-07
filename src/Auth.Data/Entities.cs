// EF entity types for schema auth (spec §8.1). The DDL (CHECKs, FKs, indexes, RLS, grants) is the hand-written
// migration's SQL; the EF model maps tables, columns, store types, nullability, keys and the concurrency token,
// and is kept equal to Migrations/AuthDbContextModelSnapshot.cs (checked by the model-diff check).
namespace Auth.Data.Entities;

public sealed class Location
{
    public Guid LocationId { get; set; }
    public Guid PartnerId { get; set; }
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class LocationLoginConfigRow
{
    public Guid LocationId { get; set; }
    public string ProviderKind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Guid[] EntraAllowedTenantIds { get; set; } = [];
    public string? OidcAuthority { get; set; }
    public string? OidcClientId { get; set; }
    public string? SecretRef { get; set; }
    public bool AllowDbFallback { get; set; }
    public bool MfaRequired { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

/// <summary>Slice A (DEC-109..117): a partner's own reason bounds; no row = deployment config (TLD 2026-11-05 08:00 Q3).</summary>
public sealed class PartnerReasonBounds
{
    public Guid PartnerId { get; set; }
    public int ReasonMinLength { get; set; }
    public int ReasonMaxLength { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long RowVersion { get; set; }
}

public sealed class LocationLoginConfigChange
{
    public Guid ChangeId { get; set; }
    public Guid LocationId { get; set; }
    public Guid ActorUserId { get; set; }
    public string AdminType { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
    public string ChangeKind { get; set; } = "";
    public string[] ChangedFields { get; set; } = [];
    public string? Before { get; set; }
    public string After { get; set; } = "";
    public long RowVersion { get; set; }
}

public sealed class AppUser
{
    public Guid UserId { get; set; }
    public Guid? PartnerId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class ExternalLogin
{
    public string ProviderKind { get; set; } = "";
    public string Issuer { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid Subject { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public Guid? LinkedBy { get; set; }
}

public sealed class UserLocation
{
    public Guid UserId { get; set; }
    public Guid LocationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class AuditOutbox
{
    public Guid EventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string EventType { get; set; } = "";
    public string Envelope { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class OidcReplay
{
    public string Kind { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class Session
{
    public Guid SessionId { get; set; }
    public byte[] CookieKeyHash { get; set; } = [];
    public Guid UserId { get; set; }
    public Guid? PartnerId { get; set; }
    public Guid? LocationId { get; set; }
    public string? AdminType { get; set; }
    public string SessionKind { get; set; } = "normal";
    public string LoginProvider { get; set; } = "";
    public Guid? EntraTenantId { get; set; }
    public long? ConfigRowVersion { get; set; }
    public string[] Amr { get; set; } = [];
    public DateTimeOffset AuthTime { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Interactive { get; set; }
    public bool ViaReturn { get; set; }
    public bool CodeIssued { get; set; }
    public DateTimeOffset? InteractiveUsedAt { get; set; }
    public DateTimeOffset? HandoffUsedAt { get; set; }
    public DateTimeOffset? CageConsumedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
}
