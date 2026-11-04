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
const click = (sel) => ev(`document.querySelector(${JSON.stringify(sel)}).click(), true`);
const type = (sel, v) => ev(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); e.focus(); e.value = ${JSON.stringify(v)}; e.dispatchEvent(new Event("input", { bubbles: true })); e.dispatchEvent(new Event("change", { bubbles: true })); return true; })()`);
async function mouseClick(sel) {
  const p = await ev(`(() => { const r = document.querySelector(${JSON.stringify(sel)}).getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
  for (const type of ["mousePressed", "mouseReleased"]) await cdp("Input.dispatchMouseEvent", { type, x: p.x, y: p.y, button: "left", clickCount: 1 });
  await sleep(50);
}
async function key(k, code) { for (const type of ["keyDown", "keyUp"]) await cdp("Input.dispatchKeyEvent", { type, key: k, code, windowsVirtualKeyCode: k === " " ? 32 : 0 }); await sleep(50); }
const page = (persona, loc) => `/as/${persona}/orgs/${H.partner_a}/locations/${loc}/sign-in`;
const rows = () => ev(`[...document.querySelectorAll("#history-rows tr")].map(tr => [...tr.cells].map(td => td.textContent))`);
const shownError = () => ev(`[...document.querySelectorAll('[data-testid="error-area"]')].filter(e => !e.hidden).map(e => ({ code: e.dataset.error ?? e.id, rid: e.querySelector("[data-request-id]")?.textContent ?? "" }))`);
const RID = /^Request ID: [0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
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
async function save() { await click("#btn-save"); await until(`document.querySelector("#btn-save").getAttribute("aria-disabled") !== "true"`); await sleep(100); }
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
    && await txt("#mfa-hint") === "Only a Super Admin can turn this off." && (await attr("#mfa", "aria-describedby")).includes("mfa-hint"));
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
await step("SA downgrade", async () => {
  await open(page("sa", H.l2));
  await click("#btn-change");
  ok("SA edit: MFA not locked, Super Admin hint", await attr("#mfa", "aria-disabled") === null && await txt("#mfa-hint") === "Turning this off lowers security: Super Admin only, with a reason.");
  ok("SA edit: no reason field before a downgrade; server card stays", !(await vis("#f-reason")) && await vis("#card-connection"));
  await mouseClick("#mfa");
  ok("downgrade: warning banner, reason field, title 'Lower sign-in security: UI Cage 2'", await vis("#banner-downgrade") && await vis("#reason") && await txt("#title") === "Lower sign-in security: UI Cage 2"
    && await txt("#reason-hint") === "Saved in the audit log.");
  await save();
  let e = await shownError();
  ok("downgrade without reason: 400 reason_required, reason field error, request id", e.length === 1 && e[0].code === "reason_required" && RID.test(e[0].rid) && await vis("#reason-err") && await attr("#reason", "aria-invalid") === "true", e);
  await type("#reason", "short"); await save();
  e = await shownError();
  ok("downgrade with a short reason: server 422 reason_length shown on the field (no client-side length rule)", e.length === 1 && e[0].code === "validation_failed" && await vis("#reason-err") && RID.test(e[0].rid), [e, await txt("#reason-err")]);
  ok("D-102 #3: reason_length field copy (numbers stay TO CONFIRM)", await txt("#reason-err") === "This reason is too short or too long. Change it, then try again.", await txt("#reason-err"));
  ok("D-102: no Request ID under fields (reason error shown); the top-level banner has it", await noRidOnFields() && ridOk(await ridInfo("#banner-error")));
  await type("#reason", "UI e2e: conditional access enforces MFA (ticket UI-1)"); await save();
  ok("downgrade with a reason: saved", await vis("#banner-saved") && await txt("#cur-mfa") === "No");
  ok("after a success no Request ID line is left visible", await ev(`[...document.querySelectorAll("[data-request-id]")].every(e => e.hidden && e.textContent === "")`));
  const r = await rows();
  ok("downgrade: history row shows second step off and the reason", r[0][2] === H.users.sa && r[0][3] === "second step off. Reason: UI e2e: conditional access enforces MFA (ticket UI-1)", r[0]);
  await open(page("pa", H.l2)); await click("#btn-change");
  ok("PA after a Super Admin downgrade: MFA back on is not locked (API allows it; TO CONFIRM with Mohanraj)", await attr("#mfa", "aria-disabled") === null && await txt("#mfa-hint") === "");
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
  await type("#lname", "UI Cage 5"); await type("#dname", "UI Casino A"); await save();
  await until(`location.pathname.endsWith("/sign-in") && document.body.dataset.state === "saved"`);
  ok("create (Super Admin): goes to the new location's page with the saved banner; hash cleared", await ev(`document.body.dataset.state`) === "saved" && await ev(`location.hash`) === ""
    && await txt("#title") === "Sign-in method: UI Cage 5" && (await rows()).length === 1);
  ok("V11-CREATE-SECRETCARD (TLD 11:57): no Microsoft card on create; it appears once the location exists", await vis("#card-connection") && await txt("#conn-secret") === "Not set");
  await click("#btn-change");
  ok("V11-CREATE-SECRETCARD: card shown in the edit view after create", await ev(`document.body.dataset.state`) === "edit" && await vis("#card-connection"));
});
await step("PA partner-create", async () => {
  await open(`/as/pa/orgs/${H.partner_a}/locations/new`);
  ok("partner-create: chip 'Partner Admin: <URL partner id>', MFA locked, no reason field, no downgrade banner", await txt("#role-chip-text") === `Partner Admin: ${H.partner_a}` && await attr("#mfa", "aria-disabled") === "true" && !(await vis("#f-reason")) && !(await vis("#banner-downgrade")));
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

await step("XSS hostile API text", async () => {
  // SSE2 1ec73d38 (live probe of the code-read XSS PASS) + TLD 2026-11-04 14:18 (A). UiHost seeded L5 through the API with the payloads in
  // the location name, display_name and the create reason; the probes put them in fields the API must refuse.
  const X = H.xss, loc = H.l5;
  console.log("INFO xss probes " + JSON.stringify(X.probes));
  ok("XSS probes: the API refuses the payloads in UUID / enum / server-only fields (422, field named), validation unchanged",
    X.probes.length === 5 && X.probes.every(p => p.status === 422 && p.fields.length >= 1), X.probes);
  // A clean page of the same kind is the baseline for img/svg/script and attribute counts.
  const dom = () => ev(`(() => { const all = [...document.querySelectorAll("*")];
    return { img: document.querySelectorAll("img").length, svg: document.querySelectorAll("svg").length, script: document.querySelectorAll("script").length,
      iframe: document.querySelectorAll("iframe,object,embed").length,
      on: all.filter(e => [...e.attributes].some(a => /^on/i.test(a.name))).map(e => e.tagName),
      js: all.filter(e => [...e.attributes].some(a => /^\\s*javascript:/i.test(a.value))).map(e => e.tagName + " " + [...e.attributes].map(a => a.name).join(",")),
      xss: typeof window.__xss, anchors: document.querySelectorAll("a").length }; })()`);
  await open(page("sa", H.l4)); const base = await dom();
  ok("XSS baseline (clean SA view): 0 img/svg/iframe, 1 script (the module), no on* attribute, no javascript: value", base.img === 0 && base.svg === 0 && base.script === 1
    && base.iframe === 0 && base.on.length === 0 && base.js.length === 0 && base.xss === "undefined", base);
  const clean = async (label) => { await sleep(300); const d = await dom();
    ok(`XSS ${label}: window.__xss undefined, no extra img/svg/script/iframe/a, no on* attribute, no javascript: attribute`, d.xss === "undefined" && d.img === 0 && d.svg === 0
      && d.script === 1 && d.iframe === 0 && d.on.length === 0 && d.js.length === 0 && d.anchors === base.anchors, d); };
  const p0 = problems.length;
  const lit = async (label, persona) => {
    ok(`XSS ${label}: name literal in title, breadcrumb and document.title (textContent)`, await txt("#title") === `Sign-in method: ${X.name}`.replace(/\s+/g, " ")
      && await ev(`document.querySelector("#bc-location").textContent`) === X.name && await ev(`document.title`) === `Sign-in method: ${X.name}`,
      [await ev(`document.querySelector("#title").textContent`), await ev(`document.title`)]);
  };
  await open(page("sa", loc));
  await lit("SA view", "sa");
  let r = await rows();
  ok("XSS SA view: history reason cell = the payload text, literally; Who = SA actor id", r.length === 1 && r[0][2] === H.users.sa && r[0][3].endsWith(`. Reason: ${X.reason}`), r);
  await clean("SA view");
  await click("#btn-change");
  ok("XSS SA edit: title literal, display name input value literal", await ev(`document.querySelector("#title").textContent`) === `Edit sign-in method: ${X.name}`
    && await ev(`document.querySelector("#dname").value`) === X.display_name);
  await clean("SA edit");
  await open(page("pa", loc));
  await lit("PA view", "pa");
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
  ok("XSS: no CSP violation, exception or console error/warning on the hostile pages", problems.length === p0, problems.slice(p0));
  H.xssPaJs = js;
});

// ---------- global ----------
ok("no CSP violation, script exception or console error/warning on any page", problems.length === 0, problems);
ok("no request left 127.0.0.1 (ui host only)", outside.length === 0, outside);
ok("every write body: no oidc_*, secret_ref or secret key", bodies.length >= 8 && bodies.every(b => !/oidc_|secret/.test(b.body)), bodies.map(b => b.body));
ok("every PUT carried a strong If-Match", bodies.filter(b => b.method === "PUT").every(b => /^"\d+"$/.test(b.ifMatch ?? "")));

// ---------- real PG after the run ----------
host.stdin.end();
const dbm = await waitFor(/^UIHOST-DB (\{.*\})$/m, 30000);
const D = dbm ? JSON.parse(dbm[1]) : null;
ok("real PG: the host reported its DB summary", D !== null);
if (D) {
  ok("real PG: org:V reader wrote no change row (F-A3)", !(H.users.rd in D.changes_by_actor), D.changes_by_actor);
  ok("real PG: change rows = 5 seeds (L1-L4 + hostile L5) + PA edit + SA downgrade + PA conflict PUT + SA create + PA create + PA hostile save", D.changes_by_actor[H.users.sa] === 7 && D.changes_by_actor[H.users.pa] === 4, D.changes_by_actor);
  ok("real PG: 7 locations (refused creates and XSS probes added none)", D.locations === 7, D.locations);
  // TLD 2026-11-04 14:18 (A): stored byte-for-byte, no server-side HTML escaping; the page is the only place the text is made safe.
  const hex = (s) => Buffer.from(s, "utf8").toString("hex"), x = D.xss ?? {};
  ok("real PG (A): auth.location.name = the hostile name, byte-for-byte (UTF-8)", JSON.stringify(x.location_name_hex) === JSON.stringify([hex(H.xss.name)]), x.location_name_hex);
  ok("real PG (A): display_name = the javascript: text the Partner Admin saved, byte-for-byte", JSON.stringify(x.display_name_hex) === JSON.stringify([hex(H.xssPaJs ?? "")]), x.display_name_hex);
  ok("real PG (A): change history reason = the hostile reason byte-for-byte (create row), null on the PA save", JSON.stringify(x.change_reason_hex) === JSON.stringify([hex(H.xss.reason), null]), x.change_reason_hex);
  ok("real PG (A): outbox auth.provider.changed details.reason = the hostile reason byte-for-byte, null on the PA save", JSON.stringify(x.outbox_reason_hex) === JSON.stringify([hex(H.xss.reason), null]), x.outbox_reason_hex);
  ok("real PG (A): no HTML entity (&lt; &gt; &quot; &amp; &apos; &#) in location, config, change or outbox rows", x.html_entity_rows === 0, x.html_entity_rows);
  ok("real PG: no auth.provider.changed by the reader", !(`auth.provider.changed ${H.users.rd}` in D.events_by_type_actor), D.events_by_type_actor);
  console.log("INFO db " + JSON.stringify(D));
}
finish();

async function finish(code) {
  try { ws?.close(); } catch { }
  chrome.kill("SIGTERM");
  const hostCode = await new Promise(r => { if (host.exitCode !== null) r(host.exitCode); else { host.once("exit", r); setTimeout(() => { host.kill(); r(-1); }, 30000); } });
  const tail = hostOut.trim().split("\n").slice(-2).join(" | ");
  ok("ui-host exited 0 after cleanup (tables empty again)", hostCode === 0 && /PASS cleanup: auth.audit_outbox, change, llc, location empty again/.test(hostOut), [hostCode, tail]);
  await sleep(300);
  rmSync(profile, { recursive: true, force: true });
  console.log(`${pass} passed, ${fail} failed`);
  process.exit(code ?? (fail === 0 ? 0 : 1));
}
