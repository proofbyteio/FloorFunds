// ENT-* end-to-end tests: real ASP.NET Core OIDC handler + Login.Entra against the fake issuer on 18093.
using System.Net;
using Login.Entra;
using Login.Abstractions;
using Login.Entra.Fixtures;
using Login.FakeEntra;
using Xunit;
namespace Login.EntraTests;

public class EntFlowTests
{
    private static void Signed(Env.Flow f, Env e) => Assert.True(f.FinalStatus == HttpStatusCode.OK && f.FinalBody == "SIGNED-IN", f.FinalBody + "\n" + string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra"))));
    private static void Refused(Env.Flow f, Env e, string v1Reason, string detail)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus);
        Assert.DoesNotContain("<form", f.FinalBody);
        Assert.Empty(e.Host.Sessions);
        Assert.True(e.Failed.Any(a => a.Reason == v1Reason && a.ProviderKind == "entra"), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra."))));
        Assert.True(e.LogHas("detail=" + detail), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra."))));
    }

    [Fact] public async Task ENT_01_right_tenant_linked_assigned_mfa_signs_in()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a");
        Signed(f, e);
        var s = Assert.Single(e.Host.Sessions.Values);
        Assert.Equal(e.UserA, s.UserId); Assert.Equal(Env.LocA, s.LocationId);
        Assert.Equal(["mfa", "pwd"], s.Amr);
        var ok = Assert.Single(e.Succeeded);
        Assert.Equal("entra", ok.ProviderKind);
        Assert.All(ok.Amr!, a => Assert.Contains(a, AmrMapper.ClosedEnum));
        Assert.Empty(e.Failed);
    }

