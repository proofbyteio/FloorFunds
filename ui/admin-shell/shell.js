// Nav comes from GET /api/nav. A screen that is not in that grant is not linked,
// and opening it still calls the API so the server can refuse it.
// PQ-284 reversible default: the partner control is a <select> filled from
// GET /api/screens/location-partners. There is no text field for a partner id.
// Swap this default by replacing fillPartners() and the select below together
// with enforceExistingPartner in server.mjs.
import { applyProductName, productNameFromConfig } from "./branding.mjs";
import {
  DEFAULT_COUNTRY,
  FIELD_LIMITS,
  loadCountries,
  loadPostalRule,
  loadRegions,
  loadTimeZones,
  normalizePostal,
  postalMaxLength,
} from "./location-address-options.mjs";
import {
  addressFieldErrors,
  entityNameError,
  fieldsBannerDetail,
} from "./org-form-rules.mjs";

const main = document.querySelector("#main");
const navEl = document.querySelector("#nav");
let leaving = false;

const pages = {
  home: { label: "Home", need: null },
  locations: { label: "Locations", need: "create_location" },
  // F-21. create_location and config come from GET /api/nav screens
  // (LoginConfigApi.NavAsync for Super Admin and Partner Admin).
  create: { label: "Create location", need: "create_location" },
  config: { label: "Sign-in config", need: "config" },
  catalog: { label: "Catalog", need: "catalog" },
  clients: { label: "Create Client Admin", need: "create_client" },
  // STEP 2 Partner form; STEP 5 locks nav labels to Create * Admin.
  partners: { label: "Create Partner Admin", need: "create_partner" },
  "partner-edit": { label: "Edit partner", need: "create_partner" },
};

let grant = [];

// Location name length matches the existing LoginConfigValidator limit: more
// than 200 Unicode scalars is name_invalid. Count by code points ([...value]),
// not UTF-16 .length. The 200 limit itself is a proposal pending PQ-313.
export function locationNameClientError(value) {
  const name = String(value ?? "").trim();
  if ([...name].length === 0) return "required";
  if ([...name].length > 200) return "name_invalid";
  return "";
}

function goSignIn(reason) {
  leaving = true;
  // Fixed enum only. session_expired uses reason=expired; other 401s have no reason.
  location.assign(reason === "expired" ? "/signin?reason=expired" : "/signin");
}

export async function api(path, opts) {
  const res = await fetch(path, { credentials: "same-origin", ...opts });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = { raw: text }; }
  // Expired, revoked, and logged-out sessions are one 401. Leave for /signin
  // before the assignment or screen banners can draw. The expiry sentence is
  // rendered on the admin sign-in page after the handoff (F-TL-B / DEC-220).
  if (res.status === 401) {
    goSignIn(body && body.error === "session_expired" ? "expired" : "");
    return { status: 401, body };
  }
  return { status: res.status, body };
}

const uuidRe = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
const orgsNewRe = new RegExp("^/orgs/(" + uuidRe + ")/locations/new/?$");
const orgsSignInRe = new RegExp("^/orgs/(" + uuidRe + ")/locations/(" + uuidRe + ")/sign-in/?$");

function orgsRoute() {
  const path = location.pathname || "/";
  let m = path.match(orgsNewRe);
  if (m) return { kind: "create", partnerId: m[1] };
  m = path.match(orgsSignInRe);
  if (m) return { kind: "config", partnerId: m[1], locationId: m[2] };
  return null;
}

function route() {
  if (orgsRoute()) return "orgs";
  const raw = (location.hash || "#/home").slice(2) || "home";
  const name = raw.split("?")[0] || "home";
  return pages[name] ? name : "denied";
}

