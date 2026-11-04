// S2a P2 DB integration checks (spec §8-§10). Runs against a migrated, seeded TEST DB named in AUTH_CHECKS_DB (TLD 8:20 P2-2).
// env: AUTH_CHECKS_DB (target DB name), CONNECTION_STRING (owner), ADMIN_API_CONNECTION_STRING, LOGIN_CONNECTION_STRING;
// arg 0: fixtures.json path.
// Test data below is SSE2-FAKE-TEST-ONLY (fake tenant ids, account 000000000000 ARNs). Never prints connection strings.
using System.Text.Json;
using Auth.Data;
using Auth.Data.Entities;
using Auth.Data.Readers;
using Login.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

int pass = 0, fail = 0;
void Ok(string name, bool cond, string? detail = null)
{
    if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}{(detail is null ? "" : " :: " + detail)}"); }
}
async Task Check(string name, Func<Task<bool>> f)
{
    try { Ok(name, await f()); } catch (Exception e) { Ok(name, false, $"{e.GetType().Name}: {e.Message}"); }
}
async Task Throws<T>(string name, Func<Task> f, Func<T, bool>? pred = null) where T : Exception
{
    try { await f(); Ok(name, false, "no exception"); }
    catch (T e) when (pred is null || pred(e)) { Ok(name, true); }
    catch (Exception e) { Ok(name, false, $"{e.GetType().Name}: {e.Message}"); }
}

string Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} not set");
var ownerCs = Env("CONNECTION_STRING");
var adminCs = Env("ADMIN_API_CONNECTION_STRING");
var loginCs = Env("LOGIN_CONNECTION_STRING");
var fixturesPath = args.Length > 0 ? args[0] : "Login.StandIn/fixtures.json";
// Checks write committed SSE2-FAKE-TEST-ONLY rows and alter constraints inside rolled-back transactions, so the target DB
// is named explicitly (AUTH_CHECKS_DB), all three connection strings must point at it, and a safety guard refuses anything
// that is not an engineers' test DB (ff_sse, ff_sse2) or a db/ kit scratch DB (ff_kit_*). SSE runs only ff_sse / ff_kit_*.
var dbName = Environment.GetEnvironmentVariable("AUTH_CHECKS_DB") ?? "";
static bool IsTestDb(string n) => n is "ff_sse" or "ff_sse2" || System.Text.RegularExpressions.Regex.IsMatch(n, "^ff_kit_[a-z0-9_]{1,45}$");
if (!IsTestDb(dbName))
{ Console.Error.WriteLine("refuse: set AUTH_CHECKS_DB to a test DB: ff_sse, ff_sse2 or ff_kit_<name> (throwaway kit DB)"); return 2; }
if (new[] { ownerCs, adminCs, loginCs }.Any(cs => new NpgsqlConnectionStringBuilder(cs).Database != dbName))
{ Console.Error.WriteLine("refuse: every connection string's Database must equal AUTH_CHECKS_DB"); return 2; }
string adminRole = dbName + "_admin_api", loginRole = dbName + "_login";
string[] LlcMutable = ["provider_kind", "display_name", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref",
                       "allow_db_fallback", "mfa_required", "updated_by", "updated_at", "row_version"];

await using var ownerDs = NpgsqlDataSource.Create(ownerCs);
await using var adminDs = NpgsqlDataSource.Create(adminCs);
await using var loginDs = NpgsqlDataSource.Create(loginCs);

// ---------- SSE2-FAKE-TEST-ONLY ids ----------
Guid P_PCI = Guid.Parse("5f0c3e1a-0001-4000-8000-000000000001"), P_NV = Guid.Parse("5f0c3e1a-0002-4000-8000-000000000002");
Guid L_PCI = Guid.Parse("5f0c3e1a-0001-4000-8000-0000000000a1"), L_NV = Guid.Parse("5f0c3e1a-0002-4000-8000-0000000000b1");
Guid T1 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa01"), T2 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa02"); // SSE2-FAKE-TEST-ONLY tenants
Guid U_C01 = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c01"), U_C03 = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c03");
Guid U_DIS = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c71"), U_LCK = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c72"); // checks-only users
Guid S1 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000001"), S2 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000002"),
     S3 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000003"), S9 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000009");
Guid ACTOR = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c0a");
string Iss(Guid t) => $"https://login.microsoftonline.com/{t}/v2.0";
const string FakeArn = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/pci-main-AbCdEf"; // SSE2-FAKE-TEST-ONLY

async Task Exec(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] ps)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
}
async Task<T> Scalar<T>(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] ps)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    return (T)(await cmd.ExecuteScalarAsync())!;
}
// Runs sql inside a rolled-back transaction on a fresh connection; returns the SQLSTATE or "ok".
async Task<string> Try(NpgsqlDataSource ds, string sql, string? adminType = null, Guid? partner = null)
{
    await using var c = await ds.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    try
    {
        if (adminType is not null) await Exec(c, tx, "SELECT set_config('app.admin_type', $1, true), set_config('app.partner_id', $2, true)", adminType, partner?.ToString() ?? "");
        await Exec(c, tx, sql);
        return "ok";
    }
    catch (PostgresException e) { return e.SqlState; }
    finally { try { await tx.RollbackAsync(); } catch { } }
}
async Task<long> CountAs(NpgsqlDataSource ds, string sql, string? adminType, Guid? partner)
{
    await using var c = await ds.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    if (adminType is not null) await Exec(c, tx, "SELECT set_config('app.admin_type', $1, true), set_config('app.partner_id', $2, true)", adminType, partner?.ToString() ?? "");
    var n = await Scalar<long>(c, tx, sql);
    await tx.CommitAsync();
    return n;
}

