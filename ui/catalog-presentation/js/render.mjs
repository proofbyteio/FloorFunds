/** Presentation markup. Accepted-contract gates override stale On/Off screen flows. */
import { ACTORS, FAMILIES, GROUPS, familyByWire, padFamily } from "./catalogs.mjs";
import { GATES, OPEN_POLICIES, PROOF_BOUNDARY } from "./projection.mjs";
import { entryFor, graphReading, historyFor, hrefFor, impactReading, visibleRows } from "./actions.mjs";

export function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
}

export function renderApp(state) {
  const actor = ACTORS[state.role];
  const title = screenTitle(state);
  return `
    <a class="skip-link" href="#main">Skip to content</a>
    <p class="draft-banner">TEST-ONLY presentation. Synthetic fixture projection of the accepted catalog contract. Not the .NET double, not an API, and not a database. Rows are not production seeds.</p>
    <header class="topbar">
      <p class="crumbs">Cash Advance / Administration / Master data</p>
      <p class="context">Synthetic review context: ${esc(actor.label)}. Not authentication.</p>
    </header>
    <div class="actor-bar">
      <label for="actor-role">Synthetic principal</label>
      <select id="actor-role" name="actor-role">
        ${Object.entries(ACTORS).map(([key, item]) => `<option value="${esc(key)}"${key === state.role ? " selected" : ""}>${esc(item.label)}</option>`).join("")}
      </select>
      <p>No auth claim is made. A Partner Admin id is the synthetic principal's partner, syntax only, not a verified registry.</p>
    </div>
    <div class="shell">
      <nav class="rail" aria-label="Sections">
        <a href="#/overview" aria-label="Master data"${state.route.screen === "overview" ? ' aria-current="page"' : ""}><span aria-hidden="true">M</span></a>
        <a href="#/map" aria-label="History and route map"><span aria-hidden="true">H</span></a>
        <a href="#/family/processor/list" aria-label="Other directories"><span aria-hidden="true">D</span></a>
        <a href="#/shared/graph" aria-label="Availability and graph"${state.route.screen === "graph" ? ' aria-current="page"' : ""}><span aria-hidden="true">A</span></a>
        <a href="#/shared/same-code" aria-label="Same code, distinct identities"><span aria-hidden="true">C</span></a>
        <a href="#/map" aria-label="Help and unimplemented flows"><span aria-hidden="true">?</span></a>
      </nav>
      <nav class="side" aria-label="Catalogs">
        <p class="side-title">Master data</p>
        <a href="#/overview"${state.route.screen === "overview" ? ' aria-current="page"' : ""}>Catalog overview</a>
        ${GROUPS.filter((group) => group !== "Master data").map((group) => `<p class="group">${esc(group)}</p>${linkGroup(state, group)}`).join("")}
        <a href="#/shared/graph">Availability</a>
        <a href="#/map">History / sources</a>
        <a href="#/shared/same-code">Global list + private</a>
        <p class="side-note">Synthetic principals only. Field persistence stays disabled except names-only fee create in this session. No Client Admin grant.</p>
      </nav>
      <main id="main" tabindex="-1">
        <h1 id="screen-title" tabindex="-1">${esc(title)}</h1>
        ${renderMain(state)}
      </main>
    </div>
    <footer class="page-foot">Synthetic presentation. No live mutation reaches an API or database. TEST-ONLY records. Proof: ${esc(PROOF_BOUNDARY)}.</footer>`;
}

function linkGroup(state, group) {
  return FAMILIES.filter((family) => family.group === group).map((family) => {
    const current = state.route.family === family.wire && state.route.screen === "list";
    return `<a href="#/family/${family.wire}/list"${current ? ' aria-current="page"' : ""}>${esc(padFamily(family.n))} ${esc(family.title)}</a>`;
  }).join("");
}

