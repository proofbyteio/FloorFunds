// S2a P4 F-A3 end-to-end: headless Chrome (CDP, node built-ins only) drives ui/orgs/location-sign-in.html against the real
// Admin.Api on real PostgreSQL (Admin.Api.Checks --ui-host). TEST DB only: argv[2] = ff_sse or ff_kit_<name>.
// Usage (repo root): node ui/orgs/tests/e2e.mjs ff_sse
// Chrome is launched with every outside host unresolvable (host-resolver-rules), so the run cannot egress.
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import net from "node:net";

const db = process.argv[2] ?? "";
if (!(db === "ff_sse" || /^ff_kit_[a-z0-9_]{1,20}$/.test(db)) || /^ff_kit_kf/.test(db)) { console.error("refuse: db must be ff_sse or ff_kit_<name>"); process.exit(2); }
const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
let pass = 0, fail = 0;
const ok = (name, cond, detail) => { if (cond) { pass++; console.log(`PASS ${name}`); } else { fail++; console.log(`FAIL ${name}${detail === undefined ? "" : " :: " + JSON.stringify(detail)}`); } };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

// ---------- host (real API + test proxy) ----------
const host = spawn("bash", ["-c", `. Auth.Data/tools/env.sh "$1" && AUTH_CHECKS_DB="$1" exec dotnet run --no-build --project Admin.Api.Checks -- --ui-host "$2"`, "e2e", db, join(repo, "ui/orgs")],
  { cwd: join(repo, "src"), stdio: ["pipe", "pipe", "pipe"], env: { ...process.env, PATH: `/home/box/.dotnet:${process.env.PATH}` } });
let hostOut = "", hostErr = "";
host.stdout.on("data", d => { hostOut += d; });
host.stderr.on("data", d => { hostErr += d; });
async function waitFor(re, ms) { const end = Date.now() + ms; while (Date.now() < end) { const m = re.exec(hostOut); if (m) return m; if (host.exitCode !== null) break; await sleep(100); } return null; }
const m = await waitFor(/^UIHOST (\{.*\})$/m, 120000);
if (!m) { console.error("ui-host did not start", hostErr.slice(-2000), hostOut.split("\n").filter(l => l.startsWith("FAIL")).join("\n")); host.kill(); process.exit(3); }
const H = JSON.parse(m[1]);
console.log(`INFO ui-host ${H.ui}`);

// ---------- chrome ----------
function freePort(skip) {
  return (async () => { for (let p = 18130; p <= 18139; p++) { if (skip.includes(p)) continue;
    const free = await new Promise(r => { const s = net.createServer().once("error", () => r(false)).once("listening", () => s.close(() => r(true))).listen(p, "127.0.0.1"); });
    if (free) return p; } throw new Error("no free port in 18130-18139"); })();
}
const uiPort = Number(new URL(H.ui).port);
const cdpPort = await freePort([uiPort]);
const profile = mkdtempSync(join(tmpdir(), "llc-e2e-chrome-"));
const chrome = spawn("google-chrome", ["--headless=new", `--remote-debugging-port=${cdpPort}`, "--remote-debugging-address=127.0.0.1", `--user-data-dir=${profile}`,
  "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-sync",
  "--disable-extensions", "--disable-default-apps", "--metrics-recording-only", "--no-sandbox", "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
  "--window-size=1280,1000", "about:blank"], { stdio: ["ignore", "ignore", "pipe"] });
let ver = null;
for (let i = 0; i < 100 && !ver; i++) { try { ver = await (await fetch(`http://127.0.0.1:${cdpPort}/json/version`)).json(); } catch { await sleep(100); } }
if (!ver) { console.error("chrome did not start"); finish(4); }
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0; const pending = new Map(); const listeners = [];
ws.onmessage = (ev) => { const msg = JSON.parse(ev.data); if (msg.id && pending.has(msg.id)) { const { r, j } = pending.get(msg.id); pending.delete(msg.id); msg.error ? j(new Error(JSON.stringify(msg.error))) : r(msg.result); } else for (const l of listeners) l(msg); };
const send = (method, params = {}, sessionId) => new Promise((r, j) => { const id = ++seq; pending.set(id, { r, j }); ws.send(JSON.stringify({ id, method, params, sessionId })); });
const { targetId } = await send("Target.createTarget", { url: "about:blank" });
const { sessionId: S } = await send("Target.attachToTarget", { targetId, flatten: true });
const cdp = (method, params) => send(method, params, S);
const problems = []; const bodies = []; const outside = [];
listeners.push((msg) => {
  if (msg.sessionId !== S) return;
  if (msg.method === "Runtime.exceptionThrown") problems.push("exception: " + (msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text));
  if (msg.method === "Runtime.consoleAPICalled" && (msg.params.type === "error" || msg.params.type === "warning")) problems.push("console: " + msg.params.args.map(a => a.value ?? a.description).join(" "));
  if (msg.method === "Log.entryAdded" && msg.params.entry.source !== "network" && (msg.params.entry.level === "error" || /Content Security Policy/i.test(msg.params.entry.text))) problems.push("log: " + msg.params.entry.text);
  if (msg.method === "Network.requestWillBeSent") {
    const u = new URL(msg.params.request.url);
    if (u.protocol !== "data:" && u.host !== new URL(H.ui).host) outside.push(msg.params.request.url);
    if (msg.params.request.postData !== undefined) bodies.push({ url: msg.params.request.url, method: msg.params.request.method, body: msg.params.request.postData, ifMatch: msg.params.request.headers["If-Match"] });
  }
});
await cdp("Runtime.enable"); await cdp("Log.enable"); await cdp("Network.enable"); await cdp("Page.enable");

