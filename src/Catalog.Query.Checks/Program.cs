using System.Diagnostics;
using System.Text.Json;
using Catalog.Contracts;
using Catalog.Query;
using Catalog.Read;
using Npgsql;

if (args.Contains("--catalog-read-check")) return await CatalogReadProbe.Run();

const string DbName = "ff_kit_dec192_query1";
const string OwnerRole = "dec192qmig";
const string AdminRole = "ff_kit_dec192_query1_admin_api";
const string LoginRole = "ff_kit_dec192_query1_login";
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
    ;
    INSERT INTO auth.app_user (user_id, partner_id, status) VALUES
      ('44444444-4444-4444-8444-444444444444', NULL, 'active')
    ON CONFLICT (user_id) DO NOTHING;
    INSERT INTO auth.user_issuer (user_id, issuer) VALUES
      ('44444444-4444-4444-8444-444444444444', 'https://catalog.invalid/scope')
    ;
    INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, partner_id, status, version, granted_by, granted_at) VALUES
      ('5f0c3e1a-00ca-4000-8000-0000000000a1', '5f0c3e1a-00ca-4000-8000-0000000000a1', 'super_admin', 'global', NULL, 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now()),
      ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'partner_admin', 'partner', 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now()),
      ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'partner_admin', 'partner', 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now()),
      ('44444444-4444-4444-8444-444444444444', '44444444-4444-4444-8444-444444444444', 'super_admin', 'global', NULL, 'active', 1, '5f0c3e1a-00ca-4000-8000-0000000000a1', now())
    ON CONFLICT (assignment_id) DO NOTHING;
    """;
var partnerA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
var partnerB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
var fixtureActor = Guid.Parse("44444444-4444-4444-8444-444444444444");
var missingId = Guid.Parse("10000000-0000-4000-8000-0000000000ff");
var log = new List<string>();
var passed = 0;
var failed = 0;

var actors = new[]
{
    Actor("super", CatalogRole.SuperAdmin, null, 1),
    Actor("partner-a", CatalogRole.PartnerAdmin, partnerA, 2),
    Actor("partner-b", CatalogRole.PartnerAdmin, partnerB, 3),
    Actor("client-admin", CatalogRole.ClientAdmin, null, 4),
    Actor("supervisor", CatalogRole.Supervisor, null, 5),
    Actor("cashier", CatalogRole.Cashier, null, 6),
    Actor("auditor", CatalogRole.Auditor, null, 7),
    Actor("accounting", CatalogRole.Accounting, null, 8),
    Actor("support", CatalogRole.Support, null, 9),
    Actor("none", CatalogRole.None, null, 10),
};
var fixtures = BuildFixtures();

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

void Note(string line)
{
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

async Task Exec(NpgsqlDataSource ds, string sql)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
}

var repo = RepoRoot();
Note("SECTION source-identity");
Note("contract tree reconstructed on 01a616c plus Catalog.Contracts and Catalog.Contracts.Checks: 3fe0a9554bab16e0cf6655479bfe0572a4c45e49");
Note("ui tree reconstructed on 01a616c plus the full supplied patch: 0c5d22100733a5b725ed6ad264ed267b39820e60");
Note("commit objects 2bba107, e94374e, and ab458ae are not in this clone");
var domainBlob = GitHash(Path.Combine(repo, "src", "Catalog.Contracts", "Doubles", "CatalogDomainService.cs"));
var principalBlob = GitHash(Path.Combine(repo, "src", "Catalog.Contracts", "CatalogPrincipal.cs"));
var readerBlob = GitHash(Path.Combine(repo, "src", "Catalog.Read", "NpgsqlCatalogReader.cs"));
var grantsBlob = GitHash(Path.Combine(repo, "src", "Auth.Data", "Migrations", "CatalogMasterData.Grants.sql"));
var grantsText = File.ReadAllText(Path.Combine(repo, "src", "Auth.Data", "Migrations", "CatalogMasterData.Grants.sql"));
if (domainBlob == "0cb83a0ebefa309a5379b65685e6ecc68d144453")
    Pass("imported-visible-source", domainBlob);
else
    Fail("imported-visible-source", domainBlob);
if (principalBlob == "5bb556ebf38c4d93e2535fdbafddfb6f5277f790")
    Pass("imported-principal-source", principalBlob);
else
    Fail("imported-principal-source", principalBlob);
var readerText = File.ReadAllText(Path.Combine(repo, "src", "Catalog.Read", "NpgsqlCatalogReader.cs"));
if (readerText.Contains("CatalogSessionSql.SetScope", StringComparison.Ordinal)
    && !readerText.Contains("set_config('app.admin_type'", StringComparison.Ordinal))
    Pass("raw-reader-uses-resolved-scope", readerBlob);
else
    Fail("raw-reader-uses-resolved-scope", readerBlob);
if (grantsText.Contains("app.resolved_global_admin", StringComparison.Ordinal)
    && !grantsText.Contains("app.admin_type", StringComparison.Ordinal))
    Pass("grants-use-resolved-scope", grantsBlob);
else
    Fail("grants-use-resolved-scope", grantsBlob);

if (!grantsText.Contains("WHEN current_setting('app.admin_type', true) = 'super' THEN true", StringComparison.Ordinal)
    && grantsText.Contains("current_setting('app.resolved_global_admin', true) = 'on'", StringComparison.Ordinal))
    Pass("super-storage-branch-replaced", "resolved_global_admin");
else
    Fail("super-storage-branch-replaced", "admin_type branch still present");
if (!readerText.Contains("model_hardware_claim", StringComparison.Ordinal)
    && !readerText.Contains("INSERT ", StringComparison.Ordinal)
    && !readerText.Contains("apply_entry_create", StringComparison.Ordinal))
    Pass("raw-reader-has-no-family-filter", "no claim token and no write");
else
    Fail("raw-reader-has-no-family-filter", "reader text changed shape");

var queryDir = Path.Combine(repo, "src", "Catalog.Query");
var queryScan = Scan(queryDir);
if (queryScan == "clean")
    Pass("query-source-has-no-write-or-host", queryScan);
else
    Fail("query-source-has-no-write-or-host", queryScan);

Note("SECTION history-shape");
ProjectParity();
var historySql = File.ReadAllText(Path.Combine(repo, "src", "Catalog.Query", "NpgsqlAllowlistedHistory.cs"));
var historySqlClean = !historySql.Contains("actor_user_id", StringComparison.Ordinal)
    && !historySql.Contains("created_by", StringComparison.Ordinal)
    && !historySql.Contains("definition", StringComparison.Ordinal)
    && !historySql.Contains("legal_name", StringComparison.Ordinal)
    && !historySql.Contains("SELECT after", StringComparison.Ordinal)
    && !historySql.Contains("SELECT before", StringComparison.Ordinal)
    && historySql.Contains("CASE WHEN change_kind = 'created' THEN after #>> '{subtype,code}'", StringComparison.Ordinal)
    && historySql.Contains("CASE WHEN change_kind = 'created' THEN after #>> '{subtype,display_name}'", StringComparison.Ordinal)
    && historySql.Contains("CASE WHEN change_kind = 'created' THEN after #>> '{subtype,description}'", StringComparison.Ordinal)
    && historySql.Contains("CASE WHEN change_kind = 'updated' THEN after #>> '{subtype,claim_value}'", StringComparison.Ordinal)
    && historySql.Contains("CASE WHEN change_kind = 'updated' THEN before #>> '{subtype,claim_value}'", StringComparison.Ordinal);
if (historySqlClean) Pass("history-sql-is-allowlisted-paths", "no actor, definition, or document select");
else Fail("history-sql-is-allowlisted-paths", "select list drifted");
var contractNames = string.Join(",", typeof(Catalog.Contracts.HistoryProjection).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
var queryNames = string.Join(",", typeof(CatalogQueryHistory).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
if (contractNames == queryNames && !queryNames.Contains("Actor", StringComparison.Ordinal) && !queryNames.Contains("Scope", StringComparison.Ordinal))
    Pass("history-dto-matches-contract-members", queryNames);
else
    Fail("history-dto-matches-contract-members", contractNames + " vs " + queryNames);

NpgsqlDataSource? ownerDs = null;
Note("SECTION memory-projection");
var memory = new MemoryStorage(fixtures);
var memoryQuery = new CatalogQuery(memory);
await RunMatrix("MEMORY", memoryQuery, memory);
await RunFocused("MEMORY", memoryQuery, memory);

Note("SECTION scratch-setup");
await using var bootstrap = NpgsqlDataSource.Create(Cs(BootstrapRole, "postgres", false));
var before = await Scalar(bootstrap, "SELECT string_agg(datname, ',' ORDER BY datname) FROM pg_database");
if (before != "postgres,template0,template1")
{
    Fail("scratch-refused", $"database list is {before}");
    return Exit();
}
Note($"databases-before {before}");
NpgsqlDataSource? pooled = null;
try
{
    await Exec(bootstrap, $"CREATE ROLE {OwnerRole} LOGIN NOSUPERUSER NOBYPASSRLS CREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE DATABASE {DbName} OWNER {OwnerRole}");
    await Exec(bootstrap, $"COMMENT ON DATABASE {DbName} IS 'floorfunds-scratch-dec192-query-nonproduct'");
    await Exec(bootstrap, $"CREATE ROLE {AdminRole} LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {LoginRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_auth_definer NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_auth_owner NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_catalog_definer NOLOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {DbName}_issuer LOGIN NOINHERIT NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    Note($"scratch-created {DbName} owner {OwnerRole}");

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
    Note("migrate update: ok");
    await using (var bootDb = NpgsqlDataSource.Create(Cs(BootstrapRole, DbName, false)))
        await Exec(bootDb, await File.ReadAllTextAsync(Path.Combine(repo, "db", "kit", "definer-owner.sql")));

    var seedPassword = await KitAdmin.InstallTestSeed(Host, Port, BootstrapRole, DbName, Path.Combine(repo, "db", "kit", "test-seed-role.sql"));
    ownerDs = NpgsqlDataSource.Create(Cs(OwnerRole, DbName, false));
    await using var seedDs = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(Cs(DbName + "_test_seed", DbName, false)) { Password = seedPassword }.ConnectionString);
    await Exec(seedDs, CatalogPrincipalSeed);
    await using var issuerDs = NpgsqlDataSource.Create(Cs(DbName + "_issuer", DbName, false));
    async Task<string> OpenCatalogSession(Guid user)
    {
        await using var c = await issuerDs.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.open_session($1, $2, $3, $4, $5)", c);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = CatalogSessionSql.CatalogIssuer });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = user.ToString("D") });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.NewGuid() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.UtcNow.AddMinutes(5) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.UtcNow });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
    KitAdmin.Arm(DbName, Port);
    KitAdmin.Exec("catalog-query-fixture", KitAdmin.SessionThen(
        DbName + "_issuer",
        CatalogSessionSql.CatalogIssuer,
        fixtureActor.ToString("D"),
        "dec192-query-nonproduct-fixture",
        "nonproduct synthetic fixture",
        CatalogFixtureSql(fixtureActor, partnerA, partnerB)));
    var loaded = await Scalar(ownerDs, "SELECT count(*) FROM catalog.registered_entry");
    if (loaded != fixtures.Count.ToString())
        throw new InvalidOperationException($"BLOCKER fixture row count {loaded}, expected {fixtures.Count}");
    var catalogTokens = new Dictionary<string, string>
    {
        ["super"] = await OpenCatalogSession(Guid.Parse(CatalogSessionSql.CatalogSuperUser)),
        [partnerA.ToString("D")] = await OpenCatalogSession(partnerA),
        [partnerB.ToString("D")] = await OpenCatalogSession(partnerB),
    };
    CatalogSessions.Use(scope => scope.AdminType == "super"
        ? catalogTokens["super"]
        : catalogTokens[scope.PartnerId!.Value.ToString("D")]);
    Note("SECTION fixture-load");
    Note("nonproduct synthetic fixture");
    Note($"fixture rows present: {loaded}");
    var storedActor = await Scalar(ownerDs, $"SELECT actor_user_id::text FROM catalog.entry_change WHERE entry_id = '{Id(20)}'");
    if (storedActor == fixtureActor.ToString("D"))
        Pass("history-row-stores-actor-that-the-dto-must-omit", storedActor);
    else
        Fail("history-row-stores-actor-that-the-dto-must-omit", storedActor);

    await using var adminBare = NpgsqlDataSource.Create(Cs(AdminRole, DbName, false));
    var insertState = await SqlState(adminBare, "INSERT INTO catalog.fee_type (entry_id, family, code, definition, display_name) VALUES ('10000000-0000-4000-8000-0000000000fe','fee_type','x','x','x')");
    if (insertState == "42501") Pass("admin-api-insert-still-denied", insertState);
    else Fail("admin-api-insert-still-denied", insertState);
    var executeState = await SqlState(adminBare, "SELECT catalog.apply_entry_create('10000000-0000-4000-8000-0000000000fe'::uuid,'fee_type','descriptive',NULL,'x',true,'{}'::jsonb,1)");
    if (executeState == "42501") Pass("admin-api-execute-still-denied", executeState);
    else Fail("admin-api-execute-still-denied", executeState);

    pooled = NpgsqlDataSource.Create(Cs(AdminRole, DbName, true, 4));
    var reader = new NpgsqlCatalogReader(pooled);
    var storage = new CountingStorage(new NpgsqlCatalogStorage(pooled));
    var query = new CatalogQuery(storage);
    var rawSuper = await reader.ListEntriesAsync(CatalogReadScope.Super(), "g", CancellationToken.None);
    var rawFees = string.Join(",", rawSuper.Where(row => row.Family == "fee_type").Select(row => row.EntryId).OrderBy(id => id));
    var rawHasPrivate = rawSuper.Any(row => row.EntryId == Id(26)) && rawSuper.Any(row => row.EntryId == Id(27));
    if (rawHasPrivate)
        Pass("raw-super-still-reads-private-fees", rawFees);
    else
        Fail("raw-super-still-reads-private-fees", rawFees);

    Note("SECTION db-matrix");
    await RunMatrix("DB", query, storage);
    await RunFocused("DB", query, storage);

    var saFees = await query.ListAsync(actors[0].Principal, CatalogFamily.FeeType, CancellationToken.None);
    var saFeeIds = string.Join(",", (saFees.Entries ?? []).Select(entry => entry.EntryId));
    var saFeeSet = (saFees.Entries ?? []).Select(entry => entry.EntryId).OrderBy(id => id).ToArray();
    if (saFees.Status == 200 && saFeeSet.SequenceEqual(new[] { Id(20), Id(26), Id(27) }))
        Pass("query-sa-fee-list-includes-private", saFeeIds);
    else
        Fail("query-sa-fee-list-includes-private", $"{saFees.Status} {saFeeIds}");

    var guc = await PooledGuc(pooled);
    if (PoolClear(guc)) Pass("pool-after-query", guc);
    else Fail("pool-after-query", guc);

    var threw = false;
    try
    {
        await new CatalogQuery(new NpgsqlCatalogStorage(ownerDs)).ListAsync(actors[0].Principal, CatalogFamily.FeeType, CancellationToken.None);
    }
    catch (CatalogReadRoleException)
    {
        threw = true;
    }
    if (threw) Pass("owner-connection-still-refused", "CatalogReadRoleException");
    else Fail("owner-connection-still-refused", "no role exception");
    var gucAfterThrow = await PooledGuc(pooled);
    if (PoolClear(gucAfterThrow)) Pass("pool-unchanged-by-owner-refusal", gucAfterThrow);
    else Fail("pool-unchanged-by-owner-refusal", gucAfterThrow);

    Note("pool-after-query reads connections only after they return to the pool. Cs() leaves No Reset On Close unset, so Npgsql 10.0.3 resets first. That sample stayed ||0 for writer mutant 5. It is not the session-cleanup proof.");
    await SessionCleanupProof(Id(20));
}
catch (Exception ex)
{
    Fail("run", ex.GetType().Name + " " + ex.Message);
    if (ex.Message.Contains("BLOCKER", StringComparison.Ordinal))
        Note("BLOCKER fixture setup stopped. No trigger was disabled and no grant was added.");
}
finally
{
    if (pooled is not null) await pooled.DisposeAsync();
    if (ownerDs is not null) await ownerDs.DisposeAsync();
    try
    {
        await using var drop = NpgsqlDataSource.Create(Cs(BootstrapRole, "postgres", false));
        await Exec(drop, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{DbName}' AND pid <> pg_backend_pid()");
        var exists = await Scalar(drop, $"SELECT count(*) FROM pg_database WHERE datname = '{DbName}'");
        if (exists == "1")
        {
            var ownerOk = await Scalar(drop, $"SELECT pg_catalog.pg_get_userbyid(datdba) = '{OwnerRole}' FROM pg_database WHERE datname = '{DbName}'");
            if (ownerOk is "true" or "True" or "t")
                await Exec(drop, $"DROP DATABASE {DbName}");
        }
        foreach (var role in new[] { AdminRole, LoginRole, OwnerRole, DbName + "_auth_definer", DbName + "_auth_owner", DbName + "_catalog_definer", DbName + "_issuer", DbName + "_test_seed" })
        {
            var present = await Scalar(drop, $"SELECT count(*) FROM pg_roles WHERE rolname = '{role}'");
            if (present == "1")
            {
                try { await Exec(drop, $"DROP ROLE {role}"); }
                catch (PostgresException ex) { Note($"drop-role {role} {ex.SqlState}"); }
            }
        }
        var after = await Scalar(drop, "SELECT string_agg(datname, ',' ORDER BY datname) FROM pg_database");
        var roles = await Scalar(drop, "SELECT coalesce(string_agg(rolname, ',' ORDER BY rolname), '') FROM pg_roles WHERE rolname LIKE 'ff_kit%' OR rolname LIKE 'dec%'");
        Note($"databases-after {after}");
        Note($"roles-after {roles}");
        if (after == "postgres,template0,template1" && roles == BootstrapRole)
            Pass("scratch-cleaned", $"{after} roles {roles}");
        else
            Fail("scratch-cleaned", $"{after} roles {roles}");
    }
    catch (Exception ex)
    {
        Fail("scratch-cleaned", ex.GetType().Name + " " + ex.Message);
    }
}

Note("SECTION limits");
Note("PQ-252.SA2 is not closed. This projection is the accepted contract fail-closed rule.");
Note("RLS, grants, raw reader, schema, and auth mapping were not changed.");
Note("No token mapping, host registration, product seed, write adapter, outbox, or candidate-function test.");
Note("Fee updated, claim created, and the other eighteen families stay closed. No accepted value allowlist names them.");
Note("Claim-edit before and after are projector parity. apply_claim_edit is not called and no updated row is stored.");
Note("The 409 catalog_version_conflict result is the CatalogQueryResult wrapper. It is not HTTP contract parity.");
Note("Writer mutant 5 at 478b540 passed 447/0 because the pool sample ran after the default driver reset. The reviewer probe with No Reset On Close saw super||1 on the same class of mutant. This run keeps transaction-local set_config and the finally clear, and samples the session before that reset can mask it.");
Note("Finally clear stays. Default driver reset is not a reason to remove it.");
Note("PQ-252.SA2, Super Admin security, and integration stay unresolved. SSE2 rereview of this test correction is still required.");
Note($"checks {passed} passed, {failed} failed");
return Exit();

int Exit()
{
    var path = "/opt/cursor/artifacts/dec192-history-poolproof-log.txt";
    File.WriteAllLines(path, log);
    Console.WriteLine($"log {path}");
    return failed == 0 ? 0 : 1;
}

void ProjectParity()
{
    var occurred = DateTimeOffset.Parse("2026-11-08T19:00:00Z");
    var fee = new AllowlistedHistoryScalars(
        Guid.Parse("30000000-0000-4000-8000-000000000099"), Id(20), "created", 1, occurred,
        "g", "nonproduct", null,
        "old", "next", "unknown", "unknown", null, null, "t0", "t1", "name", "name", null, "leak");
    var projected = HistoryAllowlist.Project("fee_type", fee);
    var feeOk = projected.ChangedFields.SequenceEqual(["code", "display_name"])
        && projected.Before is null
        && projected.After.Count == 2
        && projected.After["code"] == "g"
        && projected.After["display_name"] == "nonproduct"
        && !projected.After.ContainsKey("description")
        && !projected.After.ContainsKey("claim_value")
        && projected.OccurredAt == occurred
        && !Json(CatalogQueryResult.OkHistory([projected])).Contains("leak", StringComparison.Ordinal);
    if (feeOk) Pass("fee-create-shape-matches-contract-allowlist", "code, display_name; description omitted");
    else Fail("fee-create-shape-matches-contract-allowlist", Json(CatalogQueryResult.OkHistory([projected])));

    var edited = new AllowlistedHistoryScalars(
        Guid.Parse("30000000-0000-4000-8000-000000000098"), Id(28), "updated", 2, occurred,
        "should-not-copy", "should-not-copy", "should-not-copy",
        "old", "next", "unknown", "unknown", null, null,
        "2026-11-08T12:00:00-07:00", "2026-11-08T19:00:00Z",
        "nonproduct", "nonproduct", null, null);
    var claim = HistoryAllowlist.Project("model_hardware_claim", edited);
    var claimOk = claim.ChangedFields.SequenceEqual(["checked_at", "claim_value"])
        && claim.Before is not null
        && claim.Before.Count == 2
        && claim.After.Count == 2
        && claim.Before["claim_value"] == "old"
        && claim.After["claim_value"] == "next"
        && claim.Before["checked_at"] == "2026-11-08T12:00:00-07:00"
        && claim.After["checked_at"] == "2026-11-08T19:00:00Z"
        && !claim.After.ContainsKey("availability")
        && !claim.After.ContainsKey("display_name")
        && !claim.After.ContainsKey("code")
        && !Json(CatalogQueryResult.OkHistory([claim])).Contains("should-not-copy", StringComparison.Ordinal);
    if (claimOk) Pass("claim-edit-shape-matches-contract-changed-keys", "checked_at, claim_value");
    else Fail("claim-edit-shape-matches-contract-changed-keys", Json(CatalogQueryResult.OkHistory([claim])));

    var createdClaim = HistoryAllowlist.Project("model_hardware_claim", fee with { ChangeKind = "created", EntryId = Id(28), AfterClaimValue = "secret" });
    if (createdClaim.ChangedFields.Count == 0 && createdClaim.After.Count == 0 && createdClaim.Before is null)
        Pass("claim-create-stays-closed", "edit allowlist is not a create map");
    else
        Fail("claim-create-stays-closed", Json(CatalogQueryResult.OkHistory([createdClaim])));

    var other = HistoryAllowlist.Project("manufacturer", fee with { EntryId = Id(5) });
    if (other.ChangedFields.Count == 0 && other.After.Count == 0)
        Pass("family-without-allowlist-stays-closed", "manufacturer");
    else
        Fail("family-without-allowlist-stays-closed", Json(CatalogQueryResult.OkHistory([other])));

    Note("GAP fee updated has no accepted value allowlist and stays closed.");
    Note("GAP model_hardware_claim created is not a claim edit. ClaimEditValueKeys are not copied.");
    Note("GAP the other eighteen families have no accepted history value allowlist.");
    Note("GAP a stored updated claim row is produced by apply_claim_edit. This gate does not call it. The edit map above is projector parity, not a stored updated row.");
}

static bool HistoryShape(string wire, CatalogQueryHistory row)
{
    if (row.Before is not null || row.OccurredAt == default)
        return false;
    if (wire == "fee_type")
    {
        return row.ChangedFields.SequenceEqual(["code", "display_name"])
            && row.After.Count == 2
            && row.After["code"] == "g"
            && row.After["display_name"] == "nonproduct"
            && !row.After.ContainsKey("description")
            && !row.After.ContainsKey("definition");
    }
    return row.ChangedFields.Count == 0 && row.After.Count == 0;
}

async Task StoredImageStaysOutOfDto(CatalogQuery query)
{
    var definition = await Scalar(ownerDs!, $"SELECT after #>> '{{subtype,definition}}' FROM catalog.entry_change WHERE entry_id = '{Id(20)}'");
    var createdBy = await Scalar(ownerDs!, $"SELECT after->>'created_by' FROM catalog.entry_change WHERE entry_id = '{Id(20)}'");
    var legal = await Scalar(ownerDs!, $"SELECT after #>> '{{subtype,legal_name}}' FROM catalog.entry_change WHERE entry_id = '{Id(5)}'");
    var claimValue = await Scalar(ownerDs!, $"SELECT after #>> '{{subtype,claim_value}}' FROM catalog.entry_change WHERE entry_id = '{Id(8)}'");
    var updated = await Scalar(ownerDs!, "SELECT count(*) FROM catalog.entry_change WHERE change_kind = 'updated'");
    var feeHistory = await query.HistoryAsync(actors[0].Principal, CatalogFamily.FeeType, Id(20), null, CancellationToken.None);
    var makerHistory = await query.HistoryAsync(actors[1].Principal, CatalogFamily.Manufacturer, Id(5), null, CancellationToken.None);
    var claimHistory = await query.HistoryAsync(actors[0].Principal, CatalogFamily.ModelHardwareClaim, Id(8), null, CancellationToken.None);
    await using var conn = await ownerDs!.OpenConnectionAsync();
    await using var atCmd = new NpgsqlCommand("SELECT at FROM catalog.entry_change WHERE entry_id = $1", conn);
    atCmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Id(20) });
    var atValue = await atCmd.ExecuteScalarAsync();
    var at = atValue is DateTimeOffset dto ? dto : atValue is DateTime time ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)) : default;
    var feeJson = Json(feeHistory);
    var storedOk = definition == "names only fixture"
        && createdBy == fixtureActor.ToString("D")
        && legal == "nonproduct"
        && claimValue == "nonproduct"
        && updated == "0";
    var dtoOk = feeHistory.History?[0].OccurredAt == at
        && !feeJson.Contains(definition, StringComparison.Ordinal)
        && !feeJson.Contains(createdBy, StringComparison.Ordinal)
        && makerHistory.History?[0].After.Count == 0
        && !Json(makerHistory).Contains("legal_name", StringComparison.Ordinal)
        && claimHistory.History?[0].After.Count == 0
        && !Json(claimHistory).Contains("claim_value", StringComparison.Ordinal);
    if (storedOk && dtoOk) Pass("DB stored-image-stays-out-of-history-dto", "definition, actor, legal_name, claim_value");
    else Fail("DB stored-image-stays-out-of-history-dto", $"stored {definition}|{createdBy}|{legal}|{claimValue}|updated {updated}; at {at:o} dto {feeHistory.History?[0].OccurredAt:o}");
    var other = await query.HistoryAsync(actors[2].Principal, CatalogFamily.FeeType, Id(26), 9, CancellationToken.None);
    var missing = await query.HistoryAsync(actors[2].Principal, CatalogFamily.FeeType, missingId, 9, CancellationToken.None);
    if (Json(other) == Json(missing) && other.Status == 404 && other.History is null)
        Pass("DB other-partner-history-matches-missing-with-version", "404");
    else
        Fail("DB other-partner-history-matches-missing-with-version", Json(other));
}

async Task RunMatrix(string label, CatalogQuery query, ICallCounts counts)
{
    foreach (var family in CatalogFamilies.All)
    {
        var wire = CatalogFamilies.Wire(family);
        var rows = fixtures.Where(row => row.Family == wire).ToArray();
        foreach (var actor in actors)
        {
            var listsBefore = counts.Lists;
            var findsBefore = counts.Finds;
            var historyBefore = counts.Histories;
            CatalogQueryResult list;
            try
            {
                list = await query.ListAsync(actor.Principal, family, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Fail($"{label} {wire} {actor.Name} list", ex.GetType().Name + " " + ex.Message);
                continue;
            }
            var expected = rows.Where(row => Expected(actor, row)).OrderBy(row => row.Code, StringComparer.Ordinal).ThenBy(row => row.Id).Select(row => row.Id).ToArray();
            var actual = list.Entries?.Select(entry => entry.EntryId).ToArray() ?? [];
            var listOk = actor.Management
                ? list.Status == 200 && list.Code == "ok" && list.Pagination == "not_a_cursor_contract" && actual.SequenceEqual(expected) && counts.Lists == listsBefore + 1
                : list.Status == 403 && list.Code == "no_management_grant" && list.Entries is null && counts.Lists == listsBefore && counts.Finds == findsBefore && counts.Histories == historyBefore;
            var reads = new List<string>();
            var readsOk = true;
            foreach (var row in rows)
            {
                var visible = Expected(actor, row);
                var readBeforeFind = counts.Finds;
                var readBeforeHist = counts.Histories;
                var read = await query.ReadAsync(actor.Principal, family, row.Id, null, CancellationToken.None);
                var history = await query.HistoryAsync(actor.Principal, family, row.Id, null, CancellationToken.None);
                var readFetch = counts.Finds - readBeforeFind;
                var histFetch = counts.Histories - readBeforeHist;
                if (visible)
                {
                    var cls = row.Owner is null ? "global" : "private";
                    var oneOk = read.Status == 200 && read.Entry?.EntryId == row.Id && read.Entry.EntryClass == cls
                        && read.Entry.Family == wire && read.Entry.EntryVersion == 1
                        && history.Status == 200 && history.History?.Count == 1 && history.History[0].ChangeKind == "created"
                        && history.History[0].EntryId == row.Id
                        && HistoryShape(wire, history.History[0])
                        && !Json(history).Contains(fixtureActor.ToString("D"), StringComparison.Ordinal)
                        && !Json(history).Contains("names only fixture", StringComparison.Ordinal)
                        && !Json(history).Contains("created_by", StringComparison.Ordinal)
                        && !Json(history).Contains("legal_name", StringComparison.Ordinal)
                        && !Json(history).Contains("claim_value", StringComparison.Ordinal)
                        && readFetch == (actor.Management ? 2 : 0)
                        && histFetch == 1;
                    reads.Add($"{Short(row.Id)}:200/200");
                    readsOk &= oneOk;
                }
                else
                {
                    var hidden = Json(read) == Json(CatalogQueryResult.NotFound()) && Json(history) == Json(read)
                        && read.Status == 404 && history.History is null && read.Entry is null
                        && !Json(read).Contains(row.Id.ToString(), StringComparison.Ordinal)
                        && histFetch == 0
                        && readFetch == (actor.Management ? 2 : 0);
                    reads.Add($"{Short(row.Id)}:404/404");
                    readsOk &= hidden;
                }
            }
            var cell = $"{label} {wire} {actor.Name} list={(list.Status == 200 ? "200:" + string.Join(",", actual.Select(Short)) : list.Status.ToString())} {string.Join(" ", reads)}";
            Note("MATRIX " + cell);
            if (listOk && readsOk) Pass($"{label}-matrix {wire} {actor.Name}", "matches accepted Visible");
            else Fail($"{label}-matrix {wire} {actor.Name}", cell);
        }
    }
}

async Task RunFocused(string label, CatalogQuery query, ICallCounts counts)
{
    var sa = actors[0].Principal;
    var owner = actors[1].Principal;
    var other = actors[2].Principal;
    var cashier = actors[5].Principal;
    var hidden = await query.ReadAsync(sa, CatalogFamily.FeeType, Id(26), 1, CancellationToken.None);
    var missing = await query.ReadAsync(sa, CatalogFamily.FeeType, missingId, 9, CancellationToken.None);
    if (hidden.Status == 200 && hidden.Entry?.EntryId == Id(26) && missing.Status == 404 && missing.Code == "not_found")
        Pass($"{label} sa-private-nonclaim-read-visible", hidden.Entry.EntryId.ToString());
    else
        Fail($"{label} sa-private-nonclaim-read-visible", Json(hidden) + " vs " + Json(missing));

    var histBefore = counts.Histories;
    var hiddenHistory = await query.HistoryAsync(sa, CatalogFamily.FeeType, Id(26), null, CancellationToken.None);
    var missingHistory = await query.HistoryAsync(sa, CatalogFamily.FeeType, missingId, 9, CancellationToken.None);
    if (hiddenHistory.Status == 200 && hiddenHistory.History?.Count >= 1 && counts.Histories == histBefore + 1 && missingHistory.Status == 404)
        Pass($"{label} sa-private-nonclaim-history-visible", $"fetches {counts.Histories - histBefore}");
    else
        Fail($"{label} sa-private-nonclaim-history-visible", $"delta {counts.Histories - histBefore} {hiddenHistory.Status}");

    var claimBefore = counts.Histories;
    var claim = await query.HistoryAsync(sa, CatalogFamily.ModelHardwareClaim, Id(28), null, CancellationToken.None);
    if (claim.Status == 200 && claim.History?.Count == 1 && counts.Histories == claimBefore + 1
        && claim.History[0].EntryId == Id(28)
        && !Json(claim).Contains(fixtureActor.ToString("D"), StringComparison.Ordinal))
        Pass($"{label} sa-private-claim-history-visible", claim.History[0].ChangeKind);
    else
        Fail($"{label} sa-private-claim-history-visible", $"{claim.Status} delta {counts.Histories - claimBefore}");

    var otherClaim = await query.ReadAsync(other, CatalogFamily.ModelHardwareClaim, Id(28), 1, CancellationToken.None);
    if (Json(otherClaim) == Json(CatalogQueryResult.NotFound()))
        Pass($"{label} other-partner-private-claim-matches-missing", "404");
    else
        Fail($"{label} other-partner-private-claim-matches-missing", Json(otherClaim));

    var versionBefore = counts.Histories;
    var fresh = await query.ReadAsync(sa, CatalogFamily.FeeType, Id(20), 1, CancellationToken.None);
    var stale = await query.HistoryAsync(sa, CatalogFamily.FeeType, Id(20), 2, CancellationToken.None);
    if (fresh.Status == 200 && fresh.Entry?.EntryVersion == 1
        && stale.Status == 409 && stale.Code == "catalog_version_conflict" && stale.History is null && stale.Entry is null
        && counts.Histories == versionBefore)
        Pass($"{label} version-compared-only-after-visibility", "200 then 409 without history fetch");
    else
        Fail($"{label} version-compared-only-after-visibility", $"{fresh.Status} {stale.Status} delta {counts.Histories - versionBefore}");

    var mismatch = await query.ReadAsync(owner, CatalogFamily.Currency, Id(20), null, CancellationToken.None);
    if (Json(mismatch) == Json(CatalogQueryResult.NotFound()))
        Pass($"{label} family-mismatch-matches-missing", "404");
    else
        Fail($"{label} family-mismatch-matches-missing", Json(mismatch));

    var cashierBefore = counts.Lists + counts.Finds + counts.Histories;
    var cashierList = await query.ListAsync(cashier, CatalogFamily.FeeType, CancellationToken.None);
    var cashierRead = await query.ReadAsync(cashier, CatalogFamily.FeeType, Id(20), null, CancellationToken.None);
    var cashierHistory = await query.HistoryAsync(cashier, CatalogFamily.ModelHardwareClaim, Id(28), null, CancellationToken.None);
    if (cashierList.Status == 403 && Json(cashierRead) == Json(CatalogQueryResult.NotFound())
        && Json(cashierHistory) == Json(cashierRead)
        && counts.Lists + counts.Finds + counts.Histories == cashierBefore)
        Pass($"{label} non-management-does-not-touch-storage", "403 list, 404 read and history");
    else
        Fail($"{label} non-management-does-not-touch-storage", $"{cashierList.Status} {cashierRead.Status}");

    var scopeBefore = counts.ScopeLog.Count;
    await query.ListAsync(sa, CatalogFamily.FeeType, CancellationToken.None);
    await query.ListAsync(owner, CatalogFamily.FeeType, CancellationToken.None);
    var scopes = counts.ScopeLog.Skip(scopeBefore).ToArray();
    if (scopes.Length == 2 && scopes[0] == "super|" && scopes[1] == "partner|" + partnerA.ToString("D"))
        Pass($"{label} scope-follows-principal", string.Join(" ", scopes));
    else
        Fail($"{label} scope-follows-principal", string.Join(" ", scopes));

    var cancelled = new CancellationToken(true);
    var cancelThrew = false;
    var callsBefore = counts.Lists;
    try { await query.ListAsync(sa, CatalogFamily.FeeType, cancelled); }
    catch (OperationCanceledException) { cancelThrew = true; }
    if (cancelThrew && counts.Lists == callsBefore)
        Pass($"{label} cancellation-before-storage", "OperationCanceledException");
    else
        Fail($"{label} cancellation-before-storage", $"threw {cancelThrew}");

    var nullThrew = false;
    try { await query.ListAsync(null!, CatalogFamily.FeeType, CancellationToken.None); }
    catch (ArgumentNullException) { nullThrew = true; }
    if (nullThrew) Pass($"{label} null-principal-rejected", "ArgumentNullException");
    else Fail($"{label} null-principal-rejected", "no throw");

    var ownerHistory = await query.HistoryAsync(owner, CatalogFamily.FeeType, Id(26), null, CancellationToken.None);
    var ownerJson = Json(ownerHistory);
    var closed = typeof(CatalogQueryHistory).GetProperties().All(property => property.Name is not "ActorUserId" and not "ActorAdminType" and not "Reason")
        && typeof(CatalogQueryResult).GetProperties().All(property => property.Name is not "HiddenCount" and not "AffectedCount" and not "Total")
        && !ownerJson.Contains(fixtureActor.ToString("D"), StringComparison.Ordinal)
        && !ownerJson.Contains("actor_user_id", StringComparison.Ordinal)
        && !ownerJson.Contains("economic_role", StringComparison.Ordinal)
        && !ownerJson.Contains("HiddenCount", StringComparison.Ordinal);
    if (ownerHistory.Status == 200 && closed)
        Pass($"{label} closed-dto-omits-actor-and-counts", "history json");
    else
        Fail($"{label} closed-dto-omits-actor-and-counts", ownerJson);

    var onlyPrivate = new MemoryStorage([fixtures.Single(row => row.Id == Id(26))]);
    var onlyQuery = new CatalogQuery(onlyPrivate);
    var empty = await onlyQuery.ListAsync(sa, CatalogFamily.FeeType, CancellationToken.None);
    if (label == "DB")
        await StoredImageStaysOutOfDto(query);

    if (label != "MEMORY")
        return;
    if (empty.Status == 200 && empty.Entries?.Count == 1 && empty.Entries[0].EntryId == Id(26))
        Pass("MEMORY sa-list-of-only-private-nonclaim-includes-the-row", Id(26).ToString());
    else
        Fail("MEMORY sa-list-of-only-private-nonclaim-includes-the-row", Json(empty));
}

async Task SessionCleanupProof(Guid feeId)
{
    Note("SECTION session-cleanup-before-driver-reset");
    Note("The one-row GUC is resolved_global|resolved_partners|admin_type|partner_id|count of registered_entry for the global fee. on||||0 means the resolved flag is set and RLS still returns nothing: the flag is not an authorization source.");

    await using (var masked = ProbeSource(noResetOnClose: false))
    {
        int pid;
        await using (var conn = await masked.OpenConnectionAsync())
        {
            await PlantSessionSuper(conn);
            var before = await ReadSession(conn, feeId);
            pid = before.Pid;
            ExpectSample("default-reset-before-close", before, pid, "on||||0", "before-close");
        }
        await using (var conn = await masked.OpenConnectionAsync())
        {
            var after = await ReadSession(conn, feeId);
            ExpectSample("default-reset-after-return", after, pid, "||||0", "after-return-default-reset");
        }
    }

    await using (var retained = ProbeSource(noResetOnClose: true))
    {
        int pid;
        await using (var conn = await retained.OpenConnectionAsync())
        {
            await PlantSessionSuper(conn);
            var before = await ReadSession(conn, feeId);
            pid = before.Pid;
            ExpectSample("reset-disabled-before-close", before, pid, "on||||0", "before-close");
        }
        await using (var conn = await retained.OpenConnectionAsync())
        {
            var after = await ReadSession(conn, feeId);
            ExpectSample("reset-disabled-after-return", after, pid, "on||||0", "after-return-reset-disabled");
        }
        var history = await new CatalogQuery(new NpgsqlCatalogStorage(retained)).HistoryAsync(
            actors[0].Principal, CatalogFamily.FeeType, feeId, null, CancellationToken.None);
        var historyJson = Json(history);
        await using (var conn = await retained.OpenConnectionAsync())
        {
            var cleared = await ReadSession(conn, feeId);
            ExpectSample("history-clears-session-guc-with-reset-disabled", cleared, pid, "||||0", "after-history-return-reset-disabled");
        }
        var shape = history.Status == 200
            && history.History?.Count == 1
            && history.History[0].ChangedFields.SequenceEqual(["code", "display_name"])
            && history.History[0].Before is null
            && history.History[0].After.Count == 2
            && !historyJson.Contains(fixtureActor.ToString("D"), StringComparison.Ordinal)
            && !historyJson.Contains("names only fixture", StringComparison.Ordinal)
            && !historyJson.Contains("actor_user_id", StringComparison.Ordinal);
        if (shape) Pass("reset-disabled-history-keeps-fee-allowlist", historyJson);
        else Fail("reset-disabled-history-keeps-fee-allowlist", historyJson);
    }

    await ErrorRollbackReuse(feeId);
    await PoolExhaustion();
}

async Task ErrorRollbackReuse(Guid feeId)
{
    await using var source = ProbeSource(noResetOnClose: true, commandTimeout: 30);
    int plantedPid;
    await using (var conn = await source.OpenConnectionAsync())
    {
        await PlantSessionSuper(conn);
        plantedPid = (await ReadSession(conn, feeId)).Pid;
    }
    await using (var conn = await source.OpenConnectionAsync())
    {
        var held = await ReadSession(conn, feeId);
        ExpectSample("error-path-reset-disabled-still-holds-session-guc", held, plantedPid, "on||||0", "after-return-before-error");
    }

    await using var lockConn = await ownerDs!.OpenConnectionAsync();
    await using var lockTx = await lockConn.BeginTransactionAsync();
    await using (var lockCmd = new NpgsqlCommand("LOCK TABLE catalog.entry_change IN ACCESS EXCLUSIVE MODE", lockConn, lockTx))
        await lockCmd.ExecuteNonQueryAsync();
    using var cts = new CancellationTokenSource();
    var call = new NpgsqlAllowlistedHistory(source).ListAsync(CatalogReadScope.Super(), feeId, "fee_type", cts.Token);
    int? waitingPid = null;
    var waitUntil = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < waitUntil && !call.IsCompleted)
    {
        waitingPid = await WaitingHistoryPid();
        if (waitingPid is not null)
            break;
        await Task.Delay(50);
    }
    Exception? error = null;
    if (waitingPid is null)
    {
        cts.Cancel();
        try { await call; }
        catch (Exception ex) { error = ex; }
        await lockTx.RollbackAsync();
        Fail("error-rollback-saw-lock-wait", error is null ? "history finished without waiting" : error.GetType().Name);
        return;
    }
    Note($"error-rollback waiting pid={waitingPid.Value} timing=blocked-on-entry-change-before-cancel");
    cts.Cancel();
    try { await call; }
    catch (Exception ex) { error = ex; }
    await lockTx.RollbackAsync();
    if (error is null)
    {
        Fail("error-rollback-threw", "history completed after cancel");
        return;
    }
    Pass("error-rollback-threw", error.GetType().Name);
    await using var reused = await source.OpenConnectionAsync();
    var after = await ReadSession(reused, feeId);
    Note($"error-rollback reuse timing=after-rollback-reset-disabled waiting-pid={waitingPid.Value} reuse-pid={after.Pid} guc={after.Guc}");
    if (after.Pid == waitingPid.Value && after.Guc == "||||0")
        Pass("error-rollback-reuse-is-clear", $"pid={after.Pid} guc={after.Guc}");
    else
        Fail("error-rollback-reuse-is-clear", $"waiting-pid={waitingPid.Value} reuse-pid={after.Pid} guc={after.Guc}");
}

async Task<int?> WaitingHistoryPid()
{
    await using var look = NpgsqlDataSource.Create(Cs(BootstrapRole, DbName, false));
    await using var conn = await look.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT pid
          FROM pg_stat_activity
         WHERE datname = current_database()
           AND usename = $1
           AND wait_event_type = 'Lock'
           AND query ILIKE '%catalog.entry_change%'
         ORDER BY pid
         LIMIT 1
        """, conn);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = AdminRole });
    var value = await cmd.ExecuteScalarAsync();
    return value is int pid ? pid : null;
}

