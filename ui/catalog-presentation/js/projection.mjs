/**
 * Synthetic fixture projection of the accepted catalog contract.
 * This module does not execute the isolated C# service, an HTTP API, or a database.
 * Outcomes use the accepted status and gate names. proofBoundary stays distinct
 * so a result is not presented as an integrated double run.
 */
import { ACTORS, CLAIM_EDIT_KEYS, seedEntries } from "./catalogs.mjs";

export const PROOF_BOUNDARY = "synthetic_fixture_projection_not_the_dotnet_double_api_or_database";
export const INTENT_STATUS = "double_recorded_not_durable";
export const REJECTED_SENTINEL = "1.2.0-catalog-unapproved";
export const PARTNER_SYNTAX_ONLY = "syntax_only_not_membership_or_existence";
export const DOUBLE_INPUT_CAP = 4000;

export const GATES = {
  claimReactivation: "claim_reactivation_pending_pq272",
  graphOff: "graph_unconfigured_all_off",
  graphRestore: "graph_dependent_restore_unavailable",
  createUnresolved: "create_dto_unresolved",
  privateCode: "private_code_policy_unresolved",
  privatePartnerSwitch: "private_partner_switch_representation_unresolved",
  fixtureDoesNotAuthorize: "fixture_graph_does_not_authorize",
  saPrivateVisibility: "sa_private_non_claim_visibility_unresolved",
  fieldGate: "pq252_sa1_field_gate",
};

export const OPEN_POLICIES = {
  claimReactivation: "PQ-272",
  graphEdges: "PQ-263.1",
  privateLocationOff: "PQ-271",
  saPrivateMetadata: "PQ-252.SA2",
  privatePartnerSwitch: "PQ-252.SA3",
  privateCode: "PQ-252.SA6",
  fieldApproval: "PQ-252.SA1",
  readAudience: "PQ-252.SA10",
};

const FAMILIES = [
  "catalog_release", "transaction_type", "channel", "device_category", "manufacturer",
  "device_model", "hardware_capability_kind", "model_hardware_claim", "card_network",
  "card_funding_class", "payment_rail", "check_instrument_kind", "check_drawer_class",
  "check_purpose", "processor", "transaction_status", "reason_code", "id_document_type",
  "currency", "fee_type",
];

const PA_ADD = new Set(["manufacturer", "device_model", "processor", "reason_code", "id_document_type", "fee_type"]);
const FEE_KEYS = ["code", "display_name", "description"];
const ACTIVATION_KEYS = ["desired_active", "reason", "expected_version"];
const AUTHORITY = new Set([
  "actor", "actor_user_id", "user_id", "admin_type", "on_behalf_of", "impersonate",
  "delegated_actor", "owner_partner_id", "owner", "acting_as", "partner_header",
  "schema_version", "idempotency_key", "correlation_id", "source_request_id",
]);
const PROTECTED_CLAIM = new Set([
  "entry_id", "claim_id", "id", "family", "entry_class", "scope_kind", "code",
  "owner_partner_id", "model_id", "capability_entry_id", "option_key", "source_version",
  "is_active", "row_version", "entry_version", "created_at", "updated_at",
  "actor_user_id", "admin_type", "economic_role", "classification",
]);
const SENSITIVE = ["pan", "pin", "cvv", "micr", "secret", "password", "credential"];

export function tryParseFamily(wire) {
  return FAMILIES.includes(wire);
}

export function disposition(wire) {
  if (wire === "fee_type") return "names_only";
  if (wire === "catalog_release" || wire === "id_document_type" || wire === "currency") return "required_fields_unresolved";
  if (FAMILIES.includes(wire)) return "create_fields_not_closed";
  return null;
}

export function createSession() {
  const entries = new Map();
  for (const row of seedEntries()) entries.set(row.entryId, clone(row));
  return {
    entries,
    history: [],
    intents: [],
    idempotency: new Map(),
    idCounter: 1,
    clockMs: Date.parse("2026-11-08T19:55:00.000Z"),
    versionComparisons: 0,
  };
}

