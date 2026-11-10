namespace Catalog.Read;

/// <summary>
/// Session settings for a catalog read. The request principal is the authorization input.
/// The resolved flags, app.admin_type, and app.partner_id are cleared so a leftover value cannot authorize.
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
               set_config('app.partner_id', '', false),
               set_config('app.request_iss', '', false),
               set_config('app.request_sub', '', false)
        """;

    public const string CatalogIssuer = "https://catalog.invalid/scope";
    public const string CatalogSuperUser = "5f0c3e1a-00ca-4000-8000-0000000000a1";

    /// <summary>
    /// The catalog reader opens its own connection, so it cannot see the admin middleware's
    /// transaction-local principal. $1 is the resolved-global token ("on" or empty). $2 is the
    /// partner id, which is also the user id of the seeded Partner Admin for that partner.
    /// The flags are cleared. They do not grant rows.
    /// </summary>
    public const string SetScope =
        """
        SELECT set_config('app.request_iss', 'https://catalog.invalid/scope', true),
               set_config('app.request_sub', CASE WHEN $1 = 'on' THEN '5f0c3e1a-00ca-4000-8000-0000000000a1' ELSE $2 END, true),
               set_config('app.resolved_global_admin', '', true),
               set_config('app.resolved_partner_ids', '', true),
               set_config('app.resolved_location_ids', '', true),
               set_config('app.admin_type', '', true),
               set_config('app.partner_id', '', true)
        """;
}
