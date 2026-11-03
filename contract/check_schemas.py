import json, sys, uuid
from jsonschema import Draft202012Validator as V
from referencing import Registry, Resource
d = "/workspace/cachd-ones/code/contract/"
s = {n: json.load(open(d+n)) for n in ["openid-configuration.schema.json","claims.schema.json","audit-events.schema.json","device-attestation.schema.json","standin-ui.schema.json"]}
for n,x in s.items(): V.check_schema(x); print("schema ok", n)
fc = V.FORMAT_CHECKER
def val(schema, inst, ref=None):
    sch = {"$ref": ref} if ref else schema
    if ref: sch = dict(schema, **{"$ref": ref}); sch.pop("description",None)
    return sorted(e.message[:90] for e in V(sch, format_checker=fc).iter_errors(inst))
disc = {"issuer":"http://localhost:5080/standin","authorization_endpoint":"http://localhost:5080/standin/connect/authorize","token_endpoint":"http://localhost:8084/standin/connect/token","revocation_endpoint":"http://localhost:8084/standin/connect/revoke","end_session_endpoint":"http://localhost:5080/standin/connect/endsession","jwks_uri":"http://localhost:5080/standin/.well-known/jwks","response_types_supported":["code"],"grant_types_supported":["authorization_code","refresh_token"],"code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["client_secret_basic","private_key_jwt"],"id_token_signing_alg_values_supported":["RS256"],"subject_types_supported":["public"],"token_endpoint_auth_signing_alg_values_supported":["RS256"],"revocation_endpoint_auth_methods_supported":["client_secret_basic","private_key_jwt"],"authorization_response_iss_parameter_supported":True}
print("disc good", val(s["openid-configuration.schema.json"], disc))
print("disc +userinfo (expect err)", val(s["openid-configuration.schema.json"], dict(disc, userinfo_endpoint="x")))
print("disc plain (expect err)", val(s["openid-configuration.schema.json"], dict(disc, code_challenge_methods_supported=["plain","S256"])))
u=lambda: str(uuid.uuid4())
base={"iss":"http://localhost:5080/standin","sub":u(),"aud":"cash-advance-api","exp":1300,"iat":1000,"jti":"j"*20,"scope":"cash_advance_api","client_id":"cash_advance_bff","login_provider":"database","amr":["pwd","otp","mfa"],"auth_time":990,"x_standin":True}
c=s["claims.schema.json"]
cachd=dict(base,perm_version=3,partner_id=u(),cachd_client_id=u(),location_id=u())
print("cachd good", val(c,cachd,"#/$defs/access_token_cachd"))
print("cachd no perm_version (expect err)", val(c,{k:v for k,v in cachd.items() if k!="perm_version"},"#/$defs/access_token_cachd"))
print("cachd + roles (expect err)", val(c,dict(cachd,roles=["x"]),"#/$defs/access_token_cachd"))
print("other good", val(c,base,"#/$defs/access_token_other"))
print("other + perm_version (expect err)", val(c,dict(base,perm_version=1),"#/$defs/access_token_other"))
print("other + org (expect err)", val(c,dict(base,partner_id=u(),location_id=u()),"#/$defs/access_token_other"))
print("other org on good", val(c,dict(base,partner_id=u(),location_id=u()),"#/$defs/access_token_other_org_claims"))
print("other org on + perm_version (expect err)", val(c,dict(base,partner_id=u(),location_id=u(),perm_version=1),"#/$defs/access_token_other_org_claims"))
idt={"iss":"i","sub":u(),"aud":"cash_advance_bff","azp":"cash_advance_bff","exp":2,"iat":1,"nonce":"n","auth_time":1,"amr":["pwd","otp","mfa"]}
print("id good", val(c,idt,"#/$defs/id_token_code"))
print("id + sid (expect err)", val(c,dict(idt,sid="s"),"#/$defs/id_token_code"))
a=s["audit-events.schema.json"]
env=lambda t,o,det,**kw: dict({"event_id":u(),"schema":"cachd.log","schema_version":"1.0.0","category":"event","event_type":t,"occurred_at":"2026-11-02T19:40:00Z","outcome":o,"is_audit":True,"actor":{"user_id":u(),"session_id":"ls1"},"source":{"service":"login-standin"},"details":det},**kw)
print("denied attestation_invalid", val(a,env("auth.app_access.denied","denied",{"reason":"attestation_invalid"},target={"type":"app","id":"cash_advance"})))
print("denied bogus reason (expect err)", bool(val(a,env("auth.app_access.denied","denied",{"reason":"tenant_mismatch"},target={"type":"app","id":"cash_advance"}))))
print("signed_in hub_handoff", val(a,env("auth.app.signed_in","success",{"method":"hub_handoff","auth_time":990,"amr":["mfa"]},target={"type":"app","id":"cash_advance"},action="login")))
print("login.failed remote_session", val(a,env("auth.login.failed","failure",{"username_hmac":"a"*64,"reason":"remote_session","provider_kind":"database"},actor={"user_id":None})))
print("login.failed with password (expect err)", bool(val(a,env("auth.login.failed","failure",{"username_hmac":"a"*64,"reason":"locked","provider_kind":"database","password":"x"},actor={"user_id":None}))))
print("reuse_detected", val(a,env("auth.refresh.reuse_detected","failure",{})))
print("unknown event (expect err)", bool(val(a,env("auth.foo.bar","success",{}))))
print("denied notfromhub", val(a,env("auth.app_access.denied","denied",{"reason":"notfromhub"},target={"type":"app","id":"cash_advance"})))
print("envelope cachd_client_id", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"cachd_client_id":u(),"partner_id":u()})))
print("envelope cachd_client_id not uuid (expect err)", bool(val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"cachd_client_id":"x"}))))
print("reset_requested", val(a,env("auth.password.reset_requested","success",{"channel":"email"})))
print("provider.changed", val(a,env("auth.provider.changed","success",{"location_id":u(),"before":{"kind":"database"},"after":{"kind":"entra"},"approval_id":"ap1"})))
print("provider.changed no approval (expect err)", bool(val(a,env("auth.provider.changed","success",{"location_id":u(),"before":{},"after":{}}))))
for t in ["auth.identity.linked","auth.identity.unlinked","auth.bootstrap.completed","auth.mfa.policy_exclusion_detected","auth.provider.change_requested"]:
    print("not-emitted", t, val(a,env(t,"success",{})))
