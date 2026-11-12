using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Scratch-only superuser SQL for setup that has no product path. The assertion after
/// the statement still runs as admin_api, the issuer, or begin_request.
/// Refuses unless kit mode is on and the database name has a scratch prefix.
/// </summary>
public static class KitAdmin
{
    public const string ModeVariable = "FF_KIT_MODE";
    public const string DatabaseVariable = "FF_KIT_DB";
    public const string PortVariable = "FF_KIT_PORT";

    /// <summary>reason|suite|check|table|column|why there is no product path. The sweep and DECISIONS use this list.</summary>
    public static readonly string[] CallSites =
    [
        "expired-session-rejected-by-begin-request|Req064Suite|expired-session-rejected-by-begin-request|request_session|expires_at|no product clock; begin_request only reads expires_at",
        "expired-session-is-the-unified-401|Req064Suite|expired-session-is-the-unified-401|request_session|expires_at|no product clock; the issuer will not open an already-expired token",
        "closed-sessions-are-byte-identical|Req064Suite|closed-sessions-are-byte-identical|request_session|expires_at|no product clock; expiry is compared with revoke and logout",
        "session-still-open-closes-when-expired|Req064Suite|session-still-open-closes-when-expired|request_session|expires_at|no product clock; session_still_open is read as the issuer",
        "bounds-retry-closed-session-is-401|Req064Suite|bounds-retry-closed-session-is-401|request_session|expires_at|no product clock; the row must expire while the request is in flight",
        "catalog-closed-sessions-are-one-exception|Catalog.Read.Checks|catalog-closed-sessions-are-one-exception|request_session|expires_at|no product clock; the reader is then called as the catalog role",
        "T6-revoke-takes-effect-on-next-request|Req064Suite|T6 revoke-takes-effect-on-next-request|admin_assignment|revoked_at|no assignment revoke API; the next HTTP request is the assertion",
        "revoked-assignment-denies-a-new-session|Req064Suite|revoked-assignment-denies-a-new-session|admin_assignment|revoked_at|no assignment revoke API; a newly minted session is the assertion",
        "revocation-applies-on-the-next-action|Req064Suite|revocation-applies-on-the-next-action|admin_assignment|status|no assignment change API; begin_request as admin_api is the assertion",
        "last-active-sa-revoke|Req064Suite|last-active-sa-revoke|admin_assignment|revoked_at|trigger check; no API writes this column",
        "last-active-sa-assignment-disable|Req064Suite|last-active-sa-assignment-disable|admin_assignment|status|trigger check; no API writes this column",
        "last-active-sa-disable|Req064Suite|last-active-sa-disable|app_user|status|trigger check; no API writes this column",
        "last-active-sa-lock|Req064Suite|last-active-sa-lock|app_user|locked_until|trigger check; no API writes this column",
        "last-active-sa-scope-change|Req064Suite|last-active-sa-scope-change|admin_assignment|scope_kind|trigger check; no API writes this column",
        "last-active-sa-role-change|Req064Suite|last-active-sa-role-change|admin_assignment|role_id|trigger check; no API writes this column",
        "last-active-sa-delete|Req064Suite|last-active-sa-delete|admin_assignment|DELETE|trigger check; no API deletes this row",
        "last-active-sa-user-delete|Req064Suite|last-active-sa-user-delete|app_user|DELETE|trigger check; no API deletes this row",
        "last-active-sa-truncate|Req064Suite|last-active-sa-truncate|admin_assignment|TRUNCATE|trigger check; the migrator truncate is a separate 42501",
        "last-active-sa-user-truncate|Req064Suite|last-active-sa-user-truncate|app_user|TRUNCATE|trigger check; the migrator truncate is a separate 42501",
        "disable-one-of-two-super-admins-succeeds|Req064Suite|disable-one-of-two-super-admins-succeeds|admin_assignment|status|no API; the trigger must allow disabling one of two",
        "delete-partner-admin-while-sa-remains|Req064Suite|delete-partner-admin-while-sa-remains|admin_assignment|DELETE|no API; the trigger must allow deleting a partner admin",
        "last-active-sa-concurrency|Req064Suite|last-active-sa-concurrency|admin_assignment|revoked_at|no API; two sessions must overlap on the advisory lock",
        "login-config-plant-secret-ref|Admin.Api.Checks|put: entra -> entra keeps planted secret_ref|location_login_config|secret_ref|client secret_ref is field_not_writable; the plant is not the deployment value",
        "login-config-restore-secret-ref|Admin.Api.Checks|put: entra -> entra keeps planted secret_ref|location_login_config|secret_ref|restore the deployment value after the entra-to-entra assertion",
        "ttl-over-15-minutes-rejected|Req064Suite|ttl-over-15-minutes-rejected|request_session|expires_at|open_session caps at 15 minutes; the check constraint is the behaviour under test",
        "session-rate-setup|Req064Suite|six-hundred-first-open-rejected|request_session|created_at|historical revoked rows; the assertion is open_session as the issuer",
        "req064-fee-fixture|Req064Suite|T12 flag-does-not-grant-and-principal-does|registered_entry|INSERT|admin_api has no EXECUTE on apply_entry_create; the insert runs with a live issuer session",
        "req064-history-label-fixture|Req064Suite|history-label-uses-the-record-partner|registered_entry|INSERT|admin_api has no EXECUTE on apply_entry_create; the stored label is read as admin_api",
        "catalog-read-fee-fixture|Catalog.Read.Checks|partner-a-fee-definition|registered_entry|INSERT|admin_api has no EXECUTE on apply_entry_create; the reader assertions run as admin_api",
        "catalog-query-fixture|Catalog.Query.Checks|query-sa-fee-list-includes-private|registered_entry|INSERT|admin_api has no EXECUTE on apply_entry_create; the query assertions run as admin_api",
        "auth-data-constraint-probe|Auth.Data.Checks|constraint probes|auth|INSERT|the migrator has no DML; the probe asserts the constraint SQLSTATE",
        "auth-data-temp-location|Auth.Data.Checks|reader config location without a config row|location|DELETE|the migrator has no DELETE; the row is removed after the reader assertion",
        "auth-data-cleanup|Auth.Data.Checks|K3 cleanup returns the post-seed state|auth|DELETE|the migrator has no DELETE; the checks remove the rows they created",
        "reader-location-grant|Admin.Api.Checks|org viewer location assignment|admin_assignment|INSERT|no assignment API; row security reads the location assignment",
        "reader-location-revoke|Admin.Api.Checks|org viewer location assignment|admin_assignment|DELETE|no assignment API; the assignment is removed after the reader assertion",
        "admin-checks-cleanup|Admin.Api.Checks|cleanup|auth|DELETE|the migrator has no DELETE; the checks remove the rows they created",
        "prb-parallel-row-lock|Admin.Api.Checks|Slice A parallel update|partner_reason_bounds|FOR UPDATE|the migrator has no UPDATE; the lock is held until both API requests are waiting",
        "req064-fixture-delete|Req064Suite|fixture cleanup|auth|DELETE|the migrator has no DELETE; the checks remove the rows they created",
        "req064-test-trigger|Req064Suite|bounds-retry-closed-session-is-401|partner_reason_bounds|TRIGGER|the migrator does not own the table; the trigger is the test double",
        "req064-user-lock|Req064Suite|open-blocked-behind-a-lock|app_user|locked_until|the migrator has no UPDATE; the check locks the user while open_session waits",
    ];