function renderNav(current) {
  navEl.replaceChildren();
  if (grant.length === 0) return;
  for (const [id, page] of Object.entries(pages)) {
    // F-05. Create and Sign-in config stay in pages for F-21 routing, not as nav labels.
    // partner-edit is reached after create, not as a nav item.
    if (id === "create" || id === "config" || id === "partner-edit") continue;
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

async function createLocationControl() {
  if (!grant.includes("create_location")) return null;
  const partners = await api("/api/screens/location-partners");
  const id = partners.body?.partners?.[0];
  if (!id) return null;
  const a = primaryControl("a", "Create location");
  // UX F-06: real /orgs path. server.mjs serves it as the shell.
  a.href = "/orgs/" + id + "/locations/new";
  return a;
}

async function showLocations() {
  const list = await api("/api/orgs/locations");
  if (list.status === 403) return denied();
  const rows = list.body?.locations ?? [];
  const create = await createLocationControl();
  if (rows.length === 0) {
    main.replaceChildren(h("h1", "Locations"), banner("info", "No locations", "There are no locations for this grant yet."));
    if (create) main.append(create);
    return;
  }
  main.replaceChildren(h("h1", "Locations"), table(rows));
  if (create) main.append(create);
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

function fillSelect(select, options, { blankLabel = "Select…", selected = "" } = {}) {
  select.replaceChildren();
  const blank = document.createElement("option");
  blank.value = "";
  blank.textContent = blankLabel;
  select.append(blank);
  for (const opt of options) {
    const o = document.createElement("option");
    o.value = opt.code ?? opt.value;
    o.textContent = opt.label;
    if (selected && o.value === selected) o.selected = true;
    select.append(o);
  }
  if (selected) select.value = selected;
}

/** Shared US/CA address block for Create location and Create client. */
export async function appendNorthAmericaAddressFields(form, address) {
  function textField(label, fieldName, required, opts = {}) {
    const input = document.createElement("input");
    input.className = "field__input";
    input.name = fieldName;
    if (required) input.required = true;
    if (opts.type) input.type = opts.type;
    if (opts.maxLength) input.maxLength = opts.maxLength;
    if (opts.autocomplete) input.autocomplete = opts.autocomplete;
    if (opts.inputMode) input.inputMode = opts.inputMode;
    if (opts.placeholder) input.placeholder = opts.placeholder;
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), input);
    field.querySelector("span").className = "field__label";
    address[fieldName] = input;
    return field;
  }
  function selectField(label, fieldName, required) {
    const select = document.createElement("select");
    select.className = "field__input";
    select.name = fieldName;
    if (required) select.required = true;
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), select);
    field.querySelector("span").className = "field__label";
    address[fieldName] = select;
    return field;
  }
  const countryField = selectField("Country *", "country", true);
  const regionField = selectField("State / province *", "region", true);
  const zoneField = selectField("Time zone *", "time_zone", true);
  for (const field of [
    textField("Address line 1 *", "address_line_1", true),
    textField("Address line 2 (optional)", "address_line_2", false),
    textField("City *", "city", true),
    countryField,
    regionField,
    textField("ZIP / postal code *", "postal_code", true, {
      autocomplete: "postal-code",
      maxLength: postalMaxLength(DEFAULT_COUNTRY),
      placeholder: "12345 or 12345-6789",
    }),
  ]) form.append(field);

  // Phone: country-code picker (US/CA, both +1) + national input. Server is authoritative.
  const phoneCountry = document.createElement("select");
  phoneCountry.className = "field__input";
  phoneCountry.name = "phone_country";
  phoneCountry.required = true;
  fillSelect(phoneCountry, [
    { code: "US", label: "+1 United States (US)" },
    { code: "CA", label: "+1 Canada (CA)" },
  ], { blankLabel: "", selected: DEFAULT_COUNTRY });
  const phoneCountryField = h("label");
  phoneCountryField.className = "field";
  phoneCountryField.append(h("span", "Phone country *"), phoneCountry);
  phoneCountryField.querySelector("span").className = "field__label";
  address.phone_country = phoneCountry;
  form.append(phoneCountryField);
  form.append(textField("Primary contact phone *", "phone", true, {
    type: "tel",
    autocomplete: "tel",
    inputMode: "tel",
    maxLength: FIELD_LIMITS.phone,
    placeholder: "702 555 0100",
  }));
  form.append(textField("Primary contact email (optional)", "email", false, {
    type: "email",
    autocomplete: "email",
    maxLength: FIELD_LIMITS.email,
    placeholder: "name@example.com",
  }));
  form.append(zoneField);

  // DEC-258/260: dropdowns from GET /api/lookups/* (not hard-coded lists).
  const countries = await loadCountries(api);
  fillSelect(address.country, countries, { blankLabel: "Select a country…", selected: DEFAULT_COUNTRY });
  address._postalRule = null;
  async function applyCountry() {
    const code = address.country.value;
    address.region.disabled = !code;
    address.time_zone.disabled = !code;
    address.postal_code.disabled = !code;
    if (!code) {
      fillSelect(address.region, [], { blankLabel: "Select a state or province…" });
      fillSelect(address.time_zone, [], { blankLabel: "Select a time zone…" });
      address.region.value = "";
      address.time_zone.value = "";
      address.postal_code.value = "";
      address._postalRule = null;
      return;
    }
    const [regions, zones, rule] = await Promise.all([
      loadRegions(api, code),
      loadTimeZones(api, code),
      loadPostalRule(api, code),
    ]);
    address._postalRule = rule;
    fillSelect(address.region, regions, { blankLabel: "Select a state or province…" });
    fillSelect(address.time_zone, zones, { blankLabel: "Select a time zone…" });
    address.region.required = true;
    address.time_zone.required = true;
    address.postal_code.required = true;
    address.postal_code.maxLength = postalMaxLength(code, rule);
    address.postal_code.placeholder = code === "CA" ? "A1A 1A1" : "12345 or 12345-6789";
    address.postal_code.value = normalizePostal(code, address.postal_code.value, rule);
    if (address.postal_code.value.length > address.postal_code.maxLength) {
      address.postal_code.value = address.postal_code.value.slice(0, address.postal_code.maxLength);
    }
    // Phone region follows address country when US/CA (picker default +1).
    if (code === "US" || code === "CA") address.phone_country.value = code;
  }
  address.country.addEventListener("change", () => { void applyCountry(); });
  address.postal_code.addEventListener("blur", () => {
    const code = address.country.value;
    if (!code) return;
    address.postal_code.value = normalizePostal(code, address.postal_code.value, address._postalRule);
  });
  await applyCountry();
}