async Task PoolExhaustion()
{
    await using var source = ProbeSource(noResetOnClose: true, timeoutSeconds: 2);
    await using var held = await source.OpenConnectionAsync();
    await using var pidCmd = new NpgsqlCommand("SELECT pg_backend_pid()", held);
    var heldPid = (int)(await pidCmd.ExecuteScalarAsync())!;
    var started = Stopwatch.StartNew();
    Exception? blocked = null;
    try
    {
        await using var second = await source.OpenConnectionAsync();
    }
    catch (Exception ex)
    {
        blocked = ex;
    }
    started.Stop();
    Note($"pool-exhaustion held-pid={heldPid} waited-ms={started.ElapsedMilliseconds} second-open={(blocked is null ? "obtained" : blocked.GetType().Name)}");
    if (blocked is not null && started.ElapsedMilliseconds >= 1500)
        Pass("pool-exhaustion-second-open-times-out", $"{blocked.GetType().Name} {started.ElapsedMilliseconds}ms held-pid={heldPid}");
    else
        Fail("pool-exhaustion-second-open-times-out", blocked is null ? "second connection opened" : $"{blocked.GetType().Name} {started.ElapsedMilliseconds}ms");
    await held.DisposeAsync();
    await using var reused = await source.OpenConnectionAsync();
    await using var reusedCmd = new NpgsqlCommand("SELECT pg_backend_pid()", reused);
    var reusedPid = (int)(await reusedCmd.ExecuteScalarAsync())!;
    Note($"pool-exhaustion reuse timing=after-release-of-single-slot held-pid={heldPid} reuse-pid={reusedPid}");
    if (reusedPid == heldPid)
        Pass("pool-exhaustion-reuses-same-backend", $"pid={reusedPid}");
    else
        Fail("pool-exhaustion-reuses-same-backend", $"held-pid={heldPid} reuse-pid={reusedPid}");
}

