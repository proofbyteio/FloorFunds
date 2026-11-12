// Pins the country-rules file and the compact rows digest the server validator loads.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const file = join(dirname(fileURLToPath(import.meta.url)), "country-rules.json");
const bytes = readFileSync(file);
const doc = JSON.parse(bytes.toString("utf8"));
const rowsCompact = JSON.stringify(doc.rows);
const fileSha = createHash("sha256").update(bytes).digest("hex");
const rowsSha = createHash("sha256").update(rowsCompact).digest("hex");

const FILE_SHA256 = "cc95aa68a103b97fcfe881064e77af278921aee842c66dda7abc2c22e0210f0d";
const ROWS_SHA256 = "256bb1e0d42e36e9518bc7c71cd9d0df924e047149b7dffd43f94a651a5a40bb";

let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

check("country-rules-file-sha256", fileSha === FILE_SHA256, fileSha);
check("country-rules-rows-sha256", rowsSha === ROWS_SHA256 && doc.rows_sha256 === ROWS_SHA256, rowsSha);

const byCountry = new Map(doc.rows.map((row) => [row.country, row]));
check("country-rules-ae-state-required-postal-not-used", byCountry.get("AE")?.state === "required" && byCountry.get("AE")?.postal === "not_used");
check("country-rules-ac-state-not-used", byCountry.get("AC")?.state === "not_used");
check("country-rules-values-are-required-optional-or-not-used", doc.rows.every((row) =>
  ["required", "optional", "not_used"].includes(row.state) && ["required", "optional", "not_used"].includes(row.postal)));

if (failed !== 0) process.exit(1);
