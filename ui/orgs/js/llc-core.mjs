// S2a P4 screen 9: pure logic (no DOM, no fetch), shared by llc-page.mjs and tests/core.test.mjs.
// Spec: contract/v1.1-S2a-location-login-config.md §3, §4, §5, §11. Mockup MK9 90d2a8fa (D-103).

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Route from the page path: /orgs/{partner_id}/locations/{location_id}/sign-in or /orgs/{partner_id}/locations/new.
 *  C3 (a) [TLD 2026-11-04 9:49]: the partner comes from the org in the URL; there is no partner picker. */
export function parseRoute(pathname) {
  const m = /\/orgs\/([^/]+)\/locations\/([^/]+)(?:\/sign-in)?\/?$/.exec(pathname);
  if (!m) return null;
  const partnerId = decodeURIComponent(m[1]);
  if (!UUID.test(partnerId)) return null;
  if (m[2] === "new" && !/\/sign-in\/?$/.test(pathname)) return { mode: "create", partnerId, locationId: null };
  const locationId = decodeURIComponent(m[2]);
  if (!UUID.test(locationId) || !/\/sign-in\/?$/.test(pathname)) return null;
  return { mode: "edit", partnerId, locationId };
}

/** Who is looking, from what the API returned (the API decides; the page only follows):
 *  - a Super Admin view carries `secret` (login_config_view_super, spec §3.0); restricted views never do;
 *  - restricted + history 200 = Partner Admin of the location's partner; restricted + history 403 = org:V reader (C2). */
export function viewerKind(view, historyStatus) {
  if (view && Object.prototype.hasOwnProperty.call(view, "secret")) return "super";
  if (historyStatus === 200) return "partner";
  if (historyStatus === 403) return "viewonly";
  return "unknown";
}

/** Create layout from the host's session (data-admin-type); anything but "super" gets the partner-create layout
 *  (least privilege; the API enforces the real rule either way). */
export function createKind(adminType) { return adminType === "super" ? "super" : "partner"; }

/** DEC-096 / DEC-098 downgrade, spec §4 step 9: mfa true -> false or fallback false -> true; on create vs "none" (A-5). */
export function isDowngrade(before, after) {
  if (!before) return after.mfa_required === false || after.allow_db_fallback === true;
  return (before.mfa_required === true && after.mfa_required === false)
      || (before.allow_db_fallback === false && after.allow_db_fallback === true);
}

/** Partner Admin layout of the method section (UX D-106, md5 2b17464c) from the SAVED config (null on create):
 *  "partner-create" (MFA ticked and locked, A-5), "partner-mfa-off" (saved MFA off: the MFA checkbox is enabled, DEC-106)
 *  or "partner-edit" (saved MFA on: locked, only a Super Admin can turn it off, DEC-098). */
export function partnerMfaState(saved) {
  if (!saved) return "partner-create";
  return saved.mfa_required === false ? "partner-mfa-off" : "partner-edit";
}
/** D-106 hint for partner-mfa-off, verbatim. */
export const PARTNER_MFA_OFF_HINT = "Off at this location now. Turn it on to add a second step at sign-in. Once saved, only a Super Admin can turn it off again.";

/** login_config_write (spec §3.2): exactly the client-editable keys. Never oidc_*, never secret_ref (D-51: server-filled). */
export function buildWrite(f) {
  const entra = f.method === "entra";
  const tenant = (f.tenant ?? "").trim();
  const reason = (f.reason ?? "").trim();
  return {
    provider_kind: f.method,
    display_name: f.displayName ?? "",
    entra_allowed_tenant_ids: entra && tenant !== "" ? [tenant] : [],
    allow_db_fallback: entra ? f.fallback === true : false,
    mfa_required: f.mfa === true,
    reason: reason === "" ? null : f.reason,
  };
}

/** create_location_request (spec §3.3): partner_id from the URL (C3). reason only when there is one (A-5). */
export function buildCreate(partnerId, name, f) {
  const lc = buildWrite(f);
  if (lc.reason === null) delete lc.reason;
  return { partner_id: partnerId, name, login_config: lc };
}

