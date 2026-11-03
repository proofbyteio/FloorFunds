// SSE2 UI01-05: real-browser checks of the stand-in sign-in pages (ui-driver.md §1-3, contract 5.5/5.8/6.5, OQ-58).
// Driver: playwright-core (no test runner). Env: LOGIN_FRONT_URL, LOGIN_STANDIN_CONTROL_URL, LOGIN_STANDIN_FIXTURES,
// PW_CORE (playwright-core dir), PW_CHROME (chromium executable). Prints PASS/FAIL lines and "TOTAL p / n"; exit 1 on any FAIL.
import { createRequire } from 'module'; import crypto from 'crypto'; import fs from 'fs';
const require = createRequire(import.meta.url);
const env = k => process.env[k] || (() => { console.error(`${k} unset`); process.exit(2); })();
const FRONT = env('LOGIN_FRONT_URL'), CTL = env('LOGIN_STANDIN_CONTROL_URL');
const fx = JSON.parse(fs.readFileSync(env('LOGIN_STANDIN_FIXTURES')));
const { chromium } = require(env('PW_CORE'));
const pw = u => fx.users.find(x => x.username === u).password;
const ISS = FRONT + '/standin';
let n = 0, p = 0; const ok = (name, cond, ev = '') => { n++; if (cond) p++; console.log(`${cond ? 'PASS' : 'FAIL'} ${name}${cond ? '' : ' :: ' + JSON.stringify(ev).slice(0, 300)}`); };
const ctl = async (m, path, body) => { const r = await fetch(CTL + path, { method: m, headers: body ? { 'content-type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined }); return r.headers.get('content-type')?.includes('json') ? r.json() : r.status; };
const totp = async u => (await ctl('GET', `/_standin/users/${u}/totp`)).code;
const b64 = x => x.toString('base64url');
const browser = await chromium.launch({ executablePath: env('PW_CHROME') });
async function ctx(mode, slug = 'pci-main') {
  const c = await browser.newContext(); const viol = [];
  if (mode) { const id = 'standin-ui-' + crypto.randomBytes(4).toString('hex'); const r = await ctl('PUT', `/_standin/pcs/${id}`, { mode, location_slug: slug });
    await c.addCookies([{ name: r.cookie.name, value: r.cookie.value, url: FRONT }]); }
  await c.addInitScript(() => document.addEventListener('securitypolicyviolation', e => console.log('CSPVIOLATION ' + e.violatedDirective + ' ' + e.blockedURI)));
  const pg = await c.newPage();
  pg.on('console', m => { if (/CSPVIOLATION|Refused to/.test(m.text())) viol.push(m.text()); });
  pg.on('pageerror', e => viol.push('pageerror ' + e.message));
  return { c, pg, viol };
}
const DONE = ['screen-totp', 'screen-8a', 'screen-8b', 'screen-hub'].map(x => `[data-testid=${x}]`).join(',') + ',[data-testid=error-area]:visible';
const tid = async (pg, id) => pg.locator(`[data-testid="${id}"]`);
async function password(pg, user, pass, slug = 'pci-main') {
  await pg.goto(`${ISS}/l/${slug}`);
  await pg.fill('[data-testid=signin-username]', user); await pg.fill('[data-testid=signin-password]', pass);
  const posted = pg.waitForRequest(r => r.method() === 'POST' && r.url() === `${ISS}/l/${slug}`, { timeout: 10000 });
  await pg.click('[data-testid=signin-submit]'); const req = await posted; await req.response();
  await pg.waitForSelector(DONE, { timeout: 10000 }).catch(() => {});
  return new URLSearchParams(req.postData() || '');
}
try {
  await ctl('POST', '/_standin/reset');
  // UI01: full browser sign-in with the real page script, fake agent (mode valid), TOTP, hub.
  { const { c, pg, viol } = await ctx('valid');
    const agent = pg.waitForResponse(r => r.url().includes('/_standin/agent/attest') && r.request().method() === 'POST');
    const form = await password(pg, 'cashier_db', pw('cashier_db')); const ar = await agent;
    ok('UI01 page script asked the fake agent (200) and posted a non-empty device_attestation', ar.status() === 200 && (form.get('device_attestation') || '').split('.').length === 3, { st: ar.status() });
    ok('UI01 password step -> screen-totp', await (await tid(pg, 'screen-totp')).count() === 1);
    await pg.fill('[data-testid=totp-code]', await totp('cashier_db')); await pg.click('[data-testid=totp-submit]'); await pg.waitForLoadState('load');
    ok('UI01 TOTP step -> screen-hub with hub-tile-cash_advance', await (await tid(pg, 'screen-hub')).count() === 1 && await (await tid(pg, 'hub-tile-cash_advance')).count() === 1);
    ok('UI01 no CSP violation or page error on sign-in/TOTP/hub (OQ-58)', viol.length === 0, viol);
    // UI04: hub tile -> app (BFF emulated by route) -> /authorize with hub_launch -> interstitial script -> redirect_uri with code+state.
    const cl = fx.clients.find(x => x.client_id === 'cash_advance_bff'), ru = cl.redirect_uris[0];
    const ver = b64(crypto.randomBytes(32)), ch = b64(crypto.createHash('sha256').update(ver).digest()), st = 'ui04-' + crypto.randomBytes(6).toString('hex');
    let landed = null; // Playwright does not route redirect hops, so the final 302 target is captured from the request event.
    pg.on('request', r => { if (r.url().startsWith(ru + '?')) landed = new URL(r.url()); });
    await pg.route('https://cash-advance.fixture.invalid/**', async r => {
      const u = new URL(r.request().url());
      if (u.pathname === new URL(ru).pathname) { landed = u; return r.fulfill({ status: 200, contentType: 'text/plain', body: 'stub' }); }
      const q = new URLSearchParams({ response_type: 'code', client_id: 'cash_advance_bff', redirect_uri: ru, scope: 'openid', state: st, nonce: 'n-ui04', code_challenge: ch, code_challenge_method: 'S256', hub_launch: u.searchParams.get('hub_launch') });
      return r.fulfill({ status: 302, headers: { location: `${ISS}/connect/authorize?${q}` } });
    });
    if (process.env.UI_DEBUG) { pg.on('response', r => console.log('dbg', r.status(), r.request().method(), r.url().slice(0, 140), r.headers().location || '')); pg.on('requestfailed', r => console.log('dbgfail', r.url().slice(0,140), r.failure()?.errorText)); }
    await pg.click('[data-testid=hub-tile-cash_advance]');
    for (let i = 0; i < 150 && !landed; i++) await pg.waitForTimeout(100);
    ok('UI04 hub tile -> hand-off interstitial (script + fake agent) -> redirect_uri with code and the same state', !!landed && !!landed.searchParams.get('code') && landed.searchParams.get('state') === st, { url: pg.url().slice(0, 200), landed: String(landed) });
    ok('UI04 no CSP violation or page error on the interstitial', viol.length === 0, viol);
    await c.close(); }
  // UI02: wrong password -> generic error in error-area, still on screen-signin; only the contract string is asserted.
  { const { c, pg, viol } = await ctx('valid');
    await password(pg, 'cashier_db', 'wrong-' + crypto.randomBytes(6).toString('hex'));
    const e = await tid(pg, 'error-area'); const txt = (await e.count()) ? (await e.innerText()).trim() : null;
    ok('UI02 wrong password -> screen-signin + visible error-area "Sign-in didn\'t work."', await (await tid(pg, 'screen-signin')).count() === 1 && await e.isVisible() && /Sign-in didn.t work\./.test(txt || ''), txt);
    ok('UI02 no CSP violation', viol.length === 0, viol); await c.close(); }
  // UI03: screen 8A (remote session) and 8B (agent not answering: page times out, submits empty attestation).
  { const { c, pg } = await ctx('remote_session'); await password(pg, 'cashier_db', pw('cashier_db'));
    ok('UI03 remote_session PC -> screen-8a with "Try again after the remote session ends"', await (await tid(pg, 'screen-8a')).count() === 1 && /Try again after the remote session ends/.test(await pg.content())); await c.close(); }
  { const { c, pg } = await ctx('missing'); const t0 = Date.now(); const form = await password(pg, 'cashier_db', pw('cashier_db'));
    ok('UI03 agent not answering at plain sign-in -> page submits empty device_attestation and sign-in goes ahead to screen-totp, no 8A (contract 5.5/6.2; 8B is the hand-off/cage variant)', form.get('device_attestation') === '' && await (await tid(pg, 'screen-totp')).count() === 1 && await (await tid(pg, 'screen-8a')).count() === 0, { ms: Date.now() - t0 }); await c.close(); }
  // UI05: /admin in a browser (password + TOTP) and framing refused (frame-ancestors 'none' / XFO DENY).
  { const { c, pg, viol } = await ctx(null);
    await pg.goto(`${ISS}/admin`); ok('UI05 /admin renders page-admin-sign-in', await (await tid(pg, 'page-admin-sign-in')).count() === 1);
    await pg.fill('[data-testid=field-admin-username]', 'partner_admin'); await pg.fill('[data-testid=field-admin-password]', pw('partner_admin'));
    await pg.click('[data-testid=admin-signin-submit]'); await pg.waitForLoadState('load');
    await pg.fill('[data-testid=field-admin-totp]', await totp('partner_admin')); await pg.click('[data-testid=admin-totp-submit]'); await pg.waitForLoadState('load');
    ok('UI05 /admin password + TOTP -> screen-hub', await (await tid(pg, 'screen-hub')).count() === 1);
    ok('UI05 no CSP violation on /admin', viol.length === 0, viol);
    const f = await c.newPage(); await f.goto(`${CTL}/_standin/clock`).catch(() => {});
    await f.setContent(`<iframe id=x src="${ISS}/l/pci-main"></iframe><iframe id=y src="${ISS}/admin"></iframe>`); await f.waitForTimeout(1500);
    const framed = await Promise.all(f.frames().slice(1).map(async fr => { try { return await fr.locator('[data-testid]').count(); } catch { return 0; } }));
    ok('UI05 sign-in and /admin refuse to render inside a frame', framed.every(x => x === 0), framed); await c.close(); }
} catch (e) { ok('UI harness error', false, String(e.stack || e)); }
await browser.close();
console.log(`TOTAL ${p} / ${n}`); process.exit(p === n ? 0 : 1);
