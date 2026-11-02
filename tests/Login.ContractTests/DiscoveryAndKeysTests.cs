using System.Net;
using System.Text.Json;
using Cachd.Login.TestKit;

namespace Cachd.Login.ContractTests;

/// <summary>CT-01..04: live discovery / JWKS / listener split. Run against LOGIN_CONTRACT_TARGET; skip with a reason when no target.</summary>
[Trait("target", "both")]
public class DiscoveryAndKeysTests
{
    static void NeedLive(bool back = false) { var r = Target.LiveSkipReason(back); if (r is not null) Assert.Skip(r); }

    static async Task<JsonElement> GetJson(string url)
    {
        using var h = Target.NewClient();
        var r = await h.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
    }

    // Discovery path relative to the issuer is not stated (issuer has a /standin path): try issuer-relative first (OQ-SSE2-03).
    static async Task<JsonElement> Discovery()
    {
        using var h = Target.NewClient();
        // Listener for discovery was SSE OQ-1 (front vs 8084); SSE reports TL moved it to front (pending re-check). Try front, then back.
        var bases = new[] { Target.FrontUrl, Target.BackUrl }.Where(b => b is not null);
        foreach (var u in bases.SelectMany(b => new[] { b + "/standin/.well-known/openid-configuration", b + "/.well-known/openid-configuration" }))
        {
            var r = await h.GetAsync(u, TestContext.Current.CancellationToken);
            if (r.StatusCode == HttpStatusCode.OK) return JsonDocument.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        }
        Assert.Fail("no discovery document under LOGIN_FRONT_URL or LOGIN_BACK_URL (with or without /standin)");
        return default;
    }

    static string[] Arr(JsonElement d, string p) => d.GetProperty(p).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact, Trait("check", "7")]
    public async Task CT01_discovery_matches_plan_exactly()
    {
        NeedLive();
        var d = await Discovery();
        foreach (var p in new[] { "issuer", "authorization_endpoint", "token_endpoint", "revocation_endpoint", "end_session_endpoint", "jwks_uri" })
            Assert.True(d.TryGetProperty(p, out _), $"discovery lacks {p}");
        Assert.Equal(new[] { "code" }, Arr(d, "response_types_supported"));
        Assert.Equal(new[] { "authorization_code", "refresh_token" }, Arr(d, "grant_types_supported").Order().ToArray());
        Assert.Equal(new[] { "S256" }, Arr(d, "code_challenge_methods_supported"));
        Assert.Equal(new[] { "client_secret_basic", "private_key_jwt" }, Arr(d, "token_endpoint_auth_methods_supported").Order().ToArray());
        Assert.Equal(new[] { "RS256" }, Arr(d, "id_token_signing_alg_values_supported"));
        Assert.False(d.TryGetProperty("userinfo_endpoint", out _), "userinfo is not in v1");
    }

    [Fact, Trait("check", "11")]
    public async Task CT02_issuer_marks_standin_and_only_standin()
    {
        NeedLive();
        var iss = (await Discovery()).GetProperty("issuer").GetString()!;
        if (Target.IsStandIn) Assert.Contains("/standin", iss);
        else Assert.DoesNotContain("/standin", iss);
    }

    [Fact, Trait("check", "8")]
    public async Task CT03_jwks_public_only_with_kid_and_standin_prefix()
    {
        NeedLive();
        var jwks = await GetJson((await Discovery()).GetProperty("jwks_uri").GetString()!);
        var keys = jwks.GetProperty("keys").EnumerateArray().ToArray();
        Assert.NotEmpty(keys);
        foreach (var k in keys)
        {
            foreach (var priv in new[] { "d", "p", "q", "dp", "dq", "qi", "k" })
                Assert.False(k.TryGetProperty(priv, out _), $"JWKS key exposes private member '{priv}'");
            Assert.True(k.TryGetProperty("kid", out var kid) && !string.IsNullOrEmpty(kid.GetString()), "key without kid");
            if (Target.IsStandIn) Assert.StartsWith("standin-", kid.GetString());
            else Assert.DoesNotContain("standin", kid.GetString());
        }
    }

    [Theory, Trait("check", "8")]
    [InlineData("/connect/token")]
    [InlineData("/connect/revoke")]
    [InlineData("/connect/introspect")]
    public async Task CT04_back_channel_endpoints_are_not_served_on_front(string path)
    {
        NeedLive();
        using var h = Target.NewClient();
        foreach (var prefix in new[] { "", "/standin" })
        {
            var r = await h.PostAsync(Target.FrontUrl + prefix + path, new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = "x" }), TestContext.Current.CancellationToken);
            var body = await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            // Exact status for "not here" is unstated (404 vs 403): assert only that the endpoint did not process the request.
            Assert.False(r.IsSuccessStatusCode, $"{prefix}{path} answered {(int)r.StatusCode} on the front listener");
            Assert.DoesNotContain("\"error\"", body);
        }
    }
}
