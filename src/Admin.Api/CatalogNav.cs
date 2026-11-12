using Catalog.Contracts;

namespace Admin.Api;

/// <summary>
/// Nav need "catalog". True only when the resolved assignment passes
/// <see cref="CatalogReadAuthorization.MayRead"/>.
/// </summary>
public static class CatalogNav
{
    public static bool Includes(Guid userId, AdminGrant grant) =>
        CatalogReadAuthorization.MayRead(AssignmentPrincipal.TryFromGrant(userId, grant));
}
