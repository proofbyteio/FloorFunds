// Nav comes from GET /api/nav. A screen that is not in that grant is not linked,
// and opening it still calls the API so the server can refuse it.
// PQ-284 reversible default: the partner control is a <select> filled from
// GET /api/screens/location-partners. There is no text field for a partner id.
// Swap this default by replacing fillPartners() and the select below together
// with enforceExistingPartner in server.mjs.

const main = document.querySelector("#main");
const navEl = document.querySelector("#nav");

const pages = {
  home: { label: "Home", need: null },
  locations: { label: "Locations", need: "create_location" },
  create: { label: "Create location", need: "create_location" },
  config: { label: "Sign-in config", need: "config" },
  catalog: { label: "Catalog", need: "create_location" },
  clients: { label: "Clients", need: "create_client" },
};

let grant = [];

async function api(path, opts) {
  const res = await fetch(path, { credentials: "same-origin", ...opts });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { raw: text }; }
  return { status: res.status, body };
}

function route() {
  const name = (location.hash || "#/home").slice(2) || "home";
  return pages[name] ? name : "denied";
}

function renderNav(current) {
  navEl.replaceChildren();
  if (grant.length === 0) return;
  for (const [id, page] of Object.entries(pages)) {
    if (page.need && !grant.includes(page.need)) continue;
    const a = document.createElement("a");
    a.href = "#/" + id;
    a.textContent = page.label;
    if (id === current) a.setAttribute("aria-current", "page");
    navEl.append(a);
  }
}

function h(tag, text) {
  const el = document.createElement(tag);
  if (text !== undefined) el.textContent = text;
  return el;
}

function banner(kind, title, text) {
  const b = h("div");
  b.className = "banner banner--" + kind;
  b.append(h("span", title));
  if (text) b.append(h("p", text));
  return b;
}

function denied() {
  main.replaceChildren(
    h("h1", "This screen is not available"),
    banner("error", "Denied", "The server did not grant this screen. The menu only lists screens the grant includes."),
  );
}

function noAssignment() {
  main.replaceChildren(
    h("h1", "No assignment"),
    banner("warning", "No access", "This sign-in has no active assignment. Nothing on this screen is granted by the token."),
  );
}

async function showHome() {
  const list = await api("/api/orgs/locations");
  if (list.status === 403) return noAssignment();
  const rows = list.body?.locations ?? [];
  main.replaceChildren(h("h1", "Home"));
  if (rows.length === 0) {
    main.append(banner("info", "No locations yet", "Create a location when you are ready. The partner comes from the server list."));
    if (grant.includes("create_location")) {
      const a = h("a", "Create a location");
      a.href = "#/create";
      a.className = "button button--primary";
      main.append(a);
    }
    return;
  }
  main.append(h("p", rows.length + (rows.length === 1 ? " location." : " locations.")));
  const a = h("a", "View locations");
  a.href = "#/locations";
  a.className = "button button--secondary";
  main.append(a);
}

function table(rows) {
  const t = document.createElement("table");
  t.className = "shell__table";
  const head = document.createElement("tr");
  for (const name of ["Name", "Slug", "Partner"]) head.append(h("th", name));
  t.append(head);
  for (const row of rows) {
    const tr = document.createElement("tr");
    tr.append(h("td", row.name), h("td", row.slug), h("td", row.partner_id));
    t.append(tr);
  }
  return t;
}

async function showLocations() {
  const list = await api("/api/orgs/locations");
  if (list.status === 403) return denied();
  const rows = list.body?.locations ?? [];
  if (rows.length === 0) {
    main.replaceChildren(h("h1", "Locations"), banner("info", "No locations", "There are no locations for this grant yet."));
    if (grant.includes("create_location")) {
      const a = h("a", "Create a location");
      a.href = "#/create";
      a.className = "button button--primary";
      main.append(a);
    }
    return;
  }
  main.replaceChildren(h("h1", "Locations"), table(rows));
}

async function fillPartners(select) {
  const res = await api("/api/screens/location-partners");
  select.replaceChildren();
  if (res.status !== 200) return false;
  for (const id of res.body.partners ?? []) {
    const o = document.createElement("option");
    o.value = id;
    o.textContent = id;
    select.append(o);
  }
  return select.options.length > 0;
}

