namespace Catalog.Contracts;

/// <summary>
/// The catalog read gate. Nav emits "catalog" only when this returns true for the
/// principal built from the resolved assignment. It does not look at create_location,
/// admin_type, or client_id.
/// </summary>
public static class CatalogReadAuthorization
{
    public static bool MayRead(CatalogPrincipal? actor) => actor is { IsManagementRole: true };
}
