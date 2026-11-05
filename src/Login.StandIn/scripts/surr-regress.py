#!/usr/bin/env python3
# F-SURR-LOGIN regression (TLD 16:48; probe report md5 183220008096d2eb3b07a9edae273024, probes md5 9d7c39df).
# 322 live JSON probes against a running stand-in (the 5 Allowlist startup cases are run by surr-regress.sh).
# Every case asserts: never 500, exact contract status and body, audit-event count, no new "fail:" lines in the log.
# Usage: surr-regress.py BASE STATE_ROOT LOG OUT_JSONL. Secrets are read from the stand-in state dir and never printed.
import http.client, json, re, sys, base64, urllib.parse as up
BASE, STATE, LOG, OUT = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4]
F, BK, CT = BASE, BASE + 1, BASE + 2
GEN = json.load(open(f'{STATE}/standin-{BASE}/fixture-secrets.json'))
PW = GEN['passwords']['cashier_db']
# Forms are JSON string-content text (escapes kept literal); raw_invalid is raw bytes (lone surrogate in CESU form + 0xFF).
FORMS = {'lone_high': b'\\ud83d', 'lone_low': b'\\ude00', 'reversed': b'\\ude00\\ud83d', 'trailing_high': b'abc\\ud83d',
         'dc00_d83d': b'\\udc00\\ud83d', 'raw_invalid': b'\xed\xa0\xbd\xff', 'CTRL_valid_pair': b'\\ud83d\\ude00'}
BAD = [f for f in FORMS if not f.startswith('CTRL')]
INV = {'error': 'invalid_request'}; ATT = {'error': 'attestation_invalid'}; INVC = {'error': 'invalid_client'}

def req(m, port, path, body=None, headers=None, jar=None):
    c = http.client.HTTPConnection('127.0.0.1', port, timeout=20); h = dict(headers or {})
    if jar: h['Cookie'] = '; '.join(f'{k}={v}' for k, v in jar.items())
    c.request(m, path, body=body, headers=h); r = c.getresponse(); d = r.read()
    if jar is not None:
        for k, v in r.getheaders():
            if k.lower() == 'set-cookie':
                n, val = v.split(';')[0].split('=', 1)
                if 'expires=Thu, 01 Jan 1970' in v: jar.pop(n, None)
                else: jar[n] = val
    return r.status, r.getheader('Content-Type') or '', d.decode('utf-8', 'replace')
def ctl(m, p, obj=None):
    s, _, d = req(m, CT, p, json.dumps(obj) if obj is not None else None, {'Content-Type': 'application/json'})
    return s, (json.loads(d) if d.strip().startswith(('{', '[')) else d)
def nev(): return ctl('GET', '/_standin/audit?since=0')[1]['next']
def evs(since): return ctl('GET', f'/_standin/audit?since={since}')[1]['events']
def logfails():
    try: return open(LOG, errors='replace').read().count('fail:')
    except OSError: return 0
def b64(b): return base64.urlsafe_b64encode(b).rstrip(b'=').decode()
def af(html): return re.search(r'name="__RequestVerificationToken" value="([^"]+)"', html).group(1)

R = []
def rec(ep, pl, form, s, ct, body, ev, exp_s, exp_body, exp_ev, lf):
    try: jb = json.loads(body) if body.strip() else None
    except ValueError: jb = None
    fl = []
    if s >= 500 and not (exp_s == 504 and s == 504): fl.append('5xx')
    if s != exp_s: fl.append(f'status!={exp_s}')
    if exp_body not in (None, '') and jb != exp_body: fl.append('body')
    if exp_body == '' and body != '': fl.append('body-not-empty')
    evn = len(ev); ok_ev = exp_ev(ev) if callable(exp_ev) else evn == exp_ev
    if not ok_ev: fl.append(f'events={evn}')
    if lf: fl.append(f'log_fail+{lf}')
    R.append({'endpoint': ep, 'placement': pl, 'form': form, 'status': s, 'ctype': ct, 'body': body[:160], 'events': evn, 'expected': exp_s, 'pass': not fl, 'flag': fl})
    print(('PASS ' if not fl else 'FAIL ') + f'{ep:30} {pl:22} {form:16} {s} ev={evn} ' + ' '.join(fl))

