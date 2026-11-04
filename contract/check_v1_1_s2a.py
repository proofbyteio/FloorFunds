"""Contract v1.1 slice S2a checks (location login config, screen 9).

Validates every ```json example=NAME schema=FILE[#/$defs/X] kind=request|response|event|rules``` block in
contract/v1.1-S2a-location-login-config.md, asserts secret_ref never appears in response/event examples or
schemas, and runs negative cases. Run beside check_schemas.py with the same venv. No network, no secrets.
"""
import copy, json, os, re, sys
from jsonschema import Draft202012Validator as V

D = os.path.dirname(os.path.abspath(__file__)) + "/"
FC = V.FORMAT_CHECKER
S = {n: json.load(open(D + n, encoding="utf-8")) for n in ["location-login-config.schema.json", "audit-events.schema.json"]}
for n, x in S.items():
    V.check_schema(x)

FAIL, PASSES = [], [0]
def errs(file, ref, inst):
    sch = S[file]
    if ref:
        sch = dict(sch, **{"$ref": ref}); sch.pop("description", None)
    return sorted(e.message[:120] for e in V(sch, format_checker=FC).iter_errors(inst))
def exp(name, e, want_err):
    ok = bool(e) == want_err
    print(("PASS " if ok else "FAIL ") + name, ("" if ok else e))
    if ok: PASSES[0] += 1
    else: FAIL.append(name)

# ---- 1. examples in the spec ----
spec = open(D + "v1.1-S2a-location-login-config.md", encoding="utf-8").read()
blocks = re.findall(r"```json example=(\S+) schema=(\S+) kind=(request|response|event|rules)\n(.*?)\n```", spec, re.S)
EX = {}
ARN = re.compile(r"arn:aws:secretsmanager", re.I)
def walk(o, path=""):
    if isinstance(o, dict):
        for k, v in o.items():
            yield path + "/" + k, k, v
            yield from walk(v, path + "/" + k)
    elif isinstance(o, list):
        for i, v in enumerate(o):
            yield from walk(v, f"{path}/{i}")
exp("spec has >= 15 example blocks", [] if len(blocks) >= 15 else [f"only {len(blocks)}"], False)
for name, schema, kind, body in blocks:
    inst = json.loads(body)
    EX[name] = inst
    file, _, frag = schema.partition("#")
    exp(f"example {name} valid vs {schema}", errs(file, "#" + frag if frag else None, inst), False)
    if kind in ("response", "event"):
        leaks = [p for p, k, v in walk(inst) if k == "secret_ref" or (isinstance(v, str) and ARN.search(v))]
        exp(f"example {name} ({kind}) has no secret_ref / ARN", leaks, False)
    if kind == "request":
        real = [p for p, k, v in walk(inst) if isinstance(v, str) and ARN.search(v) and ":000000000000:" not in v]
        exp(f"example {name} uses only fake ARNs (account 000000000000)", real, False)
names_needed = {"get_200_entra", "get_200_entra_restricted", "err_404", "access_rules", "put_req_pa_db_to_entra", "put_req_sa_mfa_off", "put_req_entra_to_db", "post_req_create_db", "post_201_db", "changes_200", "err_409", "err_422", "err_403_not_super_admin", "ev_changed_pa_upgrade", "ev_changed_sa_mfa_off", "ev_changed_created", "ev_denied_not_super_admin", "ev_denied_forbidden_scope", "ev_denied_tenant_bound_sa_create"}
exp("all required examples present", sorted(names_needed - EX.keys()), False)

# ---- 2. no response/event schema declares secret_ref ----
L = S["location-login-config.schema.json"]["$defs"]; A = S["audit-events.schema.json"]["$defs"]
def declares_secret(o):
    return any(k == "secret_ref" for p, k, v in walk(o) if "/properties/" in p + "/" and p.split("/")[-2] == "properties")
for d in ["login_config_view", "login_config_view_super", "login_config_view_restricted", "secret_view", "change_history", "audited_state", "error_response"]:
    exp(f"schema llc $defs.{d} has no secret_ref property", ["declares secret_ref"] if declares_secret(L[d]) else [], False)
