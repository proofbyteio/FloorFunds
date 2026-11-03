using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cachd.Login.TestKit;
using Json.Schema;

namespace Cachd.Login.ContractTests;

/// <summary>
/// PF: pre-freeze adversarial schema pass (TL 8:37-8:43 PM PT). session_kind (7.8, DEC-085/087), admin_type (8.12), location mapping
/// (8.11/8.13), re-enrolment events (8.14), cap change (7.4/8.15, DEC-086), change_denied. Every fixture is built EXPLICITLY here with
/// no builder defaults; each negative breaks one field of a baseline that is itself asserted valid.
/// </summary>
[Trait("target", "both"), Trait("check", "7")]
public class PreFreezeSchemaTests
{
    const string P = "0190f2a4-1c2b-7d3e-8f40-423456789abc", L = "0190f2a4-1c2b-7d3e-8f40-523456789abc", U = "0190f2a4-1c2b-7d3e-8f40-623456789abc", L2 = "0190f2a4-1c2b-7d3e-8f40-723456789abc";
    const string C = "claims.schema.json", A = "audit-events.schema.json";
    static readonly EvaluationOptions Opts = new() { RequireFormatValidation = true };
    static readonly Dictionary<string, JsonSchema> Cache = new();
    static string Read(string f) => File.ReadAllText(Path.Combine(Paths.Contract, f));
    static void Need(string f) { var r = Paths.ContractSkipReason(f); if (r is not null) Assert.Skip(r); }
    static JsonSchema S(string file, string? def)
    {
        var key = file + "#" + def;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var s)) return s;
            var o = JsonNode.Parse(Read(file))!.AsObject();
            o.Remove("$id"); o["$id"] = $"urn:sse2:pf:{Guid.NewGuid():N}";
            if (def is not null) o["$ref"] = $"#/$defs/{def}";
            return Cache[key] = JsonSchema.FromText(o.ToJsonString());
        }
    }
    static bool Valid(string file, string? def, JsonNode n) => S(file, def).Evaluate(System.Text.Json.JsonDocument.Parse(n.ToJsonString()).RootElement, Opts).IsValid;
    static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();
    static JsonObject Set(JsonObject o, string path, string? json)
    {
        var c = J(o.ToJsonString()); var parts = path.Split('.'); var cur = c;
        for (int i = 0; i < parts.Length - 1; i++) cur = cur[parts[i]]!.AsObject();
        if (json is null) cur.Remove(parts[^1]); else cur[parts[^1]] = JsonNode.Parse(json);
        return c;
    }

    static JsonObject Tok => J($$"""{"session_kind":"normal","iss":"http://localhost:5080/standin","sub":"{{U}}","aud":"cash-advance-api","exp":1300,"iat":1000,"jti":"jti-0123456789abcdef","scope":"cash_advance_api","client_id":"cash_advance_bff","login_provider":"database","amr":["pwd","otp","mfa"],"auth_time":990,"perm_version":3,"partner_id":"{{P}}","location_id":"{{L}}"}""");
    static JsonObject AdmPartner => J($$"""{"session_kind":"normal","iss":"http://localhost:5080/standin","sub":"{{U}}","aud":"backoffice-api","exp":1300,"iat":1000,"jti":"jti-0123456789abcdef","scope":"backoffice_api","client_id":"backoffice_bff","login_provider":"database","amr":["pwd","otp","mfa"],"auth_time":990,"admin_type":"partner","partner_id":"{{P}}"}""");
    static JsonObject AdmSuper => Set(Set(AdmPartner, "partner_id", null), "admin_type", "\"super\"");
    static JsonObject IdTok => J($$"""{"session_kind":"normal","iss":"http://localhost:5080/standin","sub":"{{U}}","aud":"cash_advance_bff","azp":"cash_advance_bff","exp":1300,"iat":1000,"nonce":"n-1","auth_time":990,"amr":["pwd","otp","mfa"]}""");
    static JsonObject Ev(string type, string outcome, string details, string extra) => J($$"""{"event_id":"{{U}}","schema":"cachd.log","schema_version":"1.0.0","category":"event","event_type":"{{type}}","occurred_at":"2026-11-02T19:40:00Z","outcome":"{{outcome}}","is_audit":true,"actor":{"user_id":"{{U}}","session_id":"ls1"},"source":{"service":"login-standin"},"details":{{details}}{{extra}}}""");
    static string Scope1 => $$""","scope":{"partner_id":"{{P}}","location_ids":["{{L}}"]}""";
    static string TUser => $$""","target":{"type":"user","id":"{{U}}"}""";
    static JsonObject Login => Ev("auth.login.succeeded", "success", """{"provider_kind":"database","amr":["pwd"]}""", Scope1);
    static JsonObject SignedIn => Ev("auth.app.signed_in", "success", """{"method":"hub_handoff","auth_time":1,"amr":["pwd","mfa"]}""", Scope1 + ""","target":{"type":"app","id":"cash_advance"},"action":"login","session_kind":"normal" """);
    static JsonObject Denied(string reason, string extra) => Ev("auth.app_access.denied", "denied", $$"""{"reason":"{{reason}}"}""", Scope1 + ""","target":{"type":"app","id":"cash_advance"}""" + extra);
    static JsonObject Reenrolled => Ev("auth.mfa.reenrolled", "success", """{"method":"totp","old_factor_revoked":true,"old_recovery_codes_revoked":true}""", Scope1 + TUser + ""","session_kind":"recovery_reenroll" """);
    static JsonObject Codes => Ev("auth.mfa.recovery_codes_issued", "success", """{"count":10}""", Scope1 + TUser + ""","session_kind":"recovery_reenroll" """);
    static JsonObject MfaFail => Ev("auth.mfa.failed", "failure", """{"method":"totp","step":"reenroll"}""", Scope1 + TUser + ""","session_kind":"recovery_reenroll" """);
    static JsonObject Revoked => Ev("auth.session.revoked", "success", """{"reason":"reenrolled"}""", Scope1 + ""","session_kind":"recovery_reenroll" """);
    const string Cfg = ""","target":{"type":"config","id":"LoginService:Session:AbsoluteLifetime"}""";
    static JsonObject CapChanged => Ev("auth.config.session_cap_changed", "success", """{"old_value":"12:00:00","new_value":"08:00:00"}""", Cfg + $$""","admin_type":"partner","scope":{"partner_id":"{{P}}","location_ids":[]}""");
    static JsonObject ChangeDenied => Ev("auth.config.change_denied", "denied", """{"reason":"not_admin"}""", Cfg + Scope1);

    static readonly (string id, string file, string? def, Func<JsonObject> doc, bool valid)[] All =
    {
        ("SK01 cachd token baseline", C, "access_token_cachd", () => Tok, true),
        ("SK02 cachd token without session_kind", C, "access_token_cachd", () => Set(Tok, "session_kind", null), false),
        ("SK02 admin token without session_kind", C, "access_token_admin", () => Set(AdmSuper, "session_kind", null), false),
        ("SK02 id token without session_kind", C, "id_token_code", () => Set(IdTok, "session_kind", null), false),
        ("SK03 session_kind recovery", C, "access_token_cachd", () => Set(Tok, "session_kind", "\"recovery\""), false),
        ("SK03 session_kind empty", C, "access_token_cachd", () => Set(Tok, "session_kind", "\"\""), false),
        ("SK03 session_kind NORMAL", C, "access_token_cachd", () => Set(Tok, "session_kind", "\"NORMAL\""), false),
        ("SK03 session_kind null", C, "access_token_cachd", () => Set(Tok, "session_kind", "null"), false),
        ("SK03 session_kind number", C, "access_token_cachd", () => Set(Tok, "session_kind", "1"), false),
        ("SK03 session_kind array", C, "access_token_cachd", () => Set(Tok, "session_kind", "[\"normal\"]"), false),
        ("SK04 recovery_reenroll app token", C, "access_token_cachd", () => Set(Tok, "session_kind", "\"recovery_reenroll\""), false),
        ("SK04 recovery_reenroll admin token", C, "access_token_admin", () => Set(AdmPartner, "session_kind", "\"recovery_reenroll\""), false),
        ("SK04 recovery_reenroll id token", C, "id_token_code", () => Set(IdTok, "session_kind", "\"recovery_reenroll\""), false),
        ("SK05 normal pwd-only token not invalidated (amr revert)", C, "access_token_cachd", () => Set(Tok, "amr", "[\"pwd\"]"), true),
        ("SK07 audit login baseline", A, null, () => Login, true),
        ("SK07 audit login without session_kind (optional)", A, null, () => Set(Login, "session_kind", null), true),
        ("SK07 audit session_kind bogus", A, null, () => Set(Login, "session_kind", "\"recovery\""), false),
        ("SK07 audit session_kind null", A, null, () => Set(Login, "session_kind", "null"), false),
        ("SK07 signed_in baseline", A, null, () => SignedIn, true),
        ("SK07 signed_in without session_kind", A, null, () => Set(SignedIn, "session_kind", null), false),
        ("SK07 signed_in recovery_reenroll", A, null, () => Set(SignedIn, "session_kind", "\"recovery_reenroll\""), false),
        ("SK07 denied recovery_session baseline", A, null, () => Denied("recovery_session", ""","session_kind":"recovery_reenroll" """), true),
        ("SK07 denied recovery_session with normal", A, null, () => Denied("recovery_session", ""","session_kind":"normal" """), false),
        ("SK07 denied recovery_session without session_kind", A, null, () => Denied("recovery_session", ""), false),
        ("SK08 reenrolled baseline", A, null, () => Reenrolled, true),
        ("SK08 reenrolled with normal (recovery only)", A, null, () => Set(Reenrolled, "session_kind", "\"normal\""), false),
        ("SK08 reenrolled without session_kind", A, null, () => Set(Reenrolled, "session_kind", null), false),
        ("SK08 reenrolled extra detail", A, null, () => Set(Reenrolled, "details.secret", "\"JBSWY3DP\""), false),
        ("SK08 reenrolled old_factor_revoked false", A, null, () => Set(Reenrolled, "details.old_factor_revoked", "false"), false),
        ("SK12 codes issued recovery_reenroll", A, null, () => Codes, true),
        ("SK12 codes issued normal (first enrolment)", A, null, () => Set(Codes, "session_kind", "\"normal\""), true),
        ("SK12 codes issued without session_kind", A, null, () => Set(Codes, "session_kind", null), false),
        ("SK12 codes issued session_kind bogus", A, null, () => Set(Codes, "session_kind", "\"x\""), false),
        ("SK12 codes count 0", A, null, () => Set(Codes, "details.count", "0"), false),
        ("SK12 codes count string", A, null, () => Set(Codes, "details.count", "\"10\""), false),
        ("SK12 codes carries the codes", A, null, () => Set(Codes, "details.codes", "[\"a1b2\"]"), false),
        ("SK12 codes no count", A, null, () => Set(Codes, "details.count", null), false),
        ("SK13 mfa.failed step=reenroll baseline", A, null, () => MfaFail, true),
        ("SK13 mfa.failed step=reenroll with normal", A, null, () => Set(MfaFail, "session_kind", "\"normal\""), false),
        ("SK13 mfa.failed step=reenroll without session_kind", A, null, () => Set(MfaFail, "session_kind", null), false),
        ("SK13 mfa.succeeded with step=reenroll", A, null, () => Set(Set(MfaFail, "event_type", "\"auth.mfa.succeeded\""), "outcome", "\"success\""), false),
        ("SK09 revoked reenrolled baseline (DEC-087)", A, null, () => Revoked, true),
        ("SK09 revoked reenrolled for normal session", A, null, () => Set(Revoked, "session_kind", "\"normal\""), false),
        ("SK09 revoked reenrolled without session_kind", A, null, () => Set(Revoked, "session_kind", null), false),
        ("AD01 partner baseline", C, "access_token_admin", () => AdmPartner, true),
        ("AD01 super baseline", C, "access_token_admin", () => AdmSuper, true),
        ("AD01 admin_type missing (bare shape)", C, "access_token_admin", () => Set(AdmPartner, "admin_type", null), false),
        ("AD01 bare super shape (no admin_type, no partner_id)", C, "access_token_admin", () => Set(AdmSuper, "admin_type", null), false),
        ("AD01 admin_type null", C, "access_token_admin", () => Set(AdmPartner, "admin_type", "null"), false),
        ("AD01 admin_type empty", C, "access_token_admin", () => Set(AdmPartner, "admin_type", "\"\""), false),
        ("AD01 admin_type Super", C, "access_token_admin", () => Set(AdmSuper, "admin_type", "\"Super\""), false),
        ("AD01 admin_type location", C, "access_token_admin", () => Set(AdmSuper, "admin_type", "\"location\""), false),
        ("AD01 admin_type array", C, "access_token_admin", () => Set(AdmSuper, "admin_type", "[\"super\"]"), false),
        ("AD02 super with partner_id", C, "access_token_admin", () => Set(AdmSuper, "partner_id", $"\"{P}\""), false),
        ("AD02 super with partner_id null", C, "access_token_admin", () => Set(AdmSuper, "partner_id", "null"), false),
        ("AD02 partner without partner_id", C, "access_token_admin", () => Set(AdmPartner, "partner_id", null), false),
        ("AD02 partner partner_id empty", C, "access_token_admin", () => Set(AdmPartner, "partner_id", "\"\""), false),
        ("AD02 partner partner_id null", C, "access_token_admin", () => Set(AdmPartner, "partner_id", "null"), false),
        ("AD02 partner partner_id not uuid", C, "access_token_admin", () => Set(AdmPartner, "partner_id", "\"p1\""), false),
        ("AD02 admin with location_id", C, "access_token_admin", () => Set(AdmPartner, "location_id", $"\"{L}\""), false),
        ("AD02 admin with location_id null", C, "access_token_admin", () => Set(AdmPartner, "location_id", "null"), false),
        ("AD02 admin with roles", C, "access_token_admin", () => Set(AdmSuper, "roles", "[\"super_admin\"]"), false),
        ("AD04 cachd token with admin_type", C, "access_token_cachd", () => Set(Tok, "admin_type", "\"super\""), false),
        ("AD04 hand-off signed_in with admin_type (DEC-084)", A, null, () => Set(SignedIn, "admin_type", "\"super\""), false),
        ("AD04 not_location_user without admin_type", A, null, () => Denied("not_location_user", ""), false),
        ("AD04 not_location_user with admin_type", A, null, () => Denied("not_location_user", ""","admin_type":"partner" """), true),
        ("LOC01 no scope without admin_type", A, null, () => Set(Login, "scope", null), false),
        ("LOC01 location_ids missing", A, null, () => Set(Login, "scope.location_ids", null), false),
        ("LOC01 location_ids empty", A, null, () => Set(Login, "scope.location_ids", "[]"), false),
        ("LOC01 location_ids null", A, null, () => Set(Login, "scope.location_ids", "null"), false),
        ("LOC01 location_ids empty string item", A, null, () => Set(Login, "scope.location_ids", "[\"\"]"), false),
        ("LOC01 location_ids null item", A, null, () => Set(Login, "scope.location_ids", "[null]"), false),
        ("LOC01 location_ids not uuid", A, null, () => Set(Login, "scope.location_ids", "[\"loc-1\"]"), false),
        ("LOC01 singular location_id in scope", A, null, () => Set(Login, "scope.location_id", $"\"{L}\""), false),
        ("LOC01 two locations", A, null, () => Set(Login, "scope.location_ids", $"[\"{L}\",\"{L2}\"]"), true),
        ("LOC02 admin empty list ok", A, null, () => Set(Set(Login, "scope.location_ids", "[]"), "admin_type", "\"super\""), true),
        ("LOC02 admin no scope ok", A, null, () => Set(Set(Login, "scope", null), "admin_type", "\"super\""), true),
        ("LOC02 partner admin with locations ok", A, null, () => Set(Login, "admin_type", "\"partner\""), true),
        ("LOC02 admin_type null with empty list", A, null, () => Set(Set(Login, "scope.location_ids", "[]"), "admin_type", "null"), false),
        ("LOC02 admin_type empty with empty list", A, null, () => Set(Set(Login, "scope.location_ids", "[]"), "admin_type", "\"\""), false),
        ("LOC02 admin_type SUPER with empty list", A, null, () => Set(Set(Login, "scope.location_ids", "[]"), "admin_type", "\"SUPER\""), false),
        ("CAP01 cap changed baseline", A, null, () => CapChanged, true),
        ("CAP01 cap changed no admin_type", A, null, () => Set(CapChanged, "admin_type", null), false),
        ("CAP01 cap changed admin_type location", A, null, () => Set(CapChanged, "admin_type", "\"location\""), false),
        ("CAP01 cap changed actor null", A, null, () => Set(CapChanged, "actor.user_id", "null"), false),
        ("CAP01 cap changed extra detail", A, null, () => Set(CapChanged, "details.password", "\"x\""), false),
        ("CAP01 cap changed no new_value", A, null, () => Set(CapChanged, "details.new_value", null), false),
        ("CAP01 cap changed outcome denied", A, null, () => Set(CapChanged, "outcome", "\"denied\""), false),
        ("CAP01 cap changed wrong setting", A, null, () => Set(CapChanged, "target.id", "\"LoginService:Session:IdleTimeout\""), false),
        ("CAP01 cap changed new_value not TimeSpan", A, null, () => Set(CapChanged, "details.new_value", "\"8h\""), false),
        ("CAP03 change_denied baseline", A, null, () => ChangeDenied, true),
        ("CAP03 change_denied actor null allowed", A, null, () => Set(ChangeDenied, "actor.user_id", "null"), true),
        ("CAP03 change_denied actor without user_id", A, null, () => Set(ChangeDenied, "actor", "{}"), false),
        ("CAP03 change_denied actor empty string", A, null, () => Set(ChangeDenied, "actor.user_id", "\"\""), false),
        ("CAP03 change_denied actor number", A, null, () => Set(ChangeDenied, "actor.user_id", "5"), false),
        ("CAP03 change_denied actor object", A, null, () => Set(ChangeDenied, "actor.user_id", "{}"), false),
        ("CAP03 change_denied target id null", A, null, () => Set(ChangeDenied, "target.id", "null"), false),
        ("CAP03 change_denied no target", A, null, () => Set(ChangeDenied, "target", null), false),
        ("CAP03 change_denied reason null", A, null, () => Set(ChangeDenied, "details.reason", "null"), false),
        ("CAP03 change_denied reason unknown", A, null, () => Set(ChangeDenied, "details.reason", "\"nope\""), false),
        ("CAP03 change_denied no reason", A, null, () => Set(ChangeDenied, "details.reason", null), false),
        ("CAP03 change_denied outcome success", A, null, () => Set(ChangeDenied, "outcome", "\"success\""), false),
        ("CAP03 change_denied admin_type null", A, null, () => Set(ChangeDenied, "admin_type", "null"), false),
        ("CAP03 change_denied scope null", A, null, () => Set(ChangeDenied, "scope", "null"), false),
        // TL 8:37 PM asked: change_denied details closed, no secrets. FAILS at d0f75b3 (details has no additionalProperties:false).
        ("CAP04 change_denied details closed (no password field)", A, null, () => Set(ChangeDenied, "details.password", "\"hunter2\""), false),
    };

    public static TheoryData<string> Cases { get { var d = new TheoryData<string>(); foreach (var c in All) d.Add(c.id); return d; } }

    [Theory, MemberData(nameof(Cases))]
    public void PF_schema_case(string id)
    {
        var c = All.Single(x => x.id == id);
        Need(c.file);
        if (c.def is not null) Assert.True(JsonNode.Parse(Read(c.file))!["$defs"]?[c.def] is not null, $"$defs.{c.def} missing from {c.file}");
        var doc = c.doc();
        Assert.True(Valid(c.file, c.def, doc) == c.valid, $"{id}: expected {(c.valid ? "VALID" : "REJECTED")}; doc={doc.ToJsonString()}");
    }

    [Fact(Skip = "PENDING OQ-SSE2-42: scope.location_ids has no uniqueItems; whether duplicates are invalid is not stated (8.11/8.13). Schema accepts [L,L] at d0f75b3.")]
    public void LOC03_duplicate_location_ids() { }

    [Fact(Skip = "PENDING OQ-SSE2-44: 8.13 lets admin_type switch on []; nothing in the schema stops a /l/{slug} (location) session event carrying admin_type. Server-side: the stand-in never writes admin_type for a non-/admin session (SEC77).")]
    public void LOC04_location_session_event_cannot_claim_admin_type() { }

    [Fact]
    public void SK06_session_kind_enum_exact_and_retired_admin_shapes_gone()
    {
        Need(C);
        var defs = JsonNode.Parse(Read(C))!["$defs"]!.AsObject();
        Assert.Equal("[\"normal\",\"recovery_reenroll\"]", defs["session_kind"]?["enum"]?.ToJsonString());
        Assert.False(defs.ContainsKey("access_token_admin_super"), "retired _super shape still in claims schema (8.12)");
        Assert.False(defs.ContainsKey("access_token_admin_partner"), "retired _partner shape still in claims schema (8.12)");
    }

    static string Row(string id) => Read("contract-v1.md").Split('\n').FirstOrDefault(l => l.StartsWith($"| {id} |")) ?? "";

    [Fact]
    public void SK10_7_8_refusals_amr_revert_DEC087_and_codes_once()
    {
        Need("contract-v1.md");
        var r = Row("7.8");
        foreach (var must in new[] { "recovery_reenroll", "reason=recovery_session", "hub_launch", "hand-off", "cage", "sign-out", "alone does not decide", "reason=reenrolled", "DEC-087", "never becomes `normal`", "second GET returns 404", "step=reenroll" })
            Assert.True(r.Contains(must), $"7.8 lacks '{must}'");
        Assert.Matches(@"fresh `normal` sign-in", r);
    }

    [Fact]
    public void SK11_reenroll_paths_single_spelling_in_contract_and_tests()
    {
        Need("contract-v1.md");
        var files = Directory.GetFiles(Paths.Contract).Where(f => (f.EndsWith(".md") || f.EndsWith(".json")) && !f.EndsWith("SSE-BUILD-LOG-PENDING.md"));
        var all = string.Join("\n", files.Select(File.ReadAllText));
        Assert.True(Regex.IsMatch(all, "/mfa/reenroll"), "SA rename to /mfa/reenroll not in contract");
        Assert.Equal(0, Regex.Matches(all, "/mfa/reenrol(?!l)").Count);
    }

    [Fact(Skip = "PENDING OQ-SSE2-45: 7.8 says 'the existing TOTP rate limit applies' to POST /mfa/reenroll, but no TOTP rate limit (limit, window, response, audit) is defined anywhere in contract v1 (SSE's own OPEN-QUESTIONS row agrees). TL to decide blocker/major.")]
    public void SK14_reenroll_post_rate_limit_specified() { }

    [Fact]
    public void CAP02_7_4_admin_only_cap_change_audited_idle_check_interim_partner()
    {
        Need("contract-v1.md");
        var r = Row("7.4");
        foreach (var must in new[] { "`super` or `partner`", "auth.config.session_cap_changed", "auth.config.change_denied", "15-minute idle", "Interim, NOT decided", "own partner" })
            Assert.True(r.Contains(must), $"7.4 lacks '{must}'");
    }

    [Fact]
    public void PH01_placeholder_app_codes_marked_back_office_reports_separate_and_not_frozen_enum()
    {
        Need("ui-driver.md");
        foreach (var f in new[] { "ui-driver.md", "standin-control.md" })
        {
            var line = Read(f).Split('\n').FirstOrDefault(l => l.Contains("back_office") && l.Contains("reports")) ?? "";
            Assert.True(line.Contains("PLACEHOLDER"), $"{f}: app_codes not marked PLACEHOLDER (DEC-088)");
            Assert.True(line.Contains("separate"), $"{f}: back_office/reports not stated separate (DEC-089)");
        }
        var testid = JsonNode.Parse(Read("standin-ui.schema.json"))!["$defs"]!["testid"]!;
        var enumText = testid.ToJsonString();
        foreach (var code in new[] { "hub-tile-cash_advance", "hub-tile-back_office", "hub-tile-reports" })
            Assert.DoesNotContain($"\"{code}\"", enumText);
    }
}
