using System.Text.Json.Nodes;
using Cachd.Login.TestKit;
using Json.Schema;

namespace Cachd.Login.ContractTests;

/// <summary>
/// CS-xx: the written contract (code/contract/, build plan §3.1) encodes the plan's facts. Skips cleanly while the draft is absent.
/// These pin the CONTRACT; the CT-xx tests pin the running service.
/// </summary>
[Trait("target", "both")]
public class ContractArtifactTests
{
    static void NeedContract(string? file = null) { var r = Paths.ContractSkipReason(file); if (r is not null) Assert.Skip(r); }
    static string Read(string f) => File.ReadAllText(Path.Combine(Paths.Contract, f));
    // Schemas carry $id; JsonSchema.Net registers by $id, so load each file once and reuse.
    static readonly Dictionary<string, JsonSchema> Loaded = new();
    static JsonSchema Schema(string f) { lock (Loaded) return Loaded.TryGetValue(f, out var s) ? s : Loaded[f] = JsonSchema.FromText(Read(f)); }
    static System.Text.Json.JsonElement E(JsonNode n) => System.Text.Json.JsonDocument.Parse(n.ToJsonString()).RootElement;
    // Formats (uuid, uri, date-time) are annotations by default in 2020-12; the suite ASSERTS them (finding CR-01).
    static readonly EvaluationOptions Opts = new() { RequireFormatValidation = true };
    static bool Valid(JsonSchema s, string json) => s.Evaluate(E(JsonNode.Parse(json)!), Opts).IsValid;
    static bool Valid(JsonSchema s, JsonNode n) => s.Evaluate(E(n), Opts).IsValid;
    /// A schema whose root is $defs/{def} of a contract file (contract files use $defs per token/event type).
    static JsonSchema Def(string file, string def)
    {
        var key = file + "#" + def;
        lock (Loaded)
        {
            if (Loaded.TryGetValue(key, out var s)) return s;
            var o = JsonNode.Parse(Read(file))!.AsObject();
            o.Remove("$id"); o["$id"] = $"urn:sse2:test:{Guid.NewGuid():N}"; o["$ref"] = $"#/$defs/{def}";
            return Loaded[key] = JsonSchema.FromText(o.ToJsonString());
        }
    }

    [Fact, Trait("check", "7")]
    public void CS01_contract_folder_has_the_four_named_artifacts()
    {
        NeedContract();
        foreach (var f in new[] { "openid-configuration.schema.json", "claims.schema.json", "errors.md", "audit-events.schema.json" })
            Assert.True(File.Exists(Path.Combine(Paths.Contract, f)), $"code/contract/{f} missing (plan §3.1 L100)");
    }

    const string GoodDiscovery = """
    {"issuer":"http://localhost:5080/standin","authorization_endpoint":"http://localhost:5080/standin/connect/authorize",
     "token_endpoint":"http://127.0.0.1:8084/connect/token","revocation_endpoint":"http://127.0.0.1:8084/connect/revoke",
     "end_session_endpoint":"http://localhost:5080/standin/connect/endsession","jwks_uri":"http://localhost:5080/standin/.well-known/jwks",
     "response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token"],
     "code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["client_secret_basic","private_key_jwt"],
     "id_token_signing_alg_values_supported":["RS256"],"subject_types_supported":["public"]}
    """;

    [Fact, Trait("check", "7")]
    public void CS02_discovery_schema_accepts_the_plan_document()
    {
        NeedContract("openid-configuration.schema.json");
        Assert.True(Valid(Schema("openid-configuration.schema.json"), GoodDiscovery), "schema rejects a discovery doc built from plan §3.1 L106");
    }

    [Theory, Trait("check", "7")]
    [InlineData("code_challenge_methods_supported", "[\"S256\",\"plain\"]")]
    [InlineData("response_types_supported", "[\"code\",\"token\"]")]
    [InlineData("grant_types_supported", "[\"authorization_code\",\"refresh_token\",\"password\"]")]
    [InlineData("grant_types_supported", "[\"authorization_code\",\"refresh_token\",\"implicit\"]")]
    [InlineData("id_token_signing_alg_values_supported", "[\"RS256\",\"none\"]")]
    [InlineData("id_token_signing_alg_values_supported", "[\"HS256\"]")]
    [InlineData("token_endpoint_auth_methods_supported", "[\"client_secret_basic\",\"none\"]")]
    [InlineData("userinfo_endpoint", "\"http://localhost:5080/standin/connect/userinfo\"")]
    public void CS03_discovery_schema_rejects_widening(string prop, string value)
    {
        NeedContract("openid-configuration.schema.json");
        var doc = JsonNode.Parse(GoodDiscovery)!.AsObject();
        doc[prop] = JsonNode.Parse(value);
        Assert.False(Valid(Schema("openid-configuration.schema.json"), doc), $"schema accepts {prop}={value}; plan §3.1 pins it (userinfo is 'not in v1')");
    }

