# SSE2 independent focus probes (3feb66c; re-check 9b7efc9 G1-G9) (TL 10:08 focus list). Black-box only, base from STANDIN_PORT_BASE (guard in prelude).
import os as _os; exec(open(_os.path.join(_os.path.dirname(_os.path.abspath(__file__)),'prelude.py')).read())
import base64 as b64
def enc(o): return b64.urlsafe_b64encode(json.dumps(o,separators=(',',':')).encode()).rstrip(b'=').decode()
def dec(x): return json.loads(b64.urlsafe_b64decode(x+'='*(-len(x)%4)))
def agent(nonce):
    s,r=ctl('POST','/_standin/agent/attest',{'nonce':nonce}); return r['device_attestation'] if s==200 else None
def denied_ev(ev): return [e for e in ev if e['event_type']=='auth.app_access.denied']
ALLEV=[]
_ctl0=ctl
def ctl(m,p,obj=None):
    if m=='POST' and p=='/_standin/reset':
        s,e=_ctl0('GET','/_standin/audit?since=0'); ALLEV.extend(e['events'] if isinstance(e,dict) else e)
    return _ctl0(m,p,obj)
# ---------------- cage attestation fail-closed (BP-2a) ----------------
ctl('POST','/_standin/reset')
for act in ('payment','id_scan','print'):
    b,_=loc_sess(); pc(b,'standin-pc-c-'+act,'valid')
    _,j=cage(b,'attest-nonce'); good=agent(j['nonce']); h,p,sg=good.split('.')
    ok(f'C0 {act}: cage nonce is 22-char base64url (cage_nonce_response)',re.fullmatch(r'[A-Za-z0-9_-]{22}',j['nonce']),j)
    cases={
      'missing':None,'empty':'',
      'alg_none':enc({'alg':'none','kid':dec(h)['kid'],'typ':'cachd-attestation+jwt'})+'.'+p+'.',
      'alg_none_nosig':enc({'alg':'none','kid':dec(h)['kid'],'typ':'cachd-attestation+jwt'})+'.'+p,
      'alg_HS256':enc({'alg':'HS256','kid':dec(h)['kid'],'typ':'cachd-attestation+jwt'})+'.'+p+'.'+sg,
      'alg_RS256':enc({'alg':'RS256','kid':dec(h)['kid'],'typ':'cachd-attestation+jwt'})+'.'+p+'.'+sg,
      'alg_es256_lower':enc({'alg':'es256','kid':dec(h)['kid'],'typ':'cachd-attestation+jwt'})+'.'+p+'.'+sg,
      'extra_hdr_jwk':enc(dict(dec(h),jwk={'kty':'EC'}))+'.'+p+'.'+sg,
      'crit':enc(dict(dec(h),crit=['x']))+'.'+p+'.'+sg,
      'bad_sig_flip':h+'.'+p+'.'+('A' if sg[0]!='A' else 'B')+sg[1:],
      'payload_tamper_remote':h+'.'+enc(dict(dec(p),remote_session=False))+'.'+sg,
      'garbage':'not.a.jws','two_parts':h+'.'+p,
    }
    for name,jw in cases.items():
        _,j=cage(b,'attest-nonce'); n=len(evlist())
        body={} if jw is None else {'device_attestation':jw}
        s,r=cage(b,'actions/'+act,body); ev=evlist(n)
        ok(f'C1 {act} {name}: 403 attestation_invalid + auth.app_access.denied attestation_invalid (schema-valid)',s==403 and r=={'error':'attestation_invalid'} and [x['details']['reason'] for x in denied_ev(ev)]==['attestation_invalid'] and not audit_ok(ev),(s,r,reasons(ev)))
    # the original good JWS was for an earlier (now consumed) nonce: replay must fail
    s,r=cage(b,'actions/'+act,{'device_attestation':good}); ok(f'C2 {act}: JWS for a nonce consumed by an earlier failed attempt -> 403',s==403,(s,r))
    # bad_signature / expired (stale iat) / replayed / unknown / revoked / other_location modes
    for mode in ('bad_signature','expired','replayed','unknown_device','revoked_device','other_location'):
        pc(b,f'standin-pc-m-{act}-{mode}'[:60],mode); _,j=cage(b,'attest-nonce'); jw=agent(j['nonce'])
        s,r=cage(b,'actions/'+act,{'device_attestation':jw or ''}); ok(f'C3 {act} mode {mode} -> 403 attestation_invalid',s==403 and r=={'error':'attestation_invalid'},(s,r,jw is not None))
    pc(b,'standin-pc-c-'+act,'valid')
    # cross-purpose: a signin-purpose JWS (aud issuer) on a cage action
    bb=B(); pc(bb,'standin-pc-xp-'+act,'valid'); st_,_,html=bb.req('GET','/standin/l/pci-main'); t=af(html)
    _,_,nn=bb.req('POST','/standin/l/pci-main/attest-nonce',{'__RequestVerificationToken':t}); sj=agent(json.loads(nn)['nonce'])
    _,j=cage(b,'attest-nonce'); s,r=cage(b,'actions/'+act,{'device_attestation':sj}); ok(f'C4 {act}: signin-purpose JWS on cage action -> 403',s==403,(s,r,dec(sj.split('.')[1]).get('purpose')))
    # cross-session: nonce + JWS of session A submitted in session B
    b2,_=loc_sess(); pc(b2,'standin-pc-c2-'+act,'valid'); _,ja=cage(b,'attest-nonce'); jwa=agent(ja['nonce'])
    s,r=cage(b2,'actions/'+act,{'device_attestation':jwa}); ok(f'C5 {act}: session A nonce/JWS used in session B -> 403',s==403,(s,r))
    s,r=cage(b,'actions/'+act,{'device_attestation':jwa}); ok(f'C5b {act}: ... and A can still use it (B attempt did not touch A tx) -> 200 ok',s==200 and r=={'ok':True},(s,r))
    # nonce age boundary (frozen ms clock, OQ-55 style): 60000 ms ok, 60001 ms refused
    for ms,exp in ((60000,200),(60001,403)):
        ctl('POST','/_standin/clock/freeze',{'frozen':True}); _,j=cage(b,'attest-nonce'); jw=agent(j['nonce']); ctl('POST','/_standin/clock/advance',{'ms':ms})
        s,r=cage(b,'actions/'+act,{'device_attestation':jw}); ctl('POST','/_standin/clock/freeze',{'frozen':False})
        ok(f'C6 {act}: cage nonce age {ms} ms -> {exp}',s==exp,(s,r))
    _,j=cage(b,'attest-nonce'); jw=agent(j['nonce']); s1,r1=cage(b,'actions/'+act,{'device_attestation':jw}); s2,r2=cage(b,'actions/'+act,{'device_attestation':jw})
    ok(f'C7 {act}: valid -> 200 ok, exact replay -> 403',s1==200 and r1=={'ok':True} and s2==403,(s1,r1,s2,r2))
    # no antiforgery
    s,_,d=b.req('POST','/standin/cage/actions/'+act,{'device_attestation':jw}); ok(f'C8 {act}: no antiforgery (form) -> 400',s==400,s)
    _,j=cage(b,'attest-nonce'); jw=agent(j['nonce']); s,r=cage(b,'actions/'+act,{'device_attestation':jw},token=False); ok(f'C8b {act}: JSON body, no RequestVerificationToken header -> 400',s==400,(s,r))
    s,r=cage(b,'actions/'+act,{'device_attestation':jw}); ok(f'C8c {act}: ... and the nonce was not consumed by the 400 (valid JSON retry -> 200)',s==200,(s,r))
    _,j=cage(b,'attest-nonce'); jw=agent(j['nonce']); n=len(evlist()); s,r=cage(b,'actions/'+act,{'device_attestation':jw},form=True); ev=evlist(n)
    ok(f'C8d {act}: valid JWS in old form body -> 403 attestation_invalid (A73: form counts as missing) + schema-valid denied event',s==403 and r=={'error':'attestation_invalid'} and len(denied_ev(ev))==1 and not audit_ok(ev),(s,r,reasons(ev)))
    _,j=cage(b,'attest-nonce'); jw=agent(j['nonce']); s,r=cage(b,'actions/'+act,{'device_attestation':jw,'x':1}); ok(f'C8e {act}: JSON with an extra key -> 403 (A73)',s==403,(s,r))
    for bad in ('{"device_attestation":123}','[]','not json','{"device_attestation":null}'):
        _,p0=capget(b); _,j=cage(b,'attest-nonce'); s,_,d=b.req('POST','/standin/cage/actions/'+act,bad,headers={'Content-Type':'application/json','RequestVerificationToken':af(p0)})
        ok(f'C8f {act}: malformed JSON body {bad!r} -> 403 (never 200/500)',s==403,(s,d[:100]))
    # CONTRACT SHAPE: JSON body {"device_attestation":...} (6.9 / cage_action_request) with antiforgery header
    _,p0=capget(b); tok_=af(p0); _,j=cage(b,'attest-nonce'); jw=agent(j['nonce'])
    s,_,d=b.req('POST','/standin/cage/actions/'+act,json.dumps({'device_attestation':jw}),headers={'Content-Type':'application/json','RequestVerificationToken':tok_})
    print(f'INFO C9 {act}: contract JSON body + valid JWS -> {s} {d[:80]} (contract 6.9 says 200 {{"ok":true}})')
    ok(f'C9 {act}: contract-shaped JSON request with a valid attestation -> 200 {{"ok":true}} (6.9, A72)',s==200 and json.loads(d)=={'ok':True},(s,d[:120]))
    # remote_session=true, validly signed
    pc(b,'standin-pc-rs-'+act,'remote_session'); _,j=cage(b,'attest-nonce'); jw=agent(j['nonce'])
    n=len(evlist()); s,r=cage(b,'actions/'+act,{'device_attestation':jw}); ev=evlist(n); print(f'INFO C10 {act}: valid-signature remote_session=true attestation on cage action -> {s} {r} (contract 6.7 silent; 5.2 requires remote_session=false for hand-off)')
    ok(f'C10 {act}: remote_session=true attestation is NOT accepted for a cage action (403 attestation_invalid)',s==403 and r=={'error':'attestation_invalid'},(s,r))
    d_=denied_ev(ev); ok(f'C10b {act}: audit auth.app_access.denied reason remote_session, target cash_advance, schema-valid (A70)',len(d_)==1 and d_[0]['details'].get('reason')=='remote_session' and not audit_ok(ev),(reasons(ev),audit_ok(ev),d_[:1]))
    s,r=cage(b,'actions/'+act,{'device_attestation':jw}); ok(f'C10c {act}: the remote_session JWS cannot be replayed (nonce consumed)',s==403,(s,r))
    pc(b,'standin-pc-c-'+act,'valid')
