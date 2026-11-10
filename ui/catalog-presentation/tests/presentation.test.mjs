import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { applyAction, freshState, parseRoute } from "../js/actions.mjs";
import { plainReason, renderApp } from "../js/render.mjs";
import {
  GATES,
  OPEN_POLICIES,
  PARTNER_SYNTAX_ONLY,
  actorByKey,
  createSession,
  describeGraph,
  listEntries,
  preview,
  readEntry,
  sameRefusal,
  submitClaimEdit,
  submitCreate,
  submitEntryActivation,
  submitLocationActivation,
  submitPartnerActivation,
} from "../js/projection.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "..", "..");
const sa = actorByKey("super_admin");
const pa = actorByKey("partner_admin");
const other = actorByKey("partner_admin_other");
const cashier = actorByKey("cashier");

function call(key = "k") {
  return { idempotencyKey: key, correlationId: "corr", sourceRequestId: "req" };
}

function feeJson(code = "LATE", name = "Late fee", extra) {
  return JSON.stringify({ code, display_name: name, ...extra });
}

function findCode(session, family, code, entryClass) {
  return [...session.entries.values()].find((entry) => entry.family === family && entry.code === code && entry.entryClass === entryClass);
}

test("malformed create bodies write nothing", () => {
  const session = createSession();
  const before = session.entries.size;
  const cases = [
    ["nope", feeJson(), 400, "invalid_family"],
    ["fee_type", "{", 400, "invalid_dto"],
    ["fee_type", "{\"code\":{\"nested\":true},\"display_name\":\"Late\"}", 400, "invalid_dto"],
    ["fee_type", "{\"code\":\"LATE\",\"code\":\"OTHER\",\"display_name\":\"Late\"}", 400, "invalid_dto"],
    ["fee_type", "{\"code\":\"LATE FEE\",\"display_name\":\"Late\"}", 400, "invalid_field"],
    ["fee_type", "{\"display_name\":\"Late\"}", 400, "code_required"],
    ["fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late\",\"economic_role\":\"unknown\"}", 400, "unknown_field"],
    ["fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late\",\"classification\":\"dummy\"}", 400, "unknown_field"],
    ["fee_type", "{\"code\":\"LATE\",\"display_name\":\"1.2.0-catalog-unapproved\"}", 400, "schema_version_rejected"],
    ["fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late\",\"owner_partner_id\":\"20000000-0000-4000-8000-000000000001\"}", 400, "authority_field_rejected"],
    ["fee_type", "{\"code\":\"4111111111111111\",\"display_name\":\"Late\"}", 400, "sensitive_value_rejected"],
    ["catalog_release", feeJson(), 503, "unavailable"],
    ["id_document_type", feeJson(), 503, "unavailable"],
    ["currency", "{\"code\":\"USD\",\"display_name\":\"US Dollar\"}", 503, "unavailable"],
    ["manufacturer", feeJson(), 503, "unavailable"],
  ];
  for (const [family, json, status, code] of cases) {
    const outcome = submitCreate(session, sa, call(family + code), family, json);
    assert.equal(outcome.status, status, family + code);
    assert.equal(outcome.code, code, family + json);
    assert.equal(outcome.wrote, false);
    assert.equal(outcome.integrated, false);
  }
  assert.equal(session.entries.size, before);
  assert.equal(session.intents.length, 0);
  const release = submitCreate(session, sa, call("rel"), "catalog_release", feeJson());
  assert.equal(release.gate, GATES.createUnresolved);
  assert.equal(release.detail, "required_fields_unresolved");
  const currency = submitCreate(session, pa, call("cur"), "currency", "{\"code\":\"USD\",\"display_name\":\"Dollar\"}");
  assert.equal(currency.detail, "required_fields_unresolved");
  assert.equal(currency.wrote, false);
});