function screenTitle(state) {
  const route = state.route;
  if (route.screen === "overview") return "Catalog overview";
  if (route.screen === "map") return "Routes and unimplemented flows";
  if (route.screen === "same-code") return "Same code, distinct identities";
  if (route.screen === "empty") return "Empty and failure states";
  if (route.screen === "denied") return "Conditional access";
  if (route.screen === "graph") return "Dependency graph";
  if (route.screen === "malformed") return "Unrecognized route";
  const family = familyByWire(route.family);
  if (!family) return "Unrecognized catalog";
  const labels = { list: "list", view: "view", add: "add", edit: "edit", off: "turn off", on: "turn on", partner: "partner switch", location: "location switch", history: "history" };
  return `${padFamily(family.n)} / ${family.title} / ${labels[route.screen]}`;
}

function renderMain(state) {
  const route = state.route;
  if (route.screen === "overview") return overview(state);
  if (route.screen === "map") return mapScreen();
  if (route.screen === "same-code") return sameCode(state);
  if (route.screen === "empty") return emptyScreen(state);
  if (route.screen === "denied") return deniedScreen(state);
  if (route.screen === "graph") return graphScreen(state);
  if (route.screen === "malformed") return `<p role="alert">This route is not a catalog screen. Nothing was written.</p>`;
  const family = familyByWire(route.family);
  if (!family) return `<p role="alert">invalid_family. Status 400. Nothing was written.</p>`;
  if (route.screen === "list") return listScreen(state, family);
  if (route.screen === "add") return addScreen(state, family);
  if (route.screen === "view") return recordScreen(state, family, "view");
  if (route.screen === "edit") return recordScreen(state, family, "edit");
  if (route.screen === "history") return historyScreen(state, family);
  if (route.screen === "off" || route.screen === "on" || route.screen === "partner" || route.screen === "location") return activationScreen(state, family);
  return `<p role="alert">Unknown screen. Nothing was written.</p>`;
}

function overview() {
  const cards = FAMILIES.map((family) => `
    <article class="catalog-card">
      <h2>${esc(padFamily(family.n))} ${esc(family.title)}</h2>
      <p>${esc(family.wire)}</p>
      <p>${family.privateAdd ? "Private Add is a UX candidate for the owning Partner Admin." : "No Partner Admin private Add for this catalog."} Create disposition: ${esc(family.disposition)}.</p>
      <p class="actions"><a href="#/family/${family.wire}/list">List</a> <a href="#/family/${family.wire}/add">Add</a></p>
    </article>`).join("");
  return `
    <p class="lede">Twenty catalogs. Operations follow the accepted contract. The dependency graph is UNCONFIGURED.</p>
    ${policyNote()}
    <div class="card-grid">${cards}</div>
    <section class="panel" aria-labelledby="shared-heading">
      <h2 id="shared-heading">Shared checks</h2>
      <p class="actions">
        <a href="#/shared/same-code">Same code</a>
        <a href="#/shared/empty">Empty results</a>
        <a href="#/shared/denied">No management grant</a>
        <a href="#/shared/graph">Unknown graph</a>
        <a href="#/map">What is not built</a>
      </p>
    </section>`;
}

function mapScreen() {
  const rows = FAMILIES.map((family) => `<li><a href="#/family/${family.wire}/list">${esc(padFamily(family.n))} ${esc(family.title)} list</a> · <a href="#/family/${family.wire}/add">add</a></li>`).join("");
  return `
    <p>Use these links with the keyboard. Tab reaches the skip link, principal, catalog links, and the actions on each screen.</p>
    <ul class="route-list">${rows}</ul>
    <section class="panel" aria-labelledby="unbuilt-heading">
      <h2 id="unbuilt-heading">Not built</h2>
      <ul>
        <li>PQ-269 cage-program classification and PQ-270 charge-card mapping. No card eligibility is invented.</li>
        <li>PQ-271 private-master location Off preservation. No location-off memory is invented.</li>
        <li>PQ-272 claim entry On. Unavailable for every actor who can address the claim, not an SA-only grant.</li>
        <li>PQ-252.SA3 private partner-switch, PQ-252.SA6 further private-code collisions, and private preservation.</li>
        <li>PQ-263.1 directed graph, fan-out, cycles, cause engine, and impact counts.</li>
        <li>Six-field claim persistence (PQ-252.SA1). Same-ID edit is shown and does not save.</li>
        <li>Release, ID-document, currency, and every other non-fee create.</li>
        <li>HTTP transport, database, authentication, seeds, and durable audit acceptance.</li>
      </ul>
    </section>`;
}

