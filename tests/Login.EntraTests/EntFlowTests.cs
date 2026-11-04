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
        var cfg = (await e.Store.GetAsync(Env.LocA, TestContext.Current.CancellationToken))! with { EntraAllowedTenantIds = [EntraLocation.MsaTenant] };
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
        Refused(await e.SignInAsync("loc-b"), e, "not_location_user", "not_location_user"); // user A at B (same tenant)
    }

    // TLD 8:07 (a): partner must match the location's partner, on top of user_location.
    static readonly Guid OtherPartner = Guid.Parse("f4ce0000-5e20-4a00-8000-0000000000c2"); // SSE2-FAKE-TEST-ONLY
    public static TheoryData<string, bool, bool, bool> PartnerCases => new()
    {
        { "null-partner", true, false, false },      // id, assigned, partnerMatches(false => null or other), signsIn
        { "mismatch-assigned", true, false, false },
        { "match-unassigned", false, true, false },
        { "match-assigned", true, true, true },
    };

    [Theory, MemberData(nameof(PartnerCases))]
    public async Task ENT_04e_partner_must_match_location(string id, bool assigned, bool match, bool signsIn)
    {
        await using var e = await Env.StartAsync();
        Guid? partner = match ? Env.PartnerId : id == "null-partner" ? null : OtherPartner;
        e.Store.Mutate(d =>
        {
            d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, partner, "active", null));
            if (!assigned) d.Assignments.RemoveAll(a => a.UserId == e.UserA && a.LocationId == Env.LocA);
        });
        var f = await e.SignInAsync("loc-a");
        if (signsIn) { Signed(f, e); return; }
        Refused(f, e, "not_location_user", "not_location_user");
        // Indistinguishable: every refusal of this kind carries the same reason and the same log detail, and no partner id.
        var ev = Assert.Single(e.Failed);
        Assert.Equal("not_location_user", ev.Reason);
        Assert.Null(ev.UserId);
        var dump = System.Text.Json.JsonSerializer.Serialize(e.Host.Audit) + string.Join("\n", e.Logs.Lines);
        foreach (var p in new[] { Env.PartnerId, OtherPartner })
        { Assert.DoesNotContain(p.ToString("D"), dump, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(p.ToString("N"), dump, StringComparison.OrdinalIgnoreCase); }
    }

    [Fact] public async Task ENT_04f_partner_mismatch_and_unassigned_look_identical()
    {
        string Shape(Env e) => string.Join("|", e.Failed.Select(a => $"{a.EventType}/{a.ProviderKind}/{a.Reason}/{a.UserId}/{a.Amr?.Count}"))
            + "#" + string.Join("|", e.Logs.Lines.Where(l => l.Contains("entra.refused")).Select(l => System.Text.RegularExpressions.Regex.Replace(l, "event=[0-9a-f-]+", "event=X")));
        var a = await Env.StartAsync();
        a.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == a.UserA); d.Users.Add(new(a.UserA, OtherPartner, "active", null)); });
        await a.SignInAsync("loc-a");
        var sa = Shape(a);
        await a.DisposeAsync();
        await using var b = await Env.StartAsync();
        b.Store.Mutate(d => d.Assignments.RemoveAll(x => x.UserId == b.UserA && x.LocationId == Env.LocA));
        await b.SignInAsync("loc-a");
        Assert.Equal(sa, Shape(b));
    }

    // TLD 8:31 Q2: distinct audit reasons stay, but the user-facing response is byte-identical for these refusals.
    // Normalised away only: Date, per-request ids, Set-Cookie values (name + attributes kept).
    static string Wire(Env.Flow f)
    {
        var skip = new[] { "date:", "request-id:", "x-request-id:", "traceparent:", "x-correlation-id:" };
        var h = f.FinalHeaders!.Where(x => !skip.Any(s => x.StartsWith(s, StringComparison.Ordinal)))
            .Select(x => x.StartsWith("set-cookie:", StringComparison.Ordinal) ? System.Text.RegularExpressions.Regex.Replace(x, @"^set-cookie: ([^=]+)=[^;]*", "set-cookie: $1=<v>") : x)
            // the OIDC handler puts the per-request nonce/correlation id in the cookie NAME; mask that id only
            .Select(x => System.Text.RegularExpressions.Regex.Replace(x, @"^set-cookie: (\.AspNetCore\.(?:Correlation|OpenIdConnect\.Nonce))\.[^=]+=", "set-cookie: $1.<id>="))
            .Order(StringComparer.Ordinal);
        return $"{(int)f.FinalStatus}\n{string.Join("\n", h)}\n\n{Convert.ToHexString(f.FinalBytes!)}";
    }

    [Fact] public async Task ENT_04g_refusals_are_byte_identical_to_the_user()
    {
        var cases = new (string name, Action<Env> arrange, string reason)[]
        {
            ("unknown_subject", e => e.Fake.NextUser = new FakeUser { Oid = Guid.NewGuid() }, "bad_credentials"),
            ("partner_mismatch", e => e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, OtherPartner, "active", null)); }), "not_location_user"),
            ("unassigned", e => e.Store.Mutate(d => d.Assignments.RemoveAll(a => a.UserId == e.UserA && a.LocationId == Env.LocA)), "not_location_user"),
            ("locked", e => e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, Env.PartnerId, "active", DateTimeOffset.UtcNow.AddHours(1))); }), "locked"),
            ("disabled", e => e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, Env.PartnerId, "disabled", null)); }), "bad_credentials"),
        };
        var wires = new List<(string, string)>();
        foreach (var (name, arrange, reason) in cases)
        {
            var e = await Env.StartAsync();
            try
            {
                arrange(e);
                var f = await e.SignInAsync("loc-a");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus);
                Assert.Empty(e.Host.Sessions);
                Assert.Equal(reason, Assert.Single(e.Failed).Reason); // audit stays distinct (internal, admin-only)
                wires.Add((name, Wire(f)));
            }
            finally { await e.DisposeAsync(); }
        }
        foreach (var (name, w) in wires.Skip(1)) Assert.True(w == wires[0].Item2, $"{name} differs from {wires[0].Item1}:\n{w}\n---\n{wires[0].Item2}");
    }

    // 7e0350b (SSE2 N1): UserStatus {Unknown=0, Active=1, Disabled=2}; default is Unknown and MaySignIn is false.
    public static TheoryData<string> NonActive => new() { "default", "unknown" };

    [Theory, MemberData(nameof(NonActive))]
    public async Task ENT_09u_default_and_unknown_status_refused(string which)
    {
        UserStatus st = which == "default" ? default : UserStatus.Unknown;
        Assert.Equal(UserStatus.Unknown, st);
        Assert.False(new ExternalLoginMatch(Guid.NewGuid(), Env.PartnerId, st, null).MaySignIn(DateTimeOffset.UtcNow));
        Assert.False(default(ExternalLoginMatch)?.MaySignIn(DateTimeOffset.UtcNow) ?? false);
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => { d.Users.RemoveAll(u => u.UserId == e.UserA); d.Users.Add(new(e.UserA, Env.PartnerId, "active", null) { RawStatus = st }); });
        Refused(await e.SignInAsync("loc-a"), e, "bad_credentials", "user_status_unknown");
    }

    // 7e0350b (SSE2 N2): a SecretReference never reaches audit or logs, on success or refusal, nor via ToString/JSON.
    [Fact] public async Task ENT_19_secret_reference_never_in_audit_or_logs()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        e.Fake.NextUser = new FakeUser { Oid = e.OidA, Amr = ["pwd"] };
        await e.SignInAsync("loc-a"); // refused (mfa_missing)
        e.Store.Mutate(d => d.Assignments.Clear());
        await e.SignInAsync("loc-a"); // refused (not_location_user)
        // D-51: the ref is deployment-level; build the config through the same fill the store uses (no per-location ref).
        var cfg = new FixtureDeployment { Authority = "https://login.example.test", ClientId = "cid", SecretRef = Env.DeploymentRef }
            .Apply(FixtureConfig.Build(Env.LocA, Env.PartnerId, "x", ProviderKind.Entra, "x", [Guid.NewGuid()], false, true));
        var loc = EntraLocation.TryCreate(cfg, false, out _)!;
        Assert.Equal(Env.DeploymentRef, loc.SecretRef.Reveal());
        Assert.Equal(Env.DeploymentRef, e.Store.LastResolvedRef); // the sign-ins above resolved the same deployment ref
        var dump = string.Join("\n", e.Logs.Lines) + System.Text.Json.JsonSerializer.Serialize(e.Host.Audit)
            + cfg + System.Text.Json.JsonSerializer.Serialize(cfg) + loc + System.Text.Json.JsonSerializer.Serialize(loc) + cfg.SecretRef;
        Assert.True(e.Host.Audit.Count() >= 3);
        Assert.DoesNotContain("sse2-fake-test-only:secret", dump);
        Assert.DoesNotContain("9q7z", dump);
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
        var again = await c.GetAsync(captured, TestContext.Current.CancellationToken);
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
        await Task.Delay(1100, TestContext.Current.CancellationToken);
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
        var r = await c.GetAsync($"{e.HostUrl}/logout?location={Env.LocA}", TestContext.Current.CancellationToken);
        Assert.Empty(e.Host.Sessions);
        var revokedAt = e.Host.Timeline.Single(t => t.What == "auth.session.revoked").At;
        var to = r.Headers.Location!.ToString();
        Assert.StartsWith($"{e.Fake.BaseUrl}/{Env.TenantA:D}/oauth2/v2.0/logout", to);
        Assert.DoesNotContain("id_token_hint", to); Assert.DoesNotContain("logout_hint", to);
        await c.GetAsync(to, TestContext.Current.CancellationToken);
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
        var r = await c.GetAsync($"{e.HostUrl}/logout?location={Env.LocA}", TestContext.Current.CancellationToken);
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
        var c = (await e.Store.GetAsync(Env.LocA, TestContext.Current.CancellationToken))!;
        Assert.Null(EntraLocation.TryCreate(c, allowHttp: false, out var w1)); Assert.Equal("authority_not_https", w1);
        Assert.Null(EntraLocation.TryCreate(c with { EntraAllowedTenantIds = [Env.TenantA, Env.TenantB] }, true, out var w2)); Assert.Equal("tenant_count", w2);
        Assert.Null(EntraLocation.TryCreate(c with { OidcAuthority = "http://example.invalid" }, true, out var w3)); Assert.Equal("authority_not_https", w3);
        Assert.Null(EntraLocation.TryCreate(c with { SecretRef = null }, true, out var w4)); Assert.Equal("client_missing", w4);
        // D-51 fill: database locations get no authority, client id or secret ref; entra locations get the deployment's.
        var db = (await e.Store.GetAsync(Env.LocDb, TestContext.Current.CancellationToken))!;
        Assert.Null(db.OidcAuthority); Assert.Null(db.OidcClientId); Assert.Null(db.SecretRef);
        Assert.Equal(e.Fake.BaseUrl, c.OidcAuthority); Assert.Equal(e.DeploymentClientId, c.OidcClientId); Assert.Equal(Env.DeploymentRef, c.SecretRef!.Reveal());
        Assert.Equal("https://login.microsoftonline.com/" + Env.TenantA.ToString("D") + "/v2.0",
            EntraLocation.TryCreate(c with { OidcAuthority = "https://login.microsoftonline.com/" }, false, out _)!.Authority);
        var prod = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Production" });
        Assert.Throws<InvalidOperationException>(() => Microsoft.Extensions.DependencyInjection.AuthenticationServiceCollectionExtensions
            .AddAuthentication(prod.Services).AddEntraLocationSignIn(prod.Environment, o => o.AllowInsecureTestAuthority = true));
    }

    [Fact] public async Task ENT_CFG_01b_unresolvable_secret_fails_closed()
    {
        await using var e = await Env.StartAsync();
        e.Store.Mutate(d => d.Secrets.Remove(Env.DeploymentRef)); // the deployment ref no longer resolves
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus); Assert.Empty(e.Host.Sessions);
        Assert.True(e.LogHas("detail=secret_unresolved"));
    }

    // TLD 10:12: Login.Entra caches nothing per location. A deployment ref/secret change (in production: a restart,
    // since SSE's reader snapshots EntraDeployment at startup) must reach the very next request's resolver call.
    [Fact] public async Task ENT_CFG_03_deployment_secret_ref_change_reaches_next_request()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        Assert.Equal(Env.DeploymentRef, e.Store.LastResolvedRef);
        const string ref2 = "arn:sse2-fake-test-only:secret:rotated-9q7z";
        var sec2 = Env.NewFakeSecret();
        e.Store.Mutate(d => { d.Secrets.Remove(Env.DeploymentRef); d.Secrets[ref2] = sec2; d.Deployment.SecretRef = ref2; });
        e.Fake.ClientSecrets[e.DeploymentClientId] = sec2; // the old secret is now invalid_client at the fake
        e.Host.Sessions.Clear();
        Signed(await e.SignInAsync("loc-a"), e);
        Assert.Equal(ref2, e.Store.LastResolvedRef);
        Assert.Equal(2, e.Store.ResolveCalls);
        Assert.Single(e.Host.Sessions);
        // F1 (TL, after SSE's 5f08d0d review): after the deployment values change, a database location still gets nulls.
        var ct = TestContext.Current.CancellationToken;
        var db = (await e.Store.GetAsync(Env.LocDb, ct))!;
        Assert.Null(db.OidcAuthority); Assert.Null(db.OidcClientId); Assert.Null(db.SecretRef);
        var dbBySlug = (await e.Store.GetBySlugAsync("loc-db", ct))!;
        Assert.Null(dbBySlug.OidcAuthority); Assert.Null(dbBySlug.OidcClientId); Assert.Null(dbBySlug.SecretRef);
    }

    // Same for the deployment client id: the next authorize and token requests use the new one.
    [Fact] public async Task ENT_CFG_03b_deployment_client_id_change_reaches_next_request()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        var cid2 = Guid.NewGuid().ToString("D");
        e.Fake.ClientSecrets[cid2] = e.Fake.ClientSecrets[e.DeploymentClientId];
        e.Fake.ClientSecrets.TryRemove(e.DeploymentClientId, out _);
        e.Store.Mutate(d => d.Deployment.ClientId = cid2);
        e.Host.Sessions.Clear();
        Signed(await e.SignInAsync("loc-a"), e);
        Assert.Equal(cid2, e.Fake.Log.Last(l => l.Endpoint == "authorize").Query["client_id"]);
        Assert.Equal(cid2, e.Fake.Log.Last(l => l.Endpoint == "token").Query["client_id"]);
    }

    // Same for the deployment authority: the next request uses the new (here unreachable) authority and falls back,
    // with no last-good copy of the old authority's metadata.
    [Fact] public async Task ENT_CFG_03c_deployment_authority_change_reaches_next_request()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        var hits = e.Fake.Log.Count;
        e.Store.Mutate(d => d.Deployment.Authority = "http://127.0.0.1:1"); // port 1: nothing listens (checked with ss)
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.OK, f.FinalStatus); Assert.Contains("FALLBACK", f.FinalBody);
        Assert.True(e.LogHas("fallback_reason=DiscoveryFailed fallback=used"), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra"))));
        Assert.Equal(hits, e.Fake.Log.Count); // the old authority was not contacted
    }

    // TLD 10:12: client_missing is the request-time backstop. An entra location with no deployment ref (or client id)
    // fails closed on the next request, even after an earlier success: no stale ref is reused, the resolver is not called.
    [Fact] public async Task ENT_CFG_04_missing_deployment_ref_is_client_missing_at_request_time()
    {
        await using var e = await Env.StartAsync();
        Signed(await e.SignInAsync("loc-a"), e);
        e.Host.Sessions.Clear();
        var hits = e.Fake.Log.Count; var calls = e.Store.ResolveCalls;
        e.Store.Mutate(d => d.Deployment.SecretRef = null);
        var f = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, f.FinalStatus); Assert.DoesNotContain("<form", f.FinalBody);
        Assert.Empty(e.Host.Sessions); Assert.Single(f.Hops);
        Assert.True(e.LogHas($"entra.config_invalid location={Env.LocA} why=client_missing"), string.Join("\n", e.Logs.Lines.Where(l => l.Contains("entra"))));
        Assert.Equal(hits, e.Fake.Log.Count); Assert.Equal(calls, e.Store.ResolveCalls);
        // ref removed between challenge and callback: MessageReceived re-reads config and refuses before the resolver
        e.Store.Mutate(d => d.Deployment.SecretRef = Env.DeploymentRef);
        var g = await e.SignInAsync("loc-a", cb => { e.Store.Mutate(d => d.Deployment.SecretRef = null); return cb; });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, g.FinalStatus); Assert.DoesNotContain("<form", g.FinalBody); Assert.Empty(e.Host.Sessions);
        Assert.True(e.LogHas("detail=config:client_missing")); // config:* refusals are logged (v1_reason=(no audit)), not audited
        Assert.Equal(calls, e.Store.ResolveCalls);
        // a blank deployment client id is the same backstop
        e.Store.Mutate(d => { d.Deployment.SecretRef = Env.DeploymentRef; d.Deployment.ClientId = " "; });
        var h = await e.SignInAsync("loc-a");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, h.FinalStatus); Assert.Single(h.Hops);
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
            { ["grant_type"] = "authorization_code", ["code"] = code!, ["code_verifier"] = "wrong", ["client_id"] = "x" }), TestContext.Current.CancellationToken);
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
