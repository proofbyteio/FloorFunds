// S2a P4 screen 9: DOM + fetch for location-sign-in.html. Pure rules live in llc-core.mjs; error copy (approved D-102)
// is in error-copy.mjs. The page never sends oidc_* or secret_ref and shows only secret.set (spec §3.0, D-51).
import { parseRoute, viewerKind, createKind, isDowngrade, buildWrite, buildCreate, METHOD_LABEL, fallbackText, formatPT,
  changeDetails, fieldTarget, ifMatchFrom, limitsFrom } from "./llc-core.mjs";
import { TOP_LEVEL, FIELD, requestIdText, reasonLengthCopy } from "./error-copy.mjs";

const $ = (id) => document.getElementById(id);
const body = document.body;
const apiBase = (body.dataset.apiBase || "/api").replace(/\/$/, "");
// limits = { min, max } reason bounds from the API (DEC-107): create from /orgs/login-config/limits, edit from the GET.
const st = { route: null, kind: "unknown", view: null, ifMatch: null, history: null, mode: "loading", limits: null };

function show(el, on) { el.hidden = !on; }
function text(el, s) { el.textContent = s ?? ""; }

async function api(method, path, { json, ifMatch } = {}) {
  const headers = { Accept: "application/json" };
  if (json !== undefined) headers["Content-Type"] = "application/json";
  if (ifMatch) headers["If-Match"] = ifMatch;
  let res;
  try {
    res = await fetch(apiBase + path, { method, headers, body: json === undefined ? undefined : JSON.stringify(json),
      credentials: "same-origin", cache: "no-store" });
  } catch {
    return { status: null, body: null, requestId: null, etag: null };
  }
  let data = null;
  const ct = res.headers.get("Content-Type") || "";
  if (ct.includes("json")) { try { data = await res.json(); } catch { data = null; } }
  return { status: res.status, body: data, requestId: res.headers.get("X-Request-ID"), etag: res.headers.get("ETag") };
}