// ================= 1. model / snapshot / schema =================
await using (var db = new AuthDbContext(AuthDb.Options(ownerCs)))
{
    var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
    var differ = db.GetService<IMigrationsModelDiffer>();
    var designModel = db.GetService<IDesignTimeModel>().Model;
    Ok("model: hand-kept snapshot exists", snapshot is not null);
    IModel snapModel = snapshot!.Model;
    if (snapModel is IMutableModel mm) snapModel = mm.FinalizeModel();
    snapModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapModel);
    Ok("model: snapshot == AuthDbContext (IMigrationsModelDiffer.HasDifferences false)",
        !differ.HasDifferences(snapModel.GetRelationalModel(), designModel.GetRelationalModel()));
    var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
    Ok("model: applied migrations == [S2aInitial, S2aLlcColumnUpdate]", applied.SequenceEqual(["20261104160000_S2aInitial", "20261104170000_S2aLlcColumnUpdate"]), string.Join(",", applied));
    Ok("model: no pending migrations", !(await db.Database.GetPendingMigrationsAsync()).Any());

    await using var c = await ownerDs.OpenConnectionAsync();
    var bad = new List<string>();
    int cols = 0;
    foreach (var et in designModel.GetEntityTypes())
    {
        var table = et.GetTableName()!;
        var so = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, "auth");
        var dbCols = new Dictionary<string, (string type, bool nullable)>();
        await using (var cmd = new NpgsqlCommand("SELECT column_name, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull FROM information_schema.columns ic JOIN pg_attribute a ON a.attrelid = ('auth.' || ic.table_name)::regclass AND a.attname = ic.column_name WHERE table_schema = 'auth' AND table_name = $1", c))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) dbCols[r.GetString(0)] = (r.GetString(1), r.GetBoolean(2));
        }
        foreach (var p in et.GetProperties())
        {
            cols++;
            var col = p.GetColumnName(so)!;
            if (!dbCols.Remove(col, out var d)) { bad.Add($"{table}.{col} missing"); continue; }
            if (d.type != p.GetColumnType()) bad.Add($"{table}.{col} type {d.type} != {p.GetColumnType()}");
            if (d.nullable != p.IsColumnNullable(so)) bad.Add($"{table}.{col} nullability");
        }
        bad.AddRange(dbCols.Keys.Select(k => $"{table}.{k} not in EF model"));
    }
    Ok($"schema: {designModel.GetEntityTypes().Count()} EF tables / {cols} columns == pg_catalog (name, type, nullability)", bad.Count == 0, string.Join("; ", bad));
    Ok("schema: exactly 9 tables in auth", await Scalar<long>(c, null, "SELECT count(*) FROM pg_tables WHERE schemaname='auth'") == 9);
    Ok("schema: no sequences in auth (F5)", await Scalar<long>(c, null, "SELECT count(*) FROM pg_sequences WHERE schemaname='auth'") == 0);
    Ok("schema: history table in public, not auth", await Scalar<bool>(c, null, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AND to_regclass('auth.\"__EFMigrationsHistory\"') IS NULL"));
    var idx = await Scalar<long>(c, null, "SELECT count(*) FROM pg_indexes WHERE schemaname='auth' AND indexname IN ('location_partner_ix','llc_tenants_gin','llcc_location_at_ix','external_login_user_ix','user_location_location_ix','oidc_replay_expires_ix','session_user_live_ix','session_last_seen_ix','location_slug_key','session_cookie_key_hash_key')");
    Ok("schema: the 10 spec indexes exist", idx == 10, idx.ToString());
    Ok("schema: llc_tenants_gin is GIN", await Scalar<string>(c, null, "SELECT am.amname::text FROM pg_class i JOIN pg_am am ON am.oid=i.relam WHERE i.relname='llc_tenants_gin'") == "gin");
    Ok("schema: session_user_live_ix is partial (revoked_at IS NULL)", (await Scalar<string>(c, null, "SELECT indexdef FROM pg_indexes WHERE indexname='session_user_live_ix'")).Contains("WHERE (revoked_at IS NULL)"));
}

// ================= 2. roles, grants, functions, RLS flags =================
await using (var c = await ownerDs.OpenConnectionAsync())
{
    foreach (var r in new[] { adminRole, loginRole })
    {
        Ok($"role {r}: LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION",
            await Scalar<bool>(c, null, "SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication FROM pg_roles WHERE rolname=$1", r));
        Ok($"role {r}: owns nothing in auth", await Scalar<long>(c, null, "SELECT count(*) FROM pg_class cl JOIN pg_namespace n ON n.oid=cl.relnamespace WHERE n.nspname='auth' AND pg_get_userbyid(cl.relowner)=$1", r) == 0);
        Ok($"role {r}: CONNECT on db, USAGE (not CREATE) on schema auth",
            await Scalar<bool>(c, null, "SELECT has_database_privilege($1, current_database(), 'CONNECT') AND has_schema_privilege($1, 'auth', 'USAGE') AND NOT has_schema_privilege($1, 'auth', 'CREATE')", r));
    }
    Ok("schema auth: no PUBLIC privileges", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_namespace n, aclexplode(n.nspacl) x WHERE n.nspname='auth' AND x.grantee=0)"));
    Ok("database: no PUBLIC CONNECT (ff-db.sh REVOKE)", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_database d, aclexplode(d.datacl) x WHERE d.datname=current_database() AND x.grantee=0 AND x.privilege_type='CONNECT')"));

    // §8.4 matrix: every (table, role, privilege) at table level.
    var privs = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" };
    var expect = new Dictionary<(string, string), string[]>
    {
        [("location", adminRole)] = ["SELECT", "INSERT"], [("location", loginRole)] = ["SELECT"],
        [("location_login_config", adminRole)] = ["SELECT", "INSERT"], [("location_login_config", loginRole)] = ["SELECT"],
        [("location_login_config_change", adminRole)] = ["SELECT", "INSERT"], [("location_login_config_change", loginRole)] = [],
        [("app_user", adminRole)] = [], [("app_user", loginRole)] = ["SELECT"],
        [("external_login", adminRole)] = [], [("external_login", loginRole)] = ["SELECT"],
        [("user_location", adminRole)] = [], [("user_location", loginRole)] = ["SELECT"],
        [("audit_outbox", adminRole)] = ["INSERT"], [("audit_outbox", loginRole)] = ["INSERT"],
        [("oidc_replay", adminRole)] = [], [("oidc_replay", loginRole)] = ["SELECT", "INSERT", "DELETE"],
        [("session", adminRole)] = [], [("session", loginRole)] = ["SELECT", "INSERT", "DELETE"],
    };
    var mism = new List<string>();
    foreach (var ((t, r), want) in expect)
        foreach (var p in privs)
            if (await Scalar<bool>(c, null, "SELECT has_table_privilege($1, 'auth.' || $2, $3)", r, t, p) != want.Contains(p)) mism.Add($"{t}/{r}/{p}");
    Ok($"grants: table-level matrix ({expect.Count * privs.Length} cells) == spec §8.4", mism.Count == 0, string.Join(", ", mism));
    foreach (var t in expect.Keys.Select(k => k.Item1).Distinct())
        Ok($"grants: PUBLIC has nothing on auth.{t}", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_class cl, aclexplode(cl.relacl) x WHERE cl.oid = ('auth.' || $1)::regclass AND x.grantee = 0)", t));

    // Column-level UPDATE, every column of every auth table, both roles (P2-1: no UPDATE on PKs / created_* / immutable).
    var updAllowed = new Dictionary<(string, string), string[]>
    {
        [("location", adminRole)] = ["name"],
        [("location_login_config", adminRole)] = LlcMutable,
        [("session", loginRole)] = ["last_seen_at", "revoked_at", "revoked_reason", "interactive_used_at", "handoff_used_at", "cage_consumed_at", "code_issued"],
    };
    var colMism = new List<string>();
    var cols = new List<(string T, string C)>();
    await using (var cmd = new NpgsqlCommand("SELECT table_name, column_name FROM information_schema.columns WHERE table_schema='auth' ORDER BY 1, 2", c))
    await using (var rr = await cmd.ExecuteReaderAsync()) while (await rr.ReadAsync()) cols.Add((rr.GetString(0), rr.GetString(1)));
    foreach (var (t, col) in cols)
        foreach (var r in new[] { adminRole, loginRole })
            if (await Scalar<bool>(c, null, "SELECT has_column_privilege($1, 'auth.' || $2, $3, 'UPDATE')", r, t, col)
                != (updAllowed.TryGetValue((t, r), out var ok) && ok.Contains(col))) colMism.Add($"{t}.{col}/{r}");
    Ok($"grants: column UPDATE ({cols.Count} columns x 2 roles) = location(name) + location_login_config(11 mutable) for admin_api; session(7) for login; nothing else",
        cols.Count > 0 && colMism.Count == 0, string.Join(", ", colMism));
    Ok("grants: admin_api UPDATE on location_login_config location_id/created_at/created_by = none (P2-1)",
        !await Scalar<bool>(c, null, "SELECT has_column_privilege($1, 'auth.location_login_config', 'location_id', 'UPDATE') OR has_column_privilege($1, 'auth.location_login_config', 'created_at', 'UPDATE') OR has_column_privilege($1, 'auth.location_login_config', 'created_by', 'UPDATE')", adminRole));

    foreach (var f in new[] { "auth.location_partner(uuid)", "auth.tenant_bound_elsewhere(uuid,uuid)" })
    {
        Ok($"function {f}: SECURITY DEFINER, search_path pinned", await Scalar<bool>(c, null, "SELECT prosecdef AND proconfig = ARRAY['search_path=pg_catalog, auth'] FROM pg_proc WHERE oid = $1::regprocedure", f));
        Ok($"function {f}: EXECUTE admin_api yes, login no, PUBLIC no",
            await Scalar<bool>(c, null, "SELECT has_function_privilege($2, $1::regprocedure, 'EXECUTE') AND NOT has_function_privilege($3, $1::regprocedure, 'EXECUTE') AND NOT EXISTS (SELECT 1 FROM pg_proc p, aclexplode(p.proacl) x WHERE p.oid = $1::regprocedure AND x.grantee = 0)", f, adminRole, loginRole));
    }
    Ok("default privileges: owner's functions in this DB get no PUBLIC EXECUTE (global pg_default_acl entry, no PUBLIC grantee)",
        await Scalar<bool>(c, null, "SELECT EXISTS (SELECT 1 FROM pg_default_acl d WHERE d.defaclnamespace = 0 AND d.defaclobjtype = 'f' AND d.defaclrole = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND NOT EXISTS (SELECT 1 FROM aclexplode(d.defaclacl) x WHERE x.grantee = 0))"));
    await using (var tx = await c.BeginTransactionAsync())
    {
        await Exec(c, tx, "CREATE FUNCTION auth.zz_probe() RETURNS int LANGUAGE sql AS 'SELECT 1'");
        Ok("default privileges: a new auth function is not executable by login or PUBLIC (probe, rolled back)",
            !await Scalar<bool>(c, tx, "SELECT has_function_privilege($1, 'auth.zz_probe()', 'EXECUTE')", loginRole)
            && await Scalar<bool>(c, tx, "SELECT NOT EXISTS (SELECT 1 FROM pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) x WHERE p.oid = 'auth.zz_probe()'::regprocedure AND x.grantee = 0)"));
        await tx.RollbackAsync();
    }
    var rls = new List<string>();
    await using (var cmd = new NpgsqlCommand("SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class cl JOIN pg_namespace n ON n.oid=cl.relnamespace WHERE n.nspname='auth' AND relkind='r'", c))
    await using (var rr = await cmd.ExecuteReaderAsync())
        while (await rr.ReadAsync())
        {
            bool want = rr.GetString(0) is "location" or "location_login_config" or "location_login_config_change";
            if (rr.GetBoolean(1) != want || rr.GetBoolean(2) != want) rls.Add(rr.GetString(0));
        }
    Ok("RLS: ENABLE+FORCE on exactly location, location_login_config, location_login_config_change", rls.Count == 0, string.Join(",", rls));
    // K2 (SSE2 d787f76 review): the owner role is read from pg_database, not assumed equal to the DB name (kit --role != --db).
    var dbOwner = await Scalar<string>(c, null, "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()");
    Ok($"RLS: policy set == spec (owner {dbOwner} from pg_database/admin/login per table; no login policy on change)",
        await Scalar<string>(c, null, "SELECT string_agg(tablename || ':' || policyname || ':' || cmd || ':' || array_to_string(roles, '+'), ' ' ORDER BY tablename, policyname) FROM pg_policies WHERE schemaname='auth'")
        == $"location:location_admin:ALL:{adminRole} location:location_login:SELECT:{loginRole} location:location_owner:ALL:{dbOwner} " +
           $"location_login_config:llc_admin:ALL:{adminRole} location_login_config:llc_login:SELECT:{loginRole} location_login_config:llc_owner:ALL:{dbOwner} " +
           $"location_login_config_change:llcc_admin:ALL:{adminRole} location_login_config_change:llcc_owner:ALL:{dbOwner}");
}

// ================= 3. seed == SI1 fixtures =================
await using (var c = await ownerDs.OpenConnectionAsync())
{
    using var fx = JsonDocument.Parse(await File.ReadAllTextAsync(fixturesPath));
    var slugPartner = new Dictionary<string, Guid>();
    foreach (var p in fx.RootElement.GetProperty("partners").EnumerateArray())
        foreach (var l in p.GetProperty("locations").EnumerateArray())
            slugPartner[l.GetProperty("slug").GetString()!] = p.GetProperty("partner_id").GetGuid();
    var want = new SortedSet<string>();
    foreach (var u in fx.RootElement.GetProperty("users").EnumerateArray())
    {
        Guid? partner = u.TryGetProperty("admin_type", out var at)
            ? (at.GetString() == "super" ? null : u.GetProperty("partner_id").GetGuid())
            : slugPartner[u.GetProperty("location_slugs")[0].GetString()!];
        want.Add($"{u.GetProperty("user_id").GetGuid()}|{partner}|{u.GetProperty("status").GetString()}");
    }
    var got = new SortedSet<string>();
    await using (var cmd = new NpgsqlCommand("SELECT user_id, partner_id, status FROM auth.app_user WHERE user_id::text NOT LIKE '%-000000000c7_'", c))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync()) got.Add($"{r.GetGuid(0)}|{(r.IsDBNull(1) ? null : r.GetGuid(1))}|{r.GetString(2)}");
    Ok($"seed: auth.app_user == fixtures.json users ({want.Count}: id, derived partner_id, status)", want.SetEquals(got), $"want {want.Count} got {got.Count}");
}