async function ev(expr) { const r = await cdp("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text); return r.result.value; }
async function until(expr, ms = 10000) { const end = Date.now() + ms; let v; while (Date.now() < end) { try { v = await ev(expr); if (v) return v; } catch { } await sleep(50); } return v; }
async function open(path) { await cdp("Page.navigate", { url: H.ui + path }); await sleep(100); return until(`document.readyState === "complete" && document.body && document.body.dataset.state !== "loading" && (document.body.dataset.state !== undefined)`); }
const vis = (sel) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); if (!e) return false; for (let x = e; x; x = x.parentElement) if (x.hidden) return false; return e.getClientRects().length > 0; })()`);
const txt = (sel) => ev(`(document.querySelector(${JSON.stringify(sel)})?.textContent ?? "").replace(/\\s+/g, " ").trim()`);
const attr = (sel, a) => ev(`document.querySelector(${JSON.stringify(sel)})?.getAttribute(${JSON.stringify(a)}) ?? null`);
const clickDom = (sel) => ev(`document.querySelector(${JSON.stringify(sel)}).click(), true`);
async function fillDefaultSetup() {
  await ev(`(() => {
    const box = document.querySelector("#f-setup");
    if (!box || box.hidden) return true;
    const address = document.querySelector("#address1");
    if (!address || address.value.trim() !== "") return true;
    address.value = "1 Market Street";
    document.querySelector("#city").value = "San Francisco";
    const country = document.querySelector("#country");
    country.value = "US";
    country.dispatchEvent(new Event("change", { bubbles: true }));
    document.querySelector("#region").value = "CA";
    document.querySelector("#postal").value = "94105";
    document.querySelector("#tz").value = "America/Los_Angeles";
    document.querySelector("#phone").value = "+16502530000";
    const parent = document.querySelector("#parent");
    const wrap = document.querySelector("#f-parent");
    if (parent && wrap && !wrap.hidden && !parent.value) {
      const opt = [...parent.options].find((o) => o.value);
      if (opt) parent.value = opt.value;
    }
    return true;
  })()`);
}
const click = async (sel) => { if (sel === "#btn-save") await fillDefaultSetup(); return clickDom(sel); };
const type = (sel, v) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); e.focus(); e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event("input", { bubbles: true })); e.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
async function mouseClick(sel) {
  await ev(`document.querySelector(${JSON.stringify(sel)}).scrollIntoView({ block: "center" }), true`);
  const p = await ev(`(() => { const r = document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  for (const type of ["mousePressed", "mouseReleased"]) await cdp("Input.dispatchMouseEvent", { type, x: p.x, y: p.y, button: "left", clickCount: 1 });
  await sleep(50);
}
async function key(k, code) { for (const type of ["keyDown", "keyUp"]) await cdp("Input.dispatchKeyEvent", { type, key: k, code, windowsVirtualKeyCode: k === " " ? 32 : 0 }); await sleep(50); }
const page = (persona, loc) => `/as/${persona}/orgs/${H.partner_a}/locations/${loc}/sign-in`;
const rows = () => ev(`[...document.querySelectorAll("#history-rows tr")].map(tr => [...tr.cells].map(td => td.textContent))`);
const shownError = () => ev(`[...document.querySelectorAll('[data-testid="error-area"]')].filter(e => !e.hidden).map(e => ({ code: e.dataset.error ?? e.id, rid: e.querySelector("[data-request-id]")?.textContent ?? "" }))`);
const RID = /^Request ID: [0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
// [DEC-144] provisional create copy. The sign-in sentence is only for an authoritative 401 on the permission read.
const CREATE_UNKNOWN_TEXT = "We couldn\u2019t confirm whether the location was created. Check whether it exists before trying again. The original request may still finish, so trying again could create a duplicate.";
const CREATE_UNKNOWN_SESSION_TEXT = CREATE_UNKNOWN_TEXT + " Sign in again, then check whether the location exists.";
// D-102 Request ID line: own last line of the banner, muted #6b6b7b, selectable, never truncated, hidden without an id.
const ridInfo = (sel) => ev(`(() => { const b = document.querySelector(${JSON.stringify(sel)}); const r = b.querySelector("[data-request-id]"); if (!r) return null;
  const cs = getComputedStyle(r); const t = b.querySelector(".banner__title"); const sel = getSelection(); sel.removeAllRanges(); sel.selectAllChildren(r);
  const selected = sel.toString(); sel.removeAllRanges();
  return { vis: !r.hidden && r.getClientRects().length > 0, text: r.textContent, display: cs.display, userSelect: cs.userSelect, textOverflow: cs.textOverflow,
    whiteSpace: cs.whiteSpace, color: cs.color, last: r === r.parentElement.lastElementChild, ownLine: r.getBoundingClientRect().top >= t.getBoundingClientRect().bottom - 1,
    fits: r.scrollWidth <= r.clientWidth + 1, selected }; })()`);
const ridOk = (i) => !!i && i.vis && RID.test(i.text) && i.display === "block" && i.userSelect !== "none" && i.textOverflow !== "ellipsis" && !/nowrap/.test(i.whiteSpace)
  && i.color === "rgb(107, 107, 123)" && i.last && i.ownLine && i.fits && i.selected === i.text;
const noRidOnFields = () => ev(`[...document.querySelectorAll(".field__error")].every(e => !e.querySelector("[data-request-id]") && !/Request ID/.test(e.textContent))`);
async function save() {
  await click("#btn-save"); await until(`document.querySelector("#btn-save").getAttribute("aria-disabled") !== "true"`); await sleep(100);
}
const step = async (name, f) => { try { await f(); } catch (e) { ok(`${name}: ran`, false, String(e && e.stack || e)); } };

// ---------- scenarios ----------
await step("SA view entra", async () => {
  await open(page("sa", H.l2));
  ok("SA view (entra L2): state view, chip Super Admin, title", await ev(`document.body.dataset.state`) === "view" && await txt("#role-chip-text") === "Super Admin" && await txt("#title") === "Sign-in method: UI Cage 2");
  ok("SA view: read-only server card shows authority, client id and 'Set' (no date)", await vis("#card-connection") && await txt("#conn-authority") === "https://login.microsoftonline.com"
    && /^[0-9a-f-]{36}$/.test(await txt("#conn-client")) && await txt("#conn-secret") === "Set", [await txt("#conn-authority"), await txt("#conn-client"), await txt("#conn-secret")]);
  ok("SA view: no secret / oidc input anywhere and no rotation date text", await ev(`[...document.querySelectorAll("input,textarea,select")].every(e => !/secret|oidc|authority|client/i.test(e.id + e.name))`)
    && !(await ev(`document.body.textContent`)).match(/rotated|arn:|secretsmanager/i));
  const r = await rows();
  ok("SA view: history visible, Event auth.provider.changed, Who = actor id (SA), When in PT", await vis("#card-history") && r.length === 1 && r[0][1] === "auth.provider.changed" && r[0][2] === H.users.sa
    && /^[A-Z][a-z]{2} \d{1,2}, \d{4}, \d{1,2}:\d{2} [AP]M$/.test(r[0][0]) && r[0][3].startsWith("Created: Microsoft login"), r);
  ok("SA view: Last changed in PT by the actor id", (await txt("#cur-changed")).endsWith(`PT by ${H.users.sa}`), await txt("#cur-changed"));
  ok("SA view: change button shown, no form", await vis("#btn-change") && !(await vis("#form")));
});
await step("SA view db", async () => {
  await open(page("sa", H.l4));
  ok("SA view (db L4): server card 'Not set' for all three, backup sign-in 'Not used'", await txt("#conn-secret") === "Not set" && await txt("#conn-authority") === "Not set" && await txt("#cur-fallback") === "Not used (database location)");
});
await step("RD view only", async () => {
  await open(page("rd", H.l1));
  ok("org:V (F-A3, real PG): chip 'View only (org:V)', info banner", await txt("#role-chip-text") === "View only (org:V)" && await vis("#banner-viewonly")
    && await txt("#banner-viewonly") === "iYou can view this setting but not change it. Ask a Super Admin or your Partner Admin.", await txt("#banner-viewonly"));
  ok("org:V (C2): history hidden and empty, no form, no change button, no server card", !(await vis("#card-history")) && (await rows()).length === 0 && !(await vis("#form")) && !(await vis("#btn-change")) && !(await vis("#card-connection")));
  const hr = await fetch(`${H.ui}/as/rd/api/orgs/locations/${H.l1}/login-config/changes`); const hb = await hr.json();
  ok("org:V history API (real PG): 403 forbidden, so the page hides history (C2)", hr.status === 403 && hb.error === "forbidden" && !("items" in hb), [hr.status, hb]);
  const vr = await (await fetch(`${H.ui}/as/rd/api/orgs/locations/${H.l1}/login-config`)).json();
  ok("org:V view (real PG): restricted, no secret / oidc keys", !("secret" in vr) && !("oidc_authority" in vr) && !("oidc_client_id" in vr), Object.keys(vr));
  ok("org:V: current setting shown", await vis("#card-current") && await txt("#cur-method") === "Username and password + one-time code");
  await open(page("rd", H.l4));
  const e = await shownError();
  ok("org:V reader of a location they cannot read: 404 not_found banner (spec §3 GET: out of scope = missing) with the request id, nothing else shown", e.length === 1 && e[0].code === "not_found" && RID.test(e[0].rid) && !(await vis("#card-current")), e);
});
await step("PB other partner", async () => {
  await open(page("pb", H.l1));
  const e = await shownError();
  ok("Partner Admin of another partner: 404 not_found banner + request id", e.length === 1 && e[0].code === "not_found" && RID.test(e[0].rid), e);
  ok("D-102 #1: not_found text", await txt("#banner-error-text") === "It may have been removed, or you may not have access to it.", await txt("#banner-error-text"));
  const ri = await ridInfo("#banner-error");
  ok("D-102 Request ID line on the top-level banner: own last line, #6b6b7b, selectable, not truncated", ridOk(ri), ri);
  await open(`/as/sa/orgs/not-a-partner/locations/x/sign-in`);
  const rn = await ridInfo("#banner-error");
  ok("D-102 Request ID line absent when there is no id (route error, no request made)", await vis("#banner-error") && rn && !rn.vis && rn.text === "", rn);
  await open(`/as/sa/orgs/${H.partner_a}/locations/${H.l3}/sign-in`);
  const e2 = await shownError();
  ok("C3: a location opened under the wrong org in the URL shows not found", e2.length === 1 && e2[0].code === "partner_mismatch" && !(await vis("#card-current")), e2);
  await open(page("anon", H.l1));
  const e3 = await shownError();
  ok("no session: 401 banner with request id", e3.length === 1 && e3[0].code === "401" && RID.test(e3[0].rid), e3);
});
await step("PA partner-edit", async () => {
  await open(page("pa", H.l1));
  ok("PA view: chip 'Partner Admin: <partner id>' (TLD 11:57, V11-PARTNERNAME), history visible, no server card", await txt("#role-chip-text") === `Partner Admin: ${H.partner_a}` && await txt("#role-chip-partner") === H.partner_a && await vis("#card-history") && !(await vis("#card-connection")));
  await click("#btn-change");
  ok("partner-edit: MFA aria-disabled, focusable, hint 'Only a Super Admin can turn this off.'", await attr("#mfa", "aria-disabled") === "true" && !(await ev(`document.querySelector("#mfa").disabled`))
    && await txt("#mfa-hint") === "Only a Super Admin can turn this off." && (await attr("#mfa", "aria-describedby")).includes("mfa-hint") && await ev(`document.body.dataset.partnerState`) === "partner-edit");
  await mouseClick("#mfa");
  const afterClick = await ev(`document.querySelector("#mfa").checked`);
  await ev(`document.querySelector("#mfa").focus(), true`); await key(" ", "Space");
  ok("partner-edit: a real mouse click leaves MFA on, and so does Space (checked separately)", afterClick === true && await ev(`document.querySelector("#mfa").checked`) === true);
  ok("partner-edit: no reason field, no downgrade banner; history stays visible", !(await vis("#f-reason")) && !(await vis("#banner-downgrade")) && await vis("#card-history"));
  await mouseClick("#m-entra");
  ok("partner-edit: Microsoft shows the tenant field and a locked backup sign-in ('Only a Super Admin can turn this on.')", await vis("#tenant") && await vis("#fallback")
    && await attr("#fallback", "aria-disabled") === "true" && await txt("#fb-hint") === "Only a Super Admin can turn this on.");
  await mouseClick("#fallback");
  ok("partner-edit: clicking backup sign-in leaves it off", await ev(`document.querySelector("#fallback").checked`) === false);
  await type("#tenant", H.t_taken); await save();
  const e = await shownError();
  ok("tenant-taken (422 tenant_bound_elsewhere): MK9 banner + field error, aria-invalid, request id", e.length === 1 && e[0].code === "tenant_bound_elsewhere" && RID.test(e[0].rid)
    && await txt("#tenant-err") === "This tenant ID is already linked to another partner, so it can't be used here." && await attr("#tenant", "aria-invalid") === "true"
    && (await attr("#tenant", "aria-describedby")).startsWith("tenant-err"), e);
  const rt = await ridInfo("#banner-tenant");
  ok("D-102: Request ID line on the drawn 422 tenant banner", ridOk(rt), rt);
  ok("D-102: no Request ID under the tenant field", await noRidOnFields());
  ok("tenant-taken: banner text is MK9's", (await txt("#banner-tenant")).startsWith("⚠This tenant ID can't be used. It is already linked to another partner."), await txt("#banner-tenant"));
  await type("#tenant", H.t_free); await save();
  ok("partner-edit save: saved banner (MK9 copy with the location name), 'New setting' Microsoft", await vis("#banner-saved")
    && await txt("#banner-saved") === "✓Saved. The new sign-in method is on. People already signed in at UI Cage 1 stay signed in until their session ends."
    && await txt("#cur-h-text") === "New setting" && await txt("#cur-method") === "Microsoft (Entra ID)" && !(await vis("#banner-tenant")) && await attr("#tenant", "aria-invalid") === null, await txt("#banner-saved"));
  const r = await rows();
  ok("partner-edit save: history newest first, Who = PA actor id, Database → Microsoft", r.length === 2 && r[0][2] === H.users.pa && r[0][3].startsWith("Database → Microsoft"), r);
  ok("partner-edit save: still no server card for a Partner Admin", !(await vis("#card-connection")));
});
// DEC-107 [TLD 2026-11-04 16:32 Q2=(ii)]: the page takes the reason bounds from the API. /c50/... is the same page and API code,
// started with LoginConfig 50/200 (ui-host); nothing is written here (every save is a 422).
await step("DEC-107 limits and config 50/200", async () => {
  const lim = async (pfx, who) => { const r = await fetch(`${H.ui}/${pfx}/${who}/api/orgs/login-config/limits`); const t = await r.text(); return [r.status, t, r.headers.get("WWW-Authenticate")]; };
  let [st, b] = await lim("as", "sa");
  ok("limits via the UI host: SA 200 exactly {reason_min_length: 10, reason_max_length: 500}", st === 200 && b === JSON.stringify({ reason_min_length: 10, reason_max_length: 500 }), [st, b]);
  [st, b] = await lim("as", "rd");
  ok("limits via the UI host: org:V reader 200", st === 200 && JSON.parse(b).reason_max_length === 500, [st, b]);
  const a = await lim("as", "anon");
  ok("limits via the UI host: no token 401, no body, WWW-Authenticate Bearer", a[0] === 401 && a[1] === "" && /^Bearer/.test(a[2] ?? ""), a);
  [st, b] = await lim("c50", "sa");
  ok("limits on the 50/200 API: 200 exactly {50, 200}", st === 200 && b === JSON.stringify({ reason_min_length: 50, reason_max_length: 200 }), [st, b]);
  const r30 = "DEC-107 e2e: thirty chars ok.."; // 30 characters: inside 10..500, below 50
  await open(`/c50/sa/orgs/${H.partner_a}/locations/${H.l2}/sign-in`); await click("#btn-change"); await mouseClick("#mfa");
  ok("c50 Edit: reason hint uses the GET's 50 / 200", await txt("#reason-hint") === "50 to 200 characters. Saved in the audit log.", await txt("#reason-hint"));
  await type("#reason", r30); await save();
  let e = await shownError();
  ok("c50 Edit: a 30-character reason -> 422 reason_length, copy 'Enter a reason of 50 to 200 characters.'", e.length === 1 && e[0].code === "validation_failed"
    && await txt("#reason-err") === "Enter a reason of 50 to 200 characters." && await noRidOnFields(), [e, await txt("#reason-err")]);
  await open(`/c50/sa/orgs/${H.partner_a}/locations/new`);
  ok("c50 Create: reason hint uses the limits endpoint's 50 / 200", await txt("#reason-hint") === "50 to 200 characters. Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in.", await txt("#reason-hint"));
  await type("#lname", "UI Cage c50"); await type("#dname", "UI Casino A"); await mouseClick("#mfa"); await type("#reason", r30); await save();
  e = await shownError();
  ok("c50 Create: a 30-character reason -> 422, copy 'Enter a reason of 50 to 200 characters.', still on /new", e.length === 1 && e[0].code === "validation_failed"
    && await txt("#reason-err") === "Enter a reason of 50 to 200 characters." && await ev(`location.pathname.endsWith("/new")`), [e, await txt("#reason-err")]);
  await open(page("sa", H.l2));
  ok("c50: nothing was saved (L2 still MFA on, no new history row)", await txt("#cur-mfa") === "Yes" && (await rows()).length === 1, [await txt("#cur-mfa"), (await rows()).length]);
});
// v1.1 Slice A [TLD 08:00 Q3]: the create page asks limits?partner_id=<URL partner>; a partner with its own
// partner_reason_bounds row gets its numbers, another partner the config. Fresh test-only partner ids; the row is
// written through the API (SA PUT, If-Match "1") and removed by the ui-host cleanup. Nothing else is saved here.
await step("Slice A per-partner limits on Create", async () => {
  const PX = "5f0c3e1a-0007-4000-8000-000000000007", PY = "5f0c3e1a-0008-4000-8000-000000000008";
  const HINT = (a, b) => `${a} to ${b} characters. Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in.`;
  const put = await fetch(`${H.ui}/as/sa/api/orgs/partners/${PX}/login-config/reason-bounds`, { method: "PUT", headers: { "Content-Type": "application/json", "If-Match": '"0"' },
    body: JSON.stringify({ reason_min_length: 20, reason_max_length: 300 }) });
  ok("Slice A setup: SA PUT reason-bounds 20/300 on a fresh partner (If-Match \"0\" = expect unset, TLD 11:23 A) -> 200, ETag \"1\"", put.status === 200 && put.headers.get("ETag") === '"1"', put.status);
  const seen = [];
  const spy = (msg) => { if (msg.sessionId === S && msg.method === "Network.requestWillBeSent" && /\/api\/orgs\/login-config\/limits/.test(msg.params.request.url)) seen.push(msg.params.request.url); };
  listeners.push(spy);
  await open(`/as/sa/orgs/${PX}/locations/new`);
  ok("Slice A create: hint uses the URL partner's own bounds 20 / 300", await txt("#reason-hint") === HINT(20, 300), await txt("#reason-hint"));
  await open(`/as/sa/orgs/${PY}/locations/new`);
  ok("Slice A create: a partner with no row -> the config 10 / 500", await txt("#reason-hint") === HINT(10, 500), await txt("#reason-hint"));
  listeners.splice(listeners.indexOf(spy), 1);
  ok("Slice A create: the page sent limits?partner_id=<URL partner> each time", seen.length === 2 && seen[0].endsWith(`/limits?partner_id=${PX}`) && seen[1].endsWith(`/limits?partner_id=${PY}`), seen);
});
await step("SA downgrade", async () => {
  await open(page("sa", H.l2));
  await click("#btn-change");
  ok("SA edit: MFA not locked, Super Admin hint", await attr("#mfa", "aria-disabled") === null && await txt("#mfa-hint") === "Turning this off lowers security: Super Admin only, with a reason.");
  ok("SA edit: no reason field before a downgrade; server card stays", !(await vis("#f-reason")) && await vis("#card-connection"));
  await mouseClick("#mfa");
  ok("downgrade: warning banner, reason field, title 'Lower sign-in security: UI Cage 2'", await vis("#banner-downgrade") && await vis("#reason") && await txt("#title") === "Lower sign-in security: UI Cage 2"
    && await txt("#reason-hint") === "10 to 500 characters. Saved in the audit log.", await txt("#reason-hint"));
  await save();
  let e = await shownError();
  ok("downgrade without reason: 400 reason_required, reason field error, request id", e.length === 1 && e[0].code === "reason_required" && RID.test(e[0].rid) && await vis("#reason-err") && await attr("#reason", "aria-invalid") === "true", e);
  await type("#reason", "short"); await save();
  e = await shownError();
  ok("downgrade with a short reason: server 422 reason_length shown on the field (no client-side length rule)", e.length === 1 && e[0].code === "validation_failed" && await vis("#reason-err") && RID.test(e[0].rid), [e, await txt("#reason-err")]);
  ok("DEC-107: reason_length field copy with the GET's numbers", await txt("#reason-err") === "Enter a reason of 10 to 500 characters.", await txt("#reason-err"));
  ok("D-102: no Request ID under fields (reason error shown); the top-level banner has it", await noRidOnFields() && ridOk(await ridInfo("#banner-error")));
  await type("#reason", "UI e2e: conditional access enforces MFA (ticket UI-1)"); await save();
  ok("downgrade with a reason: saved", await vis("#banner-saved") && await txt("#cur-mfa") === "No");
  ok("after a success no Request ID line is left visible", await ev(`[...document.querySelectorAll("[data-request-id]")].every(e => e.hidden && e.textContent === "")`));
  const r = await rows();
  ok("downgrade: history row shows second step off and the reason", r[0][2] === H.users.sa && r[0][3] === "second step off. Reason: UI e2e: conditional access enforces MFA (ticket UI-1)", r[0]);
});
// DEC-106 [Mohanraj, PM t145u] + UX D-106 (md5 2b17464c) state partner-mfa-off: L2 is saved with MFA off by the step above.
await step("PA partner-mfa-off", async () => {
  const HINT = "Off at this location now. Turn it on to add a second step at sign-in. Once saved, only a Super Admin can turn it off again.";
  const st = async () => ({ ps: await ev(`document.body.dataset.partnerState ?? null`), aria: await attr("#mfa", "aria-disabled"), checked: await ev(`document.querySelector("#mfa").checked`),
    hint: await txt("#mfa-hint"), reason: await vis("#f-reason"), banner: await vis("#banner-downgrade"), fb: await attr("#fallback", "aria-disabled"), fbHint: await txt("#fb-hint") });
  await open(page("pa", H.l2)); await click("#btn-change");
  let s0 = await st();
  ok("DEC-106 partner-mfa-off: saved MFA off -> MFA checkbox enabled (no aria-disabled, not disabled), unticked, exact D-106 hint (described by)", s0.ps === "partner-mfa-off" && s0.aria === null
    && !(await ev(`document.querySelector("#mfa").disabled`)) && s0.checked === false && s0.hint === HINT && (await attr("#mfa", "aria-describedby")).includes("mfa-hint"), s0);
  ok("DEC-106 partner-mfa-off: no reason field, no downgrade banner; backup sign-in stays aria-disabled 'Only a Super Admin can turn this on.' (fallback back off: Confirmed DEC-118/120/121)",
    !s0.reason && !s0.banner && s0.fb === "true" && s0.fbHint === "Only a Super Admin can turn this on.", s0);
  await mouseClick("#mfa"); const s1 = await st();
  ok("DEC-106 partner-mfa-off: ticking turns MFA on, still no reason field or downgrade banner, same hint", s1.checked === true && !s1.reason && !s1.banner && s1.hint === HINT && s1.aria === null, s1);
  await mouseClick("#mfa"); const s2 = await st();
  ok("DEC-106 partner-mfa-off: unticking before Save is allowed (back to off, no reason field, no banner)", s2.checked === false && !s2.reason && !s2.banner && s2.aria === null && s2.hint === HINT, s2);
  await mouseClick("#mfa");
  const hist0 = (await rows()).length;
  await save(); await until(`document.body.dataset.state === "saved"`);
  const e = await shownError();
  const api = await (await fetch(`${H.ui}/as/pa/api/orgs/locations/${H.l2}/login-config`)).json();
  ok("DEC-106 partner-mfa-off: tick + Save -> saved (no error), 'Yes', the API row has mfa_required true", e.length === 0 && await vis("#banner-saved") && await txt("#cur-mfa") === "Yes" && api.mfa_required === true, [e, await txt("#cur-mfa"), api.mfa_required]);
  const r = await rows();
  ok("DEC-106 partner-mfa-off: new history row, newest first, Who = PA actor id", r.length === hist0 + 1 && r[0][1] === "auth.provider.changed" && r[0][2] === H.users.pa, r[0]);
  await open(page("pa", H.l2)); await click("#btn-change");
  const s3 = await st();
  ok("DEC-106: once saved on -> partner-edit lock: MFA aria-disabled, ticked, hint 'Only a Super Admin can turn this off.'; fallback still aria-disabled", s3.ps === "partner-edit" && s3.aria === "true"
    && s3.checked === true && s3.hint === "Only a Super Admin can turn this off." && s3.fb === "true" && !s3.reason && !s3.banner, s3);
  await open(page("sa", H.l2)); await click("#btn-change");
  ok("DEC-106: Super Admin unchanged (no partner state, MFA not locked, Super Admin hint)", await ev(`document.body.dataset.partnerState ?? null`) === null && await attr("#mfa", "aria-disabled") === null
    && await txt("#mfa-hint") === "Turning this off lowers security: Super Admin only, with a reason.");
});
await step("conflict", async () => {
  await open(page("sa", H.l4)); await click("#btn-change");
  await type("#dname", "UI Casino A (edited in page)");
  // Someone else saves first, through the same API (as the Partner Admin).
  const api = `${H.ui}/as/pa/api/orgs/locations/${H.l4}/login-config`;
  const g = await fetch(api); const cur = await g.json();
  const p = await fetch(api, { method: "PUT", headers: { "Content-Type": "application/json", "If-Match": g.headers.get("ETag") },
    body: JSON.stringify({ provider_kind: "database", display_name: "UI Casino A (other admin)", entra_allowed_tenant_ids: [], allow_db_fallback: false, mfa_required: true, reason: null }) });
  ok("conflict setup: the other admin's PUT is 200", p.status === 200 && cur.row_version === 1, p.status);
  await save();
  const e = await shownError();
  ok("409 concurrency_conflict: MK9 banner, request id, nothing overwritten", e.length === 1 && e[0].code === "concurrency_conflict" && RID.test(e[0].rid)
    && (await txt("#banner-conflict")).startsWith("⚠Someone else changed this setting while you were editing. Your changes were not saved. Reload to see the latest version, then make your change again."), [e, await txt("#banner-conflict")]);
  const rc = await ridInfo("#banner-conflict");
  ok("D-102: Request ID line on the drawn 409 conflict banner", ridOk(rc), rc);
  await click("#conflict-reload"); await sleep(300); await open(page("sa", H.l4));
  ok("conflict reload: the page shows the other admin's version", (await rows()).length === 2 && (await rows())[0][2] === H.users.pa);
});
await step("PA not_super_admin", async () => {
  await open(page("pa", H.l1)); await click("#btn-change");
  // Bypass the aria-disabled lock in the page (as a stale or scripted client would) so the server's 403 is shown.
  await ev(`(() => { const m = document.querySelector("#mfa"); m.checked = false; m.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
  await save();
  const e = await shownError();
  ok("D-102 #2: 403 not_super_admin title and text", e.length === 1 && e[0].code === "not_super_admin" && await txt("#banner-error-title") === "Only a Super Admin can make this change."
    && await txt("#banner-error-text") === "Nothing was saved. Turning off the second step or allowing backup sign-in needs a Super Admin.", [e, await txt("#banner-error-title"), await txt("#banner-error-text")]);
  const ri = await ridInfo("#banner-error");
  ok("D-102: Request ID line on the not_super_admin banner", ridOk(ri), ri);
});
await step("SA create", async () => {
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
  ok("create (Super Admin): title, chip, name + reason fields, no server card, no history", await txt("#title") === "Create location" && await txt("#role-chip-text") === "Super Admin"
    && await vis("#lname") && await vis("#reason") && !(await vis("#card-connection")) && !(await vis("#card-history")) && await txt("#btn-save") === "Create location" && await txt("#bc-page") === "New location");
  ok("DEC-107 create: reason hint uses the limits endpoint's 10 / 500", await txt("#reason-hint") === "10 to 500 characters. Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in.", await txt("#reason-hint"));
  await type("#lname", "UI Cage 5"); await type("#dname", "UI Casino A"); await save();
  await until(`location.pathname.endsWith("/sign-in") && document.body.dataset.state === "saved"`);
  ok("create (Super Admin): goes to the new location's page with the saved banner; hash cleared", await ev(`document.body.dataset.state`) === "saved" && await ev(`location.hash`) === ""
    && await txt("#title") === "Sign-in method: UI Cage 5" && (await rows()).length === 1);
  ok("V11-CREATE-SECRETCARD (TLD 11:57): no Microsoft card on create; it appears once the location exists", await vis("#card-connection") && await txt("#conn-secret") === "Not set");
  await click("#btn-change");
  ok("V11-CREATE-SECRETCARD: card shown in the edit view after create", await ev(`document.body.dataset.state`) === "edit" && await vis("#card-connection"));
});
// P4h [TLD 2026-11-04 16:56 Q2]: each create 422 under login_config/* (and the top-level name) lands on its form field.
// Fields the page cannot make invalid by typing (mfa_required, allow_db_fallback, provider_kind) are set in the outgoing
// POST body with CDP Fetch; the 422 itself is the real API's. Focus: the existing pattern focuses the error banner.
await step("P4h create field errors", async () => {
  let tamper = null, respId = null;
  const lsn = (msg) => {
    if (msg.sessionId !== S) return;
    if (msg.method === "Network.responseReceived" && /\/api\/orgs\/locations$/.test(msg.params.response.url) && msg.params.type === "Fetch") respId = msg.params.requestId;
    if (msg.method !== "Fetch.requestPaused") return;
    const q = msg.params.request; let post;
    if (tamper && q.method === "POST" && q.postData !== undefined) { const b = JSON.parse(q.postData); tamper(b); post = Buffer.from(JSON.stringify(b)).toString("base64"); }
    cdp("Fetch.continueRequest", post === undefined ? { requestId: msg.params.requestId } : { requestId: msg.params.requestId, postData: post }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/api/orgs/locations", requestStage: "Request" }] });
  const attempt = async (fill, tamp) => {
    await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
    await type("#lname", "UI Cage P4h"); await type("#dname", "UI Casino A");
    await fill(); tamper = tamp ?? null; respId = null; await save(); tamper = null;
    let api = null;
    try { const r = await cdp("Network.getResponseBody", { requestId: respId }); api = JSON.parse(r.body); } catch (e) { api = { err: String(e) }; }
    return api;
  };
  const cases = [
    ["name (top-level)", async () => { await type("#lname", " "); }, null, "name", "name_invalid", "lname", "lname-err", "Enter a location name."],
    ["login_config/display_name", async () => { await type("#dname", "   "); }, null, "login_config/display_name", "display_name_invalid", "dname", "dname-err", "Enter a name of 1 to 100 characters."],
    ["login_config/entra_allowed_tenant_ids", async () => { await mouseClick("#m-entra"); }, null, "login_config/entra_allowed_tenant_ids", "tenant_count", "tenant", "tenant-err", "Enter one Microsoft tenant ID."],
    ["login_config/entra_allowed_tenant_ids/0", async () => { await mouseClick("#m-entra"); await type("#tenant", "not-a-tenant"); }, null, "login_config/entra_allowed_tenant_ids/0", "tenant_id_format", "tenant", "tenant-err",
      "Enter the tenant ID in the format 00000000-0000-0000-0000-000000000000 (not all zeros)."],
    ["login_config/mfa_required", async () => { }, (b) => { b.login_config.mfa_required = "yes"; }, "login_config/mfa_required", "type", "mfa", "fallback-err", "Check this field."],
    ["login_config/allow_db_fallback", async () => { }, (b) => { b.login_config.allow_db_fallback = true; b.login_config.reason = "P4h e2e: a valid reason"; }, "login_config/allow_db_fallback", "fallback_not_applicable", "fallback", "fallback-err",
      "Backup sign-in is only for Microsoft sign-in."],
    ["login_config/reason", async () => { await mouseClick("#mfa"); await type("#reason", "too short"); }, null, "login_config/reason", "reason_length", "reason", "reason-err", "Enter a reason of 10 to 500 characters."],
  ];
  for (const [name, fill, tamp, path, code, id, errId, copy] of cases) {
    const api = await attempt(fill, tamp);
    const got = { api, err: await txt("#" + errId), errVis: await vis("#" + errId), inv: await attr("#" + id, "aria-invalid"), desc: await attr("#" + id, "aria-describedby"),
      banner: await shownError(), focus: await ev(`document.activeElement && document.activeElement.id`), url: await ev(`location.pathname.endsWith("/new")`) };
    ok(`P4h create: ${name} -> real 422 ${code}; copy on #${errId}, #${id} aria-invalid + aria-describedby, banner focused, still on /new`,
      api && api.error === "validation_failed" && Array.isArray(api.fields) && api.fields.some(f => f.field === path && f.code === code)
      && got.err === copy && got.errVis && got.inv === "true" && (got.desc ?? "").split(" ").includes(errId)
      && got.banner.length === 1 && got.banner[0].code === "validation_failed" && got.focus === "banner-error" && got.url && await noRidOnFields(), got);
  }
  // provider_kind: the method radios have no error slot (as in Edit), so the error is banner-only; no field is marked.
  const api = await attempt(async () => { }, (b) => { b.login_config.provider_kind = "saml"; });
  const quiet = await ev(`[...document.querySelectorAll(".field__error")].every(e => e.hidden) && !document.querySelector("[aria-invalid]")`);
  ok("P4h create: login_config/provider_kind -> real 422 provider_kind_invalid; no error slot, banner only (validation_failed, focused)",
    api && Array.isArray(api.fields) && api.fields.some(f => f.field === "login_config/provider_kind" && f.code === "provider_kind_invalid") && quiet
    && (await shownError())[0]?.code === "validation_failed" && await ev(`document.activeElement && document.activeElement.id`) === "banner-error", { api, quiet });
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
});
// P4i [TLD 2026-11-04 17:28 item 3]: missing or invalid reason limits -> the client falls back to the server's hard bounds
// 10 / 500 (REASON_BOUNDS). The limits / location GET responses are replaced in the browser with CDP Fetch; nothing is written.
await step("P4i limits fallback", async () => {
  let mode = null;
  const lsn = (msg) => {
    if (msg.sessionId !== S || msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    if (!mode || p.request.method !== "GET") return go();
    if (mode.kind === "fail") return cdp("Fetch.failRequest", { requestId: id, errorReason: "Failed" }).catch(() => {});
    if (mode.kind === "fake") return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: mode.status, responseHeaders: [{ name: "Content-Type", value: "application/json" }, { name: "X-Request-ID", value: "01a1a94d-0000-7000-8000-000000000417" }],
      body: Buffer.from(mode.body).toString("base64") }).catch(() => {});
    if (mode.kind === "edit") {
      if (p.responseStatusCode === undefined) return go();
      return cdp("Fetch.getResponseBody", { requestId: id }).then((r) => {
        const b = JSON.parse(r.base64Encoded ? Buffer.from(r.body, "base64").toString() : r.body); mode.edit(b);
        return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: p.responseStatusCode, responseHeaders: p.responseHeaders, body: Buffer.from(JSON.stringify(b)).toString("base64") });
      }).catch(() => go());
    }
    return go();
  };
  listeners.push(lsn);
  // UX D-108 (md5 9318c2f8) [TLD 2026-11-04 18:35 / 18:37]: with fallback limits the hint and the 422 copy show no numbers.
  const HINT_C = "Saved in the audit log. In Create: required only if you turn off the second step or turn on backup sign-in.";
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/api/orgs/login-config/limits*", requestStage: "Request" }] });
  const createCases = [
    ["limits fetch fails (network)", { kind: "fail" }],
    ["limits 500", { kind: "fake", status: 500, body: JSON.stringify({ error: "internal_error" }) }],
    ["limits non-integer min", { kind: "fake", status: 200, body: JSON.stringify({ reason_min_length: "10", reason_max_length: 500 }) }],
    ["limits min below 10", { kind: "fake", status: 200, body: JSON.stringify({ reason_min_length: 5, reason_max_length: 500 }) }],
    ["limits max above 500", { kind: "fake", status: 200, body: JSON.stringify({ reason_min_length: 10, reason_max_length: 600 }) }],
    ["limits min > max", { kind: "fake", status: 200, body: JSON.stringify({ reason_min_length: 300, reason_max_length: 200 }) }],
    ["limits missing", { kind: "fake", status: 200, body: "{}" }],
  ];
  for (const [name, m] of createCases) {
    mode = m; await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); mode = null;
    const got = { state: await ev(`document.body.dataset.state`), hint: await txt("#reason-hint"), lname: await vis("#lname") };
    ok(`P4i create: ${name} -> form shown, fallback limits, D-108 hint (no numbers)`, got.state === "create" && got.lname && got.hint === HINT_C, got);
  }
  // With fallback limits the 422 reason_length copy is D-108's (no numbers), not the c50 API's 50 / 200.
  mode = createCases[0][1]; await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); mode = null;
  await type("#lname", "UI Cage P4i"); await type("#dname", "UI Casino A"); await mouseClick("#mfa"); await type("#reason", "too short"); await save();
  const e = await shownError();
  ok("P4i create (fallback): a 9-character reason -> 422 copy 'This reason is too short or too long. Edit it and try again.' (D-108), still on /new", e.length === 1 && e[0].code === "validation_failed"
    && await txt("#reason-err") === "This reason is too short or too long. Edit it and try again." && await ev(`location.pathname.endsWith("/new")`), [e, await txt("#reason-err")]);
  mode = { kind: "fake", status: 401, body: "" }; await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); mode = null;
  ok("P4i create: limits 401 is the verified session copy, not a fallback and not the generic load error",
    await ev(`document.body.dataset.state`) === "error" && !(await vis("#lname"))
      && `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim() === "Your session has ended. Sign in again, then try again.",
    await txt("#banner-error-title"));
  await cdp("Fetch.disable");
  // Edit: the location GET's reason_min_length / reason_max_length (c50 API: 50 / 200) replaced -> 10 / 500.
  await cdp("Fetch.enable", { patterns: [{ urlPattern: `*/api/orgs/locations/${H.l1}/login-config`, requestStage: "Response" }] });
  const editCases = [
    ["GET limits missing", (b) => { delete b.reason_min_length; delete b.reason_max_length; }],
    ["GET min below 10", (b) => { b.reason_min_length = 1; }],
    ["GET non-integer max", (b) => { b.reason_max_length = 200.5; }],
  ];
  for (const [name, edit] of editCases) {
    mode = { kind: "edit", edit }; await open(`/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); mode = null;
    await click("#btn-change"); await mouseClick("#mfa");
    const got = { state: await ev(`document.body.dataset.state`), hint: await txt("#reason-hint") };
    ok(`P4i edit: ${name} -> fallback limits, D-109 Edit hint 'Saved in the audit log.' (no numbers, no Create sentence)`, got.hint === "Saved in the audit log.", got);
  }
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
  await open(page("sa", H.l1));
  ok("P4i: nothing was saved (L1 still MFA on)", await txt("#cur-mfa") === "Yes", await txt("#cur-mfa"));
});
// P4k [TLD 2026-11-04 18:35, SSE2 7b90e26f]: LOW-1 field-error clear; limits 403 / redirect / 3xx / non-JSON -> page error;
// limits timeout (AbortController, injected short timeout) -> fallback; D-108 fallback copy vs the server's numbered copy.
// All answers are faked in the browser with CDP Fetch (nothing reaches the API's write path).
await step("P4k follow-ups", async () => {
  let mode = null, post = null; const seen = [];
  const lsn = (msg) => {
    if (msg.sessionId !== S) return;
    if (msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    if (p.request.url.endsWith("/p4k-sign-in")) { seen.push(p.request.url); return go(); } // only reached if the 302 is followed
    if (p.request.method === "POST" && post) { const f = post.shift(); return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: f.status, responseHeaders: [{ name: "Content-Type", value: "application/json" }, { name: "X-Request-ID", value: "01a1a94d-0000-7000-8000-000000000418" }], body: Buffer.from(JSON.stringify(f.body)).toString("base64") }).catch(() => {}); }
    if (!mode || p.request.method !== "GET" || !/\/api\/orgs\/login-config\/limits(\?|$)/.test(p.request.url)) return go();
    if (mode.kind === "stall") return; // never answered: only the page's abort ends it
    return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: mode.status, responseHeaders: mode.headers, body: Buffer.from(mode.body).toString("base64") }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/api/orgs/login-config/limits*", requestStage: "Request" }, { urlPattern: "*/api/orgs/locations", requestStage: "Request" }, { urlPattern: "*/p4k-sign-in", requestStage: "Request" }] });
  const JSON_CT = [{ name: "Content-Type", value: "application/json" }];
  const HINT_F = "Saved in the audit log. In Create: required only if you turn off the second step or turn on backup sign-in.";
  // LOW-1: a 422 on every mapped field, then a retry that answers 500: each field is back to its own describedby, not invalid.
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
  await type("#lname", "UI Cage P4k"); await type("#dname", "UI Casino A");
  const F = [["lname", "lname-err"], ["dname", "dname-err"], ["tenant", "tenant-err"], ["reason", "reason-err"], ["fallback", "fallback-err"], ["mfa", "fallback-err"]];
  const snap = async () => { const o = {}; for (const [id] of F) o[id] = { inv: await attr("#" + id, "aria-invalid"), desc: await attr("#" + id, "aria-describedby") }; return o; };
  const base = await snap();
  post = [{ status: 422, body: { error: "validation_failed", fields: [{ field: "name", code: "name_invalid" }, { field: "login_config/display_name", code: "display_name_invalid" },
    { field: "login_config/entra_allowed_tenant_ids", code: "tenant_count" }, { field: "login_config/reason", code: "reason_length" },
    { field: "login_config/allow_db_fallback", code: "fallback_not_applicable" }, { field: "login_config/mfa_required", code: "type" }] } },
    { status: 403, body: { error: "forbidden" } }]; // [TLD 2026-11-05 15:08] was a 500 {internal_error}: a write 5xx is now UNKNOWN (locks Create), so the retry answer is a contract rejection
  await save(); const s1 = await snap();
  await save(); const s2 = await snap(); post = null;
  for (const [id, eid] of F) {
    ok(`P4k LOW-1: #${id} 422 -> aria-invalid + ${eid} in aria-describedby; retry -> cleared, other ids kept`,
      s1[id].inv === "true" && (s1[id].desc ?? "").split(" ").includes(eid) && s2[id].inv === null && (s2[id].desc ?? "") === (base[id].desc ?? "")
      && !(s2[id].desc ?? "").split(" ").includes(eid), { base: base[id], s1: s1[id], s2: s2[id] });
  }
  ok("P4k LOW-1: retry shows the 403 banner and no field error", (await shownError())[0]?.code === "forbidden" && await ev(`[...document.querySelectorAll(".field__error")].every(e => e.hidden)`), await shownError());
  // Limits answers that are page errors (no fallback, no form).
  const LOAD_ERROR = "Something went wrong. Try again.";
  const errCases = [
    ["limits 403", { status: 403, headers: JSON_CT, body: JSON.stringify({ error: "forbidden" }) }, null],
    ["limits 302 same-site (redirect: manual, not followed)", { status: 302, headers: [{ name: "Location", value: `${H.ui}/p4k-sign-in` }], body: "" }, "other"],
    ["limits 307 same-site (redirect: manual, not followed)", { status: 307, headers: [{ name: "Location", value: `${H.ui}/p4k-sign-in` }], body: "" }, "other"],
    ["limits 3xx not followed (300, no Location)", { status: 300, headers: JSON_CT, body: "{}" }, "other"],
    ["limits 200 text/html (TLD 15:08 item 2: an invalid answer, not a verified 401)", { status: 200, headers: [{ name: "Content-Type", value: "text/html" }], body: "<!doctype html><title>Sign in</title>" }, "other"],
    ["limits 401", { status: 401, headers: [], body: "" }, "401"],
  ];
  for (const [name, m, code] of errCases) {
    seen.length = 0; mode = { kind: "fake", ...m }; await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); mode = null;
    const got = { state: await ev(`document.body.dataset.state`), lname: await vis("#lname"), err: await shownError(), text: `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim(), seen: [...seen] };
    ok(`P4k create: ${name} -> page error, no form, no fallback, Location not requested`, got.state === "error" && !got.lname && got.err.length === 1
      && (code === null || got.err[0].code === code)
      && (code !== "other" || (got.text === LOAD_ERROR && !/session has ended|sign in again/i.test(got.text)))
      && (code !== "401" || got.text === "Your session has ended. Sign in again, then try again.")
      && !got.seen.some(u => u.endsWith("/p4k-sign-in")), got);
  }
  // A JSON 200 with an invalid body still falls back.
  mode = { kind: "fake", status: 200, headers: JSON_CT, body: "not json" }; await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); mode = null;
  ok("P4k create: limits JSON 200 with an unparseable body -> fallback (form, D-108 hint)", await ev(`document.body.dataset.state`) === "create" && await txt("#reason-hint") === HINT_F, await txt("#reason-hint"));
  // Timeout: a stalled limits GET is aborted (injected 400 ms instead of LIMITS_TIMEOUT_MS) and falls back like a failed fetch.
  const { identifier } = await cdp("Page.addScriptToEvaluateOnNewDocument", { source: "globalThis.__llcLimitsTimeoutMs = 400;" });
  mode = { kind: "stall" }; const t0 = Date.now(); await open(`/c50/sa/orgs/${H.partner_a}/locations/new`); const dt = Date.now() - t0; mode = null;
  await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });
  const gotT = { state: await ev(`document.body.dataset.state`), lname: await vis("#lname"), hint: await txt("#reason-hint"), dt };
  ok("P4k create: stalled limits GET -> aborted, fallback form with the D-108 hint (no numbers)", gotT.state === "create" && gotT.lname && gotT.hint === HINT_F && dt < 8000, gotT);
  await type("#lname", "UI Cage P4k"); await type("#dname", "UI Casino A"); await mouseClick("#mfa"); await type("#reason", "too short"); await save();
  ok("P4k create (timeout fallback): 9-character reason -> real 422, D-108 copy 'This reason is too short or too long. Edit it and try again.'",
    await txt("#reason-err") === "This reason is too short or too long. Edit it and try again." && !/\d/.test(await txt("#reason-err")), await txt("#reason-err"));
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
  // Server path: the c50 API's own limits (50 / 200) -> the numbered hint and 422 copy stay.
  await open(`/c50/sa/orgs/${H.partner_a}/locations/new`);
  ok("P4k create (server limits): hint '50 to 200 characters. Saved in the audit log. Required only ...'",
    await txt("#reason-hint") === "50 to 200 characters. Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in.", await txt("#reason-hint"));
  await type("#lname", "UI Cage P4k"); await type("#dname", "UI Casino A"); await mouseClick("#mfa"); await type("#reason", "too short"); await save();
  ok("P4k create (server limits): 9-character reason -> real 422 'Enter a reason of 50 to 200 characters.'", await txt("#reason-err") === "Enter a reason of 50 to 200 characters.", await txt("#reason-err"));
  await open(`/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); await click("#btn-change"); await mouseClick("#mfa");
  ok("P4k edit (server limits): hint '50 to 200 characters. Saved in the audit log.'", await txt("#reason-hint") === "50 to 200 characters. Saved in the audit log.", await txt("#reason-hint"));
  await open(page("sa", H.l1));
  ok("P4k: nothing was saved (L1 still MFA on)", await txt("#cur-mfa") === "Yes", await txt("#cur-mfa"));
});
// P4l [TLD 2026-11-04 21:11 Q2 / Q3, UX D-109 1cab5e82, SSE2 70cf9b13 LOW-1]: redirect: "manual" on the limits GET and the
// location GET. A 302 or 307 with a Location is not followed and is the generic load error, not a verified 401.
// Fetch pauses a Location request only if the browser actually sends it. The location GET timeout stays the network error.
await step("P4l redirect / timeout", async () => {
  let mode = null; const seen = [];
  const LOC = new RegExp(`/api/orgs/locations/${H.l1}/login-config$`), LIM = /\/api\/orgs\/login-config\/limits(\?|$)/;
  const lsn = (msg) => {
    if (msg.sessionId !== S) return;
    if (msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    // The redirect targets: Fetch pauses a request only when the browser really makes it (i.e. follows the 302); it is
    // answered here, so nothing is resolved or sent off the box even then.
    if (/^https:\/\/login\.microsoftonline\.com\//.test(p.request.url) || p.request.url.endsWith("/p4l-sign-in")) {
      seen.push(p.request.url);
      return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: "text/html" }], body: Buffer.from("<!doctype html><title>Sign in</title>").toString("base64") }).catch(() => {});
    }
    if (!mode || p.request.method !== "GET" || !mode.url.test(p.request.url)) return go();
    if (mode.kind === "stall") return; // never answered: only the page's abort ends it
    return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: mode.status, responseHeaders: mode.headers, body: Buffer.from(mode.body ?? "").toString("base64") }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/api/orgs/login-config/limits*", requestStage: "Request" }, { urlPattern: `*/api/orgs/locations/${H.l1}/login-config`, requestStage: "Request" },
    { urlPattern: "https://login.microsoftonline.com/*", requestStage: "Request" }, { urlPattern: "*/p4l-sign-in", requestStage: "Request" }] });
  const MS = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=p4l&response_type=code";
  const R3 = (status, loc) => ({ kind: "fake", status, headers: [{ name: "Location", value: loc }], body: "" });
  const LOAD_ERROR = "Something went wrong. Try again.";
  const cases = [
    ["create: limits 302 to login.microsoftonline.com", `/c50/sa/orgs/${H.partner_a}/locations/new`, LIM, R3(302, MS), "other", LOAD_ERROR],
    ["create: limits 307 to login.microsoftonline.com", `/c50/sa/orgs/${H.partner_a}/locations/new`, LIM, R3(307, MS), "other", LOAD_ERROR],
    ["edit: location GET 302 to login.microsoftonline.com", `/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`, LOC, R3(302, MS), "other", LOAD_ERROR],
    ["edit: location GET 307 to login.microsoftonline.com", `/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`, LOC, R3(307, MS), "other", LOAD_ERROR],
    ["edit: location GET 302 same-site", `/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`, LOC, R3(302, `${H.ui}/p4l-sign-in`), "other", LOAD_ERROR],
    ["edit: location GET 401", `/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`, LOC, { kind: "fake", status: 401, headers: [], body: "" }, "401", "Your session has ended. Sign in again, then try again."],
    ["create: limits 401", `/c50/sa/orgs/${H.partner_a}/locations/new`, LIM, { kind: "fake", status: 401, headers: [], body: "" }, "401", "Your session has ended. Sign in again, then try again."],
  ];
  for (const [name, url, re, m, code, text] of cases) {
    seen.length = 0; const o0 = outside.length; mode = { ...m, url: re }; await open(url); mode = null;
    // DevTools announces the Location (requestWillBeSent) before redirect: "manual" drops it. Fetch.requestPaused
    // is the request the browser actually sends. Drop the announcement only when that request count is zero.
    if (seen.length === 0) outside.splice(o0, outside.length - o0, ...outside.slice(o0).filter(u => u !== MS));
    const got = { state: await ev(`document.body.dataset.state`), lname: await vis("#lname"), change: await vis("#btn-change"), form: await vis("#form"), err: await shownError(), text: `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim(), seen: [...seen], path: await ev(`location.pathname`) };
    ok(`P4l ${name} -> not followed, exact page error, no form, no fallback`, got.state === "error" && !got.lname && !got.change && !got.form
      && got.err.length === 1 && got.err[0].code === code && got.text === text && got.seen.length === 0
      && got.path === new URL(H.ui + url).pathname, got);
  }
  // A redirected limits GET after the admin is allowed to acknowledge does not open Create and does not clear the guard.
  const ackKey = `llc.unconfirmed.create.${H.partner_a}`;
  const ackAttempt = JSON.stringify({ attemptId: "3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8", startedAt: "2026-11-06T07:17:00.000Z" });
  await ev(`localStorage.setItem(${JSON.stringify(ackKey)}, ${JSON.stringify(ackAttempt)}), true`);
  mode = null; await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await until(`document.body.dataset.createAck === "allow"`, 10000);
  seen.length = 0; mode = { ...R3(302, `${H.ui}/p4l-sign-in`), url: LIM };
  await ev(`document.querySelector("#ack-missing").checked = true, true`);
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "error"`, 10000);
  await sleep(300); mode = null;
  const gotAck = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"), text: `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim(), kept: await ev(`localStorage.getItem(${JSON.stringify(ackKey)})`), seen: [...seen] };
  ok("P4l Enable after an allowed recovery: limits 302 is the generic load error, guard kept, no form, Location not requested",
    gotAck.state === "error" && !gotAck.form && gotAck.text === LOAD_ERROR && gotAck.kept === ackAttempt && gotAck.seen.length === 0, gotAck);
  await ev(`localStorage.removeItem(${JSON.stringify(ackKey)}), true`);
  // Q3: a stalled location GET is aborted (injected 400 ms instead of LIMITS_TIMEOUT_MS) -> page error, no form, no fallback hint.
  const { identifier } = await cdp("Page.addScriptToEvaluateOnNewDocument", { source: "globalThis.__llcLimitsTimeoutMs = 400;" });
  mode = { kind: "stall", url: LOC }; const t0 = Date.now(); await open(`/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); const dt = Date.now() - t0; mode = null;
  await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });
  const gotT = { state: await ev(`document.body.dataset.state`), change: await vis("#btn-change"), hint: await vis("#reason-hint"), err: await shownError(), dt };
  ok("P4l edit: stalled location GET -> aborted, page error 'Couldn't reach the server.' (network), no form, no fallback limits",
    gotT.state === "error" && !gotT.change && !gotT.hint && gotT.err.length === 1 && gotT.err[0].code === "network" && dt < 8000, gotT);
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
  // Unfaked: the c50 API answers both GETs normally (manual redirect changes nothing for a 200).
  await open(`/c50/sa/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); await click("#btn-change"); await mouseClick("#mfa");
  ok("P4l edit (server limits, manual redirect): hint '50 to 200 characters. Saved in the audit log.'", await txt("#reason-hint") === "50 to 200 characters. Saved in the audit log.", await txt("#reason-hint"));
  await open(page("sa", H.l1));
  ok("P4l: nothing was saved (L1 still MFA on)", await txt("#cur-mfa") === "Yes", await txt("#cur-mfa"));
});
// P6a [TLD 2026-11-05 07:54 fetch hardening, brief /tmp/sse-llc1/briefs/fetch-hardening.md a451c6cf; SSE2 c6249722 DQ-1 / INFO-1]:
// the history GET /changes, the PUT and the POST are bounded (abort) and use redirect: "manual". Faked with CDP Fetch at the
// Request stage, so no faked write reaches the API; stalled requests are failed by the harness afterwards (never continued).
// Non-JSON write answers stay unit-only (core.test.mjs; TL DQ-2). Edit 3xx / opaqueredirect and a real PUT 401 are asserted below.
await step("P6a fetch hardening", async () => {
  let mode = null; const seen = [], stalled = [], hits = [];
  const LOC = new RegExp(`/api/orgs/locations/${H.l1}/login-config$`), HIST = new RegExp(`/api/orgs/locations/${H.l1}/login-config/changes$`), CREATE = /\/api\/orgs\/locations$/;
  const lsn = (msg) => {
    if (msg.sessionId !== S || msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    if (/^https:\/\/login\.microsoftonline\.com\//.test(p.request.url) || p.request.url.endsWith("/p6a-sign-in")) { // only if a 3xx is followed
      seen.push(p.request.url);
      return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: "text/html" }], body: Buffer.from("<!doctype html><title>Sign in</title>").toString("base64") }).catch(() => {});
    }
    if (!mode || p.request.method !== mode.method || !mode.url.test(p.request.url)) return go();
    hits.push(p.request.method + " " + p.request.url);
    if (mode.kind === "stall") { stalled.push(id); return; } // never answered: only the page's abort ends it
    return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: mode.status, responseHeaders: mode.headers, body: Buffer.from(mode.body ?? "").toString("base64") }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: `*/api/orgs/locations/${H.l1}/login-config*`, requestStage: "Request" }, { urlPattern: "*/api/orgs/locations", requestStage: "Request" },
    { urlPattern: "https://login.microsoftonline.com/*", requestStage: "Request" }, { urlPattern: "*/p6a-sign-in", requestStage: "Request" }] });
  const release = async () => { for (const id of stalled.splice(0)) await cdp("Fetch.failRequest", { requestId: id, errorReason: "Aborted" }).catch(() => {}); };
  const MS = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=p6a&response_type=code";
  const R3 = (status, loc) => ({ kind: "fake", status, headers: [{ name: "Location", value: loc }], body: "" });
  const dropMs = (o0) => { if (seen.length === 0) outside.splice(o0, outside.length - o0, ...outside.slice(o0).filter(u => u !== MS)); }; // as P4l
  const errText = async () => `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim();
  const apiL1 = async () => { const r = await fetch(`${H.ui}/as/sa/api/orgs/locations/${H.l1}/login-config`); return [r.headers.get("ETag"), (await r.json()).display_name]; };
  const l1before = await apiL1();
  const hooks = [];
  for (const src of ["globalThis.__llcLimitsTimeoutMs = 1500;", "globalThis.__llcWriteTimeoutMs = 1500;"]) hooks.push((await cdp("Page.addScriptToEvaluateOnNewDocument", { source: src })).identifier);

  // 1. history GET stalled on load -> aborted -> the softer history copy, no form, no history card, no session copy.
  seen.length = 0; hits.length = 0; mode = { kind: "stall", method: "GET", url: HIST }; let t0 = Date.now(); await open(page("sa", H.l1)); let dt = Date.now() - t0; mode = null; await release();
  let g = { state: await ev(`document.body.dataset.state`), change: await vis("#btn-change"), hist: await vis("#card-history"), err: await shownError(), text: await errText(), dt, hits: hits.length };
  ok("P6a history GET stalled -> aborted (bounded), page error 'Couldn't load history — try again', no form, no history card, not the session copy",
    g.state === "error" && !g.change && !g.hist && g.err.length === 1 && g.err[0].code === "history_unavailable" && g.text === "Couldn't load history — try again" && g.hits === 1 && dt < 8000, g);
  // 2. history GET 302 to login.microsoftonline.com -> not followed, the same softer history copy.
  seen.length = 0; let o0 = outside.length; mode = { ...R3(302, MS), method: "GET", url: HIST }; await open(page("sa", H.l1)); mode = null; dropMs(o0);
  g = { state: await ev(`document.body.dataset.state`), change: await vis("#btn-change"), err: await shownError(), text: await errText(), seen: [...seen] };
  ok("P6a history GET 302 cross-site -> not followed (redirect: manual), page error 'Couldn't load history — try again', no form",
    g.state === "error" && !g.change && g.err.length === 1 && g.err[0].code === "history_unavailable" && g.text === "Couldn't load history — try again" && g.seen.length === 0, g);

  // [TLD 2026-11-05 15:08] cases reordered: an UNKNOWN outcome now locks Save / Create, so each 3xx case runs first.
  // 3. PUT 302 same-site is an opaqueredirect under redirect: "manual". UNKNOWN, the TLD 15:08 item 3 sentence, locked, not followed.
  const EDIT_UNKNOWN = "We couldn't confirm the save. Reload to check before trying again.";
  const onEdit = `/locations/${H.l1}/sign-in`;
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6a never saved");
  seen.length = 0; hits.length = 0; mode = { ...R3(302, `${H.ui}/p6a-sign-in`), method: "PUT", url: LOC }; await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000);
  await click("#btn-save"); await sleep(500); mode = null;
  g = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), saved: await vis("#banner-saved"), err: await shownError(), text: await errText(), seen: [...seen], puts: hits.length, btn: await attr("#btn-save", "aria-disabled"), path: await ev(`location.pathname`) };
  ok("P6a PUT 302 same-site (opaqueredirect, redirect: manual) -> not followed, exact 'We couldn't confirm the save. Reload to check before trying again.', edit kept, no saved state, Save locked (second click sent nothing)",
    g.state === "edit" && g.form && !g.saved && g.err.length === 1 && g.err[0].code === "write_unconfirmed"
      && g.text === EDIT_UNKNOWN && !/sign in again|session has ended|nothing was saved/i.test(g.text)
      && g.path.endsWith(onEdit) && g.seen.length === 0 && g.puts === 1 && g.btn === "true", g);
  // 3b. PUT 307 with no Location. A 307 that names a URL is announced by DevTools as another PUT before redirect: "manual" drops it; this case stays on the one request.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6a never saved");
  seen.length = 0; hits.length = 0; mode = { kind: "fake", status: 307, headers: [], body: "", method: "PUT", url: LOC }; await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000);
  await click("#btn-save"); await sleep(500); mode = null;
  g = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), saved: await vis("#banner-saved"), err: await shownError(), text: await errText(), seen: [...seen], puts: hits.length, btn: await attr("#btn-save", "aria-disabled"), path: await ev(`location.pathname`) };
  ok("P6a PUT 307 (no Location) -> same preserved Edit uncertainty copy, edit kept, no navigation, one PUT, Save locked (second click sent nothing)",
    g.state === "edit" && g.form && !g.saved && g.err.length === 1 && g.err[0].code === "write_unconfirmed"
      && g.text === EDIT_UNKNOWN && !/sign in again|session has ended|nothing was saved/i.test(g.text)
      && g.path.endsWith(onEdit) && g.seen.length === 0 && g.puts === 1 && g.btn === "true", g);
  // 3c. A real PUT 401 keeps the verified session copy. It does not lock Save and the page does not send another PUT on its own.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6a 401 never saved");
  hits.length = 0; mode = { kind: "fake", status: 401, headers: [], body: "", method: "PUT", url: LOC }; await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000);
  await sleep(400); mode = null;
  g = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), saved: await vis("#banner-saved"), err: await shownError(), text: await errText(), puts: hits.length, btn: await attr("#btn-save", "aria-disabled"), path: await ev(`location.pathname`) };
  ok("P6a PUT 401 keeps 'Your session has ended. Sign in again, then try again.', does not lock Save, and does not use the uncertainty copy",
    g.state === "edit" && g.form && !g.saved && g.err.length === 1 && g.err[0].code === "401"
      && g.text === "Your session has ended. Sign in again, then try again." && !/couldn.t confirm the save/i.test(g.text)
      && g.path.endsWith(onEdit) && g.puts === 1 && g.btn === null, g);
  // 4. PUT stalled -> aborted -> "We couldn't confirm the save. ..." exactly; no saved state, one PUT, Save locked (a second click sends nothing).
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6a never saved"); // case 3 left its page locked
  hits.length = 0; mode = { kind: "stall", method: "PUT", url: LOC }; t0 = Date.now(); await click("#btn-save"); await until(`document.querySelector("#banner-error").hidden === false`, 10000); dt = Date.now() - t0; await sleep(300); await release();
  await click("#btn-save"); await sleep(1700); mode = null; await release();
  g = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), form: await vis("#form"), err: await shownError(), text: await errText(), dt, puts: hits.length, btn: await attr("#btn-save", "aria-disabled") };
  ok("P6a PUT stalled -> aborted (bounded), 'We couldn't confirm the save. Reload to check before trying again.', no saved state, form kept, one PUT; Save locked (second click sends nothing)",
    g.state === "edit" && !g.saved && g.form && g.err.length === 1 && g.err[0].code === "write_unconfirmed" && g.text === "We couldn't confirm the save. Reload to check before trying again."
      && !/nothing was saved/i.test(g.text) && g.puts === 1 && g.btn === "true" && dt < 8000, g);

  // 5. POST 307 to login.microsoftonline.com -> not followed; [TLD 16:00] UNKNOWN + sign in again: the blocked create state, kept over a reload.
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6a"); await type("#dname", "UI Casino A");
  seen.length = 0; hits.length = 0; o0 = outside.length; mode = { ...R3(307, MS), method: "POST", url: CREATE }; await click("#btn-save"); await until(`document.body.dataset.state === "blocked"`, 10000); mode = null; dropMs(o0);
  g = { path: await ev(`location.pathname`), state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: await shownError(), text: await errText(), seen: [...seen], posts: hits.length,
    key: await ev(`localStorage.getItem(${JSON.stringify(`llc.unconfirmed.create.${H.partner_a}`)})`) };
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); g.after = { state: await ev(`document.body.dataset.state`), form: await vis("#form") };
  ok("P6a POST 307 cross-site -> not followed, UNKNOWN without a claimed session expiry (DEC-144 copy), no form, one POST, guard stored and still blocked after a reload",
    g.path.endsWith("/locations/new") && g.state === "blocked" && !g.form && g.err.length === 1 && g.err[0].code === "create_unconfirmed_blocked"
      && g.text === CREATE_UNKNOWN_TEXT && !/sign in again/i.test(g.text) && g.seen.length === 0 && g.posts === 1 && g.key !== null && g.after.state === "blocked" && !g.after.form, g);
  await ev(`localStorage.clear(), true`); // test isolation only
  // 6. POST (create) stalled -> UNKNOWN: the blocked state "We couldn’t confirm the create." (no reload advice), no form, one POST.
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6a"); await type("#dname", "UI Casino A"); // case 5 left its page blocked
  hits.length = 0; mode = { kind: "stall", method: "POST", url: CREATE }; await click("#btn-save"); await until(`document.body.dataset.state === "blocked"`, 10000); await sleep(1700); mode = null; await release();
  g = { path: await ev(`location.pathname`), hash: await ev(`location.hash`), err: await shownError(), text: await errText(), posts: hits.length, state: await ev(`document.body.dataset.state`), form: await vis("#form") };
  ok("P6a POST (create) stalled -> blocked with the DEC-144 check-before-trying-again copy, no form, still on /new, one POST",
    g.path.endsWith("/locations/new") && g.hash === "" && g.state === "blocked" && !g.form && g.err.length === 1 && g.err[0].code === "create_unconfirmed_blocked"
      && g.text === CREATE_UNKNOWN_TEXT && g.posts === 1, g);
  await ev(`localStorage.clear(), true`); // test isolation only: later steps create under partner_a (the page itself never unlocks a Create)

  for (const identifier of hooks) await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
  // Unfaked: the real API answers; the hardened fetches change nothing for a normal load (history rows shown).
  await open(page("sa", H.l1));
  const r = await rows();
  ok("P6a unfaked: SA view of L1 loads with history rows (manual redirect + abort change nothing for 200s)", await ev(`document.body.dataset.state`) === "view" && await vis("#card-history") && r.length >= 1, r.length);
  ok("P6a: nothing was saved by the faked writes (L1 ETag and display name unchanged)", JSON.stringify(await apiL1()) === JSON.stringify(l1before), [l1before, await apiL1()]);
});
// P6b [fetch hardening follow-up, after p6a mutant f10 survived]: the abort bounds the WHOLE answer, also when the headers
// have arrived and the body never completes. CDP Fetch can't split headers from body, so a test-only fetch wrapper
// (injected before the page's scripts) answers the matched call with real-looking headers and a body stream that sends a
// partial chunk and then hangs; like a real fetch body, the stream errors with AbortError when the request's signal aborts.
// History: the real API answers (request and headers are real), only its body is withheld. PUT / POST: never sent to the API.
await step("P6b body stall", async () => {
  const WRAP = `(() => {
    const real = globalThis.fetch.bind(globalThis); globalThis.__p6bCalls = [];
    globalThis.fetch = async (input, init = {}) => {
      const m = globalThis.__p6bMode, url = String(input), method = init.method || "GET";
      if (!m || method !== m.method || !new RegExp(m.url).test(url)) return real(input, init);
      globalThis.__p6bCalls.push(method + " " + url);
      let head = null;
      if (m.real) { head = await real(input, init); try { head.body && head.body.cancel(); } catch {} }
      const sig = init.signal;
      const body = new ReadableStream({ start(c) {
        c.enqueue(new TextEncoder().encode(m.partial));
        if (m.lateMs) { setTimeout(() => { c.enqueue(new TextEncoder().encode(m.rest)); c.close(); }, m.lateMs); return; } // ignores the abort
        const fail = () => c.error(new DOMException("The operation was aborted.", "AbortError"));
        if (sig) { if (sig.aborted) fail(); else sig.addEventListener("abort", fail, { once: true }); }
      } });
      return new Response(body, head ? { status: head.status, headers: head.headers } : { status: m.status, headers: m.headers });
    };
  })();`;
  const ids = [];
  for (const src of [WRAP, "globalThis.__llcLimitsTimeoutMs = 1500;", "globalThis.__llcWriteTimeoutMs = 1500;"]) ids.push((await cdp("Page.addScriptToEvaluateOnNewDocument", { source: src })).identifier);
  const errText = async () => `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim();
  const apiL1 = async () => { const r = await fetch(`${H.ui}/as/sa/api/orgs/locations/${H.l1}/login-config`); return [r.headers.get("ETag"), (await r.json()).display_name]; };
  const l1before = await apiL1();
  const JSONH = { "Content-Type": "application/json", "ETag": '"99"' };
  const UNCONF = "We couldn't confirm the save. Reload to check before trying again.";

  // 1. history GET: real 200 headers, body withheld -> aborted -> the softer history copy, no form.
  const hm = (await cdp("Page.addScriptToEvaluateOnNewDocument", { source: `globalThis.__p6bMode = ${JSON.stringify({ method: "GET", url: `/api/orgs/locations/${H.l1}/login-config/changes$`, real: true, partial: '{"items":[' })};` })).identifier;
  let t0 = Date.now(); await open(page("sa", H.l1)); let dt = Date.now() - t0;
  await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier: hm });
  let g = { state: await ev(`document.body.dataset.state`), change: await vis("#btn-change"), hist: await vis("#card-history"), err: await shownError(), text: await errText(), calls: await ev(`globalThis.__p6bCalls.length`), dt };
  ok("P6b history GET: 200 headers arrive, body never completes -> aborted (bounded), page error 'Couldn't load history — try again', no form, no history card",
    g.state === "error" && !g.change && !g.hist && g.err.length === 1 && g.err[0].code === "history_unavailable" && g.text === "Couldn't load history — try again" && g.calls === 1 && dt < 8000, g);

  // 1b. location GET: real 200 headers, body withheld -> aborted -> the same page error as a stalled location GET before
  //     its headers (P4l: 'network'), not the invalid-answer error a complete-but-unparseable 200 gets: the abort bounds the
  //     whole answer, and a body cut off by the abort is a timeout, not a malformed response (p6b mutant f10).
  const lm = (await cdp("Page.addScriptToEvaluateOnNewDocument", { source: `globalThis.__p6bMode = ${JSON.stringify({ method: "GET", url: `/api/orgs/locations/${H.l1}/login-config$`, real: true, partial: '{"location_id":"' })};` })).identifier;
  t0 = Date.now(); await open(page("sa", H.l1)); dt = Date.now() - t0;
  await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier: lm });
  g = { state: await ev(`document.body.dataset.state`), change: await vis("#btn-change"), err: await shownError(), text: await errText(), calls: await ev(`globalThis.__p6bCalls.length`), dt };
  ok("P6b location GET: 200 headers arrive, body never completes -> aborted (bounded), the same page error as a header stall ('network', P4l), not the invalid-answer error; no form",
    g.state === "error" && !g.change && g.err.length === 1 && g.err[0].code === "network" && g.calls === 1 && dt < 8000, g);

  // 2. PUT: 200 + ETag headers, partial JSON body, then nothing -> unconfirmed copy, not saved, one PUT, no retry; the request
  //    is no longer in flight and Save stays locked (TLD 15:08 item 4) instead of busy.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6b never saved");
  await ev(`globalThis.__p6bMode = ${JSON.stringify({ method: "PUT", url: `/api/orgs/locations/${H.l1}/login-config$`, status: 200, headers: JSONH, partial: '{"location_id":"' })}, true`);
  t0 = Date.now(); await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000); dt = Date.now() - t0; await click("#btn-save"); await sleep(1700); // second click: locked, sends nothing
  g = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), form: await vis("#form"), err: await shownError(), text: await errText(), dt,
    calls: await ev(`globalThis.__p6bCalls.length`), btn: await attr("#btn-save", "aria-disabled"), cur: await txt("#cur-h-text") };
  ok("P6b PUT: 200 headers arrive, body never completes -> aborted (bounded: save() returned), the exact unconfirmed copy, Save locked, no saved banner / state, form kept, one PUT (no retry)",
    g.state === "edit" && !g.saved && g.form && g.err.length === 1 && g.err[0].code === "write_unconfirmed" && g.text === UNCONF && g.btn === "true" && g.calls === 1 && dt < 8000, g);

  // 2b. [TLD 16:00] obsolete late completion: 200 headers, then the rest of a VALID saved body arrives only after the 1.5 s bound,
  //     from a body stream that ignores the abort. The page must stay UNKNOWN + locked and never turn it into a saved state.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6b late answer");
  await ev(`globalThis.__p6bMode = ${JSON.stringify({ method: "PUT", url: `/api/orgs/locations/${H.l1}/login-config$`, status: 200, headers: JSONH, partial: '{"row_version":', rest: '99,"name":"late"}', lateMs: 3500 })}, true`);
  t0 = Date.now(); await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000); dt = Date.now() - t0; await sleep(3500);
  g = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), err: await shownError(), text: await errText(), dt, calls: await ev(`globalThis.__p6bCalls.length`), btn: await attr("#btn-save", "aria-disabled") };
  ok("P6b PUT: the valid body completes only after the bound -> still UNKNOWN (exact unconfirmed copy), Save locked, never saved (late completion ignored)",
    g.state === "edit" && !g.saved && g.err.length === 1 && g.err[0].code === "write_unconfirmed" && g.text === UNCONF && g.btn === "true" && g.calls === 1 && dt < 3000, g);

  // 3. POST (create): 201 headers, partial body, then nothing -> the blocked create state, still on /new, no navigation, one POST.
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6b"); await type("#dname", "UI Casino A");
  await ev(`globalThis.__p6bMode = ${JSON.stringify({ method: "POST", url: "/api/orgs/locations$", status: 201, headers: { "Content-Type": "application/json" }, partial: '{"location_id":"' })}, true`);
  t0 = Date.now(); await click("#btn-save"); await until(`document.body.dataset.state === "blocked" || !document.querySelector("#banner-error").hidden`, 10000); dt = Date.now() - t0; await sleep(1700);
  g = { path: await ev(`location.pathname`), hash: await ev(`location.hash`), state: await ev(`document.body.dataset.state`), err: await shownError(), text: await errText(),
    calls: await ev(`globalThis.__p6bCalls.length`), form: await vis("#form"), dt };
  ok("P6b POST: 201 headers arrive, body never completes -> aborted (bounded), DEC-144 blocked copy, no form, still on /new (no navigation), one POST",
    g.path.endsWith("/locations/new") && g.hash === "" && g.state === "blocked" && !g.form && g.err.length === 1 && g.err[0].code === "create_unconfirmed_blocked"
      && g.text === CREATE_UNKNOWN_TEXT && g.calls === 1 && dt < 8000, g);
  await ev(`localStorage.clear(), true`); // test isolation only

  for (const identifier of ids) await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });
  // Unwrapped again: the normal page and API, nothing was saved.
  await open(page("sa", H.l1));
  ok("P6b unwrapped: SA view of L1 loads normally with history rows; the wrapper is gone", await ev(`document.body.dataset.state`) === "view" && (await rows()).length >= 1 && await ev(`globalThis.__p6bCalls === undefined`));
  ok("P6b: nothing was saved (L1 ETag and display name unchanged)", JSON.stringify(await apiL1()) === JSON.stringify(l1before), [l1before, await apiL1()]);
});
// P6c [TLD 2026-11-05 15:08 items 1-5]: UNKNOWN outcomes after dispatch, the Save / Create lock and its persistence, Edit
// reconciliation by a fresh GET, no resubmission, and a confirmed save whose history GET fails. CDP Fetch: "lost" = the real
// API answered (so the server committed) and the harness drops the answer at the Response stage; "netfail" = the request is
// failed before it is sent; "bad" = a faked 200 with an unparseable JSON body. Real writes here: one lost PUT, one lost create,
// one confirmed PUT (counted in the real-PG totals at the end).
let p6cCreated = 0, p6cSaChanges = 0, gap1PaChanges = 0, gap2PaChanges = 0;
await step("P6c uncertain outcomes", async () => {
  let mode = null; const hits = [];
  const LOC = new RegExp(`/api/orgs/locations/${H.l1}/login-config$`), HIST = new RegExp(`/api/orgs/locations/${H.l1}/login-config/changes$`), CREATE = /\/api\/orgs\/locations$/;
  const lsn = (msg) => {
    if (msg.sessionId !== S || msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    const resp = p.responseStatusCode !== undefined;
    if (!mode || p.request.method !== mode.method || !mode.url.test(p.request.url)) return resp ? cdp("Fetch.continueResponse", { requestId: id }).catch(() => go()) : go();
    if (mode.kind === "lost") { if (!resp) return go(); hits.push(`${p.responseStatusCode} ${p.request.method}`); return cdp("Fetch.failRequest", { requestId: id, errorReason: "ConnectionReset" }).catch(() => {}); }
    if (resp) return cdp("Fetch.continueResponse", { requestId: id }).catch(() => go());
    hits.push(p.request.method);
    if (mode.kind === "netfail") return cdp("Fetch.failRequest", { requestId: id, errorReason: "ConnectionReset" }).catch(() => {});
    return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: mode.status, responseHeaders: mode.headers, body: Buffer.from(mode.body ?? "").toString("base64") }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: `*/api/orgs/locations/${H.l1}/login-config*`, requestStage: "Request" }, { urlPattern: `*/api/orgs/locations/${H.l1}/login-config`, requestStage: "Response" },
    { urlPattern: "*/api/orgs/locations", requestStage: "Request" }, { urlPattern: "*/api/orgs/locations", requestStage: "Response" }] });
  const errText = async () => `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim();
  const UNCONF = "We couldn't confirm the save. Reload to check before trying again.";
  const apiL1 = async () => { const r = await fetch(`${H.ui}/as/sa/api/orgs/locations/${H.l1}/login-config`); const b = await r.json(); return { etag: r.headers.get("ETag"), dname: b.display_name }; };
  const sent = (method, re) => bodies.filter(b => b.method === method && re.test(b.url)).length;
  const createKey = `llc.unconfirmed.create.${H.partner_a}`;
  const store = (k) => ev(`localStorage.getItem(${JSON.stringify(k)})`);
  const unknownEdit = async (name) => {
    const g = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), err: await shownError(), text: await errText(), btn: await attr("#btn-save", "aria-disabled") };
    ok(`P6c ${name} -> UNKNOWN: exact unconfirmed copy, not saved, Save locked`,
      g.state === "edit" && !g.saved && g.err.length === 1 && g.err[0].code === "write_unconfirmed" && g.text === UNCONF && g.btn === "true", g);
  };

  // 1. Server commits, then the answer is lost (PUT): UNKNOWN; a second click sends nothing; a sign-in round trip keeps the
  //    lock; a fresh successful GET (reload) reconciles: the committed version is shown, no PUT is replayed, Save is free again.
  const v0 = await apiL1();
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6c committed, answer lost");
  const put0 = sent("PUT", LOC);
  hits.length = 0; mode = { kind: "lost", method: "PUT", url: LOC }; await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000); mode = null;
  await unknownEdit("PUT committed, answer lost");
  await click("#btn-save"); await sleep(500);
  const v1 = await apiL1(); p6cSaChanges++;
  ok("P6c PUT committed, answer lost: the server did commit (version advanced, new display name); exactly one PUT was sent, the second click sent nothing",
    v1.etag !== v0.etag && v1.dname === "UI P6c committed, answer lost" && hits.length === 1 && /^200 /.test(hits[0]) && sent("PUT", LOC) === put0 + 1, { v0, v1, hits, put: sent("PUT", LOC) - put0 });
  await open(`/as/anon/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); // the sign-in round trip: an unauthenticated page (401)
  const gA = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: await shownError(), put: sent("PUT", LOC) - put0 };
  ok("P6c re-auth: an unauthenticated load (401 page error) shows no form and replays nothing (not a reconciliation)", gA.state === "error" && !gA.form && gA.err[0]?.code === "401" && gA.put === 1, gA);
  await open(page("sa", H.l1));
  const gV = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), put: sent("PUT", LOC) - put0 };
  await click("#btn-change");
  const gR = { state: await ev(`document.body.dataset.state`), btn: await attr("#btn-save", "aria-disabled"), dname: await ev(`document.querySelector("#dname").value`), put: sent("PUT", LOC) - put0 };
  ok("P6c reload = fresh successful GET: view mode first (inspect), then Change fills the form with the SAVED value from that GET (not a draft), Save free, no PUT replayed",
    gV.state === "view" && !gV.form && gV.put === 1 && gR.state === "edit" && gR.btn === null && gR.dname === v1.dname && gR.put === 1, { gV, gR });

  // 2. Non-timeout network failure (connection reset) on the PUT -> UNKNOWN (nothing reached the API here, but the page can't tell).
  await type("#dname", "UI P6c network loss");
  hits.length = 0; mode = { kind: "netfail", method: "PUT", url: LOC }; await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000); mode = null;
  await unknownEdit("PUT network failure (not a timeout)");
  await open(page("sa", H.l1)); await click("#btn-change");
  // 3. A 200 whose JSON body does not parse -> UNKNOWN (invalid answer, not a saved state, not a 401).
  await type("#dname", "UI P6c invalid 200");
  mode = { kind: "bad", method: "PUT", url: LOC, status: 200, headers: [{ name: "Content-Type", value: "application/json" }, { name: "ETag", value: '"77"' }], body: '{"row_version": 7, "name": ' };
  await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden`, 10000); mode = null;
  await unknownEdit("PUT 200 with an unparseable JSON body");
  ok("P6c: the network-failure and invalid-200 PUTs changed nothing on the server", JSON.stringify(await apiL1()) === JSON.stringify(v1), [v1, await apiL1()]);

  // 3b. [TLD 16:00] re-entry: a fresh GET is the current snapshot, NOT proof the abandoned write can't still land. The
  //     abandoned PUT lands after the page's GET (simulated by the same API, with the GET's ETag); the deliberate save keeps the
  //     If-Match from the page's GET, so it is refused (409 concurrency_conflict), never overwrites, and is not retried.
  const lapi = `${H.ui}/as/sa/api/orgs/locations/${H.l1}/login-config`;
  const land = async (name) => { const g0 = await fetch(lapi); const c0 = await g0.json();
    const r0 = await fetch(lapi, { method: "PUT", headers: { "Content-Type": "application/json", "If-Match": g0.headers.get("ETag") }, body: JSON.stringify({ provider_kind: c0.provider_kind,
      display_name: name, entra_allowed_tenant_ids: c0.entra_allowed_tenant_ids, allow_db_fallback: c0.allow_db_fallback, mfa_required: c0.mfa_required, reason: null }) });
    if (r0.status === 200) p6cSaChanges++; return r0.status; };
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6c deliberate save");
  const ls = await land("UI P6c abandoned write landed late");
  const putD = sent("PUT", LOC); await click("#btn-save"); await until(`!document.querySelector("#banner-error").hidden || !document.querySelector("#banner-conflict").hidden`, 10000); await sleep(500);
  const gE = { land: ls, err: await shownError(), saved: await vis("#banner-saved"), puts: sent("PUT", LOC) - putD, srv: (await apiL1()).dname };
  ok("P6c re-entry: an abandoned write landing after the fresh GET -> the deliberate save carries the GET's If-Match and is refused (409 concurrency_conflict), nothing overwritten, one PUT, no retry",
    gE.land === 200 && gE.err[0]?.code === "concurrency_conflict" && !gE.saved && gE.puts === 1 && gE.srv === "UI P6c abandoned write landed late", gE);
  // Back-forward: the server changes while the page is in history; going back must not show / edit the old snapshot.
  await open(page("sa", H.l1)); await open(page("sa", H.l4));
  const lb = await land("UI P6c changed while away");
  await ev(`history.back(), true`); await sleep(300);
  await until(`location.pathname.includes(${JSON.stringify(H.l1)}) && document.readyState === "complete" && document.body.dataset.state === "view"`, 10000);
  await click("#btn-change");
  const gB = { land: lb, path: await ev(`location.pathname`), state: await ev(`document.body.dataset.state`), dname: await ev(`document.querySelector("#dname").value`), btn: await attr("#btn-save", "aria-disabled") };
  ok("P6c back-forward: going back to the edit page shows the CURRENT server value (fresh GET, not the old snapshot) before any edit",
    gB.land === 200 && gB.state === "edit" && gB.dname === "UI P6c changed while away" && gB.btn === null, gB);
  await click("#btn-cancel");

  // 4. Create: the server commits, the 201 is lost -> blocked (no reload advice). Reload and a sign-in round trip keep it blocked;
  //    no second POST is ever sent. S2a has no surface to tell whether THIS create happened (see BUILD-LOG, Create gap).
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6c lost"); await type("#dname", "UI Casino A");
  const post0 = sent("POST", CREATE);
  hits.length = 0; mode = { kind: "lost", method: "POST", url: CREATE }; await click("#btn-save"); await until(`document.body.dataset.state === "blocked"`, 10000); mode = null; p6cCreated++; p6cSaChanges++;
  const gC = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: await shownError(), text: await errText(), hits: [...hits], key: await store(createKey) };
  ok("P6c create committed, 201 lost -> blocked with the DEC-144 copy, no form, unresolved create stored",
    gC.state === "blocked" && !gC.form && gC.err[0]?.code === "create_unconfirmed_blocked" && gC.text === CREATE_UNKNOWN_TEXT && gC.hits.length === 1 && /^201 /.test(gC.hits[0]) && gC.key !== null, gC);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
  const gC2 = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: (await shownError())[0]?.code };
  await open(`/as/anon/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); await open(`/as/pa/orgs/${H.partner_a}/locations/new`);
  const gC3 = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: (await shownError())[0]?.code, posts: sent("POST", CREATE) - post0 };
  ok("P6c create: reload and a sign-in round trip (other persona, same browser) stay blocked, no form, never unlocked; exactly one POST was ever sent",
    gC2.state === "blocked" && !gC2.form && gC2.err === "create_unconfirmed_blocked" && gC3.state === "blocked" && !gC3.form && gC3.err === "create_unconfirmed_blocked" && gC3.posts === 1, { gC2, gC3 });
  await ev(`localStorage.clear(), true`); // test isolation only (later steps create under partner_a); the page has no unlock

  // 5. A confirmed save (contract-valid PUT 200) whose history GET fails stays SAVED: soft history-only error; retry = GET only.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6c saved, history failed");
  const put1 = sent("PUT", LOC);
  mode = { kind: "bad", method: "GET", url: HIST, status: 500, headers: [{ name: "Content-Type", value: "application/json" }], body: '{"error":"x"}' };
  await save(); p6cSaChanges++;
  const gS = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), hist: await vis("#card-history"), herr: await vis("#history-error"), htext: await txt("#history-error"), rows: (await rows()).length, err: await shownError() };
  ok("P6c confirmed save + history GET 500 -> SAVED (saved banner, state saved), soft 'Couldn't load history — try again' in the history card only, no error banner",
    gS.state === "saved" && gS.saved && gS.hist && gS.herr && gS.htext === "Couldn't load history — try again" && gS.rows === 0 && gS.err.length === 0, gS);
  await click("#history-retry"); await sleep(500);
  const gS2 = { herr: await vis("#history-error"), saved: await vis("#banner-saved"), put: sent("PUT", LOC) - put1 };
  mode = null; await click("#history-retry"); await until(`document.querySelectorAll("#history-rows tr").length > 0`, 5000);
  const gS3 = { herr: await vis("#history-error"), rows: (await rows()).length, state: await ev(`document.body.dataset.state`), put: sent("PUT", LOC) - put1, top: (await rows())[0]?.[3] ?? "" };
  ok("P6c history retry: still failing -> soft error kept, still saved; then OK -> rows shown, error gone, still saved; retries sent no PUT (exactly the one save)",
    gS2.herr && gS2.saved && gS2.put === 1 && !gS3.herr && gS3.rows >= 1 && gS3.state === "saved" && gS3.put === 1, { gS2, gS3 });

  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
});
// P6d [closes p6c survivors b1 / h3 / g1; TLD 2026-11-05 16:00 / 16:47]. Real writes here: 2 SA PUTs (b1) and 1 SA create
// (h3), counted in the real-PG totals at the end.
await step("P6d re-entry / saved landing / cross-page guard", async () => {
  let fake = null; const hits = [], gets = [], extra = new Map();
  const LOC = new RegExp(`/api/orgs/locations/${H.l1}/login-config$`), CREATE = /\/api\/orgs\/locations$/, ANYHIST = /\/api\/orgs\/locations\/[0-9a-f-]+\/login-config\/changes$/;
  const lsn = (msg) => {
    if (msg.sessionId !== S) return;
    if (msg.method === "Network.requestWillBeSent" && msg.params.request.method === "GET" && LOC.test(msg.params.request.url)) gets.push(msg.params.requestId);
    if (msg.method === "Network.requestWillBeSentExtraInfo") extra.set(msg.params.requestId, msg.params.headers); // the headers as sent on the wire
    if (msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId;
    hits.push(`${p.request.method} ${new URL(p.request.url).pathname}`);
    if (fake && p.request.method === "GET" && ANYHIST.test(p.request.url))
      return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: "text/html" }], body: Buffer.from("<html>sign in</html>").toString("base64") }).catch(() => {});
    return cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
  };
  listeners.push(lsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/api/orgs/locations*", requestStage: "Request" }] });
  const sent = (method, re) => bodies.filter(b => b.method === method && re.test(b.url)).length;
  const errText = async () => `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim();
  const lapi = `${H.ui}/as/sa/api/orgs/locations/${H.l1}/login-config`;
  const land = async (name) => { const g0 = await fetch(lapi); const c0 = await g0.json();
    const r0 = await fetch(lapi, { method: "PUT", headers: { "Content-Type": "application/json", "If-Match": g0.headers.get("ETag") }, body: JSON.stringify({ provider_kind: c0.provider_kind,
      display_name: name, entra_allowed_tenant_ids: c0.entra_allowed_tenant_ids, allow_db_fallback: c0.allow_db_fallback, mfa_required: c0.mfa_required, reason: null }) });
    if (r0.status === 200) p6cSaChanges++; return r0.status; };

  // b1. pageshow with persisted = true. SYNTHETIC: headless Chrome under CDP did not restore pages from the bfcache (p6c b1), so
  //     the harness dispatches the event the browser fires on a bfcache restore. This tests the page's handler, NOT real bfcache.
  //     A stale page (old snapshot + typed draft + old ETag) must be replaced by a fresh no-store GET before any enabled form.
  await open(page("sa", H.l1)); await click("#btn-change"); await type("#dname", "UI P6d stale draft");
  await ev(`globalThis.__p6dOldDoc = true`);
  const lb = await land("UI P6d changed while hidden");
  const cur = await (await fetch(lapi)).headers.get("ETag");
  gets.length = 0; hits.length = 0; const put0 = sent("PUT", LOC);
  await ev(`dispatchEvent(new PageTransitionEvent("pageshow", { persisted: true })), true`);
  await sleep(300);
  await until(`globalThis.__p6dOldDoc === undefined && document.readyState === "complete" && document.body.dataset.state === "view"`, 8000).catch(() => {});
  const gB = { land: lb, oldDoc: await ev(`globalThis.__p6dOldDoc === true`), state: await ev(`document.body.dataset.state`), form: await vis("#form"),
    gets: gets.length, wire: gets.map(id => { const h = extra.get(id) ?? {}; return { cc: h["cache-control"] ?? h["Cache-Control"] ?? null, pragma: h["pragma"] ?? h["Pragma"] ?? null }; }),
    net: hits.filter(h => h === `GET /as/sa/api/orgs/locations/${H.l1}/login-config`).length, put: sent("PUT", LOC) - put0 };
  // no-store: the fetch spec makes a cache: "no-store" request send Cache-Control / Pragma: no-cache on the wire; the request also
  // reached the network (Fetch interception), so no cached answer was used.
  gB.noStore = gB.wire.length > 0 && gB.wire.every(w => /no-cache/i.test(w.cc ?? "") && /no-cache/i.test(w.pragma ?? ""));
  ok("P6d b1 (SYNTHETIC pageshow persisted=true, not a real bfcache restore): the stale document is replaced; a fresh no-store GET (Cache-Control / Pragma no-cache) loads view mode first; no form, no PUT",
    gB.land === 200 && !gB.oldDoc && gB.state === "view" && !gB.form && gB.gets >= 1 && gB.noStore && gB.net >= 1 && gB.put === 0, gB);
  await click("#btn-change");
  const gB2 = { dname: await ev(`document.querySelector("#dname").value`), btn: await attr("#btn-save", "aria-disabled") };
  await type("#dname", "UI P6d deliberate after restore"); await save();
  const last = bodies.filter(b => b.method === "PUT" && LOC.test(b.url)).at(-1);
  const gB3 = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), ifMatch: last?.ifMatch, cur, put: sent("PUT", LOC) - put0, srv: (await (await fetch(lapi)).json()).display_name };
  if (gB3.saved) p6cSaChanges++;
  ok("P6d b1: Change shows the CURRENT server value (not the stale draft); the deliberate save carries the fresh GET's ETag as If-Match and is saved (one PUT)",
    gB2.dname === "UI P6d changed while hidden" && gB2.btn === null && gB3.state === "saved" && gB3.saved && gB3.ifMatch === cur && gB3.put === 1 && gB3.srv === "UI P6d deliberate after restore", { gB2, gB3 });

  // h3. A confirmed create lands on /locations/{new id}/sign-in#saved; its history GET answers unusably (non-JSON 200 =
  //     "unavailable"). The page stays SAVED with the soft history-only error; retry sends GETs only, never another POST.
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6d saved landing"); await type("#dname", "UI Casino A");
  const post0 = sent("POST", CREATE); fake = true; hits.length = 0;
  await click("#btn-save");
  await until(`/\/locations\/[0-9a-f-]{36}\/sign-in$/.test(location.pathname) && document.readyState === "complete" && ["saved", "error", "view"].includes(document.body.dataset.state)`, 10000);
  p6cCreated++; p6cSaChanges++;
  const newId = await ev(`location.pathname.split("/").at(-2)`);
  const gH = { id: newId, state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), loc: await txt("#saved-loc"), hist: await vis("#card-history"), herr: await vis("#history-error"),
    htext: await txt("#history-error"), err: await shownError(), fakedHist: hits.filter(h => h === `GET /as/sa/api/orgs/locations/${newId}/login-config/changes`).length, posts: sent("POST", CREATE) - post0 };
  ok("P6d h3: confirmed create -> #saved landing on the new location id with an unusable history GET -> SAVED (banner, name), soft 'Couldn't load history — try again' only, no error banner, one POST",
    /^[0-9a-f-]{36}$/.test(gH.id) && gH.state === "saved" && gH.saved && gH.loc === "UI Cage P6d saved landing" && gH.hist && gH.herr && gH.htext === "Couldn't load history — try again"
      && gH.err.length === 0 && gH.fakedHist >= 1 && gH.posts === 1, gH);
  hits.length = 0; await click("#history-retry"); await sleep(500);
  const gH2 = { herr: await vis("#history-error"), state: await ev(`document.body.dataset.state`), hits: [...hits] };
  fake = null; hits.length = 0; await click("#history-retry"); await until(`document.querySelectorAll("#history-rows tr").length > 0`, 5000).catch(() => {});
  const gH3 = { herr: await vis("#history-error"), rows: (await rows()).length, state: await ev(`document.body.dataset.state`), hits: [...hits], posts: sent("POST", CREATE) - post0 };
  ok("P6d h3 retry: still unusable -> soft error kept, still saved; then OK -> the create row shown, error gone, still saved; each retry sent one history GET and no POST / PUT",
    gH2.herr && gH2.state === "saved" && gH2.hits.length === 1 && /^GET .*\/login-config\/changes$/.test(gH2.hits[0])
      && !gH3.herr && gH3.rows >= 1 && gH3.state === "saved" && gH3.hits.length === 1 && /^GET .*\/login-config\/changes$/.test(gH3.hits[0]) && gH3.posts === 1, { gH2, gH3 });

  // g1. Cross-page Create guard (same browser storage). Page A loads clean and its form is filled; then the unknown-create guard
  //     appears in storage, as page B's lockUnknown() writes it (the harness writes the same minimal marker: key + ISO timestamp,
  //     no draft). A's stale enabled form must not POST; the block holds over a reload and a sign-in round trip. Cross-tab is
  //     best effort only (this browser's storage), never a duplicate guarantee.
  const ckey = `llc.unconfirmed.create.${H.partner_a}`;
  await ev(`localStorage.clear(), true`);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await type("#lname", "UI Cage P6d never created"); await type("#dname", "UI Casino A");
  const gG0 = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), btn: await attr("#btn-save", "aria-disabled") };
  await ev(`localStorage.setItem(${JSON.stringify(ckey)}, new Date().toISOString()), true`); // page B's marker
  const post1 = sent("POST", CREATE); hits.length = 0;
  await click("#btn-save"); await until(`document.body.dataset.state === "blocked"`, 5000).catch(() => {}); await sleep(300);
  const gG = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), err: await shownError(), text: await errText(),
    posts: sent("POST", CREATE) - post1, hits: hits.filter(h => h.startsWith("POST")).length };
  ok("P6d g1: page A (clean load, filled form) after page B's unknown-create marker -> Create sends NO POST; DEC-144 blocked copy, no form",
    gG0.state === "create" && gG0.form && gG0.btn === null && gG.state === "blocked" && !gG.form && gG.err[0]?.code === "create_unconfirmed_blocked"
      && gG.text === CREATE_UNKNOWN_TEXT && gG.posts === 0 && gG.hits === 0, { gG0, gG });
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); const gG2 = { state: await ev(`document.body.dataset.state`), form: await vis("#form") };
  await open(`/as/anon/orgs/${H.partner_a}/locations/${H.l1}/sign-in`); await open(`/as/pa/orgs/${H.partner_a}/locations/new`);
  const gG3 = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), key: await ev(`localStorage.getItem(${JSON.stringify(ckey)}) !== null`), posts: sent("POST", CREATE) - post1 };
  ok("P6d g1: the block holds over a reload and a sign-in round trip (other persona); the guard is still stored; still no POST",
    gG2.state === "blocked" && !gG2.form && gG3.state === "blocked" && !gG3.form && gG3.key && gG3.posts === 0, { gG2, gG3 });
  await ev(`localStorage.clear(), true`); // test isolation only

  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(lsn), 1);
});
await step("PA partner-create", async () => {
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`);
  ok("partner-create: chip 'Partner Admin: <URL partner id>', MFA locked, no reason field, no downgrade banner", await txt("#role-chip-text") === `Partner Admin: ${H.partner_a}` && await attr("#mfa", "aria-disabled") === "true" && !(await vis("#f-reason")) && !(await vis("#banner-downgrade"))
    && await ev(`document.body.dataset.partnerState`) === "partner-create" && await txt("#mfa-hint") === "Only a Super Admin can turn this off.");
  await type("#lname", "UI Cage 6"); await mouseClick("#m-entra"); await type("#dname", "UI Casino A"); await type("#tenant", H.t_free); await save();
  await until(`document.body.dataset.state === "saved"`);
  ok("partner-create own partner: created (Microsoft, backup off, MFA on)", await txt("#cur-method") === "Microsoft (Entra ID)" && await txt("#cur-fallback") === "Off" && await txt("#cur-mfa") === "Yes");
  await open(`/as/pa/orgs/${H.partner_b}/locations/new`);
  await type("#lname", "UI Cage X"); await type("#dname", "UI Casino B"); await save();
  const e = await shownError();
  ok("C3: Partner Admin creating under another partner (URL org) -> 404 not_found + request id", e.length === 1 && e[0].code === "not_found" && RID.test(e[0].rid) && await ev(`location.pathname.endsWith("/new")`), e);
  await open(`/as/rd/orgs/${H.partner_a}/locations/new`);
  await type("#lname", "UI Cage Y"); await type("#dname", "UI Casino A"); await save();
  const e2 = await shownError();
  ok("org:V reader create -> 403 forbidden + request id (F-A3)", e2.length === 1 && e2[0].code === "forbidden" && RID.test(e2[0].rid), e2);
});

