namespace Cachd.Login.TestKit;

/// <summary>
/// xUnit has no xfail. Tests for behaviour that is unstated or whose mechanics are unspecified are SKIPPED with a reason that
/// starts with "PENDING OQ-..." (decision SSE2-D4 in docs/BUILD-LOG.md). Reported as "skipped (pending)" in the counts.
/// The OQ ids are in docs/test-matrix/TEST-MATRIX-S1.md.
/// </summary>
public static class Pending
{
    // Contract head checked: feat/contract-v1 003f519 (8:11 PM PT). TL Addendum 2 (rulings md5 5bcaedc9) answered OQ-SSE2-04/05/07/13/29/31-40
    // but SSE has not yet published the docs/changes, and there is no stand-in in code/src/. Reasons name what is still missing.
    const string Docs = "contract/standin-control.md + ui-driver.md published on feat/contract-v1 dd497e1 (CR-05, DRAFT v1-d4, not frozen); no stand-in in code/src/ to run against";
    public const string SignInDriver = "PENDING CR-05 (answers OQ-SSE2-05): " + Docs + ".";
    public const string ControlApi = "PENDING CR-05 (answers OQ-SSE2-04): " + Docs + ".";
    public const string Attestation = "PENDING CR-05: fake-agent modes via PUT /_standin/pcs/{pc_id} (" + Docs + ").";
    public const string AttestOnPlainSignIn = "PENDING CR-25 (answers OQ-SSE2-07): ruled (fetch nonce at submit via POST /l/{slug}/attest-nonce; invalid never valid; attestation=invalid) but not in contract-v1.md at 003f519; and " + Docs + ".";
    public const string CageAction = "PENDING CR-12 (answers OQ-SSE2-34): C §6.9 specifies the BFF stub cage endpoints (POST /cage/attest-nonce, POST /cage/actions/{action}) at dd497e1, but the Cash Advance BFF stub is not built (SI1 held).";
    public const string Timing = "PENDING OQ-SSE2-11: no pass threshold is stated for 'same timing' (not ruled in Addendum 2).";
    public const string RateLimitStatus = "PENDING OQ-SSE2-12: rate-limited status/body (not ruled in Addendum 2).";
    public const string LockoutPreset = "PENDING CR-13 (answers OQ-SSE2-13): fixture partners pci (10/30 min) and nevada (3/30 min) ruled; C §6.3 still says 10/30 only at 003f519; and " + Docs + ".";
    public const string ClockForBff = "PENDING OQ-SSE2-15: how the BFF stub's 15-min idle follows the test clock (CR-05 clock covers the login service only; not ruled for the BFF).";
    public const string ProductionRun = "PENDING OQ-SSE2-17: start command/exit code/log line for refused starts (not ruled in Addendum 2).";
    public const string StateBinding = "PENDING CR-05: hub_launch comes from the hub tile and expires by POST /_standin/clock/advance (" + Docs + ").";
    public const string RedirectRules = "PENDING OQ-SSE2-19/20: redirect_uri / post_logout_redirect_uri matching rules (not ruled in Addendum 2).";
    public const string AllowlistSim = "PENDING OQ-SSE2-22: how a test presents a non-allowlisted source IP (not ruled in Addendum 2).";
    public const string Contract = "PENDING contract v1: code/contract/ is not present yet.";
}
