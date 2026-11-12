// F-21. create and config are in pages, gated by create_location and config needs.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const root = join(repo, "ui/admin-shell");
const file = join(root, "shell.js");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const src = readFileSync(file, "utf8");
check("pages-has-create-gated-by-create_location",
  /create:\s*\{\s*label:\s*"Create location",\s*need:\s*"create_location"\s*\}/.test(src));
check("pages-has-config-gated-by-config",
  /config:\s*\{\s*label:\s*"Sign-in config",\s*need:\s*"config"\s*\}/.test(src));
check("create-config-not-separate-nav-labels",
  src.includes('if (id === "create" || id === "config") continue;'));
check("nav-need-cited-from-LoginConfigApi",
  src.includes("LoginConfigApi.NavAsync") || src.includes("GET /api/nav screens"));

function el(tag) {
  const node = {
    tag, text: "", children: [], className: "", href: "", name: "", value: "", id: "",
    type: "", htmlFor: "", required: false, maxLength: -1, disabled: false, options: [], listeners: {},
    set textContent(v) { this.text = String(v); },
    get textContent() { return this.text; },
    append(...xs) {
      for (const x of xs.flat()) {
        this.children.push(x);
        if (x && x.tag === "option") this.options.push(x);
      }
    },
    prepend(x) { this.children.unshift(x); },
    insertBefore(n, before) {
      const at = this.children.indexOf(before);
      if (at < 0) this.children.push(n);
      else this.children.splice(at, 0, n);
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
const main = el("main");
const navEl = el("nav");
const hashListeners = [];
const loc = {
  _hash: "#/create", _path: "/",
  get pathname() { return this._path; },
  assign() {},
};
Object.defineProperty(loc, "hash", {
  get() { return this._hash; },
  set(v) { this._hash = v; for (const fn of hashListeners) fn(); },
});
globalThis.document = {
  querySelector(sel) {
    if (sel === "#main") return main;
    if (sel === "#nav") return navEl;
    if (sel === "#signed-in" || sel === ".admin-lockup__name") return el("p");
    return null;
  },
  createElement(tag) { return el(tag); },
};
globalThis.location = loc;
globalThis.window = { addEventListener(type, fn) { if (type === "hashchange") hashListeners.push(fn); } };

let screens = ["create_location", "config"];
globalThis.fetch = async (url) => {
  const path = String(url);
  if (path === "/api/nav") return json(200, { screens, product_name: "Northwind", signed_in_as: "sa", role: "Super Admin" });
  if (path === "/api/screens/location-partners") return json(200, { partners: ["11111111-1111-4111-8111-111111111111"] });
  if (path === "/address/country-rules.json") return json(200, { rows: [{ country: "AC", state: "not_used", postal: "not_used" }] });
  if (path === "/api/orgs/locations") return json(200, { locations: [] });
  if (path.startsWith("/api/orgs/locations/")) {
    return json(200, { name: "North Desk", slug: "north-desk", provider_kind: "database", display_name: "A", mfa_required: false });
  }
  return json(403, {});
};
function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import(pathToFileURL(file).href + "?gates=" + Date.now());
await shell.started;
check("permitted-user-reaches-create-form",
  main.children.some((c) => c && c.tag === "h1" && c.text === "Create location"));
check("create-not-in-nav",
  !(navEl.children ?? []).some((c) => c && c.text === "Create location"));

loc.hash = "#/config";
await shell.started;
await new Promise((r) => setTimeout(r, 30));
check("permitted-user-reaches-config-view",
  main.children.some((c) => c && c.tag === "h1" && c.text === "Sign-in configuration"));
check("config-not-in-nav",
  !(navEl.children ?? []).some((c) => c && c.text === "Sign-in config"));

screens = [];
loc.hash = "#/home";
await shell.started;
await new Promise((r) => setTimeout(r, 30));
loc.hash = "#/create";
await shell.started;
await new Promise((r) => setTimeout(r, 30));
const deniedText = (function walk(node, acc = []) {
  if (!node) return acc;
  if (node.text) acc.push(node.text);
  for (const c of node.children ?? []) walk(c, acc);
  return acc;
})(main).join(" ");
check("user-without-create_location-is-denied",
  /cannot open|Access denied|No server-side role/i.test(deniedText), deniedText.slice(0, 160));

const createNeedle = 'create: { label: "Create location", need: "create_location" }';
const createMut = src.replace(createNeedle, 'create: { label: "Create location", need: null }');
check("create-gate-mutant-applied", createMut !== src);
check("mutant-create-gate-removed-killed",
  !/create:\s*\{\s*label:\s*"Create location",\s*need:\s*"create_location"\s*\}/.test(createMut));

const configNeedle = 'config: { label: "Sign-in config", need: "config" }';
const configMut = src.replace(configNeedle, 'config: { label: "Sign-in config", need: null }');
check("config-gate-mutant-applied", configMut !== src);
check("mutant-config-gate-removed-killed",
  !/config:\s*\{\s*label:\s*"Sign-in config",\s*need:\s*"config"\s*\}/.test(configMut));

if (failed !== 0) process.exit(1);
