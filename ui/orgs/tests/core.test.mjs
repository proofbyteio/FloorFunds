// node --test ui/orgs/tests/core.test.mjs  (node built-ins only; no packages)
import test from "node:test";
import assert from "node:assert/strict";
import { parseRoute, viewerKind, createKind, isDowngrade, buildWrite, buildCreate, formatPT, changeDetails, fieldTarget,
  ifMatchFrom, fallbackText, limitsFrom, limitsOrFallback, REASON_BOUNDS, partnerMfaState, PARTNER_MFA_OFF_HINT,
  errIdFor, LIMITS_TIMEOUT_MS, limitsTimeout, limitsOutcome, locationOutcome, writeOutcome, historyOutcome, CONTRACT_ERROR_CODES, WRITE_REJECTIONS } from "../js/llc-core.mjs";
import { TOP_LEVEL, FETCH_COPY, FIELD, requestIdText, reasonLengthCopy, REASON_HINT_FALLBACK, REASON_LENGTH_FALLBACK,
  REASON_HINT_FALLBACK_EDIT, reasonHintFallback } from "../js/error-copy.mjs";

const P = "5f0c3e1a-0001-4000-8000-000000000001", L = "3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8";

test("route: edit, create, prefix, rejects", () => {
  assert.deepEqual(parseRoute(`/orgs/${P}/locations/${L}/sign-in`), { mode: "edit", partnerId: P, locationId: L });
  assert.deepEqual(parseRoute(`/as/sa/orgs/${P}/locations/new`), { mode: "create", partnerId: P, locationId: null });
  assert.equal(parseRoute(`/orgs/${P}/locations/new/sign-in`), null);
  assert.equal(parseRoute(`/orgs/example/locations/${L}/sign-in`), null);
  assert.equal(parseRoute(`/orgs/${P}/locations/${L}`), null);
  assert.equal(parseRoute(`/orgs/${P}/locations/x/sign-in`), null);
});

test("viewer kind follows the API (secret key = super; history 200/403)", () => {
  assert.equal(viewerKind({ secret: { set: false } }, 200), "super");
  assert.equal(viewerKind({}, 200), "partner");
  assert.equal(viewerKind({}, 403), "viewonly");
  assert.equal(viewerKind({}, 404), "unknown");
  assert.equal(viewerKind({}, null), "unknown");
  assert.equal(createKind("super"), "super");
  for (const v of ["", undefined, "partner", "SUPER"]) assert.equal(createKind(v), "partner");
});

test("downgrade (DEC-096/098) incl. create vs none (A-5)", () => {
  const b = { mfa_required: true, allow_db_fallback: false };
  assert.equal(isDowngrade(b, { mfa_required: false, allow_db_fallback: false }), true);
  assert.equal(isDowngrade(b, { mfa_required: true, allow_db_fallback: true }), true);
  assert.equal(isDowngrade(b, { mfa_required: true, allow_db_fallback: false }), false);
  assert.equal(isDowngrade({ mfa_required: false, allow_db_fallback: true }, { mfa_required: true, allow_db_fallback: false }), false);
  assert.equal(isDowngrade({ mfa_required: false, allow_db_fallback: false }, { mfa_required: false, allow_db_fallback: false }), false);
  assert.equal(isDowngrade(null, { mfa_required: true, allow_db_fallback: false }), false);
  assert.equal(isDowngrade(null, { mfa_required: false, allow_db_fallback: false }), true);
});

test("write body: exactly the six client keys, never oidc_* or secret_ref", () => {
  const w = buildWrite({ method: "entra", displayName: "X", tenant: " 9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d ", mfa: true, fallback: true, reason: "  " });
  assert.deepEqual(Object.keys(w).sort(), ["allow_db_fallback", "display_name", "entra_allowed_tenant_ids", "mfa_required", "provider_kind", "reason"]);
  assert.deepEqual(w.entra_allowed_tenant_ids, ["9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d"]);
  assert.equal(w.reason, null);
  const d = buildWrite({ method: "database", displayName: "X", tenant: "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d", mfa: true, fallback: true, reason: "because of ticket 1" });
  assert.deepEqual(d.entra_allowed_tenant_ids, []);
  assert.equal(d.allow_db_fallback, false);
  assert.equal(d.reason, "because of ticket 1"); // sent as typed; the server trims (spec §3.2)
  const c = buildCreate(P, "Cage 2", { method: "database", displayName: "Y", mfa: true, fallback: false, reason: "" });
  assert.deepEqual(Object.keys(c).sort(), ["login_config", "name", "partner_id"]);
  assert.equal(c.partner_id, P);
  assert.equal("reason" in c.login_config, false);
  for (const o of [w, d, c.login_config]) for (const k of ["oidc_authority", "oidc_client_id", "secret_ref", "secret", "row_version"]) assert.equal(k in o, false);
});

