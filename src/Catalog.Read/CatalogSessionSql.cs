namespace Catalog.Read;

/// <summary>
/// Session settings for a catalog read. The resolved flag and id lists are the authorization inputs.
/// app.admin_type and app.partner_id are cleared so a leftover value cannot authorize.
/// </summary>
public static class CatalogSessionSql
{
    public const string Clear =
        """
        SELECT set_config('app.resolved_global_admin', '', false),
               set_config('app.resolved_partner_ids', '', false),
               set_config('app.resolved_location_ids', '', false),
               set_config('app.actor_user_id', '', false),
               set_config('app.perm_version', '', false),
               set_config('app.admin_type', '', false),
               set_config('app.partner_id', '', false)
        """;

    public const string SetScope =
        """
        SELECT set_config('app.resolved_global_admin', $1, true),
               set_config('app.resolved_partner_ids', $2, true),
               set_config('app.resolved_location_ids', '', true),
               set_config('app.admin_type', '', true),
               set_config('app.partner_id', '', true)
        """;
}