export const METHOD_LABEL = Object.freeze({ database: "Username and password + one-time code", entra: "Microsoft (Entra ID)" });
const FIELD_LABEL = Object.freeze({ provider_kind: "sign-in method", display_name: "name shown on the sign-in page",
  entra_allowed_tenant_ids: "tenant ID", mfa_required: "second step", allow_db_fallback: "backup sign-in",
  oidc_authority: "Microsoft sign-in address", oidc_client_id: "app registration", secret_ref: "Microsoft app connection" });

export function fallbackText(v) {
  if (v.provider_kind !== "entra") return "Not used (database location)";
  return v.allow_db_fallback ? "On" : "Off";
}

/** "Oct 28, 2026, 3:12 PM" in America/Los_Angeles (MK9 "When (PT)"). */
export function formatPT(iso) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return new Intl.DateTimeFormat("en-US", { timeZone: "America/Los_Angeles", month: "short", day: "numeric", year: "numeric",
    hour: "numeric", minute: "2-digit" }).format(d);
}

/** One history row's Details text. Actor ids only (C3; names = backlog V11-ACTORNAME). */
export function changeDetails(c) {
  const a = c.after ?? {};
  if (c.change_kind === "created")
    return `Created: ${a.provider_kind === "entra" ? "Microsoft" : "database"} login, second step ${a.mfa_required ? "on" : "off"}`
      + (a.provider_kind === "entra" ? `, backup sign-in ${a.allow_db_fallback ? "on" : "off"}` : "");
  const b = c.before ?? {};
  const parts = [];
  if (b.provider_kind !== a.provider_kind) parts.push(`${b.provider_kind === "entra" ? "Microsoft" : "Database"} → ${a.provider_kind === "entra" ? "Microsoft" : "Database"}`);
  if (b.mfa_required !== a.mfa_required) parts.push(`second step ${a.mfa_required ? "on" : "off"}`);
  if (b.allow_db_fallback !== a.allow_db_fallback) parts.push(`backup sign-in ${a.allow_db_fallback ? "on" : "off"}`);
  const shown = new Set(["provider_kind", "mfa_required", "allow_db_fallback"]);
  const other = (c.changed_fields ?? []).filter(x => !shown.has(x)).map(x => FIELD_LABEL[x] ?? x);
  if (other.length) parts.push(`changed: ${other.join(", ")}`);
  return parts.join("; ") || "No change to the method";
}

/** 422 field path -> the page's input id (spec §5 example: "entra_allowed_tenant_ids/0").
 *  PUT paths are the login_config_write names; a create (POST /orgs/locations) names them under "login_config/"
 *  (LoginConfigInput.ValidateCreate) and its own top-level keys unprefixed ("name"; "partner_id" has no form field).
 *  [DEC-107: login_config/reason] [TLD 2026-11-04 16:56 Q2: every login_config/* write field, P4h]
 *  Not mapped (no form field; the page shows the validation_failed banner only): partner_id, login_config itself,
 *  oidc_authority / oidc_client_id / secret_ref (field_not_writable) and unknown_field keys. provider_kind maps to
 *  "method", which has no error slot (same as Edit), so it is banner-only too. */
const WRITE_TARGET = Object.freeze({ provider_kind: "method", display_name: "dname", entra_allowed_tenant_ids: "tenant",
  mfa_required: "mfa", allow_db_fallback: "fallback", reason: "reason" });
const CREATE_TARGET = Object.freeze({ name: "lname" });
const own = (o, k) => (Object.hasOwn(o, k) ? o[k] : null);
export function fieldTarget(path) {
  const p = String(path);
  if (p.startsWith("login_config/")) return own(WRITE_TARGET, p.slice("login_config/".length).split("/")[0]);
  const top = p.split("/")[0];
  return own(WRITE_TARGET, top) ?? own(CREATE_TARGET, top);
}

/** Strong ETag "<n>" -> If-Match value; anything else -> null (never invent one). */
export function ifMatchFrom(etag, rowVersion) {
  if (typeof etag === "string" && /^"\d+"$/.test(etag)) return etag;
  return Number.isInteger(rowVersion) ? `"${rowVersion}"` : null;
}

/** Client mirror of the server's hard reason bounds: the DB CHECK llcc_reason (char_length(reason) BETWEEN 10 AND 500,
 *  db/ frozen) and the Admin.Api startup check (AdminApiOptions: 10 <= LoginConfig:ReasonMinLength <= ReasonMaxLength <= 500).
 *  Used to reject out-of-range limits and as the fallback when the API's limits are missing or invalid [TLD 2026-11-04 17:28]. */