test("history formatting: PT and actor-id details", () => {
  assert.equal(formatPT("2026-10-28T22:12:00Z"), "Oct 28, 2026, 3:12 PM");
  assert.equal(formatPT("2026-11-02T19:41:00Z"), "Nov 2, 2026, 11:41 AM");
  assert.equal(formatPT("nope"), "");
  assert.equal(changeDetails({ change_kind: "created", after: { provider_kind: "database", mfa_required: true } }), "Created: database login, second step on");
  assert.equal(changeDetails({ change_kind: "updated", changed_fields: ["provider_kind", "entra_allowed_tenant_ids", "oidc_authority"],
    before: { provider_kind: "database", mfa_required: true, allow_db_fallback: false },
    after: { provider_kind: "entra", mfa_required: true, allow_db_fallback: false } }),
    "Database → Microsoft; changed: tenant ID, Microsoft sign-in address");
  assert.equal(fallbackText({ provider_kind: "database" }), "Not used (database location)");
});

test("422 field mapping, If-Match", () => {
  assert.equal(fieldTarget("entra_allowed_tenant_ids/0"), "tenant");
  assert.equal(fieldTarget("reason"), "reason");
  assert.equal(fieldTarget("login_config/reason"), "reason"); // DEC-107: the create path
  assert.equal(fieldTarget("oidc_authority"), null);
  // P4h [TLD 2026-11-04 16:56 Q2]: every create login_config/* write field lands where the PUT name does.
  for (const [k, id] of [["provider_kind", "method"], ["display_name", "dname"], ["entra_allowed_tenant_ids", "tenant"],
    ["entra_allowed_tenant_ids/0", "tenant"], ["mfa_required", "mfa"], ["allow_db_fallback", "fallback"], ["reason", "reason"]]) {
    assert.equal(fieldTarget(k), id, k);
    assert.equal(fieldTarget("login_config/" + k), id, "login_config/" + k);
  }
  assert.equal(fieldTarget("name"), "lname");
  for (const k of ["partner_id", "login_config", "login_config/name", "login_config/oidc_authority", "login_config/oidc_client_id",
    "login_config/secret_ref", "login_config/*", "login_config/constructor", "constructor", "__proto__", "*", "login_config/login_config/reason"])
    assert.equal(fieldTarget(k), null, k);
  assert.equal(ifMatchFrom('"7"', 7), '"7"');
  assert.equal(ifMatchFrom('W/"7"', 7), '"7"');
  assert.equal(ifMatchFrom(null, 3), '"3"');
  assert.equal(ifMatchFrom(null, "3"), null);
});

test("approved D-102 copy: every top-level and field entry has text; request id display", () => {
  for (const [k, v] of Object.entries(TOP_LEVEL)) { assert.ok(v.title && v.text, k); }
  for (const [k, v] of Object.entries(FIELD)) assert.ok(v, k);
  assert.equal(FIELD.reason_length, undefined, "DEC-107: reason_length copy comes from reasonLengthCopy(min, max), no fixed text");
  assert.equal(requestIdText("abc"), "Request ID: abc");
  assert.equal(requestIdText(null), "");
  // D-102 exact changes (review md5 8c7d5274)
  assert.equal(TOP_LEVEL.not_found.text, "It may have been removed, or you may not have access to it.");
  assert.equal(TOP_LEVEL.not_super_admin.title, "Only a Super Admin can make this change.");
  assert.equal(TOP_LEVEL.not_super_admin.text, "Nothing was saved. Turning off the second step or allowing backup sign-in needs a Super Admin.");
});