export function actorByKey(key) {
  const actor = ACTORS[key];
  if (!actor) throw new Error("unknown synthetic actor");
  return { ...actor };
}

export function isManagement(actor) {
  return actor.role === "super_admin" || actor.role === "partner_admin";
}

export function listEntries(session, actor, family) {
  if (!tryParseFamily(family)) return refuse(400, "invalid_family", "invalid_family");
  if (!isManagement(actor)) return refuse(403, "no_management_grant", "no_management_grant");
  const entries = [...session.entries.values()]
    .filter((entry) => entry.family === family && visible(actor, entry))
    .map((entry) => project(actor, entry));
  return { ...ok("ok"), list: { entries, pagination: "not_a_cursor_contract" } };
}

export function readEntry(session, actor, family, entryId) {
  const entry = session.entries.get(entryId);
  if (!visible(actor, entry) || entry.family !== family) return notFound();
  return { ...ok("ok"), entry: project(actor, entry) };
}

export function readHistory(session, actor, family, entryId) {
  const entry = session.entries.get(entryId);
  if (!visible(actor, entry) || entry.family !== family) return notFound();
  const history = session.history
    .filter((row) => row.entryId === entryId)
    .map((row) => ({
      changeId: row.changeId,
      entryId: row.entryId,
      changeKind: row.changeKind,
      entryVersion: row.entryVersion,
      changedFields: row.changedFields,
      before: row.before,
      after: row.after,
      occurredAt: row.occurredAt,
    }));
  return { ...ok("ok"), history };
}

export function preview(session, actor, family, entryId, graph) {
  const entry = session.entries.get(entryId);
  if (!visible(actor, entry) || entry.family !== family) return notFound();
  return { ...ok("ok"), wrote: false, preview: readPreview(graph ?? { state: "unconfigured" }) };
}

export function describeGraph(graph) {
  const view = graph ?? { state: "unconfigured" };
  const reading = readPreview(view);
  return {
    ...ok("ok"),
    wrote: false,
    graph: {
      state: view.state === "fixture" ? "fixture" : "unconfigured",
      impact: reading.impact,
      affectedCount: reading.affectedCount,
      applyAllowed: false,
      fixtureRevision: reading.fixtureRevision,
      fixtureDeclaredEdgeCount: reading.fixtureDeclaredEdgeCount,
      runtimeConfiguration: false,
      authorizesWrites: false,
    },
  };
}

export function submitCreate(session, actor, call, family, json) {
  const parsed = parseCreate(family, json);
  if (parsed.kind === "rejected") return refuse(parsed.status, parsed.code, parsed.detail);
  if (parsed.kind === "unavailable") return unavailable(GATES.createUnresolved, parsed.detail);
  return withKey(session, actor, call, "fee_create", "fee_type", null, null, parsed.body, () => createFee(session, actor, call, parsed));
}

export function submitClaimEdit(session, actor, call, family, entryId, json) {
  if (family !== "model_hardware_claim") return refuse(400, "invalid_family", "claim_edit_family");
  const parsed = parseClaimEdit(entryId, json);
  if (parsed.kind === "rejected") return refuse(parsed.status, parsed.code, parsed.detail);
  return withKey(session, actor, call, "claim_edit", "model_hardware_claim", entryId, parsed.expected, parsed.body, () =>
    editClaimPresentation(session, actor, call, parsed));
}

export function submitEntryActivation(session, actor, call, family, entryId, json) {
  const parsed = parseActivation(family, json);
  if (parsed.kind === "rejected") return refuse(parsed.status, parsed.code, parsed.detail);
  return activate(session, actor, family, entryId, parsed.desired, null, null, true);
}

export function submitPartnerActivation(session, actor, call, family, entryId, partnerText, json) {
  if (partnerText == null || String(partnerText).trim() === "") return refuse(400, "invalid_partner_uuid", "partner_id");
  const parsed = parseActivation(family, json);
  if (parsed.kind === "rejected") return refuse(parsed.status, parsed.code, parsed.detail);
  return activate(session, actor, family, entryId, parsed.desired, String(partnerText), null, false);
}

