// F-07. The Catalog item follows the server need "catalog", and the read API stays 403 without it.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

function el(tag) {
  return {
    tag, text: "", children: [], className: "", href: "",
    set textContent(v) { this.text = String(v); },
    get textContent() { return this.text; },
    append(...xs) { this.children.push(...xs.flat()); },
    replaceChildren(...xs) { this.children = xs.flat(); },
    setAttribute() {},
  };
}
const main = el("main");
const navEl = el("nav");
const listeners = [];
const loc = { _hash: "#/home", assign() {} };
Object.defineProperty(loc, "hash", {
  get() { return this._hash; },
  set(v) { this._hash = v; for (const fn of listeners) fn(); },
});
let screens = ["create_location", "catalog"];
globalThis.document = {
  querySelector(sel) {
    if (sel === "#main") return main;
    if (sel === "#nav") return navEl;
    return el("p");
  },
  createElement: el,
};
globalThis.location = loc;
globalThis.window = { addEventListener(type, fn) { if (type === "hashchange") listeners.push(fn); } };
globalThis.fetch = async (url) => {
  const path = String(url);
  if (path === "/api/nav") return json(200, { screens, product_name: "Northwind" });
  if (path === "/api/screens/catalog") return json(screens.includes("catalog") ? 200 : 403, { screen: "catalog" });
  return json(404, {});
};
function json(status, body) {
  return { status, async text() { return JSON.stringify(body); } };
}
function navLabels() { return navEl.children.map((a) => a.text); }

const shell = await import("./shell.js");
await shell.started;
check("catalog-item-shows-with-permission", navLabels().includes("Catalog"), navLabels().join(","));

screens = ["create_location"];
loc.hash = "#/home";
await shell.started;
check("catalog-item-hidden-without-permission", !navLabels().includes("Catalog"), navLabels().join(","));
loc.hash = "#/catalog";
await shell.started;
const heading = main.children.map((n) => n.text).join(" ");
check("catalog-screen-refused-without-permission", heading.includes("Access denied"), heading);

if (!process.argv.includes("--child")) {
  const nav = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--catalog-nav-check"], { cwd: repo, encoding: "utf8" });
  const navOut = (nav.stdout || "") + (nav.stderr || "");
  check("catalog-nav-server-with-permission", navOut.includes("PASS catalog-nav-with-permission"), navOut.trim().split("\n").slice(-6).join(" | "));
  check("catalog-nav-server-hidden-without-permission", navOut.includes("PASS catalog-nav-hidden-without-permission"), navOut.trim().split("\n").slice(-6).join(" | "));

  const read = spawnSync("dotnet", ["run", "--project", "src/Catalog.Query.Checks/Catalog.Query.Checks.csproj", "--no-restore", "--", "--catalog-read-check"], { cwd: repo, encoding: "utf8" });
  const readOut = (read.stdout || "") + (read.stderr || "");
  check("catalog-api-403-without-permission", readOut.includes("PASS catalog-api-403-without-permission"), readOut.trim().split("\n").slice(-8).join(" | "));

  const navFile = join(repo, "src/Admin.Api/CatalogNav.cs");
  const navSrc = readFileSync(navFile, "utf8");
  const gated = navSrc.replace(
    "CatalogReadAuthorization.MayRead(AssignmentPrincipal.TryFromGrant(userId, grant));",
    "grant.OrgCreateEdit;",
  );
  check("create-location-mutant-applied", gated !== navSrc);
  writeFileSync(navFile, gated);
  const navMut = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--catalog-nav-check"], { cwd: repo, encoding: "utf8" });
  writeFileSync(navFile, navSrc);
  const navMutOut = (navMut.stdout || "") + (navMut.stderr || "");
  check("mutant-catalog-gated-on-create-location-killed", navMutOut.includes("FAIL catalog-nav-hidden-without-permission"), navMutOut.trim().split("\n").slice(-6).join(" | "));

  const gateFile = join(repo, "src/Catalog.Contracts/CatalogReadAuthorization.cs");
  const gateSrc = readFileSync(gateFile, "utf8");
  const always = gateSrc.replace("actor is { IsManagementRole: true };", "true;");
  check("true-mutant-applied", always !== gateSrc);
  writeFileSync(gateFile, always);
  const readMut = spawnSync("dotnet", ["run", "--project", "src/Catalog.Query.Checks/Catalog.Query.Checks.csproj", "--no-restore", "--", "--catalog-read-check"], { cwd: repo, encoding: "utf8" });
  writeFileSync(gateFile, gateSrc);
  const readMutOut = (readMut.stdout || "") + (readMut.stderr || "");
  check("mutant-catalog-gated-on-true-killed", readMutOut.includes("FAIL catalog-api-403-without-permission"), readMutOut.trim().split("\n").slice(-8).join(" | "));
}

if (failed !== 0) process.exit(1);
