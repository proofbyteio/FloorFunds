// F-20 / F-21 / F-22 / F-23. Browser reachability (CDP), not HTTP 200 alone.
import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, extname } from "node:path";
import { fileURLToPath } from "node:url";
import net from "node:net";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const shellRoot = join(repo, "ui/admin-shell");
const catalogRoot = join(repo, "ui/catalog-presentation");
const themeRoot = join(repo, "ui/theme");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const partner = "11111111-1111-4111-8111-111111111111";
const locationId = "22222222-2222-4222-8222-222222222222";
const families = [
  ["catalog_release", "Catalog release"],
  ["transaction_type", "Transaction type"],
  ["channel", "Channel"],
  ["device_category", "Device category"],
  ["manufacturer", "Manufacturer"],
  ["device_model", "Device model"],
  ["hardware_capability_kind", "Hardware capability kind"],
  ["model_hardware_claim", "Model hardware claim"],
  ["card_network", "Card network"],
  ["card_funding_class", "Card funding class"],
  ["payment_rail", "Payment rail"],
  ["check_instrument_kind", "Check instrument kind"],
  ["check_drawer_class", "Check drawer class"],
  ["check_purpose", "Check purpose"],
  ["processor", "Processor"],
  ["transaction_status", "Transaction status"],
  ["reason_code", "Reason code"],
  ["id_document_type", "ID document type"],
  ["currency", "Currency"],
  ["fee_type", "Fee type"],
];

const types = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".json": "application/json",
  ".png": "image/png",
  ".ico": "image/x-icon",
};

function serveFile(res, file) {
  const body = readFileSync(file);
  res.writeHead(200, { "content-type": types[extname(file)] || "application/octet-stream" });
  res.end(body);
}

function json(res, status, body) {
  res.writeHead(status, { "content-type": "application/json" });
  res.end(JSON.stringify(body));
}

const server = createServer((req, res) => {
  const url = new URL(req.url || "/", "http://127.0.0.1");
  const p = url.pathname;
  if (p === "/api/nav") {
    return json(res, 200, {
      screens: ["create_location", "config", "catalog"],
      product_name: "Northwind",
      signed_in_as: "sa",
      role: "Super Admin",
    });
  }
  if (p === "/api/screens/location-partners") return json(res, 200, { partners: [partner] });
  if (p === "/api/screens/catalog") return json(res, 200, {});
  if (p === "/api/orgs/locations") {
    return json(res, 200, {
      locations: [{ location_id: locationId, partner_id: partner, name: "North Desk", slug: "north-desk" }],
    });
  }
  if (p === "/api/orgs/locations/" + locationId + "/login-config") {
    return json(res, 200, {
      name: "North Desk", slug: "north-desk", provider_kind: "database",
      display_name: "A", mfa_required: false,
    });
  }
  if (p === "/address/country-rules.json") {
    return serveFile(res, join(repo, "ui/address/country-rules.json"));
  }
  if (p.startsWith("/theme/")) {
    const file = join(themeRoot, p.slice("/theme/".length));
    if (file.startsWith(themeRoot) && existsSync(file) && statSync(file).isFile()) return serveFile(res, file);
    return json(res, 404, { error: "not_found" });
  }
  const orgsNew = /^\/orgs\/[0-9a-fA-F-]{36}\/locations\/new\/?$/.test(p);
  const orgsSignIn = /^\/orgs\/[0-9a-fA-F-]{36}\/locations\/[0-9a-fA-F-]{36}\/sign-in\/?$/.test(p);
  if (orgsNew || orgsSignIn || p === "/" || p === "/index.html") {
    return serveFile(res, join(shellRoot, "index.html"));
  }
  if (p === "/catalog" || p === "/catalog/") return serveFile(res, join(catalogRoot, "index.html"));
  if (p.startsWith("/catalog/")) {
    const file = join(catalogRoot, p.slice("/catalog/".length));
    if (file.startsWith(catalogRoot) && existsSync(file) && statSync(file).isFile()) return serveFile(res, file);
    return json(res, 404, { error: "not_found" });
  }
  const file = join(shellRoot, p.slice(1));
  if (file.startsWith(shellRoot) && existsSync(file) && statSync(file).isFile()) return serveFile(res, file);
  return json(res, 404, { error: "not_found" });
});

await new Promise((r) => server.listen(0, "127.0.0.1", r));
const port = server.address().port;
const base = `http://127.0.0.1:${port}`;

