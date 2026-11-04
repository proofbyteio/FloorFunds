using System.Diagnostics;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>TOOL-xx / check 11: the release guards in code/tools/ work, proven on synthetic inputs (no dependency on SSE code).</summary>
[Trait("check", "11")]
public class ReleaseGuardTests
{
    static (int code, string output) Run(string script, string args, string? cwd = null, Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("bash", $"\"{Path.Combine(Paths.Tools, script)}\" {args}")
        { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd ?? Paths.CodeRoot };
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, o);
    }
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "sse2-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void TOOL01_check_no_standin_passes_on_clean_release_dir()
    {
        var d = Tmp(); File.WriteAllText(Path.Combine(d, "Login.Host.dll"), "x");
        var (c, o) = Run("check-no-standin.sh", $"\"{d}\"");
        Assert.True(c == 0, o);
    }

    [Theory]
    [InlineData("Login.StandIn.dll")]
    [InlineData("sub/Login.StandIn.Pages.dll")]
    [InlineData("login.standin.deps.json")]
    public void TOOL02_check_no_standin_fails_when_standin_file_present(string rel)
    {
        var d = Tmp(); var f = Path.Combine(d, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, "x");
        var (c, o) = Run("check-no-standin.sh", $"\"{d}\"");
        Assert.True(c != 0, "guard passed with " + rel + "\n" + o);
    }

    [Fact]
    public void TOOL03_check_no_standin_fails_when_standin_referenced_inside_a_file()
    {
        var d = Tmp(); File.WriteAllText(Path.Combine(d, "Login.Host.deps.json"), "{\"Login.StandIn/1.0.0\":{}}");
        var (c, o) = Run("check-no-standin.sh", $"\"{d}\"");
        Assert.True(c != 0, o);
    }

    [Fact]
    public void TOOL04_check_no_standin_fails_on_missing_dir_not_silently_passes()
    {
        var (c, o) = Run("check-no-standin.sh", "/nonexistent/sse2-release");
        Assert.True(c != 0, o);
    }

    // TLD-S2B-01 (TL 7:27 AM PT 2026-11-04): the only allowed remote is origin = FloorFunds (fetch and push). Throwaway /tmp repo;
    // nothing is ever pushed or fetched (the pre-push hook below refuses everything).
    const string FloorFunds = "https://github.com/proofbyteio/FloorFunds.git";
    static string GitRepo(bool withOrigin = true)
    {
        var d = Tmp();
        var args = new List<string> { "init -q", "-c user.name=t -c user.email=t@local.invalid commit -q --allow-empty -m init" };
        if (withOrigin) args.Add("remote add origin " + FloorFunds);
        foreach (var a in args)
            Process.Start(new ProcessStartInfo("git", a) { WorkingDirectory = d })!.WaitForExit();
        File.WriteAllText(Path.Combine(d, ".git", "hooks", "pre-push"), "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(d, ".git", "hooks", "pre-push"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return d;
    }

    // TOOL05 flipped for TLD-S2B-01 (was SI1 "passes on local-only repo"): exactly origin=FloorFunds PASSES, no remote FAILS.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TOOL05_check_no_remote_passes_only_on_floorfunds_origin(bool withOrigin, bool expectPass)
    {
        var (c, o) = Run("check-no-remote.sh", $"\"{GitRepo(withOrigin)}\"");
        Assert.True((c == 0) == expectPass, $"withOrigin={withOrigin} exit={c}\n{o}");
    }

    [Theory]
    [InlineData("remote set-url origin https://example.invalid/x.git")]
    [InlineData("config credential.helper store")]
    [InlineData("remote add other https://example.invalid/y.git")]
    public void TOOL06_check_no_remote_fails_on_other_remote_url_or_credential_helper(string gitArgs)
    {
        var d = GitRepo();
        Process.Start(new ProcessStartInfo("git", gitArgs) { WorkingDirectory = d })!.WaitForExit();
        var (c, o) = Run("check-no-remote.sh", $"\"{d}\"");
        Assert.True(c != 0, o);
    }

    [Fact]
    public void TOOL07_check_no_remote_fails_when_pre_push_hook_missing_or_permissive()
    {
        var d = GitRepo();
        File.WriteAllText(Path.Combine(d, ".git", "hooks", "pre-push"), "#!/bin/sh\nexit 0\n");
        var (c, o) = Run("check-no-remote.sh", $"\"{d}\"");
        Assert.True(c != 0, o);
        File.Delete(Path.Combine(d, ".git", "hooks", "pre-push"));
        (c, o) = Run("check-no-remote.sh", $"\"{d}\"");
        Assert.True(c != 0, o);
    }

    [Fact]
    public void TOOL08_this_repo_passes_check_no_remote()
    {
        var (c, o) = Run("check-no-remote.sh", $"\"{Paths.CodeRoot}\"");
        Assert.True(c == 0, o);
    }

    // Plan §3.5: the stand-in refuses to start unless Development + STANDIN_ALLOW=1 (and loopback, not cloud).
    [Theory]
    [InlineData("Production", "1")]
    [InlineData("Development", "")]
    [InlineData("Staging", "1")]
    public void SEC40_standin_refuses_to_start_without_dev_and_allow(string envName, string allow)
    {
        var proj = Path.Combine(Paths.CodeRoot, "src", "Login.StandIn");
        if (!Directory.Exists(proj)) Assert.Skip("code/src/Login.StandIn/ not present yet (SSE builds it in S1).");
        Assert.Skip("PENDING OQ-SSE2-17: start command, expected exit code and log line for a refused start are not specified.");
        _ = (envName, allow);
    }
}