// ================= 4. checks-only data (SSE2-FAKE-TEST-ONLY) =================
// K3 (SSE2 d787f76 review): the checks commit their own rows and count exact row sets, so they need the post-seed state:
// refuse cleanly (rc 3, before writing anything) when any business/check row is present, and delete their own rows at the
// end (finally), so a re-run on the same DB passes. A crash mid-run leaves rows -> the next run refuses (reset: update 0, update, seed).
const string StateSql = """
    SELECT (SELECT count(*) FROM auth.location) + (SELECT count(*) FROM auth.location_login_config)
         + (SELECT count(*) FROM auth.location_login_config_change) + (SELECT count(*) FROM auth.external_login)
         + (SELECT count(*) FROM auth.user_location) + (SELECT count(*) FROM auth.session) + (SELECT count(*) FROM auth.oidc_replay)
         + (SELECT count(*) FROM auth.audit_outbox) + (SELECT count(*) FROM auth.app_user WHERE user_id::text LIKE '%-000000000c7_')
    """;
await using (var c = await ownerDs.OpenConnectionAsync())
{
    var leftover = await Scalar<long>(c, null, StateSql);
    if (leftover != 0)
    {
        Console.Error.WriteLine($"refuse: {dbName} is not in the post-seed state ({leftover} rows in auth location/config/change/link/session/replay/outbox tables or checks-only users), e.g. from an interrupted run. Reset it (runner update 0, update, seed) or use a fresh kit DB.");
        return 3;
    }
}
async Task CleanupCheckData()
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    var locs = new[] { L_PCI, L_NV, Guid.Parse("5f0c3e1a-00ee-4000-8000-0000000000e1") }; // = LX
    await Exec(c, tx, "DELETE FROM auth.user_location WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.external_login WHERE subject = ANY($1)", new[] { S1, S2, S3, S9 });
    await Exec(c, tx, "DELETE FROM auth.app_user WHERE user_id = ANY($1)", new[] { U_DIS, U_LCK });
    await Exec(c, tx, "DELETE FROM auth.location_login_config_change WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.location_login_config WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.location WHERE location_id = ANY($1)", locs);
    await tx.CommitAsync();
}
try
{
await using (var c = await ownerDs.OpenConnectionAsync())
await using (var tx = await c.BeginTransactionAsync())
{
    await Exec(c, tx, "INSERT INTO auth.location VALUES ($1,$2,'pci-main','PCI main (SSE2-FAKE-TEST-ONLY)'),($3,$4,'nevada-main','Nevada main (SSE2-FAKE-TEST-ONLY)')", L_PCI, P_PCI, L_NV, P_NV);
    await Exec(c, tx, """
        INSERT INTO auth.location_login_config (location_id, provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at)
        VALUES ($1,'entra','PCI Entra (SSE2-FAKE-TEST-ONLY)', ARRAY[$2]::uuid[], $3, '5f0c3e1a-00fc-4000-8000-000000000001', $4, true, true, $5, now(), $5, now()),
               ($6,'database','Nevada password', '{}', NULL, NULL, NULL, false, false, $5, now(), $5, now())
        """, L_PCI, T1, Iss(T1), FakeArn, ACTOR, L_NV);
    await Exec(c, tx, """
        INSERT INTO auth.location_login_config_change VALUES
          ('5f0c3e1a-00fd-4000-8000-000000000001',$1,$3,'super','initial config (SSE2-FAKE-TEST-ONLY)',now(),'created','{provider_kind}',NULL,'{"provider_kind":"entra"}',1),
          ('5f0c3e1a-00fd-4000-8000-000000000002',$2,$3,'super',NULL,now(),'created','{provider_kind}',NULL,'{"provider_kind":"database"}',1)
        """, L_PCI, L_NV, ACTOR);
    await Exec(c, tx, "INSERT INTO auth.app_user VALUES ($1,$3,'disabled',NULL),($2,$3,'active',now() + interval '1 hour')", U_DIS, U_LCK, P_PCI);
    await Exec(c, tx, "INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra',$1,$2,$3,$4),('entra',$1,$2,$5,$6),('entra',$1,$2,$7,$8)",
        Iss(T1), T1, S1, U_C01, S2, U_DIS, S3, U_LCK);
    await Exec(c, tx, "INSERT INTO auth.user_location (user_id, location_id) VALUES ($1,$2)", U_C01, L_PCI);
    await tx.CommitAsync();
}

// ================= 5. RLS behaviour =================
const string cntLoc = "SELECT count(*) FROM auth.location", cntCfg = "SELECT count(*) FROM auth.location_login_config", cntChg = "SELECT count(*) FROM auth.location_login_config_change";
await Check("RLS admin_api: no app.admin_type -> 0 locations, 0 configs, 0 changes (fail closed)", async () =>
    await CountAs(adminDs, cntLoc, null, null) == 0 && await CountAs(adminDs, cntCfg, null, null) == 0 && await CountAs(adminDs, cntChg, null, null) == 0);
await Check("RLS admin_api: admin_type=bogus -> 0 rows", async () => await CountAs(adminDs, cntLoc, "bogus", P_PCI) == 0);
await Check("RLS admin_api: partner without app.partner_id -> 0 rows", async () => await CountAs(adminDs, cntLoc, "partner", null) == 0);
await Check("RLS admin_api: super -> all 2 locations / 2 configs / 2 changes", async () =>
    await CountAs(adminDs, cntLoc, "super", null) == 2 && await CountAs(adminDs, cntCfg, "super", null) == 2 && await CountAs(adminDs, cntChg, "super", null) == 2);
await Check("RLS admin_api: partner pci -> only pci rows (1/1/1)", async () =>
    await CountAs(adminDs, cntLoc + $" WHERE partner_id = '{P_PCI}'", "partner", P_PCI) == 1 && await CountAs(adminDs, cntLoc, "partner", P_PCI) == 1
    && await CountAs(adminDs, cntCfg, "partner", P_PCI) == 1 && await CountAs(adminDs, cntChg, "partner", P_PCI) == 1
    && await CountAs(adminDs, $"SELECT count(*) FROM auth.location_login_config WHERE location_id = '{L_NV}'", "partner", P_PCI) == 0);
await Check("RLS admin_api: settings are transaction-local (next tx on same connection sees 0)", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using (var tx = await c.BeginTransactionAsync())
    {
        await Exec(c, tx, "SELECT set_config('app.admin_type','super',true)");
        if (await Scalar<long>(c, tx, cntLoc) != 2) return false;
        await tx.CommitAsync();
    }
    return await Scalar<long>(c, null, cntLoc) == 0;
});
await Check("RLS admin_api: partner pci cannot INSERT a nevada location (WITH CHECK, 42501)", async () =>
    await Try(adminDs, $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_NV}', 'zz-x', 'x')", "partner", P_PCI) == "42501");
await Check("RLS admin_api: partner pci UPDATE of nevada config touches 0 rows; own config 1 row", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, "SELECT set_config('app.admin_type','partner',true), set_config('app.partner_id',$1,true)", P_PCI.ToString());
    await using var u1 = new NpgsqlCommand($"UPDATE auth.location_login_config SET mfa_required = mfa_required WHERE location_id = '{L_NV}'", c, tx);
    await using var u2 = new NpgsqlCommand($"UPDATE auth.location_login_config SET mfa_required = mfa_required WHERE location_id = '{L_PCI}'", c, tx);
    var a = await u1.ExecuteNonQueryAsync(); var b = await u2.ExecuteNonQueryAsync();
    await tx.RollbackAsync();
    return a == 0 && b == 1;
});
await Check("RLS admin_api: partner pci cannot INSERT a change row for nevada (42501)", async () =>
    await Try(adminDs, $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_NV}', '{ACTOR}', 'partner', NULL, now(), 'updated', '{{mfa_required}}', '{{}}', '{{}}', 2)", "partner", P_PCI) == "42501");
await Check("RLS admin_api: partner pci CAN insert own change row", async () =>
    await Try(adminDs, $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'partner', NULL, now(), 'updated', '{{mfa_required}}', '{{}}', '{{}}', 2)", "partner", P_PCI) == "ok");
await Check("RLS admin_api: UPDATE location.slug denied (column grant, 42501); name allowed", async () =>
    await Try(adminDs, $"UPDATE auth.location SET slug = 'zz' WHERE location_id = '{L_PCI}'", "super") == "42501"
    && await Try(adminDs, $"UPDATE auth.location SET name = 'renamed' WHERE location_id = '{L_PCI}'", "super") == "ok");
await Check("P2-1 admin_api: UPDATE location_login_config.location_id denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET location_id = gen_random_uuid() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: UPDATE location_login_config.created_at denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET created_at = now() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: UPDATE location_login_config.created_by denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET created_by = gen_random_uuid() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: §8.5 UPDATE of all 11 mutable columns allowed (rolled back)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET provider_kind = provider_kind, display_name = display_name, entra_allowed_tenant_ids = entra_allowed_tenant_ids, oidc_authority = oidc_authority, oidc_client_id = oidc_client_id, secret_ref = secret_ref, allow_db_fallback = allow_db_fallback, mfa_required = mfa_required, updated_by = '{ACTOR}', updated_at = now(), row_version = row_version + 1 WHERE location_id = '{L_PCI}'", "super") == "ok");
await Check("P2-1 admin_api: UPDATE on change table / audit_outbox denied (append-only, 42501)", async () =>
    await Try(adminDs, "UPDATE auth.location_login_config_change SET at = now()", "super") == "42501"
    && await Try(adminDs, "UPDATE auth.audit_outbox SET occurred_at = now()", "super") == "42501");
await Check("RLS admin_api: DELETE denied on location/config/change (42501)", async () =>
    await Try(adminDs, "DELETE FROM auth.location", "super") == "42501" && await Try(adminDs, "DELETE FROM auth.location_login_config", "super") == "42501"
    && await Try(adminDs, "DELETE FROM auth.location_login_config_change", "super") == "42501");
await Check("definer fn: partner pci admin attributes nevada location to its partner (404 vs forbidden_scope)", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, "SELECT set_config('app.admin_type','partner',true), set_config('app.partner_id',$1,true)", P_PCI.ToString());
    var p = await Scalar<Guid>(c, tx, "SELECT auth.location_partner($1)", L_NV);
    var none = await Scalar<object>(c, tx, "SELECT auth.location_partner(gen_random_uuid())");
    return p == P_NV && none is DBNull;
});
await Check("definer fn: tenant_bound_elsewhere(T1, nevada)=true, (T1, pci)=false, (T2, nevada)=false", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    return await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T1, P_NV)
        && !await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T1, P_PCI)
        && !await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T2, P_NV);
});
await Check("login: sees all locations and configs (no settings needed)", async () => await CountAs(loginDs, cntLoc, null, null) == 2 && await CountAs(loginDs, cntCfg, null, null) == 2);
await Check("login: SELECT change table denied (42501)", async () => await Try(loginDs, cntChg) == "42501");
await Check("login: cannot execute definer fns (42501)", async () => await Try(loginDs, $"SELECT auth.location_partner('{L_PCI}')") == "42501");
await Check("login: cannot write location/config/app_user/external_login/user_location (42501)", async () =>
    await Try(loginDs, "UPDATE auth.location_login_config SET mfa_required = true") == "42501" && await Try(loginDs, "INSERT INTO auth.location VALUES (gen_random_uuid(), gen_random_uuid(), 'zz', 'x')") == "42501"
    && await Try(loginDs, "UPDATE auth.app_user SET status = 'active'") == "42501" && await Try(loginDs, "DELETE FROM auth.external_login") == "42501"
    && await Try(loginDs, "DELETE FROM auth.user_location") == "42501");
