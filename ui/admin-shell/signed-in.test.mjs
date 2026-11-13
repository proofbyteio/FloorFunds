// F-08. The header uses signed_in_as and role from /api/nav. It does not guess.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const shellFile = join(dirname(fileURLToPath(import.meta.url)), "shell.js");
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
const header = el("p");
globalThis.document = {
  querySelector(sel) {
    if (sel === "#main" || sel === "#nav") return el(sel);
    if (sel === "#signed-in") return header;
    return el("p");
  },
  createElement: el,
};
globalThis.location = { hash: "#/home", assign() {} };
globalThis.window = { addEventListener() {} };
globalThis.fetch = async (url) => {
  if (String(url) === "/api/nav") {
    return { status: 200, async text() { return JSON.stringify({ screens: ["create_location"], product_name: "Northwind", signed_in_as: "cashier_db", role: "Partner Admin" }); } };
  }
  return { status: 404, async text() { return "{}"; } };
};

const shell = await import("./shell.js");
await shell.started;
check("signed-in-uses-the-nav-payload", header.text === "Signed in as cashier_db · Role: Partner Admin", header.text);

if (!process.argv.includes("--child")) {
  const role = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--signed-in-check"], { cwd: repo, encoding: "utf8" });
  const roleOut = (role.stdout || "") + (role.stderr || "");
  check("nav-role-is-the-resolved-display-name", roleOut.includes("PASS nav-role-is-the-resolved-display-name"), roleOut.trim().split("\n").slice(-6).join(" | "));

  const src = readFileSync(shellFile, "utf8");
  const needle = 'signedIn.textContent = who && role ? "Signed in as " + who + " · Role: " + role : "";';
  const mutated = src.replace(needle, 'signedIn.textContent = "Signed in as EXAMPLE · Role: Super Admin";');
  check("hardcoded-text-mutant-applied", mutated !== src);
  writeFileSync(shellFile, mutated);
  const child = spawnSync(process.execPath, [fileURLToPath(import.meta.url), "--child"], { cwd: repo, encoding: "utf8" });
  writeFileSync(shellFile, src);
  const childOut = (child.stdout || "") + (child.stderr || "");
  check("mutant-hardcoded-signed-in-killed", childOut.includes("FAIL signed-in-uses-the-nav-payload"), childOut.trim().split("\n").slice(-6).join(" | "));
}

if (failed !== 0) process.exit(1);
