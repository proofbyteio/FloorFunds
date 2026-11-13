// DEC-237. Location name is required, first, and posted as the existing name field.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const validator = join(repo, "src/Admin.Api/LoginConfigInput.cs");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

function el(tag) {
  const node = {
    tag,
    text: "",
    children: [],
    className: "",
    href: "",
    name: "",
    value: "",
    id: "",
    type: "",
    htmlFor: "",
    required: false,
    maxLength: -1,
    disabled: false,
    options: [],
    listeners: {},
    set textContent(v) { this.text = String(v); },
    get textContent() { return this.text; },
    append(...xs) {
      for (const x of xs.flat()) {
        this.children.push(x);
        if (x && x.tag === "option") this.options.push(x);
      }
    },
    prepend(x) { this.children.unshift(x); },
    insertBefore(node, before) {
      const at = this.children.indexOf(before);
      if (at < 0) this.children.push(node);
      else this.children.splice(at, 0, node);
    },
    replaceChildren(...xs) { this.children = xs.flat(); this.options = this.children.filter((x) => x && x.tag === "option"); },
    setAttribute() {},
    addEventListener(type, fn) { this.listeners[type] = fn; },
    querySelector(sel) {
      const walk = [this, ...this.children];
      if (sel === "span") return walk.find((x) => x && x.tag === "span") ?? null;
      if (sel === ".banner--error") return walk.find((x) => x && String(x.className).includes("banner--error")) ?? null;
      return null;
    },
    remove() {},
  };
  return node;
}

const posts = [];
const locations = [];
const main = el("main");
const navEl = el("nav");
const hashListeners = [];
const locationObj = {
  hash: "#/create",
  assign() {},
};
Object.defineProperty(locationObj, "hash", {
  get() { return this._hash; },
  set(v) {
    this._hash = v;
    for (const fn of hashListeners) fn();
  },
});
locationObj._hash = "#/home";

globalThis.document = {
  querySelector(sel) {
    if (sel === "#main") return main;
    if (sel === "#nav") return navEl;
    if (sel === "#signed-in" || sel === ".admin-lockup__name") return el("p");
    return null;
  },
  createElement(tag) { return el(tag); },
};
globalThis.location = locationObj;
globalThis.window = { addEventListener(type, fn) { if (type === "hashchange") hashListeners.push(fn); } };
globalThis.fetch = async (url, opts) => {
  const path = String(url);
  if (path === "/api/nav") return json(200, { screens: ["create_location"], product_name: "Northwind" });
  if (path === "/api/screens/location-partners") return json(200, { partners: ["11111111-1111-4111-8111-111111111111"] });
  if (path === "/address/country-rules.json") return json(200, { rows: [
    { country: "US", state: "required", postal: "required" },
    { country: "CA", state: "required", postal: "required" },
  ] });
  if (path === "/api/orgs/locations" && opts && opts.method === "POST") {
    posts.push(JSON.parse(opts.body));
    const body = JSON.parse(opts.body);
    locations.push({ location_id: "22222222-2222-4222-8222-222222222222", partner_id: body.partner_id, name: body.name, slug: "north-desk" });
    return json(201, { location_id: locations[0].location_id });
  }
  if (path === "/api/orgs/locations") return json(200, { locations });
  return json(404, {});
};
function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import("./shell.js");
await shell.started;
await shell.showCreate();

check("blank-location-name-is-rejected-by-the-client", shell.locationNameClientError("  ") === "required");
check("location-name-limit-is-the-existing-200-scalars", shell.locationNameClientError("n".repeat(201)) === "name_invalid" && shell.locationNameClientError("North Desk") === "");

const labels = [];
(function walk(node) {
  if (!node) return;
  if (node.tag === "span" && node.className === "field__label") labels.push(node.text);
  for (const child of node.children ?? []) walk(child);
})(main);
check("location-name-is-first", labels[0] === "Location name" && labels[1] === "Address line 1 *", labels.join(" | "));
const nameInput = (function find(node) {
  if (!node) return null;
  if (node.name === "name") return node;
  for (const child of node.children ?? []) {
    const hit = find(child);
    if (hit) return hit;
  }
  return null;
})(main);
check("location-name-has-no-placeholder", nameInput && nameInput.required === true && !nameInput.placeholder && !(nameInput.maxLength > 0));