await Check("login: session UPDATE of user_id denied; of revoked_at/revoked_reason allowed", async () =>
    await Try(loginDs, "UPDATE auth.session SET user_id = gen_random_uuid()") == "42501" && await Try(loginDs, "UPDATE auth.session SET revoked_at = now(), revoked_reason = 'logout'") == "ok");
await Check("admin_api: cannot read app_user/external_login/user_location/session/oidc_replay; cannot SELECT audit_outbox (42501)", async () =>
    await Try(adminDs, "SELECT 1 FROM auth.app_user") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.external_login") == "42501"
    && await Try(adminDs, "SELECT 1 FROM auth.user_location") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.session") == "42501"
    && await Try(adminDs, "SELECT 1 FROM auth.oidc_replay") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.audit_outbox") == "42501");
await Check("owner: FORCE RLS + owner policy -> owner sees all rows", async () => await CountAs(ownerDs, cntLoc, null, null) == 2);
await Check("admin/login: TRUNCATE denied (42501)", async () => await Try(adminDs, "TRUNCATE auth.location_login_config_change") == "42501" && await Try(loginDs, "TRUNCATE auth.oidc_replay") == "42501");

// ================= 6. constraints (owner, each rolled back) =================
var cfgCols = "(location_id, provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at, row_version)";
string NewLoc(string id) => $"INSERT INTO auth.location VALUES ('{id}', '{P_PCI}', 'zz-{id[..8]}', 'x');";
const string LX = "5f0c3e1a-00ee-4000-8000-0000000000e1";
string Cfg(string kind, string tids, string auth, string client, string secret, string fallback, string rv = "1") =>
    NewLoc(LX) + $"INSERT INTO auth.location_login_config {cfgCols} VALUES ('{LX}', {kind}, 'd', {tids}, {auth}, {client}, {secret}, {fallback}, false, '{ACTOR}', now(), '{ACTOR}', now(), {rv})";
string ent = $"'{Iss(T2)}'";
var cases = new (string name, string sql, string state)[]
{
    ("location slug uppercase", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'PCI-x', 'x')", "23514"),
    ("location slug leading hyphen", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '-pci', 'x')", "23514"),
    ("location slug 64 chars", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '{new string('a', 64)}', 'x')", "23514"),
    ("location slug 63 chars ok", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '{new string('a', 63)}', 'x')", "ok"),
    ("location slug duplicate", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'pci-main', 'x')", "23505"),
    ("location name empty", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'zz-n', '')", "23514"),
    ("location name 201", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'zz-n', '{new string('n', 201)}')", "23514"),
    ("config entra ok", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "true"), "ok"),
    ("config entra 0 tenants", Cfg("'entra'", "'{}'", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra 2 tenants", Cfg("'entra'", $"ARRAY['{T1}','{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra no secret_ref", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "NULL", "false"), "23514"),
    ("config entra no authority", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", "NULL", "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra no client id", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "NULL", "'arn:FAKE'", "false"), "23514"),
    ("config database ok", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "false"), "ok"),
    ("config database with tenant", Cfg("'database'", $"ARRAY['{T2}']::uuid[]", "NULL", "NULL", "NULL", "false"), "23514"),
    ("config database with secret_ref", Cfg("'database'", "'{}'", "NULL", "NULL", "'arn:FAKE'", "false"), "23514"),
    ("config database with allow_db_fallback", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "true"), "23514"),
    ("config provider 'Entra' (case)", Cfg("'Entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config row_version 0", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "false", "0"), "23514"),
    ("config without location (FK)", $"INSERT INTO auth.location_login_config {cfgCols} VALUES (gen_random_uuid(), 'database', 'd', '{{}}', NULL, NULL, NULL, false, false, '{ACTOR}', now(), '{ACTOR}', now(), 1)", "23503"),
    ("delete location with config (RESTRICT)", $"DELETE FROM auth.location WHERE location_id = '{L_NV}'", "23503"),
    ("change reason 9 chars", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '123456789', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change reason 10 chars ok", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '1234567890', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "ok"),
    ("change reason padded", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', ' 1234567890', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change reason 501", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '{new string('r', 501)}', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change admin_type 'owner'", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'owner', NULL, now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change kind 'deleted'", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', NULL, now(), 'deleted', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change after NULL", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', NULL, now(), 'updated', '{{}}', NULL, NULL, 2)", "23502"),
    ("app_user status 'locked'", "INSERT INTO auth.app_user VALUES (gen_random_uuid(), NULL, 'locked', NULL)", "23514"),
    ("app_user status 'Active'", "INSERT INTO auth.app_user VALUES (gen_random_uuid(), NULL, 'Active', NULL)", "23514"),
    ("external_login provider 'database'", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('database', '{Iss(T1)}', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer for another tenant", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T2)}', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer trailing slash", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}/', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer http", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', 'http://login.microsoftonline.com/{T1}/v2.0', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login duplicate PK", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}', '{T1}', '{S1}', '{U_C03}')", "23505"),
    ("external_login unknown user (FK)", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}', '{T1}', gen_random_uuid(), gen_random_uuid())", "23503"),
    ("user_location unknown user (FK)", $"INSERT INTO auth.user_location (user_id, location_id) VALUES (gen_random_uuid(), '{L_PCI}')", "23503"),
    ("delete location with user_location (RESTRICT)", $"DELETE FROM auth.location_login_config WHERE location_id = '{L_PCI}'; DELETE FROM auth.location_login_config_change WHERE location_id = '{L_PCI}'; DELETE FROM auth.location WHERE location_id = '{L_PCI}'", "23503"),
    ("audit_outbox event_type mismatch", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"b","event_id":"5f0c3e1a-00ff-4000-8000-000000000001"}')""", "23514"),
    ("audit_outbox event_id mismatch", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"a","event_id":"x"}')""", "23514"),
    ("audit_outbox ok", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"a","event_id":"5f0c3e1a-00ff-4000-8000-000000000001"}')""", "ok"),
    ("oidc_replay key_hash 31 bytes", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 31), 'hex'), now() + interval '10 minutes')", "23514"),
    ("oidc_replay kind 'state'", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('state', decode(repeat('ab', 32), 'hex'), now() + interval '10 minutes')", "23514"),
    ("oidc_replay ttl > 24h", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 32), 'hex'), now() + interval '25 hours')", "23514"),
    ("oidc_replay expires <= created", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 32), 'hex'), now())", "23514"),
    ("oidc_replay duplicate (replay)", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('code', decode(repeat('ab', 32), 'hex'), now() + interval '5 minutes'); INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('code', decode(repeat('ab', 32), 'hex'), now() + interval '5 minutes')", "23505"),
};
string Sess(string over) =>
    "INSERT INTO auth.session (session_id, cookie_key_hash, user_id, partner_id, location_id, admin_type, session_kind, login_provider, entra_tenant_id, config_row_version, amr, auth_time, last_seen_at, revoked_at, revoked_reason) SELECT * FROM (SELECT gen_random_uuid(), decode(repeat('cd', 32), 'hex'), "
    + $"'{U_C01}'::uuid, '{P_PCI}'::uuid, '{L_PCI}'::uuid, NULL::text, 'normal'::text, 'entra'::text, '{T1}'::uuid, 1::bigint, ARRAY['pwd','mfa']::text[], now(), now(), NULL::timestamptz, NULL::text) s(a,b,c,d,e,f,g,h,i,j,k,l,m,n,o) " + over;
