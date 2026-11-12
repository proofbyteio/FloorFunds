// DEC-230–235 restyle checks. Visual rendering was not checked in a browser.
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { applyProductName, productNameFromConfig } from "../admin-shell/branding.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const ui = join(root, "ui");
const tokensPath = join(ui, "theme", "theme-tokens.css");
let failed = 0;

function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else {
    failed += 1;
    console.log("FAIL " + name + (detail ? " :: " + detail : ""));
  }
}

function read(rel) {
  return readFileSync(join(root, rel), "utf8");
}

function cssFiles() {
  return [
    "ui/theme/theme-tokens.css",
    "ui/admin-shell/theme.css",
    "ui/admin-shell/shell.css",
    "ui/login-hub/theme.css",
    "ui/orgs/theme.css",
    "ui/orgs/orgs.css",
    "ui/catalog-presentation/css/catalog.css",
  ];
}

function componentStyleFiles() {
  return cssFiles().filter((rel) => !rel.endsWith("theme-tokens.css"));
}

const colorRe = /#[0-9A-Fa-f]{3,8}\b|(?<![\w-])rgba?\(|(?<![\w-])hsla?\(/;
function colorLiteralOutsideTokens() {
  const hits = [];
  for (const rel of componentStyleFiles()) {
    read(rel).split("\n").forEach((line, i) => {
      if (colorRe.test(line)) hits.push(`${rel}:${i + 1}`);
    });
  }
  for (const rel of ["ui/admin-shell/index.html", "ui/login-hub/signin.html", "ui/login-hub/layout.html"]) {
    read(rel).split("\n").forEach((line, i) => {
      if (colorRe.test(line)) hits.push(`${rel}:${i + 1}`);
    });
  }
  return hits;
}

