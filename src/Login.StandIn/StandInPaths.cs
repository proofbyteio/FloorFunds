namespace Login.StandIn;

// B2 / A71: fixture private keys and stand-in state stay outside the repo, under a platform temp root.
// Unix: allowed under /tmp/, default state /tmp/sse-d0 (unchanged).
// Windows: allowed under Path.GetTempPath(), default state %TEMP%\sse-d0.
static class StandInPaths
{
    static readonly StringComparison Cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Unix: /tmp ; Windows: %TEMP% (or equivalent).
    public static string AllowedTempRoot { get; } = OperatingSystem.IsWindows()
        ? Path.GetFullPath(Path.GetTempPath())
        : "/tmp";

    // Unix: /tmp/sse-d0 ; Windows: %TEMP%\sse-d0.
    public static string DefaultStateRoot { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(AllowedTempRoot, "sse-d0")
        : "/tmp/sse-d0";

    public static string NormalizeDir(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // True when path resolves strictly under AllowedTempRoot (not the root itself; same as StartsWith("/tmp/")).
    public static bool IsUnderAllowedTemp(string path)
    {
        var full = NormalizeDir(path);
        var root = NormalizeDir(AllowedTempRoot);
        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Cmp);
    }
}