function listScreen(state, family) {
  const listed = visibleRows(state);
  const below = ownerNote(family);
  return `
    ${familyBlurb(family, state)}
    ${policyNote(family)}
    <form class="filters" action="#/family/${family.wire}/list">
      <div class="filter-field">
        <label for="catalog-query">Search label / code</label>
        <input id="catalog-query" name="query" value="${esc(state.query)}" autocomplete="off">
      </div>
      <div class="filter-field">
        <label for="scope-filter">Scope filter</label>
        <select id="scope-filter" name="scope">
          <option value="all"${state.scope === "all" ? " selected" : ""}>All visible test rows</option>
          <option value="global"${state.scope === "global" ? " selected" : ""}>Global only</option>
          <option value="private"${state.scope === "private" ? " selected" : ""}>Private only</option>
        </select>
      </div>
      <button type="button" id="search-button" data-action="search">Search</button>
      <a class="button-link" href="#/family/${family.wire}/add">Add</a>
    </form>
    ${listed.status !== 200 ? outcomeBlock(listed) : resultsTable(state, family, listed)}
    ${below}
    <p class="fine">Pagination is not a cursor contract. Bulk import is excluded. Export is not available. Search keeps this query until you clear it.</p>`;
}

function resultsTable(state, family, listed) {
  const entries = listed.list.entries;
  if (entries.length === 0) {
    return `
      <section class="results-card empty-card" aria-labelledby="empty-heading">
        <h2 id="empty-heading">No matching results</h2>
        <p>No TEST-ONLY values match the current search or scope filter. This is not a claim that the catalog has no values, and it is not an access decision.</p>
        <button type="button" id="clear-filters" data-action="clear-filters">Clear filters</button>
      </section>`;
  }
  const body = entries.map((entry) => `
    <tr>
      <td><strong class="test-flag">TEST-ONLY</strong> ${esc(entry.code)}<br><span class="muted">${esc(entry.entryClass)} · ${esc(entry.entryId)}</span></td>
      <td>${esc(entry.displayName)}</td>
      <td>Unknown<br><span class="muted">Entry activation ABSENT</span></td>
      <td class="row-actions">
        <a href="${hrefFor({ screen: "view", family: family.wire, entryId: entry.entryId })}">View</a>
        <a href="${hrefFor({ screen: "edit", family: family.wire, entryId: entry.entryId })}">Edit</a>
        <a href="${hrefFor({ screen: "off", family: family.wire, entryId: entry.entryId })}">Turn Off</a>
        <a href="${hrefFor({ screen: "on", family: family.wire, entryId: entry.entryId })}">Turn On</a>
      </td>
    </tr>`).join("");
  return `
    <section class="results-card" aria-labelledby="results-heading">
      <h2 id="results-heading">TEST-ONLY results / ${esc(family.wire)}</h2>
      <p class="test-banner">Every row in this table is TEST-ONLY. None of these are production seeds or loaded owner targets.</p>
      <div class="table-scroll">
        <table>
          <caption>Visible synthetic ${esc(family.title)} rows for this principal. Effective availability is unknown.</caption>
          <thead><tr><th scope="col">Record / identity</th><th scope="col">Descriptive label</th><th scope="col">Effective availability</th><th scope="col">Actions</th></tr></thead>
          <tbody>${body}</tbody>
        </table>
      </div>
      <p class="fine">${entries.length} visible TEST-ONLY row${entries.length === 1 ? "" : "s"}. Hidden rows are omitted and not counted. Per-row actions open that row only.</p>
    </section>`;
}

