// DB-backed readers (spec §9-§10). Run as <db>_login (read-only). Raw parameterised SQL over NpgsqlDataSource:
// one statement each, no EF change tracking. Contract rules:
//  - null / false only for "no row" (F8, TLD 7:53); any DB error or cancellation throws, never null/false.
//  - FindUserAsync: external_login LEFT JOIN app_user; no link row -> null; link row without app_user -> throws (SSE2 N3);
//    status mapped by UserStatusText.Parse (unknown -> throws, never Active; TLD 7:53, SSE2 N1).
using Login.Abstractions;
using Npgsql;

namespace Auth.Data.Readers;

/// <summary>
/// [TLD 2026-11-04 10:02 Q1] OidcAuthority, OidcClientId and SecretRef of an entra location come from <paramref name="entra"/>
/// (deployment config Entra:Authority / Entra:ClientId / Entra:SecretRef), on every read; the row's oidc_authority, oidc_client_id
/// and secret_ref columns are not selected (not the source of truth; V11-ROWSECRETCLEANUP). Database locations get null for all three.
/// So what the login service feeds to ISecretResolver follows config after a restart, with no row rewrite.
/// </summary>
public sealed class NpgsqlLocationLoginConfigReader(NpgsqlDataSource dataSource, EntraDeployment entra) : ILocationLoginConfigReader
{
    private readonly EntraDeployment _entra = entra ?? throw new ArgumentNullException(nameof(entra));

    private const string Select = """
        SELECT l.location_id, l.partner_id, l.slug, c.provider_kind, c.display_name, c.entra_allowed_tenant_ids,
               c.allow_db_fallback, c.mfa_required, c.row_version
          FROM auth.location l
          JOIN auth.location_login_config c ON c.location_id = l.location_id
        """;

    public Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct)
        => QueryAsync(Select + " WHERE l.location_id = $1", new NpgsqlParameter<Guid> { TypedValue = locationId }, ct);

    public Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var normalised = LocationSlug.TryNormalize(slug);
        if (normalised is null) return Task.FromResult<LocationLoginConfig?>(null); // malformed: no query
        return QueryAsync(Select + " WHERE l.slug = $1", new NpgsqlParameter<string> { TypedValue = normalised }, ct);
    }

    private async Task<LocationLoginConfig?> QueryAsync(string sql, NpgsqlParameter p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var kind = ProviderKindText.Parse(r.GetString(3));
        var isEntra = kind == ProviderKind.Entra;
        var cfg = new LocationLoginConfig(
            r.GetGuid(0), r.GetGuid(1), r.GetString(2), kind, r.GetString(4), r.GetFieldValue<Guid[]>(5),
            isEntra ? _entra.Authority : null, isEntra ? _entra.ClientId : null,
            r.GetBoolean(6), r.GetBoolean(7), r.GetInt64(8))
        { SecretRef = isEntra ? _entra.SecretRef : null };
        if (await r.ReadAsync(ct)) throw new InvalidOperationException("location_login_config: more than one row for a unique key.");
        return cfg;
    }
}

public sealed class NpgsqlExternalLoginReader(NpgsqlDataSource dataSource) : IExternalLoginReader
{
    internal const string Sql = """
        SELECT el.user_id, (u.user_id IS NOT NULL) AS has_user, u.partner_id, u.status, u.locked_until
          FROM auth.external_login el
          LEFT JOIN auth.app_user u ON u.user_id = el.user_id
         WHERE el.provider_kind = $1 AND el.issuer = $2 AND el.tenant_id = $3 AND el.subject = $4
        """;

    public async Task<ExternalLoginMatch?> FindUserAsync(ProviderKind providerKind, string issuer, Guid tenantId, Guid subject, CancellationToken ct)
    {
        Validate(providerKind, issuer);
        ct.ThrowIfCancellationRequested();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        return await FindOnConnectionAsync(conn, null, providerKind, issuer, tenantId, subject, ct);
    }

    private static void Validate(ProviderKind providerKind, string issuer)
    {
        if (providerKind != ProviderKind.Entra)
            throw new ArgumentOutOfRangeException(nameof(providerKind), providerKind, "external_login holds Entra links only.");
        ArgumentNullException.ThrowIfNull(issuer);
    }

    // Same SQL and mapping as FindUserAsync on a caller-owned connection/transaction. Used by FindUserAsync and by the
    // integration checks that must create states the constraints forbid (orphan link, unknown status) inside a
    // rolled-back owner transaction (SSE2 N3; see src/S2A-BUILD-LOG.md).
    internal static async Task<ExternalLoginMatch?> FindOnConnectionAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        ProviderKind providerKind, string issuer, Guid tenantId, Guid subject, CancellationToken ct)
    {
        Validate(providerKind, issuer);
        await using var cmd = new NpgsqlCommand(Sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ProviderKindText.ToDb(providerKind) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = issuer });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = subject });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null; // no link row: the only null
        if (!r.GetBoolean(1))
            throw new InvalidOperationException("auth.external_login row has no auth.app_user row; refusing (fail closed).");
        var match = new ExternalLoginMatch(
            r.GetGuid(0),
            r.IsDBNull(2) ? null : r.GetGuid(2),
            UserStatusText.Parse(r.IsDBNull(3) ? null : r.GetString(3)),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4));
        if (await r.ReadAsync(ct)) throw new InvalidOperationException("external_login: more than one row for the primary key.");
        return match;
    }
}

public sealed class NpgsqlUserLocationReader(NpgsqlDataSource dataSource) : IUserLocationReader
{
    public async Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var cmd = dataSource.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM auth.user_location WHERE user_id = $1 AND location_id = $2)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = userId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = locationId });
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is bool b ? b : throw new InvalidOperationException("user_location probe returned no boolean.");
    }
}