test("names-only fee create is session-local, replayable, and not durable", () => {
  const session = createSession();
  const before = session.entries.size;
  const missingKey = submitCreate(session, sa, { idempotencyKey: "  ", correlationId: "c", sourceRequestId: "r" }, "fee_type", feeJson());
  assert.equal(missingKey.code, "idempotency_key_required");
  const denied = submitCreate(session, cashier, call("cashier"), "fee_type", feeJson());
  assert.equal(denied.status, 403);
  assert.equal(denied.code, "no_management_grant");
  const created = submitCreate(session, sa, call("fee-1"), "fee_type", feeJson());
  assert.equal(created.status, 201);
  assert.equal(created.wrote, true);
  assert.equal(created.entry.entryClass, "global");
  assert.equal(created.entry.ownerPartnerId, null);
  assert.equal(created.entry.effectiveAvailability, "unknown");
  assert.equal(created.entry.entryActivation.token, "ABSENT");
  assert.equal(created.entry.entryActivation.number, null);
  assert.equal(created.entry.entryActivation.active, null);
  assert.equal(created.intent.fields.emitted, "false");
  assert.equal(created.intent.fields.intent_status, "double_recorded_not_durable");
  assert.equal(created.intent.fields.proposed_event_name, "catalog.entry.created");
  assert.equal(JSON.stringify(created.intent).includes("DURABLY_ACCEPTED"), false);
  assert.equal(JSON.stringify(created.intent).includes("1.2.0-catalog-unapproved"), false);
  assert.equal(created.intent.after.economic_role, undefined);
  assert.equal(session.entries.size, before + 1);
  const replay = submitCreate(session, sa, call("fee-1"), "fee_type", feeJson());
  assert.equal(replay.entryId, created.entryId);
  assert.equal(replay.intent.fields.event_id, created.intent.fields.event_id);
  assert.equal(session.entries.size, before + 1);
  assert.equal(session.history.length, 1);
  const conflict = submitCreate(session, sa, call("fee-1"), "fee_type", feeJson("OTHER", "Other"));
  assert.equal(conflict.code, "idempotency_conflict");
  assert.equal(conflict.wrote, false);
  const otherActor = submitCreate(session, pa, call("fee-1"), "fee_type", feeJson());
  assert.equal(otherActor.code, "idempotency_conflict");
  const shared = submitCreate(session, pa, call("fee-private"), "fee_type", "{\"code\":\"LATE\",\"display_name\":\"Late fee\",\"description\":\"Name only\"}");
  assert.equal(shared.status, 201);
  assert.equal(shared.entry.entryClass, "private");
  assert.equal(shared.entry.ownerPartnerId, pa.partnerId);
  assert.notEqual(shared.entryId, created.entryId);
  const secondGlobal = submitCreate(session, sa, call("fee-2"), "fee_type", feeJson());
  assert.equal(secondGlobal.status, 503);
  assert.equal(secondGlobal.detail, "code_policy_unresolved");
  assert.equal(secondGlobal.wrote, false);
  const secondPrivate = submitCreate(session, other, call("fee-3"), "fee_type", feeJson());
  assert.equal(secondPrivate.gate, GATES.privateCode);
  assert.equal(secondPrivate.wrote, false);
});

test("visibility hides private non-claims and keeps same-code rows", () => {
  const session = createSession();
  const saList = listEntries(session, sa, "fee_type");
  const paList = listEntries(session, pa, "fee_type");
  const otherList = listEntries(session, other, "fee_type");
  const cashierList = listEntries(session, cashier, "fee_type");
  assert.equal(cashierList.status, 403);
  assert.equal(saList.list.pagination, "not_a_cursor_contract");
  assert.equal(saList.list.hiddenCount, undefined);
  const privateFee = findCode(session, "fee_type", "EXAMPLE-20-A", "private");
  assert.equal(saList.list.entries.some((entry) => entry.entryId === privateFee.entryId), true);
  assert.equal(paList.list.entries.filter((entry) => entry.code === "EXAMPLE-20-A").length, 2);
  assert.equal(otherList.list.entries.some((entry) => entry.entryId === privateFee.entryId), false);
  const seen = readEntry(session, sa, "fee_type", privateFee.entryId);
  const missing = readEntry(session, sa, "fee_type", "00000000-0000-4000-8000-000000000099");
  assert.equal(seen.status, 200);
  assert.equal(seen.entry.code, "EXAMPLE-20-A");
  assert.equal(sameRefusal(readEntry(session, other, "fee_type", privateFee.entryId), missing), true);
  const makers = listEntries(session, pa, "manufacturer").list.entries.filter((entry) => entry.code === "EXAMPLE-5-A");
  assert.equal(makers.length, 2);
  assert.equal(new Set(makers.map((entry) => entry.entryId)).size, 2);
});