for d in ["auth.provider.changed", "provider_state", "auth.config.change_denied.location_login_config", "auth.config.change_denied.config"]:
    exp(f"schema audit $defs.{d} has no secret_ref property", ["declares secret_ref"] if declares_secret(A[d]) else [], False)
exp("audit not_emitted list no longer has change_requested", ["present"] if "auth.provider.change_requested" in json.dumps(A["not_emitted_fields_not_stated"]["properties"]) else [], False)

FAKE = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/x-AbCdEf"
def m(base, **kw):
    o = copy.deepcopy(base)
    for k, v in kw.items():
        cur = o; parts = k.split("__")
        for pp in parts[:-1]: cur = cur[pp]
        if v is DEL: cur.pop(parts[-1], None)
        else: cur[parts[-1]] = v
    return o
DEL = object()
LLC = "location-login-config.schema.json"; W = "#/$defs/login_config_write"; VIEW = "#/$defs/login_config_view_super"; RV = "#/$defs/login_config_view_restricted"; ANYV = "#/$defs/login_config_view"; ER = "#/$defs/error_response"; CR = "#/$defs/create_location_request"
ent = EX["put_req_pa_db_to_entra"]; db = EX["put_req_entra_to_db"]; view = EX["get_200_entra"]

# ---- 3. leak negatives: secret_ref injected into responses/events is rejected ----
exp("view + secret_ref rejected", errs(LLC, VIEW, m(view, secret_ref=FAKE)), True)
exp("view secret + value rejected", errs(LLC, VIEW, m(view, secret={"set": True, "rotated_at": None, "value": FAKE})), True)
exp("view secret set=false with rotated_at rejected", errs(LLC, VIEW, m(view, secret={"set": False, "rotated_at": "2026-10-30T17:05:00Z"})), True)
exp("error + secret_ref rejected", errs(LLC, ER, {"error": "validation_failed", "fields": [{"field": "secret_ref", "code": "secret_ref_format"}], "secret_ref": FAKE}), True)
exp("error field entry + value rejected", errs(LLC, ER, {"error": "validation_failed", "fields": [{"field": "secret_ref", "code": "secret_ref_format", "value": FAKE}]}), True)
chg = EX["ev_changed_pa_upgrade"]; den = EX["ev_denied_not_super_admin"]
AE = "audit-events.schema.json"
exp("provider.changed details + secret_ref rejected", errs(AE, None, m(chg, details__secret_ref=FAKE)), True)
exp("provider.changed after + secret_ref rejected", errs(AE, None, m(chg, details__after=dict(chg["details"]["after"], secret_ref=FAKE))), True)
exp("provider.changed + approval_id rejected (DEC-096)", errs(AE, None, m(chg, details__approval_id="ap1")), True)
exp("change_denied llc + attempted_value rejected", errs(AE, None, m(den, details__attempted_value=FAKE)), True)
exp("change_denied llc + secret_ref rejected", errs(AE, None, m(den, details__secret_ref=FAKE)), True)
exp("history item + secret_ref rejected", errs(LLC, "#/$defs/change_history", {"items": [dict(EX["changes_200"]["items"][0], secret_ref=FAKE)]}), True)

# ---- 4. request field rules (schema-level part of §5.2) ----
exp("entra two tenants rejected (tenant_count)", errs(LLC, W, m(ent, entra_allowed_tenant_ids=["9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d", "1a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"])), True)
exp("entra zero tenants rejected", errs(LLC, W, m(ent, entra_allowed_tenant_ids=[])), True)
exp("tenant not uuid rejected (tenant_id_format)", errs(LLC, W, m(ent, entra_allowed_tenant_ids=["contoso.onmicrosoft.com"])), True)
exp("nil tenant rejected", errs(LLC, W, m(ent, entra_allowed_tenant_ids=["00000000-0000-0000-0000-000000000000"])), True)
# server-filled OIDC [TLD 2026-11-04 7:41]: any oidc_* key in a client body is rejected (field_not_writable)
for k, v in [("oidc_authority", "https://login.microsoftonline.com"), ("oidc_client_id", "5f4e3d2c-1b0a-4f9e-8d7c-6b5a4f3e2d1c"), ("oidc_authority", None), ("oidc_client_id", None)]:
    exp(f"PUT entra body with {k}={v!r} rejected (field_not_writable)", errs(LLC, W, m(ent, **{k: v})), True)
    exp(f"PUT database body with {k}={v!r} rejected (field_not_writable)", errs(LLC, W, m(db, **{k: v})), True)
    exp(f"create body login_config with {k}={v!r} rejected (field_not_writable)", errs(LLC, CR, m(EX["post_req_create_db"], **{"login_config__" + k: v})), True)