export const REASON_BOUNDS = Object.freeze({ min: 10, max: 500 });

/** DEC-107 [TLD 2026-11-04 16:32 Q2]: the reason bounds from GET /orgs/login-config/limits or the location GET
 *  (reason_min_length / reason_max_length) -> { min, max }, or null when missing or not integers with
 *  REASON_BOUNDS.min <= min <= max <= REASON_BOUNDS.max. */
export function limitsFrom(o) {
  const min = o && o.reason_min_length, max = o && o.reason_max_length;
  return Number.isInteger(min) && Number.isInteger(max) && min >= REASON_BOUNDS.min && min <= max && max <= REASON_BOUNDS.max ? { min, max } : null;
}

/** [TLD 2026-11-04 17:28] limitsFrom, or the server's hard bounds (REASON_BOUNDS) when the values are missing or invalid. */
export function limitsOrFallback(o) {
  return limitsFrom(o) ?? { min: REASON_BOUNDS.min, max: REASON_BOUNDS.max };
}

/** [SSE2 7b90e26f LOW-1, TLD 2026-11-04 18:35] the error slot a field's 422 copy goes in: the MFA checkbox has none of its
 *  own and shares backup sign-in's (#fallback-err). setFieldError adds this id to aria-describedby; clearFieldError removes it. */
export function errIdFor(id) {
  return id === "mfa" ? "fallback-err" : id + "-err";
}

/** [TLD 2026-11-04 18:35 item 4] the create page aborts GET /orgs/login-config/limits after this long and falls back
 *  as on a failed fetch. Tests may pass a shorter positive integer (override); anything else -> LIMITS_TIMEOUT_MS. */
export const LIMITS_TIMEOUT_MS = 10000;
export function limitsTimeout(override) {
  return Number.isInteger(override) && override > 0 ? override : LIMITS_TIMEOUT_MS;
}

/** [TLD 2026-11-04 18:35 items 2 and 5] what the create page does with the limits answer r (api(): status, body, json, redirected).
 *  "error401": a redirect (followed, or a 3xx answer) -> page error as a 401.
 *  "error": 401 / 403 -> page error from the answer (an access answer, never a fallback); a 200 that is not JSON -> page
 *    error, not a 401 [TLD 2026-11-05 15:08 item 2; was "error401" per TLD 2026-11-04 18:35].
 *  "server": a JSON 200 whose limits pass limitsFrom -> { limits }.
 *  "fallback": network failure, timeout (status null), any other status, or a JSON 200 with missing / invalid limits. */
export function limitsOutcome(r) {
  if (r.status === null) return { kind: "fallback" };
  if (r.opaque) return { kind: "error401" }; // [TLD 2026-11-04 21:11 Q2] redirect: "manual" -> opaqueredirect (any 3xx, cross-site too)
  if (r.redirected || (r.status >= 300 && r.status < 400)) return { kind: "error401" };
  if (r.status === 401 || r.status === 403) return { kind: "error" };
  // [TLD 2026-11-05 15:08 item 2] a non-JSON 200 is not a verified 401: blocked as an invalid answer (page error). A JSON 200
  // that does not parse keeps the TLD 17:28 / 18:35 fallback (a read; the server still enforces the real bounds).
  if (r.status === 200 && !r.json) return { kind: "error" };
  const limits = r.status === 200 ? limitsFrom(r.body) : null;
  return limits ? { kind: "server", limits } : { kind: "fallback" };
}

/** [TLD 2026-11-04 21:11 Q2 / Q3, SSE2 70cf9b13 LOW-1] what the edit page does with GET /orgs/locations/{id}/login-config
 *  (api(): status, body, json, redirected, opaque; fetched with redirect: "manual" and the LIMITS_TIMEOUT_MS abort).
 *  "error401": an opaqueredirect (any 3xx, e.g. a 302 to login.microsoftonline.com), a followed redirect or a 3xx answer
 *  -> the page error as a 401, no form. A 200 that is not JSON -> "error" [TLD 2026-11-05 15:08 item 2; was "error401"].
 *  "error": network failure or timeout (status null) or any other non-200 -> the page error from the answer, no form
 *  (never the fallback limits; those apply only to a 200 whose limits are missing / invalid).
 *  "ok": a JSON 200 with a body. */
