using Cachd.Login.TestKit;

namespace Cachd.Login.ContractTests;

/// <summary>
/// CT-1x..: S1 acceptance flows (plan §4.1 checks 1–10) against the live target. Each needs mechanics the contract has not
/// specified yet (sign-in driver, control API, test clients), so they are PENDING with the question id.
/// Intended steps are written in the test body comments so SSE can see exactly what will be asserted.
/// Tag "standin-only" marks tests that need /_standin/* (plan §3.2).
/// </summary>
public class FlowTests
{
    // Check 1: hub tiles by role; no_role_user -> access_denied + audit reason=no_role.
    [Fact(Skip = Pending.SignInDriver), Trait("check", "1"), Trait("target", "both")]
    public void CT10_hub_shows_only_tiles_with_a_role() { /* sign in cashier_db -> /apps lists exactly the apps whose app_code has a role; backoffice_only sees no Cash Advance */ }

    [Fact(Skip = Pending.ControlApi), Trait("check", "1"), Trait("target", "standin-only")]
    public void CT11_no_role_user_gets_access_denied_and_audit_no_role() { /* authorize cash_advance as no_role_user -> redirect error=access_denied (no reason in URL); audit auth.app_access.denied reason=no_role */ }

    // Check 2: hand-off within 5 min, no password, audit method=hub_handoff.
    [Fact(Skip = Pending.ControlApi), Trait("check", "2"), Trait("target", "standin-only")]
    public void CT12_handoff_opens_cash_advance_without_password() { /* sign-in pwd+TOTP -> hub tile -> authorize returns code w/o login page; audit auth.app.signed_in method=hub_handoff, original auth_time, login session id */ }

    // Check 3 (PLAN 10fa9853): re-prompt when there is no valid single-use hub_launch (contract §5.7; plan L130 rule 4 superseded).
    [Theory(Skip = Pending.ControlApi), Trait("check", "3"), Trait("target", "standin-only")]
    [InlineData("second_launch")] [InlineData("over_5_min")] [InlineData("no_hub_launch")] [InlineData("hub_launch_already_used")] [InlineData("hub_launch_of_other_session")] [InlineData("after_signout")]
    public void CT13_handoff_reprompts_for_password(string case_) { /* expect the sign-in page again, NOT an OAuth error redirect (plan §3.1) */ _ = case_; }

    // Check 4: remote session refused.
    [Fact(Skip = Pending.ControlApi), Trait("check", "4"), Trait("target", "standin-only")]
    public void CT14_remote_session_refuses_handoff_screen8_reason_remote_session() { /* set remote flag for PC -> launch -> screen 8; cancel -> error=access_denied; audit auth.app_access.denied reason=remote_session */ }

    // Check 5: cage_consumed.
    [Fact(Skip = Pending.ControlApi), Trait("check", "5"), Trait("target", "standin-only")]
    public void CT15_cage_consumed_session_cannot_sso_into_backoffice_stub() { /* after hand-off, authorize backoffice_stub -> sign-in page (no silent SSO); prompt=none -> login_required */ }

    // Check 6: idle + max_age.
    [Fact(Skip = Pending.ClockForBff), Trait("check", "6"), Trait("target", "standin-only")]
    public void CT16_idle_15_min_on_login_cookie_and_bff() { /* advance clock 14:59 -> still signed in; 15:01 -> signed out on both */ }

    [Fact(Skip = Pending.ControlApi), Trait("check", "6"), Trait("target", "standin-only")]
    public void CT17_max_age_900_forces_reauth() { /* auth_time older than 900 s + max_age=900 -> sign-in page; prompt=none -> login_required */ }

    // Check 7: claims.
    [Fact(Skip = Pending.SignInDriver), Trait("check", "7"), Trait("target", "both")]
    public void CT18_access_and_id_token_claims_match_schema_no_roles() { /* use ClaimRules.Violations for cash_advance (Cachd) and backoffice_stub with org_claims off/on */ }

    // Check 8: replay / reuse / bad tokens.
    [Fact(Skip = Pending.SignInDriver), Trait("check", "8"), Trait("target", "both")]
    public void CT19_code_replay_is_invalid_grant_and_revokes() { /* second redemption -> invalid_grant */ }

    [Fact(Skip = Pending.ControlApi), Trait("check", "8"), Trait("target", "standin-only")]
    public void CT20_refresh_reuse_after_30s_revokes_chain_and_audits() { /* rotate, advance 31 s, reuse old -> invalid_grant; new one also dead; audit auth.refresh.reuse_detected. Boundary at exactly 30 s: OQ-SSE2-16 */ }

    [Theory(Skip = Pending.ControlApi), Trait("check", "8"), Trait("target", "standin-only")]
    [InlineData("wrong_aud")] [InlineData("bad_signature")] [InlineData("expired")] [InlineData("not_yet_valid")]
    public void CT21_api_stub_returns_401(string fault) { _ = fault; }

    [Fact(Skip = Pending.ControlApi), Trait("check", "9"), Trait("target", "standin-only")]
    public void CT22_outbox_fault_gives_temporarily_unavailable_and_no_session() { /* fault outbox -> sign-in -> error temporarily_unavailable, no login cookie set, hub not reachable */ }

    [Theory(Skip = Pending.ControlApi), Trait("check", "3"), Trait("target", "standin-only")]
    [InlineData("super")] [InlineData("partner")]
    public void CT28_admin_session_never_gets_cash_advance_handoff(string adminType) { _ = adminType; /* [Confirmed DEC-084] contract 5.12: /admin session (admin_type super|partner) asks for the Cash Advance hand-off with an otherwise valid fresh hub_launch -> refused BEFORE rules 1-6, client error access_denied (no reason), audit auth.app_access.denied reason=not_location_user with envelope admin_type; no code issued; hub_launch outcome not stated -> assert only no code */ }
    [Fact(Skip = Pending.LockoutPreset), Trait("check", "10"), Trait("target", "standin-only")]
    public void CT23_pci_fixture_ten_wrong_passwords_lock_for_30_minutes() { /* partner pci (CR-13): 9 wrong -> 10th correct works after reset; 10 wrong -> correct refused with the SAME generic message; advance 29:59 still locked, 30:00 works; audit auth.account.locked */ }
    [Fact(Skip = Pending.LockoutPreset), Trait("check", "10"), Trait("target", "standin-only")]
    public void CT26_nevada_fixture_three_failed_tries_lock_for_30_minutes() { /* partner nevada (CR-13): 2 wrong -> correct works; 3 wrong -> refused, same generic message; advance 30 min -> works; POST /_standin/reset clears it (admin unlock not in S1) */ }
    [Fact(Skip = Pending.LockoutPreset), Trait("check", "10"), Trait("target", "standin-only")]
    public void CT27_lockout_count_is_per_partner_preset_not_global() { /* 3 wrong at nevada location does not lock a pci-partner user; 3 wrong at pci does not lock */ }

    [Fact(Skip = Pending.ProductionRun), Trait("check", "11"), Trait("target", "standin-only")]
    public void CT24_api_stub_in_production_rejects_x_standin_token() { }

    [Fact(Skip = Pending.SignInDriver), Trait("check", "11"), Trait("target", "standin-only")]
    public void CT25_every_standin_token_carries_x_standin_true() { }
}
