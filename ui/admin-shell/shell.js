// Nav comes from GET /api/nav. A screen that is not in that grant is not linked,
// and opening it still calls the API so the server can refuse it.
// PQ-284 reversible default: the partner control is a <select> filled from
// GET /api/screens/location-partners. There is no text field for a partner id.
// Swap this default by replacing fillPartners() and the select below together
// with enforceExistingPartner in server.mjs.
import { applyProductName, productNameFromConfig } from "./branding.mjs";

const main = document.querySelector("#main");
const navEl = document.querySelector("#nav");
let leaving = false;

const pages = {
  home: { label: "Home", need: null },
  locations: { label: "Locations", need: "create_location" },
  catalog: { label: "Catalog", need: "catalog" },
  clients: { label: "Clients", need: "create_client" },
};

let grant = [];

// DEC-237. The only client checks are required, and the limit already in
// LoginConfigValidator.ValidateCreate: a name of more than 200 Unicode scalars
// is name_invalid. No placeholder, no helper, and no extra character rule.
export function locationNameClientError(value) {
  const name = String(value ?? "").trim();
  if (name.length === 0) return "required";
  if ([...name].length > 200) return "name_invalid";
  return "";
}

function goSignIn() {
  leaving = true;
  location.assign("/signin");
}

export async function api(path, opts) {
  const res = await fetch(path, { credentials: "same-origin", ...opts });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { raw: text }; }
  // Expired, revoked, and logged-out sessions are one 401. Leave for /signin
  // before the assignment or screen banners can draw.
  if (res.status === 401) {
    const productName = productNameFromConfig(body);
    if (productName && main) main.replaceChildren(h("p", "For security, " + productName + " asks for your sign-in again."));
    goSignIn();
    return { status: 401, body };
  }
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

function primaryControl(tag, text) {
  const el = h(tag);
  el.className = "button button--primary btn-primary";
  const press = h("span", text);
  press.className = "btn-primary__press";
  el.append(press);
  return el;
}

function banner(kind, title, text) {
  const b = h("div");
  b.className = "banner banner--" + kind;
  b.setAttribute("role", kind === "error" ? "alert" : "status");
  b.append(h("span", title));
  if (text) b.append(h("p", text));
  return b;
}

function denied() {
  if (leaving) return;
  const back = primaryControl("a", "Back to Home");
  back.href = "#/home";
  const alert = banner("error", "You cannot open this screen", "This screen is not in the server-resolved grant for the current assignment. The item is hidden from the menu. Reaching it directly does not show catalog rows, names, or any other record.");
  alert.setAttribute("role", "alert");
  main.replaceChildren(h("h1", "Access denied"), alert, back);
}

function noAssignment() {
  if (leaving) return;
  const signOut = h("a", "Sign out");
  signOut.href = "/standin/connect/endsession";
  signOut.className = "button button--secondary";
  main.replaceChildren(
    h("h1", "No access, contact your administrator"),
    h("p", "No server-side role or partner assignment was found. Access is denied (DEC-197). This page does not list screens, clients, locations, or catalog values. Nothing here is a grant. The menu is omitted because there is no resolved assignment."),
    signOut,
  );
}

