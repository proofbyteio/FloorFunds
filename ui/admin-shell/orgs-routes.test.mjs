// F-20. /orgs create and sign-in paths are served by the shell; Create location
 // appears in empty and non-empty list states; every in-shell href is served.
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import http from "node:http";
import { createServer } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const root = join(repo, "ui/admin-shell");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = createServer();
    s.listen(0, "127.0.0.1", () => {
      const { port } = s.address();
      s.close((err) => err ? reject(err) : resolve(port));
    });
    s.on("error", reject);
  });
}

async function startShellServer() {
  const port = await freePort();
  const child = spawn(process.execPath, [join(root, "server.mjs")], {
    cwd: repo,
    env: {
      ...process.env,
      ADMIN_DEV_SHELL: "1",
      ASPNETCORE_ENVIRONMENT: "Development",
      ADMIN_SHELL_PORT: String(port),
    },
    stdio: ["ignore", "pipe", "pipe"],
  });
  let boot = "";
  await new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error("admin shell start timeout: " + boot)), 10000);
    const onData = (buf) => {
      boot += buf.toString();
      if (boot.includes("admin shell http://127.0.0.1:" + port)) {
        clearTimeout(t);
        child.stdout.off("data", onData);
        resolve();
      }
    };
    child.stdout.on("data", onData);
    child.stderr.on("data", (buf) => { boot += buf.toString(); });
    child.on("exit", (code) => {
      clearTimeout(t);
      reject(new Error("admin shell exited " + code + ": " + boot));
    });
  });
  return { port, child, base: `http://127.0.0.1:${port}` };
}

function httpGet(base, path) {
  // Use node:http — global fetch is mocked below for shell.js.
  return new Promise((resolve, reject) => {
    const url = new URL(path, base);
    http.get(url, (res) => {
      const chunks = [];
      res.on("data", (c) => chunks.push(c));
      res.on("end", () => {
        resolve({
          status: res.statusCode || 0,
          body: Buffer.concat(chunks).toString("utf8"),
          contentType: String(res.headers["content-type"] || ""),
        });
      });
    }).on("error", reject);
  });
}

function isShellHtml(res) {
  return res.status === 200 && /text\/html/i.test(res.contentType) && /<!doctype html|<html/i.test(res.body);
}

const shellSrc = readFileSync(join(root, "shell.js"), "utf8");
const serverSrc = readFileSync(join(root, "server.mjs"), "utf8");
check("server-serves-catalog", serverSrc.includes('url.pathname === "/catalog"') && serverSrc.includes("catalog-presentation"));
check("create-control-uses-orgs-path", shellSrc.includes('a.href = "/orgs/" + id + "/locations/new"'));
check("create-control-helper-exists", shellSrc.includes("createLocationControl"));

const partner = "11111111-1111-4111-8111-111111111111";
const locationId = "22222222-2222-4222-8222-222222222222";
const shellServer = await startShellServer();
try {
  const newPath = `/orgs/${partner}/locations/new`;
  const signInPath = `/orgs/${partner}/locations/${locationId}/sign-in`;
  const newRes = await httpGet(shellServer.base, newPath);
  const signInRes = await httpGet(shellServer.base, signInPath);
  check("server-serves-orgs-new", isShellHtml(newRes), `status=${newRes.status}`);
  check("server-serves-orgs-sign-in", isShellHtml(signInRes), `status=${signInRes.status}`);
} catch (e) {
  check("server-serves-orgs-new", false, String(e));
  check("server-serves-orgs-sign-in", false, String(e));
}

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

// Live server confirmation: each in-shell /orgs href must be served as the shell HTML.
// Under f20 (server.mjs routes removed) or href-* mutants (broken path), these fail.
const liveHrefFails = [];
for (const h of [...new Set(hrefs)]) {
  try {
    const res = await httpGet(shellServer.base, h);
    if (!isShellHtml(res)) liveHrefFails.push(`${h} status=${res.status}`);
  } catch (e) {
    liveHrefFails.push(`${h} ${e}`);
  }
}
check("every-orgs-href-served-by-live-server", liveHrefFails.length === 0 && hrefs.length > 0, liveHrefFails.join(" | "));

try { shellServer.child.kill("SIGTERM"); } catch { }

if (failed !== 0) process.exit(1);