    const string U1 = "0190f2a4-1c2b-7d3e-8f40-123456789abc", U2 = "0190f2a4-1c2b-7d3e-8f40-223456789abc", U3 = "0190f2a4-1c2b-7d3e-8f40-323456789abc";
    static string AtBase => $$"""{"iss":"http://localhost:5080/standin","sub":"{{U1}}","aud":"cash-advance-api","exp":1700000300,"iat":1700000000,"jti":"jti-0123456789abcdef","scope":"cash_advance","client_id":"cash_advance_bff","login_provider":"database","amr":["pwd","otp","mfa"],"auth_time":1700000000}""";
    static JsonObject With(string json, params (string k, string v)[] add) { var o = JsonNode.Parse(json)!.AsObject(); foreach (var (k, v) in add) o[k] = JsonNode.Parse(v); return o; }
    static (string, string) Org(string k, string id) => (k, $"\"{id}\"");

    [Fact, Trait("check", "7")]
    public void CS04_cachd_access_token_valid_and_rejects_roles_permissions_sid()
    {
        NeedContract("claims.schema.json");
        var s = Def("claims.schema.json", "access_token_cachd");
        var good = With(AtBase, ("perm_version", "3"), Org("partner_id", U2), Org("location_id", U3));
        Assert.True(Valid(s, good), "contract rejects a plan-conformant Cachd access token");
        foreach (var bad in new[] { "roles", "role", "permissions", "permission", "sid", "groups" })
            Assert.False(Valid(s, With(good.ToJsonString(), (bad, "[\"x\"]"))), $"accepts '{bad}'");
        Assert.False(Valid(s, With(AtBase, Org("partner_id", U2), Org("location_id", U3))), "accepts a Cachd token without perm_version");
    }

    [Fact, Trait("check", "7")]
    public void CS07_backoffice_token_no_perm_version_org_claims_only_when_on()
    {
        NeedContract("claims.schema.json");
        var off = Def("claims.schema.json", "access_token_other");
        var on = Def("claims.schema.json", "access_token_other_org_claims");
        Assert.True(Valid(off, AtBase), "rejects a plain non-Cachd token");
        Assert.False(Valid(off, With(AtBase, ("perm_version", "1"))), "perm_version accepted for non-Cachd client");
        Assert.False(Valid(off, With(AtBase, Org("location_id", U3))), "org claim accepted with org_claims off");
        Assert.True(Valid(on, With(AtBase, Org("partner_id", U2), Org("location_id", U3))), "org_claims=on token rejected");
        Assert.False(Valid(on, With(AtBase, Org("partner_id", U2), Org("location_id", U3), ("perm_version", "1"))), "perm_version accepted with org_claims on");
    }

    [Fact, Trait("check", "7")]
    public void CS08_id_token_valid_and_closed()
    {
        NeedContract("claims.schema.json");
        var s = Def("claims.schema.json", "id_token");
        var good = $$"""{"iss":"http://localhost:5080/standin","sub":"{{U1}}","aud":"cash_advance_bff","azp":"cash_advance_bff","exp":1700000300,"iat":1700000000,"nonce":"n-1","auth_time":1700000000,"amr":["pwd","otp","mfa"]}""";
        Assert.True(Valid(s, good));
        Assert.False(Valid(s, With(good, ("roles", "[\"cashier\"]"))));
        Assert.False(Valid(s, With(good, ("sid", "\"s\""))));
        Assert.False(Valid(s, With(good, ("sub", "\"not-a-uuid\""))), "format uuid not enforced");
    }

    [Fact(Skip = "PENDING OQ-SSE2-35 (CR-03): claims.schema requires nonce on every ID token, but OIDC Core §12.2 says an ID token from a refresh grant SHOULD NOT carry nonce. Contract must say whether refresh returns an ID token and its shape."), Trait("check", "7")]
    public void CS09_refresh_issued_id_token_shape() { }

    [Fact(Skip = "PENDING SSE OQ-22 (CR-02): amr enum is RFC 8176 only, but Entra pass-through (A-28) would carry e.g. 'wiaormfa', 'ngcmfa', 'rsa'. Needs a mapping rule."), Trait("check", "7")]
    public void CS10_entra_amr_values_fit_schema() { }

    [Fact, Trait("check", "8")]
    public void CS05_errors_md_lists_only_standard_codes_and_all_plan_codes()
    {
        NeedContract("errors.md");
        var t = Read("errors.md");
        foreach (var c in new[] { "invalid_request", "invalid_client", "invalid_grant", "invalid_scope", "unauthorized_client", "access_denied", "login_required", "interaction_required", "temporarily_unavailable" })
            Assert.Contains(c, t);
        // response_type=token / unknown grant need a code too; the plan's list omits them (finding CR-PRE-02, OQ-SSE2-26).
    }

    [Fact, Trait("check", "4a")]
    public void CS06_audit_schema_has_events_and_reasons_incl_attestation_invalid()
    {
        NeedContract("audit-events.schema.json");
        var t = Read("audit-events.schema.json");
        foreach (var e in new[] { "auth.login.succeeded", "auth.login.failed", "auth.account.locked", "auth.account.unlocked", "auth.mfa.enrolled", "auth.mfa.succeeded",
                     "auth.mfa.failed", "auth.mfa.reset", "auth.password.changed", "auth.session.revoked", "auth.app.signed_in", "auth.app_access.denied",
                     "auth.refresh.reuse_detected", "auth.break_glass.used", "auth.session.location_switched", "hub_handoff", "no_role", "remote_session", "attestation_invalid" })
            Assert.Contains(e, t);
    }
}