async function showHome() {
  const list = await api("/api/orgs/locations");
  if (list.status === 403) return noAssignment();
  const rows = list.body?.locations ?? [];
  main.replaceChildren(h("h1", "Home"));
  if (rows.length === 0) {
    main.append(h("h2", "Guided setup"));
    const steps = document.createElement("ol");
    steps.append(h("li", "1  Clients"), h("li", "2  Locations"), h("li", "3  Catalog and access"));
    main.append(steps);
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
  for (const name of ["Location", "Client / partner", "Sign-in config", "Actions"]) head.append(h("th", name));
  t.append(head);
  for (const row of rows) {
    const tr = document.createElement("tr");
    const config = h("a", "/orgs/{partner}/locations/{location}/sign-in".replace("{partner}", row.partner_id).replace("{location}", row.location_id));
    config.href = "/orgs/" + row.partner_id + "/locations/" + row.location_id + "/sign-in";
    tr.append(h("td", row.name), h("td", row.partner_id), h("td"), h("td"));
    tr.children[2].append(config);
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
      const partners = await api("/api/screens/location-partners");
      const id = partners.body?.partners?.[0];
      if (id) {
        const a = primaryControl("a", "Create location");
        a.href = "/orgs/" + id + "/locations/new";
        main.append(a);
      }
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

export async function showCreate(state) {
  const probe = await api("/api/screens/location-partners");
  if (probe.status === 403) return denied();
  main.replaceChildren(h("h1", "Create location"));
  if (state === "timeout") main.append(banner("warning", "Timed out", "The request did not finish. Nothing was confirmed."));
  if (state === "error" && probe.body) main.append(banner("error", "Could not create the location", JSON.stringify(probe.body)));
  const form = document.createElement("form");
  form.className = "card";
  const name = document.createElement("input");
  name.className = "field__input";
  name.id = "location-name";
  name.name = "name";
  name.required = true;
  name.maxLength = 200;
  const nameField = h("label");
  nameField.className = "field";
  nameField.htmlFor = "location-name";
  const nameLabel = h("span", "Location name");
  nameLabel.className = "field__label";
  nameField.append(nameLabel, name);
  form.append(nameField);
  const partner = document.createElement("select");
  partner.className = "field__input";
  partner.name = "partner_id";
  partner.required = true;
  partner.setAttribute("aria-describedby", "partner-hint");
  const address = {};
  function textField(label, fieldName, required) {
    const input = document.createElement("input");
    input.className = "field__input";
    input.name = fieldName;
    if (required) input.required = true;
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), input);
    field.querySelector("span").className = "field__label";
    address[fieldName] = input;
    return field;
  }
  const country = document.createElement("select");
  country.className = "field__input";
  country.name = "country";
  country.required = true;
  address.country = country;
  const countryField = h("label");
  countryField.className = "field";
  countryField.append(h("span", "Country *"), country);
  countryField.querySelector("span").className = "field__label";
  for (const field of [
    textField("Address line 1 *", "address_line_1", true),
    textField("Address line 2 (optional)", "address_line_2", false),
    textField("City *", "city", true),
    countryField,
    textField("State / province / region", "region", false),
    textField("ZIP / postal code", "postal_code", false),
    textField("Primary contact phone *", "phone", true),
    textField("Primary contact email (optional)", "email", false),
    textField("Time zone *", "time_zone", true),
  ]) form.append(field);
  for (const [label, control] of [["Partner", partner]]) {
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), control);
    field.querySelector("span").className = "field__label";
    form.append(field);
  }
  const rules = await api("/address/country-rules.json");
  const byCountry = new Map();
  for (const row of rules.body?.rows ?? []) byCountry.set(row.country, row);
  const blank = document.createElement("option");
  blank.value = "";
  country.append(blank);
  for (const code of byCountry.keys()) {
    const o = document.createElement("option");
    o.value = code;
    o.textContent = code;
    country.append(o);
  }
  function applyCountry() {
    const row = byCountry.get(country.value);
    for (const [fieldName, key] of [["region", "state"], ["postal_code", "postal"]]) {
      const input = address[fieldName];
      const use = row ? row[key] : "optional";
      input.required = use === "required";
      input.disabled = use === "not_used";
      if (use === "not_used") input.value = "";
    }
  }
  country.addEventListener("change", applyCountry);
  const submit = primaryControl("button", "Create location");
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
    const nameError = locationNameClientError(name.value);
    if (nameError) {
      main.querySelector(".banner--error")?.remove();
      form.prepend(banner("error", "Location name", ""));
      submit.disabled = false;
      return;
    }
    submit.disabled = true;
    const body = {
      partner_id: partner.value,
      name: name.value.trim(),
      address: {
        address_line_1: address.address_line_1.value.trim(),
        address_line_2: address.address_line_2.value.trim(),
        city: address.city.value.trim(),
        country: address.country.value,
        region: address.region.disabled ? "" : address.region.value.trim(),
        postal_code: address.postal_code.disabled ? "" : address.postal_code.value.trim(),
        phone: address.phone.value.trim(),
        email: address.email.value.trim(),
        time_zone: address.time_zone.value.trim(),
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
  select.id = "signin-config-location";
  select.setAttribute("aria-label", "Location");
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
  const locationLabel = h("label", "Location");
  locationLabel.htmlFor = select.id;
  locationLabel.className = "field__label";
  main.append(locationLabel, select, out);
  await load();
}

const catalogNames = [
  ["01", "Catalog release", "catalog_release"],
  ["02", "Transaction type", "transaction_type"],
  ["03", "Channel", "channel"],
  ["04", "Device category", "device_category"],
  ["05", "Manufacturer", "manufacturer"],
  ["06", "Device model", "device_model"],
  ["07", "Hardware capability kind", "hardware_capability_kind"],
  ["08", "Model hardware claim", "model_hardware_claim"],
  ["09", "Card network", "card_network"],
  ["10", "Card funding class", "card_funding_class"],
  ["11", "Payment rail", "payment_rail"],
  ["12", "Check instrument kind", "check_instrument_kind"],
  ["13", "Check drawer class", "check_drawer_class"],
  ["14", "Check purpose", "check_purpose"],
  ["15", "Processor", "processor"],
  ["16", "Transaction status", "transaction_status"],
  ["17", "Reason code", "reason_code"],
  ["18", "ID document type", "id_document_type"],
  ["19", "Currency", "currency"],
  ["20", "Fee type", "fee_type"],
];

async function showCatalog() {
  const res = await api("/api/screens/catalog");
  if (res.status !== 200) return denied();
  main.replaceChildren(h("h1", "Catalog"));
  const ul = document.createElement("ul");
  for (const [n, title, wire] of catalogNames) {
    const li = document.createElement("li");
    const a = h("a", n + " " + title);
    a.href = "/catalog/#/family/" + wire + "/list";
    li.append(a);
    ul.append(li);
  }
  main.append(ul);
}

async function showClients() {
  const res = await api("/api/screens/create-client");
  if (res.status === 403) return denied();
  main.replaceChildren(
    h("h1", "Clients"),
    h("p", "Placeholder  ·  this screen is not built"),
  );
}

async function draw() {
  const me = await api("/api/nav");
  if (me.status === 401) { goSignIn(); return; }
  const productName = productNameFromConfig(me.body);
  applyProductName(document.querySelector(".admin-lockup__name"), productName);
  if (productName) document.title = productName;
  const signedIn = document.querySelector("#signed-in");
  if (signedIn) {
    const screens = me.body?.screens ?? [];
    const role = screens.includes("create_client") ? "Super Admin" : screens.includes("create_location") ? "Partner Admin" : "";
    signedIn.textContent = role ? "Role: " + role : "";
  }
  grant = me.status === 200 ? (me.body.screens ?? []) : [];
  const name = route();
  renderNav(name === "denied" ? "" : name);
  if (grant.length === 0) return noAssignment();
  if (name === "denied") return denied();
  if (name === "home") return showHome();
  if (name === "locations") return showLocations();
  if (name === "create") return showCreate();
  if (name === "config") return showConfig();
  if (name === "catalog") return showCatalog();
  if (name === "clients") return showClients();
}

export let started = Promise.resolve();
if (main) {
  window.addEventListener("hashchange", () => { started = draw(); });
  started = draw();
}