NpgsqlDataSource ProbeSource(bool noResetOnClose, int timeoutSeconds = 10, int commandTimeout = 10) =>
    NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
    {
        Host = Host,
        Port = Port,
        Username = AdminRole,
        Database = DbName,
        Pooling = true,
        MaxPoolSize = 1,
        MinPoolSize = 0,
        Timeout = timeoutSeconds,
        CommandTimeout = commandTimeout,
        NoResetOnClose = noResetOnClose,
    }.ConnectionString);

async Task PlantSessionSuper(NpgsqlConnection conn)
{
    await using var tx = await conn.BeginTransactionAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT set_config('app.resolved_global_admin', 'on', false), set_config('app.resolved_partner_ids', '', false), set_config('app.admin_type', '', false), set_config('app.partner_id', '', false)", conn, tx);
    await cmd.ExecuteNonQueryAsync();
    await tx.CommitAsync();
}

async Task<(int Pid, string Guc)> ReadSession(NpgsqlConnection conn, Guid entryId)
{
    await using var cmd = new NpgsqlCommand("""
        SELECT pg_backend_pid(),
               coalesce(current_setting('app.resolved_global_admin', true), '') || '|' ||
               coalesce(current_setting('app.resolved_partner_ids', true), '') || '|' ||
               coalesce(current_setting('app.admin_type', true), '') || '|' ||
               coalesce(current_setting('app.partner_id', true), '') || '|' ||
               (SELECT count(*)::text FROM catalog.registered_entry WHERE entry_id = $1)
        """, conn);
    cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = entryId });
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
        throw new InvalidOperationException("session sample returned no row");
    return (reader.GetInt32(0), reader.GetString(1));
}