export function submitLocationActivation(session, actor, call, family, entryId, locationText, json) {
  if (!isGuid(locationText)) return refuse(400, "invalid_location_uuid", "location_id");
  const parsed = parseActivation(family, json);
  if (parsed.kind === "rejected") return refuse(parsed.status, parsed.code, parsed.detail);
  return activate(session, actor, family, entryId, parsed.desired, null, String(locationText), false);
}

export function sameRefusal(left, right) {
  return left.status === right.status
    && left.code === right.code
    && left.detail === right.detail
    && left.wrote === right.wrote
    && (left.gate ?? null) === (right.gate ?? null)
    && (left.entryId ?? null) === (right.entryId ?? null)
    && (left.entryVersion ?? null) === (right.entryVersion ?? null)
    && (left.partnerTargetCheck ?? null) === (right.partnerTargetCheck ?? null)
    && (left.locationMembership ?? null) === (right.locationMembership ?? null)
    && (left.auditIntentStatus ?? null) === (right.auditIntentStatus ?? null);
}

function createFee(session, actor, call, fee) {
  if (!isManagement(actor)) return refuse(403, "no_management_grant", "no_management_grant");
  if (!call.correlationId || !call.sourceRequestId) return refuse(400, "server_context_required", "server_context_required");
  const incoming = actor.role === "super_admin" ? "global" : "private";
  const codeRefusal = codeAllowed(session, "fee_type", fee.code, incoming);
  if (codeRefusal) return codeRefusal;
  const entryId = nextId(session);
  const changeId = nextId(session);
  const eventId = nextId(session);
  const occurredAt = stamp(session);
  const after = { code: fee.code, display_name: fee.displayName };
  const changed = ["code", "display_name"];
  if (fee.description != null) {
    after.description = fee.description;
    changed.push("description");
  }
  const entry = {
    entryId,
    family: "fee_type",
    entryClass: incoming,
    ownerPartnerId: incoming === "private" ? actor.partnerId : null,
    code: fee.code,
    displayName: fee.displayName,
    description: fee.description ?? null,
    entryVersion: 1,
    fields: {},
    origin: "command",
  };
  const intent = makeIntent({
    eventId, changeId, entryId, name: "catalog.entry.created", actor, family: "fee_type",
    version: 1, changed, before: null, after, reason: null, call, occurredAt,
  });
  const outcome = {
    ...base(),
    status: 201,
    code: "created",
    wrote: true,
    entryId,
    entryVersion: 1,
    auditIntentStatus: INTENT_STATUS,
    intent,
    entry: project(actor, entry),
  };
  session.entries.set(entryId, entry);
  session.history.push({ changeId, entryId, changeKind: "created", entryVersion: 1, changedFields: changed, before: null, after, occurredAt });
  session.intents.push(intent);
  session.idempotency.set(call.idempotencyKey, { hash: bind(actor, "fee_create", "fee_type", null, null, fee.body), outcome: clone(outcome) });
  return outcome;
}

function editClaimPresentation(session, actor, call, edit) {
  if (!call.correlationId || !call.sourceRequestId) return refuse(400, "server_context_required", "server_context_required");
  const entry = session.entries.get(edit.entryId);
  if (!visible(actor, entry) || entry.family !== "model_hardware_claim") return notFound();
  if (!canEditClaim(actor, entry)) return refuse(403, "catalog_claim_edit_actor", "catalog_claim_edit_actor");
  session.versionComparisons += 1;
  if (entry.entryVersion !== edit.expected.number) return refuse(409, "catalog_version_conflict", "catalog_version_conflict");
  const changed = [];
  for (const key of [...edit.fields.keys()].sort()) {
    if (currentValue(entry, key) === edit.fields.get(key)) continue;
    changed.push(key);
  }
  if (changed.length === 0) return refuse(409, "catalog_noop", "catalog_noop");
  return {
    ...unavailable(GATES.fieldGate, "edit_controls_and_persistence_disabled"),
    openPolicy: OPEN_POLICIES.fieldApproval,
    divergence: "accepted_double_would_record_proposed_allowlist_presentation_persistence_disabled",
  };
}

