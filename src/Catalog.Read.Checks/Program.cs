using System.Diagnostics;
using Catalog.Read;
using Npgsql;

const string DbName = "ff_kit_dec192_read1";
const string OwnerRole = "dec192mig";
const string AdminRole = "ff_kit_dec192_read1_admin_api";
const string LoginRole = "ff_kit_dec192_read1_login";
const string BootstrapRole = "dec161boot";
const string Host = "127.0.0.1";
const int Port = 5433;
const string CatalogPrincipalSeed = """
    INSERT INTO auth.app_user (user_id, partner_id, status) VALUES
      ('5f0c3e1a-00ca-4000-8000-0000000000a1', NULL, 'active'),
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', NULL, 'active'),
      ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', NULL, 'active')
    ON CONFLICT (user_id) DO NOTHING;
    INSERT INTO auth.user_issuer (user_id, issuer) VALUES
      ('5f0c3e1a-00ca-4000-8000-0000000000a1', 'https://catalog.invalid/scope'),
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'https://catalog.invalid/scope'),
      ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'https://catalog.invalid/scope')
    ON CONFLICT (user_id) DO UPDATE SET issuer = EXCLUDED.issuer;
    INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, partner_id, status, version, granted_by, granted_at) VALUES
      ('5f0c3e1a-00ca-4000-8000-0000000000a1', '5f0c3e1a-00ca-4000-8000-0000000000a1', 'super_admin', 'global', NULL, 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now()),
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'partner_admin', 'partner', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now()),
      ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'partner_admin', 'partner', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now())
    ON CONFLICT (assignment_id) DO NOTHING;
    """;
var partnerA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
var partnerB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
var globalId = Guid.Parse("10000000-0000-4000-8000-0000000000a1");
var feeA = Guid.Parse("10000000-0000-4000-8000-0000000000a2");
var feeB = Guid.Parse("10000000-0000-4000-8000-0000000000a3");
var actor = Guid.Parse("44444444-4444-4444-8444-444444444444");
var log = new List<string>();
var passed = 0;
var failed = 0;

string Cs(string user, string database, bool pooling, int maxPool = 1) =>
    new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Username = user,
        Database = database,
        Pooling = pooling,
        MaxPoolSize = maxPool,
        Timeout = 10,
        CommandTimeout = 10,
    }.ConnectionString;

void Pass(string name, string evidence)
{
    passed++;
    var line = $"PASS {name}: {evidence}";
    log.Add(line);
    Console.WriteLine(line);
}

void Fail(string name, string evidence)
{
    failed++;
    var line = $"FAIL {name}: {evidence}";
    log.Add(line);
    Console.WriteLine(line);
}

async Task<string> Scalar(NpgsqlDataSource ds, string sql)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    var value = await cmd.ExecuteScalarAsync();
    return value switch
    {
        null or DBNull => "",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}

async Task<string> SqlState(NpgsqlDataSource ds, string sql)
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        return "ok";
    }
    catch (PostgresException ex)
    {
        return ex.SqlState;
    }
}

async Task Exec(NpgsqlDataSource ds, string sql, params object[] ps)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
    await cmd.ExecuteNonQueryAsync();
}

var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(repo, "src", "Auth.Data.Migrator", "Auth.Data.Migrator.csproj")))
    repo = "/tmp/ff-dec192-adapter";

await using var bootstrap = NpgsqlDataSource.Create(Cs(BootstrapRole, "postgres", false));
var before = await Scalar(bootstrap, "SELECT string_agg(datname, ',' ORDER BY datname) FROM pg_database");
if (before != "postgres,template0,template1")
{
    Console.Error.WriteLine($"refuse: database list is {before}");
    return 2;
}
log.Add("SECTION scratch-setup");
log.Add($"databases-before {before}");
Console.WriteLine($"databases-before {before}");