test("every Off and graph-dependent restore write nothing", () => {
  const session = createSession();
  const fee = findCode(session, "fee_type", "EXAMPLE-20-A", "global");
  const before = session.entries.size;
  const blank = submitEntryActivation(session, sa, call("b"), "fee_type", fee.entryId, "{\"desired_active\":false,\"reason\":\"   \",\"expected_version\":\"ABSENT\"}");
  assert.equal(blank.status, 422);
  assert.equal(blank.code, "catalog_reason_required");
  const capped = submitEntryActivation(session, sa, call("c"), "fee_type", fee.entryId, `{"desired_active":false,"reason":"${"r".repeat(4001)}","expected_version":"ABSENT"}`);
  assert.equal(capped.code, "double_input_cap");
  const off = submitEntryActivation(session, sa, call("off"), "fee_type", fee.entryId, `{"desired_active":false,"reason":"${"r".repeat(501)}","expected_version":"ABSENT"}`);
  assert.equal(off.status, 503);
  assert.equal(off.gate, GATES.graphOff);
  assert.equal(off.wrote, false);
  assert.equal(JSON.stringify(off).includes("r".repeat(501)), false);
  const restore = submitEntryActivation(session, pa, call("on"), "fee_type", fee.entryId, "{\"desired_active\":true,\"reason\":\"turn it back\",\"expected_version\":\"ABSENT\"}");
  assert.equal(restore.gate, GATES.graphRestore);
  assert.equal(restore.wrote, false);
  const unknown = preview(session, sa, "fee_type", fee.entryId, null);
  assert.equal(unknown.preview.impact, "unknown");
  assert.equal(unknown.preview.affectedCount, null);
  assert.equal(unknown.preview.applyAllowed, false);
  const fixture = describeGraph({ state: "fixture", revision: "presentation-fixture", edges: [] });
  assert.equal(fixture.graph.impact, "fixture_not_authorization");
  assert.equal(fixture.graph.affectedCount, null);
  assert.equal(fixture.graph.fixtureDeclaredEdgeCount, 0);
  assert.equal(fixture.graph.authorizesWrites, false);
  assert.equal(fixture.wrote, false);
  const stillOff = submitEntryActivation(session, sa, call("off2"), "fee_type", fee.entryId, "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}");
  assert.equal(stillOff.gate, GATES.graphOff);
  assert.equal(session.entries.size, before);
  assert.equal(session.history.length, 0);
  assert.equal(fee.entryVersion, 1);
});

test("claim On is unavailable for every actor who can address it, and edits do not persist", () => {
  const session = createSession();
  const claim = findCode(session, "model_hardware_claim", "EXAMPLE-CLAIM-P", "private");
  const globalClaim = findCode(session, "model_hardware_claim", "EXAMPLE-CLAIM-G", "global");
  const versionBefore = claim.entryVersion;
  const body = "{\"desired_active\":true,\"reason\":\"reactivate\",\"expected_version\":\"ABSENT\"}";
  const saOn = submitEntryActivation(session, sa, call("sa-on"), "model_hardware_claim", claim.entryId, body);
  const paOn = submitEntryActivation(session, pa, call("pa-on"), "model_hardware_claim", claim.entryId, body);
  assert.equal(sameRefusal(saOn, paOn), true);
  assert.equal(saOn.gate, GATES.claimReactivation);
  assert.equal(saOn.detail, "pending_pq272");
  assert.equal(saOn.wrote, false);
  assert.equal(JSON.stringify(saOn).toLowerCase().includes("sa_only"), false);
  assert.equal(JSON.stringify(saOn).includes("SA-only"), false);
  const otherOn = submitEntryActivation(session, other, call("other-on"), "model_hardware_claim", claim.entryId, body);
  const missing = readEntry(session, sa, "model_hardware_claim", "missing-claim");
  assert.equal(sameRefusal(otherOn, missing), true);
  const cashierOn = submitEntryActivation(session, cashier, call("cashier-on"), "model_hardware_claim", claim.entryId, body);
  assert.equal(cashierOn.status, 404);
  assert.equal(cashierOn.wrote, false);
  const comparisons = session.versionComparisons;
  const hiddenEdit = submitClaimEdit(session, other, call("hidden"), "model_hardware_claim", claim.entryId, "{\"expected_version\":9,\"claim_value\":\"guess\"}");
  assert.equal(sameRefusal(hiddenEdit, missing), true);
  assert.equal(session.versionComparisons, comparisons);
  const forbidden = submitClaimEdit(session, pa, call("global"), "model_hardware_claim", globalClaim.entryId, "{\"expected_version\":1,\"claim_value\":\"nope\"}");
  assert.equal(forbidden.status, 403);
  assert.equal(forbidden.code, "catalog_claim_edit_actor");
  assert.equal(session.versionComparisons, comparisons);
  const stale = submitClaimEdit(session, sa, call("stale"), "model_hardware_claim", claim.entryId, "{\"expected_version\":2,\"claim_value\":\"next\"}");
  assert.equal(stale.code, "catalog_version_conflict");
  assert.equal(stale.wrote, false);
  const variant = submitClaimEdit(session, sa, call("variant"), "model_hardware_claim", claim.entryId, "{\"expected_version\":1,\"variant_note\":\"nope\"}");
  assert.equal(variant.code, "unknown_field");
  assert.equal(variant.detail, "variant_note");
  const supersedes = submitClaimEdit(session, sa, call("sup"), "model_hardware_claim", claim.entryId, "{\"expected_version\":1,\"supersedes_claim_id\":\"x\"}");
  assert.equal(supersedes.detail, "supersedes_claim_id");
  const persisted = submitClaimEdit(session, sa, call("edit"), "model_hardware_claim", claim.entryId, "{\"expected_version\":1,\"claim_value\":\"TEST-ONLY-edited-value\"}");
  assert.equal(persisted.status, 503);
  assert.equal(persisted.gate, GATES.fieldGate);
  assert.equal(persisted.openPolicy, OPEN_POLICIES.fieldApproval);
  assert.equal(persisted.wrote, false);
  assert.equal(claim.entryVersion, versionBefore);
  assert.equal(claim.fields.claim_value, "example-claim");
  assert.equal(session.history.length, 0);
});