# ---------------- session cap (DEC-086) ----------------
ctl('POST','/_standin/reset')
bp,_=adm_sess('partner_admin'); bs,_=adm_sess('super_admin'); bl,_=loc_sess()
def capcase(b,v,pid,label,exp_status,exp_reason):
    n=len(evlist()); s,p=capset(b,v,pid); ev=evlist(n)
    dn=[e for e in ev if e['event_type']=='auth.config.change_denied']; ch=[e for e in ev if e['event_type']=='auth.config.session_cap_changed']
    got=(s,dn[0]['details']['reason'] if dn else ('changed' if ch else None))
    ok(f'K {label}: value={v!r} partner={pid[:12] if pid else "-"} -> {exp_status}/{exp_reason}',got==(exp_status,exp_reason) and not audit_ok(ev),(got,audit_ok(ev)))
    return s,p
capcase(bp,'08:00:00',P2,'partner admin -> other partner','403','x') if False else None
capcase(bp,'08:00:00',P2,'partner admin -> other partner',403,'wrong_partner')
capcase(bp,'08:00:00',P2.upper(),'partner admin -> other partner upper-case',403,'wrong_partner')
capcase(bp,'08:00:00','5f0c3e1a-dead-4000-8000-000000000009','partner admin -> unknown partner',403,'wrong_partner')
capcase(bp,'08:00:00',' '+P1+' ','partner admin -> own partner padded',200,'changed')
capcase(bp,'08:00:00','','partner admin -> empty (own)',200,'changed')
for v,exp in (('00:15:00',(403,'out_of_range')),('00:14:59',(403,'out_of_range')),('00:00:00',(403,'out_of_range')),('00:15:01',(200,'changed')),
              ('-01:00:00',(400,None)),('-00:16:00',(400,None)),('1.00:00:00',(200,'changed')),('23:59:59',(200,'changed')),('1.00:00:01',(403,'out_of_range')),('2.00:00:00',(403,'out_of_range')),('365.00:00:00',(403,'out_of_range')),
              ('99999999.00:00:00',(403,'out_of_range')),('10675199.00:00:00',(403,'out_of_range')),('10675200.00:00:00',(403,'out_of_range')),('99999999999.00:00:00',(403,'out_of_range')),('00:60:00',(403,'out_of_range')),('12:00',(400,None)),('12:00:00.5',(400,None)),('P1D',(400,None))):
    capcase(bp,v,'',f'range partner {v}',*exp)
