using System.Text.Json.Nodes;
using Cachd.Login.TestKit;
using Json.Schema;

namespace Cachd.Login.ContractTests;

/// <summary>
/// AT-xx: device-attestation.schema.json shape (contract §6.5-6.8; TL rulings: aud/purpose addendum 12:15 PM, correction 8:14 PM
/// (typ required), Addendum 2 CR-21 (header additionalProperties:false)). Server checks are SEC1x.
/// Every negative case starts from a fully VALID token and changes one thing, so a rejection is for the stated reason
/// (the 12:19 PM version of this file lacked aud/purpose/typ and would have passed for the wrong reason against v1-d3).
/// </summary>
[Trait("check", "4a")]
public class DeviceAttestationSchemaTests
{
    static JsonSchema? s;
    static readonly EvaluationOptions Opts = new() { RequireFormatValidation = true };
    static JsonSchema S()
    {
        var f = Path.Combine(Paths.Contract, "device-attestation.schema.json");
        if (!File.Exists(f)) Assert.Skip("contract/device-attestation.schema.json not present");
        return s ??= JsonSchema.FromText(File.ReadAllText(f));
    }
    const string Iss = "https://login.standin.test";
    static JsonObject H() => new() { ["alg"] = "ES256", ["kid"] = "standin-pc-1", ["typ"] = "cachd-attestation+jwt" };
    static JsonObject C(string purpose = "signin", string aud = Iss) => new()
    {
        ["nonce"] = "n0123456789abcdef", ["device_id"] = "standin-pc-1", ["remote_session"] = false,
        ["iat"] = 1700000000, ["exp"] = 1700000060, ["aud"] = aud, ["purpose"] = purpose,
    };
    static bool V(JsonObject h, JsonObject c) => S().Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(new JsonObject { ["header"] = h, ["claims"] = c }), Opts).IsValid;
    static JsonObject With(JsonObject o, string k, JsonNode? v) { o[k] = v; return o; }
    static JsonObject Without(JsonObject o, string k) { o.Remove(k); return o; }

    [Theory]
    [InlineData("signin")] [InlineData("handoff")] [InlineData("cage_action")]
    public void AT01_valid_token_for_each_purpose(string purpose) => Assert.True(V(H(), C(purpose)));

    [Theory]
    [InlineData("none")] [InlineData("HS256")] [InlineData("RS256")] [InlineData("ES384")] [InlineData("es256")]
    public void AT02_alg_other_than_ES256_rejected(string alg) => Assert.False(V(With(H(), "alg", alg), C()));

    [Theory]
    [InlineData("jwk", "{}")] [InlineData("jku", "\"https://x\"")] [InlineData("x5u", "\"https://x\"")] [InlineData("x5c", "[]")] [InlineData("crit", "[\"exp\"]")]
    public void AT03_embedded_or_remote_key_refs_and_crit_rejected(string k, string v) => Assert.False(V(With(H(), k, JsonNode.Parse(v)), C()));

    [Theory]
    [InlineData("kid")] [InlineData("typ")] [InlineData("alg")]
    public void AT04_missing_header_member_rejected(string k) => Assert.False(V(Without(H(), k), C()));

    [Fact] public void AT04b_wrong_typ_rejected() => Assert.False(V(With(H(), "typ", "JWT"), C()));

    [Theory]
    [InlineData("nonce")] [InlineData("device_id")] [InlineData("remote_session")] [InlineData("iat")] [InlineData("exp")] [InlineData("aud")] [InlineData("purpose")]
    public void AT05_each_required_claim(string drop) => Assert.False(V(H(), Without(C(), drop)));

    [Fact] public void AT06_extra_claim_rejected() => Assert.False(V(H(), With(C(), "jti", "x")));
    [Fact] public void AT07_short_nonce_rejected() => Assert.False(V(H(), With(C(), "nonce", "short")));
    [Fact] public void AT08_remote_session_must_be_boolean() => Assert.False(V(H(), With(C(), "remote_session", "false")));

    /// CR-21 (Addendum 2): header allows only {alg, kid, typ}. Fails until SSE adds additionalProperties:false.
    [Theory, Trait("ruling", "CR-21")]
    [InlineData("cty", "\"JWT\"")] [InlineData("x-extra", "1")] [InlineData("b64", "false")] [InlineData("zip", "\"DEF\"")]
    public void AT09_unknown_header_params_rejected(string k, string v) => Assert.False(V(With(H(), k, JsonNode.Parse(v)), C()));

    [Theory]
    [InlineData("admin")] [InlineData("SIGNIN")] [InlineData("")]
    public void AT10_purpose_outside_enum_rejected(string p) => Assert.False(V(H(), C(p)));

    [Fact] public void AT11_aud_must_be_a_single_string() => Assert.False(V(H(), With(C(), "aud", new JsonArray(Iss))));
}