function activate(session, actor, family, entryId, desired, partnerText, locationText, entryCommand) {
  let partnerCheck = null;
  let locationMembership = null;
  if (partnerText != null) {
    if (actor.role === "super_admin") {
      if (!isGuid(partnerText)) return refuse(400, "invalid_partner_uuid", "partner_id");
      partnerCheck = PARTNER_SYNTAX_ONLY;
    } else if (actor.role === "partner_admin") {
      if (!isGuid(partnerText) || partnerText.toLowerCase() !== actor.partnerId.toLowerCase()) {
        return refuse(400, "contradictory_partner", "partner_id");
      }
      partnerCheck = "authenticated_principal_not_registry";
    }
  }
  if (locationText != null && actor.role === "partner_admin") locationMembership = "not_verified";
  if (!isManagement(actor)) return notFound();
  const entry = session.entries.get(entryId);
  if (!visible(actor, entry) || entry.family !== family) return notFound();
  if (locationText != null && actor.role === "super_admin") return refuse(403, "sa_has_no_location_switch", "sa_has_no_location_switch");
  if (partnerText != null && entry.entryClass === "private") {
    return unavailable(GATES.privatePartnerSwitch, OPEN_POLICIES.privatePartnerSwitch, partnerCheck, locationMembership);
  }
  if (entryCommand && family === "model_hardware_claim" && desired === true) {
    return unavailable(GATES.claimReactivation, "pending_pq272", partnerCheck, locationMembership);
  }
  if (desired === false) return unavailable(GATES.graphOff, GATES.graphOff, partnerCheck, locationMembership);
  return unavailable(GATES.graphRestore, GATES.graphRestore, partnerCheck, locationMembership);
}

function withKey(session, actor, call, operation, family, entryId, version, body, apply) {
  if (call.idempotencyKey == null || String(call.idempotencyKey).trim() === "" || String(call.idempotencyKey).length > 200) {
    return refuse(400, "idempotency_key_required", "idempotency_key_required");
  }
  const hash = bind(actor, operation, family, entryId, version, body);
  const existing = session.idempotency.get(call.idempotencyKey);
  if (existing) return existing.hash === hash ? clone(existing.outcome) : refuse(409, "idempotency_conflict", "idempotency_conflict");
  return apply();
}

function codeAllowed(session, family, code, incoming) {
  const same = [...session.entries.values()].filter((entry) => entry.family === family && entry.code === code);
  if (same.length === 0 || (same.length === 1 && same[0].entryClass !== incoming)) return null;
  const privateCollision = incoming === "private" || same.some((entry) => entry.entryClass === "private");
  return unavailable(privateCollision ? GATES.privateCode : "code_policy_unresolved", "code_policy_unresolved");
}

function visible(actor, entry) {
  if (!entry || !isManagement(actor)) return false;
  if (entry.entryClass === "global") return true;
  if (entry.family === "model_hardware_claim") return actor.role === "super_admin" || actor.partnerId === entry.ownerPartnerId;
  return actor.role === "partner_admin" && actor.partnerId === entry.ownerPartnerId;
}

function canEditClaim(actor, entry) {
  return actor.role === "super_admin"
    || (actor.role === "partner_admin" && entry.entryClass === "private" && actor.partnerId === entry.ownerPartnerId);
}

function currentValue(entry, key) {
  if (key === "display_name") return entry.fields.display_name ?? entry.displayName;
  if (key === "description") return entry.fields.description ?? entry.description;
  return Object.prototype.hasOwnProperty.call(entry.fields, key) ? entry.fields[key] : null;
}