var sessCases = new (string, string, string)[]
{
    ("session entra ok", Sess(""), "ok"),
    ("session entra without tenant", Sess("").Replace($"'{T1}'::uuid", "NULL::uuid"), "23514"),
    ("session entra without config_row_version", Sess("").Replace("1::bigint", "NULL::bigint"), "23514"),
    ("session entra with admin_type", Sess("").Replace("NULL::text, 'normal'", "'super'::text, 'normal'"), "23514"),
    ("session database with tenant", Sess("").Replace("'entra'::text", "'database'::text"), "23514"),
    ("session no scope (no admin, no location)", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace($"'{L_PCI}'::uuid", "NULL::uuid"), "23514"),
    ("session partner admin without partner", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace("NULL::text, 'normal'", "'partner'::text, 'normal'").Replace($"'{P_PCI}'::uuid", "NULL::uuid"), "23514"),
    ("session amr empty", Sess("").Replace("ARRAY['pwd','mfa']::text[]", "'{}'::text[]"), "23514"),
    ("session amr 'wiaormfa'", Sess("").Replace("ARRAY['pwd','mfa']::text[]", "ARRAY['wiaormfa']::text[]"), "23514"),
    ("session recovery_reenroll with mfa", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace("'normal'::text", "'recovery_reenroll'::text"), "23514"),
    ("session revoked_at without reason", Sess("").Replace("NULL::timestamptz", "now()"), "23514"),
    ("session revoked_reason format", Sess("").Replace("NULL::timestamptz, NULL::text", "now(), 'Logout!'::text"), "23514"),
    ("session cookie hash 31 bytes", Sess("").Replace("repeat('cd', 32)", "repeat('cd', 31)"), "23514"),
    ("session duplicate cookie hash", Sess("") + "; " + Sess(""), "23505"),
};
foreach (var (name, sql, state) in cases.Concat(sessCases))
{
    var got = await Try(ownerDs, sql);
    Ok($"constraint: {name} -> {state}", got == state, got);
}