capcase(bl,'08:00:00','','location user',403,'not_admin')
capcase(bl,'08:00:00',P1,'location user naming own partner',403,'not_admin')
capcase(bl,'zz','','location user, malformed value (G9: caller first)',403,'not_admin')
n=len(evlist()); s,_=capset(bl,'zz',''); ev=evlist(n); dn=ev_of(ev,'auth.config.change_denied')
ok('G9 non-admin malformed: denied event attempted_value 00:00:00 (A69), schema-valid',s==403 and len(dn)==1 and dn[0]['details'].get('attempted_value')=='00:00:00' and not audit_ok(ev),(s,dn[:1],audit_ok(ev)))
n=len(evlist()); s,_=capset(bl,'99999999.00:00:00',''); ev=evlist(n); dn=ev_of(ev,'auth.config.change_denied')
ok('G9 non-admin pattern-shaped huge: not_admin, attempted_value raw, schema-valid',s==403 and len(dn)==1 and dn[0]['details'].get('reason')=='not_admin' and dn[0]['details'].get('attempted_value')=='99999999.00:00:00' and not audit_ok(ev),(s,dn[:1],audit_ok(ev)))
n=len(evlist()); s,_=capset(bp,'zz',''); ev=evlist(n); ok('G9 admin malformed value -> 400, no event',s==400 and not ev,(s,reasons(ev)))
capcase(bs,'08:00:00',P2,'super admin -> partner 2',200,'changed')
capcase(bs,'08:00:00','','super admin -> global',200,'changed')
capcase(bs,'00:10:00','','super admin -> global below idle',403,'out_of_range')
# effect of an absurd cap: does a session still work (overflow?)
ctl('POST','/_standin/reset'); bs,_=adm_sess('super_admin'); s0,_=capset(bs,'10675199.00:00:00',''); ok('K-huge: global cap 10675199 days refused (G1: 403 out_of_range)',s0==403,s0)
s0,pg=capget(bs); ok('K-huge: admin cap page still works afterwards (200, current unchanged)',s0==200 and cur(pg) and '10675199' not in cur(pg),(s0,cur(pg)))
b,p=loc_sess(); s,_,hp=b.req('GET','/standin/apps'); print(f'INFO K-huge: after global cap 10675199 days, location sign-in -> {testid(p)}, /apps -> {s}')
ok('K-huge: a ~29,000-year global cap does not break sign-in (no 500)',testid(p)=='screen-hub' and s==200,(testid(p),s))
ctl('POST','/_standin/clock/advance',{'seconds':3600}); s,_,_=b.req('GET','/standin/apps'); print(f'INFO K-huge: /apps after 1 h idle -> {s} (idle 15 min still applies?)')
ok('K-huge: idle timeout still ends the session under a huge cap',s!=200,s)
ctl('POST','/_standin/reset')
# ---------------- re-enrolment (DEC-093/087) ----------------
def run_reenroll(kind):
    if kind=='location':
        b,p=rc_loc(); home='/standin/l/nevada-main'; user='nevada_db'
    else:
        b=B(); _,_,p0=b.req('GET','/standin/admin'); pw=fx_user('partner_admin')['password']
        _,_,p1=b.req('POST','/standin/admin',{'username':'partner_admin','password':pw,'__RequestVerificationToken':af(p0)})
        rc=fx_user('partner_admin')['recovery_codes'][0]
        _,_,p=b.req('POST','/standin/admin/mfa',{'recovery_code':rc,'__RequestVerificationToken':af(p1)}); home='/standin/admin'; user='partner_admin'
    old=totp(user)['code']; old_rcs=fx_user(user)['recovery_codes']
    s,_,g=b.req('GET',RE); key=key_of(g); t=af(g)
    # other endpoints refused for the recovery session
    s1,_,_=b.req('GET','/standin/admin/session-cap'); ok(f'R0 {kind}: recovery session -> session-cap 404',s1==404,s1)
    n=len(evlist()); s,h,_=b.req('POST',RE,{'totp_code':_code(key),'__RequestVerificationToken':t}); ev=evlist(n)
    ok(f'R1 {kind}: right code -> 302 to recovery-codes; reenrolled + recovery_codes_issued schema-valid',s==302 and h['location'].endswith('/mfa/reenroll/recovery-codes') and [e['event_type'] for e in ev]==['auth.mfa.reenrolled','auth.mfa.recovery_codes_issued'] and not audit_ok(ev),(s,[e['event_type'] for e in ev]))
    s,h,rp=b.req('GET',RE+'/recovery-codes'); codes=re.findall(r'<li><code>([0-9a-f]+)</code></li>',rp)
    nxt=re.findall(r'href="([^"]+)"',rp); print(f'INFO R2 {kind}: recovery-codes page links {nxt}')
    ok(f'R2 {kind}: codes shown once (10), no-store, Continue -> {home} (D93), no link to hub/apps/cage',s==200 and len(codes)==10 and 'no-store' in h.get('cache-control','') and home in nxt and not any(x for x in nxt if '/apps' in x or '/cage' in x),(s,len(codes),nxt))
    ok(f'R2b {kind}: recovery-codes page renders D-93 done screen testid',testid(rp),testid(rp))
    s,_,_=b.req('GET',RE+'/recovery-codes'); ok(f'R3 {kind}: 2nd GET recovery-codes -> 404',s==404,s)
    s,_,_=b.req('GET','/standin/apps'); ok(f'R4 {kind}: session revoked (DEC-087): /apps no longer 200',s!=200,s)
    # old factors refused, new key works, new recovery code works once
    ctl('POST','/_standin/clock/advance',{'seconds':30})
    if kind=='location':
        b3,h3=pw_step(user,'nevada-main'); n=len(evlist())
        _,_,q=b3.req('POST','/standin/l/nevada-main/totp',{'totp_code':old,'__RequestVerificationToken':af(h3)})
        ok(f'R5 {kind}: old TOTP secret refused',testid(q)!='screen-hub',testid(q))
        b4,h4=pw_step(user,'nevada-main'); _,_,q=b4.req('POST','/standin/l/nevada-main/totp',{'recovery_code':old_rcs[1],'__RequestVerificationToken':af(h4)})
        ok(f'R6 {kind}: old recovery code refused',testid(q)!='screen-hub',testid(q))
        b5,h5=pw_step(user,'nevada-main'); _,_,q=b5.req('POST','/standin/l/nevada-main/totp',{'totp_code':_code(key),'__RequestVerificationToken':af(h5)})
        ok(f'R7 {kind}: new TOTP -> hub (fresh sign-in)',testid(q)=='screen-hub',testid(q))
    else:
        b3,p3=admin_pw(None,user); q=admin_mfa(b3,p3,old); ok(f'R5 {kind}: old TOTP refused',testid(q)!='screen-hub',testid(q))
        b4,p4=admin_pw(None,user); _,_,q=b4.req('POST','/standin/admin/mfa',{'recovery_code':old_rcs[1],'__RequestVerificationToken':af(p4)}); ok(f'R6 {kind}: old recovery code refused',testid(q)!='screen-hub',testid(q))
        b5,p5=admin_pw(None,user); q=admin_mfa(b5,p5,_code(key)); ok(f'R7 {kind}: new TOTP -> hub',testid(q)=='screen-hub',testid(q))
    return key,codes