function project(actor, entry) {
  const fields = { ...entry.fields };
  delete fields.display_name;
  delete fields.description;
  const actions = [
    { action: "entry_off", allowed: false, gate: GATES.graphOff },
    { action: "entry_on", allowed: false, gate: entry.family === "model_hardware_claim" ? GATES.claimReactivation : GATES.graphRestore },
    { action: "partner_off", allowed: false, gate: entry.entryClass === "private" ? GATES.privatePartnerSwitch : GATES.graphOff },
    { action: "partner_on", allowed: false, gate: entry.entryClass === "private" ? GATES.privatePartnerSwitch : GATES.graphRestore },
    { action: "location_off", allowed: false, gate: actor.role === "super_admin" ? "sa_has_no_location_switch" : GATES.graphOff },
    { action: "location_on", allowed: false, gate: actor.role === "super_admin" ? "sa_has_no_location_switch" : GATES.graphRestore },
  ];
  if (entry.family === "model_hardware_claim") {
    const allowed = canEditClaim(actor, entry);
    actions.push({ action: "claim_edit", allowed, gate: allowed ? "proposed_six_field_allowlist" : "catalog_claim_edit_actor" });
  }
  return {
    entryId: entry.entryId,
    family: entry.family,
    entryClass: entry.entryClass,
    ownerPartnerId: entry.ownerPartnerId,
    scopeKind: entry.entryClass,
    code: entry.code,
    displayName: entry.displayName,
    description: entry.description,
    entryVersion: entry.entryVersion,
    fields,
    entryActivation: { token: "ABSENT", number: null, active: null },
    partnerSwitch: { token: "ABSENT", number: null, active: null },
    locationSwitch: { token: "ABSENT", number: null, active: null },
    effectiveAvailability: "unknown",
    allowedActions: actions,
    origin: entry.origin,
    proofBoundary: PROOF_BOUNDARY,
  };
}

function readPreview(graph) {
  if (graph.state === "fixture") {
    return {
      impact: "fixture_not_authorization",
      affectedCount: null,
      applyAllowed: false,
      fixtureRevision: graph.revision ? graph.revision : null,
      fixtureDeclaredEdgeCount: Array.isArray(graph.edges) ? graph.edges.length : 0,
      runtimeConfiguration: false,
      authorizesWrites: false,
    };
  }
  return {
    impact: "unknown",
    affectedCount: null,
    applyAllowed: false,
    fixtureRevision: null,
    fixtureDeclaredEdgeCount: null,
    runtimeConfiguration: false,
    authorizesWrites: false,
  };
}

function parseCreate(family, json) {
  if (!tryParseFamily(family)) return reject(400, "invalid_family", "invalid_family");
  const flat = readFlat(json);
  if (!flat.ok) return reject(400, "invalid_dto", flat.error);
  if (containsSentinel(flat.fields)) return reject(400, "schema_version_rejected", "schema_version_rejected");
  const authority = firstKey(flat.fields, AUTHORITY);
  if (authority) return reject(400, "authority_field_rejected", authority);
  if (disposition(family) !== "names_only") {
    return { kind: "unavailable", detail: disposition(family) === "required_fields_unresolved" ? "required_fields_unresolved" : "create_fields_not_closed" };
  }
  const unknown = Object.keys(flat.fields).find((key) => !FEE_KEYS.includes(key));
  if (unknown) return reject(400, "unknown_field", unknown);
  if (!Object.prototype.hasOwnProperty.call(flat.fields, "code")) return reject(400, "code_required", "code");
  if (!Object.prototype.hasOwnProperty.call(flat.fields, "display_name")) return reject(400, "display_name_required", "display_name");
  const code = bounded(flat.fields, "code", true, 64, false);
  if (!code.ok) return reject(400, code.error, "code");
  const display = bounded(flat.fields, "display_name", true, 200, true);
  if (!display.ok) return reject(400, display.error, "display_name");
  const description = bounded(flat.fields, "description", false, 2000, true);
  if (!description.ok) return reject(400, description.error, "description");
  if (sensitive(code.value) || sensitive(display.value) || sensitive(description.value)) {
    return reject(400, "sensitive_value_rejected", "sensitive_value_rejected");
  }
  const body = { code: code.value, display_name: display.value };
  if (Object.prototype.hasOwnProperty.call(flat.fields, "description")) body.description = description.value;
  return { kind: "fee", code: code.value, displayName: display.value, description: body.description ?? null, body };
}

