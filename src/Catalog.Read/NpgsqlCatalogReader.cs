using System.Data;
using Npgsql;

namespace Catalog.Read;

/// <summary>
/// SELECT-only catalog reader for the production-equivalent &lt;db&gt;_admin_api role.
/// Every call takes an explicit scope, clears session GUCs on checkout and before the
/// connection returns to the pool, and runs inside a read-only transaction.
/// Missing or invalid context throws. A hidden row and a missing row both come back empty;
/// this reader does not call a definer to tell them apart.
/// </summary>
public sealed class NpgsqlCatalogReader
{
    private const string ClearSessionSql = CatalogSessionSql.Clear;

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

    private const string SetScopeSql = CatalogSessionSql.SetScope;

    private const string ListEntriesSql = """
        SELECT entry_id, family, entry_class, owner_partner_id, code, is_active, row_version
          FROM catalog.registered_entry
         WHERE (@code::text IS NULL OR code = @code)
         ORDER BY family, code, entry_id
        """;

    private const string FindEntrySql = """
        SELECT entry_id, family, entry_class, owner_partner_id, code, is_active, row_version
          FROM catalog.registered_entry
         WHERE entry_id = $1
        """;

    private const string ListFeesSql = """
        SELECT e.entry_id, e.owner_partner_id, e.code, f.definition, f.display_name, f.description, e.is_active, e.row_version
          FROM catalog.fee_type f
          JOIN catalog.registered_entry e ON e.entry_id = f.entry_id
         ORDER BY e.code, e.entry_id
        """;

    private const string ListChangesSql = """
        SELECT change_id, entry_id, change_kind, row_version, scope_level
          FROM catalog.entry_change
         WHERE entry_id = $1
         ORDER BY row_version, change_id
        """;

    private const string CountPartnerSwitchSql = "SELECT count(*)::int FROM catalog.partner_switch";
    private const string CountLocationSwitchSql = "SELECT count(*)::int FROM catalog.location_switch";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlCatalogReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope? scope, CancellationToken ct)
        => ListEntriesAsync(scope, null, ct);

    public async Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope? scope, string? code, CancellationToken ct)
    {
        var rows = await QueryAsync(scope, ListEntriesSql, cmd =>
        {
            cmd.Parameters.Add(new NpgsqlParameter("code", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = (object?)code ?? DBNull.Value,
            });
        }, ReadEntry, ct);
        return rows;
    }

    public async Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope? scope, Guid entryId, CancellationToken ct)
    {
        var rows = await QueryAsync(scope, FindEntrySql, cmd =>
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entryId });
        }, ReadEntry, ct);
        if (rows.Count > 1)
            throw new InvalidOperationException("catalog.registered_entry returned more than one row for the primary key.");
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<IReadOnlyList<CatalogFeeTypeRow>> ListFeeTypesAsync(CatalogReadScope? scope, CancellationToken ct)
        => QueryAsync(scope, ListFeesSql, _ => { }, ReadFee, ct);

    public Task<IReadOnlyList<CatalogEntryChangeRow>> ListEntryChangesAsync(CatalogReadScope? scope, Guid entryId, CancellationToken ct)
        => QueryAsync(scope, ListChangesSql, cmd =>
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entryId });
        }, ReadChange, ct);

    public async Task<int> CountPartnerSwitchesAsync(CatalogReadScope? scope, CancellationToken ct)
    {
        var rows = await QueryAsync(scope, CountPartnerSwitchSql, _ => { }, r => r.GetInt32(0), ct);
        return rows.Count == 1 ? rows[0] : throw new InvalidOperationException("partner_switch count returned no row.");
    }

    public async Task<int> CountLocationSwitchesAsync(CatalogReadScope? scope, CancellationToken ct)
    {
        var rows = await QueryAsync(scope, CountLocationSwitchSql, _ => { }, r => r.GetInt32(0), ct);
        return rows.Count == 1 ? rows[0] : throw new InvalidOperationException("location_switch count returned no row.");
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        CatalogReadScope? scope,
        string sql,
        Action<NpgsqlCommand> bind,
        Func<NpgsqlDataReader, T> map,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        try
        {
            await ClearSessionAsync(conn, ct);
            if (scope is null || !scope.IsValid)
                throw new CatalogReadContextException("catalog read context is missing or invalid.");
            await AssertProductionRoleAsync(conn, ct);
            var token = CatalogSessions.TokenFor(scope);
            if (string.IsNullOrEmpty(token))
                throw new CatalogReadContextException("catalog read context is missing or invalid.");
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var guc = new NpgsqlCommand(SetScopeSql, conn, tx))
            {
                guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = token });
                try { await guc.ExecuteNonQueryAsync(ct); }
                catch (PostgresException ex) when (ex.SqlState == "42501"
                    && ex.MessageText is "session_expired" or "session_denied")
                {
                    throw new CatalogSessionClosedException();
                }
            }
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            bind(cmd);
            var rows = new List<T>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                    rows.Add(map(reader));
            }
            await tx.CommitAsync(ct);
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

    private static async Task ClearSessionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ClearSessionSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AssertProductionRoleAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(RoleSql, conn);
        var ok = await cmd.ExecuteScalarAsync(ct);
        if (ok is not bool pass || !pass)
            throw new CatalogReadRoleException("catalog read connection is not the production-equivalent admin_api role.");
    }

    private static CatalogEntryRow ReadEntry(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetGuid(3),
        r.GetString(4),
        r.GetBoolean(5),
        r.GetInt64(6));

    private static CatalogFeeTypeRow ReadFee(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.IsDBNull(1) ? null : r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetBoolean(6),
        r.GetInt64(7));

    private static CatalogEntryChangeRow ReadChange(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetInt64(3),
        r.GetString(4));
}
