using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cachd.Login.TestKit;

namespace Cachd.Login.SecurityTests;

/// <summary>
/// PKJ01-PKJ12: private_key_jwt client authentication (contract 7.6) against feat/si1-standin 8aab98a (first seen 9a14163) (Clients.cs 0a04ca99),
/// SSE2 Mon Nov 2 2026 ~9:50 PM PT. Keys: SSE's FIXTURE RSA keys, generated per start/reset into $STANDIN_CLIENT_KEY_DIR
/// under /tmp (B2 [TL Decision 9:16 PM PT]); read via LOGIN_STANDIN_CLIENT_KEY_DIR. Every negative must be 401 invalid_client.
/// The probe uses the revoke endpoint (7.6: "token, revoke and introspect") so no grant is consumed.
/// </summary>
[Trait("check", "pkjwt-standin"), Trait("target", "standin-only")]
public class StandInPrivateKeyJwtTests
{
    const string Type = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    static async Task<long> Ready()
    {
        var r = StandIn.SkipReason(); if (r is not null) Assert.Skip(r);
        if (StandIn.ClientKeyDir is null || !File.Exists(Path.Combine(StandIn.ClientKeyDir, "backoffice_stub.FIXTURE.key.pem")))
            Assert.Skip("PENDING OQ-SSE2-53: no FIXTURE client keys (needs feat/si1-standin >= 9a14163 and LOGIN_STANDIN_CLIENT_KEY_DIR)");
        await StandIn.Reset(); // keys are regenerated on reset (SI1 B2)
        return DateTimeOffset.Parse(await StandIn.Now()).ToUnixTimeSeconds();
    }
    static async Task<string> Probe(string assertion, string? clientIdField = null, (string, string)? basic = null)
    {
        var f = new Dictionary<string, string> { ["token"] = "not-a-token", ["client_assertion_type"] = Type, ["client_assertion"] = assertion };
        if (clientIdField is not null) f["client_id"] = clientIdField;
        var (st, body) = await StandIn.BackPostRaw("revoke", f, basic);
        return $"{(int)st} {(string?)body?["error"] ?? ""}".Trim();
    }
    const string Bad = "401 invalid_client";

    [Fact] // positive control: without it every negative passes on a broken implementation
    public async Task PKJ01_valid_assertion_is_accepted_on_revoke_and_token()
    {
        var now = await Ready();
        Assert.Equal("200", await Probe(StandIn.Assertion("backoffice_stub", nowUnix: now)));
        Assert.Equal("200", await Probe(StandIn.Assertion("cash_advance_bff", nowUnix: now), clientIdField: "cash_advance_bff"));
        // and on token: a refresh grant with a garbage token gets past client auth -> invalid_grant, not invalid_client
        var (st, body) = await StandIn.BackPostRaw("token", new Dictionary<string, string>
        { ["grant_type"] = "refresh_token", ["refresh_token"] = "x", ["client_assertion_type"] = Type, ["client_assertion"] = StandIn.Assertion("backoffice_stub", nowUnix: now) });
        Assert.Equal("400 invalid_grant", $"{(int)st} {(string?)body?["error"]}");
    }