def J(t, x): return t.replace(b'X', x)  # template X -> form text (templates never contain another capital X)

def control():
    cases = [
      ('Control clock/freeze', 'POST', '/_standin/clock/freeze', {
        'value_top': b'{"frozen":"X"}', 'key_top': b'{"X":true}', 'key_top_extra': b'{"frozen":true,"X":1}', 'nested_key': b'{"frozen":{"X":1}}',
        'nested_value': b'{"frozen":{"a":"X"}}', 'array_value': b'{"frozen":["X"]}', 'array_obj_key': b'{"frozen":[{"X":1}]}'}, {}),
      ('Control clock/advance', 'POST', '/_standin/clock/advance', {
        'value_top': b'{"seconds":"X"}', 'key_top': b'{"X":1}', 'nested_key': b'{"seconds":{"X":1}}', 'array_obj_key': b'{"ms":[{"X":1}]}'}, {}),
      ('Control faults', 'PUT', '/_standin/faults', {
        'value_top': b'{"outbox":"X"}', 'value_top2': b'{"device_registry":"X"}', 'key_top': b'{"X":"ok"}', 'nested_key': b'{"outbox":{"X":"ok"}}', 'array_value': b'{"outbox":["X"]}'}, {}),
    ]
    U = b'"password":"%s","status":"active","location_slugs":["pci-main"],"roles":[{"app_code":"cash_advance","role":"cashier"}]'
    upw = b'Surr7Regress9Pass'
    cases.append(('Control users', 'PUT', '/_standin/users/surr_u{i}', {
        'value_password': b'{' + U % (upw + b'X') + b'}',
        'value_status': b'{"password":"' + upw + b'","status":"X","location_slugs":["pci-main"],"roles":[]}',
        'array_value_slug': b'{"password":"' + upw + b'","status":"active","location_slugs":["X"],"roles":[]}',
        'array_obj_value': b'{"password":"' + upw + b'","status":"active","location_slugs":["pci-main"],"roles":[{"app_code":"X","role":"cashier"}]}',
        'array_obj_key': b'{"password":"' + upw + b'","status":"active","location_slugs":["pci-main"],"roles":[{"app_code":"cash_advance","role":"cashier","X":1}]}',
        'key_top': b'{' + U % upw + b',"X":1}', 'nested_unknown_deep': b'{' + U % upw + b',"extra":{"a":[{"b":"X"}]}}'},
        {'value_password': 200, 'array_obj_key': 200, 'key_top': 200, 'nested_unknown_deep': 200}))
    cases.append(('Control pcs', 'PUT', '/_standin/pcs/standin-surr-{i}', {
        'value_mode': b'{"mode":"X","location_slug":"pci-main"}', 'value_slug': b'{"mode":"valid","location_slug":"X"}',
        'key_top': b'{"mode":"valid","location_slug":"pci-main","X":1}', 'nested_unknown': b'{"mode":"valid","location_slug":"pci-main","x":{"y":"X"}}'},
        {'key_top': 200, 'nested_unknown': 200}))
    cases.append(('SignIn agent/attest', 'POST', '/_standin/agent/attest', {
        'value_top': b'{"nonce":"X"}', 'key_top': b'{"X":"n"}', 'nested_unknown': b'{"nonce":"zz","x":{"X":"X"}}'},
        {'value_top': 504, 'key_top': 504, 'nested_unknown': 504}))
    i = 0
    for ep, m, path, pls, ctrl_ok in cases:
        for pl, t in pls.items():
            for f, x in FORMS.items():
                i += 1; n0 = nev(); l0 = logfails()
                s, ct, d = req(m, CT, path.format(i=i), J(t, x), {'Content-Type': 'application/json'})
                ev = evs(n0); lf = logfails() - l0
                if f.startswith('CTRL') and pl in ctrl_ok:
                    es = ctrl_ok[pl]; eb = '' if es == 504 else None
                else: es, eb = 400, INV
                rec(ep, pl, f, s, ct, d, ev, es, eb, 0, lf)
                if ep.startswith('Control clock'): ctl('POST', '/_standin/clock/freeze', {'frozen': False})
                if ep == 'Control faults': ctl('PUT', '/_standin/faults', {'outbox': 'ok', 'device_registry': 'ok'})

