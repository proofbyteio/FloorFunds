// S2a P4 screen 9: DOM + fetch for location-sign-in.html. Pure rules live in llc-core.mjs; error copy (approved D-102)
// is in error-copy.mjs. The page never sends oidc_* or secret_ref and shows only secret.set (spec §3.0, D-51).
import { parseRoute, viewerKind, createKind, isDowngrade, buildWrite, buildCreate, METHOD_LABEL, fallbackText, formatPT,
  changeDetails, fieldTarget, ifMatchFrom, limitsFrom, limitsOrFallback, partnerMfaState, PARTNER_MFA_OFF_HINT, errIdFor, limitsTimeout,
  limitsOutcome, locationOutcome, writeOutcome, historyOutcome } from "./llc-core.mjs";
import { TOP_LEVEL, FETCH_COPY, FIELD, requestIdText, reasonLengthCopy, reasonHintFallback, REASON_LENGTH_FALLBACK } from "./error-copy.mjs";

const $ = (id) => document.getElementById(id);
const body = document.body;
const apiBase = (body.dataset.apiBase || "/api").replace(/\/$/, "");
// limits = { min, max } reason bounds from the API (DEC-107): create from /orgs/login-config/limits, edit from the GET.
// limitsFallback: true when those did not load and limits is REASON_BOUNDS (UX D-108 copy, no numbers) [TLD 2026-11-04 18:35].
const st = { route: null, kind: "unknown", view: null, ifMatch: null, history: null, mode: "loading", limits: null, limitsFallback: false,
  locked: false, historyError: false };
// [TLD 2026-11-05 15:08 item 4] after an UNKNOWN write outcome Save / Create stays locked until it is reconciled.
// Edit: the lock lives in this page (st.locked); the only way to a new form is a reload, whose fresh successful GET of the
// saved location is shown in view mode for the user to inspect before a deliberate Change (the draft is never stored or
// replayed), so the reload itself is the reconciliation. Create: no surface can tell whether THIS create happened, so the
// unresolved create is kept in localStorage (survives reload, sign-in round trips and new tabs of this browser) and the
// create page stays blocked; it is never unlocked by the page. A UI lock is not a duplicate guarantee (other browsers / devices).
const pendingKey = (op, id) => `llc.unconfirmed.${op}.${id}`;
function pendingGet(k) { try { return globalThis.localStorage.getItem(k); } catch { return null; } }
function pendingSet(k) { try { globalThis.localStorage.setItem(k, new Date().toISOString()); } catch { /* still locked in this page */ } }

function setLimits(fromServer) { st.limitsFallback = !fromServer; st.limits = fromServer ?? limitsOrFallback(null); }

function show(el, on) { el.hidden = !on; }
function text(el, s) { el.textContent = s ?? ""; }

