using System.Text.Json;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>KIT-xx: the helpers the live tests depend on are themselves correct. Run now.</summary>
public class TestKitSelfTests
{
    [Fact]
    public void KIT01_pkce_s256_matches_rfc7636_appendix_b()
        => Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.S256("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void KIT02_verifier_is_43_chars_base64url()
    {
        var v = Pkce.NewVerifier();
        Assert.Equal(43, v.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
    }

    static JsonElement P(string json) => JsonDocument.Parse(json).RootElement;
    const string Good = """{"iss":"i","sub":"s","aud":"a","exp":2,"iat":1,"jti":"j","scope":"x","client_id":"c","login_provider":"database","amr":["pwd","mfa"],"auth_time":1}""";

    [Fact]
    public void KIT03_claim_rules_accept_good_access_token()
        => Assert.Empty(ClaimRules.Violations(P(Good), ClaimRules.AccessTokenRequired, cachdClient: false, orgClaimsOn: false));

    [Theory]
    [InlineData("\"roles\":[\"cashier\"]", "forbidden claim 'roles'")]
    [InlineData("\"permissions\":[\"x\"]", "forbidden claim 'permissions'")]
    [InlineData("\"sid\":\"s\"", "forbidden claim 'sid'")]
    [InlineData("\"perm_version\":3", "perm_version present on a non-Cachd client")]
    [InlineData("\"location_id\":\"L\"", "org claim 'location_id' present while org_claims is off")]
    public void KIT04_claim_rules_flag_violations(string extra, string expected)
    {
        var v = ClaimRules.Violations(P(Good.TrimEnd('}') + "," + extra + "}"), ClaimRules.AccessTokenRequired, false, false).ToList();
        Assert.Contains(v, s => s.Contains(expected));
    }

    [Fact]
    public void KIT05_claim_rules_flag_missing_required()
        => Assert.Contains("missing required claim 'auth_time'", ClaimRules.Violations(P("""{"iss":"i"}"""), ClaimRules.AccessTokenRequired, true, false));

    [Fact]
    public void KIT06_jwt_parse_roundtrip()
    {
        var h = Pkce.B64Url("""{"alg":"RS256","kid":"standin-1"}"""u8.ToArray());
        var p = Pkce.B64Url("""{"x_standin":true}"""u8.ToArray());
        var j = JwtParts.Parse($"{h}.{p}.sig");
        Assert.Equal("standin-1", j.Header.GetProperty("kid").GetString());
        Assert.True(j.Has("x_standin"));
    }

    [Fact]
    public void KIT07_mann_whitney_separates_shifted_samples_and_not_identical_ones()
    {
        var r = new Random(7);
        var a = Enumerable.Range(0, 60).Select(_ => 100 + r.NextDouble() * 10).ToList();
        var b = Enumerable.Range(0, 60).Select(_ => 130 + r.NextDouble() * 10).ToList();
        var c = Enumerable.Range(0, 60).Select(_ => 100 + r.NextDouble() * 10).ToList();
        Assert.True(Timing.MannWhitneyP(a, b) < 0.001);
        Assert.True(Timing.MannWhitneyP(a, c) > 0.01);
        Assert.Equal(2.5, Timing.Median(new double[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void KIT08_target_skip_reason_is_explicit_when_unset()
    {
        if (Target.Name is not null) Assert.Skip("LOGIN_CONTRACT_TARGET is set in this run; self-test only meaningful when unset.");
        Assert.Contains("LOGIN_CONTRACT_TARGET is not set", Target.LiveSkipReason());
    }
}