function parseClaimEdit(entryId, json) {
  const flat = readFlat(json);
  if (!flat.ok) return reject(400, "invalid_dto", flat.error);
  if (containsSentinel(flat.fields)) return reject(400, "schema_version_rejected", "schema_version_rejected");
  if (Object.keys(flat.fields).some((key) => PROTECTED_CLAIM.has(key))) return reject(422, "catalog_column_matrix_unsettled", "protected_field");
  const authority = firstKey(flat.fields, AUTHORITY);
  if (authority) return reject(400, "authority_field_rejected", authority);
  const unknown = Object.keys(flat.fields).find((key) => key !== "expected_version" && !CLAIM_EDIT_KEYS.includes(key));
  if (unknown) return reject(400, "unknown_field", unknown);
  if (!Object.prototype.hasOwnProperty.call(flat.fields, "expected_version")) return reject(428, "catalog_precondition_required", "expected_version");
  const version = parseVersion(flat.fields.expected_version);
  if (!version.ok) return reject(400, "version_domain", "expected_version");
  if (version.token.absent) return reject(400, "version_domain", "entry_version_absent");
  const edits = new Map();
  for (const key of CLAIM_EDIT_KEYS) {
    if (!Object.prototype.hasOwnProperty.call(flat.fields, key)) continue;
    const value = flat.fields[key];
    if (typeof value !== "string" || !validClaimValue(key, value)) return reject(400, "invalid_field", key);
    if (sensitive(value)) return reject(400, "sensitive_value_rejected", "sensitive_value_rejected");
    edits.set(key, key === "checked_at" ? normalizeInstant(value) : value);
  }
  if (edits.size === 0) return reject(400, "empty_edit", "claim_fields");
  const body = {};
  for (const [key, value] of edits) body[key] = value;
  body.expected_version = String(version.token.number);
  return { kind: "edit", entryId, fields: edits, expected: version.token, body };
}

function parseActivation(family, json) {
  if (!tryParseFamily(family)) return reject(400, "invalid_family", "invalid_family");
  const flat = readFlat(json);
  if (!flat.ok) return reject(400, "invalid_dto", flat.error);
  if (containsSentinel(flat.fields)) return reject(400, "schema_version_rejected", "schema_version_rejected");
  if (Object.keys(flat.fields).some((key) => PROTECTED_CLAIM.has(key)) || firstKey(flat.fields, AUTHORITY)) {
    return reject(400, "authority_field_rejected", "closed_dto");
  }
  const unknown = Object.keys(flat.fields).find((key) => !ACTIVATION_KEYS.includes(key));
  if (unknown) return reject(400, "unknown_field", unknown);
  if (typeof flat.fields.reason !== "string") return reject(422, "catalog_reason_required", "reason");
  const reason = flat.fields.reason;
  if (reason.trim() === "") return reject(422, "catalog_reason_required", "reason");
  if (reason.length > DOUBLE_INPUT_CAP) return reject(400, "double_input_cap", "double_input_cap_not_owner_reason_policy");
  if (sensitive(reason)) return reject(400, "sensitive_value_rejected", "sensitive_value_rejected");
  if (typeof flat.fields.desired_active !== "boolean") return reject(400, "invalid_field", "desired_active");
  if (!Object.prototype.hasOwnProperty.call(flat.fields, "expected_version")) return reject(428, "catalog_precondition_required", "expected_version");
  const version = parseVersion(flat.fields.expected_version);
  if (!version.ok) return reject(400, "version_domain", "expected_version");
  return { kind: "activation", desired: flat.fields.desired_active, reason, expected: version.token };
}

function validClaimValue(key, text) {
  if (key === "checked_at") return hasOffset(text) && !Number.isNaN(Date.parse(text));
  if (key === "availability") return text.length > 0 && text.length <= 64 && ![...text].some((ch) => /\s/u.test(ch));
  if (key === "claim_value" || key === "description") return text.length <= 2000 && ![...text].some((ch) => ch.charCodeAt(0) < 32);
  if (key === "display_name") return text.length > 0 && text.length <= 200 && ![...text].some((ch) => ch.charCodeAt(0) < 32);
  if (key === "source_url") {
    return text.length > 0 && text.length <= 2000 && ![...text].some((ch) => /\s/u.test(ch))
      && !text.toLowerCase().includes("token=") && !text.includes("@");
  }
  return false;
}

