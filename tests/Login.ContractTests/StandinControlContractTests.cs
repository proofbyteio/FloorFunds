using Cachd.Login.TestKit;

namespace Cachd.Login.ContractTests;

/// <summary>
/// CTL-xx / UID-xx: the stand-in control API and UI driver contract (TL Addendum 2, CR-05; rulings md5 5bcaedc9).
/// First written from the rulings text (files absent at 003f519); aligned at 8:2x PM PT to contract/standin-control.md and
/// ui-driver.md as published at feat/contract-v1 dd497e1 (DRAFT v1-d4; testids and GET clock shape are SSE proposals).
/// PENDING until a stand-in exists in code/src/. Control listener: 127.0.0.1 only (LOGIN_STANDIN_CONTROL_URL).
/// </summary>
[Trait("check", "ctl"), Trait("target", "standin-only")]
public class StandinControlContractTests
{
    [Fact(Skip = Pending.ControlApi)] public void CTL01_front_listener_returns_404_for_every_standin_path() { }
    [Fact(Skip = Pending.ControlApi)] public void CTL02_control_listener_refuses_non_loopback_caller_403() { }
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("Production", "1")] [InlineData("Development", "0")] [InlineData("Development", "")]
    public void CTL03_standin_does_not_start_unless_development_and_standin_allow(string env, string allow) => _ = (env, allow);
    [Fact(Skip = Pending.ControlApi)] public void CTL04_every_control_response_is_cache_control_no_store() { }
    [Fact(Skip = Pending.ControlApi)] public void CTL05_post_reset_restores_users_sessions_nonces_hub_launch_lockouts_audit_faults_clock() { }
    /// standin-control 2.2: n >= 0; n < 0 -> 400; GET -> {"now": RFC 3339 UTC} (SSE proposal).
    [Theory(Skip = Pending.ControlApi)]
    [InlineData(0, 200)] [InlineData(61, 200)] [InlineData(-1, 400)]
    public void CTL06_clock_advance_is_forward_only_and_get_clock_reflects_it(int seconds, int status) => _ = (seconds, status);
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("valid")] [InlineData("missing")] [InlineData("bad_signature")] [InlineData("expired")] [InlineData("replayed")]
    [InlineData("unknown_device")] [InlineData("remote_session")] [InlineData("revoked_device")] [InlineData("other_location")]
    public void CTL07_put_pcs_returns_standin_pc_cookie_for_each_mode_no_set_cookie(string mode) => _ = mode;
    [Fact(Skip = Pending.ControlApi)] public void CTL07b_put_pcs_unknown_mode_or_pc_id_not_starting_standin_rejected() { }
    [Fact(Skip = Pending.ControlApi)] public void CTL08_put_users_returns_user_id_and_totp_code_signs_in() { }
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("min_length")] [InlineData("max_length")] [InlineData("letters_and_numbers")] [InlineData("two_of_four_types")] [InlineData("contains_username")] [InlineData("reused")]
    public void CTL09_put_users_password_failing_shared_validator_returns_422_violation_id(string ruleId) => _ = ruleId;
    [Theory(Skip = Pending.ControlApi)]
    [InlineData("outbox")] [InlineData("device_registry")]
    public void CTL10_put_faults_takes_effect_and_reset_clears_it(string fault) => _ = fault;
    [Fact(Skip = Pending.ControlApi)] public void CTL11_get_audit_since_pages_with_next_in_emit_order_and_each_event_validates() { }
    [Fact(Skip = Pending.ControlApi)] public void CTL12_fake_agent_listens_on_loopback_only() { }
    [Fact(Skip = Pending.ControlApi)] public void CTL13_there_is_no_hub_launch_or_nonce_mint_route() { }

    /// Doc-level, runs now: the violation ids in standin-control.md §3 are the ones this suite asserts.
    [Fact, Trait("target", "both")]
    public void CTL14_doc_lists_the_six_violation_ids()
    {
        var f = Path.Combine(Paths.Contract, "standin-control.md");
        if (!File.Exists(f)) Assert.Skip(Paths.ContractSkipReason("standin-control.md") ?? "standin-control.md missing");
        var t = File.ReadAllText(f);
        foreach (var id in new[] { "min_length", "max_length", "letters_and_numbers", "two_of_four_types", "contains_username", "reused" }) Assert.Contains($"| `{id}` |", t);
    }

    [Theory(Skip = Pending.SignInDriver)]
    [InlineData("username")] [InlineData("password")] [InlineData("device_attestation")] [InlineData("__RequestVerificationToken")] [InlineData("totp_code")]
    public void UID01_signin_form_field_names_match_ui_driver(string field) => _ = field;
    [Fact(Skip = Pending.SignInDriver)] public void UID02_post_without_antiforgery_from_prior_get_is_refused() { }
    [Theory(Skip = Pending.SignInDriver)]
    [InlineData("screen-signin")] [InlineData("signin-username")] [InlineData("signin-password")] [InlineData("signin-submit")] [InlineData("screen-totp")] [InlineData("totp-code")]
    [InlineData("totp-submit")] [InlineData("screen-hub")] [InlineData("screen-8a")] [InlineData("screen-8b")] [InlineData("error-area")] [InlineData("hub-tile-{app_code}")]
    public void UID03_data_testids_present(string testid) => _ = testid;
    /// Only these strings may be asserted (ui-driver §3); everything else by testid.
    [Theory(Skip = Pending.SignInDriver)]
    [InlineData("Sign-in didn't work.")] [InlineData("This PC's security software isn't responding. Contact IT.")] [InlineData("Try again after the remote session ends")]
    public void UID04_only_ld_strings_asserted(string text) => _ = text;
    [Fact(Skip = Pending.SignInDriver)] public void UID05_page_calls_attest_nonce_then_agent_before_post() { }
}