await step("DEC-144 manual create recovery", async () => {
  const CREATE = /\/api\/orgs\/locations$/;
  const posts = () => bodies.filter(b => b.method === "POST" && CREATE.test(new URL(b.url).pathname)).length;
  const key = `llc.unconfirmed.create.${H.partner_a}`;
  const errText = async () => `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim();
  const A1 = "3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8";
  const A2 = "11111111-2222-4333-8444-555555555555";
  const rec = (id) => JSON.stringify({ attemptId: id, startedAt: "2026-11-06T00:00:00.000Z" });
  const ack = async (kind) => until(`document.body.dataset.createAck === ${JSON.stringify(kind)}`, 10000);
  await ev(`localStorage.clear(), true`);

  // GAP1: partner A has no reason-bounds row yet. Authority is bounds_editor for this partner, not the row.
  const viewOf = async (who) => {
    const r = await fetch(`${H.ui}/as/${who}/api/orgs/partners/${H.partner_a}/login-config/reason-bounds`);
    const b = r.status === 200 ? await r.json() : null;
    return { status: r.status, body: b, etag: r.headers.get("ETag") };
  };
  const vSa0 = await viewOf("sa"), vPa0 = await viewOf("pa"), vPb0 = await viewOf("pb");
  ok("GAP1 SA with no bounds row: 200 source default, bounds_editor true, no updated_by, no ETag",
    vSa0.status === 200 && vSa0.body?.source === "default" && vSa0.body?.bounds_editor === true && vSa0.body?.partner_id === H.partner_a && !("updated_by" in vSa0.body) && vSa0.etag === null, vSa0);
  ok("GAP1 own-partner Partner Admin with no bounds row: bounds_editor true, no updated_by",
    vPa0.status === 200 && vPa0.body?.source === "default" && vPa0.body?.bounds_editor === true && vPa0.body?.partner_id === H.partner_a && !("updated_by" in vPa0.body), vPa0);
  ok("GAP1 foreign Partner Admin with no bounds row: bounds_editor false, no updated_by",
    vPb0.status === 200 && vPb0.body?.source === "default" && vPb0.body?.bounds_editor === false && !("updated_by" in (vPb0.body ?? {})), vPb0);

  const lateSrc = `(() => {
    const real = globalThis.fetch.bind(globalThis);
    globalThis.__dec144Posts = 0;
    globalThis.fetch = async (input, init = {}) => {
      const url = String(input), method = (init && init.method) || "GET";
      if (method !== "POST" || !/\\/api\\/orgs\\/locations$/.test(url)) return real(input, init);
      globalThis.__dec144Posts++;
      const body = new ReadableStream({ start(c) {
        c.enqueue(new TextEncoder().encode('{"location_id":"'));
        setTimeout(() => { try { c.enqueue(new TextEncoder().encode('3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8"}')); c.close(); } catch { /* the abort already ended the stream */ } }, 4000);
      }});
      return new Response(body, { status: 201, headers: { "Content-Type": "application/json" } });
    };
  })();`;
  const gapIds = [];
  for (const src of [lateSrc, "globalThis.__llcWriteTimeoutMs = 1200;"]) gapIds.push((await cdp("Page.addScriptToEvaluateOnNewDocument", { source: src })).identifier);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
  await type("#lname", "UI Cage GAP1 late"); await type("#dname", "UI Casino A");
  const gapNet0 = posts();
  await click("#btn-save");
  await until(`document.body.dataset.state === "blocked"`, 10000);
  await sleep(4500);
  await ack("allow");
  const gGapLate = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"),
    posts: await ev(`globalThis.__dec144Posts`), net: posts() - gapNet0, key: await ev(`localStorage.getItem(${JSON.stringify(key)})`) };
  ok("GAP1 SA no-row: a late create stays blocked, shows recovery, and does not retry",
    gGapLate.state === "blocked" && !gGapLate.form && gGapLate.recov && gGapLate.posts === 1 && gGapLate.net === 0 && gGapLate.key !== null, gGapLate);
  for (const identifier of gapIds) await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });

  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP1 SA no-row: the guard and the controls survive a reload", await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) !== null);
  const gapKey = await ev(`localStorage.getItem(${JSON.stringify(key)})`);
  await open(`/as/anon/orgs/${H.partner_a}/locations/new`); await ack("session");
  ok("GAP1 SA no-row: re-auth as anon adds the sign-in sentence and does not clear the guard",
    await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await errText() === CREATE_UNKNOWN_SESSION_TEXT && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === gapKey);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gapN0 = posts();
  await click("#ack-missing"); await sleep(200);
  ok("GAP1 SA no-row: the checkbox alone sends no POST and does not clear the guard", posts() === gapN0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === gapKey && !(await vis("#form")));
  await ev(`document.querySelector("#ack-missing").checked = false, true`);
  await click("#btn-enable-create"); await sleep(300);
  ok("GAP1 SA no-row: Enable Create again without the checkbox sends no POST", posts() === gapN0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === gapKey && !(await vis("#form")));
  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A2))}), true`);
  await click("#ack-missing");
  await click("#btn-enable-create"); await sleep(400);
  const gGapOld = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), note: await txt("#create-recovery-note"), posts: posts() - gapN0 };
  ok("GAP1 SA no-row: an acknowledgement of the older attempt does not clear the newer one and sends no POST",
    gGapOld.state === "blocked" && !gGapOld.form && gGapOld.kept === rec(A2) && /not cleared/.test(gGapOld.note) && gGapOld.posts === 0, gGapOld);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gapN1 = posts();
  await click("#ack-missing");
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden`, 10000);
  ok("GAP1 SA no-row: Enable Create again clears only the matching attempt and sends zero POSTs",
    await ev(`document.body.dataset.state`) === "create" && await vis("#form") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === null && posts() === gapN1);
  await type("#lname", "UI Cage GAP1 SA"); await type("#dname", "UI Casino A");
  const gapN2 = posts();
  await click("#btn-save");
  await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  const gGapSa = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), posts: posts() - gapN2, name: await txt("#saved-loc") };
  if (gGapSa.saved) { p6cCreated++; p6cSaChanges++; }
  ok("GAP1 SA no-row: the next deliberate Create sends one POST and saves", gGapSa.state === "saved" && gGapSa.saved && gGapSa.posts === 1 && gGapSa.name === "UI Cage GAP1 SA", gGapSa);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gapN3 = posts();
  await click("#btn-found");
  await until(`document.body.dataset.state === "reported"`, 5000);
  const gGapFound = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"),
    title: await txt("#create-reported-h"), text: await txt("#create-reported-text"), links: await ev(`document.querySelectorAll("#create-reported a").length`),
    path: await ev(`location.pathname`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - gapN3 };
  ok("GAP1 SA no-row: I found the location is an admin report, sends no POST, and invents no destination",
    gGapFound.state === "reported" && !gGapFound.form && !gGapFound.recov && gGapFound.title === "You reported that you found the location."
      && gGapFound.text === "This is your report, not a confirmation from the server." && gGapFound.links === 0 && gGapFound.path.endsWith("/locations/new")
      && gGapFound.kept === null && gGapFound.posts === 0, gGapFound);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP1 own-partner Partner Admin no-row: recovery controls, guard kept", await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1) && !(await vis("#form")));
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP1 own-partner Partner Admin no-row: reload keeps the guard and the controls", await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1));
  await open(`/as/anon/orgs/${H.partner_a}/locations/new`); await ack("session");
  ok("GAP1 own-partner Partner Admin no-row: re-auth as anon does not clear the guard", await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1) && !(await vis("#create-recovery")));
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gapPn0 = posts();
  await click("#ack-missing"); await sleep(200);
  ok("GAP1 own-partner Partner Admin no-row: the checkbox alone sends no POST", posts() === gapPn0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1) && !(await vis("#form")));
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden`, 10000);
  ok("GAP1 own-partner Partner Admin no-row: Enable Create again sends zero POSTs and clears only the matching attempt",
    await ev(`document.body.dataset.state`) === "create" && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === null && posts() === gapPn0);
  await type("#lname", "UI Cage GAP1 PA"); await type("#dname", "UI Casino A");
  const gapPn1 = posts();
  await click("#btn-save");
  await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  const gGapPa = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), posts: posts() - gapPn1, name: await txt("#saved-loc") };
  if (gGapPa.saved) { p6cCreated++; gap1PaChanges++; }
  ok("GAP1 own-partner Partner Admin no-row: the next deliberate Create sends one POST and saves", gGapPa.state === "saved" && gGapPa.saved && gGapPa.posts === 1 && gGapPa.name === "UI Cage GAP1 PA", gGapPa);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gapPn2 = posts();
  await click("#btn-found");
  await until(`document.body.dataset.state === "reported"`, 5000);
  const gGapPaFound = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), links: await ev(`document.querySelectorAll("#create-reported a").length`),
    path: await ev(`location.pathname`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - gapPn2, title: await txt("#create-reported-h") };
  ok("GAP1 own-partner Partner Admin no-row: I found the location sends no POST and opens no new form",
    gGapPaFound.state === "reported" && !gGapPaFound.form && gGapPaFound.links === 0 && gGapPaFound.path.endsWith("/locations/new")
      && gGapPaFound.kept === null && gGapPaFound.posts === 0 && gGapPaFound.title === "You reported that you found the location.", gGapPaFound);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/pb/orgs/${H.partner_a}/locations/new`); await ack("unproven");
  ok("GAP1 foreign Partner Admin no-row: no recovery controls, guard kept", await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1));
  await open(`/as/rd/orgs/${H.partner_a}/locations/new`); await ack("deny");
  ok("GAP1 org:V reader no-row: denied, guard kept", await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1));

  let gapMode = null; const gapSeen = [];
  const gapLsn = (msg) => {
    if (msg.sessionId !== S || msg.method !== "Fetch.requestPaused") return;
    const p = msg.params, id = p.requestId, go = () => cdp("Fetch.continueRequest", { requestId: id }).catch(() => {});
    if (p.request.url.endsWith("/gap-redirect-target")) { gapSeen.push(p.request.url); return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: "text/html" }], body: Buffer.from("<!doctype html><title>Sign in</title>").toString("base64") }).catch(() => {}); }
    if (!gapMode || !/\/login-config\/reason-bounds/.test(p.request.url)) return go();
    if (gapMode.kind === "fail") return cdp("Fetch.failRequest", { requestId: id, errorReason: "Failed" }).catch(() => {});
    return cdp("Fetch.fulfillRequest", { requestId: id, responseCode: gapMode.status, responseHeaders: gapMode.headers,
      body: Buffer.from(gapMode.body).toString("base64") }).catch(() => {});
  };
  listeners.push(gapLsn);
  await cdp("Fetch.enable", { patterns: [{ urlPattern: "*/login-config/reason-bounds*", requestStage: "Request" }, { urlPattern: "*/gap-redirect-target", requestStage: "Request" }] });
  const gapJson = [{ name: "Content-Type", value: "application/json" }];
  const gapPlain = [{ name: "Content-Type", value: "text/plain" }];
  const gapCases = [
    ["missing bounds_editor", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "default", reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["bounds_editor string", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "default", bounds_editor: "true", reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["bounds_editor number", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "default", bounds_editor: 1, reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["bounds_editor false", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "default", bounds_editor: false, reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["updated_by without bounds_editor", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "partner", updated_by: A1, row_version: 1, updated_at: "2026-11-06T00:00:00.000000Z", reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["bounds_editor true for another partner", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_b, source: "default", bounds_editor: true, reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["non-json 200", { kind: "fake", status: 200, headers: gapPlain, body: "ok" }, "unknown"],
    ["500", { kind: "fake", status: 500, headers: gapJson, body: JSON.stringify({ error: "internal_error" }) }, "unknown"],
    ["network failure", { kind: "fail" }, "unknown"],
    ["reason-bounds 302 with Location", { kind: "fake", status: 302, headers: [{ name: "Location", value: `${H.ui}/gap-redirect-target` }], body: "" }, "unknown"],
    ["reason-bounds 307 with Location", { kind: "fake", status: 307, headers: [{ name: "Location", value: `${H.ui}/gap-redirect-target` }], body: "" }, "unknown"],
    ["reason-bounds 401", { kind: "fake", status: 401, headers: [], body: "" }, "session"],
  ];
  for (const [name, mode, kind] of gapCases) {
    await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
    const before = posts();
    gapSeen.length = 0; gapMode = mode;
    await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
    await ack(kind);
    gapMode = null;
    const got = { state: await ev(`document.body.dataset.state`), recov: await vis("#create-recovery"), form: await vis("#form"), ack: await ev(`document.body.dataset.createAck`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - before, text: `${await txt("#banner-error-title")} ${await txt("#banner-error-text")}`.trim(), followed: gapSeen.length };
    ok(`GAP1 untrusted role evidence (${name}) does not unlock and sends no POST`,
      got.state === "blocked" && !got.recov && !got.form && got.ack === kind && got.kept === rec(A1) && got.posts === 0 && got.followed === 0
      && got.text === (kind === "session" ? CREATE_UNKNOWN_SESSION_TEXT : CREATE_UNKNOWN_TEXT), got);
  }
  // GAP2: a pre-DEC-144 guard is the raw Date#toISOString string. Partner A still has no bounds row.
  // The timestamp is not authority. Reading it does not clear it and does not POST.
  const L1 = "2026-11-06T07:17:00.000Z";
  const L2 = "2026-11-06T07:28:00.000Z";
  const setKey = async (v) => ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(v)}), true`);
  await setKey(L1);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 SA no-row: a legacy timestamp stays locked on load, with recovery controls and the stored string unchanged",
    await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && !(await vis("#form"))
      && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 SA no-row: reload keeps the same legacy guard locked",
    await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && !(await vis("#form")));
  await open(`/as/anon/orgs/${H.partner_a}/locations/new`); await ack("session");
  ok("GAP2 SA no-row: re-auth as anon does not clear the legacy guard",
    await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const g2n0 = posts();
  await click("#ack-missing"); await sleep(200);
  ok("GAP2 SA no-row: the checkbox alone sends no POST and does not clear the legacy guard",
    posts() === g2n0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && !(await vis("#form")));
  await ev(`document.querySelector("#ack-missing").checked = false, true`);
  await click("#btn-enable-create"); await sleep(300);
  ok("GAP2 SA no-row: Enable Create again without the checkbox sends no POST",
    posts() === g2n0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && !(await vis("#form")));
  await setKey(L2);
  await click("#ack-missing");
  await click("#btn-enable-create"); await sleep(400);
  const g2leg = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), note: await txt("#create-recovery-note"), posts: posts() - g2n0 };
  ok("GAP2 SA no-row: acknowledging one legacy timestamp does not clear a different legacy timestamp and sends no POST",
    g2leg.state === "blocked" && !g2leg.form && g2leg.kept === L2 && /not cleared/.test(g2leg.note) && g2leg.posts === 0, g2leg);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  await setKey(rec(A2));
  const g2nObj = posts();
  await click("#ack-missing");
  await click("#btn-enable-create"); await sleep(400);
  const g2obj = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), note: await txt("#create-recovery-note"), posts: posts() - g2nObj };
  ok("GAP2 SA no-row: a legacy acknowledgement does not clear a newer id-scoped guard and sends no POST",
    g2obj.state === "blocked" && !g2obj.form && g2obj.kept === rec(A2) && /not cleared/.test(g2obj.note) && g2obj.posts === 0, g2obj);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  await setKey(rec(A1));
  const g2nId = posts();
  await click("#ack-missing");
  await click("#btn-enable-create"); await sleep(400);
  const g2id = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), note: await txt("#create-recovery-note"), posts: posts() - g2nId };
  ok("GAP2 SA no-row: an acknowledgement of an older attempt id does not clear a newer attempt id and sends no POST",
    g2id.state === "blocked" && !g2id.form && g2id.kept === rec(A1) && /not cleared/.test(g2id.note) && g2id.posts === 0, g2id);
  await setKey(L1);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const g2n1 = posts();
  await click("#ack-missing");
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden`, 10000);
  ok("GAP2 SA no-row: Enable Create again clears only the matching legacy timestamp, shows an empty form, and sends zero POSTs",
    await ev(`document.body.dataset.state`) === "create" && await vis("#form") && await ev(`document.querySelector("#lname").value`) === ""
      && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === null && posts() === g2n1);
  await type("#lname", "UI Cage GAP2 SA"); await type("#dname", "UI Casino A");
  const g2n2 = posts();
  await click("#btn-save");
  await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  const g2Sa = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), posts: posts() - g2n2, name: await txt("#saved-loc") };
  if (g2Sa.saved) { p6cCreated++; p6cSaChanges++; }
  ok("GAP2 SA no-row: the next deliberate Create after a legacy acknowledgement sends one POST and saves",
    g2Sa.state === "saved" && g2Sa.saved && g2Sa.posts === 1 && g2Sa.name === "UI Cage GAP2 SA", g2Sa);
  await setKey(L1);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const g2n3 = posts();
  await click("#btn-found");
  await until(`document.body.dataset.state === "reported"`, 5000);
  const g2Found = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"),
    title: await txt("#create-reported-h"), text: await txt("#create-reported-text"), links: await ev(`document.querySelectorAll("#create-reported a").length`),
    path: await ev(`location.pathname`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - g2n3 };
  ok("GAP2 SA no-row: I found the location clears only the matching legacy timestamp, sends no POST, and invents no destination",
    g2Found.state === "reported" && !g2Found.form && !g2Found.recov && g2Found.title === "You reported that you found the location."
      && g2Found.text === "This is your report, not a confirmation from the server." && g2Found.links === 0 && g2Found.path.endsWith("/locations/new")
      && g2Found.kept === null && g2Found.posts === 0, g2Found);

  await setKey(L1);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 own-partner Partner Admin no-row: legacy guard stays locked with recovery controls",
    await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && !(await vis("#form")));
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 own-partner Partner Admin no-row: reload keeps the legacy guard locked",
    await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  await open(`/as/anon/orgs/${H.partner_a}/locations/new`); await ack("session");
  ok("GAP2 own-partner Partner Admin no-row: re-auth as anon does not clear the legacy guard",
    !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const g2pn0 = posts();
  await click("#ack-missing"); await sleep(200);
  ok("GAP2 own-partner Partner Admin no-row: the checkbox alone sends no POST",
    posts() === g2pn0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && !(await vis("#form")));
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden`, 10000);
  ok("GAP2 own-partner Partner Admin no-row: Enable Create again sends zero POSTs and clears only the matching legacy timestamp",
    await ev(`document.body.dataset.state`) === "create" && await ev(`document.querySelector("#lname").value`) === "" && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === null && posts() === g2pn0);
  await type("#lname", "UI Cage GAP2 PA"); await type("#dname", "UI Casino A");
  const g2pn1 = posts();
  await click("#btn-save");
  await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  const g2Pa = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), posts: posts() - g2pn1, name: await txt("#saved-loc") };
  if (g2Pa.saved) { p6cCreated++; gap2PaChanges++; }
  ok("GAP2 own-partner Partner Admin no-row: the next deliberate Create sends one POST and saves",
    g2Pa.state === "saved" && g2Pa.saved && g2Pa.posts === 1 && g2Pa.name === "UI Cage GAP2 PA", g2Pa);
  await setKey(L1);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const g2pn2 = posts();
  await click("#btn-found");
  await until(`document.body.dataset.state === "reported"`, 5000);
  const g2PaFound = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), links: await ev(`document.querySelectorAll("#create-reported a").length`),
    path: await ev(`location.pathname`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - g2pn2, title: await txt("#create-reported-h") };
  ok("GAP2 own-partner Partner Admin no-row: I found the location sends no POST and opens no new form",
    g2PaFound.state === "reported" && !g2PaFound.form && g2PaFound.links === 0 && g2PaFound.path.endsWith("/locations/new")
      && g2PaFound.kept === null && g2PaFound.posts === 0 && g2PaFound.title === "You reported that you found the location.", g2PaFound);

  await setKey(L1);
  await open(`/as/pb/orgs/${H.partner_a}/locations/new`); await ack("unproven");
  ok("GAP2 foreign Partner Admin no-row: a legacy timestamp does not unlock, and the guard is kept",
    await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  await open(`/as/rd/orgs/${H.partner_a}/locations/new`); await ack("deny");
  ok("GAP2 org:V reader no-row: a legacy timestamp stays denied, and the guard is kept",
    await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1);
  const gap2Bad = [
    ["malformed text", "nope"],
    ["ISO without milliseconds", "2026-11-06T07:17:00Z"],
    ["ISO with a numeric offset", "2026-11-06T07:17:00.000+00:00"],
    ["impossible calendar date", "2026-02-31T00:00:00.000Z"],
    ["JSON-encoded timestamp", JSON.stringify(L1)],
    ["empty string", ""],
  ];
  for (const [name, value] of gap2Bad) {
    await setKey(value);
    const before = posts();
    await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
    await ack("allow");
    const got = { state: await ev(`document.body.dataset.state`), recov: await vis("#create-recovery"), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - before };
    ok(`GAP2 malformed legacy value (${name}) stays blocked, hides recovery, and sends no POST`,
      got.state === "blocked" && !got.recov && !got.form && got.kept === value && got.posts === 0, got);
  }
  for (const [name, mode, kind] of [
    ["legacy with bounds_editor false", { kind: "fake", status: 200, headers: gapJson, body: JSON.stringify({ partner_id: H.partner_a, source: "default", bounds_editor: false, reason_min_length: 10, reason_max_length: 500 }) }, "unproven"],
    ["legacy with a failed role read", { kind: "fail" }, "unknown"],
  ]) {
    await setKey(L1);
    const before = posts();
    gapMode = mode;
    await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
    await ack(kind);
    gapMode = null;
    const got = { state: await ev(`document.body.dataset.state`), recov: await vis("#create-recovery"), form: await vis("#form"), ack: await ev(`document.body.dataset.createAck`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - before };
    ok(`GAP2 untrusted role evidence (${name}) does not unlock a legacy guard and sends no POST`,
      got.state === "blocked" && !got.recov && !got.form && got.ack === kind && got.kept === L1 && got.posts === 0, got);
  }
  await cdp("Fetch.disable"); listeners.splice(listeners.indexOf(gapLsn), 1);

  await setKey(L1);
  const unreadSrc = `(() => { const real = Storage.prototype.getItem; Storage.prototype.getItem = function (k) { if (typeof k === "string" && k.indexOf("llc.unconfirmed.create.") === 0) throw new DOMException("SecurityError"); return real.call(this, k); }; })();`;
  const unreadId = (await cdp("Page.addScriptToEvaluateOnNewDocument", { source: unreadSrc })).identifier;
  const g2nU = posts();
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 storage read error stays blocked, hides recovery, and sends no POST",
    await ev(`document.body.dataset.state`) === "blocked" && !(await vis("#create-recovery")) && !(await vis("#form")) && posts() === g2nU);
  await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier: unreadId });
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("GAP2 storage read error did not delete the legacy guard",
    await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === L1 && await ev(`document.body.dataset.state`) === "blocked");
  await ev(`localStorage.clear(), true`);

  const put = await fetch(`${H.ui}/as/sa/api/orgs/partners/${H.partner_a}/login-config/reason-bounds`, { method: "PUT", headers: { "Content-Type": "application/json", "If-Match": '"0"' },
    body: JSON.stringify({ reason_min_length: 10, reason_max_length: 500 }) });
  const putBody = put.status === 200 ? await put.json() : null;
  ok("DEC-144 setup: SA stores partner A reason-bounds; a saved row still reports bounds_editor and updated_by (If-Match 0 -> 200)", put.status === 200 && putBody?.bounds_editor === true && typeof putBody?.updated_by === "string", put.status);
  const vSa1 = await viewOf("sa"), vPa1 = await viewOf("pa"), vPb1 = await viewOf("pb");
  ok("GAP1 saved-bounds Super Admin and own-partner Partner Admin stay editors; the foreign Partner Admin does not",
    vSa1.body?.bounds_editor === true && typeof vSa1.body?.updated_by === "string" && vSa1.body?.source === "partner"
      && vPa1.body?.bounds_editor === true && typeof vPa1.body?.updated_by === "string"
      && vPb1.body?.bounds_editor === false && !("updated_by" in (vPb1.body ?? {})), { vSa1, vPa1, vPb1 });

  const late = `(() => {
    const real = globalThis.fetch.bind(globalThis);
    globalThis.__dec144Posts = 0;
    globalThis.fetch = async (input, init = {}) => {
      const url = String(input), method = (init && init.method) || "GET";
      if (method !== "POST" || !/\\/api\\/orgs\\/locations$/.test(url)) return real(input, init);
      globalThis.__dec144Posts++;
      const body = new ReadableStream({ start(c) {
        c.enqueue(new TextEncoder().encode('{"location_id":"'));
        setTimeout(() => { try { c.enqueue(new TextEncoder().encode('3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8"}')); c.close(); } catch { /* the abort already ended the stream */ } }, 4000);
      }});
      return new Response(body, { status: 201, headers: { "Content-Type": "application/json" } });
    };
  })();`;
  const ids = [];
  for (const src of [late, "globalThis.__llcWriteTimeoutMs = 1200;"]) ids.push((await cdp("Page.addScriptToEvaluateOnNewDocument", { source: src })).identifier);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`);
  await type("#lname", "UI Cage DEC144 late"); await type("#dname", "UI Casino A");
  const p0 = posts();
  await click("#btn-save");
  await until(`document.body.dataset.state === "blocked"`, 10000);
  await sleep(4500);
  await ack("allow");
  const gLate = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), path: await ev(`location.pathname`),
    posts: await ev(`globalThis.__dec144Posts`), net: posts() - p0, key: await ev(`localStorage.getItem(${JSON.stringify(key)})`), text: await errText() };
  ok("DEC-144 late 201 after the bound never retries, never navigates, and leaves the guard",
    gLate.state === "blocked" && !gLate.form && gLate.path.endsWith("/locations/new") && gLate.posts === 1 && gLate.net === 0 && gLate.key !== null && gLate.text === CREATE_UNKNOWN_TEXT, gLate);
  for (const identifier of ids) await cdp("Page.removeScriptToEvaluateOnNewDocument", { identifier });
  await ev(`localStorage.clear(), true`);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const gSa = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"),
    box: await txt("label[for=ack-missing]"), enable: await txt("#btn-enable-create"), found: await txt("#btn-found"), text: await errText() };
  ok("DEC-144 SA with a bounds row: blocked, recovery controls, exact checkbox and buttons",
    gSa.state === "blocked" && !gSa.form && gSa.recov && gSa.box === "I checked and couldn\u2019t find the location." && gSa.enable === "Enable Create again" && gSa.found === "I found the location" && gSa.text === CREATE_UNKNOWN_TEXT, gSa);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("DEC-144 reload keeps the same guard and the controls", await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1));
  await open(`/as/anon/orgs/${H.partner_a}/locations/new`); await ack("session");
  const gAnon = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"), text: await errText(), key: await ev(`localStorage.getItem(${JSON.stringify(key)})`) };
  ok("DEC-144 re-auth as anon: 401 adds the sign-in sentence, does not clear the guard, no controls",
    gAnon.state === "blocked" && !gAnon.form && !gAnon.recov && gAnon.text === CREATE_UNKNOWN_SESSION_TEXT && gAnon.key === rec(A1), gAnon);
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  ok("DEC-144 own-partner Partner Admin may see the controls; re-auth did not clear the guard",
    await ev(`document.body.dataset.state`) === "blocked" && await vis("#create-recovery") && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1));
  await open(`/as/pb/orgs/${H.partner_a}/locations/new`); await ack("unproven");
  const gPb = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"), key: await ev(`localStorage.getItem(${JSON.stringify(key)})`) };
  ok("DEC-144 other-partner Partner Admin cannot acknowledge (no updated_by)", gPb.state === "blocked" && !gPb.form && !gPb.recov && gPb.key === rec(A1), gPb);
  await open(`/as/rd/orgs/${H.partner_a}/locations/new`); await ack("deny");
  const gRd = { state: await ev(`document.body.dataset.state`), recov: await vis("#create-recovery"), key: await ev(`localStorage.getItem(${JSON.stringify(key)})`) };
  ok("DEC-144 org:V reader cannot acknowledge", gRd.state === "blocked" && !gRd.recov && gRd.key === rec(A1), gRd);

  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const n0 = posts();
  await click("#ack-missing"); await sleep(200);
  ok("DEC-144 checkbox alone sends no POST and does not clear the guard", posts() === n0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1) && await ev(`document.body.dataset.state`) === "blocked");
  await ev(`document.querySelector("#ack-missing").checked = false, true`);
  await click("#btn-enable-create"); await sleep(300);
  ok("DEC-144 Enable Create again without the checkbox sends no POST and does not clear the guard", posts() === n0 && await ev(`localStorage.getItem(${JSON.stringify(key)})`) === rec(A1) && !(await vis("#form")));

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A2))}), true`);
  await click("#ack-missing");
  await click("#btn-enable-create"); await sleep(400);
  const gOld = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), note: await txt("#create-recovery-note"), posts: posts() - n0 };
  ok("DEC-144 an acknowledgement of the older attempt does not clear the newer one and sends no POST",
    gOld.state === "blocked" && !gOld.form && gOld.kept === rec(A2) && /not cleared/.test(gOld.note) && gOld.posts === 0, gOld);

  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const n1 = posts();
  await click("#ack-missing");
  await click("#btn-enable-create");
  await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden`, 10000);
  const gEn = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - n1, btn: await txt("#btn-save") };
  ok("DEC-144 Enable Create again clears only the matching attempt, shows the form, and sends zero POSTs",
    gEn.state === "create" && gEn.form && gEn.kept === null && gEn.posts === 0 && gEn.btn === "Create location", gEn);
  await type("#lname", "UI Cage DEC144"); await type("#dname", "UI Casino A");
  const n2 = posts();
  await click("#btn-save");
  await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  const gCr = { state: await ev(`document.body.dataset.state`), saved: await vis("#banner-saved"), posts: posts() - n2, name: await txt("#saved-loc") };
  if (gCr.saved) { p6cCreated++; p6cSaChanges++; }
  ok("DEC-144 the next deliberate Create sends one POST and saves", gCr.state === "saved" && gCr.saved && gCr.posts === 1 && gCr.name === "UI Cage DEC144", gCr);

  await ev(`localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(rec(A1))}), true`);
  await open(`/as/sa/orgs/${H.partner_a}/locations/new`); await ack("allow");
  const n3 = posts();
  await click("#btn-found");
  await until(`document.body.dataset.state === "reported"`, 5000);
  const gFound = { state: await ev(`document.body.dataset.state`), form: await vis("#form"), recov: await vis("#create-recovery"),
    title: await txt("#create-reported-h"), text: await txt("#create-reported-text"), links: await ev(`document.querySelectorAll("#create-reported a").length`),
    path: await ev(`location.pathname`), kept: await ev(`localStorage.getItem(${JSON.stringify(key)})`), posts: posts() - n3 };
  ok("DEC-144 I found the location closes only this attempt, sends no POST, and invents no location link",
    gFound.state === "reported" && !gFound.form && !gFound.recov && gFound.title === "You reported that you found the location."
      && gFound.text === "This is your report, not a confirmation from the server." && gFound.links === 0 && gFound.path.endsWith("/locations/new")
      && gFound.kept === null && gFound.posts === 0, gFound);
  await ev(`localStorage.clear(), true`);
});
await step("XSS hostile API text", async () => {
  // SSE2 1ec73d38 (live probe of the code-read XSS PASS) + TLD 2026-11-04 14:18 (A). UiHost seeded L5 through the API with the payloads in
  // the location name, display_name and the create reason; the probes put them in fields the API must refuse.
  const X = H.xss, loc = H.l5;
  console.log("INFO xss probes " + JSON.stringify(X.probes));
  // SSE2 5842c444 L4: each refusal names the exact field and code (observed in p4f), not just "some field".
  const probeWant = [["tenant id = javascript: URL", "login_config/entra_allowed_tenant_ids/0:tenant_id_format"],
    ["tenant id = <img onerror>", "login_config/entra_allowed_tenant_ids/0:tenant_id_format"], ["partner_id = <svg onload>", "partner_id:partner_id_format"],
    ["oidc_authority = javascript: URL (only link-like field)", "login_config/oidc_authority:field_not_writable"], ["provider_kind = <script>", "login_config/provider_kind:provider_kind_invalid"]];
  ok("XSS probes: the API refuses the payloads in UUID / enum / server-only fields: 422 with exactly the expected field:code each, validation unchanged",
    X.probes.length === probeWant.length && probeWant.every(([label, fc], i) => X.probes[i].probe === label && X.probes[i].status === 422
      && JSON.stringify(X.probes[i].fields) === JSON.stringify([fc])), X.probes);
  console.log("INFO xss len probes " + JSON.stringify(X.len.map(p => ({ probe: p.probe, status: p.status, fields: p.fields, code: p.code, body: p.body }))));
  // A clean page of the same kind is the baseline for img/svg/script and attribute counts.
  const dom = () => ev(`(() => { const all = [...document.querySelectorAll("*")];
    return { img: document.querySelectorAll("img").length, svg: document.querySelectorAll("svg").length, script: document.querySelectorAll("script").length,
      iframe: document.querySelectorAll("iframe,object,embed").length,
      on: all.filter(e => [...e.attributes].some(a => /^on/i.test(a.name))).map(e => e.tagName),
      js: all.filter(e => [...e.attributes].some(a => /^\\s*javascript:/i.test(a.value))).map(e => e.tagName + " " + [...e.attributes].map(a => a.name).join(",")),
      attrX: all.filter(e => [...e.attributes].some(a => /__xss/.test(a.name + "=" + a.value))).map(e => e.tagName + " " + [...e.attributes].map(a => a.name).join(",")),
      xss: typeof window.__xss, anchors: document.querySelectorAll("a").length }; })()`);
  await open(page("sa", H.l4)); const base = await dom();
  ok("XSS baseline (clean SA view): 0 img/svg/iframe, 1 script (the module), no on* attribute, no javascript: value", base.img === 0 && base.svg === 0 && base.script === 1
    && base.iframe === 0 && base.on.length === 0 && base.js.length === 0 && base.attrX.length === 0 && base.xss === "undefined", base);
  const clean = async (label) => { await sleep(300); const d = await dom();
    // SSE2 5842c444 L2: attrX = any attribute whose name or value carries a payload (href, title, aria-label, src ...), so a name
    // moved into an attribute fails here and on the javascript: scan, not only on the anchor count.
    ok(`XSS ${label}: window.__xss undefined, no extra img/svg/script/iframe/a, no on* attribute, no javascript: attribute, no payload in any attribute`, d.xss === "undefined" && d.img === 0 && d.svg === 0
      && d.script === 1 && d.iframe === 0 && d.on.length === 0 && d.js.length === 0 && d.attrX.length === 0 && d.anchors === base.anchors, d); };
  const p0 = problems.length;
  const lit = async (label, name = X.name) => {
    ok(`XSS ${label}: name literal in title, breadcrumb and document.title (textContent, exact)`, await ev(`document.querySelector("#title").textContent`) === `Sign-in method: ${name}`
      && await ev(`document.querySelector("#bc-location").textContent`) === name && await ev(`document.title`) === `Sign-in method: ${name}`,
      [await ev(`document.querySelector("#title").textContent`), await ev(`document.querySelector("#bc-location").textContent`), await ev(`document.title`)]);
  };
  await open(page("sa", loc));
  await lit("SA view");
  let r = await rows();
  ok("XSS SA view: history reason cell = the payload text, literally; Who = SA actor id", r.length === 1 && r[0][2] === H.users.sa && r[0][3].endsWith(`. Reason: ${X.reason}`), r);
  await clean("SA view");
  await click("#btn-change");
  ok("XSS SA edit: title literal, display name input value literal", await ev(`document.querySelector("#title").textContent`) === `Edit sign-in method: ${X.name}`
    && await ev(`document.querySelector("#dname").value`) === X.display_name);
  await clean("SA edit");
  await open(page("pa", loc));
  await lit("PA view");
  ok("XSS PA view: history reason literal", (await rows())[0]?.[3].endsWith(`. Reason: ${X.reason}`), await rows());
  await clean("PA view");
  await click("#btn-change");
  ok("XSS PA edit: display name input value literal", await ev(`document.querySelector("#dname").value`) === X.display_name);
  const js = "javascript:window.__xss=1";
  await type("#dname", js); await save();
  ok("XSS PA save (display name -> javascript: URL): saved banner shows the name literally, history row 2 by the PA", await vis("#banner-saved")
    && await ev(`document.querySelector("#saved-loc").textContent`) === X.name && (await rows()).length === 2 && (await rows())[0][2] === H.users.pa, [await txt("#banner-saved"), await rows()]);
  await clean("PA saved");
  await open(page("rd", loc));
  ok("XSS org:V view: view-only chip, name literal, history hidden", await txt("#role-chip-text") === "View only (org:V)" && await ev(`document.querySelector("#bc-location").textContent`) === X.name
    && await ev(`document.title`) === `Sign-in method: ${X.name}` && !(await vis("#card-history")));
  await clean("org:V view");
  // ---- L6 (SSE2 5842c444 L1/L2/L3): the name starts with javascript:, the display_name is hostile and never overwritten, and all three
  // free-text fields carry typed HTML entities that must be shown as typed (not decoded, not double-shown).
  const L6 = H.xss.l6, n6 = X.name6, d6 = X.display_name6, r6 = X.reason6;
  ok("XSS L6 seed: name starts with javascript:, every field carries a typed entity", n6.startsWith("javascript:") && [n6, d6, r6].every(t => /&(lt|amp|#60);/.test(t)));
  for (const persona of ["sa", "pa"]) {
    const who = persona.toUpperCase();
    await open(page(persona, L6));
    await lit(`L6 ${who} view`, n6);
    const r6rows = await rows();
    ok(`XSS L6 ${who} view: history reason cell = the hostile reason with typed entities, literally (one row, SA)`, r6rows.length === 1 && r6rows[0][2] === H.users.sa
      && r6rows[0][3].endsWith(`. Reason: ${r6}`), r6rows);
    await clean(`L6 ${who} view`);
    await click("#btn-change");
    // L1: byte-for-byte, before any save could overwrite it (nobody saves L6).
    ok(`XSS L6 ${who} edit: #dname value = the hostile display_name exactly; #lname value = the name exactly`, await ev(`document.querySelector("#dname").value`) === d6
      && await ev(`document.querySelector("#lname").value`) === n6, [await ev(`document.querySelector("#dname").value`), await ev(`document.querySelector("#lname").value`)]);
    await clean(`L6 ${who} edit`);
  }
  await open(page("rd", L6));
  await lit("L6 org:V view", n6);
  await clean("L6 org:V view");
  ok("XSS: no CSP violation, exception or console error/warning on the hostile pages", problems.length === p0, problems.slice(p0));
  H.xssPaJs = js;
});

