using System.Diagnostics;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// SSE2 (Mon Nov 2 2026, 9:45 PM PT) [TL Decision 9:26 PM PT (d); TL 9:32 PM PT; SSE B2 (TL 9:16 PM)]:
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

    // negative controls: the script must actually fail on each of these (otherwise FS01 proves nothing)
    [Theory]
    [InlineData("client.pem", "not a key")]
    [InlineData("conf/sign.key", "not a key")]
    [InlineData("notes.txt", "-----BEGIN PRIVATE KEY-----\nSSE2-FAKE-TEST-ONLY\n-----END PRIVATE KEY-----\n")]
    [InlineData("rsa.txt", "-----BEGIN RSA PRIVATE KEY-----\nSSE2-FAKE-TEST-ONLY\n")]
    [InlineData("enc.txt", "-----BEGIN ENCRYPTED PRIVATE KEY-----\nSSE2-FAKE-TEST-ONLY\n")]
    [InlineData("jwk.json", "{\"kty\":\"RSA\",\"n\":\"AQAB\",\"e\":\"AQAB\",\"d\":\"SSE2FAKE\"}")]
    [InlineData("ec.json", "{\"kty\": \"EC\", \"crv\": \"P-256\", \"d\": \"SSE2FAKE\"}")]
    public void FS02_script_fails_when_a_private_key_is_tracked(string path, string content)
    {
        var code = Code(); var s = Script(code);
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-repo-").FullName;
        try
        {
            Run("git", "init -q", repo);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(repo, path))!);
            File.WriteAllText(Path.Combine(repo, path), content);
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

    [Theory] // probes of what the script does not cover; logged, recorded as findings (info), never fail the build
    [InlineData("okp.json", "{\"kty\":\"OKP\",\"crv\":\"Ed25519\",\"d\":\"SSE2FAKE\"}")]
    [InlineData("history", "")]
    public void FS03_coverage_probe(string what, string content)
    {
        var code = Code(); var s = Script(code);
        var repo = Directory.CreateTempSubdirectory("sse2-fsec-repo-").FullName;
        var env = new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "t", ["GIT_AUTHOR_EMAIL"] = "t@local.invalid", ["GIT_COMMITTER_NAME"] = "t", ["GIT_COMMITTER_EMAIL"] = "t@local.invalid" };
        try
        {
            Run("git", "init -q", repo);
            if (what == "history")
            {
                File.WriteAllText(Path.Combine(repo, "k.txt"), "-----BEGIN PRIVATE KEY-----\nSSE2-FAKE-TEST-ONLY\n");
                Run("git", "add -A", repo); Run("git", "commit -qm a", repo, env);
                Run("git", "rm -q k.txt", repo); Run("git", "commit -qm b", repo, env);
                File.WriteAllText(Path.Combine(repo, "ok.txt"), "clean"); Run("git", "add -A", repo);
            }
            else { File.WriteAllText(Path.Combine(repo, what), content); Run("git", "add -A", repo); }
            var (rc, outp) = Run("bash", s, repo);
            o.WriteLine($"FS03 {what}: rc={rc} ({(rc == 0 ? "NOT detected" : "detected")}) {outp.Trim()}");
        }
        finally { Directory.Delete(repo, true); File.Delete(s); }
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
