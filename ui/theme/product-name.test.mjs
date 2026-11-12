// Visible product name is Branding:ProductName. The config default lives only in appsettings.json.
import { readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "../..");
const banned = ["Cash" + " Advance", "Floor" + "Funds"];
let failed = 0;
function check(name, ok, detail = "") {
  if (ok) console.log("PASS " + name);
  else { failed++; console.log("FAIL " + name + (detail ? " " + detail : "")); }
}

function walk(dir, out) {
  for (const name of readdirSync(dir)) {
    if (name === "node_modules" || name === "bin" || name === "obj") continue;
    const path = join(dir, name);
    const st = statSync(path);
    if (st.isDirectory()) walk(path, out);
    else if (/\.(html|js|mjs|css|cs)$/.test(name)) out.push(path);
  }
}

const files = [];
walk(join(root, "ui"), files);
walk(join(root, "src/Login.StandIn"), files);
const hits = [];
for (const path of files) {
  if (path.endsWith("product-name.test.mjs") || path.endsWith("restyle.test.mjs")) continue;
  const text = readFileSync(path, "utf8")
    .replace(/\/\*[\s\S]*?\*\//g, "")
    .replace(/(^|[^:])\/\/.*$/gm, "$1");
  for (const word of banned) {
    if (text.includes(word)) hits.push(path.slice(root.length + 1) + " " + word);
  }
}
const settings = readFileSync(join(root, "src/Admin.Api/appsettings.json"), "utf8");
check("default-name-is-only-in-appsettings", settings.includes('"ProductName": "FloorFunds"') && hits.length === 0, hits.join("; "));
check("expiry-copy-uses-the-product-slot", readFileSync(join(root, "ui/login-hub/resign-in.html"), "utf8").includes("For security, {{product}} asks for your sign-in again."));
check("sign-out-copy-uses-the-product-name", readFileSync(join(root, "src/Login.StandIn/OAuth.cs"), "utf8").includes("Sign out of {System.Net.WebUtility.HtmlEncode(Ui.ProductName)}?"));
check("sign-in-title-uses-the-product-name", readFileSync(join(root, "src/Login.StandIn/SignIn.cs"), "utf8").includes('"Sign in to " + Ui.ProductName'));
check("location-label-is-location", readFileSync(join(root, "ui/admin-shell/shell.js"), "utf8").includes('setAttribute("aria-label", "Location")'));
check("header-sign-out", readFileSync(join(root, "ui/admin-shell/index.html"), "utf8").includes(">Sign out<"));

if (process.argv.includes("--child")) process.exit(failed === 0 ? 0 : 1);
const resign = join(root, "ui/login-hub/resign-in.html");
const original = readFileSync(resign);
writeFileSync(resign, original.toString("utf8").replace("{{product}}", "FloorFunds"));
const again = spawnSync(process.execPath, [fileURLToPath(import.meta.url), "--child"], { encoding: "utf8" });
writeFileSync(resign, original);
check("mutant-hardcoded-name-killed", again.stdout.includes("FAIL default-name-is-only-in-appsettings"), again.stdout);
if (failed) process.exit(1);