// Every call is bounded and never follows a redirect:
// - redirect: "manual" (limits + location GET: TLD 2026-11-04 21:11 Q2; history GET /changes, PUT and POST: TLD 2026-11-05
//   07:54 fetch hardening). A 3xx (same-site or cross-site, e.g. to login.microsoftonline.com) answers as opaque
//   (res.type "opaqueredirect") and is never followed.
// - an abort after timeoutMs (limits GET: TLD 18:35 item 4; location GET: TLD 21:11 Q3; history GET and the writes: TLD 07:54).
//   An abort answers like a failed fetch (status null) plus timedOut: true, also when it lands while the body is read, so a
//   write that timed out is never read as an answer. Callers tell a timeout apart from a network failure by timedOut.
async function api(method, path, { json, ifMatch, timeoutMs } = {}) {
  const headers = { Accept: "application/json" };
  if (json !== undefined) headers["Content-Type"] = "application/json";
  if (ifMatch) headers["If-Match"] = ifMatch;
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs);
  const aborted = new Promise((_, reject) => ac.signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")), { once: true }));
  aborted.catch(() => {});
  const failed = () => ({ status: null, body: null, requestId: null, etag: null, json: false, redirected: false, opaque: false, timedOut: ac.signal.aborted });
  try {
    let res;
    try {
      res = await fetch(apiBase + path, { method, headers, body: json === undefined ? undefined : JSON.stringify(json),
        credentials: "same-origin", cache: "no-store", signal: ac.signal, redirect: "manual" });
    } catch {
      return failed();
    }
    let data = null;
    const ct = res.headers.get("Content-Type") || "";
    const isJson = ct.includes("json");
    // [TLD 2026-11-05 16:00] the body read races the abort, so a body that would still arrive after the bound is never read
    // as an answer (an obsolete late completion is ignored, whatever the body stream does on abort).
    if (isJson) { try { data = await Promise.race([res.json(), aborted]); } catch { if (ac.signal.aborted) return failed(); data = null; } }
    return { status: res.status, body: data, requestId: res.headers.get("X-Request-ID"), etag: res.headers.get("ETag"), json: isJson, redirected: res.redirected, opaque: res.type === "opaqueredirect", timedOut: false };
  } finally {
    clearTimeout(timer);
  }
}
// GETs: the test hook __llcLimitsTimeoutMs (as before); writes: __llcWriteTimeoutMs. Both default to LIMITS_TIMEOUT_MS (10 s).
const getTimeout = () => limitsTimeout(globalThis.__llcLimitsTimeoutMs);
const writeTimeout = () => limitsTimeout(globalThis.__llcWriteTimeoutMs);

// ---------- banners and field errors ----------
const BANNERS = ["banner-loading", "banner-viewonly", "banner-saved", "banner-conflict", "banner-tenant", "banner-error", "banner-downgrade"];
function hideErrors() {
  for (const id of ["banner-conflict", "banner-tenant", "banner-error", "banner-saved"]) show($(id), false);
  for (const el of document.querySelectorAll("[data-request-id]")) { text(el, ""); show(el, false); }
  for (const id of ["lname", "dname", "tenant", "reason", "fallback", "mfa"]) clearFieldError(id);
}
// [SSE2 7b90e26f LOW-1] the same field -> slot mapping as setFieldError (errIdFor; mfa -> fallback-err); removes only that id.
function clearFieldError(id) {
  const eid = errIdFor(id);
  const input = $(id), err = $(eid);
  if (input) { input.removeAttribute("aria-invalid"); input.setAttribute("aria-describedby", (input.getAttribute("aria-describedby") || "").split(" ").filter(x => x && x !== eid).join(" ")); }
  if (err) { text(err, ""); show(err, false); }
}
function setFieldError(id, msg) {
  const errId = errIdFor(id);
  const input = $(id), err = $(errId);
  if (!err) return false;
  text(err, err.textContent ? err.textContent + " " + msg : msg);
  show(err, true);
  if (input) {
    input.setAttribute("aria-invalid", "true");
    const d = (input.getAttribute("aria-describedby") || "").split(" ").filter(Boolean);
    if (!d.includes(errId)) input.setAttribute("aria-describedby", [errId, ...d].join(" "));
  }
  return true;
}
function showBanner(id, requestId) {
  const b = $(id);
  // D-102: "Request ID: <id>" on its own last line (orgs.css .request-id); hidden when there is no id.
  for (const el of b.querySelectorAll("[data-request-id]")) { text(el, requestIdText(requestId)); show(el, !!requestId); }
  show(b, true);
  b.focus();
}
function showError(copyKey, requestId, code) {
  const c = TOP_LEVEL[copyKey] ?? FETCH_COPY[copyKey] ?? TOP_LEVEL.other; // FETCH_COPY: page-side outcomes (TLD 2026-11-05 07:54)
  text($("banner-error-title"), c.title);
  text($("banner-error-text"), c.text);
  $("banner-error").dataset.error = code ?? String(copyKey);
  showBanner("banner-error", requestId);
}
function errorKey(r) {
  if (r.status === null) return "network";
  if (r.status === 401) return "401";
  if (r.status >= 500) return "500";
  const code = r.body && typeof r.body.error === "string" ? r.body.error : null;
  return code && TOP_LEVEL[code] ? code : "other";
}

