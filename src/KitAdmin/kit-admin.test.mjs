// KitAdmin guards. Each mutant is applied, the check is run, the mutant is restored.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const kit = join(root, "src", "KitAdmin", "KitAdmin.cs");
const suite = join(root, "src", "Admin.Api.Checks", "Req064Suite.cs");
const project = join(root, "src", "Admin.Api.Checks", "Admin.Api.Checks.csproj");

function run(args, env) {
  const r = spawnSync("dotnet", ["run", "--project", project, "--no-restore", "--", ...args], {
    cwd: root,
    env: { ...process.env, ...env },
    encoding: "utf8",
  });
  return (r.stdout || "") + (r.stderr || "");
}

function check() {
  return run(["--kit-admin-check"]);
}

let failed = 0;
function expect(name, cond, detail) {
  if (cond) console.log("PASS " + name);
  else {
    failed++;
    console.log("FAIL " + name + (detail ? " :: " + detail.slice(0, 400) : ""));
  }
}

const seedRole = join(root, "db", "kit", "test-seed-role.sql");
const actorSql = join(root, "src", "Auth.Data", "Migrations", "Req064Actor.sql");
const originalKit = readFileSync(kit, "utf8");
const originalSuite = readFileSync(suite, "utf8");
const originalSeed = readFileSync(seedRole, "utf8");
const originalActor = readFileSync(actorSql, "utf8");
try {
  const base = check();
  expect("call-sites-match-before-mutants", base.includes("PASS kit-admin-call-sites-match-the-decisions-list") && !base.includes("FAIL "), base);
  expect("no-test-seed-update-before-mutants", base.includes("PASS no-test-code-updates-or-deletes-as-test-seed"), base);
  expect("no-catalog-insert-before-mutants", base.includes("PASS test-seed-has-no-catalog-insert"), base);
  expect("no-actor-bypass-before-mutants", base.includes("PASS actor-body-has-no-test-bypass"), base);
  expect("no-apply-execute-before-mutants", base.includes("PASS no-apply-execute-grant"), base);
  expect("no-replication-bypass-before-mutants", base.includes("PASS no-replication-role-or-disabled-trigger"), base);

  const extra = originalSuite.replace(
    "static class Req064Suite\n{",
    "static class Req064Suite\n{\n    static void ExtraKitAdminCall() { KitAdmin.Exec(\"extra-call-site\", \"SELECT 1\"); }\n",
  );
  writeFileSync(suite, extra);
  const extraOut = check();
  expect("mutant-extra-kit-admin-call-site-killed", extraOut.includes("FAIL kit-admin-call-not-on-the-list extra-call-site"), extraOut);
  writeFileSync(suite, originalSuite);

  const updated = originalSuite.replace(
    "static class Req064Suite\n{",
    "static class Req064Suite\n{\n    static Task SeedUpdateMutant(NpgsqlDataSource seed) => Exec(seed, \"UPDATE auth.app_user SET status = status\");\n",
  );
  writeFileSync(suite, updated);
  const updateOut = check();
  expect("mutant-test-seed-update-killed", updateOut.includes("FAIL test-seed-update-or-delete"), updateOut);
  writeFileSync(suite, originalSuite);

  const unguarded = originalKit.replace(
    "        // scratch-guard\n        if (!IsScratchDatabase(db))\n            throw new InvalidOperationException(\"kit admin refused: database is not a scratch database\");\n        // end-scratch-guard\n",
    "",
  );
  writeFileSync(kit, unguarded);
  const guardOut = run(["--kit-admin-guard"], { FF_KIT_MODE: "1", FF_KIT_DB: "postgres", FF_KIT_PORT: "5432" });
  expect("mutant-scratch-guard-removed-killed", guardOut.includes("ran") && !guardOut.includes("not a scratch database"), guardOut);
  writeFileSync(kit, originalKit);

  writeFileSync(seedRole, originalSeed + "\nGRANT INSERT ON TABLE catalog.registered_entry TO placeholder;\n");
  const insertOut = check();
  expect("test-seed-insert-registered-entry", insertOut.includes("FAIL test-seed-insert-registered-entry"), insertOut);
  writeFileSync(seedRole, originalSeed);

  writeFileSync(actorSql, originalActor.replace("BEGIN", "BEGIN\n  IF current_user = '_test_seed' THEN RETURN; END IF;"));
  const actorOut = check();
  expect("actor-test-bypass", actorOut.includes("FAIL actor-test-bypass"), actorOut);
  writeFileSync(actorSql, originalActor);

  // M-2 mutant: _actor trusts a resolved principal GUC.
  const gucMutant = originalActor.replace(
    "  actor_user_id := v_user;",
    "  actor_user_id := coalesce(nullif(current_setting('app.actor_user_id', true), '')::uuid, v_user);",
  );
  expect("principal-guc-mutant-applied", gucMutant !== originalActor);
  writeFileSync(actorSql, gucMutant);
  const gucOut = check();
  expect("mutant-actor-reads-resolved-guc-killed", gucOut.includes("FAIL principal-guc-current-setting"), gucOut);
  writeFileSync(actorSql, originalActor);

  // L-5 mutant: drop the test-seed-role install marker from setup.sh.
  const setupPath = join(root, "db", "setup.sh");
  const originalSetup = readFileSync(setupPath, "utf8");
  writeFileSync(setupPath, originalSetup.replace("test-seed-role.sql", "test-seed-role.MISSING.sql"));
  const setupOut2 = check();
  expect("mutant-production-layout-installs-test-seed-killed", setupOut2.includes("FAIL production-layout-omits-test-seed-install"), setupOut2);
  writeFileSync(setupPath, originalSetup);

  writeFileSync(suite, extra);
  const unlistedOut = check();
  expect("kitadmin-unlisted-callsite", unlistedOut.includes("kitadmin-unlisted-callsite"), unlistedOut);
  writeFileSync(suite, originalSuite);

  const applySql = join(root, "db", "kit", "test-seed-role.sql");
  writeFileSync(applySql, originalSeed + "\nGRANT EXECUTE ON FUNCTION catalog.apply_entry_create(uuid, text, text, uuid, text, boolean, jsonb, integer) TO placeholder;\n");
  const applyOut = check();
  expect("admin-or-seed-execute-apply", applyOut.includes("FAIL admin-or-seed-execute-apply"), applyOut);
  writeFileSync(applySql, originalSeed);

  const replicated = originalSuite.replace(
    "static class Req064Suite\n{",
    "static class Req064Suite\n{\n    static string ReplicationMutant() => \"SET session_replication_role = replica\";\n",
  );
  writeFileSync(suite, replicated);
  const replOut = check();
  expect("kitadmin-replication-role", replOut.includes("FAIL kitadmin-replication-role"), replOut);
  writeFileSync(suite, originalSuite);

  const restored = check();
  expect("call-sites-match-after-restore", restored.includes("PASS kit-admin-call-sites-match-the-decisions-list") && !restored.includes("FAIL "), restored);
  const refused = run(["--kit-admin-guard"], { FF_KIT_MODE: "1", FF_KIT_DB: "postgres", FF_KIT_PORT: "5432" });
  expect("scratch-guard-refuses-a-non-scratch-database", refused.includes("kit admin refused: database is not a scratch database"), refused);
  expect("kitadmin-nonscratch", refused.includes("kit admin refused: database is not a scratch database"), refused);
} finally {
  writeFileSync(kit, originalKit);
  writeFileSync(suite, originalSuite);
  writeFileSync(seedRole, originalSeed);
  writeFileSync(actorSql, originalActor);
}
process.exit(failed === 0 ? 0 : 1);
