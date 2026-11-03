using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>Threat-model rows T-S1..T-S9 (docs/threat-model/THREAT-MODEL-S1.md). Live; pending until mechanics are specified.</summary>
public class EnumerationAndSessionTests
{
    // Doc §16.18.3: one shared message for wrong, unknown, locked and rate-limited, no lockout time. L174 unknown slug same page/status/timing.
    [Theory(Skip = Pending.SignInDriver), Trait("check", "10"), Trait("target", "both"), Trait("threat", "I-1")]
    [InlineData("unknown_user")] [InlineData("locked_user")] [InlineData("disabled_user")] [InlineData("wrong_password")]
    public void SEC20_signin_failures_are_byte_identical_after_normalising_csrf(string persona) { _ = persona; }

    [Fact(Skip = Pending.Timing), Trait("target", "both"), Trait("threat", "I-2")]
    public void SEC21_unknown_vs_known_user_timing_indistinguishable() { /* Timing.MannWhitneyP over n samples; threshold TBD */ }

    [Fact(Skip = Pending.Timing), Trait("target", "both"), Trait("threat", "I-3")]
    public void SEC22_unknown_slug_same_page_status_timing() { }

    [Fact(Skip = Pending.RateLimitStatus), Trait("target", "standin-only"), Trait("threat", "D-1")]
    public void SEC23_rate_limit_10_per_ip_5_per_username_no_detail() { }

    [Fact(Skip = Pending.SignInDriver), Trait("target", "both"), Trait("threat", "S-3")]
    public void SEC24_login_session_id_reissued_after_password_step_no_fixation() { /* OQ-SSE2-40 TL Decision (ASVS V3.2.1): pre-sign-in cookie value never promoted */ }
    [Fact(Skip = Pending.SignInDriver), Trait("target", "both"), Trait("threat", "S-5")]
    public void SEC24b_login_session_id_reissued_again_when_mfa_completes() { }

    [Fact(Skip = Pending.SignInDriver), Trait("target", "both"), Trait("threat", "T-2")]
    public void SEC25_signin_post_without_antiforgery_rejected() { }

    [Fact(Skip = Pending.RedirectRules), Trait("target", "both"), Trait("threat", "T-3")]
    public void SEC26_unregistered_redirect_uri_never_redirected_to() { }

    [Fact(Skip = Pending.RedirectRules), Trait("target", "both"), Trait("threat", "T-3")]
    public void SEC27_endsession_ignores_or_exact_matches_post_logout_redirect_uri() { }

    [Fact(Skip = Pending.SignInDriver), Trait("target", "both"), Trait("threat", "S-4")]
    public void SEC28_pkce_plain_or_missing_rejected_and_verifier_mismatch_invalid_grant() { }

    [Fact(Skip = Pending.AllowlistSim), Trait("target", "standin-only"), Trait("threat", "E-3")]
    public void SEC29_allowlist_block_is_minimal_403_not_oauth_error() { }

    [Fact(Skip = Pending.LockoutPreset), Trait("target", "standin-only"), Trait("threat", "D-2")]
    public void SEC30_lockout_cannot_be_triggered_for_admin_via_public_slug() { /* lockout-abuse: attacker locks a cashier by guessing; recovery path & admin never unlocked by hand */ }

    [Fact(Skip = Pending.SignInDriver), Trait("target", "both"), Trait("threat", "T-4")]
    public void SEC31_password_129_chars_rejected_no_truncation_nfkc() { /* Q28 */ }

    [Fact(Skip = Pending.StateBinding), Trait("check", "3"), Trait("target", "standin-only"), Trait("threat", "S-2")]
    public void SEC32_handoff_state_from_another_session_is_not_hub_issued() { }
}