ctl('POST','/_standin/reset'); k1,c1=run_reenroll('location')
ctl('POST','/_standin/reset'); k2,c2=run_reenroll('admin')
# rate limit with window boundary
ctl('POST','/_standin/reset'); ctl('POST','/_standin/clock/freeze',{'frozen':True})
b,p=rc_loc(); _,_,g=b.req('GET',RE); key=key_of(g); t=af(g); n=len(evlist()); sts=[]
for i in range(5): s,_,pp=b.req('POST',RE,{'totp_code':_other(_code(key)),'__RequestVerificationToken':t}); sts.append(s); t=af(pp)
s6,_,pp=b.req('POST',RE,{'totp_code':_code(key),'__RequestVerificationToken':t}); t=af(pp); ev=evlist(n)
ok('RL1 5 wrong -> 200 x5; 6th (right) -> 429 + auth.mfa.failed reason=rate_limited',sts==[200]*5 and s6==429 and ev[-1]['details'].get('reason')=='rate_limited',(sts,s6,reasons(ev)[-2:]))
ctl('POST','/_standin/clock/advance',{'ms':900000-1}); s7,_,pp=b.req('POST',RE,{'totp_code':_code(key),'__RequestVerificationToken':t}); t=af(pp) if s7!=302 else t
ok('RL2 at 14:59.999 still 429',s7==429,s7)
ctl('POST','/_standin/clock/advance',{'ms':1}); s8,h8,_=b.req('POST',RE,{'totp_code':_code(key),'__RequestVerificationToken':t})
print(f'INFO RL3 at exactly 15:00.000 after the 1st failure -> {s8} (idle timeout 15 min may end the session first)')
ctl('POST','/_standin/clock/freeze',{'frozen':False})
# ---------------- event order + no secrets in events/logs ----------------
ctl('POST','/_standin/reset')
for who in ('cashier_db','partner_admin','super_admin'):
    n=len(evlist()); b,p=(loc_sess() if who=='cashier_db' else adm_sess(who)); ev=[e['event_type'] for e in evlist(n)]
    ok(f'EO {who}: mfa.succeeded immediately before login.succeeded',ev[-2:]==['auth.mfa.succeeded','auth.login.succeeded'],ev)