exp("entra secret_ref absent ok (keep current)", errs(LLC, W, m(ent, secret_ref=DEL)), False)
exp("entra secret_ref null rejected", errs(LLC, W, m(ent, secret_ref=None)), True)
exp("secret_ref not ARN rejected", errs(LLC, W, m(ent, secret_ref="my-client-secret")), True)
for bad in ["http://login.microsoftonline.com", "https://login.microsoftonline.com/", "https://login.microsoftonline.com/9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d/v2.0", "https://login.microsoftonline.com?x=1", "https://user@login.microsoftonline.com", "login.microsoftonline.com"]:
    exp(f"super view oidc_authority {bad!r} rejected (deployment value rule)", errs(LLC, VIEW, m(view, oidc_authority=bad)), True)
exp("super view oidc_authority with port ok", errs(LLC, VIEW, m(view, oidc_authority="https://login.example.test:8443")), False)
exp("super view oidc_client_id not uuid rejected", errs(LLC, VIEW, m(view, oidc_client_id="riverbend-app")), True)
# oidc visible to Super Admin only [TLD 2026-11-04 7:41]
rview = EX["get_200_entra_restricted"]
exp("restricted view + oidc_authority rejected", errs(LLC, RV, m(rview, oidc_authority="https://login.microsoftonline.com")), True)
exp("restricted view + oidc_client_id rejected", errs(LLC, RV, m(rview, oidc_client_id="5f4e3d2c-1b0a-4f9e-8d7c-6b5a4f3e2d1c")), True)
exp("super view without oidc_authority rejected", errs(LLC, VIEW, m(view, oidc_authority=DEL)), True)
exp("super view without oidc_client_id rejected", errs(LLC, VIEW, m(view, oidc_client_id=DEL)), True)
exp("restricted view = super view minus oidc keys", [] if rview == {k: v for k, v in view.items() if not k.startswith("oidc_")} else ["differ"], False)
exp("restricted view + secret_ref rejected", errs(LLC, RV, m(rview, secret_ref=FAKE)), True)
exp("provider_kind other rejected", errs(LLC, W, m(ent, provider_kind="saml")), True)
exp("database + tenant rejected", errs(LLC, W, m(db, entra_allowed_tenant_ids=["9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"])), True)
exp("database + secret_ref string rejected", errs(LLC, W, m(db, secret_ref=FAKE)), True)
exp("database + allow_db_fallback true rejected (A-16)", errs(LLC, W, m(db, allow_db_fallback=True)), True)
exp("database secret_ref absent ok (schema; server checks current secret)", errs(LLC, W, m(db, secret_ref=DEL)), False)
for k in ["provider_kind", "display_name", "entra_allowed_tenant_ids", "allow_db_fallback", "mfa_required"]:
    exp(f"PUT missing {k} rejected (no silent clearing)", errs(LLC, W, m(ent, **{k: DEL})), True)