test("partner UUID is syntax only and private switch, location, and code gates stay unbuilt", () => {
  const session = createSession();
  const fee = findCode(session, "fee_type", "EXAMPLE-20-A", "global");
  const privateFee = findCode(session, "fee_type", "EXAMPLE-20-A", "private");
  const off = "{\"desired_active\":false,\"reason\":\"stop\",\"expected_version\":\"ABSENT\"}";
  const bad = submitPartnerActivation(session, sa, call("bad"), "fee_type", fee.entryId, "not-a-uuid", off);
  assert.equal(bad.code, "invalid_partner_uuid");
  assert.equal(bad.partnerTargetCheck, null);
  assert.equal(JSON.stringify(bad).toLowerCase().includes("verified"), false);
  const syntax = submitPartnerActivation(session, sa, call("syntax"), "fee_type", fee.entryId, "20000000-0000-4000-8000-000000000099", off);
  assert.equal(syntax.gate, GATES.graphOff);
  assert.equal(syntax.partnerTargetCheck, PARTNER_SYNTAX_ONLY);
  assert.equal(syntax.wrote, false);
  const retarget = submitPartnerActivation(session, pa, call("retarget"), "fee_type", fee.entryId, "20000000-0000-4000-8000-000000000099", off);
  assert.equal(retarget.code, "contradictory_partner");
  assert.equal(retarget.wrote, false);
  const partnerSwitch = submitPartnerActivation(session, pa, call("pswitch"), "fee_type", privateFee.entryId, pa.partnerId, off);
  assert.equal(partnerSwitch.gate, GATES.privatePartnerSwitch);
  assert.equal(partnerSwitch.detail, OPEN_POLICIES.privatePartnerSwitch);
  assert.equal(partnerSwitch.wrote, false);
  const saLocation = submitLocationActivation(session, sa, call("sloc"), "fee_type", fee.entryId, "20000000-0000-4000-8000-000000000040", off);
  assert.equal(saLocation.status, 403);
  assert.equal(saLocation.code, "sa_has_no_location_switch");
  const paLocation = submitLocationActivation(session, pa, call("ploc"), "fee_type", fee.entryId, "20000000-0000-4000-8000-000000000040", "{\"desired_active\":true,\"reason\":\"resume\",\"expected_version\":\"ABSENT\"}");
  assert.equal(paLocation.gate, GATES.graphRestore);
  assert.equal(paLocation.locationMembership, "not_verified");
  assert.equal(paLocation.wrote, false);
  const badLocation = submitLocationActivation(session, pa, call("bloc"), "fee_type", fee.entryId, "loc", off);
  assert.equal(badLocation.code, "invalid_location_uuid");
});

