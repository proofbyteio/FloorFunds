// The shell reopens an expired session once. A failed reopen goes to /signin
// and does not draw the assignment or screen banners.
const calls = [];
let nav = 0;
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
  if (url === "/api/session") return json(200, { error: "ok" });
  if (url === "/api/nav") {
    nav += 1;
    if (nav === 1) return json(401, { error: "session_expired" });
    return json(200, { screens: ["create_location", "config"] });
  }
  if (String(url).startsWith("/api/orgs/locations")) return json(200, { locations: [] });
  return json(200, {});
};

function json(status, body) {
  return { status, ok: status >= 200 && status < 300, async text() { return JSON.stringify(body); } };
}

const shell = await import("./shell.js");
await shell.started;

const sessionCalls = calls.filter((url) => url === "/api/session").length;
const navCalls = calls.filter((url) => url === "/api/nav").length;
const banned = texts.some((t) => t === "No assignment" || t === "This screen is not available");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}
check("shell-reopens-an-expired-session-once", sessionCalls === 1 && navCalls === 2 && location.went === "" && !banned,
  `session=${sessionCalls} nav=${navCalls} went=${location.went} banned=${banned}`);

let second = 0;
globalThis.fetch = async (url) => {
  if (url === "/api/session") { second += 1; return json(401, { error: "session_expired" }); }
  return json(401, { error: "session_expired" });
};
await shell.api("/api/nav");
check("shell-failed-reopen-goes-to-signin", second === 1 && location.went === "/signin",
  `session=${second} went=${location.went}`);

if (failed !== 0) process.exit(1);
