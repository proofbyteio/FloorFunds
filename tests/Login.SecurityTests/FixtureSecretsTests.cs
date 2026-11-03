using System.Diagnostics;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// SSE2 (Mon Nov 2 2026, 9:45 PM PT; rev 10:10 PM PT for TL 9:50 PM fixture-secrets ruling) [TL Decision 9:26 PM PT (d); TL 9:32 PM PT; SSE B2 (TL 9:16 PM)]:
/// wires SSE's src/Login.StandIn/scripts/check-fixture-secrets.sh ("fails if a private key is tracked in git") into the suite
/// via tools/check-fixture-secrets.sh. Negative controls run the same script in a throwaway git repo under /tmp; the markers
/// written there are fake test-only strings, not keys.
/// </summary>
[Trait("check", "fixture-secrets")]
public class FixtureSecretsTests(ITestOutputHelper o)
{
    static string Code()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "tools", "check-fixture-secrets.sh"))) return d.FullName;
        throw new InvalidOperationException("tools/check-fixture-secrets.sh not found above " + AppContext.BaseDirectory);
    }
    static (int rc, string output) Run(string file, string args, string cwd, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var kv in env ?? new Dictionary<string, string>()) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); p.WaitForExit();
        return (p.ExitCode, s);
    }
    static string Script(string code)
    {
        var (rc, s) = Run("git", "show feat/si1-standin:src/Login.StandIn/scripts/check-fixture-secrets.sh", code);
        if (rc != 0) Assert.Skip("PENDING OQ-SSE2-53: check-fixture-secrets.sh not on feat/si1-standin");
        var f = Path.Combine(Path.GetTempPath(), "sse2-fsec-" + Guid.NewGuid().ToString("N") + ".sh"); File.WriteAllText(f, s); return f;
    }

    [Fact] // every branch under test has no tracked private key
    public void FS01_no_private_key_tracked_on_any_branch()
    {
        var (rc, s) = Run("bash", "tools/check-fixture-secrets.sh", Code());
        o.WriteLine(s);
        if (rc == 2) Assert.Skip("PENDING OQ-SSE2-53: " + s.Trim());
        Assert.Equal(0, rc);
    }

    // Fake markers are assembled at run time so this source file never holds a PEM header itself (21449f9 did: OQ-SSE2-59).
    static string Fake(string c) => c.Replace("PEM(", "-----BEG" + "IN ").Replace(")PEM", "-----\nSSE2-FAKE-TEST-ONLY\n")
        .Replace("RC(", "FIXTURE" + "-RC-").Replace(")RC", "");
    static readonly Dictionary<string, string> Git = new() { ["GIT_AUTHOR_NAME"] = "t", ["GIT_AUTHOR_EMAIL"] = "t@local.invalid", ["GIT_COMMITTER_NAME"] = "t", ["GIT_COMMITTER_EMAIL"] = "t@local.invalid" };

    // negative controls (index): SSE's script must fail on each of these, else FS01 proves nothing. TL 9:50 PM: OKP and
    // fixture secrets / recovery codes are now required FAILs (were FS03 probes at 8aab98a).
    [Theory]
    [InlineData("client.pem", "not a key")]
    [InlineData("conf/sign.key", "not a key")]
    [InlineData("notes.txt", "PEM(PRIVATE KEY)PEM")]
    [InlineData("rsa.txt", "PEM(RSA PRIVATE KEY)PEM")]
    [InlineData("enc.txt", "PEM(ENCRYPTED PRIVATE KEY)PEM")]
    [InlineData("jwk.json", "{\"kty\":\"RSA\",\"n\":\"AQAB\",\"e\":\"AQAB\",\"d\":\"SSE2FAKE\"}")]
    [InlineData("ec.json", "{\"kty\": \"EC\", \"crv\": \"P-256\", \"d\": \"SSE2FAKE\"}")]
    [InlineData("okp.json", "{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"d\":\"SSE2FAKE\"}")]
    [InlineData("src/fixtures.json", "{\"clients\":[{\"client_id\":\"x\",\"secret\":\"SSE2FAKE-secret\"}]}")]
    [InlineData("src/fixtures.json", "{\"users\":[{\"username\":\"a\",\"recovery_codes\":[\"SSE2FAKE\"]}]}")]
    [InlineData("notes.md", "RC(5e2fa4e0)RC")]
    public void FS02_script_fails_when_a_private_key_or_fixture_secret_is_tracked(string path, string content)
    {
        var code = Code(); var s = Script(code);
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-repo-").FullName;
        try
        {
            Run("git", "init -q", repo);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path))!);
            File.WriteAllText(Path.Combine(repo, path), Fake(content));
            File.WriteAllText(Path.Combine(repo, "ok.txt"), "clean");
            Run("git", "add -A", repo);
            var (rc, outp) = Run("bash", s, repo);
            o.WriteLine($"{path}: rc={rc} {outp.Trim()}");
            Assert.Equal(1, rc);
            // positive control in the same repo: remove it -> clean
            Run("git", $"rm -q --cached -- {path}", repo);
            Assert.Equal(0, Run("bash", s, repo).rc);
        }
        finally { Directory.Delete(repo, true); File.Delete(s); }
    }

    // negative controls (history): a key committed then deleted must FAIL (SSE F14 history scan; was "NOT detected" at 8aab98a).
    [Theory]
    [InlineData("k.txt", "PEM(PRIVATE KEY)PEM")]
    [InlineData("okp.json", "{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"d\":\"SSE2FAKE\"}")]
    [InlineData("old.key", "not a key")]
    public void FS03_script_fails_on_a_key_deleted_in_history(string path, string content)
    {
        var code = Code(); var s = Script(code);
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-repo-").FullName;
        try
        {
            Run("git", "init -q", repo);
            File.WriteAllText(Path.Combine(repo, path), Fake(content));
            Run("git", "add -A", repo); Run("git", "commit -qm a", repo, Git);
            Run("git", $"rm -q {path}", repo); File.WriteAllText(Path.Combine(repo, "ok.txt"), "clean");
            Run("git", "add -A", repo); Run("git", "commit -qm b", repo, Git);
            var (rc, outp) = Run("bash", $"{s} \"\" HEAD", repo);
            o.WriteLine($"FS03 {path}: rc={rc} {outp.Trim()}");
            Assert.Equal(1, rc);
            Assert.Contains("FAIL history", outp);
        }
        finally { Directory.Delete(repo, true); File.Delete(s); }
    }

    // TL 9:50 PM PT: secrets found ONLY in commits eb0fa5d..c4da996 may WARN; any secret in any other commit must FAIL.
    // Throwaway repo under /tmp gets SSE's real history by a local fetch (no remote configured); outside-range commits are made
    // on top of it with fake values. Run through tools/check-fixture-secrets.sh with SSE's script at 10a1ca4 and at the branch tip.
    public static TheoryData<string, string> RangeCases() => new()
    {
        { "feat/si1-standin", "in-range" }, { "feat/si1-standin", "new-secret" }, { "feat/si1-standin", "reused-in-range-blob" }, { "feat/si1-standin", "rc-literal" },
        { "10a1ca4", "in-range" }, { "10a1ca4", "new-secret" }, { "10a1ca4", "reused-in-range-blob" }, { "10a1ca4", "rc-literal" },
        { "96a629a", "in-range" }, { "96a629a", "new-secret" }, { "96a629a", "reused-in-range-blob" }, { "96a629a", "rc-literal" },
    };
    [Theory, MemberData(nameof(RangeCases))]
    public void FS05_warn_only_inside_eb0fa5d_c4da996_fail_outside(string scriptRef, string kase)
    {
        var code = Code();
        if (Run("git", "rev-parse -q --verify c4da996^{commit}", code).rc != 0) Assert.Skip("PENDING: c4da996 not in this repo");
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-range-").FullName;
        try
        {
            Run("git", "init -q", repo);
            Run("git", $"fetch -q {code} feat/si1-standin:refs/heads/base", repo);
            var sref = scriptRef == "feat/si1-standin" ? "base" : (Run("git", $"rev-parse {scriptRef}", code).output.Trim());
            Run("git", "checkout -q base", repo);
            var fx = Path.Combine(repo, "src", "Login.StandIn", "fixtures.json");
            var clean = File.ReadAllText(fx);
            string? added = null;
            if (kase != "in-range")
            {
                if (kase == "new-secret") File.WriteAllText(fx, clean.Replace("\"secret\": \"GENERATED\"", "\"secret\": \"SSE2FAKE-out-of-range\""));
                else if (kase == "reused-in-range-blob") File.WriteAllText(fx, Run("git", "show c4da996:src/Login.StandIn/fixtures.json", repo).output);
                else File.WriteAllText(Path.Combine(repo, "notes.md"), Fake("RC(5e2fa4e0)RC"));
                Run("git", "add -A", repo); Run("git", "commit -qm sse2-fake-out-of-range", repo, Git);
                added = Run("git", "rev-parse --short=7 HEAD", repo).output.Trim();
                File.WriteAllText(fx, clean); File.Delete(Path.Combine(repo, "notes.md"));
                Run("git", "add -A", repo); Run("git", "commit -qm sse2-restore", repo, Git);
            }
            var (rc, outp) = Run("bash", $"{code}/tools/check-fixture-secrets.sh {sref} HEAD", code, new Dictionary<string, string> { ["FSEC_REPO"] = repo });
            o.WriteLine($"FS05 script@{scriptRef} {kase}: rc={rc}\n{outp.Trim()}");
            Assert.Contains("WARN", outp);                                     // the real eb0fa5d..c4da996 history always warns
            if (added is null) { Assert.Equal(0, rc); Assert.DoesNotContain("FAIL", outp.Replace("FP   FAIL", "")); }
            else { Assert.Equal(1, rc); Assert.Contains(added, outp); }
        }
        finally { Directory.Delete(repo, true); }
    }

    // Probe (finding SSE2-F-FS1, minor): SSE's script skips its own path in the index and in history, so a fixture secret committed
    // INTO src/Login.StandIn/scripts/check-fixture-secrets.sh outside eb0fa5d..c4da996 is never reported. Logged, never fails the build.
    [Fact]
    public void FS06_probe_secret_hidden_in_the_script_path()
    {
        var code = Code();
        if (Run("git", "rev-parse -q --verify c4da996^{commit}", code).rc != 0) Assert.Skip("PENDING: c4da996 not in this repo");
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-self-").FullName;
        try
        {
            Run("git", "init -q", repo);
            Run("git", $"fetch -q {code} feat/si1-standin:refs/heads/base", repo);
            Run("git", "checkout -q base", repo);
            var sp = Path.Combine(repo, "src", "Login.StandIn", "scripts", "check-fixture-secrets.sh");
            File.AppendAllText(sp, "\n# " + Fake("RC(5e2fa4e0)RC") + " {\"secret\": \"SSE2FAKE-hidden\"}\n");
            Run("git", "add -A", repo); Run("git", "commit -qm sse2-fake-hidden-in-script", repo, Git);
            var (rc, outp) = Run("bash", $"{code}/tools/check-fixture-secrets.sh base HEAD", code, new Dictionary<string, string> { ["FSEC_REPO"] = repo });
            var missed = rc == 0 && !outp.Contains("sse2-fake") && !outp.Contains(Run("git", "rev-parse --short=7 HEAD", repo).output.Trim());
            o.WriteLine($"FS06 secret inside the script path: rc={rc} missed={missed} (finding SSE2-F-FS1 when missed)\n{outp.Trim()}");
        }
        finally { Directory.Delete(repo, true); }
    }

    [Fact] // B2: FIXTURE client keys live only under /tmp, mode 0600
    public void FS04_fixture_client_keys_live_under_tmp_mode_0600()
    {
        var dir = Environment.GetEnvironmentVariable("LOGIN_STANDIN_CLIENT_KEY_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Skip("PENDING OQ-SSE2-53: no LOGIN_STANDIN_CLIENT_KEY_DIR (stand-in without private_key_jwt)");
        Assert.StartsWith("/tmp/", Path.GetFullPath(dir) + "/");
        var keys = Directory.GetFiles(dir, "*.FIXTURE.key.pem");
        Assert.NotEmpty(keys);
        foreach (var k in keys)
        {
            var m = File.GetUnixFileMode(k);
            o.WriteLine($"{Path.GetFileName(k)} {m}");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, m);
            Assert.False(k.StartsWith("/workspace/"));
        }
    }
}