function addScreen(state, family) {
  const actor = ACTORS[state.role];
  const management = actor.role === "super_admin" || actor.role === "partner_admin";
  const fee = family.disposition === "names_only";
  const title = actor.role === "partner_admin" && family.privateAdd
    ? `Partner Admin private ${family.wire} — Add`
    : `Add ${family.wire}`;
  return `
    ${familyBlurb(family, state)}
    ${policyNote(family)}
    <section class="panel" aria-labelledby="add-heading">
      <h2 id="add-heading">${esc(title)}</h2>
      <p>${fee ? "Names only: code, display name, and optional description. economic_role and classification are not fields. No dummy classification is stored." : "This create DTO is unavailable. Unapproved fields stay disabled."}</p>
      ${fee && management ? feeForm(state) : disabledCreate(family, management)}
      ${state.outcome ? outcomeBlock(state.outcome) : ""}
      ${state.outcome?.wrote ? `<p><button type="button" id="replay-fee" data-action="replay-fee">Submit again with the same idempotency key</button></p>` : ""}
      <p><a href="#/family/${family.wire}/list">Back to list</a></p>
    </section>`;
}

function feeForm(state) {
  return `
    <form class="stack-form" action="#/family/fee_type/add">
      <div class="filter-field">
        <label for="fee-code">Code</label>
        <input id="fee-code" name="code" maxlength="64" required value="${esc(state.form.code)}" autocomplete="off">
      </div>
      <div class="filter-field">
        <label for="fee-name">Display name</label>
        <input id="fee-name" name="display_name" maxlength="200" required value="${esc(state.form.displayName)}" autocomplete="off">
      </div>
      <div class="filter-field">
        <label for="fee-description">Description, optional</label>
        <textarea id="fee-description" name="description" maxlength="2000">${esc(state.form.description)}</textarea>
      </div>
      <p class="field-help" id="fee-help">Recording a names-only fee here adds a synthetic session row. Refresh discards it. The audit intent is not emitted and is not durable acceptance.</p>
      <button type="button" class="primary" id="fee-save" data-action="submit-fee" aria-describedby="fee-help">Record names-only fee in this synthetic session</button>
    </form>`;
}

function disabledCreate(family, management) {
  const detail = !management ? "no_management_grant" : family.disposition === "required_fields_unresolved" ? "required_fields_unresolved" : "create_fields_not_closed";
  return `
    <form class="stack-form" action="#/family/${family.wire}/add">
      <div class="filter-field"><label for="closed-code">Code</label><input id="closed-code" name="code" disabled value=""></div>
      <div class="filter-field"><label for="closed-name">Display name</label><input id="closed-name" name="display_name" disabled value=""></div>
      <p class="field-help" id="create-help">Create stays unavailable (${esc(detail)}). ${family.privateAdd ? "Private Add remains a UX candidate and is not a closed DTO." : "No Partner Admin private Add for this catalog."}</p>
      <button type="button" id="closed-create" data-action="try-closed-create" aria-describedby="create-help">Check create request</button>
      <button type="button" disabled aria-describedby="create-help">Save unavailable</button>
    </form>`;
}

function recordScreen(state, family, mode) {
  const reading = entryFor(state);
  if (!reading || reading.status !== 200) return `${outcomeBlock(reading || { status: 404, code: "not_found", detail: "not_found", wrote: false })}<p><a href="#/family/${family.wire}/list">Back to list</a></p>`;
  const entry = reading.entry;
  const claim = family.claim;
  return `
    ${familyBlurb(family, state)}
    <p class="test-banner">TEST-ONLY ${esc(entry.origin)} row. ${esc(entry.displayName)}</p>
    <dl class="facts">
      <div><dt>Code</dt><dd>${esc(entry.code)}</dd></div>
      <div><dt>Display name</dt><dd>${esc(entry.displayName)}</dd></div>
      <div><dt>Description</dt><dd>${esc(entry.description)}</dd></div>
      <div><dt>Scope</dt><dd>${esc(entry.scopeKind)}</dd></div>
      <div><dt>Entry id</dt><dd>${esc(entry.entryId)}</dd></div>
      <div><dt>Entry version</dt><dd>${esc(entry.entryVersion)}</dd></div>
      <div><dt>Owner partner</dt><dd>${entry.ownerPartnerId ? esc(entry.ownerPartnerId) : "Not applicable"}</dd></div>
      <div><dt>Effective availability</dt><dd>Unknown</dd></div>
      <div><dt>Entry activation</dt><dd>ABSENT. Active is not stored. Version is not 0.</dd></div>
      <div><dt>Partner switch</dt><dd>ABSENT</dd></div>
      <div><dt>Location switch</dt><dd>ABSENT</dd></div>
    </dl>
    ${claim ? claimEditor(state, entry, mode) : metadataEditor(mode)}
    ${familyNotes(family)}
    ${state.outcome ? outcomeBlock(state.outcome) : ""}
    <p class="actions">
      <a href="${hrefFor({ screen: "off", family: family.wire, entryId: entry.entryId })}">Turn Off</a>
      <a href="${hrefFor({ screen: "on", family: family.wire, entryId: entry.entryId })}">Turn On</a>
      <a href="${hrefFor({ screen: "partner", family: family.wire, entryId: entry.entryId })}">Partner switch</a>
      <a href="${hrefFor({ screen: "location", family: family.wire, entryId: entry.entryId })}">Location switch</a>
      <a href="${hrefFor({ screen: "history", family: family.wire, entryId: entry.entryId })}">History</a>
      <a href="#/family/${family.wire}/list">Back to list</a>
    </p>`;
}

