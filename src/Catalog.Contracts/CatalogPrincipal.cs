namespace Catalog.Contracts;

/// <summary>
/// Management roles are Super Admin and Partner Admin. Every other role has no catalog management grant.
/// <see cref="None"/> is the default so an uninitialized principal cannot act.
/// </summary>
public enum CatalogRole
{
    None = 0,
    SuperAdmin = 1,
    PartnerAdmin = 2,
    ClientAdmin = 3,
    Supervisor = 4,
    Cashier = 5,
    Auditor = 6,
    Accounting = 7,
    Support = 8,
}

/// <summary>
/// Server-derived actor. Callers of the domain service supply this from the authenticated context.
/// Command bodies cannot set, replace, or impersonate it. There is no delegated-actor field.
/// </summary>
public sealed record CatalogPrincipal
{
    public CatalogPrincipal(Guid userId, CatalogRole role, Guid? partnerId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A catalog actor needs a server-derived user id.", nameof(userId));
        if (role == CatalogRole.PartnerAdmin && (partnerId is null || partnerId == Guid.Empty))
            throw new ArgumentException("A Partner Admin principal carries the authenticated partner id.", nameof(partnerId));
        if (role != CatalogRole.PartnerAdmin && partnerId is not null)
            throw new ArgumentException("Only a Partner Admin principal carries a partner id.", nameof(partnerId));
        UserId = userId;
        Role = role;
        PartnerId = partnerId;
    }

    public Guid UserId { get; }
    public CatalogRole Role { get; }
    public Guid? PartnerId { get; }

    public bool IsManagementRole => Role is CatalogRole.SuperAdmin or CatalogRole.PartnerAdmin;
}

/// <summary>Server call context. Idempotency and correlation are not read from the command body.</summary>
public sealed record CatalogCall
{
    public CatalogCall(string? idempotencyKey, string correlationId, string sourceRequestId)
    {
        IdempotencyKey = idempotencyKey;
        CorrelationId = correlationId ?? "";
        SourceRequestId = sourceRequestId ?? "";
    }

    public string? IdempotencyKey { get; }
    public string CorrelationId { get; }
    public string SourceRequestId { get; }
}
