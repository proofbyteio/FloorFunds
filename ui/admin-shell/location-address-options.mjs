// Address dropdown options for Create location and Create client.
// DEC-258/260: countries, regions, postal rules, and time zones come from GET /api/lookups/*.

export const DEFAULT_COUNTRY = "US";

/** @deprecated Prefer loadCountries(); kept empty so accidental sync use fails closed. */
export const COUNTRIES = [];
/** @deprecated Prefer loadRegions(country). */
export const US_STATES = [];
/** @deprecated Prefer loadRegions(country). */
export const CA_PROVINCES = [];
/** @deprecated Prefer loadTimeZones(country). */
export const TIME_ZONES = [];

/** Field length limits for the create form (UI). Postal max comes from the postal-rule API when available. */
export const FIELD_LIMITS = {
  postal_code_us: 10,
  postal_code_ca: 7,
  // F5: was 20 — too short for formatted numbers the server accepts (spaces/dashes/parens).
  phone: 32,
  email: 100,
};

/**
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 */
export async function loadCountries(apiGet) {
  const res = await apiGet("/api/lookups/countries");
  if (res.status !== 200 || !Array.isArray(res.body?.countries)) return [];
  return res.body.countries.map((c) => ({ code: c.code, label: c.label }));
}

/**
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 * @param {string} country
 */
export async function loadRegions(apiGet, country) {
  if (!country) return [];
  const res = await apiGet("/api/lookups/regions?country=" + encodeURIComponent(country));
  if (res.status !== 200 || !Array.isArray(res.body?.regions)) return [];
  return res.body.regions.map((r) => ({ code: r.code, label: r.label }));
}

/**
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 * @param {string} country
 */
export async function loadTimeZones(apiGet, country) {
  if (!country) return [];
  const res = await apiGet("/api/lookups/time-zones?country=" + encodeURIComponent(country));
  if (res.status !== 200 || !Array.isArray(res.body?.time_zones)) return [];
  return res.body.time_zones.map((z) => ({ value: z.value, label: z.label }));
}

/**
 * @param {(path: string) => Promise<{status:number, body?: any}>} apiGet
 * @param {string} country
 */
export async function loadPostalRule(apiGet, country) {
  if (!country) return null;
  const res = await apiGet("/api/lookups/postal-rule?country=" + encodeURIComponent(country));
  if (res.status !== 200 || !res.body) return null;
  return {
    pattern: res.body.pattern,
    max_length: res.body.max_length,
    normalize: res.body.normalize,
  };
}

export function regionsFor(country) {
  return [];
}

export function postalMaxLength(country, rule) {
  if (rule && Number.isInteger(rule.max_length)) return rule.max_length;
  if (country === "CA") return FIELD_LIMITS.postal_code_ca;
  return FIELD_LIMITS.postal_code_us;
}

/** CA: strip spaces + uppercase; format A1A1A1 → A1A 1A1 only when exactly 6 chars (F1: never truncate). */
export function normalizePostal(country, value, rule) {
  let v = String(value ?? "").trim();
  const mode = rule?.normalize || (country === "CA" ? "ca_postal" : country === "US" ? "us_zip" : "none");
  if (mode === "ca_postal") {
    v = v.toUpperCase().replace(/\s+/g, "");
    if (v.length !== 6) return v; // leave untruncated; postalError / server reject
    return v.slice(0, 3) + " " + v.slice(3);
  }
  if (mode === "us_zip") return v.replace(/\s+/g, "");
  return v;
}