test("screens keep TEST-ONLY rows, disabled creates, and no stale claim fields", () => {
  const state = freshState("super_admin");
  applyAction(state, { type: "navigate", route: parseRoute("#/family/card_network/list") });
  const card = renderApp(state);
  assert.match(card, /TEST-ONLY/);
  assert.match(card, /note-gap/);
  assert.match(card, /not loaded/);
  assert.equal(card.includes("On (EXAMPLE)"), false);
  assert.match(card, /PQ-269/);
  applyAction(state, { type: "navigate", route: parseRoute("#/family/currency/list") });
  const currency = renderApp(state);
  assert.match(currency, /USD and CAD/);
  assert.match(currency, /not loaded/);
  assert.equal(currency.includes(">USD<"), false);
  const claim = [...state.session.entries.values()].find((entry) => entry.code === "EXAMPLE-CLAIM-P");
  applyAction(state, { type: "navigate", route: parseRoute(`#/family/model_hardware_claim/edit/${claim.entryId}`) });
  const edit = renderApp(state);
  assert.match(edit, /Ordinary same-ID edit/);
  assert.match(edit, /variant_note and supersedes_claim_id are not fields/);
  assert.equal(edit.includes('name="variant_note"'), false);
  assert.equal(edit.includes('name="supersedes_claim_id"'), false);
  assert.match(edit, /name="claim_value"/);
  assert.match(edit, /disabled/);
  assert.match(edit, /Save unavailable pending field gate/);
  applyAction(state, { type: "try-claim-edit" });
  assert.equal(state.outcome.wrote, false);
  assert.equal(claim.entryVersion, 1);
  applyAction(state, { type: "navigate", route: parseRoute("#/family/fee_type/add") });
  const fee = renderApp(state);
  assert.equal(fee.includes('name="economic_role"'), false);
  assert.match(fee, /economic_role and classification are not fields/);
  applyAction(state, { type: "set-field", name: "code", value: "SESSION" });
  applyAction(state, { type: "set-field", name: "displayName", value: "TEST-ONLY session fee" });
  applyAction(state, { type: "submit-fee" });
  assert.equal(state.outcome.status, 201);
  assert.match(state.outcome.displayName || state.outcome.entry.displayName, /TEST-ONLY session fee/);
  const count = state.session.entries.size;
  applyAction(state, { type: "replay-fee" });
  assert.equal(state.session.entries.size, count);
  applyAction(state, { type: "navigate", route: parseRoute(`#/family/fee_type/off/${state.outcome.entryId}`) });
  state.form.reason = "stop this test row";
  const before = state.session.entries.size;
  applyAction(state, { type: "check-activation", mode: "entry", desiredActive: false });
  assert.equal(state.outcome.status, 503);
  assert.equal(state.outcome.wrote, false);
  assert.equal(state.session.entries.size, before);
  const offHtml = renderApp(state);
  assert.match(offHtml, /Affected count/);
  assert.match(offHtml, /Unavailable\. Not zero\./);
  assert.equal(offHtml.includes("0 linked"), false);
  assert.match(offHtml, /Apply unavailable/);
  applyAction(state, { type: "navigate", route: parseRoute("#/family/catalog_release/add") });
  applyAction(state, { type: "try-closed-create" });
  assert.equal(state.outcome.wrote, false);
  assert.equal(state.outcome.detail, "required_fields_unresolved");
  applyAction(state, { type: "navigate", route: parseRoute("#/family/transaction_type/edit/" + findCode(state.session, "transaction_type", "EXAMPLE-2-A", "global").entryId) });
  applyAction(state, { type: "try-metadata" });
  assert.equal(state.outcome.wrote, false);
  assert.equal(state.outcome.detail, "metadata_update_route_withheld");
});

test("empty, denied, and same-code screens stay truthful", () => {
  const state = freshState("partner_admin");
  applyAction(state, { type: "navigate", route: parseRoute("#/shared/empty") });
  const empty = renderApp(state);
  assert.match(empty, /No matching results/);
  applyAction(state, { type: "clear-filters" });
  const cleared = renderApp(state);
  assert.match(cleared, /TEST-ONLY/);
  applyAction(state, { type: "navigate", route: parseRoute("#/shared/denied") });
  const denied = renderApp(state);
  assert.match(denied, /no_management_grant/);
  applyAction(state, { type: "navigate", route: parseRoute("#/family/model_hardware_claim/list") });
  const claimList = renderApp(state);
  assert.match(claimList, /Ordinary same-ID edit|Edit/);
  assert.match(claimList, />Edit</);
  applyAction(state, { type: "navigate", route: parseRoute("#/shared/same-code") });
  const same = renderApp(state);
  assert.equal(state.role, "partner_admin");
  assert.match(same, /value="partner_admin" selected>Partner Admin \(own test partner\)/);
  assert.match(same, /Selected principal: Partner Admin \(own test partner\)/);
  assert.match(same, /11000000-0000-4000-8005-100000000001/);
  assert.match(same, /11000000-0000-4000-8005-200000000001/);
  assert.equal(same.includes("hidden count:"), false);
  applyAction(state, { type: "navigate", route: parseRoute("#/shared/graph") });
  const graph = renderApp(state);
  assert.match(graph, /UNCONFIGURED/);
  assert.match(graph, /Not zero/);
  applyAction(state, { type: "toggle-fixture", on: true });
  assert.equal(state.outcome.graph.affectedCount, null);
  assert.equal(state.outcome.graph.authorizesWrites, false);
});