test("DEC-107: reason length copy and limits from the API, no fixed numbers", () => {
  assert.equal(reasonLengthCopy(10, 500), "Enter a reason of 10 to 500 characters.");
  assert.equal(reasonLengthCopy(50, 200), "Enter a reason of 50 to 200 characters.");
  assert.deepEqual(limitsFrom({ reason_min_length: 50, reason_max_length: 200 }), { min: 50, max: 200 });
  assert.deepEqual(limitsFrom({ reason_min_length: 10, reason_max_length: 500, other: 1 }), { min: 10, max: 500 });
  for (const bad of [null, undefined, {}, { reason_min_length: 10 }, { reason_min_length: "10", reason_max_length: 500 }, { reason_min_length: 10.5, reason_max_length: 500 },
    { reason_min_length: 300, reason_max_length: 200 }, { reason_min_length: 0, reason_max_length: 200 },
    { reason_min_length: 9, reason_max_length: 200 }, { reason_min_length: 10, reason_max_length: 501 }, { reason_min_length: 1, reason_max_length: 500 }])
    assert.equal(limitsFrom(bad), null, JSON.stringify(bad));
});
test("TLD 17:28: limits fallback = the server's hard bounds 10 / 500 (llcc_reason, startup check)", () => {
  assert.deepEqual(REASON_BOUNDS, { min: 10, max: 500 });
  assert.ok(Object.isFrozen(REASON_BOUNDS));
  for (const bad of [null, undefined, {}, { reason_min_length: "10", reason_max_length: 500 }, { reason_min_length: 10.5, reason_max_length: 500 },
    { reason_min_length: 5, reason_max_length: 500 }, { reason_min_length: 10, reason_max_length: 600 }, { reason_min_length: 300, reason_max_length: 200 }])
    assert.deepEqual(limitsOrFallback(bad), { min: 10, max: 500 }, JSON.stringify(bad));
  assert.deepEqual(limitsOrFallback({ reason_min_length: 50, reason_max_length: 200 }), { min: 50, max: 200 });
  assert.deepEqual(limitsOrFallback({ reason_min_length: 10, reason_max_length: 500 }), { min: 10, max: 500 });
});

test("DEC-106 / D-106: partner MFA state from the SAVED value; upgrade is not a downgrade", () => {
  assert.equal(partnerMfaState(null), "partner-create");
  assert.equal(partnerMfaState({ mfa_required: false, allow_db_fallback: false }), "partner-mfa-off");
  assert.equal(partnerMfaState({ mfa_required: true, allow_db_fallback: false }), "partner-edit");
  assert.equal(PARTNER_MFA_OFF_HINT, "Off at this location now. Turn it on to add a second step at sign-in. Once saved, only a Super Admin can turn it off again.");
  assert.equal(isDowngrade({ mfa_required: false, allow_db_fallback: false }, { mfa_required: true, allow_db_fallback: false }), false);
});