try
{
    await Exec(bootstrap, $"CREATE ROLE {OwnerRole} LOGIN NOSUPERUSER NOBYPASSRLS CREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE DATABASE {DbName} OWNER {OwnerRole}");
    await Exec(bootstrap, $"COMMENT ON DATABASE {DbName} IS 'floorfunds-scratch-dec192-read'");
    await Exec(bootstrap, $"CREATE ROLE {AdminRole} LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {LoginRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_auth_definer NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_issuer LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    var ownerName = await Scalar(bootstrap, $"SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = '{DbName}'");
    if (ownerName != OwnerRole)
        throw new InvalidOperationException($"scratch owner is {ownerName}");
    log.Add($"scratch-created {DbName} owner {ownerName}");
    Console.WriteLine($"scratch-created {DbName} owner {ownerName}");

    var migrateInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = "run --project src/Auth.Data.Migrator/Auth.Data.Migrator.csproj --no-build -- update",
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    migrateInfo.Environment["CONNECTION_STRING"] = Cs(OwnerRole, DbName, false);
    var migrate = Process.Start(migrateInfo) ?? throw new InvalidOperationException("migrator did not start");
    var migOut = await migrate.StandardOutput.ReadToEndAsync();
    var migErr = await migrate.StandardError.ReadToEndAsync();
    await migrate.WaitForExitAsync();
    if (migrate.ExitCode != 0 || !migOut.Contains("update: ok", StringComparison.Ordinal))
        throw new InvalidOperationException($"migrate failed ({migrate.ExitCode}) {migErr}");
    log.Add("migrate update: ok");
    Console.WriteLine("migrate update: ok");
    await using (var bootDb = NpgsqlDataSource.Create(Cs(BootstrapRole, DbName, false)))
        await Exec(bootDb, await File.ReadAllTextAsync(Path.Combine(repo, "db", "kit", "definer-owner.sql")));

    await using var owner = NpgsqlDataSource.Create(Cs(OwnerRole, DbName, false));
    await Exec(owner, CatalogPrincipalSeed);
    await using var issuer = NpgsqlDataSource.Create(Cs(DbName + "_issuer", DbName, false));
    async Task<string> OpenCatalogSession(Guid user)
    {
        await using var c = await issuer.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.open_session($1, $2, $3, $4, $5)", c);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = CatalogSessionSql.CatalogIssuer });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = user.ToString("D") });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.UtcNow.AddMinutes(5) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.UtcNow });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
    var fixtureToken = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser));
    await LoadFee(owner, globalId, null, "surcharge", "super", "", fixtureToken);
    await LoadFee(owner, feeA, partnerA, "afee", "partner", partnerA.ToString("D"), fixtureToken);
    await LoadFee(owner, feeB, partnerB, "bfee", "partner", partnerB.ToString("D"), fixtureToken);
    var loaded = await Scalar(owner, "SELECT count(*) FROM catalog.registered_entry");
    if (loaded != "3")
        throw new InvalidOperationException($"fixture row count {loaded}");
    var catalogTokens = new Dictionary<string, string>
    {
        ["super"] = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser)),
        [partnerA.ToString("D")] = await OpenCatalogSession(partnerA),
        [partnerB.ToString("D")] = await OpenCatalogSession(partnerB),
    };
    CatalogSessions.Use(scope => scope.AdminType == "super"
        ? catalogTokens["super"]
        : catalogTokens[scope.PartnerId!.Value.ToString("D")]);
    log.Add("SECTION fixture-load");
    log.Add("fixture rows present: 3 fee_type. Direct owner INSERT. Not a candidate-function result.");
    Console.WriteLine("fixture rows present: 3");

    log.Add("SECTION scratch-role-evidence");
    Console.WriteLine("SECTION scratch-role-evidence");
    await using var adminProbe = NpgsqlDataSource.Create(Cs(AdminRole, DbName, false));
    var version = await Scalar(adminProbe, "SELECT version()");
    Pass("db-version", version);
    var who = await Scalar(adminProbe, "SELECT current_user || ' super=' || rolsuper::text || ' bypass=' || rolbypassrls::text FROM pg_roles WHERE rolname = current_user");
    Pass("admin-role-attributes", who);
    var grants = await Scalar(adminProbe, """
        SELECT coalesce(string_agg(table_name || ':' || privilege_type, ',' ORDER BY table_name, privilege_type), '')
          FROM information_schema.role_table_grants
         WHERE grantee = current_user AND table_schema = 'catalog'
        """);
    Pass("admin-catalog-grants", grants);
    if (grants.Contains("INSERT", StringComparison.Ordinal) || grants.Contains("UPDATE", StringComparison.Ordinal) || grants.Contains("DELETE", StringComparison.Ordinal))
        Fail("admin-catalog-grants-select-only", grants);
    var fnGrants = await Scalar(adminProbe, """
        SELECT count(*) FROM information_schema.role_routine_grants
         WHERE grantee = current_user AND routine_schema = 'catalog'
        """);
    Pass("admin-catalog-execute-grants", fnGrants);
    var economic = await Scalar(adminProbe, """
        SELECT count(*) FROM information_schema.columns
         WHERE table_schema = 'catalog' AND table_name = 'fee_type' AND column_name = 'economic_role'
        """);
    Pass("fee-type-economic-role-column", economic);

    async Task Deny(string name, string sql, string expect)
    {
        var state = await SqlState(adminProbe, sql);
        if (state == expect) Pass(name, state);
        else Fail(name, state);
    }
    await Deny("deny-insert-fee", "INSERT INTO catalog.fee_type (entry_id, family, code, definition, display_name) VALUES ('10000000-0000-4000-8000-0000000000ff','fee_type','x','x','x')", "42501");
    await Deny("deny-update-flag", "UPDATE catalog.registered_entry SET is_active = false", "42501");
    await Deny("deny-delete-entry", "DELETE FROM catalog.registered_entry", "42501");
    await Deny("deny-truncate-entry", "TRUNCATE catalog.registered_entry", "42501");
    await Deny("deny-execute-create", "SELECT catalog.apply_entry_create('10000000-0000-4000-8000-0000000000ff'::uuid,'fee_type','descriptive',NULL,'x',true,'{}'::jsonb,1)", "42501");
    await Deny("deny-execute-claim", "SELECT catalog.apply_claim_edit('10000000-0000-4000-8000-0000000000a1'::uuid, 1, '{}'::jsonb)", "42501");
    await Deny("deny-select-marker", "SELECT 1 FROM catalog.tx_marker", "42501");
    await Deny("deny-select-coord", "SELECT 1 FROM catalog.edit_coord", "42501");
    await Deny("deny-select-pending", "SELECT 1 FROM catalog.history_pending", "42501");
    var bare = await Scalar(adminProbe, "SELECT count(*) FROM catalog.registered_entry");
    if (bare == "0") Pass("rls-missing-context-zero-rows", bare);
    else Fail("rls-missing-context-zero-rows", bare);
    await Exec(adminProbe, "SELECT set_config('app.resolved_partner_ids','not-a-uuid', false), set_config('app.admin_type','partner', false), set_config('app.partner_id','not-a-uuid', false)");
    var invalid = await Scalar(adminProbe, "SELECT count(*) FROM catalog.registered_entry");
    await Exec(adminProbe, "SELECT set_config('app.resolved_partner_ids','', false), set_config('app.admin_type','', false), set_config('app.partner_id','', false)");
    if (invalid == "0") Pass("rls-invalid-partner-zero-rows", invalid);
    else Fail("rls-invalid-partner-zero-rows", invalid);
    await Exec(adminProbe, "SELECT set_config('app.admin_type','super', false), set_config('app.resolved_global_admin','', false)");
    var forgedRows = await Scalar(adminProbe, "SELECT count(*) FROM catalog.registered_entry");
    var forgedHist = await Scalar(adminProbe, "SELECT count(*) FROM catalog.entry_change");
    await Exec(adminProbe, "SELECT set_config('app.admin_type','', false)");
    if (forgedRows == "0" && forgedHist == "0") Pass("admin-type-super-sees-no-private-rows", $"{forgedRows},{forgedHist}");
    else Fail("admin-type-super-sees-no-private-rows", $"{forgedRows},{forgedHist}");
    await using var loginProbe = NpgsqlDataSource.Create(Cs(LoginRole, DbName, false));
    var loginState = await SqlState(loginProbe, "SELECT 1 FROM catalog.registered_entry");
    if (loginState == "42501") Pass("login-catalog-select", loginState);
    else Fail("login-catalog-select", loginState);

    log.Add("SECTION adapter-reads");
    Console.WriteLine("SECTION adapter-reads");
    var scan = ScanAdapter(Path.Combine(repo, "src", "Catalog.Read"));
    if (scan == "clean") Pass("adapter-source-has-no-dml", scan);
    else Fail("adapter-source-has-no-dml", scan);

    await using var pooled = NpgsqlDataSource.Create(Cs(AdminRole, DbName, true, 1));
    var reader = new NpgsqlCatalogReader(pooled);
    var super = CatalogReadScope.Super();
    var scopeA = CatalogReadScope.Partner(partnerA);
    var scopeB = CatalogReadScope.Partner(partnerB);

    var superRows = await reader.ListEntriesAsync(super, CancellationToken.None);
    ExpectCodes("super-sees-global-and-both-private", superRows.Select(r => r.Code), "afee", "bfee", "surcharge");
    var rowsA = await reader.ListEntriesAsync(scopeA, CancellationToken.None);
    ExpectCodes("partner-a-mask", rowsA.Select(r => r.Code), "afee", "surcharge");
    var rowsB = await reader.ListEntriesAsync(scopeB, CancellationToken.None);
    ExpectCodes("partner-b-mask", rowsB.Select(r => r.Code), "bfee", "surcharge");
    if (rowsA.All(r => r.IsActive && r.RowVersion == 1) && rowsB.All(r => r.OwnerPartnerId != partnerA))
        Pass("partner-b-does-not-carry-a", string.Join(",", rowsB.Select(r => r.OwnerPartnerId?.ToString("D") ?? "null")));
    else Fail("partner-b-does-not-carry-a", "row shape");

    var feesA = await reader.ListFeeTypesAsync(scopeA, CancellationToken.None);
    if (feesA.Select(f => f.Code).OrderBy(c => c).SequenceEqual(new[] { "afee", "surcharge" })
        && feesA.All(f => f.Definition == "names only fixture"))
        Pass("partner-a-fee-definition", string.Join(",", feesA.Select(f => f.Code + ":" + f.Definition)));
    else Fail("partner-a-fee-definition", string.Join(",", feesA.Select(f => f.Code)));

    var hidden = await reader.FindEntryAsync(scopeA, feeB, CancellationToken.None);
    var missing = await reader.FindEntryAsync(scopeA, Guid.Parse("10000000-0000-4000-8000-0000000000ff"), CancellationToken.None);
    if (hidden is null && missing is null) Pass("hidden-and-missing-are-empty", "null,null");
    else Fail("hidden-and-missing-are-empty", $"{hidden?.Code},{missing?.Code}");

    var histAGlobal = await reader.ListEntryChangesAsync(scopeA, globalId, CancellationToken.None);
    var histAA = await reader.ListEntryChangesAsync(scopeA, feeA, CancellationToken.None);
    var histAB = await reader.ListEntryChangesAsync(scopeA, feeB, CancellationToken.None);
    var histSuperB = await reader.ListEntryChangesAsync(super, feeB, CancellationToken.None);
    if (histAGlobal.Count == 1 && histAGlobal[0].ChangeKind == "created"
        && histAA.Count == 1 && histAA[0].ChangeKind == "created"
        && histAB.Count == 0 && histSuperB.Count == 1)
        Pass("history-mask", $"global={histAGlobal.Count} own={histAA.Count} other={histAB.Count} superOther={histSuperB.Count}");
    else Fail("history-mask", $"global={histAGlobal.Count} own={histAA.Count} other={histAB.Count} superOther={histSuperB.Count}");

    var switches = await reader.CountPartnerSwitchesAsync(super, CancellationToken.None);
    var locations = await reader.CountLocationSwitchesAsync(scopeA, CancellationToken.None);
    if (switches == 0 && locations == 0) Pass("scope-tables-readable-empty", $"{switches},{locations}");
    else Fail("scope-tables-readable-empty", $"{switches},{locations}");

    var injected = await reader.ListEntriesAsync(super, "surcharge' OR '1'='1", CancellationToken.None);
    var still = await reader.ListEntriesAsync(super, CancellationToken.None);
    if (injected.Count == 0 && still.Count == 3) Pass("parameter-does-not-widen", $"injected={injected.Count} still={still.Count}");
    else Fail("parameter-does-not-widen", $"injected={injected.Count} still={still.Count}");

    await using (var leak = await pooled.OpenConnectionAsync())
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT set_config('app.request_iss', 'https://catalog.invalid/scope', false),
                   set_config('app.request_sub', $1, false),
                   set_config('app.resolved_partner_ids', $1, false),
                   set_config('app.admin_type', 'partner', false),
                   set_config('app.partner_id', $1, false)
            """, leak);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = partnerA.ToString("D") });
        await cmd.ExecuteNonQueryAsync();
        var seen = await new NpgsqlCommand("SELECT count(*) FROM catalog.registered_entry", leak).ExecuteScalarAsync();
        if (Convert.ToString(seen, System.Globalization.CultureInfo.InvariantCulture) != "0")
            throw new InvalidOperationException($"pre-leak count {seen}");
    }
    var missingContext = await ThrowsContext(() => reader.ListEntriesAsync(null, CancellationToken.None));
    if (missingContext) Pass("missing-context-throws", "CatalogReadContextException");
    else Fail("missing-context-throws", "no throw");
    var afterMissing = await PooledGuc(pooled);
    if (afterMissing == "||||0") Pass("missing-context-resets-pool", afterMissing);
    else Fail("missing-context-resets-pool", afterMissing);

    try
    {
        CatalogReadScope.Parse("SUPER", null);
        Fail("invalid-token-throws", "parsed");
    }
    catch (CatalogReadContextException)
    {
        Pass("invalid-token-throws", "CatalogReadContextException");
    }
    try
    {
        CatalogReadScope.Parse("super", partnerA.ToString("D"));
        Fail("super-with-partner-throws", "parsed");
    }
    catch (CatalogReadContextException)
    {
        Pass("super-with-partner-throws", "CatalogReadContextException");
    }
    try
    {
        CatalogReadScope.Parse("partner", "not-a-uuid");
        Fail("bad-partner-text-throws", "parsed");
    }
    catch (CatalogReadContextException)
    {
        Pass("bad-partner-text-throws", "CatalogReadContextException");
    }
    try
    {
        CatalogReadScope.Partner(Guid.Empty);
        Fail("empty-partner-throws", "parsed");
    }
    catch (CatalogReadContextException)
    {
        Pass("empty-partner-throws", "CatalogReadContextException");
    }

    await using (var leak = await pooled.OpenConnectionAsync())
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.resolved_partner_ids', $1, false), set_config('app.admin_type','partner', false), set_config('app.partner_id', $1, false)", leak);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = partnerB.ToString("D") });
        await cmd.ExecuteNonQueryAsync();
    }
    var forced = await reader.ListEntriesAsync(super, CancellationToken.None);
    ExpectCodes("argument-scope-ignores-leftover-session", forced.Select(r => r.Code), "afee", "bfee", "surcharge");
    var afterSuper = await PooledGuc(pooled);
    if (afterSuper == "||||0") Pass("successful-read-resets-pool", afterSuper);
    else Fail("successful-read-resets-pool", afterSuper);

    var noResetBuilder = new NpgsqlConnectionStringBuilder(Cs(AdminRole, DbName, true))
    {
        NoResetOnClose = true,
        MaxPoolSize = 1,
    };
    await using var noReset = NpgsqlDataSource.Create(noResetBuilder.ConnectionString);
    var noResetReader = new NpgsqlCatalogReader(noReset);
    await using (var leak = await noReset.OpenConnectionAsync())
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT set_config('app.request_iss', 'https://catalog.invalid/scope', false),
                   set_config('app.request_sub', $1, false)
            """, leak);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = partnerA.ToString("D") });
        await cmd.ExecuteNonQueryAsync();
        var seen = await new NpgsqlCommand("SELECT count(*) FROM catalog.registered_entry", leak).ExecuteScalarAsync();
        if (Convert.ToString(seen, System.Globalization.CultureInfo.InvariantCulture) != "0")
            throw new InvalidOperationException($"no-reset plant count {seen}");
    }
    var clearedRead = await noResetReader.ListEntriesAsync(super, CancellationToken.None);
    var afterClear = await PooledGuc(noReset);
    var issLeft = await Scalar(noReset, "SELECT coalesce(current_setting('app.request_iss', true), '') || '|' || coalesce(current_setting('app.request_sub', true), '')");
    // SetScope clears the flag GUCs. Clear is what wipes request_iss and request_sub. A no-op Clear leaves the plant.
    if (clearedRead.Count == 3 && afterClear == "||||0" && issLeft == "|") Pass("clear-wipes-principal-when-reset-disabled", afterClear);
    else Fail("clear-wipes-principal-when-reset-disabled", $"rows {clearedRead.Count} guc {afterClear} iss {issLeft}");

    await using var ownerReaderDs = NpgsqlDataSource.Create(Cs(OwnerRole, DbName, false));
    var ownerThrew = await ThrowsRole(() => new NpgsqlCatalogReader(ownerReaderDs).ListEntriesAsync(super, CancellationToken.None));
    if (ownerThrew) Pass("owner-role-refused", "CatalogReadRoleException");
    else Fail("owner-role-refused", "no role exception");
    await using var loginReaderDs = NpgsqlDataSource.Create(Cs(LoginRole, DbName, false));
    var loginThrew = await ThrowsRole(() => new NpgsqlCatalogReader(loginReaderDs).ListEntriesAsync(super, CancellationToken.None));
    if (loginThrew) Pass("login-role-refused", "CatalogReadRoleException");
    else Fail("login-role-refused", "no role exception");
    var stillThere = await Scalar(adminProbe, "SELECT count(*) FROM catalog.registered_entry");
    // admin probe has no GUC, so RLS count is 0. Confirm rows with the owner.
    var ownerCount = await Scalar(owner, "SELECT count(*) FROM catalog.registered_entry");
    if (stillThere == "0" && ownerCount == "3") Pass("refusals-did-not-persist-a-write", $"adminBare={stillThere} owner={ownerCount}");
    else Fail("refusals-did-not-persist-a-write", $"adminBare={stillThere} owner={ownerCount}");

    async Task<string> ClosedShape(string token)
    {
        try
        {
            await reader.ListEntriesAsync(CatalogReadScope.Super().WithSession(token), CancellationToken.None);
            return "opened";
        }
        catch (CatalogSessionClosedException)
        {
            return CatalogSessionClosedException.StatusCode + "|" + CatalogSessionClosedException.Body + "|" + CatalogSessionClosedException.Authenticate;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + " " + ex.Message;
        }
    }
    var closedA = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser));
    var closedB = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser));
    var closedC = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser));
    await Exec(owner, "UPDATE auth.request_session SET expires_at = clock_timestamp() - interval '1 second' WHERE token_hash = sha256(convert_to($1, 'UTF8'))", closedA);
    await Exec(owner, "UPDATE auth.request_session SET revoked_at = clock_timestamp() WHERE token_hash = sha256(convert_to($1, 'UTF8'))", closedB);
    var shapeA = await ClosedShape(closedA);
    var shapeB = await ClosedShape(closedB);
    await Exec(issuer, "SELECT auth.revoke_login($1, $2)", CatalogSessionSql.CatalogIssuer, CatalogSessionSql.CatalogSuperUser);
    var shapeC = await ClosedShape(closedC);
    var oneShape = CatalogSessionClosedException.StatusCode + "|" + CatalogSessionClosedException.Body + "|" + CatalogSessionClosedException.Authenticate;
    if (shapeA == oneShape && shapeA == shapeB && shapeB == shapeC) Pass("catalog-closed-sessions-are-one-exception", shapeA);
    else Fail("catalog-closed-sessions-are-one-exception", $"a {shapeA} b {shapeB} c {shapeC}");

    log.Add("SECTION not-released");
    log.Add("candidate-function tests: not added");
    log.Add("outbox and schema: unchanged");
    log.Add("synthetic doubles: none; PASS lines are server results");
    Console.WriteLine("candidate-function tests: not added");
}
catch (Exception ex)
{
    Fail("run", ex.GetType().Name + " " + ex.Message);
}
finally
{
  try
  {
    await using var drop = NpgsqlDataSource.Create(Cs(BootstrapRole, "postgres", false));
    await Exec(drop, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DbName}' AND pid <> pg_backend_pid()");
    var exists = await Scalar(drop, $"SELECT count(*) FROM pg_database WHERE datname = '{DbName}'");
    if (exists == "1")
    {
        var ownerOk = await Scalar(drop, $"SELECT pg_catalog.pg_get_userbyid(datdba) = '{OwnerRole}' AND datname = '{DbName}' FROM pg_database WHERE datname = '{DbName}'");
        if (ownerOk is "true" or "True" or "t")
            await Exec(drop, $"DROP DATABASE {DbName}");
    }
    foreach (var role in new[] { AdminRole, LoginRole, OwnerRole, DbName + "_auth_definer", DbName + "_issuer" })
    {
        var present = await Scalar(drop, $"SELECT count(*) FROM pg_roles WHERE rolname = '{role}'");
            if (present == "1")
        {
            try { await Exec(drop, $"DROP ROLE {role}"); }
            catch (PostgresException ex) { log.Add($"drop-role {role} {ex.SqlState}"); }
        }
    }
    var after = await Scalar(drop, "SELECT string_agg(datname, ',' ORDER BY datname) FROM pg_database");
    var roles = await Scalar(drop, "SELECT coalesce(string_agg(rolname, ',' ORDER BY rolname), '') FROM pg_roles WHERE rolname LIKE 'ff_kit%' OR rolname LIKE 'dec%'");
    log.Add($"databases-after {after}");
    log.Add($"roles-after {roles}");
    Console.WriteLine($"databases-after {after}");
    Console.WriteLine($"roles-after {roles}");
    if (after != "postgres,template0,template1" || roles != BootstrapRole)
        Fail("scratch-cleaned", $"{after} roles {roles}");
    else Pass("scratch-cleaned", $"{after} roles {roles}");
  }
  catch (Exception ex)
  {
    Fail("scratch-cleanup", ex.GetType().Name + " " + ex.Message);
  }
}

