// A closed session goes to /signin. The shell does not call /api/session and
// does not draw the assignment or screen banners.
const calls = [];
const texts = [];

function node() {
  const el = {
    text: "",
    children: [],
    className: "",
    href: "",
    set textContent(v) { this.text = String(v); texts.push(this.text); },
    get textContent() { return this.text; },
    replaceChildren(...xs) { this.children = xs.flat(); },
    append(...xs) { this.children.push(...xs.flat()); },
    setAttribute() {},
    addEventListener() {},
    querySelector() { return null; },
  };
  return el;
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
  if (url === "/api/nav") return json(401, { error: "session_expired" });
  return json(200, {});
};

function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import("./shell.js");
await shell.started;

const sessionCalls = calls.filter((url) => url === "/api/session").length;
const banned = texts.some((t) => t === "No assignment" || t === "This screen is not available");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}
check("shell-goes-to-signin-on-closed-session", sessionCalls === 0 && location.went === "/signin" && !banned,
  `session=${sessionCalls} went=${location.went} banned=${banned}`);

location.went = "";
globalThis.fetch = async () => json(401, { error: "session_denied" });
await shell.api("/api/nav");
check("shell-goes-to-signin-on-any-401", location.went === "/signin", `went=${location.went}`);

location.went = "";
globalThis.fetch = async () => json(403, {});
const denied = await shell.api("/api/nav");
check("shell-keeps-a-403", denied.status === 403 && location.went === "", `status=${denied.status} went=${location.went}`);

if (failed !== 0) process.exit(1);