function claimEditor(state, entry, mode) {
  const claimEdit = entry.allowedActions.find((item) => item.action === "claim_edit");
  const fields = [
    ["claim_value", entry.fields.claim_value ?? ""],
    ["availability", entry.fields.availability ?? ""],
    ["source_url", entry.fields.source_url ?? ""],
    ["checked_at", entry.fields.checked_at ?? ""],
    ["display_name", entry.displayName],
    ["description", entry.description ?? ""],
  ];
  const inputs = fields.map(([name, value]) => `
    <div class="filter-field">
      <label for="claim-${esc(name)}">${esc(name)}</label>
      <input id="claim-${esc(name)}" name="${esc(name)}" disabled value="${esc(value)}">
    </div>`).join("");
  return `
    <section class="panel" aria-labelledby="claim-edit-heading">
      <h2 id="claim-edit-heading">${mode === "edit" ? "Ordinary same-ID edit" : "Claim fields"}</h2>
      <p>DEC-172 edit is the same entry id. This is not a new claim. variant_note and supersedes_claim_id are not fields. The six-field allowlist is unresolved, so the controls and persistence are disabled (PQ-252.SA1).</p>
      <p>Actor gate: ${esc(claimEdit?.gate || "catalog_claim_edit_actor")}. Allowed in the contract for this principal: ${claimEdit?.allowed ? "yes, and this screen still does not save" : "no"}.</p>
      <form class="stack-form">${inputs}
        <p class="field-help" id="claim-help">Identity, model, capability, option key, code, and active flag are not editable here.</p>
        <button type="button" id="try-claim-edit" data-action="try-claim-edit" aria-describedby="claim-help">Try same-ID edit (persistence stays off)</button>
        <button type="button" disabled aria-describedby="claim-help">Save unavailable pending field gate</button>
      </form>
    </section>`;
}

function metadataEditor(mode) {
  return `
    <section class="panel">
      <h2>${mode === "edit" ? "Metadata edit" : "Read-only value"}</h2>
      <p>No metadata update command is in the accepted slice. Display name and description stay disabled. Nothing on this screen changes the row.</p>
      <button type="button" id="try-metadata" data-action="try-metadata">Try metadata save</button>
      <button type="button" disabled>Save unavailable</button>
    </section>`;
}

