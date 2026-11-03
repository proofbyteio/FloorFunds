using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// Check 4a (PLAN 10fa9853 §4.1; contract §6.1-6.8 at 003f519; TL Addendum 2 CR-20/21/22/23/23b/25).
/// Hand-off and cage actions FAIL CLOSED on any attestation fault: no password-free code, audit
/// auth.app_access.denied reason=attestation_invalid (verified remote_session=true -> reason=remote_session), fallback to sign-in.
/// Driven by PUT /_standin/pcs/{pc_id} modes (CR-05), so standin-only.
/// </summary>
[Trait("check", "4a"), Trait("target", "standin-only")]
public class AttestationFailClosedTests
{
    // §6.8 seven modes + CR-22 revoked_device / other_location.
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("missing", "attestation_invalid")]
    [InlineData("bad_signature", "attestation_invalid")]
    [InlineData("expired", "attestation_invalid")]
    [InlineData("replayed", "attestation_invalid")]
    [InlineData("unknown_device", "attestation_invalid")]
    [InlineData("revoked_device", "attestation_invalid")]   // CR-22
    [InlineData("other_location", "attestation_invalid")]   // CR-22
    [InlineData("remote_session", "remote_session")]
    public void SEC10_handoff_refuses_on_attestation_fault(string mode, string auditReason) { _ = (mode, auditReason); }

    // Server checks §6.7 as ruled. Every case: attestation_invalid, no password-free code, nonce consumed.
    [Theory(Skip = Pending.Attestation)]
    [InlineData("nonce_of_other_transaction")]                 // 6.4 bound to this transaction
    [InlineData("nonce_already_consumed")]                     // single use
    [InlineData("nonce_older_than_60s_no_skew")]               // CR-23: age <= 60 s, server clock, no skew (advance 61 s)
    [InlineData("bad_signature_then_retry_same_nonce")]        // CR-23b: compare-and-delete at START; failed check burns it
    [InlineData("wrong_aud_then_retry_same_nonce")]            // CR-23b
    [InlineData("two_parallel_submits_same_nonce")]            // CR-23b: exactly one may pass
    [InlineData("device_id_ne_kid")]
    [InlineData("kid_unknown")]
    [InlineData("kid_unenrolled")]                             // CR-22
    [InlineData("kid_revoked")]                                // CR-22
    [InlineData("kid_disabled")]                               // CR-22
    [InlineData("kid_registered_at_other_location")]           // CR-22 (location source still OPEN for real devices)
    [InlineData("exp_passed_beyond_30s_skew")]                 // CR-23: exp > now - 30 s
    [InlineData("exp_not_after_iat")]                          // CR-23: exp > iat
    [InlineData("exp_minus_iat_61s")]                          // CR-23: <= 60
    [InlineData("iat_in_future_beyond_30s_skew")]              // CR-23: iat <= now + 30 s (was 5 s in SSE2's text; G1 is 30 s)
    [InlineData("alg_none")]
    [InlineData("alg_HS256_with_public_key_as_secret")]
    [InlineData("crit_header")]                                // CR-21
    [InlineData("unknown_header_param")]                       // CR-21
    [InlineData("typ_missing")]                                // correction 8:14 PM
    [InlineData("embedded_jwk_or_jku")]
    [InlineData("valid_sig_wrong_key_same_kid")]
    [InlineData("registry_unavailable")]                       // PUT /_standin/faults device_registry=unavailable (CR-24)
    public void SEC11_handoff_refuses_on_binding_or_crypto_edge(string case_) { _ = case_; }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC12_valid_attestation_no_remote_allows_handoff_positive_control() { }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC13_attestation_refusal_falls_back_to_password_and_mfa_and_audits_method_interactive() { }

    [Fact(Skip = Pending.ControlApi)]
    public void SEC14_plain_signin_without_attestation_proceeds_attestation_missing() { /* agent timeout -> submitted without one = missing (CR-25 (1)) */ }

    /// Only a VERIFIED remote_session=true refuses a plain sign-in (screen 8A, auth.login.failed reason=remote_session).
    [Fact(Skip = Pending.ControlApi)]
    public void SEC15_verified_remote_session_refuses_plain_signin_screen_8A() { }

    /// CR-25 (2): an invalid attestation is never valid at plain sign-in; its remote_session is ignored.
    /// Expect: sign-in proceeds, auth.login.succeeded attestation=invalid, no screen 8A, session not attested,
    /// and the NEXT hand-off is refused attestation_invalid.
    [Theory(Skip = Pending.AttestOnPlainSignIn)]
    [InlineData("expired_with_remote_session_true")]
    [InlineData("bad_signature_with_remote_session_true")]
    [InlineData("replayed_with_remote_session_true")]
    [InlineData("wrong_aud_with_remote_session_true")]
    [InlineData("wrong_purpose_with_remote_session_true")]
    [InlineData("unknown_device_with_remote_session_true")]
    public void SEC16_invalid_attestation_on_plain_signin_proceeds_records_invalid_ignores_remote(string case_) => _ = case_;

    /// CR-25 (1): POST /l/{slug}/attest-nonce returns a fresh nonce and invalidates the earlier one.
    [Fact(Skip = Pending.AttestOnPlainSignIn)]
    public void SEC17_attest_nonce_invalidates_earlier_nonce_and_needs_antiforgery_no_store() { }

    /// Cross-purpose replay both ways (aud/purpose addendum 12:15 PM; CR-20 AT10 server side).
    [Theory(Skip = Pending.Attestation)]
    [InlineData("signin", "handoff")] [InlineData("handoff", "signin")]
    [InlineData("handoff", "cage_action")] [InlineData("cage_action", "handoff")]
    public void SEC18_attestation_minted_for_one_purpose_refused_for_another(string mintedFor, string presentedAt) => _ = (mintedFor, presentedAt);

    /// CR-25 (1): slow typing no longer expires the attestation: advance clock 120 s on the form, submit -> valid.
    [Fact(Skip = Pending.AttestOnPlainSignIn)]
    public void SEC19_slow_typing_then_submit_time_fetch_is_valid() { }
}