export function locationOutcome(r) {
  if (r.status === null) return { kind: "error" };
  if (r.opaque || r.redirected || (r.status >= 300 && r.status < 400)) return { kind: "error401" };
  // [TLD 2026-11-05 15:08 item 2] a non-JSON / unparseable 200 is an invalid answer (page error), not a verified 401.
  return r.status === 200 && r.json && r.body ? { kind: "ok" } : { kind: "error" };
}

/** Contract error codes (contract/location-login-config.schema.json $defs/error_code). A 4xx JSON body naming one of them
 *  is an authoritative rejection by the API (spec §5.1: nothing written). */
export const CONTRACT_ERROR_CODES = Object.freeze(["unauthenticated", "forbidden", "not_found", "precondition_required", "if_match_invalid",
  "concurrency_conflict", "malformed_body", "payload_too_large", "request_timeout", "validation_failed", "not_super_admin", "reason_required"]);
/** [TLD 2026-11-05 16:00] Authoritative rejections: the status + error code pairs that Admin.Api's location PUT / POST handlers
 * (src/Admin.Api/LoginConfigApi.cs PutAsync / CreateAsync) throw before their transaction commits, so nothing in the
 * location config was written (a denial audit row may be). Any other 4xx / 5xx, or a known code under another status, is UNKNOWN. */
export const WRITE_REJECTIONS = Object.freeze({
  PUT: Object.freeze({ 400: ["if_match_invalid", "malformed_body", "reason_required"], 403: ["forbidden", "not_super_admin"], 404: ["not_found"],
    408: ["request_timeout"], 409: ["concurrency_conflict"], 413: ["payload_too_large"], 422: ["validation_failed"], 428: ["precondition_required"] }),
  POST: Object.freeze({ 400: ["malformed_body", "reason_required"], 403: ["forbidden", "not_super_admin"], 404: ["not_found"],
    408: ["request_timeout"], 413: ["payload_too_large"], 422: ["validation_failed"] }),
});
const UUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The success body each write needs before the page may call it saved: PUT 200 = a login_config_view with an integer
 *  row_version; POST 201 = a body with a uuid location_id. */
export function usableWriteBody(okStatus, body) {
  if (!body || typeof body !== "object") return false;
  return okStatus === 201 ? typeof body.location_id === "string" && UUID_RE.test(body.location_id) : Number.isInteger(body.row_version);
}

/** [TLD 2026-11-05 15:08 items 1-3; supersedes the TLD 07:54 version] what the page does with a write answer r
 *  (PUT /orgs/locations/{id}/login-config okStatus 200, POST /orgs/locations okStatus 201; api(): status, body, json,
 *  redirected, opaque, timedOut). Outcome certainty is kept apart from auth handling:
 *  "ok": okStatus, JSON, and a usable body (usableWriteBody) -> saved.
 *  "session": 401, or an opaqueredirect / followed redirect / 3xx (redirect: "manual") -> sign in again. No save claim either way
 *    (a 3xx is not a contract answer).
 *  "rejected": a 4xx (not 401) whose JSON body names a contract error code -> the API refused it (authoritative, nothing written).
 *  "unknown": everything else after dispatch: the abort (headers or body, timedOut), a network failure that is not a timeout
 *    (status null), a 2xx that is not okStatus / not JSON / unparseable / missing the required fields, any 5xx, any other
 *    answer -> the outcome is UNKNOWN: never "saved", never "nothing saved", no retry; the page locks Save / Create. */
export function writeOutcome(r, okStatus) {
  if (r.timedOut || r.status === null) return { kind: "unknown" };
  // [TLD 2026-11-05 16:00] an unexpected 3xx / opaqueredirect after dispatch is UNKNOWN (locked like any UNKNOWN) AND gets the
  // sign-in-again treatment (session: true); never followed or replayed. A 401 is answered by the API before any handler runs.
  if (r.opaque || r.redirected || (r.status >= 300 && r.status < 400)) return { kind: "unknown", session: true };
  if (r.status === 401) return { kind: "session" };
  if (r.status >= 200 && r.status < 300)
    return r.status === okStatus && r.json && usableWriteBody(okStatus, r.body) ? { kind: "ok" } : { kind: "unknown" };
  // [TLD 16:00] "rejected" only for a status + code pair this endpoint's handler returns before anything is committed.
  const codes = WRITE_REJECTIONS[okStatus === 201 ? "POST" : "PUT"][r.status];
  if (codes && r.json && r.body && codes.includes(r.body.error)) return { kind: "rejected" };
  return { kind: "unknown" };
}

