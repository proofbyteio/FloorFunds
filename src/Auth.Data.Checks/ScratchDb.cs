// Scratch DB for the N3/N1 fail-closed probes [TLD 2026-11-04 11:21, Q2 option b]. The probes need states the constraints
// forbid (orphan link, unknown status); they are COMMITTED only in a throwaway database ff_scratch_<runid>, migrated with
// the same MigrateAsync runner, and read back through the public FindUserAsync. Rules (TLD 11:21, TLD 11:57 o1-o3, TLD 12:04 F1):
//   - cluster [TLD 2026-11-04 12:04, replaces the 5432-only part of 11:21]: host is loopback only, 127.0.0.1 or ::1
//     (SCRATCH_PG_HOST, default 127.0.0.1; hostnames incl. 'localhost', other IPs, host lists and Unix socket paths are
//     refused before any connection). Port SCRATCH_PG_PORT (default 5432) and maintenance DB SCRATCH_MAINT_DB (default
//     postgres; never ff_sse / ff_sse2 / template* / ff_scratch_*) are overridable. Credentials come from
//     SCRATCH_ADMIN_CONNECTION_STRING (role ff_scratchadm, tools/scratch-admin-setup.sh); its Host must be absent or equal the
//     loopback host, and its Port / Database are replaced by the values above. Never prints connection strings.
//   - name ^ff_scratch_[a-z0-9]{6,24}\z (\z, never $: a trailing newline is refused, SSE2 09e31f68 L2), runid = UTC yyyyMMddHHmmss + 6 hex; never ff_sse / ff_sse2 / postgres / template* /
//     ff_maint_scratch (explicit guard); refuse when the name already exists.
//   - marked COMMENT ON DATABASE ... 'floorfunds-scratch' right after CREATE; nothing is ever dropped without that marker
//     AND ownership by the maintenance role (DROP DATABASE needs ownership anyway).
//   - dropped in a finally by the run that made it. Start-up sweep [TLD 2026-11-04 11:57 o1/o2]: a MARKED leftover is dropped
//     only when its runid timestamp is more than 1 hour old AND pg_stat_activity shows no connection to it; the current
//     run's DB is never dropped; no WITH (FORCE). An UNMARKED (or not-ours) ff_scratch_* DB is never dropped: it is reported
//     by name and the caller fails the run.
//   - o3: no other cluster-level object is created; the per-run <db>_admin_api / <db>_login roles the frozen migration needs
//     are marked and dropped with the DB, and ClusterSnapshotAsync (roles, memberships, tablespaces, databases, shared
//     comments, role settings) must be identical before and after a run.
//   - [TLD 2026-11-04 12:33 item 1] o3 now reads "net role set identical; per-run roles allowed only if marked and dropped":
//     per-run roles carry the runid in their name (<runid>_admin_api / <runid>_login), are marked COMMENT ON ROLE
//     'floorfunds-scratch' and are dropped in the same finally as the DB. The start-up sweep drops a stale MARKED
//     ff_scratch_* role only when its runid timestamp is older than 1 h, no pg_stat_activity session runs as it, and its DB is
//     gone (a DB that is kept keeps its roles; DropAsync drops them with it). An UNMARKED ff_scratch_* look-alike role is never
//     dropped: it is reported by name (FATAL) and the caller fails the run. ff_scratchadm is documented kit infrastructure
//     (marked 'floorfunds-scratch-admin', LOGIN NOSUPERUSER NOINHERIT CREATEDB CREATEROLE NOREPLICATION NOBYPASSRLS, connlimit
//     -1, no VALID UNTIL, no per-role settings); AdminRoleViolationsAsync fails on any other attribute or membership. The only
//     memberships allowed are the PG16+/PG17 implicit grants made when ff_scratchadm creates a role: ff_scratchadm is a member
//     of each MARKED per-run ff_scratch_*_(admin_api|login) role with ADMIN OPTION, INHERIT FALSE, SET FALSE
//     (createrole_self_grant empty), grantor the bootstrap superuser (oid 10); they disappear with the role. Nothing is ever a
//     member OF ff_scratchadm.
//   - [TLD 2026-11-04 12:33 item 3] the maintenance-DB exception is scoped in code: the maintenance connection is only
//     reachable as MaintConn, and every statement on it goes through MaintSql.Classify first. Allowed: CONNECT; the exact read
//     statements in MaintSql.Reads (catalogs pg_database, pg_roles, pg_stat_activity, pg_auth_members, pg_shdescription,
//     pg_tablespace, pg_db_role_setting; no pg_authid); CREATE / COMMENT 'floorfunds-scratch' / DROP DATABASE or ROLE on
//     ff_scratch_* names in the exact shapes of MaintSql.Ddl, DROP only when the object carries the marker (and a DB is owned
//     by the maintenance role). Anything else throws "refuse [maint-guard TLD 12:33]" before it is sent.
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Auth.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