def login(jar, pcid):
    s, r = ctl('PUT', f'/_standin/pcs/{pcid}', {'mode': 'valid', 'location_slug': 'pci-main'}); jar['standin_pc'] = r['cookie']['value']
    _, _, h = req('GET', F, '/standin/l/pci-main', jar=jar); t = af(h)
    _, _, n = req('POST', F, '/standin/l/pci-main/attest-nonce', up.urlencode({'__RequestVerificationToken': t}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
    sa, a = ctl('POST', '/_standin/agent/attest', {'nonce': json.loads(n)['nonce']})
    _, _, h = req('POST', F, '/standin/l/pci-main', up.urlencode({'username': 'cashier_db', 'password': PW, 'device_attestation': a['device_attestation'] if sa == 200 else '', '__RequestVerificationToken': t}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
    s, c = ctl('GET', '/_standin/users/cashier_db/totp')
    if s == 409: ctl('POST', '/_standin/clock/advance', {'seconds': 30}); s, c = ctl('GET', '/_standin/users/cashier_db/totp')
    req('POST', F, '/standin/l/pci-main/totp', up.urlencode({'totp_code': c['code'], '__RequestVerificationToken': af(h)}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)

def denied1(ev): return [(e['event_type'], e['details'].get('reason')) for e in ev] == [('auth.app_access.denied', 'attestation_invalid')]

def front():
    H = b'{"alg":"ES256","typ":"cachd-attestation+jwt","kid":"standin-surr-k"}'; P = b'{"nonce":"n","device_id":"standin-surr-k"}'
    sig = 'AAAA'
    # sign-in JWS (form field device_attestation): fails closed; sign-in itself continues (OQ-11) -> 200 page, events >= 1.
    sj = {'hdr_value': lambda x: b64(J(b'{"alg":"ES256","typ":"cachd-attestation+jwt","kid":"X"}', x)) + '.' + b64(P) + '.' + sig,
          'hdr_key': lambda x: b64(J(b'{"alg":"ES256","typ":"cachd-attestation+jwt","X":"k"}', x)) + '.' + b64(P) + '.' + sig,
          'pl_value_nonce(unsigned)': lambda x: b64(H) + '.' + b64(J(b'{"nonce":"X","device_id":"standin-surr-k"}', x)) + '.' + sig}
    for pl, mk in sj.items():
        for f, x in FORMS.items():
            ctl('POST', '/_standin/reset'); jar = {}; ctl('PUT', '/_standin/pcs/standin-surr-k', {'mode': 'valid', 'location_slug': 'pci-main'}); jar['standin_pc'] = 'standin-surr-k'
            _, _, h = req('GET', F, '/standin/l/pci-main', jar=jar); t = af(h)
            req('POST', F, '/standin/l/pci-main/attest-nonce', up.urlencode({'__RequestVerificationToken': t}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
            n0 = nev(); l0 = logfails()
            s, ct, d = req('POST', F, '/standin/l/pci-main', up.urlencode({'username': 'cashier_db', 'password': PW, 'device_attestation': mk(x), '__RequestVerificationToken': t}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
            # OQ-11: sign-in goes ahead; the bad JWS is never trusted -> auth.login.succeeded attestation=invalid after TOTP.
            sc, c = ctl('GET', '/_standin/users/cashier_db/totp')
            if sc == 409: ctl('POST', '/_standin/clock/advance', {'seconds': 30}); sc, c = ctl('GET', '/_standin/users/cashier_db/totp')
            s2, _, _ = req('POST', F, '/standin/l/pci-main/totp', up.urlencode({'totp_code': c['code'], '__RequestVerificationToken': af(d)}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
            ev = evs(n0)
            def att_invalid(e): return [x_['details'].get('attestation') for x_ in e if x_['event_type'] == 'auth.login.succeeded'] == ['invalid']
            rec('SignIn POST /l/{slug} (JWS)', pl, f, s, ct, '', ev, 200, None, att_invalid, logfails() - l0 + (1 if s2 >= 500 else 0))
    # cage/actions (6.9): every malformed body/JWS -> 403 attestation_invalid + exactly one auth.app_access.denied attestation_invalid.
    ctl('POST', '/_standin/reset'); jar = {}; login(jar, 'standin-surr-cage')
    cb = {'value_top': b'{"device_attestation":"X"}', 'key_top': b'{"X":"j"}', 'key_extra': b'{"device_attestation":"j","X":1}',
          'nested': b'{"device_attestation":{"X":"j"}}', 'array': b'{"device_attestation":["X"]}',
          'jws_payload_nonce': lambda x: b'{"device_attestation":"' + (b64(H) + '.' + b64(J(b'{"nonce":"X"}', x)) + '.' + sig).encode() + b'"}',
          'jws_payload_key': lambda x: b'{"device_attestation":"' + (b64(H) + '.' + b64(J(b'{"X":"n"}', x)) + '.' + sig).encode() + b'"}',
          'jws_hdr_kid': lambda x: b'{"device_attestation":"' + (b64(J(b'{"alg":"ES256","typ":"cachd-attestation+jwt","kid":"X"}', x)) + '.' + b64(P) + '.' + sig).encode() + b'"}'}
    for pl, t in cb.items():
        for f, x in FORMS.items():
            _, _, p = req('GET', F, '/standin/admin/session-cap', jar=jar); tok = af(p)
            req('POST', F, '/standin/cage/attest-nonce', up.urlencode({'__RequestVerificationToken': tok}), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
            body = t(x) if callable(t) else J(t, x)
            n0 = nev(); l0 = logfails()
            s, ct, d = req('POST', F, '/standin/cage/actions/payment', body, {'Content-Type': 'application/json', 'RequestVerificationToken': tok}, jar)
            rec('SessionCap cage/actions', pl, f, s, ct, d, evs(n0), 403, ATT, denied1, logfails() - l0)
    # session-cap (form only; a location user is refused).
    for f, x in FORMS.items():
        _, _, p = req('GET', F, '/standin/admin/session-cap', jar=jar); tok = af(p); n0 = nev(); l0 = logfails()
        s, ct, d = req('POST', F, '/standin/admin/session-cap', b'value=' + up.quote_from_bytes(x).encode() + b'&partner_id=&__RequestVerificationToken=' + up.quote(tok).encode(), {'Content-Type': 'application/x-www-form-urlencoded'}, jar)
        rec('SessionCap admin/session-cap', 'form value', f, s, ct, '', evs(n0), 403, None, 1, logfails() - l0)
    # Clients /connect/token private_key_jwt assertion (7.6) -> 401 invalid_client.
    CH = b'{"alg":"RS256","kid":"k","typ":"JWT"}'; CP = b'{"iss":"backoffice_stub","sub":"backoffice_stub","aud":"a","jti":"j","iat":1,"exp":2}'
    cj = {'hdr_value': lambda x: b64(J(b'{"alg":"RS256","kid":"X"}', x)) + '.' + b64(CP) + '.' + sig,
          'hdr_key': lambda x: b64(J(b'{"alg":"RS256","X":"k"}', x)) + '.' + b64(CP) + '.' + sig,
          'claims_value_iss': lambda x: b64(CH) + '.' + b64(J(b'{"iss":"X","sub":"X"}', x)) + '.' + sig,
          'claims_key': lambda x: b64(CH) + '.' + b64(J(b'{"iss":"backoffice_stub","sub":"backoffice_stub","X":1}', x)) + '.' + sig}
    for pl, mk in cj.items():
        for f, x in FORMS.items():
            n0 = nev(); l0 = logfails()
            s, ct, d = req('POST', BK, '/standin/connect/token', up.urlencode({'grant_type': 'client_credentials', 'client_assertion_type': 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', 'client_assertion': mk(x)}), {'Content-Type': 'application/x-www-form-urlencoded'})
            rec('Clients /connect/token', pl, f, s, ct, d, evs(n0), 401, INVC, 0, logfails() - l0)

ctl('POST', '/_standin/reset')
control(); front()
ctl('POST', '/_standin/reset')
with open(OUT, 'w') as o:
    for r in R: o.write(json.dumps(r) + '\n')
np = sum(r['pass'] for r in R); n5 = sum(r['status'] >= 500 and r['status'] != 504 for r in R)
print(f'SURR-REGRESS {np}/{len(R)} pass, {n5} x 5xx (excl. designed 504), {len(R) - np} fail')
sys.exit(0 if np == len(R) and len(R) == 322 else 1)