// ---------- rendering ----------
function setChip(kind, partnerId) {
  const t = { super: "Super Admin", partner: "Partner Admin", viewonly: "View only (org:V)" }[kind];
  if (!t) { show($("role-chip"), false); return; }
  const el = $("role-chip-text");
  if (kind === "partner") {
    // MK9 "Partner Admin: <partner name>". No API gives the name in S2a, so the partner id is shown, like the actor ids
    // [TLD 2026-11-04 11:57]; names: backlog V11-PARTNERNAME.
    const id = document.createElement("span"); id.className = "mono"; id.id = "role-chip-partner"; id.textContent = partnerId;
    el.replaceChildren(document.createTextNode(`${t}: `), id);
  } else text(el, t);
  show($("role-chip"), true);
}
function renderCurrent(v, saved) {
  text($("cur-h-text"), saved ? "New setting" : "Current setting");
  text($("cur-method"), METHOD_LABEL[v.provider_kind] ?? v.provider_kind);
  text($("cur-mfa"), v.mfa_required ? "Yes" : "No");
  text($("cur-fallback"), fallbackText(v));
  const cc = $("cur-changed");
  cc.replaceChildren(document.createTextNode(`${formatPT(v.updated_at)} PT by `));
  const who = document.createElement("span"); who.className = "mono"; who.textContent = v.updated_by;
  cc.append(who);
}
function renderConnection(v) {
  // Super Admin only (C1/D-51): read-only, never the secret value, no date (V11-ROTATEDAT).
  text($("conn-authority"), v.oidc_authority ?? "Not set");
  text($("conn-client"), v.oidc_client_id ?? "Not set");
  text($("conn-secret"), v.secret && v.secret.set === true ? "Set" : "Not set");
}
function renderHistory(items) {
  const rows = items.map(c => {
    const tr = document.createElement("tr");
    tr.dataset.changeId = c.change_id;
    const cells = [formatPT(c.at), "auth.provider.changed", c.actor_user_id,
      changeDetails(c) + (c.reason ? `. Reason: ${c.reason}` : "")];
    cells.forEach((s, i) => { const td = document.createElement("td"); if (i === 1 || i === 2) td.className = "mono"; td.textContent = s; tr.append(td); });
    return tr;
  });
  $("history-rows").replaceChildren(...rows);
}
function title(s) { text($("title"), s); document.title = s; }

function setMode(mode) {
  st.mode = mode;
  body.dataset.state = mode;
  const k = st.kind, edit = mode === "edit" || mode === "create";
  show($("banner-loading"), false);
  show($("banner-viewonly"), k === "viewonly");
  show($("card-current"), (mode === "view" || mode === "saved") && !!st.view);
  show($("row-change"), mode === "view" && (k === "super" || k === "partner"));
  show($("card-connection"), k === "super" && mode !== "create" && !!st.view);
  show($("form"), edit);
  show($("card-history"), (k === "super" || k === "partner") && (st.history !== null || st.historyError)
    && (mode === "view" || mode === "saved" || (mode === "edit" && k === "partner")));
  const name = st.view ? st.view.name : "";
  if (mode === "create") title("Create location");
  else if (mode === "edit") title(`${isDowngrade(st.view, readForm()) && k === "super" ? "Lower sign-in security" : "Edit sign-in method"}: ${name}`);
  else if (st.view) title(`Sign-in method: ${name}`);
  text($("btn-save"), mode === "create" ? "Create location" : "Save");
  if (edit) syncForm(); else delete body.dataset.partnerState;
}

