exec(open('/tmp/sse2-work/probe3/prelude.py').read())
def capcase(b,v,pid,label,exp_status,exp_reason):
    n=len(evlist()); s,p=capset(b,v,pid); ev=evlist(n)
    dn=[e for e in ev if e['event_type']=='auth.config.change_denied']; ch=[e for e in ev if e['event_type']=='auth.config.session_cap_changed']
    got=(s,dn[0]['details']['reason'] if dn else ('changed' if ch else None))
    ok(f'K {label}: value={v!r} -> {exp_status}/{exp_reason}',got==(exp_status,exp_reason) and not audit_ok(ev),(got,audit_ok(ev)))
ctl('POST','/_standin/reset')
bp,_=adm_sess('partner_admin'); bl,_=loc_sess()
for v in ('99999999.00:00:00','00:60:00','12:00','12:00:00.5','P1D','-01:00:00'):
    capcase(bp,v,'',f'format {v}',400,None)
capcase(bl,'08:00:00','','location user',403,'not_admin'); capcase(bl,'08:00:00',P1,'location user naming partner',403,'not_admin')
s,p=capset(bl,'zz',''); print('INFO location user malformed value ->',s,'(validation before authorization; no event)')
# DoS boundary: largest cap that still works for a P1 session, set by the partner admin for its own partner
for v in ('3650.00:00:00','100000.00:00:00','2000000.00:00:00','2914000.00:00:00','2915000.00:00:00','10675199.00:00:00'):
    ctl('POST','/_standin/reset'); bp,_=adm_sess('partner_admin'); s0,_=capset(bp,v,''); bl,_=loc_sess(); s,_,_=bl.req('GET','/standin/apps'); s2,_=capget(bp)
    print(f'INFO K-DoS partner cap {v}: set->{s0}; P1 location /apps->{s}; partner admin cap page->{s2}')
    ok(f'K-DoS partner cap {v} does not 500 P1 sessions',s!=500 and s2!=500,(s0,s,s2))
ctl('POST','/_standin/reset'); bs,_=adm_sess('super_admin'); capset(bs,'10675199.00:00:00',P2); b,p=loc_sess(); s,_,_=b.req('GET','/standin/apps')
print('INFO K-DoS super sets P2 huge; P1 location /apps ->',s)
lg=open('/tmp/sse2-si1-3feb66c/standin.log').read(); m=re.findall(r'(?:ArgumentOutOfRange|Overflow)[^\n]{0,200}',lg); print('INFO log exception:',m[-1][:220] if m else None)
ctl('POST','/_standin/reset'); print('TOTAL',sum(1 for _,c in R if c),'/',len(R))