test("presentation lows keep the denied list, same-code principal, plain result, and policy tense", () => {
  const cashierState = freshState("cashier");
  applyAction(cashierState, { type: "navigate", route: parseRoute("#/family/transaction_type/list") });
  const deniedList = renderApp(cashierState);
  assert.match(deniedList, /Add is omitted/);
  assert.match(deniedList, /This principal has no management grant/);
  assert.match(deniedList, /no_management_grant/);
  assert.match(deniedList, />No</);
  assert.equal(deniedList.includes('href="#/family/transaction_type/add"'), false);
  assert.equal(deniedList.includes(">Add</a>"), false);
  const feeRefusal = submitCreate(cashierState.session, cashier, call("cashier-list"), "fee_type", feeJson());
  assert.equal(feeRefusal.status, 403);
  assert.equal(feeRefusal.wrote, false);
  assert.equal(cashierState.session.entries.size, freshState().session.entries.size);

  const allowed = freshState("super_admin");
  applyAction(allowed, { type: "navigate", route: parseRoute("#/family/transaction_type/list") });
  const allowedList = renderApp(allowed);
  assert.match(allowedList, /href="#\/family\/transaction_type\/add">Add</);
  assert.match(allowedList, /Agreed policy record/);
  assert.match(allowedList, /Currently unavailable on this synthetic page/);
  assert.match(allowedList, /DEC-166/);
  assert.match(allowedList, /DEC-174/);
  assert.equal(/Partner Admin can reverse/.test(allowedList), false);

  const saSameState = freshState("super_admin");
  applyAction(saSameState, { type: "navigate", route: parseRoute("#/shared/same-code") });
  const saSame = renderApp(saSameState);
  assert.equal(saSameState.role, "super_admin");
  assert.match(saSame, /value="super_admin" selected>Super Admin/);
  assert.match(saSame, /Selected principal: Super Admin/);
  assert.match(saSame, /11000000-0000-4000-8005-100000000001/);
  assert.equal(saSame.includes("11000000-0000-4000-8005-200000000001"), true);
  assert.match(saSame, /resolved global Super Admin assignment/);
  assert.equal(/same as a missing row/.test(saSame), false);
  assert.equal(saSame.includes("hidden count:"), false);
  const otherState = freshState("partner_admin_other");
  applyAction(otherState, { type: "navigate", route: parseRoute("#/shared/same-code") });
  const otherSame = renderApp(otherState);
  assert.match(otherSame, /Selected principal: Partner Admin \(other test partner\)/);
  assert.equal(otherSame.includes("11000000-0000-4000-8005-200000000001"), false);
  assert.match(otherSame, /11000000-0000-4000-8005-100000000001/);

  const offState = freshState("super_admin");
  const fee = findCode(offState.session, "fee_type", "EXAMPLE-20-A", "global");
  applyAction(offState, { type: "navigate", route: parseRoute(`#/family/fee_type/off/${fee.entryId}`) });
  offState.form.reason = "stop this test row";
  applyAction(offState, { type: "check-activation", mode: "entry", desiredActive: false });
  const offHtml = renderApp(offState);
  const offCard = offHtml.slice(offHtml.indexOf('aria-label="Request result"'));
  const offReason = "The graph is unknown, so this Off is unavailable. No record was changed.";
  assert.ok(offCard.indexOf(offReason) >= 0);
  assert.ok(offCard.indexOf(offReason) < offCard.indexOf("graph_unconfigured_all_off"));
  assert.match(offCard, /<dt>Wrote<\/dt><dd>No<\/dd>/);
  assert.equal(offState.outcome.wrote, false);

  const claim = [...offState.session.entries.values()].find((entry) => entry.code === "EXAMPLE-CLAIM-P");
  applyAction(offState, { type: "navigate", route: parseRoute(`#/family/model_hardware_claim/on/${claim.entryId}`) });
  offState.form.reason = "reactivate";
  applyAction(offState, { type: "check-activation", mode: "entry", desiredActive: true });
  const onHtml = renderApp(offState);
  const onCard = onHtml.slice(onHtml.indexOf('aria-label="Request result"'));
  const onReason = "Claim entry On is not approved for any actor. No record was changed.";
  assert.ok(onCard.indexOf(onReason) < onCard.indexOf("claim_reactivation_pending_pq272"));
  assert.match(onCard, /<dt>Wrote<\/dt><dd>No<\/dd>/);
  assert.equal(offState.outcome.wrote, false);

  applyAction(offState, { type: "navigate", route: parseRoute(`#/family/fee_type/on/${fee.entryId}`) });
  offState.form.reason = "restore";
  applyAction(offState, { type: "check-activation", mode: "entry", desiredActive: true });
  const restoreHtml = renderApp(offState);
  const restoreCard = restoreHtml.slice(restoreHtml.indexOf('aria-label="Request result"'));
  const restoreReason = "Restoring this value depends on the graph, which is unknown. No record was changed.";
  assert.ok(restoreCard.indexOf(restoreReason) < restoreCard.indexOf("graph_dependent_restore_unavailable"));
  assert.equal(offState.outcome.wrote, false);
});