function activationScreen(state, family) {
  const reading = entryFor(state);
  if (!reading || reading.status !== 200) return `${outcomeBlock(reading || { status: 404, code: "not_found", detail: "not_found", wrote: false })}<p><a href="#/family/${family.wire}/list">Back to list</a></p>`;
  const entry = reading.entry;
  const mode = state.route.screen;
  const desired = mode === "on";
  const impact = impactReading(state);
  const preview = impact?.preview;
  const graph = graphReading(state).graph;
  const claimOn = family.claim && mode === "on";
  return `
    <p class="test-banner">TEST-ONLY ${esc(entry.code)} · ${esc(entry.displayName)}</p>
    ${claimOn ? `<p class="amber" role="note">Claim entry On is unavailable for every actor, including Super Admin (${esc(OPEN_POLICIES.claimReactivation)}). It is not an SA-only control.</p>` : ""}
    ${mode === "off" ? `<p class="amber" role="note">The graph is UNCONFIGURED. Every Off is unavailable and writes nothing. HTTP 503, gate ${esc(GATES.graphOff)}. Impact is unknown. This screen does not show a linked-record count.</p>` : ""}
    ${mode === "on" && !claimOn ? `<p class="amber" role="note">Graph-dependent restore is unavailable and writes nothing. Source On does not restore affected values. A manual Off is not cleared here. No impact count is shown.</p>` : ""}
    ${mode === "partner" ? `<p class="amber" role="note">An SA partner UUID is syntax-checked only. It is not a verified partner. Private partner-switch representation is unresolved (${esc(OPEN_POLICIES.privatePartnerSwitch)}).</p>` : ""}
    ${mode === "location" ? `<p class="amber" role="note">Super Admin has no location switch. Partner Admin location membership is not verified. PQ-271 preservation is not built.</p>` : ""}
    <section class="panel" aria-labelledby="impact-heading">
      <h2 id="impact-heading">Impact</h2>
      <dl class="facts">
        <div><dt>Graph</dt><dd>${esc(graph.state)}</dd></div>
        <div><dt>Impact</dt><dd>${esc(preview?.impact || graph.impact)}</dd></div>
        <div><dt>Affected count</dt><dd>Unavailable. Not zero.</dd></div>
        <div><dt>Apply allowed</dt><dd>No</dd></div>
        <div><dt>Fixture edges</dt><dd>${graph.fixtureDeclaredEdgeCount == null ? "Not a fixture" : `${esc(graph.fixtureDeclaredEdgeCount)} declared, which is not an impact count`}</dd></div>
      </dl>
      <label class="checkline" for="fixture-toggle"><input id="fixture-toggle" type="checkbox" data-action="toggle-fixture"${state.fixtureGraph ? " checked" : ""}> Describe a non-authorizing fixture graph</label>
    </section>
    <form class="stack-form">
      ${mode === "partner" ? `<div class="filter-field"><label for="partner-text">Partner UUID, syntax only</label><input id="partner-text" name="partner" value="${esc(state.form.partnerText)}" autocomplete="off"></div>` : ""}
      ${mode === "location" ? `<div class="filter-field"><label for="location-text">Location UUID</label><input id="location-text" name="location" value="${esc(state.form.locationText)}" autocomplete="off"></div>` : ""}
      <div class="filter-field"><label for="reason">Written reason</label><textarea id="reason" name="reason">${esc(state.form.reason)}</textarea></div>
      <p class="field-help" id="activation-help">Expected switch version is ABSENT. Checking this request does not apply it. Currency and transaction status history effects are not executed here.</p>
      <p class="actions">
        <button type="button" id="check-activation" data-action="check-activation" data-desired="${desired ? "true" : "false"}" data-mode="${mode === "on" || mode === "off" ? "entry" : mode}" aria-describedby="activation-help">Check this request</button>
        <button type="button" disabled aria-describedby="activation-help">Apply unavailable</button>
        <a href="${hrefFor({ screen: "view", family: family.wire, entryId: entry.entryId })}">Back to the record</a>
      </p>
    </form>
    ${state.outcome ? outcomeBlock(state.outcome) : ""}`;
}

function historyScreen(state, family) {
  const reading = historyFor(state);
  if (!reading || reading.status !== 200) return `${outcomeBlock(reading || { status: 404, code: "not_found", detail: "not_found", wrote: false })}<p><a href="#/family/${family.wire}/list">Back to list</a></p>`;
  const rows = reading.history;
  const body = rows.length === 0
    ? `<p>No synthetic history is recorded for this fixture row. Actor user id is omitted from history (${esc(OPEN_POLICIES.readAudience)}).</p>`
    : `<table><caption>Synthetic history. Actor user id is omitted.</caption><thead><tr><th scope="col">Change</th><th scope="col">Version</th><th scope="col">Fields</th><th scope="col">When</th></tr></thead><tbody>
      ${rows.map((row) => `<tr><td>${esc(row.changeKind)}</td><td>${esc(row.entryVersion)}</td><td>${esc(row.changedFields.join(", "))}</td><td>${esc(row.occurredAt)}</td></tr>`).join("")}
      </tbody></table>`;
  return `<section class="panel"><h2>History</h2>${body}<p><a href="#/family/${family.wire}/list">Back to list</a></p></section>${state.outcome ? outcomeBlock(state.outcome) : ""}`;
}