// ---------- DEC-145/146 client and location forms ----------
await step("DEC-145 client and location forms", async () => {
  await ev(`localStorage.clear(), true`);
  const partner = H.partner_a;
  const openCreate = async (path) => {
    await open(path);
    await until(`document.body.dataset.state === "create" && !document.querySelector("#form").hidden && document.querySelectorAll("#country option").length > 50`, 10000);
  };
  const setCountry = async (code) => ev(`(() => { const c = document.querySelector("#country"); c.value = ${JSON.stringify(code)}; c.dispatchEvent(new Event("change", { bubbles: true })); return c.value; })()`);
  const fieldText = async (id) => txt(`#${id}`);
  await openCreate(`/as/sa/orgs/${partner}/clients/new`);
  const opened = {
    countries: await ev(`document.querySelectorAll("#country option").length`),
    zones: await ev(`document.querySelectorAll("#tz option").length`),
    tz: await ev(`document.querySelector("#tz").value`),
    country: await ev(`document.querySelector("#country").value`),
    label: await txt("#lname-kind"),
    save: await txt("#btn-save"),
    parent: await vis("#f-parent"),
    method: await vis("#f-method"),
  };
  ok("DEC-145 client form: ref dropdowns, blank country and time zone, no parent or sign-in fields",
    opened.countries > 50 && opened.zones > 50 && opened.tz === "" && opened.country === "" && opened.label === "Client name" && opened.save === "Create client" && !opened.parent && !opened.method, opened);
  await type("#lname", "Forms Client US");
  await type("#address1", "1 Market Street");
  await type("#city", "San Francisco");
  await setCountry("US");
  await type("#postal", "94105");
  await type("#phone", "+16502530000");
  await ev(`document.querySelector("#tz").value = "America/Los_Angeles", true`);
  const usedBox = { hidden: !(await vis("#f-region-unused")), checked: await ev(`document.querySelector("#region-unused").checked`) };
  ok("DEC-145 client: a known-used region hides the confirmation and does not check it", usedBox.hidden && usedBox.checked === false, usedBox);
  await clickDom("#btn-save");
  await until(`!document.querySelector("#region-err").hidden`, 10000);
  ok("DEC-145 client: blank known-used region shows the field error and does not save",
    await fieldText("region-err") === "Fill in this field." && await ev(`document.body.dataset.state`) === "create" && !(await vis("#banner-client-saved")));
  await ev(`document.querySelector("#region-unused").checked = true, true`);
  await ev(`document.querySelector("#region-err").textContent = ""`);
  await clickDom("#btn-save");
  await until(`document.querySelector("#region-err").textContent.includes("Fill in this field.")`, 10000);
  ok("DEC-145 client: checking the hidden known-used box does not waive the region",
    await fieldText("region-err") === "Fill in this field." && await ev(`document.body.dataset.state`) === "create");
  await type("#region", "CA");
  await clickDom("#btn-save");
  await until(`document.querySelector("#btn-save").getAttribute("aria-disabled") !== "true"`, 10000);
  await until(`document.body.dataset.state === "saved" && !document.querySelector("#banner-client-saved").hidden`, 10000);
  ok("DEC-145 client: US with region, postal, phone, and an explicit zone saves",
    await txt("#saved-client") === "Forms Client US" && await ev(`document.body.dataset.state`) === "saved");

  await openCreate(`/as/sa/orgs/${partner}/clients/new`);
  await type("#lname", "Forms Client AG");
  await type("#address1", "1 Independence Avenue");
  await type("#city", "St John's");
  await setCountry("AG");
  await type("#phone", "+12684601234");
  await ev(`document.querySelector("#tz").value = "America/St_Johns", true`);
  const ag = {
    regionBox: await vis("#f-region-unused"),
    postalBox: await vis("#f-postal-unused"),
    regionFor: await ev(`document.querySelector("label[for=region-unused]") !== null`),
    postalFor: await ev(`document.querySelector("label[for=postal-unused]") !== null`),
    regionChecked: await ev(`document.querySelector("#region-unused").checked`),
  };
  ok("DEC-145 client: unknown region and postal each show their own confirmation, unchecked",
    ag.regionBox && ag.postalBox && ag.regionFor && ag.postalFor && ag.regionChecked === false, ag);
  await clickDom("#btn-save");
  await until(`!document.querySelector("#region-unused-err").hidden && !document.querySelector("#postal-unused-err").hidden`, 10000);
  ok("DEC-145 client: unknown blanks ask for each confirmation",
    await fieldText("region-unused-err") === "Confirm that this field is not used for the selected country, or enter a value."
      && await fieldText("postal-unused-err") === "Confirm that this field is not used for the selected country, or enter a value.");
  await click("#region-unused");
  await clickDom("#btn-save");
  await until(`document.querySelector("#region-unused-err").hidden && !document.querySelector("#postal-unused-err").hidden`, 10000);
  ok("DEC-145 client: confirming region leaves the postal confirmation required",
    await fieldText("postal-unused-err") === "Confirm that this field is not used for the selected country, or enter a value." && await ev(`document.querySelector("#region-unused-err").hidden`));
  await setCountry("US");
  const afterChange = {
    regionChecked: await ev(`document.querySelector("#region-unused").checked`),
    postalChecked: await ev(`document.querySelector("#postal-unused").checked`),
    regionBox: await vis("#f-region-unused"),
    tz: await ev(`document.querySelector("#tz").value`),
    phone: await ev(`document.querySelector("#phone").value`),
  };
  ok("DEC-145 client: changing country clears both confirmations and keeps the phone and time zone",
    afterChange.regionChecked === false && afterChange.postalChecked === false && !afterChange.regionBox && afterChange.tz === "America/St_Johns" && afterChange.phone === "+12684601234", afterChange);
  await setCountry("AG");
  await type("#region", "St John's");
  await click("#postal-unused");
  await clickDom("#btn-save");
  await until(`document.body.dataset.state === "saved"`, 10000);
  ok("DEC-145 client: a filled unknown region needs no confirmation and a confirmed blank postal saves",
    await txt("#saved-client") === "Forms Client AG");

  await openCreate(`/as/sa/orgs/${partner}/clients/new`);
  await type("#lname", "Forms Client DE");
  await type("#address1", "1 Unter den Linden");
  await type("#city", "Berlin");
  await setCountry("DE");
  await type("#phone", "+4930123456");
  await ev(`document.querySelector("#tz").value = "Europe/Berlin", true`);
  const de = { region: await ev(`document.querySelector("#region").readOnly`), box: await vis("#f-region-unused"), hint: await txt("#region-hint") };
  ok("DEC-145 client: a known-unused region is blank, read-only, and has no confirmation",
    de.region && !de.box && de.hint === "Not used for this country.", de);
  await type("#postal", "10115");
  await clickDom("#btn-save");
  await until(`document.body.dataset.state === "saved"`, 10000);
  ok("DEC-145 client: Germany with a postal code and a blank region saves", await txt("#saved-client") === "Forms Client DE");

  const tamper = await fetch(`${H.ui}/as/sa/api/orgs/partners/${partner}/clients`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
    partner_id: partner, name: "Tampered waiver", address1: "1 Market Street", city: "San Francisco", region: "   ", country_code: "US", postal_code: "94105",
    region_unused_confirmed: true, postal_unused_confirmed: false, phone: "+16502530000", time_zone_id: "America/Los_Angeles" }) });
  const tamperBody = await tamper.json();
  const tamperFields = (tamperBody.fields || []).map(f => `${f.field}:${f.code}`);
  ok("DEC-145 server: whitespace region plus a known-used confirmation is still required",
    tamper.status === 422 && tamperFields.includes("region:required") && tamperFields.includes("region_unused_confirmed:confirmation_not_applicable"), tamperFields);
  const cross = await fetch(`${H.ui}/as/pa/api/orgs/partners/${H.partner_b}/clients`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
    partner_id: H.partner_b, name: "Cross", address1: "1 Market Street", city: "San Francisco", region: "CA", country_code: "US", postal_code: "94105",
    region_unused_confirmed: false, postal_unused_confirmed: false, phone: "+16502530000", time_zone_id: "America/Los_Angeles" }) });
  ok("DEC-145 Partner Admin cannot create a client under another partner", cross.status === 404, cross.status);
  const reader = await fetch(`${H.ui}/as/rd/api/orgs/partners/${partner}/clients`, { method: "POST", headers: { "Content-Type": "application/json" }, body: "{}" });
  ok("DEC-145 org:V cannot create a client", reader.status === 403, reader.status);

  await openCreate(`/as/sa/orgs/${partner}/locations/new`);
  const locOpen = { tz: await ev(`document.querySelector("#tz").value`), parent: await vis("#f-parent"), label: await txt("#lname-kind"), save: await txt("#btn-save") };
  ok("DEC-145 location form: time zone starts blank and the client list is a separate choice",
    locOpen.tz === "" && locOpen.parent && locOpen.label === "Location name" && locOpen.save === "Create location", locOpen);
  await ev(`(() => { const s = document.querySelector("#parent"); const o = [...s.options].find(x => x.textContent === "Forms Client US"); if (o) s.value = o.value; return s.value; })()`);
  const clientId = await ev(`document.querySelector("#parent").value`);
  await type("#phone", "+16502530001");
  await type("#email", "desk@example.test");
  await ev(`document.querySelector("#tz").value = "America/Chicago", true`);
  await setCountry("AG");
  await click("#region-unused");
  await click("#postal-unused");
  await clickDom("#btn-pull-address");
  await until(`document.querySelector("#address1").value === "1 Market Street"`, 10000);
  const pulled = {
    address1: await ev(`document.querySelector("#address1").value`),
    city: await ev(`document.querySelector("#city").value`),
    region: await ev(`document.querySelector("#region").value`),
    country: await ev(`document.querySelector("#country").value`),
    postal: await ev(`document.querySelector("#postal").value`),
    phone: await ev(`document.querySelector("#phone").value`),
    email: await ev(`document.querySelector("#email").value`),
    tz: await ev(`document.querySelector("#tz").value`),
    regionChecked: await ev(`document.querySelector("#region-unused").checked`),
    postalChecked: await ev(`document.querySelector("#postal-unused").checked`),
    regionBox: await vis("#f-region-unused"),
  };
  ok("DEC-145 location: pull copies the address only, clears confirmations, and keeps phone, email, and time zone",
    pulled.address1 === "1 Market Street" && pulled.city === "San Francisco" && pulled.region === "CA" && pulled.country === "US" && pulled.postal === "94105"
      && pulled.phone === "+16502530001" && pulled.email === "desk@example.test" && pulled.tz === "America/Chicago"
      && pulled.regionChecked === false && pulled.postalChecked === false && !pulled.regionBox, pulled);
  await type("#address1", "9 Edited Street");
  await type("#lname", "Forms Location");
  await type("#dname", "Forms Casino");
  await clickDom("#btn-save");
  const locationSaved = await until(`/\\/locations\\/[0-9a-f-]{36}\\/sign-in$/.test(location.pathname) && document.body.dataset.state === "saved"`, 10000);
  if (locationSaved) { p6cCreated++; p6cSaChanges++; }
  const pre = await fetch(`${H.ui}/as/sa/api/orgs/partners/${partner}/clients/${clientId}`);
  const preBody = pre.status === 200 ? await pre.json() : {};
  const preKeys = Object.keys(preBody).sort().join(",");
  ok("DEC-145 location save does not change the client, and the address read has no phone, email, or time zone",
    pre.status === 200 && preBody.address1 === "1 Market Street" && preKeys === "address1,address2,city,client_id,country_code,name,partner_id,postal_code,region", preKeys);
  const other = await fetch(`${H.ui}/as/pb/api/orgs/partners/${H.partner_b}/clients/${clientId}`);
  ok("DEC-145 another partner cannot read the client address", other.status === 404, other.status);
  const badPhone = await fetch(`${H.ui}/as/sa/api/orgs/locations`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
    partner_id: partner, name: "Bad phone", client_id: clientId, address1: "1 Market Street", city: "San Francisco", region: "CA", country_code: "US", postal_code: "94105",
    region_unused_confirmed: false, postal_unused_confirmed: false, phone: "not-a-phone", time_zone_id: "America/Los_Angeles",
    login_config: { provider_kind: "database", display_name: "Forms Casino", entra_allowed_tenant_ids: [], allow_db_fallback: false, mfa_required: true } }) });
  const badBody = await badPhone.json();
  ok("DEC-145 invalid phone is 422 and returns no location id",
    badPhone.status === 422 && (badBody.fields || []).some(f => f.field === "phone" && f.code === "phone_invalid") && !badBody.location_id, badPhone.status);
});

