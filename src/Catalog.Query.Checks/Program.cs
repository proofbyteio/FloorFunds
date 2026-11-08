using System.Diagnostics;
using System.Text.Json;
using Catalog.Contracts;
using Catalog.Query;
using Catalog.Read;
using Npgsql;

const string DbName = "ff_kit_dec192_query1";
const string OwnerRole = "dec192qmig";
const string AdminRole = "ff_kit_dec192_query1_admin_api";
const string LoginRole = "ff_kit_dec192_query1_login";
const string BootstrapRole = "dec161boot";
const string Host = "127.0.0.1";
const int Port = 5433;
const string FixtureLabel = "NONPRODUCT synthetic fixture. Owner direct INSERT. Not a product seed, not a candidate function, not a grant change.";

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
if (domainBlob == "2cdc73c82e89996fdc538156d4aeca5f9d9979b8")
    Pass("imported-visible-source", domainBlob);
else
    Fail("imported-visible-source", domainBlob);
if (principalBlob == "5bb556ebf38c4d93e2535fdbafddfb6f5277f790")
    Pass("imported-principal-source", principalBlob);
else
    Fail("imported-principal-source", principalBlob);
if (readerBlob == "e51071fe66c8c01a044b4066632b5ba67f149f2a")
    Pass("raw-reader-unchanged", readerBlob);
else
    Fail("raw-reader-unchanged", readerBlob);
if (grantsBlob == "56f771ed6cc6813bb2fb17e03947c6f9078bd377")
    Pass("grants-unchanged", grantsBlob);
else
    Fail("grants-unchanged", grantsBlob);

var grantsText = File.ReadAllText(Path.Combine(repo, "src", "Auth.Data", "Migrations", "CatalogMasterData.Grants.sql"));
if (grantsText.Contains("WHEN current_setting('app.admin_type', true) = 'super' THEN true", StringComparison.Ordinal))
    Pass("super-storage-branch-still-present", "Grants.sql super THEN true");
else
    Fail("super-storage-branch-still-present", "missing");