// ---------- banners and field errors ----------
const BANNERS = ["banner-loading", "banner-viewonly", "banner-saved", "banner-conflict", "banner-tenant", "banner-error", "banner-downgrade"];
function hideErrors() {
  for (const id of ["banner-conflict", "banner-tenant", "banner-error", "banner-saved"]) show($(id), false);
  for (const el of document.querySelectorAll("[data-request-id]")) { text(el, ""); show(el, false); }
  for (const id of ["lname", "dname", "tenant", "reason", "fallback", "mfa"]) clearFieldError(id);
}
function clearFieldError(id) {
  const input = $(id), err = $(id + "-err");
  if (input) { input.removeAttribute("aria-invalid"); input.setAttribute("aria-describedby", (input.getAttribute("aria-describedby") || "").split(" ").filter(x => x && x !== id + "-err").join(" ")); }
  if (err) { text(err, ""); show(err, false); }
}
function setFieldError(id, msg) {
  const errId = id === "mfa" ? "fallback-err" : id + "-err";
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
  const c = TOP_LEVEL[copyKey] ?? TOP_LEVEL.other;
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
  show($("card-history"), (k === "super" || k === "partner") && st.history !== null
    && (mode === "view" || mode === "saved" || (mode === "edit" && k === "partner")));
  const name = st.view ? st.view.name : "";
  if (mode === "create") title("Create location");
  else if (mode === "edit") title(`${isDowngrade(st.view, readForm()) && k === "super" ? "Lower sign-in security" : "Edit sign-in method"}: ${name}`);
  else if (st.view) title(`Sign-in method: ${name}`);
  text($("btn-save"), mode === "create" ? "Create location" : "Save");
  if (edit) syncForm();
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
    lockFor($("mfa"), false, "mfa-hint", "Turning this off lowers security: Super Admin only, with a reason.");
    lockFor($("fallback"), false, "fb-hint", "Off by default. Turning this on lowers security: Super Admin only, with a reason.");
    const down = isDowngrade(before, f);
    show($("banner-downgrade"), down);
    show($("f-reason"), down || create);
    // UX D-106 (md5 2b17464c): "{min} to {max} characters. Saved in the audit log." with the API's numbers (DEC-107).
    const len = st.limits ? `${st.limits.min} to ${st.limits.max} characters. ` : "";
    text($("reason-hint"), len + (create ? "Saved in the audit log. Required only if you turn off the second step or turn on backup sign-in."
      : "Saved in the audit log."));
  } else {
    // Locks only the downgrade direction relative to the saved value (create: relative to MFA on / backup off).
    // Partner Admin turning MFA back on / backup back off (allowed by the API, spec §6): TO CONFIRM (Mohanraj).
    const mfaLocked = before ? before.mfa_required === true : true;
    const fbLocked = before ? before.allow_db_fallback === false : true;
    lockFor($("mfa"), mfaLocked, "mfa-hint", mfaLocked ? "Only a Super Admin can turn this off." : "");
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
      const msg = fe.code === "reason_length" && st.limits ? reasonLengthCopy(st.limits.min, st.limits.max) : FIELD[fe.code] ?? FIELD.other;
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
  hideErrors();
  const f = readForm();
  const btn = $("btn-save");
  btn.setAttribute("aria-disabled", "true");
  try {
    if (st.mode === "create") {
      const r = await api("POST", "/orgs/locations", { json: buildCreate(st.route.partnerId, $("lname").value, f) });
      if (r.status === 201 && r.body && r.body.location_id) {
        location.assign(location.pathname.replace(/locations\/new\/?$/, `locations/${encodeURIComponent(r.body.location_id)}/sign-in`) + "#saved");
        return;
      }
      applyErrors(r);
    } else {
      const r = await api("PUT", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config`, { json: buildWrite(f), ifMatch: st.ifMatch });
      if (r.status === 200 && r.body) {
        st.view = r.body; st.ifMatch = ifMatchFrom(r.etag, r.body.row_version);
        await loadHistory();
        afterSaved();
        return;
      }
      applyErrors(r);
    }
  } finally { btn.removeAttribute("aria-disabled"); }
});
function afterSaved() {
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

// ---------- load ----------
async function loadHistory() {
  const h = await api("GET", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config/changes`);
  st.history = h.status === 200 && h.body && Array.isArray(h.body.items) ? h.body.items : null;
  if (st.history) renderHistory(st.history);
  return h;
}
async function start() {
  st.route = parseRoute(location.pathname);
  if (!st.route) { show($("banner-loading"), false); showError("not_found", null, "route"); return; }
  text($("bc-partner"), st.route.partnerId);
  if (st.route.mode === "create") {
    st.kind = createKind(body.dataset.adminType);
    setChip(st.kind, st.route.partnerId);
    text($("bc-page"), "New location");
    // DEC-107 [TLD 2026-11-04 16:32 Q2]: no location yet, so the reason bounds come from the limits endpoint.
    const lr = await api("GET", "/orgs/login-config/limits");
    st.limits = lr.status === 200 ? limitsFrom(lr.body) : null;
    if (!st.limits) { show($("banner-loading"), false); applyErrors(lr.status === 200 ? { ...lr, status: 500 } : lr); body.dataset.state = "error"; return; }
    fillForm({ provider_kind: "database", display_name: "", entra_allowed_tenant_ids: [], mfa_required: true, allow_db_fallback: false, name: "" });
    setMode("create");
    return;
  }
  const r = await api("GET", `/orgs/locations/${encodeURIComponent(st.route.locationId)}/login-config`);
  if (r.status !== 200 || !r.body) { show($("banner-loading"), false); applyErrors(r); body.dataset.state = "error"; return; }
  st.view = r.body; st.ifMatch = ifMatchFrom(r.etag, r.body.row_version);
  // DEC-107: the GET carries the reason bounds in effect; a PUT 200 body does not, so st.limits outlives st.view.
  st.limits = limitsFrom(r.body);
  if (!st.limits) { show($("banner-loading"), false); showError("500", r.requestId, "limits"); body.dataset.state = "error"; return; }
  // Only a Super Admin or own-partner Partner Admin may read history (C2). The super view is recognised by its `secret` key;
  // a restricted view asks /changes: 200 = Partner Admin, 403 = org:V reader (history hidden, no form).
  const h = await loadHistory();
  st.kind = viewerKind(st.view, h.status);
  if (st.kind === "unknown" || (st.kind === "super" && h.status !== 200)) { show($("banner-loading"), false); applyErrors(h); body.dataset.state = "error"; return; }
  if (st.route.partnerId !== st.view.partner_id) { show($("banner-loading"), false); showError("not_found", r.requestId, "partner_mismatch"); body.dataset.state = "error"; return; }
  setChip(st.kind, st.view.partner_id);
  const bl = $("bc-location"); text(bl, st.view.name); show(bl, true);
  renderCurrent(st.view, false);
  if (st.kind === "super") renderConnection(st.view);
  if (location.hash === "#saved") { history.replaceState(null, "", location.pathname + location.search); afterSaved(); return; }
  setMode("view");
}
start();