function sameCode(state) {
  const previous = { route: state.route, query: state.query, scope: state.scope };
  state.route = { screen: "list", family: "manufacturer" };
  state.query = "";
  state.scope = "all";
  const listed = visibleRows(state);
  state.route = previous.route;
  state.query = previous.query;
  state.scope = previous.scope;
  const entries = listed.status === 200 ? listed.list.entries.filter((entry) => entry.code === "EXAMPLE-5-A") : [];
  return `
    <p>DEC-170 allows the same code on a global row and a private row. They keep distinct ids. Code alone is not the selected record.</p>
    ${listed.status !== 200 ? outcomeBlock(listed) : `<div class="results-card"><table><caption>TEST-ONLY manufacturer rows that share EXAMPLE-5-A</caption><thead><tr><th scope="col">Identity</th><th scope="col">Scope</th><th scope="col">Label</th></tr></thead><tbody>
      ${entries.map((entry) => `<tr><td><strong class="test-flag">TEST-ONLY</strong> ${esc(entry.code)}<br>${esc(entry.entryId)}</td><td>${esc(entry.entryClass)}</td><td>${esc(entry.displayName)}</td></tr>`).join("")}
    </tbody></table></div>`}
    <p>A further private-code collision writes nothing. Cross-partner code policy is not built.</p>
    <p><a href="#/family/manufacturer/list">Open the manufacturer list</a></p>`;
}

function emptyScreen(state) {
  const previous = state.route;
  state.route = { ...state.route, screen: "list", family: "transaction_type" };
  const view = listScreen(state, familyByWire("transaction_type"));
  state.route = previous;
  return view;
}

function deniedScreen(state) {
  const previous = state.role;
  const priorRoute = state.route;
  state.role = "cashier";
  state.route = { screen: "list", family: "transaction_type" };
  const view = listScreen(state, familyByWire("transaction_type"));
  state.role = previous;
  state.route = priorRoute;
  return `<p>This preview uses the Cashier principal for the list request. A missing management grant is not a catalog-wide read policy.</p>${view}`;
}

function graphScreen(state) {
  const graph = graphReading(state).graph;
  return `
    <p class="amber">The graph on this screen is UNCONFIGURED unless you ask for a fixture description. Unknown impact is not zero linked records. A fixture does not authorize Off, cascade, or restore.</p>
    <dl class="facts">
      <div><dt>State</dt><dd>${esc(graph.state)}</dd></div>
      <div><dt>Impact</dt><dd>${esc(graph.impact)}</dd></div>
      <div><dt>Affected count</dt><dd>${graph.affectedCount == null ? "Unavailable. Not zero." : esc(graph.affectedCount)}</dd></div>
      <div><dt>Authorizes writes</dt><dd>${graph.authorizesWrites ? "Yes" : "No"}</dd></div>
      <div><dt>Runtime configuration</dt><dd>${graph.runtimeConfiguration ? "Yes" : "No"}</dd></div>
    </dl>
    <label class="checkline" for="fixture-toggle"><input id="fixture-toggle" type="checkbox" data-action="toggle-fixture"${state.fixtureGraph ? " checked" : ""}> Describe a non-authorizing fixture graph</label>
    ${state.outcome ? outcomeBlock(state.outcome) : ""}`;
}