test("TLD 18:35 LOW-1: field -> error slot (setFieldError and clearFieldError share it; mfa -> fallback-err)", () => {
  for (const [id, e] of [["lname", "lname-err"], ["dname", "dname-err"], ["tenant", "tenant-err"], ["reason", "reason-err"], ["fallback", "fallback-err"], ["mfa", "fallback-err"]])
    assert.equal(errIdFor(id), e, id);
});
test("TLD 18:35 items 2 / 4 / 5: limits answer -> page error, server limits or fallback; 10 s timeout", () => {
  const j = (status, body, extra = {}) => ({ status, body, json: true, redirected: false, ...extra });
  assert.deepEqual(limitsOutcome(j(401, null)), { kind: "error" });
  assert.deepEqual(limitsOutcome(j(403, { error: "forbidden" })), { kind: "error" });
  assert.deepEqual(limitsOutcome(j(200, { reason_min_length: 10, reason_max_length: 500 }, { redirected: true })), { kind: "error401" });
  for (const s of [300, 302, 303, 304, 307, 308]) assert.deepEqual(limitsOutcome(j(s, null)), { kind: "error401" }, String(s));
  // [TLD 2026-11-05 15:08 item 2] a non-JSON 200 is an invalid answer (page error), not a verified 401 (was error401).
  assert.deepEqual(limitsOutcome({ status: 200, body: null, json: false, redirected: false }), { kind: "error" });
  assert.deepEqual(limitsOutcome({ status: null, body: null, json: false, redirected: false }), { kind: "fallback" }); // network / timeout
  assert.deepEqual(limitsOutcome(j(500, { error: "internal_error" })), { kind: "fallback" });
  assert.deepEqual(limitsOutcome(j(404, null)), { kind: "fallback" });
  assert.deepEqual(limitsOutcome(j(200, {})), { kind: "fallback" });
  assert.deepEqual(limitsOutcome(j(200, null)), { kind: "fallback" }); // JSON content-type, unparseable body
  assert.deepEqual(limitsOutcome(j(200, { reason_min_length: 5, reason_max_length: 500 })), { kind: "fallback" });
  assert.deepEqual(limitsOutcome(j(200, { reason_min_length: 50, reason_max_length: 200 })), { kind: "server", limits: { min: 50, max: 200 } });
  assert.equal(LIMITS_TIMEOUT_MS, 10000);
  assert.equal(limitsTimeout(undefined), 10000);
  assert.equal(limitsTimeout(300), 300);
  for (const bad of [0, -1, 1.5, "300", null]) assert.equal(limitsTimeout(bad), 10000, String(bad));
});
test("UX D-108 (TLD 18:37): fallback copy has no numbers; server copy keeps them", () => {
  assert.equal(REASON_HINT_FALLBACK, "Saved in the audit log. In Create: required only if you turn off the second step or turn on backup sign-in.");
  assert.equal(REASON_LENGTH_FALLBACK, "This reason is too short or too long. Edit it and try again.");
  assert.ok(!/\d/.test(REASON_HINT_FALLBACK + REASON_LENGTH_FALLBACK));
  assert.equal(reasonLengthCopy(50, 200), "Enter a reason of 50 to 200 characters.");
});
test("UX D-109 (TLD 20:18 / 21:11): fallback hint is short on Edit, full D-108 hint on Create; 422 fallback copy unchanged", () => {
  assert.equal(REASON_HINT_FALLBACK_EDIT, "Saved in the audit log.");
  assert.equal(reasonHintFallback(false), "Saved in the audit log.");
  assert.equal(reasonHintFallback(true), "Saved in the audit log. In Create: required only if you turn off the second step or turn on backup sign-in.");
  assert.equal(REASON_LENGTH_FALLBACK, "This reason is too short or too long. Edit it and try again.");
});
test("TLD 21:11 Q2 / Q3: opaqueredirect -> 401 page error (limits and location GET); location GET timeout -> page error", () => {
  const j = (status, body, extra = {}) => ({ status, body, json: true, redirected: false, opaque: false, ...extra });
  const opq = { status: 0, body: null, json: false, redirected: false, opaque: true }; // fetch redirect: "manual", any 3xx
  assert.deepEqual(limitsOutcome(opq), { kind: "error401" });
  assert.deepEqual(locationOutcome(opq), { kind: "error401" });
  assert.deepEqual(locationOutcome({ status: null, body: null, json: false, redirected: false, opaque: false }), { kind: "error" }); // network / timeout
  assert.deepEqual(locationOutcome(j(200, { name: "L1" }, { redirected: true })), { kind: "error401" });
  for (const s of [300, 302, 303, 307, 308]) assert.deepEqual(locationOutcome(j(s, null)), { kind: "error401" }, String(s));
  assert.deepEqual(locationOutcome({ status: 200, body: null, json: false, redirected: false, opaque: false }), { kind: "error" }); // [TLD 15:08 item 2] was error401
  assert.deepEqual(locationOutcome({ status: 200, body: { name: "L1" }, json: false, redirected: false, opaque: false }), { kind: "error" });
  for (const s of [401, 403, 404, 500]) assert.deepEqual(locationOutcome(j(s, { error: "x" })), { kind: "error" }, String(s));
  assert.deepEqual(locationOutcome(j(200, null)), { kind: "error" });
  assert.deepEqual(locationOutcome(j(200, { name: "L1" })), { kind: "ok" });
});