function hasOffset(text) {
  return text.endsWith("Z") || text.includes("+") || text.lastIndexOf("-") > 9;
}

function normalizeInstant(text) {
  const date = new Date(text);
  const pad = (value, size = 2) => String(value).padStart(size, "0");
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}.${pad(date.getUTCMilliseconds(), 3)}0000Z`;
}

function bounded(fields, key, required, max, allowSpace) {
  if (!Object.prototype.hasOwnProperty.call(fields, key)) return required ? { ok: false, error: `${key}_required` } : { ok: true, value: null };
  const text = fields[key];
  if (typeof text !== "string") return { ok: false, error: "invalid_field" };
  const blank = text.trim() === "";
  const spaced = [...text].some((ch) => /\s/u.test(ch));
  const control = [...text].some((ch) => ch.charCodeAt(0) < 32);
  if (text.length === 0 || text.length > max || control || (!allowSpace && spaced) || (allowSpace && blank)) return { ok: false, error: "invalid_field" };
  return { ok: true, value: text };
}

function parseVersion(value) {
  if (value === "ABSENT") return { ok: true, token: { absent: true, number: null } };
  if (typeof value === "number" && Number.isSafeInteger(value) && value >= 1) return { ok: true, token: { absent: false, number: value } };
  return { ok: false };
}

function readFlat(json) {
  if (typeof json !== "string") return { ok: false, error: "invalid_json" };
  let root;
  try {
    root = JSON.parse(json);
  } catch {
    return { ok: false, error: "invalid_json" };
  }
  if (root === null || typeof root !== "object" || Array.isArray(root)) return { ok: false, error: "invalid_json" };
  for (const value of Object.values(root)) {
    if (value !== null && typeof value === "object") return { ok: false, error: "invalid_field" };
  }
  if (hasDuplicateRawKey(json)) return { ok: false, error: "duplicate_field" };
  return { ok: true, fields: root };
}

function hasDuplicateRawKey(json) {
  const seen = new Set();
  let index = 0;
  const skip = () => { while (index < json.length && /\s/u.test(json[index])) index += 1; };
  const readString = () => {
    index += 1;
    let text = "";
    while (index < json.length && json[index] !== '"') {
      if (json[index] === "\\") index += 1;
      text += json[index] ?? "";
      index += 1;
    }
    if (index < json.length) index += 1;
    return text;
  };
  const skipValue = () => {
    skip();
    if (index >= json.length) return false;
    const mark = json[index];
    if (mark === '"') { readString(); return true; }
    if (mark === "{" || mark === "[") return false;
    while (index < json.length && json[index] !== "," && json[index] !== "}") index += 1;
    return true;
  };
  skip();
  if (index >= json.length || json[index] !== "{") return false;
  index += 1;
  while (true) {
    skip();
    if (index < json.length && json[index] === "}") return false;
    if (index >= json.length || json[index] !== '"') return false;
    const key = readString();
    if (seen.has(key)) return true;
    seen.add(key);
    skip();
    if (index >= json.length || json[index] !== ":") return false;
    index += 1;
    if (!skipValue()) return false;
    skip();
    if (index < json.length && json[index] === ",") { index += 1; continue; }
    return false;
  }
}

function containsSentinel(fields) {
  return Object.values(fields).some((value) => typeof value === "string" && value.includes(REJECTED_SENTINEL));
}

function firstKey(fields, names) {
  return Object.keys(fields).sort().find((key) => names.has(key)) ?? null;
}

function sensitive(value) {
  if (!value) return false;
  if (value.toLowerCase().includes("token=")) return true;
  const digits = [...value].filter((ch) => ch >= "0" && ch <= "9").join("");
  const separatorsOnly = [...value].every((ch) => (ch >= "0" && ch <= "9") || ch === " " || ch === "-");
  if (separatorsOnly && digits.length >= 13 && digits.length <= 19) return true;
  const lower = value.toLowerCase();
  return SENSITIVE.some((token) => boundedToken(lower, token));
}

function boundedToken(lower, token) {
  let index = 0;
  while ((index = lower.indexOf(token, index)) >= 0) {
    const before = index === 0 || !/[a-z]/u.test(lower[index - 1]);
    const afterIndex = index + token.length;
    const after = afterIndex === lower.length || !/[a-z]/u.test(lower[afterIndex]);
    if (before && after) return true;
    index += 1;
  }
  return false;
}

function isGuid(value) {
  if (typeof value !== "string") return false;
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value)) return false;
  return value.toLowerCase() !== "00000000-0000-0000-0000-000000000000";
}

function bind(actor, operation, family, entryId, version, body) {
  const lines = [
    "v1", actor.userId, actor.role, actor.partnerId ?? "-", operation, family,
    entryId ?? "-", version?.absent ? "ABSENT" : (version?.number ?? "-"),
  ];
  for (const key of Object.keys(body).sort()) lines.push(`${key}=${body[key]}`);
  return lines.join("\n");
}

function makeIntent({ eventId, changeId, entryId, name, actor, family, version, changed, before, after, reason, call, occurredAt }) {
  if (name !== "catalog.entry.created" && name !== "catalog.entry.updated") {
    throw new Error("That catalog event name is not recorded by this projection.");
  }
  const fields = {
    event_id: eventId,
    change_id: changeId,
    entry_id: entryId,
    proposed_event_name: name,
    emitted: "false",
    proof_boundary: PROOF_BOUNDARY,
    actor_user_id: actor.userId,
    acting_role: actor.role === "super_admin" ? "super_admin" : actor.role === "partner_admin" ? "partner_admin" : "no_management_grant",
    actor_partner_id: actor.partnerId,
    target_family: family,
    target_scope: "entry",
    entry_version: String(version),
    changed_fields: changed.join(","),
    before: before ? "allowlisted" : null,
    after: "allowlisted",
    reason,
    correlation_id: call.correlationId,
    source_request_id: call.sourceRequestId,
    occurred_at: occurredAt,
    intent_status: INTENT_STATUS,
  };
  if (fields.emitted !== "false" || fields.intent_status !== INTENT_STATUS) throw new Error("intent status drifted");
  if (JSON.stringify(fields).includes(REJECTED_SENTINEL)) throw new Error("sentinel stored");
  return { fields, before, after };
}

function nextId(session) {
  const n = session.idCounter++;
  return `40000000-0000-4000-8000-${n.toString(16).padStart(12, "0")}`;
}

function stamp(session) {
  const value = session.clockMs;
  session.clockMs += 3_600_000;
  const date = new Date(value);
  const pad = (part, size = 2) => String(part).padStart(size, "0");
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}T${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}:${pad(date.getUTCSeconds())}.${pad(date.getUTCMilliseconds(), 3)}0000Z`;
}

function base() {
  return {
    status: 200,
    code: "",
    proofBoundary: PROOF_BOUNDARY,
    wrote: false,
    gate: null,
    detail: null,
    entryId: null,
    entryVersion: null,
    partnerTargetCheck: null,
    locationMembership: null,
    auditIntentStatus: null,
    intent: null,
    entry: null,
    list: null,
    history: null,
    preview: null,
    graph: null,
    integrated: false,
  };
}

function ok(code) {
  return { ...base(), status: 200, code };
}

function refuse(status, code, detail) {
  return { ...base(), status, code, detail, wrote: false };
}

function notFound() {
  return refuse(404, "not_found", "not_found");
}

function unavailable(gate, detail, partnerCheck = null, locationMembership = null) {
  return { ...base(), status: 503, code: "unavailable", gate, detail, wrote: false, partnerTargetCheck: partnerCheck, locationMembership };
}

function reject(status, code, detail) {
  return { kind: "rejected", status, code, detail };
}

function clone(value) {
  return structuredClone(value);
}
