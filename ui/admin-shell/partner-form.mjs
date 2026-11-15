// Partner create/edit fields (DEC-268). Client and Location keep appendNorthAmericaAddressFields.
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
  label.append(document.createTextNode(text + " "));
  const opt = el("span", { className: "optional" }, "(optional)");
  label.append(opt);
  return label;
}

/**
 * Partner-only address/contact fields. Country blank by default; labels/hints match the mockup HTML.
 * phone_country for the API is the address country (US/CA); the visible calling-code control is +1 only.
 * @param {HTMLFormElement} form
 * @param {Record<string, any>} address
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 * @param {{ nameInput: HTMLInputElement }} opts
 */
export async function appendPartnerFields(form, address, apiGet, opts) {
  const { nameInput } = opts;

  const secPartner = el("section", { className: "section", "aria-labelledby": "sec-partner" });
  secPartner.append(el("h2", { className: "section-title", id: "sec-partner" }, "Partner"));
  const gridPartner = el("div", { className: "grid" });
  const nameField = el("div", { className: "field field--partner span-6", id: "field-partner-name" });
  nameField.append(el("label", { htmlFor: "partner-name" }, "Partner name"));
  nameInput.id = "partner-name";
  nameInput.name = "name";
  nameInput.type = "text";
  nameInput.autocomplete = "organization";
  nameInput.required = true;
  nameInput.className = "field__input";
  nameField.append(nameInput);
  gridPartner.append(nameField);
  secPartner.append(gridPartner);
  form.append(secPartner);

  const secAddress = el("section", { className: "section", "aria-labelledby": "sec-address" });
  secAddress.append(el("h2", { className: "section-title", id: "sec-address" }, "Address"));
  const gridAddress = el("div", { className: "grid" });

  function textInput(id, name, autocomplete) {
    const input = el("input", {
      id,
      name,
      type: "text",
      className: "field__input",
      autocomplete: autocomplete || undefined,
    });
    return input;
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
  postal.setAttribute("aria-describedby", "zip-hint");
  const postalLabel = el("label", { htmlFor: "zip", id: "zip-label" }, "ZIP or postal code");
  const postalField = el("div", { className: "field field--zip span-2", id: "field-zip" });
  postalField.append(postalLabel, postal);
  address.postal_code = postal;
  const zipHint = el("p", { className: "hint-zip", id: "zip-hint", hidden: true }, "Like 94102 or 94102-1234.");

  gridAddress.append(countryField, line1Field, line2Field, cityField, regionField, postalField, zipHint);
  secAddress.append(gridAddress);
  form.append(secAddress);

  const secContact = el("section", { className: "section", "aria-labelledby": "sec-contact" });
  secContact.append(el("h2", { className: "section-title", id: "sec-contact" }, "Contact & time zone"));
  const gridContact = el("div", { className: "grid" });

  const phoneLabel = el("span", { className: "label", id: "phone-label" }, "Phone number");
  const phoneWrap = el("div", { className: "phone", role: "group", "aria-labelledby": "phone-label" });
  const phoneCc = el("select", { id: "phone-cc", name: "phone-cc", "aria-label": "Country calling code" });
  const ccOpt = el("option", { value: "+1", selected: true }, "+1");
  phoneCc.append(ccOpt);
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
      postal.removeAttribute("aria-describedby");
      return;
    }
    const isUS = code === "US";
    regionLabel.textContent = isUS ? "State" : "Province";
    postalLabel.textContent = isUS ? "ZIP code" : "Postal code";
    zipHint.hidden = false;
    zipHint.textContent = isUS ? "Like 94102 or 94102-1234." : "Like A1A 1A1.";
    postal.setAttribute("aria-describedby", "zip-hint");
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

/** Map API / client field keys onto Partner form control ids. */
export function partnerControlId(field) {
  switch (field) {
    case "name": return "partner-name";
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

export function clearPartnerErrors(form) {
  form.querySelector(".error-summary")?.remove();
  form.classList.remove("partner-form--errors");
  for (const field of form.querySelectorAll(".field.has-error")) {
    field.classList.remove("has-error");
  }
  for (const err of form.querySelectorAll(".error-text, .error-zip")) {
    err.remove();
  }
  for (const control of form.querySelectorAll("[aria-invalid]")) {
    control.removeAttribute("aria-invalid");
  }
  for (const hint of form.querySelectorAll(".hint, .hint-zip")) {
    hint.hidden = hint.classList.contains("hint-zip") && !form.querySelector("#country")?.value;
  }
}

/**
 * Render inline errors + summary ("There is a problem") for Partner create/edit.
 * @param {HTMLFormElement} form
 * @param {{field:string, code:string}[]} fields
 */
export function renderPartnerErrors(form, fields) {
  clearPartnerErrors(form);
  const list = Array.isArray(fields) ? fields.filter((f) => f && f.code) : [];
  if (!list.length) return;

  form.classList.add("partner-form--errors");
  const summary = el("div", { className: "error-summary", role: "alert", id: "error-summary" });
  const heading = el("h2", {}, "There is a problem");
  summary.append(heading);
  const ul = el("ul");
  const seen = new Set();
  for (const item of list) {
    const id = partnerControlId(item.field);
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
    if (fieldEl) fieldEl.classList.add("has-error");
    control.setAttribute("aria-invalid", "true");
    const errId = id + "-error";
    let err = form.querySelector("#" + errId);
    if (!err) {
      err = el("p", {
        className: id === "zip" ? "error-zip" : "error-text",
        id: errId,
      }, sentence);
      if (id === "zip") {
        const grid = control.closest(".grid");
        if (grid) grid.append(err);
        else fieldEl?.append(err);
      } else if (id === "phone") {
        fieldEl?.append(err);
      } else {
        fieldEl?.append(err);
      }
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
