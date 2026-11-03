using System.Diagnostics;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// Absolute session cap (contract 6e8a106 §7.4; Mohanraj t43u "12 hr is fine but ... configurable"; TL correction 12:01/12:08 PM PT).
/// Test ONLY: (a) the 12 h default applies; (b) a configured value of 15 minutes or less (the idle timeout) is rejected (service does not start).
/// The allowed RANGE is OPEN (OQ-40, Mohanraj): no test asserts any other bound.
/// </summary>
[Trait("check", "session-cap")]
public class SessionCapTests
{
    const string Setting = "LoginService__Session__AbsoluteLifetime"; // env form of LoginService:Session:AbsoluteLifetime (contract §7.4)

    [Fact(Skip = Pending.ControlApi),
     Trait("target", "standin-only")]
    public void SEC60_default_cap_is_12h_session_and_refresh_family_end_at_signin_plus_12h() { }

    [Fact, Trait("target", "standin-only")]
    public void SEC60b_default_value_is_12h_in_contract_text()
    {
        var f = Path.Combine(Paths.Contract, "contract-v1.md");
        if (!File.Exists(f)) Assert.Skip(Paths.ContractSkipReason("contract-v1.md") ?? "contract-v1.md missing");
        var t = File.ReadAllText(f);
        Assert.Contains("LoginService:Session:AbsoluteLifetime", t);
        Assert.Contains("`12:00:00`", t);
    }

    /// (b): cap <= 15 min idle must stop startup. Values at and below the boundary only.
    [Theory, Trait("target", "standin-only")]
    [InlineData("00:15:00")] [InlineData("00:14:59")] [InlineData("00:01:00")] [InlineData("00:00:00")] [InlineData("-01:00:00")]
    public void SEC61_configured_cap_of_15min_or_less_is_rejected_at_startup(string value)
    {
        var proj = Path.Combine(Paths.CodeRoot, "src", "Login.StandIn");
        if (!Directory.Exists(proj)) Assert.Skip("code/src/Login.StandIn/ not present yet (SSE builds it after the contract freeze).");
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{proj}\" --no-launch-profile")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment[Setting] = value;
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0"; // never 8080/8081
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        using var p = Process.Start(psi)!;
        var exited = p.WaitForExit(TimeSpan.FromSeconds(90));
        if (!exited) { p.Kill(true); Assert.Fail($"stand-in kept running with {Setting}={value}; contract §7.4 says it must not start"); }
        Assert.NotEqual(0, p.ExitCode);
    }

    [Fact(Skip = "PENDING OQ-40 (Mohanraj): who may change the cap and its allowed range (shortest/longest) are OPEN. No bound other than '> 15 min idle' is tested until decided."),
     Trait("target", "both")]
    public void SEC62_cap_range_bounds() { }

    [Fact(Skip = "DEFERRED S2 (OQ-SSE2-36 ruled): real service started with env LoginService__Session__AbsoluteLifetime=00:15:00 must exit non-zero and log a line; runs in S2, not S1."),
     Trait("target", "real")]
    public void SEC63_real_service_rejects_cap_le_15min() { }

    [Fact(Skip = Pending.ControlApi + " Ruled OQ-SSE2-37: a hand-off client's refresh family ends at ORIGINAL sign-in + cap."),
     Trait("target", "both")]
    public void SEC64_handoff_does_not_extend_cap() { }
}