    public static void Exec(string reason, string sql)
    {
        var db = Ready(reason, sql);
        // scratch-guard
        if (!IsScratchDatabase(db))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        // end-scratch-guard
        var (code, stdout, stderr) = Run(db, sql);
        if (code != 0)
            throw new InvalidOperationException("kit admin sql failed: " + FirstError(stderr + stdout));
    }

    /// <summary>Runs the statement in a transaction that is rolled back. Returns ok, or the SQLSTATE.</summary>
    public static string SqlState(string reason, string sql)
    {
        var db = Ready(reason, sql);
        if (!IsScratchDatabase(db))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        var (code, stdout, stderr) = Run(db, "BEGIN; " + sql + "; ROLLBACK;");
        if (code == 0)
            return "ok";
        var match = Regex.Match(stderr + stdout, @"ERROR:\s+([0-9A-Z]{5}):");
        return match.Success ? match.Groups[1].Value : "error";
    }

    /// <summary>
    /// Starts BEGIN; sql; COMMIT on the scratch superuser and returns before it finishes.
    /// The caller waits until its lock is visible, then Finish waits for the commit.
    /// </summary>
    public static ScratchJob Begin(string reason, string sql)
    {
        var db = Ready(reason, sql);
        if (!IsScratchDatabase(db))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        var statement = sql.Trim();
        if (!statement.EndsWith(';'))
            statement += ";";
        var process = Start(db, "BEGIN; " + statement + " COMMIT;", null);
        return new ScratchJob(process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
    }

    public sealed class ScratchJob : IDisposable
    {
        readonly Process _process;
        readonly Task<string> _stdout;
        readonly Task<string> _stderr;
        bool _finished;

        internal ScratchJob(Process process, Task<string> stdout, Task<string> stderr)
        {
            _process = process;
            _stdout = stdout;
            _stderr = stderr;
        }

        public async Task<string> Finish()
        {
            if (_finished)
                return "ok";
            var timeout = Task.Delay(20000);
            if (await Task.WhenAny(_process.WaitForExitAsync(), timeout) == timeout)
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                _finished = true;
                return "timeout";
            }
            var text = await _stderr + await _stdout;
            _finished = true;
            return _process.ExitCode == 0 ? "ok" : "kit admin sql failed: " + FirstError(text);
        }

        public void Dispose()
        {
            if (_finished)
                return;
            _finished = true;
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Holds one transaction open until disposed. Disposal cancels the backend, which rolls the transaction back.</summary>
    public static IDisposable Hold(string reason, string sql)
    {
        var db = Ready(reason, sql);
        if (!IsScratchDatabase(db))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        var marker = "kit-hold " + reason;
        var process = Start(db, "BEGIN; " + sql + "; /* " + marker + " */ SELECT pg_sleep(300); ROLLBACK;", null);
        var ready = false;
        var last = "";
        for (var i = 0; i < 50 && !ready; i++)
        {
            if (process.HasExited)
                throw new InvalidOperationException("kit admin hold ended: " + FirstError(process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd()));
            var probe = "BEGIN; " + sql.Replace(" FOR UPDATE", " FOR UPDATE NOWAIT", StringComparison.Ordinal) + "; ROLLBACK;";
            var (code, stdout, stderr) = Run(db, probe);
            last = stderr + stdout;
            if (last.Contains("55P03", StringComparison.Ordinal))
                ready = true;
            else if (code != 0)
                break;
            else
                Thread.Sleep(100);
        }
        if (!ready)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("kit admin hold did not take the lock: " + FirstError(last));
        }
        return new HeldBackend(process, db, marker);
    }

    static string Ready(string reason, string sql)
    {
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("kit admin refused: reason and sql are required");
        if (sql.Contains("session_replication_role", StringComparison.Ordinal)
            || sql.Contains("DISABLE TRIGGER", StringComparison.Ordinal)
            || sql.Contains("ENABLE TRIGGER", StringComparison.Ordinal))
            throw new InvalidOperationException("kit admin refused: catalog write bypasses triggers");
        if (Environment.GetEnvironmentVariable(ModeVariable) != "1")
            throw new InvalidOperationException("kit admin refused: kit mode is off");
        var db = Environment.GetEnvironmentVariable(DatabaseVariable) ?? "";
        if (!CallSites.Any(s => s.StartsWith(reason + "|", StringComparison.Ordinal)))
            throw new InvalidOperationException("kit admin refused: reason is not on the decisions list");
        return db;
    }

    public static bool IsScratchDatabase(string db) =>
        (db.StartsWith("ff_kit_", StringComparison.Ordinal) && db.Length > "ff_kit_".Length)
        || (db.StartsWith("ff_scratch_", StringComparison.Ordinal) && db.Length > "ff_scratch_".Length);

    /// <summary>Runs db/kit/test-seed-role.sql as the bootstrap role. Returns the password written to the env file.</summary>
    public static async Task<string> InstallTestSeed(string host, int port, string bootstrapUser, string database, string scriptPath)
    {
        if (!IsScratchDatabase(database))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        Directory.CreateDirectory("/tmp/ff-db");
        var envPath = "/tmp/ff-db/" + database + "_test_seed.env";
        await File.WriteAllTextAsync(envPath, "TEST_SEED_PASSWORD=" + password + "\n");
        using (var chmod = Process.Start(new ProcessStartInfo("chmod", ["600", envPath]) { UseShellExecute = false }))
        {
            chmod?.WaitForExit();
        }
        var script = await File.ReadAllTextAsync(scriptPath);
        var psi = new ProcessStartInfo
        {
            FileName = "psql",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-h");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-U");
        psi.ArgumentList.Add(bootstrapUser);
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(database);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("ON_ERROR_STOP=1");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("test seed role script did not start");
        await process.StandardInput.WriteAsync("\\set test_seed_password '" + password + "'\n");
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("test seed role script failed: " + FirstError(stderr + stdout));
        return password;
    }

    /// <summary>
    /// One transaction: the issuer role opens a session, then begin_request, correlation_id,
    /// and reason, then the catalog writes. Triggers stay enabled.
    /// </summary>
    public static string SessionThen(string issuerRole, string iss, string userId, string correlationId, string writeReason, string writes)
    {
        if (!Regex.IsMatch(issuerRole, "^[a-z0-9_]+\\z"))
            throw new InvalidOperationException("kit admin refused: issuer role");
        if (writes.Contains("$fix$", StringComparison.Ordinal) || writes.Contains("session_replication_role", StringComparison.Ordinal)
            || writes.Contains("DISABLE TRIGGER", StringComparison.Ordinal) || writes.Contains("ENABLE TRIGGER", StringComparison.Ordinal))
            throw new InvalidOperationException("kit admin refused: catalog write bypasses triggers");
        return "DO $fix$\nDECLARE tok text;\nBEGIN\n"
            + "EXECUTE 'SET LOCAL ROLE " + issuerRole + "';\n"
            + "SELECT auth.open_session(" + Literal(iss) + ", " + Literal(userId) + ", gen_random_uuid(), clock_timestamp() + interval '5 minutes', clock_timestamp()) INTO tok;\n"
            + "RESET ROLE;\n"
            + "PERFORM auth.begin_request(tok);\n"
            + "PERFORM set_config('app.correlation_id', " + Literal(correlationId) + ", true);\n"
            + "PERFORM set_config('app.reason', " + Literal(writeReason) + ", true);\n"
            + writes + "\nEND\n$fix$;";
    }

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static void Arm(string database, int port)
    {
        Environment.SetEnvironmentVariable(ModeVariable, IsScratchDatabase(database) ? "1" : null);
        Environment.SetEnvironmentVariable(DatabaseVariable, database);
        Environment.SetEnvironmentVariable(PortVariable, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static int Check(string repo)
    {
        var failures = new List<string>();
        var calls = new List<string>();
        foreach (var file in Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            if (file.EndsWith($"{Path.DirectorySeparatorChar}KitAdmin.cs", StringComparison.Ordinal))
                continue;
            foreach (Match m in Regex.Matches(text, @"(?:KitAdmin\.Exec|KitAdmin\.SqlState|KitAdmin\.Hold|KitAdmin\.Begin|KitFailsLast|KitRaisesLast|Kit)\(\s*""([^""]+)"""))
                calls.Add(m.Groups[1].Value);
            if (Regex.IsMatch(text, @"Exec\(\s*seed\b[^;]{0,500}?(UPDATE |DELETE )", RegexOptions.Singleline))
                failures.Add("test-seed-update-or-delete " + Path.GetRelativePath(repo, file));
        }
        var listed = DecisionsReasons();
        var coded = CallSites.Select(s => s.Split('|')[0]).ToHashSet(StringComparer.Ordinal);
        if (!listed.SetEquals(coded))
            failures.Add("kit-admin-list-does-not-match-decisions");
        foreach (var reason in calls)
        {
            if (!listed.Contains(reason))
                failures.Add("kit-admin-call-not-on-the-list " + reason + " kitadmin-unlisted-callsite");
        }
        foreach (var reason in listed)
        {
            if (!calls.Contains(reason))
                failures.Add("kit-admin-list-without-call " + reason);
        }
        var actorBody = File.ReadAllText(Path.Combine(repo, "src", "Auth.Data", "Migrations", "Req064Actor.sql"))
            + File.ReadAllText(Path.Combine(repo, "src", "Auth.Data", "Migrations", "Req064Session.sql"));
        if (actorBody.Contains("_test_seed", StringComparison.Ordinal) || actorBody.Contains("kit_actor_bypass", StringComparison.Ordinal))
            failures.Add("actor-test-bypass");
        var seedRole = File.ReadAllText(Path.Combine(repo, "db", "kit", "test-seed-role.sql"));
        if (seedRole.Contains("GRANT SELECT, INSERT ON TABLE catalog.", StringComparison.Ordinal)
            || seedRole.Contains("GRANT INSERT ON TABLE catalog.", StringComparison.Ordinal)
            || seedRole.Contains("GRANT INSERT ON catalog.registered_entry", StringComparison.Ordinal))
            failures.Add("test-seed-insert-registered-entry");
        foreach (var sqlFile in Directory.EnumerateFiles(repo, "*.sql", SearchOption.AllDirectories))
        {
            if (sqlFile.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            var sql = File.ReadAllText(sqlFile);
            if (sql.Contains("GRANT EXECUTE ON FUNCTION catalog.apply_entry_create", StringComparison.Ordinal)
                || sql.Contains("GRANT EXECUTE ON FUNCTION catalog.apply_claim_edit", StringComparison.Ordinal))
                failures.Add("admin-or-seed-execute-apply " + Path.GetRelativePath(repo, sqlFile));
        }
        foreach (var file in Directory.EnumerateFiles(repo, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            if (!file.EndsWith(".cs", StringComparison.Ordinal) && !file.EndsWith(".sql", StringComparison.Ordinal))
                continue;
            if (file.EndsWith($"{Path.DirectorySeparatorChar}KitAdmin.cs", StringComparison.Ordinal))
                continue;
            var body = File.ReadAllText(file);
            if (body.Contains("session_replication_role", StringComparison.Ordinal)
                || body.Contains("DISABLE TRIGGER", StringComparison.Ordinal)
                || body.Contains("ENABLE TRIGGER", StringComparison.Ordinal))
                failures.Add("kitadmin-replication-role " + Path.GetRelativePath(repo, file));
        }
        if (failures.Count == 0)
        {
            Console.WriteLine("PASS kit-admin-call-sites-match-the-decisions-list");
            Console.WriteLine("PASS no-test-code-updates-or-deletes-as-test-seed");
            Console.WriteLine("PASS actor-body-has-no-test-bypass");
            Console.WriteLine("PASS test-seed-has-no-catalog-insert");
            Console.WriteLine("PASS no-apply-execute-grant");
            Console.WriteLine("PASS no-replication-role-or-disabled-trigger");
            return 0;
        }
        foreach (var f in failures)
            Console.WriteLine("FAIL " + f);
        return 1;
    }

    static HashSet<string> DecisionsReasons()
    {
        var path = File.Exists("/opt/cursor/artifacts/DECISIONS.md")
            ? "/opt/cursor/artifacts/DECISIONS.md"
            : "/cursor/stores/self/artifacts/DECISIONS.md";
        if (!File.Exists(path))
            throw new InvalidOperationException("kit admin sweep: DECISIONS.md is missing");
        var text = File.ReadAllText(path);
        var start = text.IndexOf("## KitAdmin call sites", StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException("kit admin sweep: DECISIONS has no KitAdmin call sites");
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text[start..].Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal) && !raw.StartsWith("## KitAdmin call sites", StringComparison.Ordinal))
                break;
            if (!raw.StartsWith("| `", StringComparison.Ordinal))
                continue;
            var reason = raw.Split('|')[1].Trim().Trim('`');
            if (reason.Length > 0 && reason != "Reason")
                reasons.Add(reason);
        }
        return reasons;
    }

    static (int code, string stdout, string stderr) Run(string db, string sql) => Run(db, sql, null);

    static (int code, string stdout, string stderr) Run(string db, string sql, string? applicationName)
    {
        using var process = Start(db, sql, applicationName);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    static Process Start(string db, string sql, string? applicationName)
    {
        var port = Environment.GetEnvironmentVariable(PortVariable);
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("postgres");
        psi.ArgumentList.Add("psql");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("ON_ERROR_STOP=1");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("VERBOSITY=verbose");
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(db);
        if (!string.IsNullOrEmpty(port))
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(port);
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(sql);
        if (!string.IsNullOrEmpty(applicationName))
            psi.Environment["PGAPPNAME"] = applicationName;
        return Process.Start(psi) ?? throw new InvalidOperationException("kit admin refused: psql did not start");
    }

    sealed class HeldBackend : IDisposable
    {
        readonly Process _process;
        readonly string _database;
        readonly string _application;
        bool _released;

        public HeldBackend(Process process, string database, string application)
        {
            _process = process;
            _database = database;
            _application = application;
        }

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            var (code, stdout, stderr) = Run(_database, "SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE query LIKE " + Literal("%" + _application + "%") + " AND pid <> pg_backend_pid()");
            if (!_process.WaitForExit(15000))
            {
                try { _process.Kill(); } catch (InvalidOperationException) { }
                _process.WaitForExit();
            }
            if (code != 0)
                throw new InvalidOperationException("kit admin hold release failed: " + FirstError(stderr + stdout));
        }
    }

    static string FirstError(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("ERROR:", StringComparison.Ordinal) || t.StartsWith("FATAL:", StringComparison.Ordinal))
                return t.Length > 240 ? t[..240] : t;
        }
        var flat = text.Replace('\n', ' ').Trim();
        return flat.Length > 240 ? flat[..240] : flat;
    }
}