// ---------- global ----------
ok("no CSP violation, script exception or console error/warning on any page", problems.length === 0, problems);
ok("no request left 127.0.0.1 (ui host only)", outside.length === 0, outside);
ok("every write body: no oidc_*, secret_ref or secret key", bodies.length >= 8 && bodies.every(b => !/oidc_|secret/.test(b.body)), bodies.map(b => b.body));
{
  const badPuts = bodies.filter(b => b.method === "PUT" && !/^"\d+"$/.test(b.ifMatch ?? "")).map(b => ({ url: b.url, ifMatch: b.ifMatch ?? null, body: b.body.slice(0, 180) }));
  ok("every PUT carried a strong If-Match", badPuts.length === 0, badPuts);
}

// ---------- real PG after the run ----------
host.stdin.end();
const dbm = await waitFor(/^UIHOST-DB (\{.*\})$/m, 30000);
const D = dbm ? JSON.parse(dbm[1]) : null;
ok("real PG: the host reported its DB summary", D !== null);
if (D) {
  ok("real PG: org:V reader wrote no change row (F-A3)", !(H.users.rd in D.changes_by_actor), D.changes_by_actor);
  const lenOk = (H.xss.len ?? []).filter(p => p.status === 201).length;
  ok(`real PG: change rows = 6 seeds (L1-L4 + hostile L5, L6) + ${lenOk} accepted length probes + PA edit + SA downgrade + PA conflict PUT + SA create + PA create + PA hostile save + PA MFA back on (DEC-106)`,
    D.changes_by_actor[H.users.sa] === 8 + lenOk + p6cSaChanges && D.changes_by_actor[H.users.pa] === 5 + gap1PaChanges + gap2PaChanges, { D: D.changes_by_actor, p6cSaChanges, gap1PaChanges, gap2PaChanges }); // + P6c: lost PUT, lost create, confirmed PUT [TLD 15:08]
  ok(`real PG: ${8 + lenOk + p6cCreated} locations (refused creates and XSS probes added none; ${lenOk} accepted length probes; ${p6cCreated} later creates: the lost P6c create, the P6d saved landing, the DEC-144 deliberate create, the GAP1 no-row creates, and the GAP2 legacy acknowledgements)`, D.locations === 8 + lenOk + p6cCreated, D.locations);
  // TLD 2026-11-04 14:18 (A): stored byte-for-byte, no server-side HTML escaping; the page is the only place the text is made safe.
  const hex = (s) => Buffer.from(s, "utf8").toString("hex"), x = D.xss ?? {};
  ok("real PG (A): auth.location.name = the hostile name, byte-for-byte (UTF-8)", JSON.stringify(x.location_name_hex) === JSON.stringify([hex(H.xss.name)]), x.location_name_hex);
  ok("real PG (A): display_name = the javascript: text the Partner Admin saved, byte-for-byte", JSON.stringify(x.display_name_hex) === JSON.stringify([hex(H.xssPaJs ?? "")]), x.display_name_hex);
  ok("real PG (A): change history reason = the hostile reason byte-for-byte (create row), null on the PA save", JSON.stringify(x.change_reason_hex) === JSON.stringify([hex(H.xss.reason), null]), x.change_reason_hex);
  ok("real PG (A): outbox auth.provider.changed details.reason = the hostile reason byte-for-byte, null on the PA save", JSON.stringify(x.outbox_reason_hex) === JSON.stringify([hex(H.xss.reason), null]), x.outbox_reason_hex);
  // SSE2 5842c444 L3: the old global "no entity in any row" check is gone (it would fail on a user who types "&lt;"). Instead each field
  // must hold exactly the typed entities that were sent: no server-side encoding (more entities) and no decoding (fewer).
  const ents = (t) => (t.match(/&(lt|gt|quot|amp|apos|#[0-9]+|#x[0-9a-f]+);/gi) ?? []).length;
  const unhex = (h) => h == null ? null : Buffer.from(h, "hex").toString("utf8");
  const x6 = x.l6 ?? {};
  ok("real PG (L1): L6 display_name = the hostile display_name, byte-for-byte (never overwritten)", JSON.stringify(x6.display_name_hex) === JSON.stringify([hex(H.xss.display_name6)]), x6.display_name_hex);
  ok("real PG (L2): L6 name (javascript: first) byte-for-byte", JSON.stringify(x6.location_name_hex) === JSON.stringify([hex(H.xss.name6)]), x6.location_name_hex);
  ok("real PG (L2): L6 change reason and outbox reason (javascript: first) byte-for-byte", JSON.stringify(x6.change_reason_hex) === JSON.stringify([hex(H.xss.reason6)])
    && JSON.stringify(x6.outbox_reason_hex) === JSON.stringify([hex(H.xss.reason6)]), [x6.change_reason_hex, x6.outbox_reason_hex]);
  const entPairs = [["L5 name", x.location_name_hex?.[0], H.xss.name], ["L5 change reason", x.change_reason_hex?.[0], H.xss.reason], ["L5 outbox reason", x.outbox_reason_hex?.[0], H.xss.reason],
    ["L6 name", x6.location_name_hex?.[0], H.xss.name6], ["L6 display_name", x6.display_name_hex?.[0], H.xss.display_name6],
    ["L6 change reason", x6.change_reason_hex?.[0], H.xss.reason6], ["L6 outbox reason", x6.outbox_reason_hex?.[0], H.xss.reason6]];
  ok("real PG (L3): per field, the stored text holds exactly as many HTML entities as were typed (L5: 0; L6: the typed ones)",
    entPairs.every(([, h, sent]) => h != null && ents(unhex(h)) === ents(sent)) && ents(H.xss.name6) === 3 && ents(H.xss.display_name6) === 4 && ents(H.xss.reason6) === 4,
    entPairs.map(([f, h, sent]) => [f, ents(sent), h == null ? null : ents(unhex(h))]));
  // SSE2 5842c444 L5: observed behaviour, asserted exactly (not a spec guess). [label, status, field:code list]; a 201 must store the
  // sent text trimmed (JS trim() = the same set .NET Trim() removed here: space, tab, LF, NBSP, U+3000; U+200B is kept), reason "" -> null.
  const N = "name:name_invalid", DN = "login_config/display_name:display_name_invalid", RL = "login_config/reason:reason_length";
  const lenWant = [["name: 200 runes (hostile)", 201], ["name: 201 runes (hostile)", 422, [N]], ["name: 200 astral runes (400 UTF-16 units)", 201],
    ["name: 201 astral runes", 422, [N]], ["name: hostile padded with spaces/tab/newline", 201], ["name: 200 hostile runes + 3 spaces of padding", 201],
    ["name: whitespace only", 422, [N]], ["name: empty", 422, [N]], ["name: interior newline", 422, [N]], ["name: NBSP / U+3000 padding", 201],
    ["name: zero-width space padding (U+200B)", 201], ["display_name: 100 runes (hostile)", 201], ["display_name: 101 runes (hostile)", 422, [DN]],
    ["display_name: 100 astral runes", 201], ["display_name: hostile padded with spaces", 201], ["display_name: whitespace only", 422, [DN]],
    ["display_name: interior tab", 201], ["reason: 9 runes, padded", 422, [RL]], ["reason: 10 runes", 201], ["reason: 500 runes (hostile)", 201],
    ["reason: 501 runes (hostile)", 422, [RL]],
    ["reason: 250 astral runes (500 UTF-16 units)", 201],
    // F-ASTRAL [TLD 2026-11-04 15:03] (was a 500: the outbox pattern counted UTF-16 units): JsonSchemaLite now matches "pattern" over code
    // points, so 251/300/500 astral runes are accepted and stored byte-for-byte, and 501 is the API's own 422 reason_length.
    ["reason: 251 astral runes (502 UTF-16 units)", 201], ["reason: 300 astral runes", 201], ["reason: 500 astral runes", 201],
    ["reason: 501 astral runes", 422, [RL]],
    ["reason: 10 runes + 1990 spaces (2000 raw)", 201], ["reason: 10 runes + 1991 spaces (2001 raw)", 422, [RL]],
    ["reason: whitespace only", 201], ["reason: empty string", 201], ["reason: interior newlines", 201]];
  const L = H.xss.len ?? [], XL = x.len ?? [];
  ok("L5 length/trim probes: every probe has exactly the observed status and field:code", L.length === lenWant.length
    && lenWant.every(([label, st, f = []], i) => L[i].probe === label && L[i].status === st && JSON.stringify(L[i].fields) === JSON.stringify(f)),
    L.map((p, i) => [p.probe, p.status, p.fields, lenWant[i]?.[1]]));
  const tr = (t) => t.trim();
  const stored = L.map((p, i) => p.status !== 201 ? null : [p.probe, JSON.stringify(XL[i]?.name_hex) === JSON.stringify([hex(tr(p.sent_name))]),
    JSON.stringify(XL[i]?.display_name_hex) === JSON.stringify([hex(tr(p.sent_display_name))]),
    JSON.stringify(XL[i]?.change_reason_hex) === JSON.stringify([p.sent_reason == null || tr(p.sent_reason) === "" ? null : hex(tr(p.sent_reason))]),
    JSON.stringify(XL[i]?.outbox_reason_hex) === JSON.stringify([p.sent_reason == null || tr(p.sent_reason) === "" ? null : hex(tr(p.sent_reason))])]).filter(Boolean);
  ok("L5 accepted probes: name, display_name, change reason and outbox reason stored = the sent text trimmed, byte-for-byte (whitespace-only / empty reason -> null)",
    stored.length === lenWant.filter(w => w[1] === 201).length && stored.every(r => r.slice(1).every(Boolean)), stored.filter(r => !r.slice(1).every(Boolean)));
  ok("L5 (F-ASTRAL): no probe failed with a 500, and every refused probe stored nothing", L.length > 0 && L.every(p => p.status !== 500)
    && L.filter(p => p.status !== 201).every(p => p.location_id === undefined) && XL.every((o, i) => L[i].status === 201 || o.name_hex === undefined));
  console.log("INFO len stored " + JSON.stringify((x.len ?? []).map((o, i) => ({ probe: o.probe, status: H.xss.len[i].status,
    name: o.name_hex ? [...unhex(o.name_hex[0])].length : undefined, display_name: o.display_name_hex ? [...unhex(o.display_name_hex[0])].length : undefined,
    reason: o.change_reason_hex ? o.change_reason_hex.map(h => h == null ? null : [...unhex(h)].length) : undefined }))));
  ok("real PG: no auth.provider.changed by the reader", !(`auth.provider.changed ${H.users.rd}` in D.events_by_type_actor), D.events_by_type_actor);
  console.log("INFO db " + JSON.stringify(D));
}
finish();

async function finish(code) {
  try { ws?.close(); } catch { }
  chrome.kill("SIGTERM");
  const hostCode = await new Promise(r => { if (host.exitCode !== null) r(host.exitCode); else { host.once("exit", r); setTimeout(() => { host.kill(); r(-1); }, 30000); } });
  const tail = hostOut.trim().split("\n").slice(-2).join(" | ");
  ok("ui-host exited 0 after cleanup (tables empty again)", hostCode === 0 && /PASS cleanup: auth.audit_outbox, change, llc, location, partner_reason_bounds empty again/.test(hostOut), [hostCode, tail]);
  await sleep(300);
  rmSync(profile, { recursive: true, force: true });
  console.log(`${pass} passed, ${fail} failed`);
  process.exit(code ?? (fail === 0 ? 0 : 1));
}