test("unavailable create copy matches its detail and same-code copy matches the list status", () => {
  assert.equal(
    plainReason({ status: 503, code: "unavailable", gate: "create_dto_unresolved", detail: "create_fields_not_closed", wrote: false }),
    "This create is unavailable because its fields are not closed. Nothing was written.",
  );
  assert.equal(
    plainReason({ status: 503, code: "unavailable", gate: "create_dto_unresolved", detail: "required_fields_unresolved", wrote: false }),
    "This create is unavailable because the required fields are not settled. Nothing was written.",
  );
  const closed = freshState("super_admin");
  const before = closed.session.entries.size;
  applyAction(closed, { type: "navigate", route: parseRoute("#/family/transaction_type/add") });
  applyAction(closed, { type: "try-closed-create" });
  const transaction = renderApp(closed);
  const transactionCard = transaction.slice(transaction.indexOf('aria-label="Request result"'));
  assert.equal(closed.outcome.status, 503);
  assert.equal(closed.outcome.gate, "create_dto_unresolved");
  assert.equal(closed.outcome.detail, "create_fields_not_closed");
  assert.equal(closed.outcome.wrote, false);
  assert.match(transaction, /Create stays unavailable \(create_fields_not_closed\)/);
  assert.match(transactionCard, /its fields are not closed/);
  assert.equal(/required fields are not settled/.test(transactionCard), false);
  assert.ok(transactionCard.indexOf("its fields are not closed") < transactionCard.indexOf("create_fields_not_closed"));
  assert.match(transactionCard, /<dt>Wrote<\/dt><dd>No<\/dd>/);
  assert.notEqual(closed.outcome.status, 201);
  assert.equal(closed.session.entries.size, before);

  applyAction(closed, { type: "navigate", route: parseRoute("#/family/catalog_release/add") });
  applyAction(closed, { type: "try-closed-create" });
  const release = renderApp(closed);
  const releaseCard = release.slice(release.indexOf('aria-label="Request result"'));
  assert.equal(closed.outcome.detail, "required_fields_unresolved");
  assert.equal(closed.outcome.gate, "create_dto_unresolved");
  assert.equal(closed.outcome.wrote, false);
  assert.match(release, /Create stays unavailable \(required_fields_unresolved\)/);
  assert.match(releaseCard, /required fields are not settled/);
  assert.equal(/fields are not closed/.test(releaseCard), false);
  assert.match(releaseCard, /<dt>Wrote<\/dt><dd>No<\/dd>/);
  assert.equal(closed.session.entries.size, before);

  const privateId = "11000000-0000-4000-8005-200000000001";
  const seenPrivate = readEntry(closed.session, sa, "manufacturer", privateId);
  const missing = readEntry(closed.session, sa, "manufacturer", "00000000-0000-4000-8000-000000000099");
  assert.equal(seenPrivate.status, 200);
  assert.equal(sameRefusal(readEntry(closed.session, other, "manufacturer", privateId), missing), true);

  for (const role of ["client_admin", "supervisor", "cashier", "auditor", "accounting", "support", "none"]) {
    const state = freshState(role);
    applyAction(state, { type: "navigate", route: parseRoute("#/shared/same-code") });
    const html = renderApp(state);
    const note = html.slice(html.indexOf('role="note"'), html.indexOf("DEC-170"));
    const card = html.slice(html.indexOf('aria-label="Request result"'));
    assert.equal(state.role, role);
    assert.match(note, new RegExp(`list status is 403 no_management_grant`));
    assert.match(note, /not a missing row/);
    assert.equal(/same as a missing row/.test(note), false);
    assert.equal(/The table is/.test(note), false);
    assert.match(card, /This principal has no management grant\. Nothing was written\./);
    assert.match(card, /403 no_management_grant/);
    assert.match(card, /<dt>Wrote<\/dt><dd>No<\/dd>/);
    assert.equal(card.includes("same as a missing row"), false);
    assert.equal(html.includes(privateId), false);
    assert.equal(listEntries(state.session, actorByKey(role), "manufacturer").status, 403);
  }
});