function remoteFontUrls() {
  const hits = [];
  const remote = /url\(\s*['"]?https?:|fonts\.googleapis|fonts\.gstatic/i;
  for (const rel of cssFiles().concat(["ui/admin-shell/index.html", "ui/login-hub/layout.html", "ui/login-hub/signin.html"])) {
    if (remote.test(read(rel))) hits.push(rel);
  }
  return hits;
}

function blockAfter(css, start) {
  const i = css.indexOf(start);
  if (i < 0) return "";
  const open = css.indexOf("{", i);
  let depth = 0;
  for (let j = open; j < css.length; j++) {
    if (css[j] === "{") depth += 1;
    else if (css[j] === "}") {
      depth -= 1;
      if (depth === 0) return css.slice(i, j + 1);
    }
  }
  return "";
}

const tokens = read("ui/theme/theme-tokens.css");
const phoneQuery = blockAfter(tokens, "@media not all and (min-width: 64rem)");
const reduced = blockAfter(tokens, "@media (prefers-reduced-motion: reduce)");
const outerBtn = blockAfter(tokens, ".btn-primary {");
const press = blockAfter(tokens, ".btn-primary__press {");
const focus = blockAfter(tokens, ":focus-visible {");

check("no-color-literal-outside-theme-tokens", colorLiteralOutsideTokens().length === 0, colorLiteralOutsideTokens().join(", "));
check("no-remote-font-url", remoteFontUrls().length === 0, remoteFontUrls().join(", "));

const fontFiles = {
  "ui/theme/fonts/InterVariable.woff2": "693b77d4f32ee9b8bfc995589b5fad5e99adf2832738661f5402f9978429a8e3",
  "ui/theme/fonts/InterVariable-Italic.woff2": "e564f652916db6c139570fefb9524a77c4d48f30c92928de9db19b6b5c7a262a",
  "ui/theme/fonts/LICENSE.txt": "262481e844521b326f5ecd053e59b98c8b2da78c8ee1bdbb6e8174305e54935a",
  "ui/theme/logo-1.svg": "117217d83459f9215cd379a759b7e2e2902111760c2f853de85d9f7ef235165e",
};
for (const [rel, want] of Object.entries(fontFiles)) {
  const got = createHash("sha256").update(readFileSync(join(root, rel))).digest("hex");
  check("sha256-" + rel.split("/").pop(), got === want, got);
}
check("inter-font-face-swap", tokens.includes('font-family: Inter') && tokens.includes("font-display: swap") && tokens.includes('url("fonts/InterVariable.woff2")') && tokens.includes('url("fonts/InterVariable-Italic.woff2")'));

const name = productNameFromConfig({ product_name: "TestBrandX" });
const el = { text: "", title: "", textContent: "", setAttribute(k, v) { this.title = v; }, removeAttribute() { this.title = ""; } };
Object.defineProperty(el, "textContent", { set(v) { this.text = String(v); }, get() { return this.text; } });
applyProductName(el, name);
check("lockup-reads-configured-product-name", el.textContent === "TestBrandX" && !el.textContent.includes("FloorFunds"), el.textContent);
const branding = read("ui/admin-shell/branding.mjs");
const shell = read("ui/admin-shell/shell.js");
const uiCs = read("src/Login.StandIn/Ui.cs");
check("lockup-source-has-no-hardcoded-name", !branding.includes("FloorFunds") && !shell.includes("FloorFunds") && !uiCs.includes("FloorFunds"));
const long = "TestBrandX".repeat(12);
applyProductName(el, long);
check("lockup-long-name-keeps-full-accessible-name", el.textContent === long && el.title === long && tokens.includes("text-overflow: ellipsis") && tokens.includes("white-space: nowrap") && tokens.includes("--lockup-name-max-width-sidebar: 160px") && tokens.includes("--lockup-name-max-width-signin: 240px"), el.textContent.length + "");

check("scale-only-below-64rem", phoneQuery.includes("scale: var(--cta-press-scale)") && !blockAfter(tokens, "@media (min-width: 64rem)").includes("scale:") && !outerBtn.includes("scale:"));
check("scale-absent-at-min-width-64rem", !tokens.includes("@media (min-width: 64rem)") && phoneQuery.startsWith("@media not all and (min-width: 64rem)"));
check("scale-absent-under-reduced-motion", reduced.includes("transition: none") && reduced.includes("scale: none") && reduced.includes(".btn-primary:active .btn-primary__press") && !reduced.includes("--cta-press-scale") && !reduced.includes("--cta-glow-hover: var(--cta-glow)"));
check("focus-ring-on-non-scaling-outer", focus.includes("outline: var(--focus-ring-width) solid var(--focus-ring)") && focus.includes("outline-offset: var(--focus-ring-offset)") && tokens.includes("--focus-ring-width: 2px") && outerBtn.includes("overflow: visible") && !outerBtn.includes("scale:") && press.includes("transform-origin: center") && phoneQuery.includes(".btn-primary:active .btn-primary__press"));
check("press-transition", press.includes("transition-duration: var(--cta-press-duration)") && tokens.includes("--cta-press-duration: 0.15s") && tokens.includes("--cta-press-easing: cubic-bezier(0.4, 0, 0.2, 1)") && press.includes("transition-property: scale, box-shadow"));

const signin = read("ui/login-hub/signin.html");
check("signin-press-is-inner", /class="button button--primary button--block btn-primary"[\s\S]*<span class="btn-primary__press">Sign in<\/span>/.test(signin) && signin.indexOf("btn-primary__press") < signin.indexOf("btn-primary-halo"));
const haloFiles = ["ui/login-hub/signin.html", "src/Login.StandIn/Handoff.cs"];
let haloOk = true;
const haloDetail = [];
for (const rel of ["ui/login-hub/signin.html", "ui/login-hub/totp.html", "ui/login-hub/resign-in.html", "ui/orgs/location-sign-in.html", "ui/admin-shell/shell.js", "src/Login.StandIn/Handoff.cs"]) {
  const text = read(rel);
  if (!text.includes("btn-primary-halo")) continue;
  if (!haloFiles.includes(rel)) { haloOk = false; haloDetail.push(rel); }
  if (!/signin-submit|admin-signin-submit/.test(text)) { haloOk = false; haloDetail.push(rel + ":no-submit"); }
}
check("halo-only-on-standalone-signin", haloOk && signin.includes("btn-primary-halo"), haloDetail.join(", "));
check("gradient-only-on-primary-press", tokens.includes(".btn-primary__press") && press.includes("background-image: var(--cta-gradient)") && !outerBtn.includes("--cta-gradient") && componentStyleFiles().every((rel) => !read(rel).includes("--cta-gradient") && !read(rel).includes("scale:")));

const appsettings = read("src/Admin.Api/appsettings.json");
check("product-name-config-key", appsettings.includes('"Branding"') && appsettings.includes('"ProductName"'));

if (process.argv.includes("--checks-only")) process.exit(failed === 0 ? 0 : 1);

function runChild() {
  return new Promise((resolve) => {
    import("node:child_process").then(({ spawnSync }) => {
      const r = spawnSync(process.execPath, [fileURLToPath(import.meta.url), "--checks-only"], { encoding: "utf8" });
      resolve(r.stdout + r.stderr);
    });
  });
}

const brandingPath = join(root, "ui/admin-shell/branding.mjs");
const brandingBytes = readFileSync(brandingPath);
const shellCssPath = join(root, "ui/admin-shell/shell.css");
const shellCssBytes = readFileSync(shellCssPath);
const tokenBytes = readFileSync(tokensPath);

try {
  writeFileSync(brandingPath, brandingBytes.toString("utf8").replace("nameEl.textContent = value;", 'nameEl.textContent = "FloorFunds";'));
  const out = await runChild();
  check("mutant-hardcoded-name-killed", out.includes("FAIL lockup-reads-configured-product-name"), out.split("\n").filter((l) => l.includes("lockup-reads")).join(" | "));
} finally {
  writeFileSync(brandingPath, brandingBytes);
}

try {
  writeFileSync(shellCssPath, shellCssBytes.toString("utf8") + "\n.mutant { color: #ff00ff; }\n");
  const out = await runChild();
  check("mutant-stray-hex-killed", out.includes("FAIL no-color-literal-outside-theme-tokens"));
} finally {
  writeFileSync(shellCssPath, shellCssBytes);
}

try {
  const text = tokenBytes.toString("utf8").replace("    scale: none;\n", "");
  writeFileSync(tokensPath, text);
  const out = await runChild();
  check("mutant-reduced-motion-guard-killed", out.includes("FAIL scale-absent-under-reduced-motion"));
} finally {
  writeFileSync(tokensPath, tokenBytes);
}

if (failed !== 0) process.exit(1);
