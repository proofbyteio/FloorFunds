namespace Cachd.Login.TestKit;

/// <summary>
/// xUnit has no xfail. Tests for behaviour that is unstated or whose mechanics are unspecified are SKIPPED with a reason that
/// starts with "PENDING OQ-..." (decision SSE2-D4 in docs/BUILD-LOG.md). Reported as "skipped (pending)" in the counts.
/// The OQ ids are in docs/test-matrix/TEST-MATRIX-S1.md.
/// </summary>
public static class Pending
{
    public const string SignInDriver = "PENDING OQ-SSE2-05: how a test drives DB sign-in + TOTP on the stand-in (pages/fields/antiforgery) is not specified in contract v1 yet.";
    public const string ControlApi = "PENDING OQ-SSE2-04: /_standin/* control API routes and payloads (clock, faults, attestation mode, audit read, reset) are not specified yet.";
    public const string Attestation = "PENDING OQ-SSE2-04/OQ-33: format now fixed (contract 6e8a106 §6.5, device-attestation.schema.json) but the stand-in fake agent / control API to mint faulty attestations is not specified (OQ-33 is an SSE Proposal).";
    public const string AttestOnPlainSignIn = "PENDING OQ-SSE2-07 / SSE OQ-11: present-but-INVALID attestation on a plain sign-in: contract 6.2 SSE Proposal \"go ahead, no event\" is not frozen (CR-25: expiry after 60 s silently drops the remote check).";
    public const string Timing = "PENDING OQ-SSE2-11: no pass threshold is stated for 'same timing' (doc L174, L209).";
    public const string RateLimitStatus = "PENDING OQ-SSE2-12: rate-limited = 429 (doc L209) vs 'one shared message' incl. rate-limited (§16.18.3): which status/body?";
    public const string LockoutPreset = "PENDING OQ-SSE2-13: which lockout preset the stand-in fixture location uses (check 10 says 10/30 min; Nevada preset is 3).";
    public const string ClockForBff = "PENDING OQ-SSE2-15: how the BFF stub's 15-min idle is driven by the test clock is not specified.";
    public const string ProductionRun = "PENDING OQ-SSE2-17: how tests start the API stub in Production (command/port) is not specified.";
    public const string StateBinding = "PENDING OQ-SSE2-04: hub_launch is now defined (contract 6e8a106 §5.7) but there is no control API to mint / expire / cross-bind one for a test.";
    public const string RedirectRules = "PENDING OQ-SSE2-19/20: redirect_uri and post_logout_redirect_uri matching rules are not in contract v1 yet.";
    public const string AllowlistSim = "PENDING OQ-SSE2-22: how a test presents a non-allowlisted source IP to the stand-in is not specified.";
    public const string Contract = "PENDING contract v1: code/contract/ is not present yet.";
}