void ExpectSample(string name, (int Pid, string Guc) sample, int expectedPid, string expectedGuc, string timing)
{
    Note($"{name} timing={timing} pid={sample.Pid} guc={sample.Guc}");
    if (sample.Pid == expectedPid && sample.Guc == expectedGuc)
        Pass(name, $"timing={timing} pid={sample.Pid} guc={sample.Guc}");
    else
        Fail(name, $"timing={timing} pid={sample.Pid} guc={sample.Guc} expected pid={expectedPid} guc={expectedGuc}");
}

async Task<string> PooledGuc(NpgsqlDataSource ds)
{
    var connections = new List<NpgsqlConnection>();
    try
    {
        for (var i = 0; i < 4; i++)
            connections.Add(await ds.OpenConnectionAsync());
        var parts = new List<string>();
        foreach (var conn in connections)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT coalesce(current_setting('app.resolved_global_admin', true), '') || '|' ||
                       coalesce(current_setting('app.resolved_partner_ids', true), '') || '|' ||
                       coalesce(current_setting('app.resolved_location_ids', true), '') || '|' ||
                       coalesce(current_setting('app.admin_type', true), '') || '|' ||
                       coalesce(current_setting('app.partner_id', true), '') || '|' ||
                       (SELECT count(*)::text FROM catalog.registered_entry)
                """, conn);
            var value = await cmd.ExecuteScalarAsync();
            parts.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
        return string.Join(",", parts);
    }
    finally
    {
        foreach (var conn in connections)
            await conn.DisposeAsync();
    }
}

static bool PoolClear(string guc) => guc.Split(',').All(part => part == "|||||0");

static bool Expected(CheckActor actor, FixtureRow row)
{
    if (actor.Role is not (CatalogRole.SuperAdmin or CatalogRole.PartnerAdmin))
        return false;
    if (row.Owner is null)
        return true;
    if (row.Family == "model_hardware_claim")
        return actor.Role == CatalogRole.SuperAdmin || actor.Partner == row.Owner;
    return actor.Role == CatalogRole.SuperAdmin || (actor.Role == CatalogRole.PartnerAdmin && actor.Partner == row.Owner);
}

static string CatalogFixtureSql(Guid actor, Guid partnerA, Guid partnerB)
{
    string U(int n) => $"'10000000-0000-4000-8000-0000000000{n:x2}'::uuid";
    var by = $"'{actor:D}'::uuid";
    var a = $"'{partnerA:D}'::uuid";
    var b = $"'{partnerB:D}'::uuid";
    string Entry(int n, string family, string entryClass, string owner) =>
        "INSERT INTO catalog.registered_entry (entry_id, family, entry_class, owner_partner_id, code, is_active, row_version, created_by, created_at, updated_by, updated_at) VALUES ("
        + $"{U(n)}, '{family}', '{entryClass}', {owner}, 'g', true, 1, {by}, clock_timestamp(), {by}, clock_timestamp());\n";
    return string.Concat(
        Entry(1, "catalog_release", "provenance", "NULL"),
        $"INSERT INTO catalog.catalog_release (entry_id, family, code, release_family, version, imported_at, display_name) VALUES ({U(1)}, 'catalog_release', 'g', 'nonproduct', 'v1', clock_timestamp(), 'nonproduct');\n",
        Entry(2, "transaction_type", "descriptive", "NULL"),
        $"INSERT INTO catalog.transaction_type (entry_id, family, code, semantic_key, value_effect, display_name) VALUES ({U(2)}, 'transaction_type', 'g', 'nonproduct', 'none', 'nonproduct');\n",
        Entry(3, "channel", "descriptive", "NULL"),
        $"INSERT INTO catalog.channel (entry_id, family, code, attendance, display_name) VALUES ({U(3)}, 'channel', 'g', 'staffed', 'nonproduct');\n",
        Entry(4, "device_category", "descriptive", "NULL"),
        $"INSERT INTO catalog.device_category (entry_id, family, code, definition, display_name) VALUES ({U(4)}, 'device_category', 'g', 'nonproduct', 'nonproduct');\n",
        Entry(5, "manufacturer", "descriptive", "NULL"),
        $"INSERT INTO catalog.manufacturer (entry_id, family, code, owner_partner_id, legal_name, display_name) VALUES ({U(5)}, 'manufacturer', 'g', NULL, 'nonproduct', 'nonproduct');\n",
        Entry(6, "device_model", "descriptive", "NULL"),
        $"INSERT INTO catalog.device_model (entry_id, family, code, owner_partner_id, model_name, manufacturer_global_id, manufacturer_global_family, category_entry_id, display_name) VALUES ({U(6)}, 'device_model', 'g', NULL, 'global-model', {U(5)}, 'manufacturer', {U(4)}, 'nonproduct');\n",
        Entry(7, "hardware_capability_kind", "descriptive", "NULL"),
        $"INSERT INTO catalog.hardware_capability_kind (entry_id, family, code, measurement_kind, display_name) VALUES ({U(7)}, 'hardware_capability_kind', 'g', 'boolean', 'nonproduct');\n",
        Entry(8, "model_hardware_claim", "model_evidence", "NULL"),
        $"INSERT INTO catalog.model_hardware_claim (entry_id, family, code, derived_owner, model_global_id, model_global_family, capability_entry_id, claim_value, availability, option_key, source_version, checked_at, display_name) VALUES ({U(8)}, 'model_hardware_claim', 'g', NULL, {U(6)}, 'device_model', {U(7)}, 'nonproduct', 'unknown', 'opt', 'v1', clock_timestamp(), 'nonproduct');\n",
        Entry(9, "card_network", "descriptive", "NULL"),
        $"INSERT INTO catalog.card_network (entry_id, family, code, official_name, display_name) VALUES ({U(9)}, 'card_network', 'g', 'nonproduct', 'nonproduct');\n",
        Entry(10, "card_funding_class", "descriptive", "NULL"),
        $"INSERT INTO catalog.card_funding_class (entry_id, family, code, is_classification_unknown, display_name) VALUES ({U(10)}, 'card_funding_class', 'g', true, 'nonproduct');\n",
        Entry(11, "payment_rail", "descriptive", "NULL"),
        $"INSERT INTO catalog.payment_rail (entry_id, family, code, processor_namespace, rail_family, display_name) VALUES ({U(11)}, 'payment_rail', 'g', 'nonproduct', 'nonproduct', 'nonproduct');\n",
        Entry(12, "check_instrument_kind", "descriptive", "NULL"),
        $"INSERT INTO catalog.check_instrument_kind (entry_id, family, code, definition, display_name) VALUES ({U(12)}, 'check_instrument_kind', 'g', 'nonproduct', 'nonproduct');\n",
        Entry(13, "check_drawer_class", "descriptive", "NULL"),
        $"INSERT INTO catalog.check_drawer_class (entry_id, family, code, definition, display_name) VALUES ({U(13)}, 'check_drawer_class', 'g', 'nonproduct', 'nonproduct');\n",
        Entry(14, "check_purpose", "descriptive", "NULL"),
        $"INSERT INTO catalog.check_purpose (entry_id, family, code, definition, display_name) VALUES ({U(14)}, 'check_purpose', 'g', 'nonproduct', 'nonproduct');\n",
        Entry(15, "processor", "descriptive", "NULL"),
        $"INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, display_name) VALUES ({U(15)}, 'processor', 'g', NULL, 'nonproduct', 'nonproduct');\n",
        Entry(16, "transaction_status", "descriptive", "NULL"),
        $"INSERT INTO catalog.transaction_status (entry_id, family, code, state_dimension, is_terminal, display_name) VALUES ({U(16)}, 'transaction_status', 'g', 'nonproduct', false, 'nonproduct');\n",
        Entry(17, "reason_code", "descriptive", "NULL"),
        $"INSERT INTO catalog.reason_code (entry_id, family, code, owner_partner_id, namespace, category, safe_operator_message, requires_note, display_name) VALUES ({U(17)}, 'reason_code', 'g', NULL, 'nonproduct', 'nonproduct', 'nonproduct', false, 'nonproduct');\n",
        Entry(18, "id_document_type", "descriptive", "NULL"),
        $"INSERT INTO catalog.id_document_type (entry_id, family, code, owner_partner_id, document_family, capture_method_key, display_name) VALUES ({U(18)}, 'id_document_type', 'g', NULL, 'nonproduct', 'nonproduct', 'nonproduct');\n",
        Entry(19, "currency", "descriptive", "NULL"),
        $"INSERT INTO catalog.currency (entry_id, family, code, iso_alpha3, iso_numeric3, minor_unit, display_name) VALUES ({U(19)}, 'currency', 'g', 'XTS', '963', 0, 'nonproduct');\n",
        Entry(20, "fee_type", "descriptive", "NULL"),
        $"INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name) VALUES ({U(20)}, 'fee_type', 'g', NULL, 'names only fixture', 'nonproduct');\n",
        Entry(21, "manufacturer", "descriptive", a),
        $"INSERT INTO catalog.manufacturer (entry_id, family, code, owner_partner_id, legal_name, display_name) VALUES ({U(21)}, 'manufacturer', 'g', {a}, 'nonproduct', 'nonproduct');\n",
        Entry(22, "device_model", "descriptive", a),
        $"INSERT INTO catalog.device_model (entry_id, family, code, owner_partner_id, model_name, manufacturer_private_id, manufacturer_private_family, manufacturer_private_owner, category_entry_id, display_name) VALUES ({U(22)}, 'device_model', 'g', {a}, 'private-model', {U(21)}, 'manufacturer', {a}, {U(4)}, 'nonproduct');\n",
        Entry(23, "processor", "descriptive", a),
        $"INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, display_name) VALUES ({U(23)}, 'processor', 'g', {a}, 'nonproduct', 'nonproduct');\n",
        Entry(24, "reason_code", "descriptive", a),
        $"INSERT INTO catalog.reason_code (entry_id, family, code, owner_partner_id, namespace, category, safe_operator_message, requires_note, display_name) VALUES ({U(24)}, 'reason_code', 'g', {a}, 'nonproduct', 'nonproduct', 'nonproduct', false, 'nonproduct');\n",
        Entry(25, "id_document_type", "descriptive", a),
        $"INSERT INTO catalog.id_document_type (entry_id, family, code, owner_partner_id, document_family, capture_method_key, display_name) VALUES ({U(25)}, 'id_document_type', 'g', {a}, 'nonproduct', 'nonproduct', 'nonproduct');\n",
        Entry(26, "fee_type", "descriptive", a),
        $"INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name) VALUES ({U(26)}, 'fee_type', 'g', {a}, 'names only fixture', 'nonproduct');\n",
        Entry(27, "fee_type", "descriptive", b),
        $"INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name) VALUES ({U(27)}, 'fee_type', 'g', {b}, 'names only fixture', 'nonproduct');\n",
        Entry(28, "model_hardware_claim", "model_evidence", a),
        $"INSERT INTO catalog.model_hardware_claim (entry_id, family, code, derived_owner, model_private_id, model_private_owner, model_private_family, capability_entry_id, claim_value, availability, option_key, source_version, checked_at, display_name) VALUES ({U(28)}, 'model_hardware_claim', 'g', {a}, {U(22)}, {a}, 'device_model', {U(7)}, 'nonproduct', 'unknown', 'opt', 'v1', clock_timestamp(), 'nonproduct');\n");
}

static List<FixtureRow> BuildFixtures()
{
    var rows = new List<FixtureRow>();
    var n = 1;
    foreach (var family in CatalogFamilies.All)
        rows.Add(new FixtureRow(Id(n++), CatalogFamilies.Wire(family), null, "g"));
    rows.Add(new FixtureRow(Id(21), "manufacturer", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(22), "device_model", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(23), "processor", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(24), "reason_code", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(25), "id_document_type", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(26), "fee_type", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    rows.Add(new FixtureRow(Id(27), "fee_type", Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), "g"));
    rows.Add(new FixtureRow(Id(28), "model_hardware_claim", Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), "g"));
    return rows;
}

static Guid Id(int n) => Guid.Parse($"10000000-0000-4000-8000-0000000000{n:x2}");

static string Short(Guid id) => id.ToString("N")[^2..];

static string Json(CatalogQueryResult result) => JsonSerializer.Serialize(result);

static CheckActor Actor(string name, CatalogRole role, Guid? partner, int user) =>
    new(name, role, partner, Guid.Parse($"20000000-0000-4000-8000-0000000000{user:x2}"));

static string Scan(string dir)
{
    var banned = new[] { "INSERT ", "UPDATE ", "DELETE ", "TRUNCATE", "apply_entry_create", "apply_claim_edit", "MapGet", "HttpContext", "UseAuthorization" };
    foreach (var file in Directory.GetFiles(dir, "*.cs"))
    {
        var text = File.ReadAllText(file);
        foreach (var token in banned)
            if (text.Contains(token, StringComparison.Ordinal))
                return Path.GetFileName(file) + " contains " + token.Trim();
    }
    return "clean";
}

static string GitHash(string path)
{
    var start = new ProcessStartInfo("git", $"hash-object -- {path}")
    {
        RedirectStandardOutput = true,
        WorkingDirectory = Path.GetDirectoryName(path)!,
    };
    var process = Process.Start(start) ?? throw new InvalidOperationException("git hash-object did not start");
    var hash = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    return hash;
}

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Catalog.Query", "Catalog.Query.csproj")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return "/tmp/ff-dec192-query";
}

sealed record FixtureRow(Guid Id, string Family, Guid? Owner, string Code);

sealed record CheckActor(string Name, CatalogRole Role, Guid? Partner, Guid UserId)
{
    public CatalogPrincipal Principal => new(UserId, Role, Partner);
    public bool Management => Role is CatalogRole.SuperAdmin or CatalogRole.PartnerAdmin;
}

interface ICallCounts
{
    int Lists { get; }
    int Finds { get; }
    int Histories { get; }
    IReadOnlyList<string> ScopeLog { get; }
}

sealed class MemoryStorage : ICatalogStorage, ICallCounts
{
    private readonly IReadOnlyList<CatalogEntryRow> _rows;
    private readonly IReadOnlyList<AllowlistedHistoryScalars> _changes;
    private static readonly DateTimeOffset MemoryAt = DateTimeOffset.Parse("2026-11-08T19:00:00Z");

    public MemoryStorage(IReadOnlyList<FixtureRow> fixtures)
    {
        _rows = fixtures.Select(row => new CatalogEntryRow(row.Id, row.Family, "descriptive", row.Owner, row.Code, true, 1)).ToArray();
        _changes = fixtures.Select(row => new AllowlistedHistoryScalars(
            Guid.Parse("30000000-0000-4000-8000-" + row.Id.ToString("N")[^12..]),
            row.Id,
            "created",
            1,
            MemoryAt,
            row.Family == "fee_type" ? "g" : null,
            row.Family == "fee_type" ? "nonproduct" : null,
            null, null, null, null, null, null, null, null, null, null, null, null, null)).ToArray();
    }

    public int Lists { get; private set; }
    public int Finds { get; private set; }
    public int Histories { get; private set; }
    public List<string> ScopeLog { get; } = [];
    IReadOnlyList<string> ICallCounts.ScopeLog => ScopeLog;

    public Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Lists++;
        ScopeLog.Add(Tag(scope));
        return Task.FromResult(_rows);
    }

    public Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Finds++;
        ScopeLog.Add(Tag(scope));
        return Task.FromResult(_rows.SingleOrDefault(row => row.EntryId == entryId));
    }

    public Task<IReadOnlyList<AllowlistedHistoryScalars>> ListChangesAsync(CatalogReadScope scope, Guid entryId, string family, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Histories++;
        ScopeLog.Add(Tag(scope));
        _ = family;
        return Task.FromResult<IReadOnlyList<AllowlistedHistoryScalars>>(_changes.Where(row => row.EntryId == entryId).ToArray());
    }

    private static string Tag(CatalogReadScope scope) => scope.AdminType + "|" + (scope.PartnerId?.ToString("D") ?? "");
}

sealed class CountingStorage : ICatalogStorage, ICallCounts
{
    private readonly ICatalogStorage _inner;
    public int Lists { get; private set; }
    public int Finds { get; private set; }
    public int Histories { get; private set; }
    public List<string> ScopeLog { get; } = [];
    IReadOnlyList<string> ICallCounts.ScopeLog => ScopeLog;

    public CountingStorage(ICatalogStorage inner) => _inner = inner;

    public async Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken)
    {
        Lists++;
        ScopeLog.Add(scope.AdminType + "|" + (scope.PartnerId?.ToString("D") ?? ""));
        return await _inner.ListEntriesAsync(scope, cancellationToken);
    }

    public async Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
    {
        Finds++;
        ScopeLog.Add(scope.AdminType + "|" + (scope.PartnerId?.ToString("D") ?? ""));
        return await _inner.FindEntryAsync(scope, entryId, cancellationToken);
    }

    public async Task<IReadOnlyList<AllowlistedHistoryScalars>> ListChangesAsync(CatalogReadScope scope, Guid entryId, string family, CancellationToken cancellationToken)
    {
        Histories++;
        ScopeLog.Add(scope.AdminType + "|" + (scope.PartnerId?.ToString("D") ?? ""));
        return await _inner.ListChangesAsync(scope, entryId, family, cancellationToken);
    }
}