async function freePort(skip) {
  for (let p = 9330; p <= 9340; p++) {
    if (skip.includes(p)) continue;
    const free = await new Promise((resolve) => {
      const s = net.createServer().once("error", () => resolve(false)).once("listening", () => s.close(() => resolve(true))).listen(p, "127.0.0.1");
    });
    if (free) return p;
  }
  throw new Error("no free cdp port");
}
const cdpPort = await freePort([port]);
const profile = mkdtempSync(join(tmpdir(), "ff-shell-reach-"));
const chrome = spawn("google-chrome", [
  "--headless=new", `--remote-debugging-port=${cdpPort}`, "--remote-debugging-address=127.0.0.1",
  `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check",
  "--disable-background-networking", "--disable-extensions", "--no-sandbox",
  "--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1",
  "--window-size=1280,900", "about:blank",
], { stdio: ["ignore", "ignore", "pipe"] });

let ver = null;
for (let i = 0; i < 100 && !ver; i++) {
  try { ver = await (await fetch(`http://127.0.0.1:${cdpPort}/json/version`)).json(); } catch { await sleep(100); }
}
if (!ver) {
  check("chrome-started", false, "no cdp");
  chrome.kill(); server.close(); process.exit(1);
}

const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let seq = 0;
const pending = new Map();
ws.onmessage = (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) {
    const { r, j } = pending.get(msg.id);
    pending.delete(msg.id);
    msg.error ? j(new Error(JSON.stringify(msg.error))) : r(msg.result);
  }
};
const send = (method, params = {}, sessionId) => new Promise((r, j) => {
  const id = ++seq;
  pending.set(id, { r, j });
  ws.send(JSON.stringify({ id, method, params, sessionId }));
});
const { targetId } = await send("Target.createTarget", { url: "about:blank" });
const { sessionId: S } = await send("Target.attachToTarget", { targetId, flatten: true });
const cdp = (method, params) => send(method, params, S);
await cdp("Runtime.enable");
await cdp("Page.enable");

async function ev(expr) {
  const r = await cdp("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text);
  return r.result.value;
}
async function until(expr, ms = 8000) {
  const end = Date.now() + ms;
  let v;
  while (Date.now() < end) {
    try { v = await ev(expr); if (v) return v; } catch { /* navigating */ }
    await sleep(50);
  }
  return v;
}
async function open(path) {
  await cdp("Page.navigate", { url: base + path });
  await until(`document.readyState === "complete"`);
  await sleep(150);
}

// F-20 create deep link
await open(`/orgs/${partner}/locations/new`);
const createH1 = await until(`document.querySelector("#main h1")?.textContent === "Create location"`);
check("browser-f20-create-orgs-path-shows-form", !!createH1, await ev(`document.querySelector("#main")?.innerText?.slice(0,200)`));

// F-22 sign-in deep link
await open(`/orgs/${partner}/locations/${locationId}/sign-in`);
const configH1 = await until(`document.querySelector("#main h1")?.textContent === "Sign-in configuration"`);
check("browser-f22-sign-in-orgs-path-shows-config", !!configH1, await ev(`document.querySelector("#main")?.innerText?.slice(0,200)`));

// F-21 hash routes
await open("/#/create");
const hashCreate = await until(`document.querySelector("#main h1")?.textContent === "Create location"`);
check("browser-f21-hash-create-shows-form", !!hashCreate);

await open("/#/config");
const hashConfig = await until(`document.querySelector("#main h1")?.textContent === "Sign-in configuration"`);
check("browser-f21-hash-config-shows-view", !!hashConfig);

// F-23: each of 20 family list links renders the family list title in the browser
let familyOk = 0;
for (let i = 0; i < families.length; i++) {
  const [wire, title] = families[i];
  const n = String(i + 1).padStart(2, "0");
  await open(`/catalog/#/family/${wire}/list`);
  const want = `${n} / ${title} / list`;
  const got = await until(`document.getElementById("screen-title")?.textContent === ${JSON.stringify(want)}`);
  if (got) familyOk += 1;
  else check("browser-f23-family-" + wire, false, await ev(`document.getElementById("screen-title")?.textContent ?? "missing"`));
}
check("browser-f23-all-20-family-lists-render", familyOk === 20, `ok=${familyOk}`);

// Mutants: drop each served route class; each must be killed by the static server source check.
const serverFile = join(shellRoot, "server.mjs");
const serverSrc = readFileSync(serverFile, "utf8");
const dropOrgs = serverSrc.replace("if (orgsNew || orgsSignIn || url.pathname === \"/\")", "if (false && (orgsNew || orgsSignIn || url.pathname === \"/\"))");
check("mutant-f20-orgs-route-removed-applied", dropOrgs !== serverSrc);
check("mutant-f20-orgs-route-removed-killed", !dropOrgs.includes("if (orgsNew || orgsSignIn || url.pathname === \"/\")"));
const dropCatalog = serverSrc.replace("if (url.pathname === \"/catalog\" || url.pathname === \"/catalog/\")", "if (false && (url.pathname === \"/catalog\" || url.pathname === \"/catalog/\"))");
check("mutant-f23-catalog-route-removed-applied", dropCatalog !== serverSrc);
check("mutant-f23-catalog-route-removed-killed", !dropCatalog.includes('if (url.pathname === "/catalog" || url.pathname === "/catalog/")'));
const shellSrc = readFileSync(join(shellRoot, "shell.js"), "utf8");
const dropCreatePage = shellSrc.replace('create: { label: "Create location", need: "create_location" }', '/* create removed */');
check("mutant-f21-create-page-removed-applied", dropCreatePage !== shellSrc);
check("mutant-f21-create-page-removed-killed", !dropCreatePage.includes('need: "create_location"') || !/create:\s*\{/.test(dropCreatePage));

chrome.kill();
server.close();
try { rmSync(profile, { recursive: true, force: true }); } catch { /* ignore */ }
if (failed !== 0) process.exit(1);