export function addressPayload(address) {
  const postal = address.postal_code.disabled ? "" : address.postal_code.value.trim();
  return {
    address_line_1: address.address_line_1.value.trim(),
    address_line_2: address.address_line_2.value.trim(),
    city: address.city.value.trim(),
    country: address.country.value,
    region: address.region.disabled ? "" : address.region.value.trim(),
    postal_code: postal,
    phone: address.phone.value.trim(),
    email: address.email.value.trim(),
    time_zone: address.time_zone.value.trim(),
  };
}

export function addressLengthError(address) {
  // Prefer exact owner sentences via addressFieldErrors; keep a length-only fallback.
  const fields = addressFieldErrors(address);
  if (fields.length) return { title: "Check the address", detail: fieldsBannerDetail(fields), fields };
  return null;
}

function showValidationBanner(form, title, fieldsOrDetail) {
  main.querySelector(".banner--error")?.remove();
  form.querySelector(".banner--error")?.remove();
  const detail = typeof fieldsOrDetail === "string"
    ? fieldsOrDetail
    : fieldsBannerDetail(fieldsOrDetail);
  form.prepend(banner("error", title, detail || " "));
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
  // Do not set HTML maxlength: it counts UTF-16 units. JS + server use Unicode scalars (F-24 / PQ-313).
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
  await appendNorthAmericaAddressFields(form, address);
  for (const [label, control] of [["Partner", partner]]) {
    const field = h("label");
    field.className = "field";
    field.append(h("span", label), control);
    field.querySelector("span").className = "field__label";
    form.append(field);
  }
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
    // Pending PQ-313 → FFP name 2–100 with exact sentences (STEP 4). Client pre-check here.
    const nameMsg = entityNameError("location", name.value) || locationNameClientError(name.value);
    if (nameMsg && nameMsg !== "required" && nameMsg !== "name_invalid") {
      showValidationBanner(form, "Location name", nameMsg);
      return;
    }
    if (nameMsg === "required" || nameMsg === "name_invalid") {
      showValidationBanner(form, "Location name", entityNameError("location", name.value) || "Enter a location name.");
      return;
    }
    const lengthError = addressLengthError(address);
    if (lengthError) {
      showValidationBanner(form, lengthError.title, lengthError.detail);
      return;
    }
    submit.disabled = true;
    const body = {
      partner_id: partner.value,
      name: name.value.trim(),
      address: addressPayload(address),
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
      const detail = res.status === 422 && Array.isArray(res.body?.fields)
        ? fieldsBannerDetail(res.body.fields)
        : (res.body?.error || "The server refused the request.");
      showValidationBanner(form, "Could not create the location", detail);
      submit.disabled = false;
      return;
    }
    location.hash = "#/locations";
  });
  main.append(form);
}