exp("PUT + row_version in body rejected (If-Match only)", errs(LLC, W, m(ent, row_version=7)), True)
exp("PUT + unknown field rejected", errs(LLC, W, m(ent, approval_id="x")), True)
exp("display_name blank rejected", errs(LLC, W, m(ent, display_name="  ")), True)
exp("display_name 101 chars rejected", errs(LLC, W, m(ent, display_name="x" * 101)), True)
# reason 10..500 after trim [TLD 2026-11-04 7:39, OQ-S2a-7]
exp("reason 9 chars rejected", errs(LLC, W, m(ent, reason="x" * 9)), True)
exp("reason 9 chars padded to 13 rejected (trim first)", errs(LLC, W, m(ent, reason="  " + "x" * 9 + "  ")), True)
exp("reason 10 chars ok", errs(LLC, W, m(ent, reason="x" * 10)), False)
exp("reason 10 chars padded ok", errs(LLC, W, m(ent, reason=" \t" + "x" * 10 + "\n ")), False)
exp("reason 500 chars ok", errs(LLC, W, m(ent, reason="x" * 500)), False)
exp("reason 501 chars rejected", errs(LLC, W, m(ent, reason="x" * 501)), True)
exp("reason 501 chars after trim rejected even if padded", errs(LLC, W, m(ent, reason=" " + "x" * 501 + " ")), True)
exp("reason inner spaces count (a + 8 spaces + b = 10) ok", errs(LLC, W, m(ent, reason="a" + " " * 8 + "b")), False)
exp("reason whitespace-only ok at schema level (= no reason; server 400 on a downgrade)", errs(LLC, W, m(ent, reason="     ")), False)
exp("reason null ok", errs(LLC, W, m(ent, reason=None)), False)
exp("create without login_config rejected (LD L169)", errs(LLC, CR, m(EX["post_req_create_db"], login_config=DEL)), True)
exp("create with slug rejected (server-generated)", errs(LLC, CR, m(EX["post_req_create_db"], slug="riverbend")), True)
exp("view slug uppercase rejected", errs(LLC, VIEW, m(view, slug="XK4M-9Q2C")), True)
exp("view slug leading hyphen rejected", errs(LLC, VIEW, m(view, slug="-xk4m")), True)
exp("view slug 63 chars ok", errs(LLC, VIEW, m(view, slug="a" * 63)), False)
exp("view slug 64 chars rejected", errs(LLC, VIEW, m(view, slug="a" * 64)), True)
exp("error 422 without fields rejected", errs(LLC, ER, {"error": "validation_failed"}), True)
exp("error 409 without current_row_version rejected", errs(LLC, ER, {"error": "concurrency_conflict"}), True)
exp("error 403 with fields rejected", errs(LLC, ER, {"error": "not_super_admin", "fields": [{"field": "mfa_required", "code": "type"}]}), True)
exp("error unknown code rejected", errs(LLC, ER, {"error": "approval_required"}), True)
# 404 vs forbidden_scope [TLD 2026-11-04 7:39, OQ-S2a-3]
exp("error forbidden_scope rejected (out of scope answers 404)", errs(LLC, ER, {"error": "forbidden_scope"}), True)
exp("err_404 body is exactly {error: not_found}", [] if EX["err_404"] == {"error": "not_found"} else ["differs"], False)
exp("field code field_not_writable ok", errs(LLC, ER, {"error": "validation_failed", "fields": [{"field": "oidc_client_id", "code": "field_not_writable"}]}), False)
exp("field code oidc_authority_format rejected (no longer client-supplied)", errs(LLC, ER, {"error": "validation_failed", "fields": [{"field": "oidc_authority", "code": "oidc_authority_format"}]}), True)
exp("field code reason_length ok", errs(LLC, ER, {"error": "validation_failed", "fields": [{"field": "reason", "code": "reason_length"}]}), False)
# access_rules pins (history visibility, 404 rule, oidc visibility, reason bounds, server-filled keys)
AR = "#/$defs/access_rules"; ar = EX["access_rules"]
exp("access_rules missing and out-of-scope bodies identical", [] if ar["missing_location"]["body"] == ar["out_of_scope_location"]["body"] and ar["missing_location"]["http"] == ar["out_of_scope_location"]["http"] == 404 else ["differ"], False)
exp("access_rules out_of_scope 403 rejected", errs(LLC, AR, m(ar, out_of_scope_location__http=403)), True)
exp("access_rules out_of_scope body forbidden_scope rejected", errs(LLC, AR, m(ar, out_of_scope_location__body={"error": "forbidden_scope"})), True)
exp("access_rules out_of_scope event wrong_partner rejected", errs(LLC, AR, m(ar, out_of_scope_location__event_reason="wrong_partner")), True)
exp("access_rules missing location with event rejected", errs(LLC, AR, m(ar, missing_location__event_reason="forbidden_scope")), True)
exp("access_rules history + org_v rejected", errs(LLC, AR, m(ar, history_visible_to=["super_admin", "partner_admin_own_partner", "org_v_reader"])), True)
exp("access_rules history + other partner rejected", errs(LLC, AR, m(ar, history_visible_to=["super_admin", "partner_admin_own_partner", "partner_admin_other_partner"])), True)
exp("access_rules history org_v 200 rejected", errs(LLC, AR, m(ar, history_refused__org_v_reader=200)), True)
exp("access_rules oidc visible to partner rejected", errs(LLC, AR, m(ar, oidc_fields_visible_to=["super_admin", "partner_admin_own_partner"])), True)
exp("access_rules reason 1..500 rejected", errs(LLC, AR, m(ar, reason_trimmed_length={"min": 1, "max": 500})), True)
exp("access_rules config key rename rejected", errs(LLC, AR, m(ar, server_filled_config_keys={"oidc_authority": "Entra:Instance", "oidc_client_id": "Entra:ClientId"})), True)