function policyNote(family) {
  const add = !family ? "Private Add exists only for the six UX-candidate catalogs, and only fee create has a closed names-only DTO." : family.privateAdd ? "Private Add is a UX candidate for the owning Partner Admin. The create DTO is closed only for names-only fees." : "No Partner Admin private Add for this catalog.";
  return `<aside class="amber" role="note"><p>All 20 catalogs can be switched under DEC-162. Partner Admin can reverse a Super Admin single-partner Off (DEC-166), not a global deactivation (DEC-174).</p><p>Visible rows are TEST-ONLY examples (DEC-176), not production seeds.</p><p>${esc(add)}</p></aside>`;
}

function ownerNote(family) {
  if (family.wire === "card_network") {
    return `<aside class="amber note-gap" role="note"><p>Owner-selected targets are not loaded: Visa, Mastercard, Discover, and American Express.</p><p>No verified integration, funding, or response support is claimed. PQ-269 and PQ-270 are not built.</p><p>Visible rows are TEST-ONLY examples. There is no production seed baseline.</p></aside>`;
  }
  if (family.wire === "currency") {
    return `<aside class="amber note-gap" role="note"><p>Owner-selected targets are not loaded: USD and CAD (DEC-180).</p><p>Off would affect future selection only. Past transactions and history are not changed here, and Off is unavailable while the graph is unknown.</p><p>Visible rows are TEST-ONLY examples. There is no production seed baseline.</p></aside>`;
  }
  if (family.wire === "id_document_type") {
    return `<aside class="amber note-gap" role="note"><p>Owner-selected labels are not loaded: US driver's license, US State ID, any-country passport, US military ID, and Canadian driver's license.</p><p>Those jurisdictions do not establish a capture method. Create stays unavailable.</p><p>Visible rows are TEST-ONLY examples.</p></aside>`;
  }
  return "";
}

function familyBlurb(family) {
  return `<p class="lede">${esc(family.group)}. Wire name ${esc(family.wire)}. Create disposition ${esc(family.disposition)}.</p>`;
}

function familyNotes(family) {
  if (family.wire === "currency" || family.wire === "transaction_status") {
    return `<p class="fine">DEC-179: a future Off would affect future selection only. That operation is unavailable while the graph is UNCONFIGURED. Past rows are not rewritten here.</p>`;
  }
  if (family.wire === "card_network") return `<p class="fine">A network name is not accepted-card eligibility.</p>`;
  if (family.wire === "fee_type") return `<p class="fine">Fee types are names only. No amount, rate, formula, or economic role is shown.</p>`;
  if (family.claim) return `<p class="fine">A claim is not a certification or a payment enablement.</p>`;
  return "";
}

export function outcomeBlock(outcome) {
  if (!outcome) return "";
  const wrote = outcome.wrote === true;
  const countLine = outcome.preview?.affectedCount == null && outcome.graph?.affectedCount == null ? "" : "";
  return `
    <section class="${outcome.status >= 400 ? "outcome bad" : "outcome ok"}" role="${outcome.status >= 400 ? "alert" : "status"}" aria-label="Request result">
      <h2>Request result</h2>
      <dl class="facts">
        <div><dt>Status</dt><dd>${esc(outcome.status)} ${esc(outcome.code)}</dd></div>
        <div><dt>Wrote</dt><dd>${wrote ? "Synthetic session only" : "No"}</dd></div>
        <div><dt>Gate</dt><dd>${esc(outcome.gate || "None")}</dd></div>
        <div><dt>Detail</dt><dd>${esc(outcome.detail || "None")}</dd></div>
        <div><dt>Partner check</dt><dd>${esc(outcome.partnerTargetCheck || "None")}</dd></div>
        <div><dt>Location membership</dt><dd>${esc(outcome.locationMembership || "None")}</dd></div>
        <div><dt>Integrated</dt><dd>No</dd></div>
      </dl>
      ${outcome.intent ? `<p>Synthetic audit intent ${esc(outcome.intent.fields.proposed_event_name)}, emitted ${esc(outcome.intent.fields.emitted)}, status ${esc(outcome.intent.fields.intent_status)}. DURABLY_ACCEPTED is not claimed.</p>` : ""}
      ${outcome.divergence ? `<p>${esc(outcome.divergence)}</p>` : ""}
      ${countLine}
      <p class="fine">${esc(outcome.proofBoundary || PROOF_BOUNDARY)}</p>
    </section>`;
}