async function showPartners(editId) {
  const probe = await api("/api/nav");
  if (probe.status === 401) return;
  if (!(probe.body?.screens ?? []).includes("create_partner")) return denied();
  const editing = Boolean(editId);
  main.replaceChildren(h("h1", editing ? "Edit partner" : "Create Partner Admin"));
  const form = document.createElement("form");
  form.className = "card";
  const name = document.createElement("input");
  name.className = "field__input";
  name.name = "name";
  name.required = true;
  const nameField = h("label");
  nameField.className = "field";
  nameField.append(h("span", "Partner name"), name);
  nameField.querySelector("span").className = "field__label";
  form.append(nameField);
  const address = {};
  await appendNorthAmericaAddressFields(form, address);
  const submit = primaryControl("button", editing ? "Save partner" : "Create partner");
  submit.type = "submit";
  form.append(submit);
  if (editing) {
    const got = await api("/api/orgs/partners/" + editId);
    if (got.status !== 200) {
      main.append(banner("error", "Not found", "That partner is not available."));
      return;
    }
    name.value = got.body.name ?? "";
    const a = got.body.address ?? {};
    address.address_line_1.value = a.address_line_1 ?? "";
    address.address_line_2.value = a.address_line_2 ?? "";
    address.city.value = a.city ?? "";
    if (a.country) {
      address.country.value = a.country;
      await address.country.dispatchEvent(new Event("change"));
      // allow applyCountry to settle
      await new Promise((r) => setTimeout(r, 0));
      address.region.value = a.region ?? "";
      address.postal_code.value = a.postal_code ?? "";
      address.time_zone.value = a.time_zone ?? "";
      address.phone.value = a.phone ?? "";
      address.email.value = a.email ?? "";
    }
  }
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const nameMsg = entityNameError("partner", name.value);
    if (nameMsg) { showValidationBanner(form, "Partner name", nameMsg); return; }
    const addrErr = addressLengthError(address);
    if (addrErr) { showValidationBanner(form, addrErr.title, addrErr.detail); return; }
    submit.disabled = true;
    const body = { name: name.value.trim(), address: addressPayload(address) };
    const path = editing ? "/api/orgs/partners/" + editId : "/api/orgs/partners";
    const method = editing ? "PUT" : "POST";
    let res;
    try {
      res = await api(path, {
        method,
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(12000),
      });
    } catch {
      showValidationBanner(form, "Timed out", "The request did not finish. Nothing was confirmed.");
      submit.disabled = false;
      return;
    }
    if (res.status >= 400) {
      const detail = res.status === 422 && Array.isArray(res.body?.fields)
        ? fieldsBannerDetail(res.body.fields)
        : (res.body?.error || "The server refused the request.");
      showValidationBanner(form, "Could not save the partner", detail);
      submit.disabled = false;
      return;
    }
    const id = res.body?.partner_id || editId;
    location.hash = "#/partner-edit?id=" + encodeURIComponent(id);
    form.prepend(banner("info", editing ? "Saved" : "Created", "Partner " + (res.body?.name || "") + " was accepted."));
    submit.disabled = false;
  });
  main.append(form);
}

async function showConfig(selectedLocationId) {
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
    if (selectedLocationId && row.location_id === selectedLocationId) o.selected = true;
    select.append(o);
  }
  if (selectedLocationId && ![...select.options].some((o) => o.value === selectedLocationId)) {
    select.value = rows[0].location_id;
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
    // F-23. admin-shell serves ui/catalog-presentation under /catalog (see server.mjs).
    a.href = "/catalog/#/family/" + wire + "/list";
    li.append(a);
    ul.append(li);
  }
  main.append(ul);
}

