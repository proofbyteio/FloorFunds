// F-09. Admin sign-out ends at /signin. The shell then uses the existing admin handoff.
// F-TL-B / DEC-220. Authorize for admin_dev_shell with no admin session 302s to /admin?ret=.
import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:net";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const signin = readFileSync(join(repo, "ui/admin-shell/server.mjs"), "utf8");
check("shell-signin-hands-off-to-authorize", signin.includes('url.pathname === "/signin"') && signin.includes('location: "/standin/connect/authorize?"'));

function freePortBase() {
  // Need base, base+1, base+2 free. Probe base only; StandIn binds three consecutive ports.
  return new Promise((resolve, reject) => {
    const s = createServer();
    s.listen(0, "127.0.0.1", () => {
      const { port } = s.address();
      s.close((err) => err ? reject(err) : resolve(port > 65532 ? 19000 : port));
    });
    s.on("error", reject);
  });
}

function runHandoffCheck() {
  return (async () => {
    const base = await freePortBase();
    const stateDir = `/tmp/ff-db/admin-handoff-${base}`;
    const allowPath = `/tmp/ff-db/admin-handoff-allow-${base}.json`;
    mkdirSync(stateDir, { recursive: true });
    mkdirSync("/tmp/ff-db", { recursive: true });
    writeFileSync(allowPath, JSON.stringify({ entries: [{ value: "127.0.0.1/32" }, { value: "::1/128" }] }));
    const env = {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      STANDIN_ALLOW: "1",
      ADMIN_DEV_SHELL: "1",
      ADMIN_DEV_SHELL_REDIRECT: `http://127.0.0.1:${base + 10}/signin-oidc`,
      STANDIN_PORT_BASE: String(base),
      STANDIN_STATE_DIR: stateDir,
      StandIn__AllowlistJson: allowPath,
      StandIn__Issuer: `http://127.0.0.1:${base}/standin`,
    };
    const r = spawnSync("dotnet", [
      "run", "--project", "src/Login.StandIn/Login.StandIn.csproj",
      "--no-restore", "-p:IncludeStandIn=true", "--", "--admin-handoff-check",
    ], { cwd: repo, encoding: "utf8", env, timeout: 120000 });
    return (r.stdout || "") + (r.stderr || "");
  })();
}

if (!process.argv.includes("--child")) {
  const runSignout = () => {
    const r = spawnSync("dotnet", ["run", "--project", "src/Login.StandIn/Login.StandIn.csproj", "--no-restore", "-p:IncludeStandIn=true", "--", "--admin-signout-check"], { cwd: repo, encoding: "utf8" });
    return (r.stdout || "") + (r.stderr || "");
  };
  const goodSignout = runSignout();
  check("admin-sign-out-goes-to-signin", goodSignout.includes("PASS admin-sign-out-goes-to-signin"), goodSignout.trim().split("\n").slice(-8).join(" | "));

  const file = join(repo, "src/Login.StandIn/OAuth.cs");
  const src = readFileSync(file, "utf8");

  // Behavioral authorize handoff (replaces brittle source grep of Redirect(admin)).
  const handoffOut = await runHandoffCheck();
  check("authorize-hands-off-to-admin-sign-in", handoffOut.includes("PASS authorize-hands-off-to-admin-sign-in"), handoffOut.trim().split("\n").filter((l) => l.startsWith("FAIL") || l.startsWith("PASS authorize")).join(" | "));

  // Mutant: break the admin handoff Location so the behavioral check fails.
  const handoffMutated = src.replace(
    'var admin = issuer.TrimEnd(\'/\') + "/admin?ret=" + Uri.EscapeDataString(ret);',
    'var admin = issuer.TrimEnd(\'/\') + "/l/broken?ret=" + Uri.EscapeDataString(ret);',
  );
  check("admin-handoff-mutant-applied", handoffMutated !== src);
  writeFileSync(file, handoffMutated);
  const handoffKilled = await runHandoffCheck();
  writeFileSync(file, src);
  check("mutant-admin-handoff-redirect-killed", handoffKilled.includes("FAIL authorize-hands-off-to-admin-sign-in"), handoffKilled.trim().split("\n").filter((l) => l.startsWith("FAIL") || l.startsWith("PASS authorize")).join(" | "));

  const mutated = src.replace('adminSession ? "/signin"', 'adminSession ? issuerPath + "/admin"');
  check("direct-redirect-mutant-applied", mutated !== src);
  writeFileSync(file, mutated);
  const killed = runSignout();
  writeFileSync(file, src);
  check("mutant-admin-signout-redirects-directly-killed", killed.includes("FAIL admin-sign-out-goes-to-signin"), killed.trim().split("\n").slice(-8).join(" | "));
}

if (failed !== 0) process.exit(1);
