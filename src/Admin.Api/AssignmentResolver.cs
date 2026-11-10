using Catalog.Contracts;
using Npgsql;

namespace Admin.Api;

/// <summary>
/// Reads auth.resolve_grant inside the caller's transaction. The function is SECURITY DEFINER.
/// admin_api is not granted SELECT on auth.app_user. Any error denies. There is no cache.
/// </summary>
public sealed class DbAdminDirectory : IAdminDirectory
{
    public async Task<AdminGrant> ResolveAsync(AdminIdentity id, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT outcome, role_wire, global_admin, partner_ids, location_ids, perm_version FROM auth.resolve_grant($1, $2)",
            connection, transaction);
        cmd.Parameters.Add(new() { Value = id.Issuer });
        cmd.Parameters.Add(new() { Value = id.UserId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return AdminGrant.Nothing;
        if (reader.GetString(0) != "grant") return AdminGrant.Nothing;
        var roleWire = reader.GetString(1);
        var global = reader.GetBoolean(2);
        var partners = reader.IsDBNull(3) ? Array.Empty<Guid>() : reader.GetFieldValue<Guid[]>(3);
        var locations = reader.IsDBNull(4) ? Array.Empty<Guid>() : reader.GetFieldValue<Guid[]>(4);
        var version = reader.GetInt64(5);
        if (global && roleWire == "super_admin")
            return new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true,
                new HashSet<Guid>(partners), new HashSet<Guid>(locations), version);
        if (roleWire == "partner_admin" && partners.Length > 0)
            return new AdminGrant(BuiltInRole.PartnerAdmin, true, true, new HashSet<Guid>(), true,
                new HashSet<Guid>(partners), new HashSet<Guid>(locations), version);
        return AdminGrant.Nothing;
    }
}

/// <summary>
/// Builds a catalog principal from a resolved grant. A grant with more than one partner is not folded
/// into the single partner id CatalogPrincipal can hold. Callers that need the union use AdminGrant.AssignedPartners.
/// </summary>
public static class AssignmentPrincipal
{
    public static CatalogPrincipal? TryFromGrant(Guid userId, AdminGrant grant)
    {
        if (grant.Role == BuiltInRole.SuperAdmin)
            return CatalogPrincipalFactory.Management(userId, CatalogRole.SuperAdmin, null);
        if (grant.Role == BuiltInRole.PartnerAdmin && grant.AssignedPartners.Count == 1)
            return CatalogPrincipalFactory.Management(userId, CatalogRole.PartnerAdmin, grant.AssignedPartners.First());
        return null;
    }
}
