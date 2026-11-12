namespace Catalog.Contracts;

/// <summary>
/// Builds a catalog principal from a resolved role. Denied is the no-assignment principal.
/// </summary>
public static class CatalogPrincipalFactory
{
    public static CatalogPrincipal Denied(Guid userId) => new(userId, CatalogRole.None, null);

    public static CatalogPrincipal Management(Guid userId, CatalogRole role, Guid? partnerId) =>
        new(userId, role, partnerId);
}
