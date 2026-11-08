/** Screen actions. Session changes stay in memory for this page load only. */
import { familyByWire } from "./catalogs.mjs";
import {
  actorByKey,
  createSession,
  describeGraph,
  listEntries,
  preview,
  readEntry,
  readHistory,
  submitClaimEdit,
  submitCreate,
  submitEntryActivation,
  submitLocationActivation,
  submitPartnerActivation,
} from "./projection.mjs";

export function freshState(role = "super_admin") {
  return {
    role,
    route: { screen: "overview" },
    query: "",
    scope: "all",
    form: emptyForm(),
    outcome: null,
    session: createSession(),
    fixtureGraph: false,
    lastFee: null,
  };
}

export function emptyForm() {
  return {
    code: "",
    displayName: "",
    description: "",
    reason: "",
    partnerText: "",
    locationText: "",
  };
}

export function attachSession(state, session) {
  state.session = session;
  return state;
}

export function parseRoute(hash) {
  const raw = (hash || "").replace(/^#/, "");
  const parts = raw.split("/").filter(Boolean);
  if (parts.length === 0 || (parts.length === 1 && parts[0] === "overview")) return { screen: "overview" };
  if (parts[0] === "map") return { screen: "map" };
  if (parts[0] === "shared" && parts[1] === "same-code") return { screen: "same-code" };
  if (parts[0] === "shared" && parts[1] === "empty") return { screen: "empty" };
  if (parts[0] === "shared" && parts[1] === "denied") return { screen: "denied" };
  if (parts[0] === "shared" && parts[1] === "graph") return { screen: "graph" };
  if (parts[0] === "family" && parts.length >= 2) {
    const family = parts[1];
    const screen = parts[2] || "list";
    const known = ["list", "view", "add", "edit", "off", "on", "partner", "location", "history"];
    if (!known.includes(screen)) return { screen: "malformed", family, detail: "unknown_screen" };
    const entryId = parts[3] || null;
    if (screen !== "list" && screen !== "add" && !entryId) return { screen: "malformed", family, detail: "missing_id" };
    return { screen, family, entryId };
  }
  return { screen: "malformed", detail: "unknown_route" };
}

export function hrefFor(route) {
  if (!route || route.screen === "overview") return "#/overview";
  if (route.screen === "map") return "#/map";
  if (route.screen === "same-code") return "#/shared/same-code";
  if (route.screen === "empty") return "#/shared/empty";
  if (route.screen === "denied") return "#/shared/denied";
  if (route.screen === "graph") return "#/shared/graph";
  if (route.screen === "malformed") return "#/malformed";
  if (route.screen === "list" || route.screen === "add") return `#/family/${route.family}/${route.screen}`;
  return `#/family/${route.family}/${route.screen}/${route.entryId}`;
}

export function applyAction(state, action) {
  const next = state;
  if (action.type === "navigate") {
    if (action.route.screen === "empty") next.query = "___no_such_test_row___";
    else if (action.route.family !== next.route.family) next.query = "";
    next.route = action.route;
    next.outcome = null;
    return next;
  }
  if (action.type === "set-role") {
    next.role = action.role;
    next.outcome = null;
    return next;
  }
  if (action.type === "set-query") {
    next.query = action.query;
    return next;
  }
  if (action.type === "set-scope") {
    next.scope = action.scope;
    return next;
  }
  if (action.type === "set-field") {
    next.form = { ...next.form, [action.name]: action.value };
    return next;
  }
  if (action.type === "clear-filters") {
    next.query = "";
    next.scope = "all";
    next.outcome = null;
    return next;
  }
  if (action.type === "toggle-fixture") {
    next.fixtureGraph = action.on;
    next.outcome = describeGraph(action.on ? { state: "fixture", revision: "presentation-fixture", edges: [] } : { state: "unconfigured" });
    return next;
  }
  if (action.type === "check-activation") return checkActivation(next, action);
  if (action.type === "submit-fee") return submitFee(next);
  if (action.type === "replay-fee") return replayFee(next);
  if (action.type === "try-claim-edit") return tryClaimEdit(next);
  if (action.type === "try-metadata") return tryMetadata(next);
  if (action.type === "try-closed-create") return tryClosedCreate(next);
  return next;
}

function call(key) {
  return {
    idempotencyKey: key,
    correlationId: "synthetic-correlation",
    sourceRequestId: "synthetic-request",
  };
}

function checkActivation(state, action) {
  const actor = actorByKey(state.role);
  const body = JSON.stringify({
    desired_active: action.desiredActive,
    reason: state.form.reason,
    expected_version: "ABSENT",
  });
  const serverCall = call(`synthetic-${action.mode}-${Date.now()}`);
  const family = state.route.family;
  const entryId = state.route.entryId;
  let outcome;
  if (action.mode === "entry") outcome = submitEntryActivation(state.session, actor, serverCall, family, entryId, body);
  else if (action.mode === "partner") outcome = submitPartnerActivation(state.session, actor, serverCall, family, entryId, state.form.partnerText, body);
  else outcome = submitLocationActivation(state.session, actor, serverCall, family, entryId, state.form.locationText, body);
  state.outcome = outcome;
  return state;
}

function submitFee(state) {
  const actor = actorByKey(state.role);
  const body = { code: state.form.code, display_name: state.form.displayName };
  if (state.form.description.trim() !== "") body.description = state.form.description;
  const json = JSON.stringify(body);
  const serverCall = call(`fee-${Date.now()}-${Math.random().toString(16).slice(2)}`);
  state.lastFee = { call: serverCall, json };
  state.outcome = submitCreate(state.session, actor, serverCall, "fee_type", json);
  return state;
}

function replayFee(state) {
  if (!state.lastFee) {
    state.outcome = { status: 400, code: "no_prior_fee_submission", detail: "no_prior_fee_submission", wrote: false, integrated: false };
    return state;
  }
  state.outcome = submitCreate(state.session, actorByKey(state.role), state.lastFee.call, "fee_type", state.lastFee.json);
  return state;
}

function tryClaimEdit(state) {
  const actor = actorByKey(state.role);
  const entry = readEntry(state.session, actor, state.route.family, state.route.entryId);
  const version = entry.entry?.entryVersion ?? 1;
  const json = JSON.stringify({
    expected_version: version,
    claim_value: "TEST-ONLY-edited-value",
  });
  state.outcome = submitClaimEdit(
    state.session,
    actor,
    call(`claim-${Date.now()}`),
    state.route.family,
    state.route.entryId,
    json,
  );
  return state;
}

function tryMetadata(state) {
  state.outcome = {
    status: 503,
    code: "unavailable",
    gate: "no_metadata_update_command",
    detail: "metadata_update_route_withheld",
    wrote: false,
    integrated: false,
    proofBoundary: "synthetic_fixture_projection_not_the_dotnet_double_api_or_database",
  };
  return state;
}

function tryClosedCreate(state) {
  const family = familyByWire(state.route.family);
  const json = JSON.stringify({ code: state.form.code || "EXAMPLE", display_name: state.form.displayName || "TEST-ONLY" });
  state.outcome = submitCreate(state.session, actorByKey(state.role), call(`create-${Date.now()}`), state.route.family, json);
  if (family?.wire === "fee_type") return state;
  return state;
}

export function visibleRows(state) {
  const listed = listEntries(state.session, actorByKey(state.role), state.route.family);
  if (listed.status !== 200) return listed;
  const query = state.query.trim().toLowerCase();
  const entries = listed.list.entries.filter((entry) => {
    if (state.scope === "global" && entry.entryClass !== "global") return false;
    if (state.scope === "private" && entry.entryClass !== "private") return false;
    if (!query) return true;
    return [entry.code, entry.displayName, entry.description, entry.entryId].join(" ").toLowerCase().includes(query);
  });
  return { ...listed, list: { ...listed.list, entries } };
}

export function entryFor(state) {
  if (!state.route.entryId) return null;
  return readEntry(state.session, actorByKey(state.role), state.route.family, state.route.entryId);
}

export function historyFor(state) {
  return readHistory(state.session, actorByKey(state.role), state.route.family, state.route.entryId);
}

export function graphReading(state) {
  return describeGraph(state.fixtureGraph ? { state: "fixture", revision: "presentation-fixture", edges: [] } : { state: "unconfigured" });
}

export function impactReading(state) {
  if (!state.route.entryId) return null;
  return preview(
    state.session,
    actorByKey(state.role),
    state.route.family,
    state.route.entryId,
    state.fixtureGraph ? { state: "fixture", revision: "presentation-fixture", edges: [] } : { state: "unconfigured" },
  );
}