/// <summary>
/// Check 4b, CR-12 (Addendum 2): enforced per ACTION at the Cash Advance server (S1: BFF stub), not by the login service.
/// Each action (payment, ID scan, print) needs its own fresh attestation: purpose=cage_action, aud = Cash Advance server,
/// nonce from that server (<= 60 s, single use, burnt on first check). Scope = the action (answers OQ-SSE2-34).
/// </summary>
[Trait("check", "4b"), Trait("target", "standin-only")]
public class CageActionsFailClosedTests
{
    [Fact(Skip = Pending.ControlApi)]
    public void SEC50_attestation_invalid_fallback_signin_opens_cash_advance_with_method_interactive() { }

    [Theory(Skip = Pending.CageAction)]
    [InlineData("payment")] [InlineData("id_scan")] [InlineData("print")]
    public void SEC51_cage_action_refused_without_fresh_valid_cage_attestation(string action) => _ = action;

    [Theory(Skip = Pending.CageAction)]
    [InlineData("payment")] [InlineData("id_scan")] [InlineData("print")]
    public void SEC52_cage_action_allowed_with_fresh_valid_cage_attestation(string action) => _ = action;

    [Fact(Skip = Pending.ControlApi)]
    public void SEC53_verified_remote_session_refuses_fallback_signin_too() { }

    /// Scope is the action: one cage attestation cannot authorise a second action; a valid one earlier in the session doesn't carry over.
    [Fact(Skip = Pending.CageAction)]
    public void SEC54_one_cage_attestation_one_action() { }

    /// A signin/handoff attestation (aud = issuer) is refused at a cage action, and a cage nonce from the BFF is single use.
    [Theory(Skip = Pending.CageAction)]
    [InlineData("signin_jws_at_cage_action")] [InlineData("handoff_jws_at_cage_action")] [InlineData("cage_nonce_reused")] [InlineData("cage_nonce_older_than_60s")]
    public void SEC55_cage_action_rejects_wrong_purpose_and_stale_nonce(string case_) => _ = case_;
    [Theory(Skip = Pending.CageAction), Trait("check", "4b"), Trait("category", "security")]
    [InlineData("super")] [InlineData("partner")]
    public void SEC56_admin_session_cage_action_refused_even_with_valid_attestation(string adminType) { _ = adminType; /* [Confirmed DEC-084] contract 6.9, standin-control 2.9: BFF stub cage action from an /admin session with a valid cage_action attestation -> 403 {"error":"access_denied"}; action not performed */ }
}