    [Fact] public async Task ENT_02_wrong_tenant_refused()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, TidOverride = Env.TenantB };
        Refused(await e.SignInAsync("loc-a"), e, "tenant_mismatch", "tenant");
    }

    [Fact] public async Task ENT_02b_msa_tenant_refused_in_token_and_in_config()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, TidOverride = EntraLocation.MsaTenant };
        Refused(await e.SignInAsync("loc-a"), e, "tenant_mismatch", "tenant");
        var cfg = (await e.Store.GetAsync(Env.LocA, default))! with { EntraAllowedTenantIds = [EntraLocation.MsaTenant] };
        Assert.Null(EntraLocation.TryCreate(cfg, true, out var why)); Assert.Equal("tenant_invalid", why);
    }

    [Fact] public async Task ENT_02c_iss_guid_differs_from_tid_refused()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, IssTenantOverride = Env.TenantB.ToString("D") };
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus);
        Assert.Empty(e.Host.Sessions);
        Assert.Contains(e.Failed, a => a.Reason is "bad_credentials" or "tenant_mismatch");
    }

    [Fact] public async Task ENT_03_unknown_subject_refused_even_with_same_email()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = Guid.NewGuid(), Email = "same@fake.invalid" }; // email of a linked user
        Refused(await e.SignInAsync("loc-a"), e, "bad_credentials", "unknown_subject");
    }

    public static TheoryData<string, string, int?, string, string> UserStates => new()
    {
        { "locked", "active", 10, "locked", "locked" },
        { "lock-expired", "active", -10, "", "" },
        { "disabled", "disabled", null, "bad_credentials", "disabled" },
    };

    [Theory, MemberData(nameof(UserStates))]
    public async Task ENT_04b_app_user_state(string id, string status, int? lockMin, string v1, string detail)
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA);
            d.Users.Add(new(e.UserA, Env.PartnerId, status, lockMin is { } m ? DateTimeOffset.UtcNow.AddMinutes(m) : null)); });
        var f = await e.SignInAsync("loc-a");
        if (v1 == "") Signed(f, e); else Refused(f, e, v1, detail);
        _ = id;
    }

    // Unknown status / missing user row: Abstractions readers THROW (UserStatusText.Parse), so these fail closed
    // through the same path as a DB error (no session, no form). The old "user_status_unknown"/"user_missing" details are gone.
    [Theory, InlineData("pending_review"), InlineData("Active"), InlineData("")]
    public async Task ENT_04b2_unknown_status_fails_closed(string status)
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, Env.PartnerId, status, null)); });
        var f = await e.SignInAsync("loc-a");
        Assert.NotEqual(HttpStatusCode.OK, f.FinalStatus); Assert.DoesNotContain("<form", f.FinalBody); Assert.Empty(e.Host.Sessions);
    }

    [Fact] public async Task ENT_04c_linked_but_no_app_user_row_fails_closed()
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => d.Users.Clear());
        var f = await e.SignInAsync("loc-a");
        Assert.NotEqual(HttpStatusCode.OK, f.FinalStatus); Assert.DoesNotContain("<form", f.FinalBody); Assert.Empty(e.Host.Sessions);
    }

    [Fact] public async Task ENT_04d_reader_throws_fails_closed()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a", cb => { e.Store.Mutate(d => d.ReadersThrow = true); return cb; });
        Assert.NotEqual(HttpStatusCode.OK, f.FinalStatus);
        Assert.DoesNotContain("<form", f.FinalBody);
        Assert.Empty(e.Host.Sessions);
    }

    [Fact] public async Task ENT_04_linked_but_not_assigned_refused()
    {
        await using var e = await Env.StartAsync();
        Refused(await e.SignInAsync("loc-b"), e, "bad_credentials", "location_not_assigned"); // user A at B (same tenant)
    }

    [Fact] public async Task ENT_05_tampered_state_and_replayed_callback_refused()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a", cb => cb.Replace("state=", "state=X"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus); Assert.Empty(e.Host.Sessions);
        Assert.True(e.LogHas("detail=state_invalid"));

        string? captured = null;
        var ok = await e.SignInAsync("loc-a", cb => captured = cb);
        Signed(ok, e);
        using var h = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = ok.Jar };
        using var c = new HttpClient(h);
        var again = await c.GetAsync(captured);
        Assert.NotEqual(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(e.Host.Sessions);
    }

    public static TheoryData<string, string[], string[], bool> AmrCases => new()
    {
        { "a-mfa", ["mfa"], ["mfa"], true },
        { "b-ngcmfa", ["ngcmfa"], ["mfa"], true },
        { "c-wiaormfa", ["wiaormfa"], [], false },
        { "d-wia", ["wia"], ["wia"], false },
        { "e-pwd", ["pwd"], ["pwd"], false },
        { "f-otp", ["otp"], ["otp"], false },
        { "g-fed", ["fed"], [], false },
        { "h-none", ["none", "rsa"], [], false },
    };

    [Theory, MemberData(nameof(AmrCases))]
    public async Task ENT_06_amr_mapping_and_mfa_required(string id, string[] entra, string[] mapped, bool signsIn)
    {
        Assert.Equal(mapped, AmrMapper.Map(entra));
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = entra }; // loc-a has MfaRequired=true
        var f = await e.SignInAsync("loc-a");
        if (signsIn) Signed(f, e); else Refused(f, e, "mfa_missing", "mfa_missing");
        Assert.DoesNotContain(e.Logs.Lines, l => entra.Except(mapped).Any(raw => raw.Length > 3 && l.Contains(raw))); // raw amr never logged
        _ = id;
    }

    [Fact] public async Task ENT_06i_mfa_not_required_location_still_needs_nonempty_amr()
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => { d.Assignments.Add(new(e.UserA, Env.LocB)); });
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = ["wiaormfa"] };
        Refused(await e.SignInAsync("loc-b"), e, "bad_credentials", "amr_missing");
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = ["pwd"] };
        Signed(await e.SignInAsync("loc-b"), e);
    }

    [Fact] public async Task ENT_HO_01_ngcmfa_session_passes_handoff_rule2()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = ["ngcmfa"] };
        Signed(await e.SignInAsync("loc-a"), e);
        var s = Assert.Single(e.Host.Sessions.Values);
        Assert.Equal(["mfa"], s.Amr);
        Assert.True(HandoffRule.Allows(s.Amr, s.AuthTime, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
        Assert.False(HandoffRule.Allows(s.Amr, s.AuthTime, DateTimeOffset.UtcNow.AddMinutes(6), TimeSpan.FromMinutes(5)));
    }

    [Fact] public async Task ENT_HO_02_wiaormfa_session_fails_handoff_rule2()
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => d.Assignments.Add(new(e.UserA, Env.LocB)));
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = ["wiaormfa", "pwd"] };
        Signed(await e.SignInAsync("loc-b"), e);
        var s = Assert.Single(e.Host.Sessions.Values);
        Assert.False(HandoffRule.Allows(s.Amr, s.AuthTime, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));
    }

    public static TheoryData<string, Func<Guid, FakeUser>> TokenShapes => new()
    {
        { "a-id-token-as-access-token", oid => new FakeUser { Oid = oid, AccessTokenIsIdToken = true } },
        { "b-v1-token", oid => new FakeUser { Oid = oid, Ver = "1.0" } },
        { "c-scp-missing", oid => new FakeUser { Oid = oid, Scp = null } },
        { "d-azp-wrong", oid => new FakeUser { Oid = oid, AzpOverride = Guid.NewGuid().ToString() } },
        { "e-alg-hs256", oid => new FakeUser { Oid = oid, Alg = "HS256" } },
        { "f-unknown-kid", oid => new FakeUser { Oid = oid, UnknownKid = true } },
        { "g-aud-other-api", oid => new FakeUser { Oid = oid, AudOverride = Guid.NewGuid().ToString() } },
    };

    [Theory, MemberData(nameof(TokenShapes))]
    public async Task ENT_07_bad_access_token_refused(string id, Func<Guid, FakeUser> user)
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = user(e.OidA);
        Refused(await e.SignInAsync("loc-a"), e, "bad_credentials", "token_invalid");
        _ = id;
    }

    [Fact] public async Task ENT_08_stale_or_missing_auth_time_refused()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, AuthTimeOffsetSec = -301 };
        Refused(await e.SignInAsync("loc-a"), e, "stale_auth_time", "stale_auth_time");
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, OmitAuthTime = true };
        Refused(await e.SignInAsync("loc-a"), e, "stale_auth_time", "auth_time_missing");
    }

    [Theory, InlineData("acct"), InlineData("idp")]
    public async Task ENT_09_guest_refused(string kind)
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = kind == "acct" ? new FakeUser { Oid = e.OidA, Acct = "1" } : new FakeUser { Oid = e.OidA, Idp = "https://sts.windows.net/f4ce0000-5e20-4a00-8000-0000000000ff/" };
        Refused(await e.SignInAsync("loc-a"), e, "bad_credentials", "guest_account");
    }

    [Fact] public async Task ENT_10_uti_replay_refused()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, FixedUti = "fixed-uti-1" };
        Signed(await e.SignInAsync("loc-a"), e);
        e.Host.Sessions.Clear();
        Refused(await e.SignInAsync("loc-a"), e, "bad_credentials", "uti_replay");
    }

    public static TheoryData<string, string, int, string> DownCases => new()
    {
        { "a-discovery-500", "discovery", 500, "DiscoveryFailed" },
        { "b-jwks-503", "jwks", 503, "JwksFailed" },
        { "c-token-503", "token", 503, "TokenEndpointFailed" },
        { "d-authorize-temporarily_unavailable", "authorize", 0, "IdpTemporarilyUnavailable" },
    };

    private static void Fail(Env e, string ep, int status) =>
        e.Fake.Failures[ep] = ep == "authorize" ? new FakeFailure(AuthorizeError: "temporarily_unavailable") : new FakeFailure(status);

    [Theory, MemberData(nameof(DownCases))]
    public async Task ENT_11_down_with_fallback_on_shows_db_form(string id, string ep, int status, string reason)
    {
        await using var e = await Env.StartAsync();
        Fail(e, ep, status);
        var f = await e.SignInAsync("loc-a"); // AllowDbFallback=true
        Assert.Equal(HttpStatusCode.OK, f.FinalStatus);
        Assert.Contains("FALLBACK", f.FinalBody); Assert.Contains("type=password", f.FinalBody);
        Assert.True(e.LogHas($"fallback_reason={reason} fallback=used"), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra"))));
        Assert.Empty(e.Host.Sessions);
        _ = id;
    }

    [Theory, MemberData(nameof(DownCases))]
    public async Task ENT_12_down_with_fallback_off_shows_no_form(string id, string ep, int status, string reason)
    {
        await using var e = await Env.StartAsync();
        Fail(e, ep, status);
        e.Fake.NextUser = new FakeUser { Oid = e.OidB };
        var f = await e.SignInAsync("loc-nofb");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus);
        Assert.DoesNotContain("<form", f.FinalBody); Assert.DoesNotContain("password", f.FinalBody);
        Assert.True(e.LogHas($"fallback_reason={reason} fallback=disabled"));
        _ = id;
    }

    [Fact] public async Task ENT_12d_fallback_on_happy_path_for_tenant_b()
    {
        await using var e = await Env.StartAsync();
        e.Fake.NextUser = new FakeUser { Oid = e.OidB };
        Signed(await e.SignInAsync("loc-nofb"), e);
    }

    [Theory, InlineData(400, true), InlineData(1600, false)]
    public async Task ENT_13_timeout_boundary(int delayMs, bool up)
    {
        await using var e = await Env.StartAsync(o => o.BackchannelTimeout = TimeSpan.FromSeconds(1));
        e.Fake.Failures["discovery"] = new FakeFailure(DelayMs: delayMs);
        var f = await e.SignInAsync("loc-a");
        if (up) Signed(f, e);
        else { Assert.Contains("FALLBACK", f.FinalBody); Assert.True(e.LogHas("fallback_reason=DiscoveryTimeout")); }
    }

    [Fact] public async Task ENT_13b_token_endpoint_timeout_is_down()
    {
        await using var e = await Env.StartAsync(o => o.BackchannelTimeout = TimeSpan.FromSeconds(1));
        e.Fake.Failures["token"] = new FakeFailure(DelayMs: 1600);
        var f = await e.SignInAsync("loc-a");
        Assert.Contains("FALLBACK", f.FinalBody);
        Assert.True(e.LogHas("fallback_reason=TokenEndpointTimeout"), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra"))));
    }

    [Fact] public async Task ENT_14_last_good_config_counts_as_up()
    {
        await using var e = await Env.StartAsync(o => o.RefreshInterval = TimeSpan.FromSeconds(1));
        Signed(await e.SignInAsync("loc-a"), e);
        e.Fake.Failures["discovery"] = new FakeFailure(500);
        e.Fake.Failures["jwks"] = new FakeFailure(500);
        e.Fake.NextUser = new FakeUser { Oid = e.OidA };
        await Task.Delay(1100);
        Signed(await e.SignInAsync("loc-a"), e);
        Assert.Equal(2, e.Host.Sessions.Count);
    }

    [Fact] public async Task ENT_14b_last_good_older_than_max_age_is_down()
    {
        var clock = new ShiftClock();
        await using var e = await Env.StartAsync(o => { o.LkgMaxAge = TimeSpan.FromHours(24); }, clock: clock);
        Signed(await e.SignInAsync("loc-a"), e);
        e.Fake.Failures["discovery"] = new FakeFailure(500);
        clock.Shift = TimeSpan.FromHours(25);
        var f = await e.SignInAsync("loc-a");
        Assert.Contains("FALLBACK", f.FinalBody);
    }

    [Fact] public async Task ENT_15_invalid_grant_is_not_down()
    {
        await using var e = await Env.StartAsync();
        e.Fake.Failures["token"] = new FakeFailure(400);
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus);
        Assert.DoesNotContain("FALLBACK", f.FinalBody);
        Assert.Contains(e.Failed, a => a.Reason == "bad_credentials");
        Assert.False(e.LogHas("entra.down"));
    }

    [Fact] public async Task ENT_SO_01_our_revoke_strictly_before_entra_endsession()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a");
        Signed(f, e);
        using var h = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = f.Jar };
        using var c = new HttpClient(h);
        var r = await c.GetAsync($"{e.HostUrl}/logout?location={Env.LocA}");
        Assert.Empty(e.Host.Sessions);
        var revokedAt = e.Host.Timeline.Single(t => t.What == "auth.session.revoked").At;
        var to = r.Headers.Location!.ToString();
        Assert.StartsWith($"{e.Fake.BaseUrl}/{Env.TenantA:D}/oauth2/v2.0/logout", to);
        Assert.DoesNotContain("id_token_hint", to); Assert.DoesNotContain("logout_hint", to);
        await c.GetAsync(to);
        var hit = e.Fake.Log.Single(l => l.Endpoint == "endsession");
        Assert.True(revokedAt < hit.At);
        Assert.Single(e.Host.Audit, a => a.EventType == "auth.session.revoked" && a.Reason == "sign_out");
    }

    [Fact] public async Task ENT_SO_02_ours_revoked_even_if_entra_down()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        e.Fake.Failures["discovery"] = new FakeFailure(500);
        // new row_version → new config cache entry → no last-good copy
        e.Store.Mutate(d => { var i = d.Locations.FindIndex(l => l.Slug == "loc-a"); d.Locations[i] = d.Locations[i] with { Config = d.Locations[i].Config with { RowVersion = 2 } }; });
        using var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var r = await c.GetAsync($"{e.HostUrl}/logout?location={Env.LocA}");
        Assert.Equal("/l/loc-a", r.Headers.Location!.ToString());
        Assert.Single(e.Host.Audit, a => a.EventType == "auth.session.revoked");
        Assert.True(e.LogHas("entra_endsession=skipped"));
    }

    [Fact] public async Task ENT_DB_01_database_location_renders_host_page_untouched()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-db");
        Assert.Equal(HttpStatusCode.OK, f.FinalStatus);
        Assert.DoesNotContain("FALLBACK", f.FinalBody);
        Assert.Single(f.Hops);
        Assert.Empty(e.Fake.Log);
    }

    [Fact] public async Task ENT_CFG_01_missing_or_invalid_config_fails_closed()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("no-such-location");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus); Assert.DoesNotContain("<form", f.FinalBody);
        var c = (await e.Store.GetAsync(Env.LocA, default))!;
        Assert.Null(EntraLocation.TryCreate(c, allowHttp: false, out var w1)); Assert.Equal("authority_not_https", w1);
        Assert.Null(EntraLocation.TryCreate(c with { EntraAllowedTenantIds = [Env.TenantA, Env.TenantB] }, true, out var w2)); Assert.Equal("tenant_count", w2);
        Assert.Null(EntraLocation.TryCreate(c with { OidcAuthority = "http://example.invalid" }, true, out var w3)); Assert.Equal("authority_not_https", w3);
        Assert.Null(EntraLocation.TryCreate(FixtureConfig.WithoutSecret(c), true, out _));
        Assert.Equal("https://login.microsoftonline.com/" + Env.TenantA.ToString("D") + "/v2.0",
            EntraLocation.TryCreate(c with { OidcAuthority = "https://login.microsoftonline.com/" }, false, out _)!.Authority);
        var prod = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Production" });
        Assert.Throws<InvalidOperationException>(() => Microsoft.Extensions.DependencyInjection.AuthenticationServiceCollectionExtensions
            .AddAuthentication(prod.Services).AddEntraLocationSignIn(prod.Environment, o => o.AllowInsecureTestAuthority = true));
    }

    [Fact] public async Task ENT_CFG_01b_unresolvable_secret_fails_closed()
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => d.Secrets.Remove("ref-a"));
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus); Assert.Empty(e.Host.Sessions);
        Assert.True(e.LogHas("detail=secret_unresolved"));
    }

    [Fact] public async Task ENT_CFG_02_row_version_change_between_challenge_and_callback_refused()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a", cb =>
        {
            e.Store.Mutate(d => { var i = d.Locations.FindIndex(l => l.Slug == "loc-a"); d.Locations[i] = d.Locations[i] with { Config = d.Locations[i].Config with { RowVersion = 9 } }; });
            return cb;
        });
        Refused(f, e, "bad_credentials", "config_changed");
    }

    [Fact] public async Task ENT_PKCE_01_wrong_or_missing_verifier_rejected_by_fake_and_sent_by_us()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        var tok = e.Fake.Log.Single(l => l.Endpoint == "token");
        Assert.Equal("(redacted)", tok.Query["code_verifier"]);
        Assert.Equal("authorization_code", tok.Query["grant_type"]);
        // direct: a fresh code with a wrong verifier
        string? code = null;
        var f = await e.SignInAsync("loc-a", cb => { code = System.Web.HttpUtility.ParseQueryString(new Uri(cb).Query)["code"]; return cb.Replace("code=", "code=x"); });
        Assert.NotEqual(HttpStatusCode.OK, f.FinalStatus);
        using var c = new HttpClient();
        var r = await c.PostAsync($"{e.Fake.BaseUrl}/{Env.TenantA:D}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = "authorization_code", ["code"] = code!, ["code_verifier"] = "wrong", ["client_id"] = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact] public async Task ENT_PROMPT_01_authorize_request_shape()
    {
        await using var e = await Env.StartAsync();
        var f = await e.SignInAsync("loc-a");
        var q = e.Fake.Log.Single(l => l.Endpoint == "authorize").Query;
        Assert.Equal("login", q["prompt"]); Assert.Equal("0", q["max_age"]);
        Assert.Equal("S256", q["code_challenge_method"]); Assert.Equal("code", q["response_type"]);
        Assert.False(string.IsNullOrEmpty(q["state"])); Assert.False(string.IsNullOrEmpty(q["nonce"]));
        Assert.DoesNotContain("offline_access", q["scope"]);
        Assert.Contains("/login.verify", q["scope"]);
        Assert.DoesNotContain("location", string.Join("&", q.Keys)); // location only in protected state
    }
}

public sealed class ShiftClock : TimeProvider
{
    public TimeSpan Shift { get; set; }
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
}
