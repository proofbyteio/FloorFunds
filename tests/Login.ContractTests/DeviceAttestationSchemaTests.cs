using System.Text.Json.Nodes;
using Cachd.Login.TestKit;
using Json.Schema;

namespace Cachd.Login.ContractTests;

/// <summary>AT-xx: device-attestation.schema.json (contract 6e8a106 §6.4-6.8). Header/claims shape only; server checks are SEC1x.</summary>
[Trait("check", "4a")]
public class DeviceAttestationSchemaTests
{
    static JsonSchema? s;
    static readonly EvaluationOptions Opts = new() { RequireFormatValidation = true };
    static JsonSchema S()
    {
        var f = Path.Combine(Paths.Contract, "device-attestation.schema.json");
        if (!File.Exists(f)) Assert.Skip("contract/device-attestation.schema.json not present (needs contract >= 6b89c57)");
        return s ??= JsonSchema.FromText(File.ReadAllText(f));
    }
    static bool V(string json) => S().Evaluate(System.Text.Json.JsonDocument.Parse(json).RootElement, Opts).IsValid;
    const string Claims = """{"nonce":"n0123456789abcdef","device_id":"standin-pc-1","remote_session":false,"iat":1700000000,"exp":1700000060}""";
    static string J(string header, string claims = Claims) => $$"""{"header":{{header}},"claims":{{claims}}}""";

    [Fact] public void AT01_valid_shape() => Assert.True(V(J("""{"alg":"ES256","kid":"standin-pc-1","typ":"cachd-attestation+jwt"}""")));

    [Theory]
    [InlineData("none")] [InlineData("HS256")] [InlineData("RS256")] [InlineData("ES384")] [InlineData("es256")]
    public void AT02_alg_other_than_ES256_rejected(string alg) => Assert.False(V(J($$"""{"alg":"{{alg}}","kid":"standin-pc-1"}""")));

    [Theory]
    [InlineData("jwk", "{}")] [InlineData("jku", "\"https://x\"")] [InlineData("x5u", "\"https://x\"")] [InlineData("x5c", "[]")]
    public void AT03_embedded_or_remote_key_refs_rejected(string k, string v) => Assert.False(V(J($$"""{"alg":"ES256","kid":"standin-pc-1","{{k}}":{{v}}}""")));

    [Fact] public void AT04_missing_kid_rejected() => Assert.False(V(J("""{"alg":"ES256"}""")));

    [Theory]
    [InlineData("nonce")] [InlineData("device_id")] [InlineData("remote_session")] [InlineData("iat")] [InlineData("exp")]
    public void AT05_each_required_claim(string drop)
    {
        var c = JsonNode.Parse(Claims)!.AsObject(); c.Remove(drop);
        Assert.False(V(J("""{"alg":"ES256","kid":"standin-pc-1"}""", c.ToJsonString())));
    }

    [Fact] public void AT06_extra_claim_rejected() => Assert.False(V(J("""{"alg":"ES256","kid":"standin-pc-1"}""", Claims.TrimEnd('}') + ""","jti":"x"}""")));
    [Fact] public void AT07_short_nonce_rejected() => Assert.False(V(J("""{"alg":"ES256","kid":"standin-pc-1"}""", Claims.Replace("n0123456789abcdef", "short"))));
    [Fact] public void AT08_remote_session_must_be_boolean() => Assert.False(V(J("""{"alg":"ES256","kid":"standin-pc-1"}""", Claims.Replace("false", "\"false\""))));

    [Fact(Skip = "PENDING CR-21 / OQ-35: 'crit' and unknown header params are not refused (header has no additionalProperties:false); typ is optional. Proposal: allowlist alg/kid/typ, typ required.")]
    public void AT09_crit_and_unknown_header_params_rejected() { }

    [Fact(Skip = "PENDING CR-20 / OQ-35: no aud/purpose claim; an attestation minted for a plain sign-in nonce vs a hand-off nonce vs a cage-action verifier is distinguished only by server-side nonce state. Needs TL decision.")]
    public void AT10_attestation_bound_to_purpose() { }
}
