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
    /// The catalog reader opens its own connection, so it cannot see another transaction's
    /// binding. $1 is the session token. begin_request binds it to this backend and this
    /// transaction. The flags are cleared. They do not grant rows. The transaction cannot be
    /// READ ONLY: the binding is an insert.
    /// </summary>
    public const string SetScope =
        """
        SELECT auth.begin_request($1),
               set_config('app.resolved_global_admin', '', true),
               set_config('app.resolved_partner_ids', '', true),
               set_config('app.resolved_location_ids', '', true),
               set_config('app.admin_type', '', true),
               set_config('app.partner_id', '', true)
        """;
}

/// <summary>
/// Optional lookup from a scope to a session token. The reader uses the scope's own token
/// when it has one, and this lookup otherwise. A missing token denies the read.
/// </summary>
public static class CatalogSessions
{
    static Func<CatalogReadScope, string?>? _lookup;

    public static void Use(Func<CatalogReadScope, string?>? lookup) => _lookup = lookup;

    public static string? TokenFor(CatalogReadScope scope)
        => string.IsNullOrEmpty(scope.SessionToken) ? _lookup?.Invoke(scope) : scope.SessionToken;
}