# ---- 5. auth.provider.changed v1.1 rules ----
sa = EX["ev_changed_sa_mfa_off"]; cre = EX["ev_changed_created"]
exp("MFA-off by partner rejected (DEC-098)", errs(AE, None, m(sa, admin_type="partner")), True)
exp("MFA-off without reason rejected", errs(AE, None, m(sa, details__reason=None)), True)
exp("MFA-off blank reason rejected", errs(AE, None, m(sa, details__reason="   ")), True)
exp("event reason 9 chars rejected", errs(AE, None, m(sa, details__reason="x" * 9)), True)
exp("event reason 10 chars ok", errs(AE, None, m(sa, details__reason="x" * 10)), False)
exp("event reason 500 chars ok", errs(AE, None, m(sa, details__reason="x" * 500)), False)
exp("event reason 501 chars rejected", errs(AE, None, m(sa, details__reason="x" * 501)), True)
exp("event reason on a non-downgrade, 9 chars rejected", errs(AE, None, m(chg, details__reason="x" * 9)), True)
exp("event reason on a non-downgrade, 10 chars ok", errs(AE, None, m(chg, details__reason="x" * 10)), False)
fb_before = dict(sa["details"]["before"], allow_db_fallback=False); fb_after = dict(sa["details"]["before"], allow_db_fallback=True)
fb = m(sa, details__before=fb_before, details__after=fb_after, details__changed_fields=["allow_db_fallback"])
exp("fallback-on by super with reason ok (DEC-096)", errs(AE, None, fb), False)
exp("fallback-on by partner rejected (DEC-096)", errs(AE, None, m(fb, admin_type="partner")), True)
exp("fallback-on by super without reason rejected", errs(AE, None, m(fb, details__reason=None)), True)
up = m(sa, admin_type="partner", details__before=sa["details"]["after"], details__after=sa["details"]["before"], details__reason=None)
exp("MFA-on by partner, no reason ok (TL upgrade rule)", errs(AE, None, up), False)
fboff = m(fb, admin_type="partner", details__before=fb_after, details__after=fb_before, details__reason=None)
exp("fallback-off by partner, no reason ok (TL upgrade rule)", errs(AE, None, fboff), False)
keep = m(fb, admin_type="partner", details__before=fb_after, details__after=dict(fb_after, mfa_required=True), details__reason=None, details__changed_fields=["display_name"])
exp("partner edit leaving fallback on (unchanged) ok", errs(AE, None, keep), False)
exp("created with MFA off by partner rejected (A-5)", errs(AE, None, m(cre, details__after=dict(cre["details"]["after"], mfa_required=False))), True)
exp("created with before object rejected", errs(AE, None, m(cre, details__before=cre["details"]["after"])), True)
exp("updated with before null rejected", errs(AE, None, m(chg, details__before=None)), True)
exp("provider.changed no admin_type rejected", errs(AE, None, m(chg, admin_type=DEL)), True)
exp("provider.changed no row_version rejected", errs(AE, None, m(chg, details__row_version=DEL)), True)
exp("provider.changed no change_id rejected", errs(AE, None, m(chg, details__change_id=DEL)), True)
exp("provider.changed two location_ids rejected", errs(AE, None, m(chg, scope__location_ids=[chg["details"]["location_id"], "4d3c2b1a-0f9e-4d8c-b7a6-f5e4d3c2b1a0"])), True)
exp("provider.changed outcome denied rejected", errs(AE, None, m(chg, outcome="denied")), True)
exp("provider.changed before 2 tenants rejected", errs(AE, None, m(chg, details__after=dict(chg["details"]["after"], entra_allowed_tenant_ids=["9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d", "1a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"]))), True)
exp("provider.changed changed_fields unknown name rejected", errs(AE, None, m(chg, details__changed_fields=["password"])), True)
exp("auth.provider.change_requested rejected (v1.1)", errs(AE, None, m(chg, event_type="auth.provider.change_requested", details={})), True)

