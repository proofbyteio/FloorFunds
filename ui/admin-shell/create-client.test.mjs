// Create client reuses the same US/CA address controls as Create location.
// Write stays held (PQ-278 → client_create_pending_pq278).
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
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
    remove() {
      // no-op for banner teardown in the fake DOM
    },
  };
  return node;
}

const posts = [];
const main = el("main");
const navEl = el("nav");
const hashListeners = [];
const locationObj = {
  _hash: "#/home",
  assign() {},
};
Object.defineProperty(locationObj, "hash", {
  get() { return this._hash; },
  set(v) {
    this._hash = v;
    for (const fn of hashListeners) fn();
  },
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
globalThis.location = locationObj;
globalThis.window = { addEventListener(type, fn) { if (type === "hashchange") hashListeners.push(fn); } };
globalThis.fetch = async (url, opts) => {
  const path = String(url);
  if (path === "/api/nav") return json(200, { screens: ["create_client", "create_location"], product_name: "Northwind" });
  if (path === "/api/screens/create-client" && opts && opts.method === "POST") {
    posts.push(JSON.parse(opts.body));
    return json(403, { error: "client_create_pending_pq278" });
  }
  if (path === "/api/screens/create-client") return json(200, { screen: "create_client", write: "held" });
  if (path === "/address/country-rules.json") return json(200, { rows: [
    { country: "US", state: "required", postal: "required" },
    { country: "CA", state: "required", postal: "required" },
  ] });
  return json(404, {});
};
function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import("./shell.js");
await shell.started;
await shell.showClients();

const labels = [];
(function walk(node) {
  if (!node) return;
  if (node.tag === "span" && node.className === "field__label") labels.push(node.text);
  for (const child of node.children ?? []) walk(child);
})(main);
check("client-name-is-first", labels[0] === "Client name" && labels[1] === "Address line 1 *", labels.join(" | "));

const pageText = (function texts(node, acc) {
  if (!node) return acc;
  if (node.text) acc.push(node.text);
  for (const child of node.children ?? []) texts(child, acc);
  return acc;
})(main, []);
check("held-write-banner-is-shown", pageText.includes("Create is held"), pageText.join(" | "));

function findNamed(root, field) {
  if (!root) return null;
  if (root.name === field) return root;
  for (const child of root.children ?? []) {
    const hit = findNamed(child, field);
    if (hit) return hit;
  }
  return null;
}
const form = (function find(node) {
  if (!node) return null;
  if (node.tag === "form") return node;
  for (const child of node.children ?? []) {
    const hit = find(child);
    if (hit) return hit;
  }
  return null;
})(main);

const nameInput = findNamed(form, "name");
const country = findNamed(form, "country");
check("country-defaults-to-us-and-lists-only-us-ca",
  country?.value === "US"
  && country.options.filter((o) => o.value).map((o) => o.value).join(",") === "US,CA",
  country?.options?.map((o) => o.value).join(","));
country.value = "CA";
country.listeners.change();
const region = findNamed(form, "region");
const postal = findNamed(form, "postal_code");
const zone = findNamed(form, "time_zone");
const phone = findNamed(form, "phone");
const email = findNamed(form, "email");
check("province-is-a-required-select-for-ca", region?.tag === "select" && region.required === true && region.options.some((o) => o.value === "ON"));
check("timezone-is-a-required-select", zone?.tag === "select" && zone.required === true && zone.options.some((o) => o.value === "America/Toronto"));
check("postal-phone-email-have-length-limits",
  postal?.maxLength === 7 && phone?.maxLength === 20 && email?.maxLength === 50 && email?.type === "email",
  `postal=${postal?.maxLength} phone=${phone?.maxLength} email=${email?.maxLength}/${email?.type}`);

nameInput.value = "  ";
await form.listeners.submit({ preventDefault() {} });
check("blank-name-does-not-submit", posts.length === 0, String(posts.length));

nameInput.value = "Acme Casino";
for (const field of ["address_line_1", "city", "phone"]) {
  const input = findNamed(form, field);
  if (input) input.value = "One";
}
region.value = "ON";
postal.value = "M5V 2T6";
zone.value = "America/Toronto";
email.value = "ops@example.com";
await form.listeners.submit({ preventDefault() {} });
check("held-post-sends-name-and-address-payload",
  posts.length === 1
  && posts[0].name === "Acme Casino"
  && posts[0].address?.country === "CA"
  && posts[0].address?.region === "ON"
  && posts[0].address?.postal_code === "M5V 2T6"
  && posts[0].address?.time_zone === "America/Toronto"
  && posts[0].address?.email === "ops@example.com",
  JSON.stringify(posts[0] ?? null));

const afterSubmit = (function texts(node, acc) {
  if (!node) return acc;
  if (node.text) acc.push(node.text);
  for (const child of node.children ?? []) texts(child, acc);
  return acc;
})(form, []);
check("held-api-error-is-surfaced", afterSubmit.includes("client_create_pending_pq278") || afterSubmit.some((t) => String(t).includes("Could not create the client")), afterSubmit.join(" | "));

void repo;
if (failed !== 0) process.exit(1);
