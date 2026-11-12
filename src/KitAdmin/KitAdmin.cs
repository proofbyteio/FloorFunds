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
    ];

    public static void Exec(string reason, string sql)
    {
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException("kit admin refused: reason and sql are required");
        if (Environment.GetEnvironmentVariable(ModeVariable) != "1")
            throw new InvalidOperationException("kit admin refused: kit mode is off");
        var db = Environment.GetEnvironmentVariable(DatabaseVariable) ?? "";
        // scratch-guard
        if (!IsScratchDatabase(db))
            throw new InvalidOperationException("kit admin refused: database is not a scratch database");
        // end-scratch-guard
        if (!CallSites.Any(s => s.StartsWith(reason + "|", StringComparison.Ordinal)))
            throw new InvalidOperationException("kit admin refused: reason is not on the decisions list");
        Run(db, sql);
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
            foreach (Match m in Regex.Matches(text, @"KitAdmin\.Exec\(\s*""([^""]+)"""))
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
                failures.Add("kit-admin-call-not-on-the-list " + reason);
        }
        foreach (var reason in listed)
        {
            if (!calls.Contains(reason))
                failures.Add("kit-admin-list-without-call " + reason);
        }
        if (failures.Count == 0)
        {
            Console.WriteLine("PASS kit-admin-call-sites-match-the-decisions-list");
            Console.WriteLine("PASS no-test-code-updates-or-deletes-as-test-seed");
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

    static void Run(string db, string sql)
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
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("kit admin refused: psql did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("kit admin sql failed: " + FirstError(stderr + stdout));
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
