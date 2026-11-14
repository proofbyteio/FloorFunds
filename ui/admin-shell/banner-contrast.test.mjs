// Soft banners use light fills; dark-theme --foreground must not paint their text.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)));
const themes = [
  join(root, "theme.css"),
  join(root, "..", "orgs", "theme.css"),
  join(root, "..", "login-hub", "theme.css"),
];
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

for (const file of themes) {
  const css = readFileSync(file, "utf8");
  const label = file.replace(/\\/g, "/").split("/ui/")[1] || file;
  check(label + "-sets-banner-body-default", /\.banner\s*\{[^}]*color:\s*var\(--banner-body\)/.test(css));
  check(label + "-info-uses-info-banner-text", /\.banner--info\s*\{[^}]*color:\s*var\(--info-banner-text\)/.test(css));
  check(label + "-error-uses-error-banner-text", /\.banner--error\s*\{[^}]*color:\s*var\(--error-banner-text\)/.test(css));
  check(label + "-warning-uses-warning-banner-text", /\.banner--warning\s*\{[^}]*color:\s*var\(--warning-banner-text\)/.test(css));
  check(label + "-success-uses-success-banner-text", /\.banner--success\s*\{[^}]*color:\s*var\(--success-banner-text\)/.test(css));
}

const shell = readFileSync(join(root, "shell.js"), "utf8");
check("shell-banner-title-class", /heading\.className\s*=\s*"banner__title"/.test(shell) || /className = "banner__title"/.test(shell));

if (failed !== 0) process.exit(1);
