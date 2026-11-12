// F-TL-B / DEC-220. session_expired goes to /signin?reason=expired; unknown reason
// renders nothing; a mutant that drops the sentence is killed.
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const shellSrc = readFileSync(join(repo, "ui/admin-shell/shell.js"), "utf8");
const serverSrc = readFileSync(join(repo, "ui/admin-shell/server.mjs"), "utf8");
const handoff = readFileSync(join(repo, "src/Login.StandIn/Handoff.cs"), "utf8");
const oauth = readFileSync(join(repo, "src/Login.StandIn/OAuth.cs"), "utf8");

check("shell-assigns-expired-reason", shellSrc.includes('"/signin?reason=expired"') && shellSrc.includes('body.error === "session_expired"'));
check("server-passes-ff-reason", serverSrc.includes('q.set("ff_reason", "expired")'));
check("oauth-preserves-reason-on-admin", oauth.includes('Q("ff_reason") == "expired"') && oauth.includes("&reason=expired"));
check("handoff-renders-exact-sentence",
  handoff.includes('For security, {E(Ui.ProductName)} asks for your sign-in again.')
  && handoff.includes('reason == "expired"'));
check("handoff-ignores-unknown-reason", handoff.includes('raw == "expired" ? "expired" : null'));

// Runtime shell redirect
const calls = [];
function node() {
  return {
    text: "", children: [], className: "", href: "",
    set textContent(v) { this.text = String(v); },
    get textContent() { return this.text; },
    replaceChildren(...xs) { this.children = xs.flat(); },
    append(...xs) { this.children.push(...xs.flat()); },
    setAttribute() {}, addEventListener() {}, querySelector() { return null; },
  };
}
const main = node();
const navEl = node();
globalThis.document = {
  querySelector(sel) { return sel === "#main" ? main : sel === "#nav" ? navEl : null; },
  createElement() { return node(); },
};
globalThis.location = { hash: "#/home", went: "", assign(url) { this.went = url; } };
globalThis.window = { addEventListener() {} };
globalThis.fetch = async (url) => {
  calls.push(String(url));
  if (url === "/api/nav") return { status: 401, async text() { return JSON.stringify({ error: "session_expired" }); } };
  return { status: 200, async text() { return "{}"; } };
};
const shell = await import(pathToFileURL(join(repo, "ui/admin-shell/shell.js")).href + "?exp=" + Date.now());
await shell.started;
check("expired-session-goes-to-signin-reason-expired", location.went === "/signin?reason=expired", `went=${location.went}`);

location.went = "";
globalThis.fetch = async () => ({ status: 401, async text() { return JSON.stringify({ error: "session_denied" }); } });
await shell.api("/api/nav");
check("non-expired-401-goes-to-signin-without-reason", location.went === "/signin", `went=${location.went}`);

// Mutant: drop the expiry sentence from Handoff.
const file = join(repo, "src/Login.StandIn/Handoff.cs");
const src = readFileSync(file, "utf8");
const mutated = src.replace(
  'reason == "expired"\n                ? $"""<p class="card__subtitle" data-testid="session-expired-sentence">For security, {E(Ui.ProductName)} asks for your sign-in again.</p>"""\n                : "";',
  'reason == "expired"\n                ? ""\n                : "";');
check("expiry-sentence-mutant-applied", mutated !== src);
writeFileSync(file, mutated);
const still = readFileSync(file, "utf8");
const killed = !still.includes("asks for your sign-in again");
writeFileSync(file, src);
check("mutant-drops-expiry-sentence-killed", killed);

if (failed !== 0) process.exit(1);
