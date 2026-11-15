// Location create/edit fields (DEC-273). Mirrors client-form.mjs; Partner/Client CSS/JS unchanged.
import {
  loadCountries,
  loadPostalRule,
  loadRegions,
  loadTimeZones,
  normalizePostal,
  postalMaxLength,
} from "./location-address-options.mjs";

function el(tag, attrs = {}, text) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "className") node.className = v;
    else if (k === "htmlFor") node.htmlFor = v;
    else if (v === true) node.setAttribute(k, "");
    else if (v !== false && v != null) node.setAttribute(k, String(v));
  }
  if (text !== undefined) node.textContent = text;
  return node;
}

function optionalLabel(text) {
  const label = el("label");
  // Prefer createTextNode when present; suites use a minimal document mock without it.
  if (typeof document.createTextNode === "function") {
    label.append(document.createTextNode(text + " "));
  } else {
    label.append(el("span", {}, text + " "));
  }
  const opt = el("span", { className: "optional" }, "(optional)");
  label.append(opt);
  return label;
}

/**
 * Location fields: Partner + Client picks, Location name, then address/contact like Client.
 * Country blank by default; phone_country for the API is the address country (US/CA);
 * the visible calling-code control is +1 only. No HTML maxlength (GAPS / Partner parity).
 * ZIP hint/error live inside `.field--zip` (Client NOTES fix).
 * Pull-address button is wired by the shell, not this module.
 * @param {HTMLFormElement} form
 * @param {Record<string, any>} address
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 * @param {{
 *   nameInput: HTMLInputElement,
 *   partnerSelect: HTMLSelectElement,
 *   clientSelect: HTMLSelectElement,
 *   partnerReadOnly?: boolean,
 *   clientReadOnly?: boolean,
 *   partnerLabel?: string,
 *   clientLabel?: string,
 *   roleHint?: string,
 *   onPartnerChange?: (partnerId: string) => void,
 * }} opts
 */
