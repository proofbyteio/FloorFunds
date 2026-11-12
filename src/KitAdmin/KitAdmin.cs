using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Scratch-only superuser SQL for the cases with no product path: expires_at time travel,
/// request_binding tamper, and secret_ref only when no login-config API can write it.
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
        var allowed = CallSites.Select(s => s.Split('|')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var reason in calls)
        {
            if (!allowed.Contains(reason))
                failures.Add("kit-admin-call-not-on-the-list " + reason);
        }
        foreach (var reason in allowed)
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