/** [TLD 2026-11-05 07:54] GET /orgs/locations/{id}/login-config/changes (redirect: "manual", bounded like the location GET).
 *  "unavailable": timeout, opaqueredirect / followed redirect / 3xx, or a 200 that is not JSON -> the softer history copy
 *    "Couldn't load history — try again" (brief item 2; not the session copy).
 *  "ok": a JSON 200 with an items array -> { items }.
 *  "status": anything else (403 = org:V reader per C2, 401, 404, 5xx, a network failure, a JSON 200 without items):
 *    handled from the status as before. */
export function historyOutcome(r) {
  if (r.timedOut) return { kind: "unavailable" };
  if (r.status !== null && (r.opaque || r.redirected || (r.status >= 300 && r.status < 400))) return { kind: "unavailable" };
  if (r.status === 200 && (!r.json || r.body === null)) return { kind: "unavailable" }; // non-JSON or unparseable [TLD 15:08 item 2]
  if (r.status === 200 && r.body && Array.isArray(r.body.items)) return { kind: "ok", items: r.body.items };
  return { kind: "status" };
}

/** [DEC-144] The create guard value is only { attemptId, startedAt }. A timestamp-only or other value is a legacy
 *  guard: it still blocks, and it cannot be acknowledged (there is no attempt id to match). */
export function parseCreateAttempt(raw) {
  if (typeof raw !== "string" || raw.length === 0) return null;
  let o;
  try { o = JSON.parse(raw); } catch { return { legacy: true }; }
  if (!o || typeof o !== "object" || Array.isArray(o)) return { legacy: true };
  if (typeof o.attemptId !== "string" || !UUID.test(o.attemptId) || typeof o.startedAt !== "string" || o.startedAt.length === 0) return { legacy: true };
  return { attemptId: o.attemptId, startedAt: o.startedAt };
}

/** [DEC-144] Clear only when the stored attempt is the one this page showed. An older acknowledgement must not
 *  clear a newer or different attempt. */
export function ackClearsAttempt(storedRaw, shownAttemptId) {
  const p = parseCreateAttempt(storedRaw);
  return !!(p && !p.legacy && typeof shownAttemptId === "string" && shownAttemptId.length > 0 && p.attemptId === shownAttemptId);
}

/** [DEC-144 gap 1] Who may acknowledge, from GET /orgs/partners/{id}/login-config/reason-bounds only.
 *  partnerId is the partner this page asked about (the URL). It is not a role and not data-admin-type.
 *  "allow": JSON 200 object, bounds_editor === true, and body.partner_id is that same partner.
 *    bounds_editor is ShowsBoundsEditor (Super Admin, or Partner Admin of THIS partner) from the validated token.
 *    It is set whether or not a reason-bounds row exists. updated_by is the row's audit identity, not this proof.
 *  "unproven": a JSON 200 object that does not carry that proof. False, a non-boolean, a missing flag, a different
 *    partner_id, or updated_by with no bounds_editor. The page does not treat that as permission.
 *  "session": 401. Authoritative session expiry for this read.
 *  "deny": 403 / 404 (not an admin, or not allowed to read this partner).
 *  "unknown": timeout, transport failure, 3xx / opaque redirect, or a non-object 200. Not session expiry. */
export function createAckAuthority(r, partnerId) {
  if (!r || r.timedOut || r.status === null) return { kind: "unknown" };
  if (r.opaque || r.redirected || (r.status >= 300 && r.status < 400)) return { kind: "unknown" };
  if (r.status === 401) return { kind: "session" };
  if (r.status === 403 || r.status === 404) return { kind: "deny" };
  if (r.status === 200 && r.json && r.body && typeof r.body === "object" && !Array.isArray(r.body)) {
    const same = typeof partnerId === "string" && UUID.test(partnerId) && r.body.partner_id === partnerId;
    if (r.body.bounds_editor === true && same) return { kind: "allow" };
    return { kind: "unproven" };
  }
  return { kind: "unknown" };
}