export async function appendLocationFields(form, address, apiGet, opts) {
  const {
    nameInput,
    partnerSelect,
    clientSelect,
    partnerReadOnly = false,
    clientReadOnly = false,
    partnerLabel = "",
    clientLabel = "",
    roleHint = "",
    onPartnerChange,
  } = opts;

  const secLocation = el("section", { className: "section", "aria-labelledby": "sec-location" });
  secLocation.append(el("h2", { className: "section-title", id: "sec-location" }, "Location"));
  const gridLocation = el("div", { className: "grid" });

  const partnerField = el("div", { className: "field field--partner-pick span-4", id: "field-partner" });
  const partnerLabelEl = el("label", { id: "partner-label" }, "Partner");
  partnerSelect.id = "partner-id";
  partnerSelect.name = "partner_id";
  partnerSelect.required = true;
  partnerSelect.className = "field__input";
  if (!partnerSelect.querySelector('option[value=""]')) {
    partnerSelect.prepend(el("option", { value: "" }, "Select a partner"));
  }

  if (partnerReadOnly) {
    partnerSelect.hidden = true;
    partnerLabelEl.removeAttribute("for");
    const roText = partnerLabel
      || partnerSelect.selectedOptions?.[0]?.textContent
      || partnerSelect.value
      || "";
    const ro = el("div", {
      className: "readonly-select",
      id: "partner-readonly",
      role: "combobox",
      "aria-readonly": "true",
      "aria-disabled": "true",
      "aria-labelledby": "partner-label",
    });
    ro.append(el("span", { id: "partner-readonly-text" }, roText));
    partnerField.append(partnerLabelEl, partnerSelect, ro);
  } else {
    partnerLabelEl.htmlFor = "partner-id";
    partnerField.append(partnerLabelEl, partnerSelect);
  }

  const clientField = el("div", { className: "field field--client-pick span-4", id: "field-client" });
  const clientLabelEl = el("label", { id: "client-label" }, "Client");
  clientSelect.id = "client-id";
  clientSelect.name = "client_id";
  clientSelect.required = true;
  clientSelect.className = "field__input";
  const partnerAlreadySet = Boolean(partnerSelect.value);
  const clientBlankLabel = partnerAlreadySet ? "Select a client" : "Select a partner first";
  if (!clientSelect.querySelector('option[value=""]')) {
    clientSelect.prepend(el("option", { value: "" }, clientBlankLabel));
  } else {
    const blank = clientSelect.querySelector('option[value=""]');
    if (blank) blank.textContent = clientBlankLabel;
  }
  if (!partnerAlreadySet && !clientReadOnly) {
    clientSelect.disabled = true;
  }

  if (clientReadOnly) {
    clientSelect.hidden = true;
    clientLabelEl.removeAttribute("for");
    const roText = clientLabel
      || clientSelect.selectedOptions?.[0]?.textContent
      || clientSelect.value
      || "";
    const ro = el("div", {
      className: "readonly-select",
      id: "client-readonly",
      role: "combobox",
      "aria-readonly": "true",
      "aria-disabled": "true",
      "aria-labelledby": "client-label",
    });
    ro.append(el("span", { id: "client-readonly-text" }, roText));
    clientField.append(clientLabelEl, clientSelect, ro);
  } else {
    clientLabelEl.htmlFor = "client-id";
    clientField.append(clientLabelEl, clientSelect);
  }

  gridLocation.append(partnerField, clientField);

  if (roleHint) {
    const hint = el("p", { className: "hint field-hint ro-hint", id: "role-hint" }, roleHint);
    if (partnerReadOnly) {
      const partnerRo = partnerField.querySelector("#partner-readonly");
      partnerRo?.setAttribute("aria-describedby", "role-hint");
    }
    if (clientReadOnly) {
      const clientRo = clientField.querySelector("#client-readonly");
      clientRo?.setAttribute("aria-describedby", "role-hint");
    }
    gridLocation.append(hint);
  }

  const nameField = el("div", { className: "field field--location span-6", id: "field-location-name" });
  nameField.append(el("label", { htmlFor: "location-name" }, "Location name"));
  nameInput.id = "location-name";
  nameInput.name = "name";
  nameInput.type = "text";
  nameInput.autocomplete = "organization";
  nameInput.required = true;
  nameInput.className = "field__input";
  nameField.append(nameInput);

  gridLocation.append(nameField);
  secLocation.append(gridLocation);
  form.append(secLocation);

  if (!partnerReadOnly) {
    partnerSelect.addEventListener("change", () => {
      const pid = partnerSelect.value;
      if (!clientReadOnly) {
        clientSelect.replaceChildren(el("option", { value: "" }, pid ? "Select a client" : "Select a partner first"));
        clientSelect.value = "";
        clientSelect.disabled = !pid;
      }
      if (typeof onPartnerChange === "function") onPartnerChange(pid);
    });
  }

  const secAddress = el("section", { className: "section", "aria-labelledby": "sec-address" });
  secAddress.append(el("h2", { className: "section-title", id: "sec-address" }, "Address"));
  const gridAddress = el("div", { className: "grid" });

  function textInput(id, name, autocomplete) {
    return el("input", {
      id,
      name,
      type: "text",
      className: "field__input",
      autocomplete: autocomplete || undefined,
    });
  }
  function selectInput(id, name) {
    return el("select", { id, name, className: "field__input" });
  }

  const country = selectInput("country", "country");
  country.required = true;
  const countryField = el("div", { className: "field field--country span-4" });
  countryField.append(el("label", { htmlFor: "country" }, "Country"), country);
  address.country = country;

  const line1 = textInput("line1", "address_line_1", "address-line1");
  line1.required = true;
  const line1Field = el("div", { className: "field field--line1 span-7" });
  line1Field.append(el("label", { htmlFor: "line1" }, "Street address line 1"), line1);
  address.address_line_1 = line1;

  const line2 = textInput("line2", "address_line_2", "address-line2");
  const line2Field = el("div", { className: "field field--line2 span-4" });
  const line2Label = optionalLabel("Street address line 2");
  line2Label.htmlFor = "line2";
  line2Field.append(line2Label, line2);
  address.address_line_2 = line2;

  const city = textInput("city", "city", "address-level2");
  city.required = true;
  const cityField = el("div", { className: "field field--city span-3", id: "field-city" });
  cityField.append(el("label", { htmlFor: "city" }, "City"), city);
  address.city = city;

  const region = selectInput("state", "region");
  region.required = true;
  region.disabled = true;
  const regionLabel = el("label", { htmlFor: "state", id: "state-label" }, "State or province");
  const regionField = el("div", { className: "field field--state span-3" });
  regionField.append(regionLabel, region);
  address.region = region;

  const postal = textInput("zip", "postal_code", "postal-code");
  postal.required = true;
  postal.disabled = true;
  if (typeof postal.setAttribute === "function") postal.setAttribute("aria-describedby", "zip-hint");
  const postalLabel = el("label", { htmlFor: "zip", id: "zip-label" }, "ZIP or postal code");
  const postalField = el("div", { className: "field field--zip span-2", id: "field-zip" });
  // Client NOTES: zipHint lives INSIDE field--zip (not a grid sibling).
  const zipHint = el("p", { className: "hint-zip", id: "zip-hint", hidden: true }, "Like 94102 or 94102-1234.");
  postalField.append(postalLabel, postal, zipHint);
  address.postal_code = postal;

  gridAddress.append(countryField, line1Field, line2Field, cityField, regionField, postalField);
  secAddress.append(gridAddress);
  form.append(secAddress);

  const secContact = el("section", { className: "section", "aria-labelledby": "sec-contact" });
  secContact.append(el("h2", { className: "section-title", id: "sec-contact" }, "Contact & time zone"));
  const gridContact = el("div", { className: "grid" });

  const phoneLabel = el("span", { className: "label", id: "phone-label" }, "Phone number");
  const phoneWrap = el("div", { className: "phone", role: "group", "aria-labelledby": "phone-label" });
  const phoneCc = el("select", { id: "phone-cc", name: "phone-cc", "aria-label": "Country calling code" });
  phoneCc.append(el("option", { value: "+1", selected: true }, "+1"));
  const phone = el("input", {
    id: "phone",
    name: "phone",
    type: "tel",
    inputmode: "tel",
    autocomplete: "tel-national",
    className: "field__input",
    required: true,
    "aria-describedby": "phone-hint",
  });
  phoneWrap.append(phoneCc, phone);
  const phoneHint = el("p", { className: "hint", id: "phone-hint" }, "Use 10 digits, like 415 555 0198.");
  const phoneField = el("div", { className: "field field--phone span-4" });
  phoneField.append(phoneLabel, phoneWrap, phoneHint);
  address.phone = phone;
  // API phone_country follows address country (US/CA); visible control is +1-only.
  address.phone_country = { value: "" };

  const email = el("input", {
    id: "email",
    name: "email",
    type: "email",
    autocomplete: "email",
    className: "field__input",
  });
  const emailField = el("div", { className: "field field--email span-4", id: "field-email" });
  const emailLabel = optionalLabel("Email");
  emailLabel.htmlFor = "email";
  emailField.append(emailLabel, email);
  address.email = email;

  const timeZone = selectInput("timezone", "time_zone");
  timeZone.required = true;
  timeZone.disabled = true;
  const tzField = el("div", { className: "field field--tz span-5" });
  tzField.append(el("label", { htmlFor: "timezone" }, "Time zone"), timeZone);
  address.time_zone = timeZone;

  gridContact.append(phoneField, emailField, tzField);
  secContact.append(gridContact);
  form.append(secContact);

  function fillSelect(select, options, blankLabel) {
    select.replaceChildren();
    const blank = el("option", { value: "" }, blankLabel);
    select.append(blank);
    for (const opt of options) {
      const o = el("option", { value: opt.code ?? opt.value }, opt.label);
      select.append(o);
    }
  }

  function applyCountryLabels(code) {
    if (!code) {
      regionLabel.textContent = "State or province";
      postalLabel.textContent = "ZIP or postal code";
      zipHint.hidden = true;
      if (typeof postal.removeAttribute === "function") postal.removeAttribute("aria-describedby");
      return;
    }
    const isUS = code === "US";
    regionLabel.textContent = isUS ? "State" : "Province";
    postalLabel.textContent = isUS ? "ZIP code" : "Postal code";
    zipHint.hidden = false;
    zipHint.textContent = isUS ? "Like 94102 or 94102-1234." : "Like A1A 1A1.";
    if (typeof postal.setAttribute === "function") postal.setAttribute("aria-describedby", "zip-hint");
  }

  address._postalRule = null;
  async function applyCountry(optsApply = {}) {
    const { clearDependent = false } = optsApply;
    const code = country.value;
    region.disabled = !code;
    timeZone.disabled = !code;
    postal.disabled = !code;
    applyCountryLabels(code);
    if (!code) {
      fillSelect(region, [], "Select a country first");
      fillSelect(timeZone, [], "Select a country first");
      region.value = "";
      timeZone.value = "";
      postal.value = "";
      address._postalRule = null;
      address.phone_country.value = "";
      return;
    }
    const [regions, zones, rule] = await Promise.all([
      loadRegions(apiGet, code),
      loadTimeZones(apiGet, code),
      loadPostalRule(apiGet, code),
    ]);
    address._postalRule = rule;
    fillSelect(region, regions, code === "US" ? "Select a state" : "Select a province");
    fillSelect(timeZone, zones, "Select a time zone");
    // No maxlength on text inputs (GAPS 3.1-1); postalMaxLength only guides blur normalize.
    address._postalMax = postalMaxLength(code, rule);
    if (clearDependent) {
      region.value = "";
      timeZone.value = "";
      postal.value = "";
    } else {
      postal.value = normalizePostal(code, postal.value, rule);
    }
    if (code === "US" || code === "CA") address.phone_country.value = code;
  }
  address._applyCountry = applyCountry;

  country.addEventListener("change", () => { void applyCountry({ clearDependent: true }); });
  postal.addEventListener("blur", () => {
    const code = country.value;
    if (!code) return;
    postal.value = normalizePostal(code, postal.value, address._postalRule);
  });

  const countries = await loadCountries(apiGet);
  fillSelect(country, countries, "Select a country");
  await applyCountry();
}