    [Fact] // 7.6 alg pinned RS256 (RFC 8725 3.1)
    public async Task PKJ02_alg_none_and_hs256_keyed_with_public_key_are_refused()
    {
        var now = await Ready();
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", header: h => h["alg"] = "none", nowUnix: now, sign: _ => "")));
        using var k = StandIn.ClientKey("backoffice_stub");
        var pub = Encoding.ASCII.GetBytes(k.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", header: h => h["alg"] = "HS256", nowUnix: now,
            sign: si => StandIn.B64(HMACSHA256.HashData(pub, Encoding.ASCII.GetBytes(si))))));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", header: h => h["alg"] = "PS256", nowUnix: now)));
    }

    [Fact]
    public async Task PKJ03_wrong_key_or_another_clients_key_is_refused()
    {
        var now = await Ready();
        using var other = RSA.Create(2048);
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", key: other, nowUnix: now)));
        using var ca = StandIn.ClientKey("cash_advance_bff");
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", key: ca, nowUnix: now)));
    }

    [Fact] // 7.6 header allow-list: jwk / jku / x5c never trusted
    public async Task PKJ04_embedded_jwk_or_jku_header_is_refused()
    {
        var now = await Ready();
        using var mine = RSA.Create(2048);
        var p = mine.ExportParameters(false);
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", key: mine, nowUnix: now,
            header: h => h["jwk"] = new JsonObject { ["kty"] = "RSA", ["n"] = StandIn.B64(p.Modulus!), ["e"] = StandIn.B64(p.Exponent!) })));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", nowUnix: now, header: h => h["jku"] = "https://attacker.invalid/jwks")));
    }

    [Fact] // 7.6 iss = sub = client_id; client_id form field must agree
    public async Task PKJ05_iss_sub_client_id_mismatch_is_refused()
    {
        var now = await Ready();
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c["sub"] = "cash_advance_bff", nowUnix: now)));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", nowUnix: now), clientIdField: "cash_advance_bff"));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c.Remove("sub"), nowUnix: now)));
    }

    [Fact] // 7.6 aud = the issuer exactly
    public async Task PKJ06_aud_other_than_issuer_is_refused()
    {
        var now = await Ready();
        foreach (var aud in new JsonNode?[] { StandIn.BackEndpoint("token"), StandIn.Issuer + "/", "https://login.cachd.invalid", new JsonArray(StandIn.Issuer, "x"), null })
            Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => { if (aud is null) c.Remove("aud"); else c["aud"] = aud.DeepClone(); }, nowUnix: now)));
    }

    [Fact] // 7.6 jti required, single use until exp
    public async Task PKJ07_jti_missing_or_reused_is_refused()
    {
        var now = await Ready();
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c.Remove("jti"), nowUnix: now)));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c["jti"] = "", nowUnix: now)));
        var a = StandIn.Assertion("backoffice_stub", nowUnix: now);
        Assert.Equal("200", await Probe(a));
        Assert.Equal(Bad, await Probe(a));
        // same jti, new signature/iat: still a reuse
        var j = Guid.NewGuid().ToString("N");
        Assert.Equal("200", await Probe(StandIn.Assertion("backoffice_stub", c => c["jti"] = j, nowUnix: now)));
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => { c["jti"] = j; c["iat"] = now + 1; }, nowUnix: now)));
    }

    [Fact] // 7.6 exp - iat <= 60; G1 skew 30 s
    public async Task PKJ08_lifetime_and_skew_bounds()
    {
        var now = await Ready();
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c["exp"] = now + 61, nowUnix: now)));       // lifetime 61
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => { c["iat"] = now + 45; c["exp"] = now + 90; }, nowUnix: now))); // iat future > 30
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => { c["iat"] = now - 100; c["exp"] = now - 45; }, nowUnix: now))); // expired > skew
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", c => c["exp"] = now, nowUnix: now)));
        Assert.Equal("200", await Probe(StandIn.Assertion("backoffice_stub", c => { c["iat"] = now + 20; c["exp"] = now + 70; }, nowUnix: now))); // within skew
    }

    [Fact] // robustness: malformed claims are 401, never 500 (contract 7.x: errors are RFC 6749 JSON)
    public async Task PKJ09_missing_or_non_numeric_iat_exp_is_401_not_500()
    {
        var now = await Ready();
        foreach (Action<JsonObject> m in new Action<JsonObject>[] { c => c.Remove("iat"), c => c.Remove("exp"), c => c["iat"] = "soon", c => c["exp"] = 1.5e300 })
            Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", m, nowUnix: now)));
        Assert.Equal(Bad, await Probe("a.b.c"));
        Assert.Equal(Bad, await Probe("e30.e30"));
    }

    [Fact] // RFC 6749 2.3: one client authentication method per request
    public async Task PKJ10_basic_plus_assertion_is_refused()
    {
        var now = await Ready();
        Assert.Equal(Bad, await Probe(StandIn.Assertion("backoffice_stub", nowUnix: now), basic: ("backoffice_stub", (string)StandIn.Client("backoffice_stub")["secret"]!)));
    }

    [Fact] // 7.6 client_assertion_type must be jwt-bearer
    public async Task PKJ11_wrong_assertion_type_is_refused()
    {
        var now = await Ready();
        var (st, body) = await StandIn.BackPostRaw("revoke", new Dictionary<string, string>
        { ["token"] = "x", ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:saml2-bearer", ["client_assertion"] = StandIn.Assertion("backoffice_stub", nowUnix: now) });
        Assert.Equal(Bad, $"{(int)st} {(string?)body?["error"]}");
    }

    [Fact] // B2: keys regenerated at reset -> an assertion signed with the pre-reset key is refused
    public async Task PKJ12_key_rotates_on_reset()
    {
        var now = await Ready();
        using var before = StandIn.ClientKey("backoffice_stub");
        var a = StandIn.Assertion("backoffice_stub", key: before, nowUnix: now);
        await StandIn.Reset();
        Assert.Equal(Bad, await Probe(a));
    }
}
