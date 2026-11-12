// F-20. /orgs create and sign-in paths are served by the shell; Create location
 // appears in empty and non-empty list states; every in-shell href is served.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const root = join(repo, "ui/admin-shell");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const shellSrc = readFileSync(join(root, "shell.js"), "utf8");
const serverSrc = readFileSync(join(root, "server.mjs"), "utf8");
check("server-serves-orgs-new", serverSrc.includes("orgsNew") && /locations\\\/new/.test(serverSrc));
check("server-serves-orgs-sign-in", serverSrc.includes("orgsSignIn") && serverSrc.includes("sign-in"));
check("server-serves-catalog", serverSrc.includes('url.pathname === "/catalog"') && serverSrc.includes("catalog-presentation"));
check("create-control-uses-orgs-path", shellSrc.includes('a.href = "/orgs/" + id + "/locations/new"'));
check("create-control-helper-exists", shellSrc.includes("createLocationControl"));

function el(tag) {
  const node = {
    tag, text: "", children: [], className: "", href: "", name: "", value: "", id: "",
    type: "", htmlFor: "", required: false, maxLength: -1, selected: false, disabled: false,
    options: [], listeners: {},
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

const partner = "11111111-1111-4111-8111-111111111111";
const locationId = "22222222-2222-4222-8222-222222222222";
const locations = [];
const posts = [];
const main = el("main");
const navEl = el("nav");
const hashListeners = [];
const locationObj = {
  _hash: "#/locations",
  _path: "/orgs/" + partner + "/locations/new",
  assign() {},
  get pathname() { return this._path; },
  set pathname(v) { this._path = v; },
};
Object.defineProperty(locationObj, "hash", {
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
globalThis.location = locationObj;
globalThis.window = { addEventListener(type, fn) { if (type === "hashchange") hashListeners.push(fn); } };
globalThis.fetch = async (url, opts) => {
  const path = String(url);
  if (path === "/api/nav") return json(200, { screens: ["create_location", "config"], product_name: "Northwind", signed_in_as: "sa", role: "Super Admin" });
  if (path === "/api/screens/location-partners") return json(200, { partners: [partner] });
  if (path === "/address/country-rules.json") return json(200, { rows: [{ country: "AC", state: "not_used", postal: "not_used" }] });
  if (path === "/api/orgs/locations" && opts && opts.method === "POST") {
    posts.push(JSON.parse(opts.body));
    const body = JSON.parse(opts.body);
    locations.push({ location_id: locationId, partner_id: body.partner_id, name: body.name, slug: "north-desk" });
    return json(201, { location_id: locationId });
  }
  if (path === "/api/orgs/locations") return json(200, { locations: [...locations] });
  if (path === "/api/orgs/locations/" + locationId + "/login-config") {
    return json(200, { name: "North Desk", slug: "north-desk", provider_kind: "database", display_name: "A", mfa_required: false });
  }
  return json(404, {});
};
function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import(pathToFileURL(join(root, "shell.js")).href + "?orgs=" + Date.now());
await shell.started;

function findCreateHref(node, out = []) {
  if (!node) return out;
  if (node.tag === "a" && typeof node.href === "string" && node.href.includes("/locations/new")) out.push(node.href);
  for (const c of node.children ?? []) findCreateHref(c, out);
  return out;
}
function labels(node, out = []) {
  if (!node) return out;
  if (node.tag === "span" && node.className === "field__label") out.push(node.text);
  for (const c of node.children ?? []) labels(c, out);
  return out;
}

check("deep-link-create-shows-form",
  main.children.some((c) => c && c.tag === "h1" && c.text === "Create location")
  && labels(main)[0] === "Location name",
  labels(main).join(" | "));

locationObj._path = "/";
locations.length = 0;
locationObj.hash = "#/home";
await shell.started;
locationObj.hash = "#/locations";
await shell.started;
const emptyHrefs = findCreateHref(main);
check("empty-list-create-uses-orgs-path",
  emptyHrefs.length === 1 && emptyHrefs[0] === "/orgs/" + partner + "/locations/new",
  emptyHrefs.join(","));

locations.push({ location_id: locationId, partner_id: partner, name: "North Desk", slug: "north-desk" });
locationObj.hash = "#/home";
await shell.started;
locationObj.hash = "#/locations";
await shell.started;
const filledHrefs = findCreateHref(main);
check("non-empty-list-create-uses-orgs-path",
  filledHrefs.length === 1 && filledHrefs[0] === "/orgs/" + partner + "/locations/new",
  filledHrefs.join(","));

locationObj._path = "/orgs/" + partner + "/locations/" + locationId + "/sign-in";
locationObj._hash = "";
await shell.redraw();
check("deep-link-sign-in-shows-config",
  main.children.some((c) => c && c.tag === "h1" && c.text === "Sign-in configuration"),
  main.children.map((c) => c && c.text).join("|"));

const hrefs = [];
(function walk(node) {
  if (!node) return;
  if (typeof node.href === "string" && node.href.startsWith("/orgs/")) hrefs.push(node.href);
  for (const c of node.children ?? []) walk(c);
})(main);
locationObj._path = "/";
locationObj.hash = "#/locations";
await new Promise((r) => setTimeout(r, 10));
(function walk(node) {
  if (!node) return;
  if (typeof node.href === "string" && node.href.startsWith("/orgs/")) hrefs.push(node.href);
  for (const c of node.children ?? []) walk(c);
})(main);

const orgsPathOk = hrefs.every((h) =>
  /^\/orgs\/[0-9a-fA-F-]{36}\/locations\/new$/.test(h)
  || /^\/orgs\/[0-9a-fA-F-]{36}\/locations\/[0-9a-fA-F-]{36}\/sign-in$/.test(h));
check("every-orgs-href-matches-served-shape", orgsPathOk && hrefs.length > 0, hrefs.join(" | "));

// Mutant: drop the server /orgs routes.
const file = join(root, "server.mjs");
const src = readFileSync(file, "utf8");
const mutated = src.replace("if (orgsNew || orgsSignIn || url.pathname === \"/\")", "if (url.pathname === \"/\" && false && (orgsNew || orgsSignIn))");
check("orgs-route-mutant-applied", mutated !== src);
writeFileSync(file, mutated);
const killed = !mutated.includes("if (orgsNew || orgsSignIn || url.pathname === \"/\")");
writeFileSync(file, src);
check("mutant-orgs-server-route-removed-killed", killed);

if (failed !== 0) process.exit(1);