/** Map API / client field keys onto Location form control ids. */
export function locationControlId(field) {
  switch (field) {
    case "partner_id": return "partner-id";
    case "client_id": return "client-id";
    case "name": return "location-name";
    case "address_line_1":
    case "address": return "line1";
    case "address_line_2": return "line2";
    case "city": return "city";
    case "country":
    case "phone_country": return "country";
    case "region": return "state";
    case "postal_code": return "zip";
    case "phone": return "phone";
    case "email": return "email";
    case "time_zone": return "timezone";
    default: return null;
  }
}

export function clearLocationErrors(form) {
  form.querySelector(".error-summary")?.remove?.();
  form.classList?.remove?.("location-form--errors");
  for (const field of form.querySelectorAll(".field.has-error")) {
    field.classList?.remove?.("has-error");
  }
  for (const err of form.querySelectorAll(".error-text, .error-zip")) {
    err.remove?.();
  }
  for (const control of form.querySelectorAll("[aria-invalid]")) {
    control.removeAttribute?.("aria-invalid");
  }
  for (const hint of form.querySelectorAll(".hint, .hint-zip")) {
    hint.hidden = !!(hint.classList?.contains?.("hint-zip") && !form.querySelector("#country")?.value);
  }
}

/**
 * Render inline errors + summary ("There is a problem") for Location create/edit.
 * ZIP errors append inside `.field--zip` (Client NOTES fix).
 * @param {HTMLFormElement} form
 * @param {{field:string, code:string}[]} fields
 */