test("documented color pairs meet WCAG AA contrast", () => {
  const lin = (channel) => {
    const c = channel / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  const luminance = (hex) => {
    const value = parseInt(hex.slice(1), 16);
    return 0.2126 * lin((value >> 16) & 255) + 0.7152 * lin((value >> 8) & 255) + 0.0722 * lin(value & 255);
  };
  const ratio = (fg, bg) => {
    const hi = Math.max(luminance(fg), luminance(bg));
    const lo = Math.min(luminance(fg), luminance(bg));
    return (hi + 0.05) / (lo + 0.05);
  };
  const pairs = [
    ["#ffffff", "#1b2430", 4.5],
    ["#1f2937", "#ffffff", 4.5],
    ["#5b6472", "#ffffff", 4.5],
    ["#5b6472", "#f3f5f8", 4.5],
    ["#1f2937", "#fff4e0", 4.5],
    ["#7a4d00", "#ffffff", 4.5],
    ["#ffffff", "#7a4d00", 4.5],
    ["#ffffff", "#1f2937", 4.5],
    ["#8a94a3", "#ffffff", 3],
  ];
  for (const [fg, bg, minimum] of pairs) assert.ok(ratio(fg, bg) >= minimum, `${fg} on ${bg}`);
});

test("narrow layout keeps the proof token and shows script focus", () => {
  const html = renderApp(freshState("super_admin"));
  assert.match(html, /class="page-foot"/);
  assert.match(html, /synthetic_fixture_projection_not_the_dotnet_double_api_or_database/);
  assert.match(html, /role="alert"|role="status"|<h1 id="screen-title"/);
  const css = readFileSync(join(here, "..", "css", "catalog.css"), "utf8");
  assert.match(css, /:focus-visible \{ outline: 3px solid var\(--focus\); outline-offset: 2px; \}/);
  assert.match(css, /\.program-focus \{ outline: 3px solid var\(--focus\); outline-offset: 2px; \}/);
  assert.match(css, /\.page-foot, \.fine \{ overflow-wrap: anywhere; \}/);
  assert.match(css, /\.actor-bar > \* \{ min-width: 0; max-width: 100%; \}/);
  assert.match(css, /td, th, caption \{ overflow-wrap: anywhere; \}/);
  const app = readFileSync(join(here, "..", "js", "app.mjs"), "utf8");
  assert.match(app, /scrollTo\(0, 0\)/);
  assert.match(app, /classList\.add\("program-focus"\)/);
  assert.match(app, /addEventListener\("focusin"/);
  assert.equal(app.includes("localStorage"), false);
});

test("presentation source does not touch storage, host, or the double", () => {
  const sourceDir = join(here, "..", "js");
  const text = readdirSync(sourceDir).map((name) => readFileSync(join(sourceDir, name), "utf8")).join("\n");
  for (const banned of ["localStorage", "sessionStorage", "fetch(", "XMLHttpRequest", "CatalogDomainService", "Admin.Api", "Npgsql", "indexedDB"]) {
    assert.equal(text.includes(banned), false, banned);
  }
});

test("imported contract files stay byte-identical", () => {
  const expected = {
    "src/Catalog.Contracts/CatalogAudit.cs": "b7b0a0756f173ac2849ab4bbce31f9d40e6237158d343b6d328d8ed9b7de4b6d",
    "src/Catalog.Contracts/CatalogCommands.cs": "a530c79ead81b8e4135a39619d069ae70fa5c5a8e73ea221cc713d27e9e0846d",
    "src/Catalog.Contracts/Catalog.Contracts.csproj": "bdd906f6cb155efc6721f590c3ba5f71abdab981f2d1dde92773f55b1ad0e2f1",
    "src/Catalog.Contracts/CatalogFamily.cs": "8d3aa3917a9dc89c051342e0c53c14ea40cd43bddccbcbd7d0fbde1112827b22",
    "src/Catalog.Contracts/CatalogGraph.cs": "ca566431571281784750464e3bbce2f46c65f6b07c7c038421b69ed87818b831",
    "src/Catalog.Contracts/CatalogOutcomes.cs": "2d0adec01a1507e114b17ae5c207431f9c07f1663e5a2275289340181906f083",
    "src/Catalog.Contracts/CatalogPrincipal.cs": "7c3df3b9d3288de8c079276ed01e13715431c8bc4126f2524988f75f9f07056e",
    "src/Catalog.Contracts.Checks/Catalog.Contracts.Checks.csproj": "3379645588ddc925aa92e97e367878844c532e7d6ab8b4ba4b67ab194ccd530d",
    "src/Catalog.Contracts.Checks/packages.lock.json": "8f105936eeb4a67e0e5a59bea0345b679bd0fd675f3252fa213f2fee3630f9ad",
    "src/Catalog.Contracts.Checks/Program.cs": "c8ac4885cc788eaed676bc567495f44b0c760b0d3fff6001cf7e3f90537d2959",
    "src/Catalog.Contracts/Doubles/CatalogDomainService.cs": "31f2a8747f37474f71c49d62d11798b0140a3b9b35ff369545313d2fecb598c6",
    "src/Catalog.Contracts/Doubles/InMemoryCatalogRepository.cs": "ab633f65b36af87bc887cf4f5f7fd2322d32e9c3b81684a1bf42c7249170af1a",
    "src/Catalog.Contracts/Doubles/ScopeVersionTable.cs": "b7ba1d860f5a95e3904b8a2287415aa820a7ec3d8c9dd2c2b2a52b9bafb2052c",
    "src/Catalog.Contracts/ISOLATED-CONTRACT.md": "18915e60cd54597aac580fd68e51d03a2027f9573093921870e6934324b89135",
    "src/Catalog.Contracts/packages.lock.json": "98540f8c8005e000cc83cffb4598281f61900a13426b470f1d1114c62b54a63d",
    "src/Catalog.Contracts/VersionToken.cs": "3d621c76f0b9fb93711b4f9c7c267ffc928ee6fd07fb7502868309d7c606aafd",
  };
  for (const [path, hash] of Object.entries(expected)) {
    const full = join(root, path);
    assert.equal(statSync(full).isFile(), true, path);
    assert.equal(createHash("sha256").update(readFileSync(full)).digest("hex"), hash, path);
  }
});