// ================= 7. optimistic concurrency (§8.5) =================
await Check("concurrency: two EF contexts (admin_api, super) on row_version 1 -> 2nd SaveChanges throws DbUpdateConcurrencyException; row_version 2", async () =>
{
    async Task<(AuthDbContext db, NpgsqlConnection c, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)> Open()
    {
        var c = await adminDs.OpenConnectionAsync();
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(c).Options);
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.admin_type','super',true)");
        return (db, c, tx);
    }
    var (db1, c1, t1) = await Open();
    var (db2, c2, t2) = await Open();
    try
    {
        var r1 = await db1.LocationLoginConfigRows.SingleAsync(x => x.LocationId == L_NV);
        var r2 = await db2.LocationLoginConfigRows.SingleAsync(x => x.LocationId == L_NV);
        if (r1.RowVersion != 1 || r2.RowVersion != 1) return false;
        r1.MfaRequired = true; r1.RowVersion += 1; r1.UpdatedAt = DateTimeOffset.UtcNow;
        await db1.SaveChangesAsync(); await t1.CommitAsync();
        r2.DisplayName = "stale writer"; r2.RowVersion += 1;
        try { await db2.SaveChangesAsync(); return false; }
        catch (DbUpdateConcurrencyException) { }
        await t2.RollbackAsync();
        await using var oc = await ownerDs.OpenConnectionAsync();
        return await Scalar<long>(oc, null, $"SELECT row_version FROM auth.location_login_config WHERE location_id = '{L_NV}'") == 2
            && await Scalar<string>(oc, null, $"SELECT display_name FROM auth.location_login_config WHERE location_id = '{L_NV}'") == "Nevada password";
    }
    finally { await db1.DisposeAsync(); await db2.DisposeAsync(); await c1.DisposeAsync(); await c2.DisposeAsync(); }
});
await Check("concurrency: raw §8.5 UPDATE ... WHERE row_version = stale -> 0 rows; = current -> 1 row", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, "SELECT set_config('app.admin_type','super',true)");
    await using var s = new NpgsqlCommand($"UPDATE auth.location_login_config SET row_version = row_version + 1 WHERE location_id = '{L_NV}' AND row_version = 1", c, tx);
    await using var k = new NpgsqlCommand($"UPDATE auth.location_login_config SET row_version = row_version + 1 WHERE location_id = '{L_NV}' AND row_version = 2", c, tx);
    var a = await s.ExecuteNonQueryAsync(); var b = await k.ExecuteNonQueryAsync();
    await tx.RollbackAsync();
    return a == 0 && b == 1;
});