export async function showClients() {
  const res = await api("/api/screens/create-client");
  if (res.status === 403) return denied();
  main.replaceChildren(h("h1", "Create Client Admin"));
  const form = document.createElement("form");
  form.className = "card";
  const partner = document.createElement("select");
  partner.className = "field__input";
  partner.name = "partner_id";
  partner.required = true;
  const partnerField = h("label");
  partnerField.className = "field";
  partnerField.append(h("span", "Partner"), partner);
  partnerField.querySelector("span").className = "field__label";
  form.append(partnerField);
  const name = document.createElement("input");
  name.className = "field__input";
  name.id = "client-name";
  name.name = "name";
  name.required = true;
  const nameField = h("label");
  nameField.className = "field";
  nameField.htmlFor = "client-name";
  const nameLabel = h("span", "Client name");
  nameLabel.className = "field__label";
  nameField.append(nameLabel, name);
  form.append(nameField);
  const address = {};
  await appendNorthAmericaAddressFields(form, address);
  const submit = primaryControl("button", "Create client");
  submit.type = "submit";
  form.append(submit);
  const partners = await api("/api/orgs/partners");
  fillSelect(partner, (partners.body?.partners ?? []).map((p) => ({
    code: p.partner_id,
    label: p.name + " (" + p.partner_id + ")",
  })), { blankLabel: "Select a partner…" });
  if (!(partners.body?.partners ?? []).length) {
    partner.disabled = true;
    submit.disabled = true;
    form.prepend(banner("info", "No partners", "Create a partner before creating a client."));
  }
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    if (!partner.value) { showValidationBanner(form, "Partner", "Select a partner."); return; }
    const nameMsg = entityNameError("client", name.value);
    if (nameMsg) { showValidationBanner(form, "Client name", nameMsg); return; }
    const lengthError = addressLengthError(address);
    if (lengthError) { showValidationBanner(form, lengthError.title, lengthError.detail); return; }
    submit.disabled = true;
    const body = {
      partner_id: partner.value,
      name: name.value.trim(),
      address: addressPayload(address),
    };
    let write;
    try {
      write = await api("/api/screens/create-client", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(12000),
      });
    } catch {
      form.prepend(banner("warning", "Timed out", "The request did not finish. Nothing was confirmed."));
      submit.disabled = false;
      return;
    }
    if (write.status >= 400) {
      const detail = write.status === 422 && Array.isArray(write.body?.fields)
        ? fieldsBannerDetail(write.body.fields)
        : (write.body?.error || "The server refused the request.");
      showValidationBanner(form, "Could not create the client", detail);
      submit.disabled = false;
      return;
    }
    form.prepend(banner("info", "Created", "The client was accepted."));
    submit.disabled = false;
  });
  main.append(form);
}

async function draw() {
  const me = await api("/api/nav");
  if (me.status === 401) return;
  const productName = productNameFromConfig(me.body);
  applyProductName(document.querySelector(".admin-lockup__name"), productName);
  if (productName) document.title = productName;
  const signedIn = document.querySelector("#signed-in");
  if (signedIn) {
    const who = typeof me.body?.signed_in_as === "string" ? me.body.signed_in_as : "";
    const role = typeof me.body?.role === "string" ? me.body.role : "";
    signedIn.textContent = who && role ? "Signed in as " + who + " · Role: " + role : "";
  }
  grant = me.status === 200 ? (me.body.screens ?? []) : [];
  const deep = orgsRoute();
  const name = route();
  renderNav(deep ? "locations" : name === "denied" ? "" : name);
  if (grant.length === 0) return noAssignment();
  if (deep?.kind === "create") return showCreate();
  if (deep?.kind === "config") return showConfig(deep.locationId);
  if (name === "denied") return denied();
  if (name === "home") return showHome();
  if (name === "locations") return showLocations();
  if (name === "create") return showCreate();
  if (name === "config") return showConfig();
  if (name === "catalog") return showCatalog();
  if (name === "clients") return showClients();
  if (name === "partners") return showPartners();
  if (name === "partner-edit") {
    const id = new URLSearchParams((location.hash.split("?")[1] || "")).get("id");
    return showPartners(id || "");
  }
}

export let started = Promise.resolve();
export function redraw() {
  started = draw();
  return started;
}
if (main) {
  window.addEventListener("hashchange", () => { started = draw(); });
  started = draw();
}
