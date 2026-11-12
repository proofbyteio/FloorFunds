// F-24 / PQ-313. Client and server count Location name as Unicode scalars.
// The 200 limit is the existing validator proposal pending PQ-313.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const shellFile = join(repo, "ui/admin-shell/shell.js");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const emoji = "😀"; // one Unicode scalar, two UTF-16 code units
const atLimit = emoji.repeat(200);
const over = emoji.repeat(201);
check("utf16-length-differs-from-scalars", atLimit.length === 400 && [...atLimit].length === 200);

function el() {
  return {
    text: "", children: [],
    set textContent(v) { this.text = String(v); },
    get textContent() { return this.text; },
    replaceChildren() {}, append() {}, setAttribute() {}, removeAttribute() {},
    addEventListener() {}, querySelector() { return null; },
  };
}
globalThis.document = {
  querySelector() { return el(); },
  createElement() { return el(); },
};
globalThis.location = { hash: "#/home", pathname: "/", assign() {} };
globalThis.window = { addEventListener() {} };
globalThis.fetch = async () => ({ status: 200, async text() { return JSON.stringify({ screens: [] }); } });

const shell = await import(pathToFileURL(shellFile).href + "?scalars=" + Date.now());
await shell.started.catch(() => {});
check("client-accepts-200-astral-scalars", shell.locationNameClientError(atLimit) === "");
check("client-rejects-201-astral-scalars", shell.locationNameClientError(over) === "name_invalid");

const probe = spawnSync("dotnet", ["run", "--project", "src/Admin.Api/Admin.Api.csproj", "--no-restore", "--", "--name-required-check"], {
  cwd: repo, encoding: "utf8",
});
const out = (probe.stdout || "") + (probe.stderr || "");
check("server-accepts-200-astral-scalars", out.includes("PASS server-accepts-200-astral-scalars"), out.trim().split("\n").slice(-8).join(" | "));
check("server-rejects-201-astral-scalars", out.includes("PASS server-rejects-201-astral-scalars"), out.trim().split("\n").slice(-8).join(" | "));

// Mutant: revert client to UTF-16 .length counting.
const src = readFileSync(shellFile, "utf8");
const mutated = src.replace("if ([...name].length > 200) return \"name_invalid\";", "if (name.length > 200) return \"name_invalid\";");
check("utf16-mutant-applied", mutated !== src);
writeFileSync(shellFile, mutated);
const mut = await import(pathToFileURL(shellFile).href + "?mut=" + Date.now());
await mut.started.catch(() => {});
const killed = mut.locationNameClientError(atLimit) === "name_invalid"
  && [...atLimit].length === 200
  && atLimit.length === 400;
writeFileSync(shellFile, src);
check("mutant-client-utf16-length-killed", killed, `err=${mut.locationNameClientError(atLimit)}`);

if (failed !== 0) process.exit(1);
