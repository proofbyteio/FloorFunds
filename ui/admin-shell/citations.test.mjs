// B3. Each re-derived file:line still contains the quoted symbol.
import { readFileSync, writeFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const here = fileURLToPath(import.meta.url);
const cites = [
  ["db/kit/definer-owner.sql", 54, "REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz)"],
  ["db/kit/definer-owner.sql", 58, "REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE auth.login_cutoff"],
  ["db/kit/definer-owner.sql", 78, "CREATE POLICY login_cutoff_owner"],
  ["db/kit/definer-owner.sql", 81, "GRANT EXECUTE ON FUNCTION auth.resolve_grant(text, uuid)"],
  ["db/kit/definer-owner.sql", 34, "auth._last_sa_guard()"],
  ["db/kit/definer-owner.sql", 36, "auth.location_partner(uuid)"],
  ["src/Auth.Data/Migrations/Req064Session.sql", 407, "auth.open_session(text, text, uuid, timestamptz)"],
  ["src/Auth.Data/Migrations/Req064SessionClose.sql", 279, "auth.open_session(text, text, uuid, timestamptz, timestamptz)"],
  ["src/Auth.Data/Migrations/Req064SessionClose.sql", 92, "auth._lock_login"],
  ["src/Auth.Data/Migrations/Req064SessionClose.sql", 107, "p_iat <= c.revoked_before"],
  ["src/Auth.Data/Migrations/Req064SessionClose.sql", 269, "REVOKE TRUNCATE ON TABLE auth.login_cutoff"],
  ["src/Auth.Data/Migrations/Req064SessionClose.sql", 281, "auth.session_still_open"],
  ["db/migrations.sql", 4398, "auth.resolve_grant"],
];

let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

for (const [file, line, symbol] of cites) {
  const rows = readFileSync(join(repo, file), "utf8").split("\n");
  const text = rows[line - 1] ?? "";
  check("cite-" + file.split("/").pop() + "-" + line, text.includes(symbol), text.trim().slice(0, 140));
}

if (!process.argv.includes("--child")) {
  const src = readFileSync(here, "utf8");
  const mutated = src.replace(
    '["db/kit/definer-owner.sql", 54, "REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz)"]',
    '["db/kit/definer-owner.sql", 55, "REVOKE ALL ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz)"]',
  );
  check("cite-shift-mutant-applied", mutated !== src);
  writeFileSync(here, mutated);
  const child = spawnSync(process.execPath, [here, "--child"], { cwd: repo, encoding: "utf8" });
  writeFileSync(here, src);
  const out = (child.stdout || "") + (child.stderr || "");
  check("mutant-cite-shifted-by-one-line-killed", out.includes("FAIL cite-definer-owner.sql-55"), out.trim().split("\n").filter((l) => l.startsWith("FAIL")).join(" | "));
}

if (failed !== 0) process.exit(1);