// [TLD 2026-11-05 07:54 fetch hardening, brief a451c6cf] Edit / write 307, 401 and non-JSON cases are unit-only here (TL DQ-2).
const ans = (status, body, extra = {}) => ({ status, body, json: true, redirected: false, opaque: false, timedOut: false, ...extra });
const TIMEOUT = { status: null, body: null, json: false, redirected: false, opaque: false, timedOut: true };
const NETWORK = { ...TIMEOUT, timedOut: false };
const OPAQUE = { status: 0, body: null, json: false, redirected: false, opaque: true, timedOut: false }; // redirect: "manual", any 3xx
test("TLD 16:00 (supersedes 15:08 / 07:54): write answers: ok only with a usable body; 401 -> session; 3xx -> UNKNOWN + session; only this endpoint's pre-commit status+code -> rejected; all else UNKNOWN", () => {
  const PUT_OK = { row_version: 2, name: "L1" }, POST_OK = { location_id: "3c2b1a09-8f7e-4d6c-a5b4-c3d2e1f0a9b8" };
  for (const [ok, good] of [[200, PUT_OK], [201, POST_OK]]) {
    assert.deepEqual(writeOutcome(ans(ok, good), ok), { kind: "ok" });
    assert.deepEqual(writeOutcome(TIMEOUT, ok), { kind: "unknown" }, "abort (headers or body)");
    assert.deepEqual(writeOutcome(NETWORK, ok), { kind: "unknown" }, "non-timeout network failure after dispatch");
    assert.deepEqual(writeOutcome({ ...ans(ok, good), timedOut: true }, ok), { kind: "unknown" }, "a late answer after the abort never counts");
    assert.deepEqual(writeOutcome(ans(ok, null, { json: false }), ok), { kind: "unknown" }, "non-JSON 2xx: invalid answer, not a verified 401");
    assert.deepEqual(writeOutcome(ans(ok, null), ok), { kind: "unknown" }, "JSON 2xx that did not parse");
    assert.deepEqual(writeOutcome(ans(ok, {}), ok), { kind: "unknown" }, "2xx without the required fields");
    assert.deepEqual(writeOutcome(ans(204, null, { json: false }), ok), { kind: "unknown" });
    const U3 = { kind: "unknown", session: true }; // [TLD 16:00] locked like any UNKNOWN, plus sign in again
    assert.deepEqual(writeOutcome(OPAQUE, ok), U3);
    for (const s of [300, 301, 302, 303, 307, 308]) assert.deepEqual(writeOutcome(ans(s, null, { json: false }), ok), U3, String(s));
    assert.deepEqual(writeOutcome(ans(ok, good, { redirected: true }), ok), U3);
    assert.deepEqual(writeOutcome(ans(401, null, { json: false }), ok), { kind: "session" }, "401 (spec: no body)");
    const both = [[400, "malformed_body"], [400, "reason_required"], [403, "forbidden"], [403, "not_super_admin"], [404, "not_found"],
      [408, "request_timeout"], [413, "payload_too_large"], [422, "validation_failed"]];
    const putOnly = [[400, "if_match_invalid"], [409, "concurrency_conflict"], [428, "precondition_required"]];
    for (const [st, code] of ok === 200 ? [...both, ...putOnly] : both)
      assert.deepEqual(writeOutcome(ans(st, { error: code }), ok), { kind: "rejected" }, `${st} ${code}`);
    if (ok === 201) for (const [st, code] of putOnly) assert.deepEqual(writeOutcome(ans(st, { error: code }), ok), { kind: "unknown" }, `POST never returns ${st} ${code}`);
    for (const [st, code] of [[400, "forbidden"], [403, "validation_failed"], [409, "not_found"], [422, "malformed_body"], [500, "validation_failed"], [403, "unauthenticated"]])
      assert.deepEqual(writeOutcome(ans(st, { error: code }), ok), { kind: "unknown" }, `${st} ${code}: code under the wrong status is not authoritative`);
    assert.deepEqual(writeOutcome(ans(403, { error: "forbidden" }, { json: false }), ok), { kind: "unknown" }, "non-JSON 4xx");
    for (const r of [ans(404, null, { json: false }), ans(429, { error: "rate_limited" }), ans(403, { error: "x" }), ans(500, null, { json: false }), ans(500, { error: "x" }),
      ans(502, null, { json: false }), ans(503, null, { json: false }), ans(504, null, { json: false })])
      assert.deepEqual(writeOutcome(r, ok), { kind: "unknown" }, `${r.status} not a contract rejection`);
  }
  assert.deepEqual(writeOutcome(ans(200, POST_OK), 201), { kind: "unknown" }, "POST needs 201");
  assert.deepEqual(writeOutcome(ans(201, PUT_OK), 200), { kind: "unknown" }, "PUT needs 200");
  assert.deepEqual(writeOutcome(ans(201, { location_id: "not-a-uuid" }), 201), { kind: "unknown" });
  assert.deepEqual(writeOutcome(ans(200, { row_version: "2" }), 200), { kind: "unknown" });
  for (const op of ["PUT", "POST"]) for (const codes of Object.values(WRITE_REJECTIONS[op])) for (const c of codes) assert.ok(CONTRACT_ERROR_CODES.includes(c), c);
  assert.deepEqual([...CONTRACT_ERROR_CODES].sort(), ["concurrency_conflict", "forbidden", "if_match_invalid", "malformed_body", "not_found", "not_super_admin",
    "payload_too_large", "precondition_required", "reason_required", "request_timeout", "unauthenticated", "validation_failed"]);
});
test("TLD 07:54: history answers: timeout / 3xx / redirect / non-JSON 200 -> unavailable; 200 items -> ok; the rest by status (C2)", () => {
  assert.deepEqual(historyOutcome(TIMEOUT), { kind: "unavailable" });
  assert.deepEqual(historyOutcome(OPAQUE), { kind: "unavailable" });
  for (const s of [301, 302, 303, 307, 308]) assert.deepEqual(historyOutcome(ans(s, null, { json: false })), { kind: "unavailable" }, String(s));
  assert.deepEqual(historyOutcome(ans(200, { items: [] }, { redirected: true })), { kind: "unavailable" });
  assert.deepEqual(historyOutcome(ans(200, null, { json: false })), { kind: "unavailable" });
  assert.deepEqual(historyOutcome(ans(200, null)), { kind: "unavailable" }, "unparseable JSON 200 [TLD 15:08 item 2]");
  assert.deepEqual(historyOutcome(ans(200, { items: [{ change_id: "c" }] })), { kind: "ok", items: [{ change_id: "c" }] });
  assert.deepEqual(historyOutcome(ans(200, { items: [] })), { kind: "ok", items: [] });
  assert.deepEqual(historyOutcome(NETWORK), { kind: "status" }, "network failure: as before (D-102 network copy)");
  for (const s of [401, 403, 404, 500]) assert.deepEqual(historyOutcome(ans(s, { error: "x" })), { kind: "status" }, String(s));
  assert.deepEqual(historyOutcome(ans(200, { other: 1 })), { kind: "status" }, "JSON 200 without items: as before");
});
test("TLD 15:08 copy: exact Edit uncertain copy; Create blocked (no reload advice); no 'Nothing was saved' in shared session / transport / generic copy", () => {
  const w = FETCH_COPY.write_unconfirmed;
  assert.equal(`${w.title} ${w.text}`, "We couldn't confirm the save. Reload to check before trying again.");
  assert.deepEqual(FETCH_COPY.create_unconfirmed_blocked, { title: "We couldn\u2019t confirm the create.", text: "" });
  assert.ok(!/reload|check|try again/i.test(FETCH_COPY.create_unconfirmed_blocked.title), "no unsupported recovery advice");
  assert.equal(FETCH_COPY.history_unavailable.title, "Couldn't load history — try again");
  assert.equal(FETCH_COPY.history_unavailable.text, "");
  assert.deepEqual(TOP_LEVEL[401], { title: "Your session has ended.", text: "Sign in again, then try again." });
  assert.deepEqual(TOP_LEVEL.network, { title: "Couldn't reach the server.", text: "Check your connection, then try again." });
  assert.deepEqual(TOP_LEVEL[500], { title: "Something went wrong on our side.", text: "Try again in a few minutes." });
  assert.deepEqual(TOP_LEVEL.other, { title: "Something went wrong.", text: "Try again." });
  for (const k of ["401", "network", "500", "other"]) assert.ok(!/nothing was saved/i.test(TOP_LEVEL[k].title + TOP_LEVEL[k].text), k);
  for (const c of Object.values(FETCH_COPY)) assert.ok(!/nothing was saved|saved\./i.test(c.title + c.text));
  for (const k of Object.keys(FETCH_COPY)) assert.equal(TOP_LEVEL[k], undefined, `${k}: not an API code / D-102 key`);
});
test("TLD 16:47 (supersedes 16:00 strings): exact provisional sign-in-again + UNKNOWN copy, no unverified cause", () => {
  const j = (c) => `${c.title} ${c.text}`;
  assert.equal(j(FETCH_COPY.write_unconfirmed_session), "We couldn\u2019t confirm the save. Sign in again, then reload to check before trying again.");
  assert.equal(j(FETCH_COPY.create_unconfirmed_blocked_session), "We couldn\u2019t confirm whether the location was created. Sign in again. Creating another location remains blocked until the result is confirmed.");
  for (const k of ["write_unconfirmed_session", "create_unconfirmed_blocked_session"]) assert.ok(!/session has ended|nothing was saved|try again\.$/i.test(j(FETCH_COPY[k])), k);
  assert.equal(j(TOP_LEVEL["401"]), "Your session has ended. Sign in again, then try again.", "a real 401 keeps its verified-auth copy");
  assert.equal(j(FETCH_COPY.write_unconfirmed), "We couldn't confirm the save. Reload to check before trying again.", "timeout copy unchanged (exact)");
});