// ---------- form ----------
function readForm() {
  const m = document.querySelector('input[name="method"]:checked');
  return { method: m ? m.value : null, displayName: $("dname").value, tenant: $("tenant").value,
    mfa: $("mfa").checked, fallback: $("fallback").checked, reason: $("reason").value,
    mfa_required: $("mfa").checked, allow_db_fallback: m && m.value === "entra" ? $("fallback").checked : false };
}
function fillForm(v) {
  $("m-db").checked = v.provider_kind === "database";
  $("m-entra").checked = v.provider_kind === "entra";
  $("dname").value = v.display_name ?? "";
  $("tenant").value = (v.entra_allowed_tenant_ids ?? [])[0] ?? "";
  $("mfa").checked = v.mfa_required === true;
  $("fallback").checked = v.allow_db_fallback === true;
  $("reason").value = "";
  $("lname").value = v.name ?? "";
}
function lockFor(input, locked, hint, hintText) {
  // Partner Admin: aria-disabled, still focusable, consequence in the described hint (MK9 partner-edit).
  if (locked) input.setAttribute("aria-disabled", "true"); else input.removeAttribute("aria-disabled");
  text($(hint), hintText);
}
function syncForm() {
  const f = readForm(), entra = f.method === "entra", create = st.mode === "create", k = st.kind;
  show($("f-lname"), create);
  show($("f-tenant"), entra);
  show($("c-fallback"), entra);
  const before = create ? null : st.view;
  if (k === "super") {
    delete body.dataset.partnerState;
    lockFor($("mfa"), false, "mfa-hint", "Turning this off lowers security: Super Admin only, with a reason.");
    lockFor($("fallback"), false, "fb-hint", "Off by default. Turning this on lowers security: Super Admin only, with a reason.");
    const down = isDowngrade(before, f);
    show($("banner-downgrade"), down);
    show($("f-reason"), down || create);
    // UX D-106 (md5 2b17464c): "{min} to {max} characters. Saved in the audit log." with the API's numbers (DEC-107).
    // Fallback limits: UX D-108 (md5 9318c2f8) hint, no length sentence [TLD 2026-11-04 18:35 / 18:37]; Edit shows only
    // "Saved in the audit log." (UX D-109, md5 1cab5e82) [TLD 2026-11-04 20:18 / 21:11] (reasonHintFallback, error-copy.mjs).
    const len = st.limits ? `${st.limits.min} to ${st.limits.max} characters. ` : "";
    text($("reason-hint"), st.limitsFallback ? reasonHintFallback(create) : len + (create ? "Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in."
      : "Saved in the audit log."));
  } else {
    // Locks only the downgrade direction relative to the SAVED value (create: relative to MFA on / backup off).
    // MFA back on [Confirmed DEC-106, Mohanraj PM t145u] = UX D-106 `partner-mfa-off` (md5 2b17464c): while the saved value is
    // off the checkbox is enabled with the D-106 hint; ticking needs no reason (no reason field, no downgrade banner) and
    // unticking before Save only undoes the edit. Once saved on, it is the partner-edit lock again.
    // Backup sign-in back off for a Partner Admin (allowed by the API, spec §4 / §6): Confirmed DEC-118/120/121 (own partner only; no reason needed).
    const ps = partnerMfaState(before);
    body.dataset.partnerState = ps;
    const mfaLocked = ps !== "partner-mfa-off";
    const fbLocked = before ? before.allow_db_fallback === false : true;
    lockFor($("mfa"), mfaLocked, "mfa-hint", mfaLocked ? "Only a Super Admin can turn this off." : PARTNER_MFA_OFF_HINT);
    lockFor($("fallback"), fbLocked, "fb-hint", fbLocked ? "Only a Super Admin can turn this on." : "");
    show($("banner-downgrade"), false);
    show($("f-reason"), false);
  }
  if (st.mode === "edit") title(`${k === "super" && isDowngrade(before, f) ? "Lower sign-in security" : "Edit sign-in method"}: ${st.view.name}`);
}
for (const id of ["mfa", "fallback"]) {
  $(id).addEventListener("click", (e) => { if (e.currentTarget.getAttribute("aria-disabled") === "true") e.preventDefault(); });
}
for (const el of [$("m-db"), $("m-entra"), $("mfa"), $("fallback")]) el.addEventListener("change", syncForm);

