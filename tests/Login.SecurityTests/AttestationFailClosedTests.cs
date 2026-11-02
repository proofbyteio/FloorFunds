using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// Check 4a (plan §1.2 / BP-2a; login doc rev 11:25 §16.16.1, §16.18.5): the HAND-OFF fails closed on any attestation fault.
/// Each case must: (a) NOT issue a code without password+MFA, (b) audit auth.app_access.denied reason=attestation_invalid
/// (remote_session=true -> reason=remote_session, screen 8), (c) fall back to the normal sign-in page.
/// Uses the stand-in's per-PC attestation mode (plan §3.2), so standin-only; the real target needs a test agent (OQ-SSE2-06).
/// </summary>
[Trait("check", "4a"), Trait("target", "standin-only")]
public class AttestationFailClosedTests
{
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("missing", "attestation_invalid")]
    [InlineData("bad_signature", "attestation_invalid")]
    [InlineData("expired", "attestation_invalid")]          // > 60 s old
    [InlineData("replayed", "attestation_invalid")]         // single use
    [InlineData("unknown_device", "attestation_invalid")]   // not the enrolled PC
    [InlineData("remote_session_true", "remote_session")]
    public void SEC10_handoff_refuses_on_attestation_fault(string mode, string auditReason) { _ = (mode, auditReason); }

    [Theory(Skip = Pending.Attestation)]
    // contract 6e8a106 §6.4-6.7 server checks; each must end in attestation_invalid and no password-free code.
    [InlineData("nonce_of_other_transaction")]      // 6.4 bound to this transaction
    [InlineData("nonce_already_consumed")]          // 6.4 single use
    [InlineData("nonce_older_than_60s")]            // 6.4 server-side nonce TTL
    [InlineData("nonce_consumed_by_failed_attempt")]// failed check must still burn the nonce (CR-23, OQ-SSE2-38)
    [InlineData("signin_nonce_used_for_handoff")]   // purpose binding (CR-20, OQ-35)
    [InlineData("device_id_ne_kid")]                // 6.7 device_id = kid
    [InlineData("kid_unknown")]                     // registry lookup only
    [InlineData("kid_of_revoked_device")]           // CR-22, OQ-SSE2-39
    [InlineData("kid_of_device_at_other_location")] // CR-22, OQ-SSE2-39
    [InlineData("exp_passed")]
    [InlineData("exp_minus_iat_61s")]               // 6.7 <= 60 s
    [InlineData("iat_in_future_beyond_skew")]       // skew 5 s is an SSE Proposal (OQ-35)
    [InlineData("alg_none")]
    [InlineData("alg_HS256_with_public_key_as_secret")]
    [InlineData("embedded_jwk_or_jku")]
    [InlineData("valid_sig_wrong_key_same_kid")]
    [InlineData("registry_unavailable")]            // exception path must fail closed (CR-24)
    public void SEC11_handoff_refuses_on_binding_or_crypto_edge(string case_) { _ = case_; }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC12_valid_attestation_no_remote_allows_handoff_positive_control() { }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC13_attestation_refusal_falls_back_to_password_and_mfa_and_audits_method_interactive() { }

    // §16.18.5 plain sign-in is best effort: no attestation -> proceeds; remote_session=true -> auth.login.failed reason=remote_session.
    [Fact(Skip = Pending.ControlApi)]
    public void SEC14_plain_signin_without_attestation_proceeds() { }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC15_plain_signin_with_remote_session_is_refused_reason_remote_session() { }

    [Fact(Skip = Pending.AttestOnPlainSignIn)]
    public void SEC16_plain_signin_with_present_but_invalid_attestation() { }

    [Fact(Skip = Pending.Attestation)]
    public void SEC17_remote_flag_inside_badly_signed_attestation_reason_precedence() { /* OQ-SSE2-09 */ }
}

/// <summary>
/// Check 4b (plan ae18d664 L243): after an attestation_invalid fallback sign-in Cash Advance opens, but payment, ID scan and print
/// stay refused until the agent attests again (cage actions fail closed); screen 8 variant B text; while a remote session is
/// reported, the fallback sign-in on that PC is refused too (best effort, §16.18.5).
/// The S1 stubs have no cage actions; how 4b is exercised in S1 is unstated (OQ-SSE2-34).
/// </summary>
[Trait("check", "4b"), Trait("target", "standin-only")]
public class CageActionsFailClosedTests
{
    const string HowIn_S1 = "PENDING OQ-SSE2-34: S1 has no cage-action endpoints (payment / ID scan / print) in the BFF/API stubs; which component refuses them and how a test observes it is unstated.";

    [Fact(Skip = Pending.ControlApi)]
    public void SEC50_attestation_invalid_fallback_signin_opens_cash_advance_with_method_interactive() { }

    [Theory(Skip = HowIn_S1)]
    [InlineData("payment")] [InlineData("id_scan")] [InlineData("print")]
    public void SEC51_cage_action_refused_after_attestation_invalid_fallback(string action) => _ = action;

    [Theory(Skip = HowIn_S1)]
    [InlineData("payment")] [InlineData("id_scan")] [InlineData("print")]
    public void SEC52_cage_action_allowed_again_after_valid_attestation(string action) => _ = action;

    [Fact(Skip = Pending.ControlApi)]
    public void SEC53_remote_session_refuses_fallback_signin_too_reason_remote_session() { }

    [Fact(Skip = "PENDING OQ-SSE2-34: is the cage-action block bound to the PC (device id) or to the login session? A second user signing in on the same PC while the agent is down must also be blocked.")]
    public void SEC54_cage_block_scope_pc_vs_session() { }
}
