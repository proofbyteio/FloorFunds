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

/** 422 field path -> the page's input id (spec §5 example: "entra_allowed_tenant_ids/0"). */
export function fieldTarget(path) {
  // DEC-107: a create 422 names the reason "login_config/reason"; it lands on the same field as the PUT's "reason".
  // (Other login_config/* create paths are unchanged here: outside DEC-107's scope, reported as an open question.)
  if (String(path) === "login_config/reason") return "reason";
  const top = String(path).split("/")[0];
  return ({ display_name: "dname", entra_allowed_tenant_ids: "tenant", reason: "reason", name: "lname",
    allow_db_fallback: "fallback", mfa_required: "mfa", provider_kind: "method" })[top] ?? null;
}

/** Strong ETag "<n>" -> If-Match value; anything else -> null (never invent one). */
export function ifMatchFrom(etag, rowVersion) {
  if (typeof etag === "string" && /^"\d+"$/.test(etag)) return etag;
  return Number.isInteger(rowVersion) ? `"${rowVersion}"` : null;
}

/** DEC-107 [TLD 2026-11-04 16:32 Q2]: the reason bounds from GET /orgs/login-config/limits or the location GET
 *  (reason_min_length / reason_max_length) -> { min, max }, or null when missing or not 1 <= min <= max integers.
 *  The page never falls back to numbers of its own. */
export function limitsFrom(o) {
  const min = o && o.reason_min_length, max = o && o.reason_max_length;
  return Number.isInteger(min) && Number.isInteger(max) && min >= 1 && min <= max ? { min, max } : null;
}