function applyErrors(r) {
  const key = errorKey(r);
  if (r.status === 409) { showBanner("banner-conflict", r.requestId); return; }
  if (r.status === 422 && r.body && Array.isArray(r.body.fields)) {
    let tenantTaken = false, first = null;
    for (const fe of r.body.fields) {
      const target = fieldTarget(fe.field);
      if (fe.code === "tenant_bound_elsewhere") {
        tenantTaken = true;
        setFieldError("tenant", "This tenant ID is already linked to another partner, so it can't be used here.");
        first ??= "tenant";
        continue;
      }
      const msg = fe.code === "reason_length" && st.limits ? (st.limitsFallback ? REASON_LENGTH_FALLBACK : reasonLengthCopy(st.limits.min, st.limits.max))
        : FIELD[fe.code] ?? FIELD.other; // fallback limits: UX D-108 copy, no numbers
      if (target && setFieldError(target, msg)) first ??= target;
    }
    if (tenantTaken) showBanner("banner-tenant", r.requestId); else showError("validation_failed", r.requestId, "validation_failed");
    return;
  }
  if (r.status === 400 && r.body && r.body.error === "reason_required") {
    show($("f-reason"), true);
    setFieldError("reason", FIELD.reason_required);
  }
  showError(key, r.requestId, r.body && r.body.error);
}