# ---- 6. auth.config.change_denied: S2a branch + v1 branch intact ----
wp = EX["ev_denied_forbidden_scope"]; tb = EX["ev_denied_tenant_bound_sa_create"]
exp("denied not_super_admin by super rejected", errs(AE, None, m(den, admin_type="super", details__actor_partner_id=DEL)), True)
exp("denied forbidden_scope without actor_partner_id rejected", errs(AE, None, m(wp, details__actor_partner_id=DEL)), True)
exp("denied not_admin without admin_type ok", errs(AE, None, m(den, admin_type=DEL, details__reason="not_admin", details__actor_partner_id=DEL)), False)
exp("denied not_admin with admin_type rejected", errs(AE, None, m(den, details__reason="not_admin")), True)
exp("denied super with actor_partner_id rejected", errs(AE, None, m(tb, details__actor_partner_id="7d3f6c1e-2b4a-4c8e-9f10-1a2b3c4d5e6f")), True)
exp("denied create with target location rejected", errs(AE, None, m(tb, target__type="location_login_config")), True)
exp("denied update with target partner rejected", errs(AE, None, m(den, target__type="partner")), True)
exp("denied update without location_ids rejected", errs(AE, None, m(den, scope__location_ids=DEL)), True)
exp("denied reason not_location_admin rejected", errs(AE, None, m(den, details__reason="not_location_admin")), True)
exp("denied wrong_partner rejected on llc (now forbidden_scope)", errs(AE, None, m(wp, details__reason="wrong_partner")), True)
exp("denied forbidden_scope by super rejected", errs(AE, None, m(wp, admin_type="super", details__actor_partner_id=DEL)), True)
exp("denied forbidden_scope on create ok", errs(AE, None, m(wp, details__operation="create", target={"type": "partner", "id": "7d3f6c1e-2b4a-4c8e-9f10-1a2b3c4d5e6f"}, scope={"partner_id": "7d3f6c1e-2b4a-4c8e-9f10-1a2b3c4d5e6f", "location_ids": []})), False)
exp("denied reason out_of_range (v1 only) rejected on llc", errs(AE, None, m(den, details__reason="out_of_range")), True)
exp("denied setting other rejected", errs(AE, None, m(den, details__setting="LoginService:Session:AbsoluteLifetime")), True)
exp("denied outcome success rejected", errs(AE, None, m(den, outcome="success")), True)
exp("denied no attempted_fields rejected", errs(AE, None, m(den, attempted_fields=DEL, details__attempted_fields=DEL)), True)
exp("denied attempted_fields value-like rejected", errs(AE, None, m(den, details__attempted_fields=["9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"])), True)
v1 = {"event_id": "019a4f2e-7c10-7a3b-9c4d-5e6f7a8b9c09", "schema": "cachd.log", "schema_version": "1.0.0", "category": "event", "event_type": "auth.config.change_denied", "occurred_at": "2026-11-02T19:40:00Z", "outcome": "denied", "is_audit": True, "actor": {"user_id": None}, "source": {"service": "login-standin"}, "target": {"type": "config", "id": "LoginService:Session:AbsoluteLifetime"}, "scope": {"partner_id": "7d3f6c1e-2b4a-4c8e-9f10-1a2b3c4d5e6f", "location_ids": ["3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8"]}, "details": {"setting": "LoginService:Session:AbsoluteLifetime", "attempted_value": "08:00:00", "reason": "not_admin"}}
exp("v1 change_denied config baseline still ok", errs(AE, None, v1), False)
exp("v1 change_denied config attempted_value secret text still rejected", errs(AE, None, m(v1, details__attempted_value="hunter2")), True)
exp("v1 change_denied config reason not_super_admin rejected (S2a reasons only on llc)", errs(AE, None, m(v1, details__reason="not_super_admin")), True)
exp("v1 change_denied config wrong_partner partner ok", errs(AE, None, m(v1, admin_type="partner", details__reason="wrong_partner")), False)