static class ScratchDb
{
    public const string Marker = "floorfunds-scratch";
    public const string DefaultHost = "127.0.0.1", DefaultMaintDb = "postgres";
    public const int DefaultPort = 5432;
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(1);
    static readonly Regex NameRe = new("^ff_scratch_[a-z0-9]{6,24}\\z", RegexOptions.CultureInvariant);
    static readonly Regex RunIdRe = new("^ff_scratch_([0-9]{14})[0-9a-f]{6}\\z", RegexOptions.CultureInvariant);
    static readonly Regex RoleRunIdRe = new("^(ff_scratch_[0-9]{14}[0-9a-f]{6})_(?:admin_api|login)\\z", RegexOptions.CultureInvariant);
    public const string AdminMarker = "floorfunds-scratch-admin";
    // [TLD 12:33] the one allowed ff_scratchadm row (pg_roles: name|super|inherit|createrole|createdb|canlogin|replication|bypassrls|connlimit|validuntil|comment)
    public const string AdminRoleExpected = "ff_scratchadm|false|false|true|true|true|false|false|-1||" + AdminMarker;
    static readonly Regex DbIdentRe = new("^[a-z_][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);
    static readonly string[] Forbidden = ["ff_sse", "ff_sse2", "postgres", "template0", "template1", "ff_maint_scratch"];
    static readonly string[] AdminCsKeys = ["host", "server", "port", "database", "db", "username", "user id", "userid", "user name", "user", "password", "pwd", "psw", "pooling"];
    // [SSE2 review 09e31f68 L1] a marked runid in the future by more than this is reported (clock skew allowance).
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);
    public static int Opens; // maintenance connections attempted (the refusal checks assert it does not move)

    // Throws unless the name is a scratch name and not one of the protected databases.
    public static void GuardName(string name)
    {
        if (Forbidden.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("template", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"refuse: {name} is a protected database (ff_sse, ff_sse2, postgres, template*, ff_maint_scratch)");
        if (!NameRe.IsMatch(name)) throw new InvalidOperationException($"refuse: {name} is not a scratch name (^ff_scratch_[a-z0-9]{{6,24}}\\z)");
    }

    // [TLD 12:04] loopback literal only: exactly "127.0.0.1" or "::1". No DNS, no socket paths, no lists.
    public static void RequireLoopback(string? host)
    {
        if (host is not ("127.0.0.1" or "::1"))
            throw new InvalidOperationException("refuse: scratch DBs only on a loopback cluster (host must be 127.0.0.1 or ::1; hostnames, other IPs and Unix sockets are refused) [TLD 12:04]");
    }

    public static void RequireMaintDb(string db)
    {
        if (!DbIdentRe.IsMatch(db) || db is "ff_sse" or "ff_sse2" || db.StartsWith("template", StringComparison.Ordinal) || db.StartsWith("ff_scratch_", StringComparison.Ordinal))
            throw new InvalidOperationException($"refuse: maintenance DB {db} (never ff_sse / ff_sse2 / template* / ff_scratch_*) [TLD 12:04]");
    }

    // Pure string validation, no connection: loopback host, port, maintenance DB; returns the connection string to use.
    public static string ValidateAdminCs(string cs, string host = DefaultHost, int port = DefaultPort, string maintDb = DefaultMaintDb)
    {
        RequireLoopback(host);
        // [SSE2 review 09e31f68 Info] only these keys may appear in the admin connection string (no Passfile, Options,
        // Target Session Attributes, SSL / Kerberos settings ...); the key name is reported, never a value.
        var raw = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = cs };
        foreach (string k in raw.Keys)
            if (!AdminCsKeys.Contains(k.ToLowerInvariant()))
                throw new InvalidOperationException($"refuse: connection-string key '{k}' is not allowed in the scratch admin connection string (allowed: Host, Port, Database, Username, Password, Pooling) [SSE2 09e31f68 Info]");
        var b = new NpgsqlConnectionStringBuilder(cs);
        if (b.Host is { Length: > 0 } h && h != host) { RequireLoopback(h); throw new InvalidOperationException("refuse: connection string host differs from SCRATCH_PG_HOST [TLD 12:04]"); }
        if (port is < 1 or > 65535) throw new InvalidOperationException("refuse: bad port");
        RequireMaintDb(maintDb);
        b.Host = host; b.Port = port; b.Database = maintDb; b.Pooling = false;
        return b.ConnectionString;
    }

    public static (string host, int port, string maintDb) Target() => (
        Environment.GetEnvironmentVariable("SCRATCH_PG_HOST") is { Length: > 0 } h ? h : DefaultHost,
        Environment.GetEnvironmentVariable("SCRATCH_PG_PORT") is { Length: > 0 } p
            ? (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidOperationException("refuse: SCRATCH_PG_PORT is not a number"))
            : DefaultPort,
        Environment.GetEnvironmentVariable("SCRATCH_MAINT_DB") is { Length: > 0 } d ? d : DefaultMaintDb);

    public static string AdminCs()
    {
        var (host, port, maint) = Target();
        RequireLoopback(host);
        return ValidateAdminCs(Environment.GetEnvironmentVariable("SCRATCH_ADMIN_CONNECTION_STRING") is { Length: > 0 } v
            ? v : throw new InvalidOperationException("SCRATCH_ADMIN_CONNECTION_STRING not set (run src/Auth.Data/tools/scratch-admin-setup.sh)"), host, port, maint);
    }

    // Owner connection string for a scratch DB (same role and server, Database = name, no pooling so DROP is not blocked).
    public static string OwnerCs(string adminCs, string name)
    {
        GuardName(name);
        var b = new NpgsqlConnectionStringBuilder(adminCs) { Database = name, Pooling = false };
        RequireLoopback(b.Host);
        return b.ConnectionString;
    }

    public static async Task<MaintConn> OpenMaint(string adminCs)
    {
        RequireLoopback(new NpgsqlConnectionStringBuilder(adminCs).Host);
        Interlocked.Increment(ref Opens);
        var c = new NpgsqlConnection(adminCs);
        await c.OpenAsync();
        return new MaintConn(c);
    }

    // (exists, marked && owned by the maintenance role && not a template)
    public static async Task<(bool exists, bool ours)> StateAsync(string adminCs, string name)
    {
        await using var c = await OpenMaint(adminCs);
        return await c.StateAsync(name);
    }