var summary = $"{passed} passed, {failed} failed";
log.Add(summary);
Console.WriteLine(summary);
Directory.CreateDirectory("/opt/cursor/artifacts");
await File.WriteAllLinesAsync("/opt/cursor/artifacts/dec192-read-adapter-log.txt", log);
return failed == 0 ? 0 : 1;

void ExpectCodes(string name, IEnumerable<string> actual, params string[] expect)
{
    var got = actual.OrderBy(c => c, StringComparer.Ordinal).ToArray();
    var want = expect.OrderBy(c => c, StringComparer.Ordinal).ToArray();
    if (got.SequenceEqual(want)) Pass(name, string.Join(",", got));
    else Fail(name, string.Join(",", got));
}

static async Task<bool> ThrowsContext(Func<Task> call)
{
    try { await call(); return false; }
    catch (CatalogReadContextException) { return true; }
}

static async Task<bool> ThrowsRole(Func<Task> call)
{
    try { await call(); return false; }
    catch (CatalogReadRoleException) { return true; }
}

async Task<string> PooledGuc(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT coalesce(current_setting('app.resolved_global_admin', true), '') || '|' ||
               coalesce(current_setting('app.resolved_partner_ids', true), '') || '|' ||
               coalesce(current_setting('app.admin_type', true), '') || '|' ||
               coalesce(current_setting('app.partner_id', true), '') || '|' ||
               (SELECT count(*)::text FROM catalog.registered_entry)
        """, conn);
    var value = await cmd.ExecuteScalarAsync();
    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

async Task LoadFee(NpgsqlDataSource ds, Guid id, Guid? ownerId, string code, string adminType, string partner, string sessionToken)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var bind = new NpgsqlCommand("SELECT auth.begin_request($1)", conn, tx))
    {
        bind.Parameters.Add(new NpgsqlParameter<string> { TypedValue = sessionToken });
        await bind.ExecuteNonQueryAsync();
    }
    await using (var guc = new NpgsqlCommand("""
        SELECT set_config('app.resolved_global_admin', CASE WHEN $1 = 'super' THEN 'on' ELSE '' END, true),
               set_config('app.resolved_partner_ids', CASE WHEN $1 = 'partner' THEN $2 ELSE '' END, true),
               set_config('app.admin_type', '', true),
               set_config('app.partner_id', '', true),
               set_config('app.actor_user_id', $3, true),
               set_config('app.correlation_id', $4, true),
               set_config('app.reason', 'read fixture', true)
        """, conn, tx))
    {
        guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = adminType });
        guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = partner });
        guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor.ToString("D") });
        guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = "dec192-fixture-" + code });
        await guc.ExecuteNonQueryAsync();
    }
    await using (var entry = new NpgsqlCommand("""
        INSERT INTO catalog.registered_entry (
          entry_id, family, entry_class, owner_partner_id, code, is_active, row_version,
          created_by, created_at, updated_by, updated_at)
        VALUES ($1, 'fee_type', 'descriptive', $2, $3, true, 1, $4, clock_timestamp(), $4, clock_timestamp())
        """, conn, tx))
    {
        entry.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        entry.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = ownerId });
        entry.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });
        entry.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = actor });
        await entry.ExecuteNonQueryAsync();
    }
    await using (var fee = new NpgsqlCommand("""
        INSERT INTO catalog.fee_type (
          entry_id, family, code, owner_partner_id, definition, display_name, description, release_id)
        VALUES ($1, 'fee_type', $2, $3, 'names only fixture', $4, NULL, NULL)
        """, conn, tx))
    {
        fee.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        fee.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });
        fee.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = ownerId });
        fee.Parameters.Add(new NpgsqlParameter<string> { TypedValue = code });
        await fee.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
}

static string ScanAdapter(string dir)
{
    var banned = new[] { "INSERT ", "UPDATE ", "DELETE ", "TRUNCATE", "apply_entry_create", "apply_claim_edit", "EXECUTE " };
    foreach (var file in Directory.GetFiles(dir, "*.cs"))
    {
        var text = File.ReadAllText(file);
        foreach (var token in banned)
            if (text.Contains(token, StringComparison.Ordinal))
                return Path.GetFileName(file) + " contains " + token.Trim();
    }
    return "clean";
}