async function showCreate(state) {
  const probe = await api("/api/screens/location-partners");
  if (probe.status === 403) return denied();
  main.replaceChildren(h("h1", "Create location"));
  if (state === "timeout") main.append(banner("warning", "Timed out", "The request did not finish. Nothing was confirmed."));
  if (state === "error" && probe.body) main.append(banner("error", "Could not create the location", JSON.stringify(probe.body)));
  const form = document.createElement("form");
  form.className = "card";
  const name = document.createElement("input");
  name.className = "field__input";
  name.name = "name";
  name.required = true;
  name.maxLength = 200;
  const partner = document.createElement("select");
  partner.className = "field__input";
  partner.name = "partner_id";
  partner.required = true;
  partner.setAttribute("aria-describedby", "partner-hint");
  const display = document.createElement("input");
  display.className = "field__input";
  display.name = "display_name";
  display.required = true;
  display.value = "Staff sign-in";
  const reason = document.createElement("input");
  reason.className = "field__input";
  reason.name = "reason";
  reason.required = true;
  reason.minLength = 10;
  reason.value = "Opening this location for staff.";
  for (const [label, control] of [["Location name", name], ["Partner", partner], ["Sign-in name", display], ["Reason", reason]]) {
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), control);
    field.querySelector("span").className = "field__label";
    form.append(field);
  }
  const submit = h("button", "Create location");
  submit.className = "button button--primary";
  submit.type = "submit";
  form.append(submit);
  const hint = h("p", "Choose an existing partner from the server list. A partner id cannot be typed. PQ-284 reversible default.");
  hint.id = "partner-hint";
  hint.className = "field__hint";
  form.insertBefore(hint, submit);
  const ready = await fillPartners(partner);
  if (!ready) {
    partner.disabled = true;
    submit.disabled = true;
    form.prepend(banner("info", "No existing partner", "The server returned no partner for this grant. A partner id cannot be typed."));
  }
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    submit.disabled = true;
    const body = {
      partner_id: partner.value,
      name: name.value.trim(),
      login_config: {
        provider_kind: "database",
        display_name: display.value.trim(),
        entra_allowed_tenant_ids: [],
        allow_db_fallback: false,
        mfa_required: true,
        reason: reason.value.trim(),
      },
    };
    let res;
    try {
      res = await api("/api/orgs/locations", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(12000),
      });
    } catch {
      location.hash = "#/create";
      return showCreate("timeout");
    }
    if (res.status === 408) return showCreate("timeout");
    if (res.status >= 400) {
      main.querySelector(".banner--error")?.remove();
      form.prepend(banner("error", "Could not create the location", res.body?.error || "The server refused the request."));
      submit.disabled = false;
      return;
    }
    location.hash = "#/locations";
  });
  main.append(form);
}

async function showConfig() {
  const list = await api("/api/orgs/locations");
  if (list.status === 403) return denied();
  main.replaceChildren(h("h1", "Sign-in configuration"));
  const rows = list.body?.locations ?? [];
  if (rows.length === 0) {
    main.append(banner("info", "No locations", "Create a location before configuring sign-in."));
    return;
  }
  const select = document.createElement("select");
  select.className = "field__input";
  for (const row of rows) {
    const o = document.createElement("option");
    o.value = row.location_id;
    o.textContent = row.name;
    select.append(o);
  }
  const out = h("div");
  async function load() {
    const res = await api("/api/orgs/locations/" + select.value + "/login-config");
    out.replaceChildren();
    if (res.status !== 200) {
      out.append(banner("error", "Not available", "The server did not return this configuration."));
      return;
    }
    const dl = document.createElement("dl");
    dl.className = "card";
    for (const key of ["name", "slug", "provider_kind", "display_name", "mfa_required"]) {
      dl.append(h("dt", key), h("dd", String(res.body[key] ?? "")));
    }
    out.append(dl);
  }
  select.addEventListener("change", load);
  main.append(select, out);
  await load();
}

async function showCatalog() {
  const res = await api("/api/screens/catalog");
  if (res.status !== 200) return denied();
  main.replaceChildren(
    h("h1", "Catalog"),
    banner("info", "Read only", "Location switch and claim reactivation stay held. This screen does not write."),
  );
  const ul = document.createElement("ul");
  for (const name of ["Fee type", "Manufacturer", "Model", "Hardware claim"]) ul.append(h("li", name));
  main.append(ul);
}

async function showClients() {
  const res = await api("/api/screens/create-client");
  if (res.status === 403) return denied();
  main.replaceChildren(
    h("h1", "Clients"),
    banner("info", "Placeholder", "Create Client is held. This screen does not write."),
  );
}

async function draw() {
  const me = await api("/api/nav");
  if (me.status === 401) { location.assign("/signin"); return; }
  grant = me.status === 200 ? (me.body.screens ?? []) : [];
  const name = route();
  renderNav(name === "denied" ? "" : name);
  if (grant.length === 0 && name === "home") return noAssignment();
  if (name === "denied") return denied();
  if (name === "home") return showHome();
  if (name === "locations") return showLocations();
  if (name === "create") return showCreate();
  if (name === "config") return showConfig();
  if (name === "catalog") return showCatalog();
  if (name === "clients") return showClients();
}

window.addEventListener("hashchange", () => { draw(); });
draw();