// ================= 8. readers (as login) =================
// [TLD 2026-11-04 10:02 Q1] deployment config is the source of truth for oidc_* / secret_ref: these values differ from the row's
// (row: Iss(T1) / FakeArn), so the checks below prove the reader returns config, never the per-row copy. Same check count (162).
const string CfgArn = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/deploy-cfg-AbCdEf"; // SSE2-FAKE-TEST-ONLY
const string CfgClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc"; // SSE2-FAKE-TEST-ONLY
var entraCfg = new Auth.Data.EntraDeployment("https://login.microsoftonline.com", CfgClientId, new SecretReference(CfgArn)); // host-validated values (TLD 10:12)
var cfgReader = new NpgsqlLocationLoginConfigReader(loginDs, entraCfg);
var extReader = new NpgsqlExternalLoginReader(loginDs);
var ulReader = new NpgsqlUserLocationReader(loginDs);
await Check("reader config: GetAsync(pci-main) -> entra row, tenant T1, row_version 1, oidc_* and SecretRef from deployment config not the row (TLD 10:02 Q1), ToString redacted", async () =>
{
    var c = await cfgReader.GetAsync(L_PCI, default);
    return c is not null && c.ProviderKind == ProviderKind.Entra && c.PartnerId == P_PCI && c.Slug == "pci-main" && c.EntraAllowedTenantIds.SequenceEqual([T1])
        && c.OidcAuthority == "https://login.microsoftonline.com" && c.OidcClientId == CfgClientId && c.AllowDbFallback && c.MfaRequired && c.RowVersion == 1
        && c.SecretRef?.Reveal() == CfgArn && CfgArn != FakeArn
        && !c.ToString().Contains("000000000000") && !JsonSerializer.Serialize(c).Contains("000000000000");
});
await Check("reader config: GetAsync(nevada-main) -> database, SecretRef null, row_version 2 (after concurrency check)", async () =>
{
    var c = await cfgReader.GetAsync(L_NV, default);
    return c is { ProviderKind: ProviderKind.Database, SecretRef: null, OidcAuthority: null, OidcClientId: null, RowVersion: 2 } && c.EntraAllowedTenantIds.Count == 0;
});
await Check("reader config: unknown id -> null", async () => await cfgReader.GetAsync(Guid.NewGuid(), default) is null);
await Check("reader config: GetBySlugAsync(\" PCI-Main \") normalises -> pci-main", async () => (await cfgReader.GetBySlugAsync(" PCI-Main ", default))?.LocationId == L_PCI);
await Check("reader config: GetBySlugAsync(unknown) -> null", async () => await cfgReader.GetBySlugAsync("zz-none", default) is null);
await Check("reader config: location without config row -> null (inner join)", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, $"INSERT INTO auth.location VALUES ('{LX}', '{P_PCI}', 'zz-noconfig', 'x')");
    await tx.CommitAsync();
    var r = await cfgReader.GetAsync(Guid.Parse(LX), default) is null && await cfgReader.GetBySlugAsync("zz-noconfig", default) is null;
    await Exec(c, null, $"DELETE FROM auth.location WHERE location_id = '{LX}'");
    return r;
});

await Check("reader ext: linked active user (c01) -> Active, partner pci, not locked, MaySignIn", async () =>
{
    var m = await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default);
    return m is not null && m.UserId == U_C01 && m.PartnerId == P_PCI && m.Status == UserStatus.Active && m.LockedUntil is null && m.MaySignIn(DateTimeOffset.UtcNow);
});
await Check("reader ext: disabled user -> Disabled, MaySignIn false", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S2, default) is { Status: UserStatus.Disabled } m && !m.MaySignIn(DateTimeOffset.UtcNow));
await Check("reader ext: locked user -> Active + locked_until in future, MaySignIn false now, true after", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S3, default) is { Status: UserStatus.Active, LockedUntil: { } lu } m
    && !m.MaySignIn(DateTimeOffset.UtcNow) && m.MaySignIn(lu.AddSeconds(1)));