print("iam other (expect err)", bool(val(a,env("iam.role.assigned","success",{}))))
at=s["device-attestation.schema.json"]
good={"header":{"alg":"ES256","kid":"standin-pc-01","typ":"cachd-attestation+jwt"},"claims":{"nonce":"n"*22,"device_id":"standin-pc-01","remote_session":False,"iat":1000,"exp":1060,"aud":"http://localhost:5080/standin","purpose":"signin"}}
print("attest good", val(at,good))
print("attest HS256 (expect err)", bool(val(at,dict(good,header=dict(good["header"],alg="HS256")))))
print("attest jwk embedded (expect err)", bool(val(at,dict(good,header=dict(good["header"],jwk={})))))
print("attest no nonce (expect err)", bool(val(at,dict(good,claims={k:v for k,v in good["claims"].items() if k!="nonce"}))))
print("attest extra claim (expect err)", bool(val(at,dict(good,claims=dict(good["claims"],jti="x")))))
print("attest remote_session true (schema-valid)", val(at,dict(good,claims=dict(good["claims"],remote_session=True))))

O=s["openid-configuration.schema.json"]
print("disc stand-in exact keys", val(O,disc,"#/$defs/standin_exact_keys"))
print("disc stand-in extra key (expect err)", bool(val(O,dict(disc,foo=1),"#/$defs/standin_exact_keys")))
print("disc iss_parameter false (expect err)", bool(val(O,dict(disc,authorization_response_iss_parameter_supported=False))))
print("disc missing iss_parameter (expect err)", bool(val(O,{k:v for k,v in disc.items() if k!="authorization_response_iss_parameter_supported"})))
print("disc scopes_supported (expect err)", bool(val(O,dict(disc,scopes_supported=["openid"]))))
print("access aud array (expect err)", bool(val(c,dict(cachd,aud=["cash-advance-api"]),"#/$defs/access_token_cachd")))
print("access aud unknown (expect err)", bool(val(c,dict(cachd,aud="other-api"),"#/$defs/access_token_cachd")))
print("amr wiaormfa (expect err)", bool(val(c,dict(cachd,amr=["wiaormfa"]),"#/$defs/access_token_cachd")))
print("login.succeeded attestation=invalid", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"],"attestation":"invalid"})))
print("login.succeeded attestation=bogus (expect err)", bool(val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"],"attestation":"trusted"}))))
print("session.revoked user_disabled", val(a,env("auth.session.revoked","success",{"reason":"user_disabled"})))
print("iam.permission.other (expect err)", bool(val(a,env("iam.permission.granted","success",{}))))
for pu,au in [("handoff","http://localhost:5080/standin"),("cage_action","cash-advance-server")]:
    print("attest purpose",pu, val(at,dict(good,claims=dict(good["claims"],purpose=pu,aud=au))))
print("attest wrong purpose (expect err)", bool(val(at,dict(good,claims=dict(good["claims"],purpose="login")))))
print("attest aud array (expect err)", bool(val(at,dict(good,claims=dict(good["claims"],aud=["x"])))))
print("attest no aud (expect err)", bool(val(at,dict(good,claims={k:v for k,v in good["claims"].items() if k!="aud"}))))
print("attest no purpose (expect err)", bool(val(at,dict(good,claims={k:v for k,v in good["claims"].items() if k!="purpose"}))))
print("attest empty aud (expect err)", bool(val(at,dict(good,claims=dict(good["claims"],aud="")))))
for alg in ["RS256","ES384","none","PS256"]:
    print("attest alg",alg,"(expect err)", bool(val(at,dict(good,header=dict(good["header"],alg=alg)))))
print("attest crit (expect err)", bool(val(at,dict(good,header=dict(good["header"],crit=["exp"])))))
print("attest no typ (expect err)", bool(val(at,dict(good,header={k:v for k,v in good["header"].items() if k!="typ"}))))
print("NOTE: wrong-aud-for-purpose (e.g. cage_action with login issuer) is a server check (6.7), not schema-expressible without deployment URLs")

# ---- v1-d4 additions [TL Decision 2026-11-02, CR-01/CR-03/CR-21, OQ-15/22/37/38] ----
FAIL=[]
def exp(name, errs, want_err):
    ok = bool(errs)==want_err; print(("PASS " if ok else "FAIL ")+name, errs if not want_err else bool(errs))
    if not ok: FAIL.append(name)
noform=lambda schema,inst,ref: sorted(e.message[:90] for e in V(dict(schema,**{"$ref":ref})).iter_errors(inst))
exp("id_token_code good", val(c,idt,"#/$defs/id_token_code"), False)
exp("id_token_code missing nonce", val(c,{k:v for k,v in idt.items() if k!="nonce"},"#/$defs/id_token_code"), True)
exp("id_token_refresh good (no nonce)", val(c,{k:v for k,v in idt.items() if k!="nonce"},"#/$defs/id_token_refresh"), False)
exp("id_token_refresh with nonce", val(c,idt,"#/$defs/id_token_refresh"), True)
exp("id_token_refresh + sid", val(c,dict({k:v for k,v in idt.items() if k!="nonce"},sid="s"),"#/$defs/id_token_refresh"), True)
exp("old id_token def gone", ["missing"] if "id_token" in c["$defs"] else [], False)
exp("sub not uuid (format on)", val(c,dict(idt,sub="user-1"),"#/$defs/id_token_code"), True)
exp("sub not uuid (format OFF, pattern catches)", noform(c,dict(idt,sub="user-1"),"#/$defs/id_token_code"), True)
exp("sub uuid upper-case ok (format OFF)", noform(c,dict(idt,sub=u().upper()),"#/$defs/id_token_code"), False)
exp("cachd partner_id not uuid (format OFF)", noform(c,dict(cachd,partner_id="p1"),"#/$defs/access_token_cachd"), True)
exp("amr raw ngcmfa (must be mapped to mfa, CR-02)", val(c,dict(cachd,amr=["pwd","ngcmfa"]),"#/$defs/access_token_cachd"), True)
exp("amr recovery-code [pwd] ok", val(c,dict(cachd,amr=["pwd"]),"#/$defs/access_token_cachd"), False)
exp("amr wiaormfa err", val(c,dict(cachd,amr=["pwd","wiaormfa"]),"#/$defs/access_token_cachd"), True)
adm=dict(base,aud="backoffice-api",scope="backoffice_api",client_id="backoffice_bff",partner_id=u())
exp("admin token good (no location_id)", val(c,adm,"#/$defs/access_token_admin"), False)
exp("admin token with location_id", val(c,dict(adm,location_id=u()),"#/$defs/access_token_admin"), True)
exp("admin token aud cash-advance-api", val(c,dict(adm,aud="cash-advance-api"),"#/$defs/access_token_admin"), True)
exp("cachd token without location_id", val(c,{k:v for k,v in cachd.items() if k!="location_id"},"#/$defs/access_token_cachd"), True)
exp("attest header extra key (kid2)", val(at,dict(good,header=dict(good["header"],kid2="x"))), True)
exp("attest header extra key (x5t)", val(at,dict(good,header=dict(good["header"],x5t="x"))), True)
exp("attest header exact keys", val(at,good), False)
exp("attest no typ", val(at,dict(good,header={k:v for k,v in good["header"].items() if k!="typ"})), True)
exp("attest no aud", val(at,dict(good,claims={k:v for k,v in good["claims"].items() if k!="aud"})), True)
exp("attest device_id 128 chars ok", val(at,dict(good,header=dict(good["header"],kid="d"*128),claims=dict(good["claims"],device_id="d"*128))), False)
exp("attest device_id 129 chars", val(at,dict(good,claims=dict(good["claims"],device_id="d"*129))), True)
exp("audit auth.provider.change_requested", val(a,env("auth.provider.change_requested","success",{})), False)
exp("audit old iam.permission.change_requested", val(a,env("iam.permission.change_requested","success",{})), True)
exp("audit scope with location_ids", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"partner_id":u(),"cachd_client_id":u(),"location_ids":[u()]})), False)
exp("audit scope.client_id (old name)", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"client_id":u()})), True)
exp("audit event_id not uuid (format OFF)", sorted(e.message for e in V(a).iter_errors(dict(env("auth.refresh.reuse_detected","failure",{}),event_id="e1"))), True)
# [TL Decision 2026-11-02 8:26 PM] accepted proposals, admin super/partner, location mapping
su=s["standin-ui.schema.json"]
SUP,PAR="#/$defs/access_token_admin_super","#/$defs/access_token_admin_partner"
nop={k:v for k,v in adm.items() if k!="partner_id"}
exp("super admin without partner_id ok", val(c,nop,SUP), False)
exp("super admin with location_id", val(c,dict(nop,location_id=u()),SUP), True)
exp("partner admin with partner_id ok", val(c,adm,PAR), False)
exp("partner admin without partner_id", val(c,nop,PAR), True)
exp("token location_id as array", val(c,dict(cachd,location_id=[u()]),"#/$defs/access_token_cachd"), True)
exp("audit scope singular location_id", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"partner_id":u(),"location_id":u()})), True)
exp("audit scope location_ids two locations ok", val(a,env("auth.login.succeeded","success",{"provider_kind":"database","amr":["pwd"]},scope={"partner_id":u(),"location_ids":[u(),u()]})), False)
for nm,ref,good_i,bad_i in [
 ("clock GET",'clock_get',{"now":"2026-11-02T12:00:00Z"},{"time":"2026-11-02T12:00:00Z"}),
 ("password violations",'password_violations',{"violations":["min_length","reused"]},{"violations":["too_short"]}),
 ("testid",'testid',"totp-code","totp_code"),
 ("testid hub tile",'testid',"hub-tile-cash_advance","hub-tile-"),
 ("totp form",'totp_form',{"totp_code":"123456","__RequestVerificationToken":"t"},{"code":"123456","__RequestVerificationToken":"t"}),
 ("agent timeout 3 s",'agent_timeout_config',{"AgentTimeout":"00:00:03"},{"AgentTimeout":3}),
 ("cage action path",'cage_action_path',"/cage/actions/id_scan","/cage/actions/refund"),
 ("cage nonce",'cage_nonce_response',{"nonce":"A"*22},{"nonce":"A"*21+"="}),
 ("cage action request",'cage_action_request',{"device_attestation":"a.b.c"},{"attestation":"a.b.c"}),
 ("cage action ok",'cage_action_ok',{"ok":True},{"ok":False}),
 ("cage action denied",'cage_action_denied',{"error":"attestation_invalid"},{"error":"forbidden"})]:
    exp(nm+" good", val(su,good_i,"#/$defs/"+ref), False)
    exp(nm+" bad", val(su,bad_i,"#/$defs/"+ref), True)
assert su["$defs"]["agent_timeout_config"]["properties"]["AgentTimeout"]["default"]=="00:00:03"
print("v1-d4 checks:", "ALL PASS" if not FAIL else "FAILED: %s" % FAIL)
sys.exit(1 if FAIL else 0)