# ---- 7. F16: optional config_row_version on auth.login.* [TLD 2026-11-04 7:45] ----
ls = {"event_id": "019a4f2e-7c10-7a3b-9c4d-5e6f7a8b9c10", "schema": "cachd.log", "schema_version": "1.1.0", "category": "event", "event_type": "auth.login.succeeded", "occurred_at": "2026-11-04T15:50:00Z", "outcome": "success", "is_audit": True, "actor": {"user_id": "5f0c3e1a-0003-4000-8000-000000000c01"}, "source": {"service": "login-service"}, "target": {"type": "user", "id": "5f0c3e1a-0003-4000-8000-000000000c01"}, "scope": {"partner_id": "5f0c3e1a-0001-4000-8000-000000000001", "location_ids": ["5f0c3e1a-0001-4000-8000-0000000000a1"]}, "details": {"provider_kind": "entra", "tenant_id": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d", "amr": ["pwd", "mfa"]}}
exp("login.succeeded baseline (no config_row_version) ok", errs(AE, None, ls), False)
exp("login.succeeded config_row_version 7 ok", errs(AE, None, m(ls, details__config_row_version=7)), False)
exp("login.succeeded config_row_version 0 rejected", errs(AE, None, m(ls, details__config_row_version=0)), True)
exp("login.succeeded config_row_version string rejected", errs(AE, None, m(ls, details__config_row_version="7")), True)
exp("login.succeeded config_row_version 1.5 rejected", errs(AE, None, m(ls, details__config_row_version=1.5)), True)
lf = m(ls, event_type="auth.login.failed", outcome="failure", details={"username_hmac": "0" * 64, "reason": "tenant_mismatch", "provider_kind": "entra"})
exp("login.failed baseline (no config_row_version) ok", errs(AE, None, lf), False)
exp("login.failed config_row_version 3 ok", errs(AE, None, m(lf, details__config_row_version=3)), False)
exp("login.failed config_row_version null rejected", errs(AE, None, m(lf, details__config_row_version=None)), True)
exp("login.failed config_row_version -1 rejected", errs(AE, None, m(lf, details__config_row_version=-1)), True)

# ---- 8. TLD 2026-11-04 8:30: not_location_user on auth.login.failed; 8:44 K4 spec pins ----
exp("login.failed not_location_user ok", errs(AE, None, m(lf, details__reason="not_location_user")), False)
exp("login.failed not_location_user + config_row_version ok", errs(AE, None, m(lf, details__reason="not_location_user", details__config_row_version=2)), False)
exp("login.failed reason with upper case still rejected", errs(AE, None, m(lf, details__reason="Not_Location_User")), True)
_lfr = S[AE]["$defs"]["auth.login.failed"]["properties"]["details"]["properties"]["reason"]
exp("login.failed reason lists not_location_user (examples + description)", [] if ("not_location_user" in _lfr["examples"] and "not_location_user" in _lfr["description"]) else ["missing"], False)
for _pin in ["### 7.4 `not_location_user` on `auth.login.failed`", "**Trust boundary [TLD 2026-11-04 8:44, K4].**", "**V11-RLSROLES**", "`SecretRef:AllowedAccountIds`", "`updated_by`, `updated_at` (and `partner_id` in a PUT body)"]:
    exp("spec pin: " + _pin[:50], [] if _pin in spec else ["missing"], False)

print("v1.1-S2a checks: %d passed, %d failed" % (PASSES[0], len(FAIL)), "ALL PASS" if not FAIL else "FAILED: %s" % FAIL)
sys.exit(1 if FAIL else 0)