export function renderLocationErrors(form, fields) {
  clearLocationErrors(form);
  const list = Array.isArray(fields) ? fields.filter((f) => f && f.code) : [];
  if (!list.length) return;

  form.classList?.add?.("location-form--errors");
  const summary = el("div", { className: "error-summary", role: "alert", id: "error-summary" });
  const heading = el("h2");
  heading.innerHTML =
    '<svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true">' +
    '<circle cx="9" cy="9" r="8" stroke="#e44236" stroke-width="1.5"/>' +
    '<path d="M9 5v5.5" stroke="#e44236" stroke-width="1.5" stroke-linecap="round"/>' +
    '<circle cx="9" cy="13" r="0.9" fill="#e44236"/>' +
    "</svg>";
  if (typeof document.createTextNode === "function") heading.append(document.createTextNode("There is a problem"));
  else heading.append(el("span", {}, "There is a problem"));
  summary.append(heading);
  const ul = el("ul");
  const seen = new Set();
  for (const item of list) {
    const id = locationControlId(item.field);
    const sentence = item.code;
    if (!sentence) continue;
    const key = (id || item.field) + "|" + sentence;
    if (seen.has(key)) continue;
    seen.add(key);
    const li = el("li");
    const a = el("a", { href: id ? "#" + id : "#error-summary" }, sentence);
    li.append(a);
    ul.append(li);

    if (!id) continue;
    const control = form.querySelector("#" + id);
    if (!control) continue;
    const fieldEl = control.closest(".field") || control.parentElement;
    if (fieldEl) fieldEl.classList?.add?.("has-error");
    control.setAttribute?.("aria-invalid", "true");
    const errId = id + "-error";
    let err = form.querySelector("#" + errId);
    if (!err) {
      err = el("p", {
        className: id === "zip" ? "error-zip" : "error-text",
        id: errId,
      }, sentence);
      // ZIP hint/error stay inside field--zip (not grid siblings).
      fieldEl?.append(err);
    } else {
      err.textContent = sentence;
    }
    const described = (control.getAttribute("aria-describedby") || "")
      .split(/\s+/)
      .filter(Boolean)
      .filter((x) => x !== errId && !x.endsWith("-hint"));
    described.push(errId);
    control.setAttribute("aria-describedby", described.join(" "));
  }
  summary.append(ul);
  form.prepend(summary);
  for (const hint of form.querySelectorAll(".hint, .hint-zip")) {
    hint.hidden = true;
  }
}
