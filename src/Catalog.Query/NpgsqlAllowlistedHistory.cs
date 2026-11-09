using System.Data;
using Catalog.Contracts;
using Catalog.Read;
using Npgsql;

namespace Catalog.Query;

/// <summary>
/// Reads history scalars through the same admin_api session rules as the raw reader.
/// The select list is a fixed allowlist. It does not return a jsonb document or an actor column.
/// </summary>
public sealed class NpgsqlAllowlistedHistory
{
    private const string ClearSessionSql =
        "SELECT set_config('app.admin_type', '', false), set_config('app.partner_id', '', false)";

    private const string RoleSql = """
        SELECT current_user = current_database() || '_admin_api'
           AND NOT r.rolsuper
           AND NOT r.rolbypassrls
           AND NOT r.rolcreatedb
           AND NOT r.rolcreaterole
           AND NOT r.rolreplication
           AND r.rolcanlogin
           AND current_user <> o.owner_name
           AND NOT pg_has_role(current_user, o.owner_name, 'MEMBER')
          FROM pg_roles r
          CROSS JOIN (
            SELECT pg_get_userbyid(c.relowner) AS owner_name
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'catalog' AND c.relname = 'registered_entry'
          ) o
         WHERE r.rolname = current_user
        """;

    private const string SetScopeSql =
        "SELECT set_config('app.admin_type', $1, true), set_config('app.partner_id', $2, true)";

    private const string ClosedSql = """
        SELECT change_id, entry_id, change_kind, row_version, at
          FROM catalog.entry_change
         WHERE entry_id = $1
         ORDER BY row_version, change_id
        """;

    private const string FeeCreatedSql = """
        SELECT change_id, entry_id, change_kind, row_version, at,
               CASE WHEN change_kind = 'created' THEN after #>> '{subtype,code}' END,
               CASE WHEN change_kind = 'created' THEN after #>> '{subtype,display_name}' END,
               CASE WHEN change_kind = 'created' THEN after #>> '{subtype,description}' END
          FROM catalog.entry_change
         WHERE entry_id = $1
         ORDER BY row_version, change_id
        """;

    private const string ClaimEditSql = """
        SELECT change_id, entry_id, change_kind, row_version, at,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,claim_value}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,claim_value}' END,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,availability}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,availability}' END,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,source_url}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,source_url}' END,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,checked_at}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,checked_at}' END,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,display_name}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,display_name}' END,
               CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,description}' END,
               CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,description}' END
          FROM catalog.entry_change
         WHERE entry_id = $1
         ORDER BY row_version, change_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlAllowlistedHistory(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<IReadOnlyList<AllowlistedHistoryScalars>> ListAsync(
        CatalogReadScope scope,
        Guid entryId,
        string family,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope is null || !scope.IsValid)
            throw new CatalogReadContextException("catalog read context is missing or invalid.");
        var sql = family switch
        {
            _ when family == CatalogFamilies.Wire(CatalogFamily.FeeType) => FeeCreatedSql,
            _ when family == CatalogFamilies.Wire(CatalogFamily.ModelHardwareClaim) => ClaimEditSql,
            _ => ClosedSql,
        };
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await ClearSessionAsync(conn, cancellationToken);
            await AssertProductionRoleAsync(conn, cancellationToken);
            await using var tx = await conn.BeginTransactionAsync(cancellationToken);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx))
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            await using (var guc = new NpgsqlCommand(SetScopeSql, conn, tx))
            {
                guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope.AdminType });
                guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope.PartnerId?.ToString("D") ?? "" });
                await guc.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entryId });
            var rows = new List<AllowlistedHistoryScalars>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    rows.Add(Read(reader, family));
            }
            await tx.CommitAsync(cancellationToken);
            return rows;
        }
        finally
        {
            if (conn.State == ConnectionState.Open)
            {
                try { await ClearSessionAsync(conn, CancellationToken.None); }
                catch (Exception) { NpgsqlConnection.ClearPool(conn); }
            }
        }
    }

    private static AllowlistedHistoryScalars Read(NpgsqlDataReader reader, string family)
    {
        var changeId = reader.GetGuid(0);
        var entryId = reader.GetGuid(1);
        var kind = reader.GetString(2);
        var version = reader.GetInt64(3);
        var at = reader.GetFieldValue<DateTimeOffset>(4);
        if (family == CatalogFamilies.Wire(CatalogFamily.FeeType))
        {
            return new AllowlistedHistoryScalars(
                changeId, entryId, kind, version, at,
                Text(reader, 5), Text(reader, 6), Text(reader, 7),
                null, null, null, null, null, null, null, null, null, null, null, null);
        }
        if (family == CatalogFamilies.Wire(CatalogFamily.ModelHardwareClaim))
        {
            return new AllowlistedHistoryScalars(
                changeId, entryId, kind, version, at,
                null, null, null,
                Text(reader, 5), Text(reader, 6),
                Text(reader, 7), Text(reader, 8),
                Text(reader, 9), Text(reader, 10),
                Text(reader, 11), Text(reader, 12),
                Text(reader, 13), Text(reader, 14),
                Text(reader, 15), Text(reader, 16));
        }
        return new AllowlistedHistoryScalars(
            changeId, entryId, kind, version, at,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task ClearSessionAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(ClearSessionSql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertProductionRoleAsync(NpgsqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(RoleSql, conn);
        var ok = await cmd.ExecuteScalarAsync(cancellationToken);
        if (ok is not bool pass || !pass)
            throw new CatalogReadRoleException("catalog read connection is not the production-equivalent admin_api role.");
    }
}
