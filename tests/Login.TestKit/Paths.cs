namespace Cachd.Login.TestKit;

/// <summary>Locates code/ (the repo root) and sibling folders without hard-coding absolute paths.</summary>
public static class Paths
{
    public static string CodeRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CACHD_CODE_ROOT");
            if (!string.IsNullOrEmpty(env)) return env;
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null)
            {
                if (File.Exists(Path.Combine(d.FullName, "tests", "Cachd.Tests.slnx"))) return d.FullName;
                d = d.Parent;
            }
            throw new InvalidOperationException("Could not find code/ root (tests/Cachd.Tests.slnx). Set CACHD_CODE_ROOT.");
        }
    }
    public static string Contract => Path.Combine(CodeRoot, "contract");
    public static string Tools => Path.Combine(CodeRoot, "tools");
    /// <summary>UX mockups are a sibling of code/ (read-only for SSE2).</summary>
    public static string Mockups => Environment.GetEnvironmentVariable("CACHD_MOCKUPS_DIR")
        ?? Path.GetFullPath(Path.Combine(CodeRoot, "..", "login-hub-mockups"));

    /// <summary>Null when contract v1 is present; otherwise the skip reason.</summary>
    public static string? ContractSkipReason(string? file = null)
    {
        if (!Directory.Exists(Contract)) return "contract v1 draft not present (code/contract/ does not exist yet; SSE owns it).";
        if (file is not null && !File.Exists(Path.Combine(Contract, file))) return $"contract file code/contract/{file} not present yet.";
        return null;
    }
}