var readerText = File.ReadAllText(Path.Combine(repo, "src", "Catalog.Read", "NpgsqlCatalogReader.cs"));
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
NpgsqlDataSource? ownerDs = null;
try
{
    await Exec(bootstrap, $"CREATE ROLE {OwnerRole} LOGIN NOSUPERUSER NOBYPASSRLS CREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE DATABASE {DbName} OWNER {OwnerRole}");
    await Exec(bootstrap, $"COMMENT ON DATABASE {DbName} IS 'floorfunds-scratch-dec192-query-nonproduct'");
    await Exec(bootstrap, $"CREATE ROLE {AdminRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
    await Exec(bootstrap, $"CREATE ROLE {LoginRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
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

    ownerDs = NpgsqlDataSource.Create(Cs(OwnerRole, DbName, false));
    await LoadFixture(ownerDs);
    var loaded = await Scalar(ownerDs, "SELECT count(*) FROM catalog.registered_entry");
    if (loaded != fixtures.Count.ToString())
        throw new InvalidOperationException($"BLOCKER fixture row count {loaded}, expected {fixtures.Count}");
    Note("SECTION fixture-load");
    Note(FixtureLabel);
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
    var storage = new CountingStorage(new NpgsqlCatalogStorage(reader));
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
    if (saFees.Status == 200 && saFeeIds == Id(20).ToString() && !saFeeIds.Contains(Id(26).ToString(), StringComparison.Ordinal))
        Pass("query-sa-fee-list-omits-private", saFeeIds);
    else
        Fail("query-sa-fee-list-omits-private", $"{saFees.Status} {saFeeIds}");

    var guc = await PooledGuc(pooled);
    if (guc == "||0") Pass("pool-after-query", guc);
    else Fail("pool-after-query", guc);

    var ownerReader = new NpgsqlCatalogReader(ownerDs);
    var threw = false;
    try
    {
        await new CatalogQuery(new NpgsqlCatalogStorage(ownerReader)).ListAsync(actors[0].Principal, CatalogFamily.FeeType, CancellationToken.None);
    }
    catch (CatalogReadRoleException)
    {
        threw = true;
    }
    if (threw) Pass("owner-connection-still-refused", "CatalogReadRoleException");
    else Fail("owner-connection-still-refused", "no role exception");
    var gucAfterThrow = await PooledGuc(pooled);
    if (gucAfterThrow == "||0") Pass("pool-unchanged-by-owner-refusal", gucAfterThrow);
    else Fail("pool-unchanged-by-owner-refusal", gucAfterThrow);
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
        foreach (var role in new[] { AdminRole, LoginRole, OwnerRole })
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
Note("Mutant campaign was not re-run. r2 accounting remains 4 of 5 caught. Mutant 4, finally-clear removed and set_config left transaction-local, was no leak.");
Note($"checks {passed} passed, {failed} failed");
return Exit();

int Exit()
{
    var path = "/opt/cursor/artifacts/dec192-query-log.txt";
    File.WriteAllLines(path, log);
    Console.WriteLine($"log {path}");
    return failed == 0 ? 0 : 1;
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
                        && !Json(history).Contains(fixtureActor.ToString("D"), StringComparison.Ordinal)
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
    var hidden = await query.ReadAsync(sa, CatalogFamily.FeeType, Id(26), 9, CancellationToken.None);
    var missing = await query.ReadAsync(sa, CatalogFamily.FeeType, missingId, 9, CancellationToken.None);
    if (Json(hidden) == Json(missing) && hidden.Status == 404 && hidden.Code == "not_found")
        Pass($"{label} hidden-read-matches-missing", "404");
    else
        Fail($"{label} hidden-read-matches-missing", Json(hidden) + " vs " + Json(missing));

    var histBefore = counts.Histories;
    var hiddenHistory = await query.HistoryAsync(sa, CatalogFamily.FeeType, Id(26), 9, CancellationToken.None);
    var missingHistory = await query.HistoryAsync(sa, CatalogFamily.FeeType, missingId, 9, CancellationToken.None);
    if (counts.Histories == histBefore && Json(hiddenHistory) == Json(missingHistory) && hiddenHistory.Status == 404)
        Pass($"{label} hidden-history-matches-missing-without-fetch", $"fetches {counts.Histories - histBefore}");
    else
        Fail($"{label} hidden-history-matches-missing-without-fetch", $"delta {counts.Histories - histBefore} {hiddenHistory.Status}");

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
    if (label != "MEMORY")
        return;
    if (empty.Status == 200 && empty.Entries?.Count == 0 && !Json(empty).Contains(Id(26).ToString(), StringComparison.Ordinal))
        Pass("MEMORY sa-list-of-only-private-nonclaim-is-empty-200", "no hidden id");
    else
        Fail("MEMORY sa-list-of-only-private-nonclaim-is-empty-200", Json(empty));
}

async Task LoadFixture(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var guc = new NpgsqlCommand("""
        SELECT set_config('app.admin_type', 'super', true),
               set_config('app.partner_id', '', true),
               set_config('app.actor_user_id', $1, true),
               set_config('app.correlation_id', 'dec192-query-nonproduct-fixture', true),
               set_config('app.reason', 'nonproduct synthetic fixture', true)
        """, conn, tx))
    {
        guc.Parameters.Add(new NpgsqlParameter<string> { TypedValue = fixtureActor.ToString("D") });
        await guc.ExecuteNonQueryAsync();
    }

    await Step(conn, tx, "catalog_release", () => Entry(conn, tx, Id(1), "catalog_release", "provenance", null));
    await Step(conn, tx, "catalog_release subtype", () => Sql(conn, tx, """
        INSERT INTO catalog.catalog_release (entry_id, family, code, release_family, version, imported_at, display_name)
        VALUES ($1, 'catalog_release', 'g', 'nonproduct', 'v1', clock_timestamp(), 'nonproduct')
        """, Id(1)));
    await Simple(conn, tx, 2, "transaction_type", """
        INSERT INTO catalog.transaction_type (entry_id, family, code, semantic_key, value_effect, display_name)
        VALUES ($1, 'transaction_type', 'g', 'nonproduct', 'none', 'nonproduct')
        """);
    await Simple(conn, tx, 3, "channel", """
        INSERT INTO catalog.channel (entry_id, family, code, attendance, display_name)
        VALUES ($1, 'channel', 'g', 'staffed', 'nonproduct')
        """);
    await Simple(conn, tx, 4, "device_category", """
        INSERT INTO catalog.device_category (entry_id, family, code, definition, display_name)
        VALUES ($1, 'device_category', 'g', 'nonproduct', 'nonproduct')
        """);
    await Owned(conn, tx, 5, "manufacturer", null, """
        INSERT INTO catalog.manufacturer (entry_id, family, code, owner_partner_id, legal_name, display_name)
        VALUES ($1, 'manufacturer', 'g', $2, 'nonproduct', 'nonproduct')
        """);
    await Step(conn, tx, "device_model global", () => Entry(conn, tx, Id(6), "device_model", "descriptive", null));
    await Step(conn, tx, "device_model global subtype", () => Sql(conn, tx, """
        INSERT INTO catalog.device_model (
          entry_id, family, code, owner_partner_id, model_name,
          manufacturer_global_id, manufacturer_global_family, category_entry_id, display_name)
        VALUES ($1, 'device_model', 'g', NULL, 'global-model', $2, 'manufacturer', $3, 'nonproduct')
        """, Id(6), Id(5), Id(4)));
    await Simple(conn, tx, 7, "hardware_capability_kind", """
        INSERT INTO catalog.hardware_capability_kind (entry_id, family, code, measurement_kind, display_name)
        VALUES ($1, 'hardware_capability_kind', 'g', 'boolean', 'nonproduct')
        """);
    await Step(conn, tx, "claim global", () => Entry(conn, tx, Id(8), "model_hardware_claim", "model_evidence", null));
    await Step(conn, tx, "claim global subtype", () => Sql(conn, tx, """
        INSERT INTO catalog.model_hardware_claim (
          entry_id, family, code, derived_owner, model_global_id, model_global_family,
          capability_entry_id, claim_value, availability, option_key, source_version, checked_at, display_name)
        VALUES ($1, 'model_hardware_claim', 'g', NULL, $2, 'device_model', $3, 'nonproduct', 'unknown', 'opt', 'v1', clock_timestamp(), 'nonproduct')
        """, Id(8), Id(6), Id(7)));
    await Simple(conn, tx, 9, "card_network", """
        INSERT INTO catalog.card_network (entry_id, family, code, official_name, display_name)
        VALUES ($1, 'card_network', 'g', 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 10, "card_funding_class", """
        INSERT INTO catalog.card_funding_class (entry_id, family, code, is_classification_unknown, display_name)
        VALUES ($1, 'card_funding_class', 'g', true, 'nonproduct')
        """);
    await Simple(conn, tx, 11, "payment_rail", """
        INSERT INTO catalog.payment_rail (entry_id, family, code, processor_namespace, rail_family, display_name)
        VALUES ($1, 'payment_rail', 'g', 'nonproduct', 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 12, "check_instrument_kind", """
        INSERT INTO catalog.check_instrument_kind (entry_id, family, code, definition, display_name)
        VALUES ($1, 'check_instrument_kind', 'g', 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 13, "check_drawer_class", """
        INSERT INTO catalog.check_drawer_class (entry_id, family, code, definition, display_name)
        VALUES ($1, 'check_drawer_class', 'g', 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 14, "check_purpose", """
        INSERT INTO catalog.check_purpose (entry_id, family, code, definition, display_name)
        VALUES ($1, 'check_purpose', 'g', 'nonproduct', 'nonproduct')
        """);
    await Owned(conn, tx, 15, "processor", null, """
        INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, display_name)
        VALUES ($1, 'processor', 'g', $2, 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 16, "transaction_status", """
        INSERT INTO catalog.transaction_status (entry_id, family, code, state_dimension, is_terminal, display_name)
        VALUES ($1, 'transaction_status', 'g', 'nonproduct', false, 'nonproduct')
        """);
    await Owned(conn, tx, 17, "reason_code", null, """
        INSERT INTO catalog.reason_code (
          entry_id, family, code, owner_partner_id, namespace, category, safe_operator_message, requires_note, display_name)
        VALUES ($1, 'reason_code', 'g', $2, 'nonproduct', 'nonproduct', 'nonproduct', false, 'nonproduct')
        """);
    await Owned(conn, tx, 18, "id_document_type", null, """
        INSERT INTO catalog.id_document_type (
          entry_id, family, code, owner_partner_id, document_family, capture_method_key, display_name)
        VALUES ($1, 'id_document_type', 'g', $2, 'nonproduct', 'nonproduct', 'nonproduct')
        """);
    await Simple(conn, tx, 19, "currency", """
        INSERT INTO catalog.currency (entry_id, family, code, iso_alpha3, iso_numeric3, minor_unit, display_name)
        VALUES ($1, 'currency', 'g', 'XTS', '963', 0, 'nonproduct')
        """);
    await Owned(conn, tx, 20, "fee_type", null, """
        INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name)
        VALUES ($1, 'fee_type', 'g', $2, 'names only fixture', 'nonproduct')
        """);
    await Owned(conn, tx, 21, "manufacturer", partnerA, """
        INSERT INTO catalog.manufacturer (entry_id, family, code, owner_partner_id, legal_name, display_name)
        VALUES ($1, 'manufacturer', 'g', $2, 'nonproduct', 'nonproduct')
        """);
    await Step(conn, tx, "device_model private", () => Entry(conn, tx, Id(22), "device_model", "descriptive", partnerA));
    await Step(conn, tx, "device_model private subtype", () => Sql(conn, tx, """
        INSERT INTO catalog.device_model (
          entry_id, family, code, owner_partner_id, model_name,
          manufacturer_private_id, manufacturer_private_family, manufacturer_private_owner,
          category_entry_id, display_name)
        VALUES ($1, 'device_model', 'g', $2, 'private-model', $3, 'manufacturer', $2, $4, 'nonproduct')
        """, Id(22), partnerA, Id(21), Id(4)));
    await Owned(conn, tx, 23, "processor", partnerA, """
        INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, display_name)
        VALUES ($1, 'processor', 'g', $2, 'nonproduct', 'nonproduct')
        """);
    await Owned(conn, tx, 24, "reason_code", partnerA, """
        INSERT INTO catalog.reason_code (
          entry_id, family, code, owner_partner_id, namespace, category, safe_operator_message, requires_note, display_name)
        VALUES ($1, 'reason_code', 'g', $2, 'nonproduct', 'nonproduct', 'nonproduct', false, 'nonproduct')
        """);
    await Owned(conn, tx, 25, "id_document_type", partnerA, """
        INSERT INTO catalog.id_document_type (
          entry_id, family, code, owner_partner_id, document_family, capture_method_key, display_name)
        VALUES ($1, 'id_document_type', 'g', $2, 'nonproduct', 'nonproduct', 'nonproduct')
        """);
    await Owned(conn, tx, 26, "fee_type", partnerA, """
        INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name)
        VALUES ($1, 'fee_type', 'g', $2, 'names only fixture', 'nonproduct')
        """);
    await Owned(conn, tx, 27, "fee_type", partnerB, """
        INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name)
        VALUES ($1, 'fee_type', 'g', $2, 'names only fixture', 'nonproduct')
        """);
    await Step(conn, tx, "claim private", () => Entry(conn, tx, Id(28), "model_hardware_claim", "model_evidence", partnerA));
    await Step(conn, tx, "claim private subtype", () => Sql(conn, tx, """
        INSERT INTO catalog.model_hardware_claim (
          entry_id, family, code, derived_owner,
          model_private_id, model_private_owner, model_private_family,
          capability_entry_id, claim_value, availability, option_key, source_version, checked_at, display_name)
        VALUES ($1, 'model_hardware_claim', 'g', $2, $3, $2, 'device_model', $4, 'nonproduct', 'unknown', 'opt', 'v1', clock_timestamp(), 'nonproduct')
        """, Id(28), partnerA, Id(22), Id(7)));
    await tx.CommitAsync();
}

async Task Simple(NpgsqlConnection conn, NpgsqlTransaction tx, int n, string family, string subtype)
{
    await Step(conn, tx, family, () => Entry(conn, tx, Id(n), family, "descriptive", null));
    await Step(conn, tx, family + " subtype", () => Sql(conn, tx, subtype, Id(n)));
}

async Task Owned(NpgsqlConnection conn, NpgsqlTransaction tx, int n, string family, Guid? owner, string subtype)
{
    await Step(conn, tx, family + " " + n, () => Entry(conn, tx, Id(n), family, "descriptive", owner));
    await Step(conn, tx, family + " subtype " + n, () => Sql(conn, tx, subtype, Id(n), owner));
}

async Task Step(NpgsqlConnection conn, NpgsqlTransaction tx, string name, Func<Task> action)
{
    _ = conn;
    _ = tx;
    try
    {
        await action();
    }
    catch (PostgresException ex)
    {
        throw new InvalidOperationException($"BLOCKER fixture {name} {ex.SqlState} {ex.MessageText} {ex.ConstraintName}", ex);
    }
}

async Task Entry(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string family, string entryClass, Guid? owner)
{
    await using var cmd = new NpgsqlCommand("""
        INSERT INTO catalog.registered_entry (
          entry_id, family, entry_class, owner_partner_id, code, is_active, row_version,
          created_by, created_at, updated_by, updated_at)
        VALUES ($1, $2, $3, $4, 'g', true, 1, $5, clock_timestamp(), $5, clock_timestamp())
        """, conn, tx);
    cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = family });
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = entryClass });
    cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = owner });
    cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = fixtureActor });
    await cmd.ExecuteNonQueryAsync();
}

async Task Sql(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params object?[] args)
{
    await using var cmd = new NpgsqlCommand(sql, conn, tx);
    foreach (var arg in args)
    {
        if (arg is Guid guid)
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = guid });
        else if (arg is null)
            cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = null });
        else
            throw new InvalidOperationException("BLOCKER fixture argument is not a uuid.");
    }
    await cmd.ExecuteNonQueryAsync();
}

async Task<string> PooledGuc(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT coalesce(current_setting('app.admin_type', true), '') || '|' ||
               coalesce(current_setting('app.partner_id', true), '') || '|' ||
               (SELECT count(*)::text FROM catalog.registered_entry)
        """, conn);
    var value = await cmd.ExecuteScalarAsync();
    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

static bool Expected(CheckActor actor, FixtureRow row)
{
    if (actor.Role is not (CatalogRole.SuperAdmin or CatalogRole.PartnerAdmin))
        return false;
    if (row.Owner is null)
        return true;
    if (row.Family == "model_hardware_claim")
        return actor.Role == CatalogRole.SuperAdmin || actor.Partner == row.Owner;
    return actor.Role == CatalogRole.PartnerAdmin && actor.Partner == row.Owner;
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
    private readonly IReadOnlyList<CatalogEntryChangeRow> _changes;

    public MemoryStorage(IReadOnlyList<FixtureRow> fixtures)
    {
        _rows = fixtures.Select(row => new CatalogEntryRow(row.Id, row.Family, row.Owner is null ? "descriptive" : "descriptive", row.Owner, row.Code, true, 1)).ToArray();
        _changes = fixtures.Select(row => new CatalogEntryChangeRow(Guid.Parse("30000000-0000-4000-8000-" + row.Id.ToString("N")[^12..]), row.Id, "created", 1, "entry")).ToArray();
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

    public Task<IReadOnlyList<CatalogEntryChangeRow>> ListChangesAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Histories++;
        ScopeLog.Add(Tag(scope));
        return Task.FromResult<IReadOnlyList<CatalogEntryChangeRow>>(_changes.Where(row => row.EntryId == entryId).ToArray());
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

    public async Task<IReadOnlyList<CatalogEntryChangeRow>> ListChangesAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
    {
        Histories++;
        ScopeLog.Add(scope.AdminType + "|" + (scope.PartnerId?.ToString("D") ?? ""));
        return await _inner.ListChangesAsync(scope, entryId, cancellationToken);
    }
}