# secrets: everything the driver saw (USED) + re-enrol keys/codes, against audit (whole run, cumulative since last reset only) and the log
sec=set(x for x in USED if len(x)>=6)|{k1,k2}|set(c1)|set(c2)
lg=open(D+'/standin.log').read()
hits=[x for x in sec if x in lg]; ok(f'NS1 none of {len(sec)} secret values (incl. re-enrol keys + new recovery codes) in standin.log',not hits,[h[:6] for h in hits][:5])
ok('NS2 log has no otpauth/secret= / hub_launch= / code= / state= query fragments',not re.search(r'otpauth://|secret=[A-Z2-7]{16}|hub_launch=[A-Za-z0-9_-]{10}|[?&]code=[A-Za-z0-9_-]{10}',lg),re.findall(r'.{30}(?:otpauth://|secret=|hub_launch=|[?&]code=).{10}',lg)[:3])
# CSP / XFO over everything seen
csp_bad=[x for x in SEEN if x[5] and ("'unsafe-inline'" in x[5] or ('script-src' in x[5] and "script-src 'self'" not in x[5]))]
xfo_bad=[x for x in SEEN if x[6] not in (None,'DENY')]
ok(f'CSP: no unsafe-inline, script-src \'self\' wherever script-src is set ({len(SEEN)} responses)',not csp_bad,csp_bad[:3])
ok('XFO: DENY or absent everywhere',not xfo_bad,xfo_bad[:3])
html_nocsp=[x for x in SEEN if x[4].startswith('text/html') and not x[5]]; ok('CSP present on every HTML response',not html_nocsp,html_nocsp[:3])
# discovery identical front/back
s1,_,d1=B().req('GET','/standin/.well-known/openid-configuration'); s2,_,d2=B('127.0.0.1').req('GET','/standin/.well-known/openid-configuration',port=BK)
ok('DISC front == back openid-configuration',s1==s2==200 and json.loads(d1)==json.loads(d2),(s1,s2,set(json.loads(d1))^set(json.loads(d2)) if s1==s2==200 else ''))
# admin error testids
b=B(); _,_,p=b.req('GET','/standin/admin'); _,_,p=b.req('POST','/standin/admin',{'username':'partner_admin','password':'wrong-'+secrets.token_hex(4),'__RequestVerificationToken':af(p)})
print('INFO ADM error testids:',sorted(set(re.findall(r'data-testid="(error-[^"]+)"',p))))
ok('ADM wrong password page carries ui-driver error-area testid',('data-testid="error-area"' in p) or re.search(r'data-testid="[^"]*error-area',p),sorted(set(re.findall(r'data-testid="([^"]+)"',p))))
# G5 (DEC TL 10:35): pending MFA expires after 5 min; code: PreMfaExpired = elapsed ms > 300000 (300000 still valid)
ctl('POST','/_standin/reset')
for ms,live in ((300000,True),(300001,False),(86400000,False)):
    ctl('POST','/_standin/clock/freeze',{'frozen':True}); b,p=admin_pw(None,'partner_admin'); ctl('POST','/_standin/clock/advance',{'ms':ms})
    n=len(evlist()); q=admin_mfa(b,p,totp('partner_admin')['code']); ev=evlist(n); ctl('POST','/_standin/clock/freeze',{'frozen':False})
    got=testid(q); okk=(got=='screen-hub' and b.jar.get('standin_session')) if live else (got!='screen-hub' and not b.jar.get('standin_session') and not ev_of(ev,'auth.login.succeeded'))
    print(f'INFO G5 admin pending MFA {ms} ms -> {got} events={reasons(ev)}')
    ok(f'G5 admin: TOTP {ms} ms after password -> {"accepted" if live else "refused"}',okk and not audit_ok(ev),(got,reasons(ev)))
    ctl('POST','/_standin/reset')
    ctl('POST','/_standin/clock/freeze',{'frozen':True}); b,h=pw_step('cashier_db'); ctl('POST','/_standin/clock/advance',{'ms':ms})
    n=len(evlist()); _,_,q=b.req('POST','/standin/l/pci-main/totp',{'totp_code':totp('cashier_db')['code'],'__RequestVerificationToken':af(h)}); ev=evlist(n); ctl('POST','/_standin/clock/freeze',{'frozen':False})
    got=testid(q); okk=(got=='screen-hub') if live else (got!='screen-hub' and not ev_of(ev,'auth.login.succeeded'))
    print(f'INFO G5 location pending MFA {ms} ms -> {got} events={reasons(ev)}')
    ok(f'G5 location: TOTP {ms} ms after password -> {"accepted" if live else "refused"}',okk and not audit_ok(ev),(got,reasons(ev)))
    ctl('POST','/_standin/reset')
ctl('POST','/_standin/reset')
js=json.dumps(ALLEV); ev_hits=[x for x in sec if x in js]
ok(f'NS3 none of {len(sec)} secret values in {len(ALLEV)} audit events (all resets)',len(ALLEV)>100 and not ev_hits,[h[:6] for h in ev_hits][:5])
print('TOTAL',sum(1 for _,c in R if c),'/',len(R))
