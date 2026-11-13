// Client-side mirrors of OrgFormValidation (server is authoritative). Exact owner sentences only.

export function normalizeText(value) {
  return String(value ?? "").trim().replace(/\s+/g, " ");
}

export function scalarLen(value) {
  return [...String(value ?? "")].length;
}

const NAME_CHARS = /^[\p{L}\p{N} &\.,'\-()/]+$/u;
const STREET_CHARS = /^[\p{L}\p{N} #\-\.,/']+$/u;
const CITY_CHARS = /^[\p{L} \-\.']+$/u;

export function entityNameError(entity, value) {
  const label = entity[0].toUpperCase() + entity.slice(1);
  const name = normalizeText(value);
  if (!name) return `Enter a ${entity} name.`;
  const n = scalarLen(name);
  if (n < 2 || n > 100) return `${label} name must be 2 to 100 characters.`;
  if (!NAME_CHARS.test(name)) return `${label} name can only use letters, numbers, spaces and & . , ' - ( ) /`;
  return "";
}

export function street1Error(value) {
  const v = normalizeText(value);
  if (!v) return "Enter a street address.";
  if (scalarLen(v) > 100) return "Street address must be 100 characters or fewer.";
  if (!STREET_CHARS.test(v)) return "Street address can only use letters, numbers, spaces and # - . , / '";
  return "";
}

export function street2Error(value) {
  const v = normalizeText(value);
  if (!v) return "";
  if (scalarLen(v) > 40) return "Address line 2 must be 40 characters or fewer.";
  if (!STREET_CHARS.test(v)) return "Address line 2 can only use letters, numbers, spaces and # - . , / '";
  return "";
}

export function cityError(value) {
  const v = normalizeText(value);
  if (!v) return "Enter a city.";
  const n = scalarLen(v);
  if (n < 2 || n > 50) return "City must be 2 to 50 characters.";
  if (!CITY_CHARS.test(v) || /\d/.test(v)) return "City can only use letters, spaces and - . '";
  return "";
}

export function countryError(value) {
  return value ? "" : "Select a country.";
}

export function regionError(value, country) {
  if (!country) return "";
  return value ? "" : "Select a state or province.";
}

export function postalError(value, country, rule) {
  if (!country) return "";
  let v = String(value ?? "").trim();
  if (!v) return "Enter a ZIP or postal code.";
  // F1: never truncate. CA — strip spaces + uppercase; reject unless exactly 6 chars;
  // then insert the space and apply the regex. US — strip spaces only.
  if (rule?.normalize === "ca_postal" || country === "CA") {
    v = v.toUpperCase().replace(/\s+/g, "");
    if (v.length !== 6) return "Enter a valid Canadian postal code, like A1A 1A1.";
    v = v.slice(0, 3) + " " + v.slice(3);
  } else {
    v = v.replace(/\s+/g, "");
  }
  const pattern = rule?.pattern ? new RegExp(rule.pattern) : country === "CA" ? /^[A-Z]\d[A-Z] \d[A-Z]\d$/ : /^\d{5}(-\d{4})?$/;
  if (!pattern.test(v)) {
    return country === "CA"
      ? "Enter a valid Canadian postal code, like A1A 1A1."
      : "Enter a valid US ZIP code, like 12345 or 12345-6789.";
  }
  return "";
}

/** Strip spaces - . ( ); reject letters. Full IsValidNumberForRegion is server-side. */
export function phoneError(value) {
  const raw = String(value ?? "");
  if (!raw.trim()) return "Enter a phone number.";
  let cleaned = "";
  for (const ch of raw) {
    if (/\s/.test(ch) || ch === "-" || ch === "." || ch === "(" || ch === ")") continue;
    cleaned += ch;
  }
  if (!cleaned) return "Enter a phone number.";
  if (/[A-Za-z]/.test(cleaned) || /ext/i.test(cleaned)) {
    return "Enter a valid phone number for the selected country.";
  }
  if (!/^\+?[0-9]{7,15}$/.test(cleaned)) {
    return "Enter a valid phone number for the selected country.";
  }
  return "";
}

export function emailError(value) {
  const v = String(value ?? "").trim();
  if (!v) return "";
  if (scalarLen(v) > 100) return "Email must be 100 characters or fewer.";
  const at = v.indexOf("@");
  if (at <= 0 || at !== v.lastIndexOf("@") || v.includes(" ") || !v.slice(at + 1).includes(".")) {
    return "Enter a valid email address, like name@example.com.";
  }
  return "";
}

export function timeZoneError(value, country) {
  if (!country) return "";
  return value ? "" : "Select a time zone.";
}

export function addressFieldErrors(address) {
  const country = address.country.value;
  const out = [];
  const push = (field, msg) => { if (msg) out.push({ field, code: msg }); };
  push("address_line_1", street1Error(address.address_line_1.value));
  push("address_line_2", street2Error(address.address_line_2.value));
  push("city", cityError(address.city.value));
  push("country", countryError(country));
  push("region", regionError(address.region.value, country));
  push("postal_code", postalError(address.postal_code.value, country, address._postalRule));
  push("phone", phoneError(address.phone.value));
  push("email", emailError(address.email.value));
  push("time_zone", timeZoneError(address.time_zone.value, country));
  return out;
}

export function fieldsBannerDetail(fields) {
  if (!Array.isArray(fields) || fields.length === 0) return "";
  return fields.map((f) => f.code || f.message || "").filter(Boolean).join(" ");
}