$("form").addEventListener("submit", async (e) => {
  e.preventDefault();
  if (st.locked) return; // [TLD 15:08 item 4] locked after an UNKNOWN outcome: no second write from this page
  // [TLD 16:00] the Create guard is re-read at submit: another tab / a restored page may have recorded an unresolved create.
  if (st.mode === "create" && pendingGet(pendingKey("create", st.route.partnerId)) !== null) { showCreateBlocked(); return; }
  hideErrors();
  const f = readForm();
  const btn = $("btn-save");
  btn.setAttribute("aria-disabled", "true");
  try {
    // [TLD 2026-11-05 15:08, supersedes TLD 07:54] writeOutcome: "unknown" (timeout of headers or body, transport failure,
    // unusable answer, 5xx) -> never saved / never "nothing saved", no retry, Save / Create locked; "session" (401, 3xx) ->
    // sign in again; "rejected" (contract 4xx) -> the error from the answer.
    if (st.mode === "create") {
      const r = await api("POST", "/orgs/locations", { json: buildCreate(st.route.partnerId, $("lname").value, f), timeoutMs: writeTimeout() });
      const w = writeOutcome(r, 201);
      if (w.kind === "ok") {
        location.assign(location.pathname.replace(/locations\/new\/?$/, `locations/${encodeURIComponent(r.body.location_id)}/sign-in`) + "#saved");
        return;
      }
      writeFailed(w, r);
    } else {
      const r = await api("PUT", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config`, { json: buildWrite(f), ifMatch: st.ifMatch, timeoutMs: writeTimeout() });
      const w = writeOutcome(r, 200);
      if (w.kind === "ok") {
        st.view = r.body; st.ifMatch = ifMatchFrom(r.etag, r.body.row_version);
        // [TLD 15:08 item 5] the save is confirmed; a failed history GET does not change that (soft history-only error).
        const { ho } = await loadHistory();
        st.historyError = ho.kind !== "ok";
        afterSaved();
        return;
      }
      writeFailed(w, r);
    }
  } finally { if (!st.locked) btn.removeAttribute("aria-disabled"); }
});
function lockUnknown() {
  st.locked = true;
  $("btn-save").setAttribute("aria-disabled", "true");
  if (st.mode === "create") pendingSet(pendingKey("create", st.route.partnerId));
}
function writeFailed(w, r) {
  if (w.kind === "unknown") {
    lockUnknown();
    if (st.mode === "create") showCreateBlocked(w.session);
    else showError(w.session ? "write_unconfirmed_session" : "write_unconfirmed", r.requestId, w.session ? "write_unconfirmed_session" : "write_unconfirmed");
    return;
  }
  applyErrors(w.kind === "session" ? { ...r, status: 401 } : r);
}
// [TLD 15:08 items 4 / 6] Create outcome UNKNOWN: blocked, no reload advice (no recovery path exists), no form, never unlocked here.
function showCreateBlocked(session = false) {
  st.locked = true;
  show($("banner-loading"), false);
  show($("form"), false);
  const k = session ? "create_unconfirmed_blocked_session" : "create_unconfirmed_blocked";
  showError(k, null, k);
  body.dataset.state = "blocked";
}
function renderHistoryError() { show($("history-error"), st.historyError); }
function afterSaved() {
  renderHistoryError();
  renderCurrent(st.view, true);
  if (st.kind === "super") renderConnection(st.view);
  setMode("saved");
  text($("saved-loc"), st.view.name);
  show($("banner-saved"), true);
  $("banner-saved").focus();
}
$("btn-change").addEventListener("click", () => { hideErrors(); fillForm(st.view); setMode("edit"); $("m-db").checked ? $("m-db").focus() : $("m-entra").focus(); });
$("btn-cancel").addEventListener("click", () => {
  hideErrors();
  if (st.mode === "create") { if (history.length > 1) history.back(); else { $("form").reset(); syncForm(); } return; }
  show($("banner-downgrade"), false);
  setMode("view");
});
$("conflict-reload").addEventListener("click", (e) => { e.preventDefault(); location.reload(); });
// [TLD 15:08 item 5] retry the history GET only; never a write.
$("history-retry").addEventListener("click", async () => {
  const { ho } = await loadHistory();
  st.historyError = ho.kind !== "ok";
  renderHistoryError();
  setMode(st.mode);
});

// [TLD 2026-11-05 16:00] a back-forward restore never reuses an old snapshot / form: reload, so the page's fresh no-store GET
// (+ ETag) is the only way to an enabled form, and the Create guard is re-read.
addEventListener("pageshow", (e) => { if (e.persisted) location.reload(); });

// ---------- load ----------
// [TLD 2026-11-05 07:54] the history GET is bounded and never follows a redirect, like the location GET (api()).
// historyOutcome: a timeout, 3xx / followed redirect or non-JSON 200 is "unavailable" (the softer history copy on load).
async function loadHistory() {
  const h = await api("GET", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config/changes`, { timeoutMs: getTimeout() });
  const ho = historyOutcome(h);
  st.history = ho.kind === "ok" ? ho.items : null;
  if (st.history) renderHistory(st.history); else $("history-rows").replaceChildren();
  return { h, ho };
}
async function start() {
  st.route = parseRoute(location.pathname);
  if (!st.route) { show($("banner-loading"), false); showError("not_found", null, "route"); return; }
  text($("bc-partner"), st.route.partnerId);
  if (st.route.mode === "create") {
    st.kind = createKind(body.dataset.adminType);
    setChip(st.kind, st.route.partnerId);
    text($("bc-page"), "New location");
    if (pendingGet(pendingKey("create", st.route.partnerId)) !== null) { showCreateBlocked(); return; } // [TLD 15:08 item 4]
    // DEC-107 [TLD 2026-11-04 16:32 Q2]: no location yet, so the reason bounds come from the limits endpoint.
    // [TLD 2026-11-04 17:28] a failed fetch or missing / invalid values -> the server's hard bounds 10 / 500 (REASON_BOUNDS);
    // a 401 / 403 is an access answer, not a limits value, so it is still shown as the page error.
    // [TLD 2026-11-04 18:35] a redirect, a 3xx or a non-JSON 200 is shown like a 401; the GET is aborted after
    // LIMITS_TIMEOUT_MS (10 s) and then falls back like a failed fetch (limitsOutcome, limitsTimeout in llc-core.mjs).
    // v1.1 Slice A [TLD 08:00 Q3]: the URL partner's bounds (its partner_reason_bounds row, else the deployment config).
    const lr = await api("GET", `/orgs/login-config/limits?partner_id=${encodeURIComponent(st.route.partnerId)}`, { timeoutMs: getTimeout() });
    const lo = limitsOutcome(lr);
    if (lo.kind === "error" || lo.kind === "error401") { show($("banner-loading"), false); applyErrors(lo.kind === "error401" ? { ...lr, status: 401 } : lr); body.dataset.state = "error"; return; }
    setLimits(lo.kind === "server" ? lo.limits : null);
    fillForm({ provider_kind: "database", display_name: "", entra_allowed_tenant_ids: [], mfa_required: true, allow_db_fallback: false, name: "" });
    setMode("create");
    return;
  }
  // [TLD 2026-11-04 21:11 Q2 / Q3] redirect: "manual" + the same abort as the limits GET; a redirect / non-JSON 200 is shown
  // as a 401 and a timeout as the page error (locationOutcome), never a form with fallback limits.
  const r = await api("GET", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config`, { timeoutMs: getTimeout() });
  const ro = locationOutcome(r);
  if (ro.kind !== "ok") { show($("banner-loading"), false); applyErrors(ro.kind === "error401" ? { ...r, status: 401 } : r); body.dataset.state = "error"; return; }
  st.view = r.body; st.ifMatch = ifMatchFrom(r.etag, r.body.row_version);
  // DEC-107: the GET carries the reason bounds in effect; a PUT 200 body does not, so st.limits outlives st.view.
  setLimits(limitsFrom(r.body)); // [TLD 2026-11-04 17:28] missing / invalid -> REASON_BOUNDS (D-108 copy, TLD 18:35)
  // Only a Super Admin or own-partner Partner Admin may read history (C2). The super view is recognised by its `secret` key;
  // a restricted view asks /changes: 200 = Partner Admin, 403 = org:V reader (history hidden, no form).
  const { h, ho } = await loadHistory();
  // [TLD 2026-11-05 07:54] history unavailable (timeout / redirect / non-JSON 200): the viewer kind can't be told (C2), so the
  // page error, with the softer history copy "Couldn't load history — try again" (brief item 2), not the session copy.
  const savedLanding = location.hash === "#saved";
  if (ho.kind === "unavailable" && !savedLanding) { show($("banner-loading"), false); showError("history_unavailable", h.requestId, "history_unavailable"); body.dataset.state = "error"; return; }
  // [TLD 15:08 item 5] after a confirmed create (201 -> #saved) a failed history GET keeps the saved state: the viewer is the
  // admin who just created it (super view = `secret` key, else Partner Admin; the API still decides every later write).
  st.kind = ho.kind === "unavailable" ? (viewerKind(st.view, null) === "super" ? "super" : "partner") : viewerKind(st.view, h.status);
  st.historyError = savedLanding && ho.kind !== "ok";
  if (st.kind === "unknown" || (st.kind === "super" && h.status !== 200)) { show($("banner-loading"), false); applyErrors(h); body.dataset.state = "error"; return; }
  if (st.route.partnerId !== st.view.partner_id) { show($("banner-loading"), false); showError("not_found", r.requestId, "partner_mismatch"); body.dataset.state = "error"; return; }
  setChip(st.kind, st.view.partner_id);
  const bl = $("bc-location"); text(bl, st.view.name); show(bl, true);
  renderCurrent(st.view, false);
  if (st.kind === "super") renderConnection(st.view);
  if (location.hash === "#saved") { history.replaceState(null, "", location.pathname + location.search); afterSaved(); return; }
  setMode("view"); // [TLD 15:08 item 4] Edit reconciliation: this fresh GET is on screen before any deliberate Change
}
start();