nameInput.value = "   ";
await nameInput.formListeners?.submit?.({ preventDefault() {} });
const form = (function find(node) {
  if (!node) return null;
  if (node.tag === "form") return node;
  for (const child of node.children ?? []) {
    const hit = find(child);
    if (hit) return hit;
  }
  return null;
})(main);
await form.listeners.submit({ preventDefault() {} });
check("blank-name-does-not-submit", posts.length === 0, String(posts.length));

function findNamed(root, field) {
  if (!root) return null;
  if (root.name === field) return root;
  for (const child of root.children ?? []) {
    const hit = findNamed(child, field);
    if (hit) return hit;
  }
  return null;
}
nameInput.value = "North Desk";
for (const field of ["address_line_1", "city", "phone"]) {
  const input = findNamed(form, field);
  if (input) input.value = "One";
}
const country = findNamed(form, "country");
check("country-defaults-to-us-and-lists-only-us-ca",
  country?.value === "US"
  && country.options.filter((o) => o.value).map((o) => o.value).join(",") === "US,CA",
  country?.options?.map((o) => o.value).join(","));
country.value = "US";
country.listeners.change();
const region = findNamed(form, "region");
const postal = findNamed(form, "postal_code");
const zone = findNamed(form, "time_zone");
const phone = findNamed(form, "phone");
const email = findNamed(form, "email");
check("state-is-a-required-select-for-us", region?.tag === "select" && region.required === true && region.options.some((o) => o.value === "NV"));
check("timezone-is-a-required-select", zone?.tag === "select" && zone.required === true && zone.options.some((o) => o.value === "America/Los_Angeles"));
check("postal-phone-email-have-length-limits",
  postal?.maxLength === 10 && phone?.maxLength === 20 && email?.maxLength === 254 && email?.type === "email",
  `postal=${postal?.maxLength} phone=${phone?.maxLength} email=${email?.maxLength}/${email?.type}`);
region.value = "NV";
postal.value = "89101";
zone.value = "America/Los_Angeles";
await form.listeners.submit({ preventDefault() {} });
await shell.started;
const listed = (function texts(node, acc) {
  if (!node) return acc;
  if (node.text) acc.push(node.text);
  for (const child of node.children ?? []) texts(child, acc);
  return acc;
})(main, []);
check("valid-name-submits-and-shows-in-the-locations-list",
  posts.length === 1 && posts[0].name === "North Desk" && locationObj.hash === "#/locations" && listed.includes("North Desk"),
  `posts=${posts.length} hash=${locationObj.hash} texts=${listed.join(",")}`);

if (!process.argv.includes("--child")) {
  const good = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--name-required-check"], {
    cwd: repo, encoding: "utf8",
  });
  const goodOut = (good.stdout || "") + (good.stderr || "");
  check("blank-location-name-is-rejected-by-the-server", goodOut.includes("PASS create-rejects-blank-location-name"), goodOut.trim().split("\n").slice(-8).join(" | "));
  const original = readFileSync(validator, "utf8");
  const needle = 'if (!o.TryGetProperty("name", out var nm)) errs.Add(new("name", "required"));';
  const mutated = original.replace(needle, 'if (!o.TryGetProperty("name", out var nm)) { }');
  check("server-required-check-is-present", mutated !== original);
  writeFileSync(validator, mutated);
  const built = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--name-required-check"], {
    cwd: repo, encoding: "utf8",
  });
  writeFileSync(validator, original);
  const out = (built.stdout || "") + (built.stderr || "");
  check("mutant-dropped-name-required-killed", out.includes("FAIL create-rejects-blank-location-name"), out.trim().split("\n").slice(-5).join(" | "));
}

if (failed !== 0) process.exit(1);