await Check("reader ext: no external_login row -> null", async () => await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S9, default) is null);
await Check("reader ext: issuer must match exactly (trailing slash / upper case -> null)", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1) + "/", T1, S1, default) is null
    && await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1).ToUpperInvariant(), T1, S1, default) is null);
await Check("reader ext: right subject, other tenant -> null", async () => await extReader.FindUserAsync(ProviderKind.Entra, Iss(T2), T2, S1, default) is null);
await Throws<ArgumentOutOfRangeException>("reader ext: providerKind Database -> ArgumentOutOfRangeException", () => extReader.FindUserAsync(ProviderKind.Database, Iss(T1), T1, S1, default));

// N3: orphan link must throw. The FK forbids the state, so it is created inside a rolled-back OWNER transaction with the
// FK dropped, and the reader's own SQL/mapping runs on that transaction via FindOnConnectionAsync (same code path).
async Task<ExternalLoginMatch?> WithBrokenState(string breakSql, Guid subject)
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    try
    {
        await Exec(c, tx, breakSql);
        return await NpgsqlExternalLoginReader.FindOnConnectionAsync(c, tx, ProviderKind.Entra, Iss(T1), T1, subject, default);
    }
    finally { await tx.RollbackAsync(); }
}
var orphan = Guid.Parse("5f0c3e1a-0003-4000-8000-0000000000ff");
await Throws<InvalidOperationException>("N3: external_login row without app_user row -> throws (never null); FK dropped in rolled-back owner tx",
    () => WithBrokenState($"ALTER TABLE auth.external_login DROP CONSTRAINT external_login_user_fk; INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}', '{T1}', '{S9}', '{orphan}')", S9),
    e => e.Message.Contains("no auth.app_user row"));
await Throws<InvalidOperationException>("N3: same via deleting the app_user row (FK dropped) -> throws",
    () => WithBrokenState($"ALTER TABLE auth.external_login DROP CONSTRAINT external_login_user_fk; DELETE FROM auth.user_location WHERE user_id = '{U_C01}'; DELETE FROM auth.app_user WHERE user_id = '{U_C01}'", S1));
await Throws<InvalidOperationException>("N1/7:53: unknown status 'locked' (CHECK dropped in rolled-back tx) -> throws, never Active",
    () => WithBrokenState($"ALTER TABLE auth.app_user DROP CONSTRAINT app_user_status; UPDATE auth.app_user SET status = 'locked' WHERE user_id = '{U_C01}'", S1));
await Throws<InvalidOperationException>("N1/7:53: status 'Active' (case) -> throws", () => WithBrokenState($"ALTER TABLE auth.app_user DROP CONSTRAINT app_user_status; UPDATE auth.app_user SET status = 'Active' WHERE user_id = '{U_C01}'", S1));
await Check("N3: FK and CHECK still present after the rolled-back probes", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    return await Scalar<long>(c, null, "SELECT count(*) FROM pg_constraint WHERE conname IN ('external_login_user_fk','app_user_status')") == 2;
});
await Check("N3: on-connection path returns the same match as FindUserAsync for a valid row", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    var a = await NpgsqlExternalLoginReader.FindOnConnectionAsync(c, null, ProviderKind.Entra, Iss(T1), T1, S1, default);
    var b = await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default);
    return a is not null && a == b;
});

await Check("reader user_location: c01@pci-main true; c01@nevada-main false; c03@pci-main false; unknown false", async () =>
    await ulReader.IsAssignedAsync(U_C01, L_PCI, default) && !await ulReader.IsAssignedAsync(U_C01, L_NV, default)
    && !await ulReader.IsAssignedAsync(U_C03, L_PCI, default) && !await ulReader.IsAssignedAsync(Guid.NewGuid(), L_PCI, default));

// F8: DB failure and cancellation throw; never null/false.
var badCs = new NpgsqlConnectionStringBuilder(loginCs) { Password = "wrong-" + Guid.NewGuid().ToString("N"), Pooling = false }.ConnectionString;
await using (var badDs = NpgsqlDataSource.Create(badCs))
{
    await Throws<PostgresException>("F8: config GetAsync with DB auth failure -> throws (28P01)", () => new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetAsync(L_PCI, default), e => e.SqlState == "28P01");
    await Throws<PostgresException>("F8: config GetBySlugAsync with DB failure -> throws", () => new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetBySlugAsync("pci-main", default));
    await Check("F8: GetBySlugAsync(malformed) -> null without touching the DB (broken data source)", async () =>
        await new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetBySlugAsync("pci_main!", default) is null);
    await Throws<PostgresException>("F8: FindUserAsync with DB failure -> throws", () => new NpgsqlExternalLoginReader(badDs).FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default));
    await Throws<PostgresException>("F8: IsAssignedAsync with DB failure -> throws (never false)", () => new NpgsqlUserLocationReader(badDs).IsAssignedAsync(U_C01, L_PCI, default));
}
await using (var adminAsLogin = NpgsqlDataSource.Create(adminCs))
{
    await Throws<PostgresException>("F8: reader on a role without SELECT (admin_api on external_login) -> throws 42501", () => new NpgsqlExternalLoginReader(adminAsLogin).FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default), e => e.SqlState == "42501");
    await Throws<PostgresException>("F8: IsAssignedAsync on a role without SELECT -> throws 42501", () => new NpgsqlUserLocationReader(adminAsLogin).IsAssignedAsync(U_C01, L_PCI, default), e => e.SqlState == "42501");
}
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    await Throws<OperationCanceledException>("F8: config GetAsync cancelled -> OperationCanceledException", () => cfgReader.GetAsync(L_PCI, cts.Token));
    await Throws<OperationCanceledException>("F8: config GetBySlugAsync cancelled -> OperationCanceledException", () => cfgReader.GetBySlugAsync("pci-main", cts.Token));
    await Throws<OperationCanceledException>("F8: FindUserAsync cancelled -> OperationCanceledException", () => extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, cts.Token));
    await Throws<OperationCanceledException>("F8: IsAssignedAsync cancelled -> OperationCanceledException", () => ulReader.IsAssignedAsync(U_C01, L_PCI, cts.Token));
}
await Check("F8: in-flight cancellation (pg_sleep) -> OperationCanceledException", async () =>
{
    await using var c = await loginDs.OpenConnectionAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    await using var cmd = new NpgsqlCommand("SELECT pg_sleep(5)", c);
    try { await cmd.ExecuteNonQueryAsync(cts.Token); return false; } catch (OperationCanceledException) { return true; }
});

}
finally
{
    await CleanupCheckData();
}
await Check("K3: cleanup -> DB back in the post-seed state (re-runnable)", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    return await Scalar<long>(c, null, StateSql) == 0;
});

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