    // runid = UTC yyyyMMddHHmmss + 6 hex (o1: the sweep reads the age from it). backdate is for the self-test only.
    public static string NewName(TimeSpan? backdate = null) =>
        "ff_scratch_" + (DateTime.UtcNow - (backdate ?? TimeSpan.Zero)).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..6];

    public static DateTime? RunIdUtc(string name) =>
        RunIdRe.Match(name) is { Success: true } m && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    // CREATE + mark (back to back, nothing in between). Returns the name; the caller drops it in a finally.
    // o2: if the COMMENT fails, the unmarked DB is left in place (never dropped) and reported by name.
    public static async Task<string> CreateMarkedAsync(string adminCs, string? name = null)
    {
        name ??= NewName();
        GuardName(name);
        if ((await StateAsync(adminCs, name)).exists) throw new InvalidOperationException($"refuse: {name} already exists");
        await using var c = await OpenMaint(adminCs);
        await c.ExecAsync($"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8'");
        try { await c.ExecAsync($"COMMENT ON DATABASE \"{name}\" IS '{Marker}'"); }
        catch (Exception e) { throw new InvalidOperationException($"FATAL scratch: created {name} but could not mark it; left in place UNMARKED (never dropped automatically): {e.Message}", e); }
        return name;
    }
    // App roles the migration requires (<db>_admin_api, <db>_login): plain LOGIN roles with NO password (cannot sign in),
    // marked right after CREATE [TLD 12:33 item 1], then MigrateAsync (the same runner as src/Auth.Data.Migrator `update`).
    public static async Task PrepareAsync(string adminCs, string name)
    {
        if (!(await StateAsync(adminCs, name)).ours) throw new InvalidOperationException($"refuse: {name} is not a marked scratch DB");
        await using (var c = await OpenMaint(adminCs))
            foreach (var r in new[] { name + "_admin_api", name + "_login" })
            {
                await c.ExecAsync($"CREATE ROLE \"{r}\" LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION");
                await c.ExecAsync($"COMMENT ON ROLE \"{r}\" IS '{Marker}'");
            }
        await using var db = new AuthDbContext(AuthDb.Options(OwnerCs(adminCs, name)));
        await db.Database.MigrateAsync();
    }

    // Drops a scratch DB and its two marked app roles. Refuses (throws) anything unmarked, not owned, or protected.
    // Returns the number of objects dropped (0 = nothing existed; callers must not print "dropped" then [SSE2 09e31f68 L2]).
    public static async Task<int> DropAsync(string adminCs, string name)
    {
        var dropped = 0;
        GuardName(name);
        var (exists, ours) = await StateAsync(adminCs, name);
        if (exists && !ours) throw new InvalidOperationException($"refuse: {name} has no '{Marker}' marker (or is not owned by the scratch role); not dropping");
        await using var m = await OpenMaint(adminCs);
        if (exists) { await m.ExecAsync($"DROP DATABASE \"{name}\""); dropped++; }
        foreach (var r in new[] { name + "_admin_api", name + "_login" })
        {
            var v = await m.ScalarAsync(MaintSql.RoleMarker, r) as string;
            if (v == Marker) { await m.ExecAsync($"DROP ROLE \"{r}\""); dropped++; }
            else if (v is not null && v != Marker) throw new InvalidOperationException($"refuse: role {r} has no '{Marker}' marker; not dropping");
        }
        return dropped;
    }

    // --scratch-drop [SSE2 review 09e31f68 M2]: refuses (rc 2, clear message, no crash) a marked DB that has connections,
    // anything unmarked / not ours / protected, and a 55006 race on DROP; "nothing to drop" when nothing existed (rc 0).
    public static async Task<(int rc, string msg)> DropCliAsync(string adminCs, string name)
    {
        try
        {
            GuardName(name);
            var (exists, ours) = await StateAsync(adminCs, name);
            var conns = exists && ours ? await ConnectionsAsync(adminCs, name) : 0;
            if (conns > 0) return (2, $"scratch-drop: refused {name}: {conns} connection(s) in pg_stat_activity; not dropped (close them first; no FORCE) [SSE2 09e31f68 M2]");
            var n = await DropAsync(adminCs, name);
            return (0, n == 0 ? $"scratch-drop: nothing to drop for {name} (no such DB, no marked roles)" : $"scratch-drop: dropped {name} ({n} object(s))");
        }
        catch (InvalidOperationException e) { return (2, $"scratch-drop: refused {name}: {e.Message}"); }
        catch (PostgresException e) when (e.SqlState == "55006") { return (2, $"scratch-drop: refused {name}: in use (SQLSTATE 55006); not dropped [SSE2 09e31f68 M2]"); }
    }

    public static async Task<List<string>> ListAsync(string adminCs, bool markedOnly)
    {
        await using var c = await OpenMaint(adminCs);
        return await c.ColumnAsync(MaintSql.DbList, markedOnly, Marker);
    }

    public static async Task<int> ConnectionsAsync(string adminCs, string name)
    {
        await using var c = await OpenMaint(adminCs);
        return (int)(long)(await c.ScalarAsync(MaintSql.DbConns, name))!;
    }

    // ff_scratch_* roles (name, comment), any marker state.
    public static async Task<List<(string role, string comment)>> RolesAsync(string adminCs)
    {
        await using var c = await OpenMaint(adminCs);
        return (await c.ColumnAsync(MaintSql.RoleList)).Select(l => (l[..l.IndexOf('|')], l[(l.IndexOf('|') + 1)..])).ToList();
    }

    public static async Task<int> RoleSessionsAsync(string adminCs, string role)
    {
        await using var c = await OpenMaint(adminCs);
        return (int)(long)(await c.ScalarAsync(MaintSql.RoleSessions, role))!;
    }

    public record SweepResult(List<string> Dropped, List<string> Kept, List<string> Unmarked, List<string> RolesDropped, List<string> RolesKept, List<string> UnmarkedRoles, List<string> BadRunId);

    // Start-up sweep [TLD 2026-11-04 11:57 o1/o2]. MARKED + ours + not the current run + runid older than 1 h + no connection
    // in pg_stat_activity -> DROP (no FORCE; 55006 -> kept). Everything else marked is kept with the reason printed.
    // UNMARKED / not-ours ff_scratch_* -> never dropped, returned in Unmarked (the caller fails the run).
    // Roles [TLD 2026-11-04 12:33 item 1]: a MARKED ff_scratch_<runid>_(admin_api|login) role is dropped only when its DB is
    // gone, its runid is older than 1 h and pg_stat_activity has no session as it; an UNMARKED ff_scratch_* role is never
    // dropped, returned in UnmarkedRoles (FATAL; the caller fails the run).
    public static async Task<SweepResult> SweepAsync(string adminCs, string? currentName, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        SweepResult res = new([], [], [], [], [], [], []);
        foreach (var n in await ListAsync(adminCs, markedOnly: false))
        {
            if (n == currentName) { res.Kept.Add(n); Console.WriteLine($"scratch-sweep: kept {n} (this run's own DB)"); continue; }
            bool ours;
            try { GuardName(n); ours = (await StateAsync(adminCs, n)).ours; } catch (InvalidOperationException) { ours = false; }
            if (!ours) { res.Unmarked.Add(n); Console.WriteLine($"scratch-sweep: UNMARKED (or not ours) {n}: not dropped"); continue; }
            var t = RunIdUtc(n);
            if (t is null || t.Value > now + FutureSkew) { res.BadRunId.Add(n); Console.WriteLine($"scratch-sweep: BAD RUNID {n} ({(t is null ? "missing / invalid" : "in the future")}): not dropped"); continue; }
            if (now - t.Value <= MinAge) { res.Kept.Add(n); Console.WriteLine($"scratch-sweep: kept {n} (age {(now - t.Value).TotalMinutes:F0} min <= 60)"); continue; }
            var conns = await ConnectionsAsync(adminCs, n);
            if (conns > 0) { res.Kept.Add(n); Console.WriteLine($"scratch-sweep: kept {n} ({conns} connection(s) in pg_stat_activity)"); continue; }
            try { await DropAsync(adminCs, n); res.Dropped.Add(n); Console.WriteLine($"scratch-sweep: dropped leftover {n} (age {(now - t.Value).TotalHours:F1} h, no connections)"); }
            catch (PostgresException e) when (e.SqlState == "55006") { res.Kept.Add(n); Console.WriteLine($"scratch-sweep: kept {n} (in use, 55006)"); }
        }
        await using (var m = await OpenMaint(adminCs))
            foreach (var line in await m.ColumnAsync(MaintSql.RoleList))
            {
                var role = line[..line.IndexOf('|')]; var comment = line[(line.IndexOf('|') + 1)..];
                if (comment != Marker) { res.UnmarkedRoles.Add(role); Console.WriteLine($"scratch-sweep: UNMARKED role {role}: not dropped"); continue; }
                var rm = RoleRunIdRe.Match(role);
                if (!rm.Success) { res.BadRunId.Add(role); Console.WriteLine($"scratch-sweep: BAD RUNID role {role} (missing / invalid): not dropped"); continue; }
                var db = rm.Groups[1].Value;
                if (db == currentName) { res.RolesKept.Add(role); Console.WriteLine($"scratch-sweep: kept role {role} (this run's)"); continue; }
                var rt = RunIdUtc(db);
                if (rt is null || rt.Value > now + FutureSkew) { res.BadRunId.Add(role); Console.WriteLine($"scratch-sweep: BAD RUNID role {role} ({(rt is null ? "invalid" : "in the future")}): not dropped"); continue; }
                if ((long)(await m.ScalarAsync(MaintSql.DbExists, db))! > 0) { res.RolesKept.Add(role); Console.WriteLine($"scratch-sweep: kept role {role} (its DB {db} still exists; dropped with it)"); continue; }
                var t = rt.Value;
                if (now - t <= MinAge) { res.RolesKept.Add(role); Console.WriteLine($"scratch-sweep: kept role {role} (age {(now - t).TotalMinutes.ToString("F0", CultureInfo.InvariantCulture)} min <= 60)"); continue; }
                var sess = (long)(await m.ScalarAsync(MaintSql.RoleSessions, role))!;
                if (sess > 0) { res.RolesKept.Add(role); Console.WriteLine($"scratch-sweep: kept role {role} ({sess} session(s) as it in pg_stat_activity)"); continue; }
                await m.ExecAsync($"DROP ROLE \"{role}\"");
                res.RolesDropped.Add(role); Console.WriteLine($"scratch-sweep: dropped stale marked role {role} (age {(now - t).TotalHours:F1} h, no sessions, DB gone)");
            }
        if (res.Unmarked.Count > 0)
            Console.WriteLine($"FATAL scratch-sweep: unmarked ff_scratch_* database(s) found, NOT dropped: {string.Join(", ", res.Unmarked)}. Check who made them; drop by hand only once known. This run fails. [TLD 11:57 o2]");
        if (res.BadRunId.Count > 0)
            Console.WriteLine($"FATAL scratch-sweep: marked ff_scratch_* object(s) with a missing, invalid or future runid, NOT dropped: {string.Join(", ", res.BadRunId)}. Check who made them; drop by name only once known. This run fails. [SSE2 09e31f68 L1]");
        if (res.UnmarkedRoles.Count > 0)
            Console.WriteLine($"FATAL scratch-sweep: unmarked ff_scratch_* look-alike role(s) found, NOT dropped: {string.Join(", ", res.UnmarkedRoles)}. Check who made them; drop by hand only once known. This run fails. [TLD 12:33]");
        return res;
    }

    // [TLD 12:33 item 1] ff_scratchadm must have exactly the documented attributes and only the allowed memberships.
    // Returns the violations (empty = ok). Read-only, through the maint guard.
    public static async Task<List<string>> AdminRoleViolationsAsync(string adminCs)
    {
        await using var m = await OpenMaint(adminCs);
        var v = new List<string>();
        var row = await m.ScalarAsync(MaintSql.AdminRole) as string;
        if (row != AdminRoleExpected) v.Add($"attributes: {row ?? "(no row)"} != {AdminRoleExpected}");
        if ((long)(await m.ScalarAsync(MaintSql.AdminSettings))! != 0) v.Add("pg_db_role_setting rows for the role (login settings)");
        foreach (var l in await m.ColumnAsync(MaintSql.AdminMembers))
        {
            // role|member|admin|inherit|set|grantor_is_bootstrap|role comment
            var f = l.Split('|');
            bool allowed = f.Length == 7 && f[1] == "ff_scratchadm" && Regex.IsMatch(f[0], "^" + MaintSql.R + "\\z") && f[6] == Marker
                           && f[2] == "true" && f[3] == "false" && f[4] == "false" && f[5] == "true";
            if (!allowed) v.Add($"membership: {l}");
        }
        return v;
    }

    // o3: cluster-level state that must be identical before and after a run. Read-only catalog queries (no pg_authid).
    // scopePrefix empty: every ff_scratch_* object is left out. A non-empty prefix keeps that run's own
    // database and roles and leaves every other ff_scratch_* name out, including shared-description counts.
    // This run's own database is gone. Another run's marked scratch database does not count.
    public static bool RunLeftNoMarkedScratch(string after, string before, bool stillExists, IReadOnlyCollection<string> marked, string runName)
        => after == before && !stillExists && !marked.Contains(runName);

    public static async Task<string> ClusterSnapshotAsync(string adminCs, string scopePrefix = "")
    {
        if (scopePrefix.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(scopePrefix, "^ff_scratch_[a-z0-9]{6,24}\\z"))
            throw new ArgumentException("snapshot scope must be empty or one ff_scratch_* prefix");
        await using var c = await OpenMaint(adminCs);
        var sb = new System.Text.StringBuilder();
        foreach (var (what, sql) in MaintSql.Snapshot)
            foreach (var l in await c.ColumnAsync(sql, scopePrefix)) sb.Append(what).Append(' ').Append(l).Append('\n');
        return sb.ToString();
    }

    // Verify-script / self-test only: an UNMARKED ff_scratch_* object made on purpose (o2 decoy DB, look-alike role).
    public static async Task CreateUnmarkedAsync(string adminCs, string name, bool role)
    {
        await using var m = await OpenMaint(adminCs);
        await m.ExecAsync(role ? $"CREATE ROLE \"{name}\" NOLOGIN" : $"CREATE DATABASE \"{name}\" TEMPLATE template0 ENCODING 'UTF8'");
    }
    // Self-test only: a marked stand-alone ff_scratch_<runid>_login role (no DB), optionally with a LOGIN password that lives
    // only in memory (the session case of the role sweep); never written anywhere. [TLD 2026-11-04 13:15, KF-A] the password
    // never reaches the server: only its client-side SCRAM-SHA-256 verifier (ScramVerifier) is sent in CREATE ROLE, and the
    // maint guard accepts no other PASSWORD shape. afterCreate (self-test only) runs after the CREATE, before the COMMENT,
    // while this session's pg_stat_activity.query is still the CREATE ROLE text.
    public static async Task CreateMarkedRoleAsync(string adminCs, string role, string? password = null, Func<Task>? afterCreate = null)
    {
        await using var m = await OpenMaint(adminCs);
        await m.ExecAsync($"CREATE ROLE \"{role}\" LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION" + (password is null ? "" : $" PASSWORD '{ScramVerifier(password)}'"));
        if (afterCreate is not null) await afterCreate();
        await m.ExecAsync($"COMMENT ON ROLE \"{role}\" IS '{Marker}'");
    }
    // [TLD 2026-11-04 13:15, KF-A] client-side SCRAM-SHA-256 verifier, the same format and derivation as the kit's scram() in
    // db/setup.sh (and libpq's PQencryptPasswordConn): RFC 5802 / RFC 7677, PBKDF2-HMAC-SHA256, 4096 iterations, random 16-byte
    // salt; ClientKey = HMAC(salted, "Client Key"), StoredKey = SHA256(ClientKey), ServerKey = HMAC(salted, "Server Key"),
    // base64 -> SCRAM-SHA-256$4096:<salt>$<StoredKey>:<ServerKey>. Like the kit (K5 / SASLprep note), only printable ASCII
    // (space to ~) is accepted, for which SASLprep is the identity. The salt parameter exists for the RFC 7677 vector check only.
    public const int ScramIterations = 4096;
    public static string ScramVerifier(string password, byte[]? salt = null)
    {
        if (password.Length == 0 || !password.All(ch => ch >= ' ' && ch <= '~'))
            throw new ArgumentException("refuse [KF-A]: the password must be non-empty printable ASCII (space to ~)");
        salt ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        if (salt.Length != 16) throw new ArgumentException("refuse [KF-A]: the SCRAM salt must be 16 bytes");
        var salted = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(System.Text.Encoding.UTF8.GetBytes(password), salt, ScramIterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var clientKey = System.Security.Cryptography.HMACSHA256.HashData(salted, "Client Key"u8);
        var storedKey = System.Security.Cryptography.SHA256.HashData(clientKey);
        var serverKey = System.Security.Cryptography.HMACSHA256.HashData(salted, "Server Key"u8);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(salted);
        return $"SCRAM-SHA-256${ScramIterations}:{Convert.ToBase64String(salt)}${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
    // [TLD 13:15 KF-A (b)] the query text of this role's OTHER sessions (pg_stat_activity.query: the last statement of an idle
    // session), read on a separate maintenance connection; used by the self-test while CreateMarkedRoleAsync waits in afterCreate.
    public static async Task<List<string>> OwnSessionQueriesAsync(string adminCs)
    {
        await using var m = await OpenMaint(adminCs);
        return await m.ColumnAsync(MaintSql.OwnQueries);
    }
    // [TLD 13:15 KF-A (b)/(c)] log_statement | password_encryption | may this role SET log_statement | may it read pg_authid.
    public static async Task<string> LogPolicyAsync(string adminCs)
    {
        await using var m = await OpenMaint(adminCs);
        return (string)(await m.ScalarAsync(MaintSql.LogPolicy))!;
    }
    // Exact-name cleanup of an UNMARKED object the verify script / self-test created itself (never used by the sweep): the
    // name must start with the given prefix, exist unmarked (a DB also owned by us), have no connections / sessions; it is then
    // marked and dropped through the marked-only path.
    public static async Task<string> CleanupUnmarkedAsync(string adminCs, string name, bool role, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return $"refuse: {name} does not start with {prefix}";
        await using var m = await OpenMaint(adminCs);
        if (role)
        {
            if (await m.ScalarAsync(MaintSql.RoleMarker, name) is not "") return $"refuse: role {name} is missing or already marked";
            if ((long)(await m.ScalarAsync(MaintSql.RoleSessions, name))! > 0) return $"refuse: role {name} has sessions";
            await m.ExecAsync($"COMMENT ON ROLE \"{name}\" IS '{Marker}'");
            await m.ExecAsync($"DROP ROLE \"{name}\"");
        }
        else
        {
            GuardName(name);
            if (await m.ScalarAsync(MaintSql.DbOwnedUnmarked, name) is not true) return $"refuse: {name} is missing, marked or not ours";
            if ((long)(await m.ScalarAsync(MaintSql.DbConns, name))! > 0) return $"refuse: {name} has connections";
            await m.ExecAsync($"COMMENT ON DATABASE \"{name}\" IS '{Marker}'");
            await m.ExecAsync($"DROP DATABASE \"{name}\"");
        }
        return $"cleaned up {name} (exact name, marked then dropped)";
    }

    public static string Diff(string a, string b)
    {
        var x = a.Split('\n').ToHashSet(); var y = b.Split('\n').ToHashSet();
        return string.Join("; ", x.Except(y).Select(l => "-" + l).Concat(y.Except(x).Select(l => "+" + l)));
    }
}

// [SSE2 review 09e31f68 M3] fail-fast single-runner guard: a session-level pg_try_advisory_lock on the KIT TARGET DB
// connection (CONNECTION_STRING: ff_sse, ff_sse2 or ff_kit_*), never on the maintenance connection, so the TLD 12:33
// maintenance allowlist is unchanged. Fixed key Key (ASCII "ffscratc"). Held until the process exits. Advisory locks are
// per database: runs against two different kit DBs do not exclude each other (documented limitation).
static class ScratchLock
{
    public const long Key = 0x6666736372617463; // = 7378711910451082339
    public const string Busy = "another scratch run holds the lock";
    static readonly Regex KitDbRe = new("^(?:ff_sse|ff_sse2|ff_kit_[a-z0-9_]{1,20})\\z", RegexOptions.CultureInvariant);
    public const string NotKit = "refuse: the scratch single-runner lock is taken on the kit target DB only (ff_sse, ff_sse2 or ff_kit_*)";
    // [TLD 2026-11-04 13:54 item 1] pure pre-check (no connection): null when the lock target is allowed, else the refusal
    // message (never echoes the name or any other connection-string value). Callers exit 2 with it; TryAcquireAsync still throws.
    public static string? Refusal(string kitCs) =>
        KitDbRe.IsMatch(new NpgsqlConnectionStringBuilder(kitCs).Database ?? "") ? null : NotKit;
    public static async Task<NpgsqlConnection?> TryAcquireAsync(string kitCs)
    {
        var b = new NpgsqlConnectionStringBuilder(kitCs) { Pooling = false };
        if (!KitDbRe.IsMatch(b.Database ?? "")) throw new InvalidOperationException(NotKit);
        var c = new NpgsqlConnection(b.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", c);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = Key });
        if ((bool)(await cmd.ExecuteScalarAsync())!) return c;
        await c.DisposeAsync();
        return null;
    }
    public static string BusyMessage(string kitCs) =>
        $"FATAL scratch-lock: {Busy} (pg_try_advisory_lock({Key}) on {new NpgsqlConnectionStringBuilder(kitCs).Database}); exiting now, nothing done (single runner) [SSE2 09e31f68 M3]";
}

// [TLD 2026-11-04 12:33 item 3] Statement allowlist for the maintenance connection (default DB postgres).
static class MaintSql
{
    public const string D = "ff_scratch_[a-z0-9]{6,24}";
    public const string R = D + "_(?:admin_api|login)";
    public const string Info = "SELECT current_setting('server_version'), current_user, current_database()";
    // [TLD 2026-11-04 13:15, KF-A] the only PASSWORD shape the guard accepts: a SCRAM-SHA-256 verifier with 4096 iterations, a
    // 16-byte salt and 32-byte keys (exact base64 lengths). Cleartext (hex or anything else), md5 hashes, other iteration
    // counts and anything with a quote are refused before sending.
    // [SSE2 04dba71d KF-A] PwNotVerifier: a PASSWORD literal not followed by 'SCRAM-SHA-256$4096:' (cleartext, md5, other
    // iteration counts ...); the self-test applies it to the statement text and pg_stat_activity, the verify script to the log.
    public static readonly Regex PwAny = new(@"(?i:PASSWORD)\s*'", RegexOptions.CultureInvariant);
    public static readonly Regex PwNotVerifier = new(@"(?i:PASSWORD)\s*'(?!SCRAM-SHA-256\$4096:)", RegexOptions.CultureInvariant);
    public const string ScramVerifierRe = @"SCRAM-SHA-256\$4096:[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=:[A-Za-z0-9+/]{43}=";
    public const string OwnQueries = "SELECT coalesce(query, '') FROM pg_stat_activity WHERE usename = current_user AND pid <> pg_backend_pid()";
    public const string LogPolicy = "SELECT current_setting('log_statement')||'|'||current_setting('password_encryption')||'|'||has_parameter_privilege('log_statement', 'SET')||'|'||has_table_privilege('pg_authid', 'SELECT')";
    public const string DbState = "SELECT coalesce(shobj_description(d.oid, 'pg_database'), '') = $2 AND d.datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND NOT d.datistemplate FROM pg_database d WHERE d.datname = $1";
    public const string DbOwnedUnmarked = "SELECT coalesce(shobj_description(oid, 'pg_database'), '') = '' AND datdba = (SELECT oid FROM pg_roles WHERE rolname = current_user) FROM pg_database WHERE datname = $1";
    public const string DbList = "SELECT datname FROM pg_database WHERE datname LIKE 'ff\\_scratch\\_%' AND ($1 = false OR coalesce(shobj_description(oid, 'pg_database'), '') = $2) ORDER BY 1";
    public const string DbExists = "SELECT count(*) FROM pg_database WHERE datname = $1";
    public const string DbConns = "SELECT count(*) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()";
    public const string RoleList = "SELECT rolname || '|' || coalesce(shobj_description(oid, 'pg_authid'), '') FROM pg_roles WHERE rolname LIKE 'ff\\_scratch\\_%' ORDER BY 1";
    public const string RoleMarker = "SELECT coalesce(shobj_description(oid, 'pg_authid'), '') FROM pg_roles WHERE rolname = $1";
    public const string RoleSessions = "SELECT count(*) FROM pg_stat_activity WHERE usename = $1";
    public const string AdminRole = "SELECT rolname||'|'||rolsuper||'|'||rolinherit||'|'||rolcreaterole||'|'||rolcreatedb||'|'||rolcanlogin||'|'||rolreplication||'|'||rolbypassrls||'|'||rolconnlimit||'|'||coalesce(rolvaliduntil::text,'')||'|'||coalesce(shobj_description(oid,'pg_authid'),'') FROM pg_roles WHERE rolname = current_user";
    public const string AdminSettings = "SELECT count(*) FROM pg_db_role_setting WHERE setrole = (SELECT oid FROM pg_roles WHERE rolname = current_user)";
    public const string AdminMembers = "SELECT r.rolname||'|'||m.rolname||'|'||a.admin_option||'|'||a.inherit_option||'|'||a.set_option||'|'||(a.grantor = 10)||'|'||coalesce(shobj_description(r.oid,'pg_authid'),'') FROM pg_auth_members a JOIN pg_roles r ON r.oid = a.roleid JOIN pg_roles m ON m.oid = a.member WHERE current_user IN (r.rolname, m.rolname) ORDER BY 1";
    // $1 is this run's ff_scratch_* prefix, or '' to leave every such name out. Another run's scratch
    // database, role, membership, role setting, and shared description does not change the result.
    public static readonly (string what, string sql)[] Snapshot = [
        ("role", "SELECT rolname||'|'||rolsuper||rolinherit||rolcreaterole||rolcreatedb||rolcanlogin||rolreplication||rolbypassrls||'|'||rolconnlimit||'|'||coalesce(rolvaliduntil::text,'')||'|'||coalesce(shobj_description(oid,'pg_authid'),'') FROM pg_roles WHERE NOT (rolname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(rolname, $1))) ORDER BY 1"),
        ("member", "SELECT r.rolname||'>'||m.rolname||'|'||a.admin_option||a.inherit_option||a.set_option FROM pg_auth_members a JOIN pg_roles r ON r.oid=a.roleid JOIN pg_roles m ON m.oid=a.member WHERE NOT (r.rolname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(r.rolname, $1))) AND NOT (m.rolname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(m.rolname, $1))) ORDER BY 1"),
        ("tablespace", "SELECT spcname||'|'||pg_get_userbyid(spcowner)||'|'||coalesce(spcacl::text,'') FROM pg_tablespace WHERE $1 = $1 ORDER BY 1"),
        ("database", "SELECT datname||'|'||pg_get_userbyid(datdba)||'|'||datistemplate||datallowconn||'|'||coalesce(datacl::text,'')||'|'||coalesce(shobj_description(oid,'pg_database'),'') FROM pg_database WHERE NOT (datname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(datname, $1))) ORDER BY 1"),
        ("rolesetting", "SELECT setdatabase||'|'||setrole||'|'||array_to_string(setconfig,',') FROM pg_db_role_setting s WHERE NOT EXISTS (SELECT 1 FROM pg_roles r WHERE r.oid = s.setrole AND r.rolname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(r.rolname, $1))) AND NOT EXISTS (SELECT 1 FROM pg_database d WHERE d.oid = s.setdatabase AND d.datname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(d.datname, $1))) ORDER BY 1"),
        ("shdesc", "SELECT d.classoid::regclass::text||'|'||count(*) FROM pg_shdescription d WHERE NOT EXISTS (SELECT 1 FROM pg_database db WHERE db.oid = d.objoid AND d.classoid = 'pg_database'::regclass AND db.datname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(db.datname, $1))) AND NOT EXISTS (SELECT 1 FROM pg_roles r WHERE r.oid = d.objoid AND d.classoid = 'pg_authid'::regclass AND r.rolname LIKE 'ff\\_scratch\\_%' AND ($1 = '' OR NOT starts_with(r.rolname, $1))) GROUP BY d.classoid ORDER BY 1")];
    public static readonly string[] Reads = [Info, OwnQueries, LogPolicy, DbState, DbOwnedUnmarked, DbList, DbExists, DbConns, RoleList, RoleMarker, RoleSessions, AdminRole, AdminSettings, AdminMembers, .. Snapshot.Select(x => x.sql)];
    // The catalogs the reads may name after FROM / JOIN (the self-test checks every read against this list).
    public static readonly string[] Catalogs = ["pg_database", "pg_roles", "pg_stat_activity", "pg_auth_members", "pg_shdescription", "pg_tablespace", "pg_db_role_setting"];
    static readonly (Regex re, string kind)[] Ddl = [
        (new($"^CREATE DATABASE \"({D})\" TEMPLATE template0 ENCODING 'UTF8'\\z"), "create-db"),
        (new($"^COMMENT ON DATABASE \"({D})\" IS 'floorfunds-scratch'\\z"), "mark-db"),
        (new($"^DROP DATABASE \"({D})\"\\z"), "drop-db"),
        (new($"^CREATE ROLE \"({R})\" (?:NOLOGIN|LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION(?: PASSWORD '{ScramVerifierRe}')?)\\z"), "create-role"),
        (new($"^COMMENT ON ROLE \"({R})\" IS 'floorfunds-scratch'\\z"), "mark-role"),
        (new($"^DROP ROLE \"({R})\"\\z"), "drop-role")];
    public static (string? kind, string? name) Classify(string sql)
    {
        if (Reads.Contains(sql, StringComparer.Ordinal)) return ("read", null);
        foreach (var (re, kind) in Ddl) if (re.Match(sql) is { Success: true } m) return (kind, m.Groups[1].Value);
        return (null, null);
    }
    // First keyword(s) only: refusal messages never echo literals (passwords, names).
    public static string Head(string sql) => string.Join(' ', sql.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries).Take(2)).ToUpperInvariant();
}

// The only handle on the maintenance connection [TLD 12:33 item 3]: no raw NpgsqlConnection / NpgsqlCommand is exposed.
sealed class MaintConn(NpgsqlConnection c) : IAsyncDisposable
{
    async Task Guard(string sql)
    {
        var (kind, name) = MaintSql.Classify(sql);
        if (kind is null) throw new InvalidOperationException($"refuse [maint-guard TLD 12:33]: statement not on the maintenance-connection allowlist ({MaintSql.Head(sql)} ...)");
        if (kind == "read") return;
        var db = kind.EndsWith("-role", StringComparison.Ordinal) ? Regex.Replace(name!, "_(admin_api|login)\\z", "") : name!;
        ScratchDb.GuardName(db);
        if (kind == "drop-db" && !(await StateAsync(name!)).ours)
            throw new InvalidOperationException($"refuse [maint-guard TLD 12:33]: DROP DATABASE {name} without the '{ScratchDb.Marker}' marker / not ours");
        if (kind == "drop-role" && await ScalarAsync(MaintSql.RoleMarker, name!) as string != ScratchDb.Marker)
            throw new InvalidOperationException($"refuse [maint-guard TLD 12:33]: DROP ROLE {name} without the '{ScratchDb.Marker}' marker");
    }
    // [TLD 13:15 KF-A (b)] self-test only: sees the exact text of every statement after the guard passed it, before it is sent.
    internal static Action<string>? Trace;
    NpgsqlCommand Cmd(string sql, object[] ps)
    {
        Trace?.Invoke(sql);
        var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps)
            cmd.Parameters.Add(p switch { string s => new NpgsqlParameter<string> { TypedValue = s }, bool b => new NpgsqlParameter<bool> { TypedValue = b }, _ => throw new ArgumentException("param type") });
        return cmd;
    }
    public async Task<object?> ScalarAsync(string sql, params object[] ps)
    {
        await Guard(sql);
        await using var cmd = Cmd(sql, ps);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }
    public async Task<List<string>> ColumnAsync(string sql, params object[] ps)
    {
        await Guard(sql);
        await using var cmd = Cmd(sql, ps);
        await using var r = await cmd.ExecuteReaderAsync();
        var l = new List<string>();
        while (await r.ReadAsync()) l.Add(r.GetString(0));
        return l;
    }
    public async Task<string[]> RowAsync(string sql)
    {
        await Guard(sql);
        await using var cmd = Cmd(sql, []);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return [];
        return Enumerable.Range(0, r.FieldCount).Select(i => r.GetValue(i)?.ToString() ?? "").ToArray();
    }
    public async Task ExecAsync(string sql)
    {
        await Guard(sql);
        await using var cmd = Cmd(sql, []);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task<(bool exists, bool ours)> StateAsync(string name) =>
        await ScalarAsync(MaintSql.DbState, name, ScratchDb.Marker) is bool b ? (true, b) : (false, false);
    public ValueTask DisposeAsync() => c.DisposeAsync();
}
