// F-09. Admin sign-out ends at /signin. The shell then uses the existing admin handoff.
// F-TL-B / DEC-220. Authorize for admin_dev_shell with no admin session 302s to
// issuerPath + "/admin?ret=" + escaped ret (path-relative; config issuer only).
// Kill evidence for handoff mutants lives in THIS file's PASS/FAIL lines (not req064-mutants.py).
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

function handoffLines(out) {
  return out.trim().split("\n").filter((l) =>
    l.startsWith("PASS authorize-") || l.startsWith("FAIL authorize-"));
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

  const handoffOut = await runHandoffCheck();
  check("authorize-hands-off-to-admin-sign-in", handoffOut.includes("PASS authorize-hands-off-to-admin-sign-in"), handoffLines(handoffOut).join(" | "));
  check("authorize-handoff-ignores-forged-host", handoffOut.includes("PASS authorize-handoff-ignores-forged-host"), handoffLines(handoffOut).join(" | "));

  // Mutant 1: break path → /l/broken (killed by exact Location equality).
  const brokenMutant = src.replace(
    'var admin = issuerPath + "/admin?ret=" + Uri.EscapeDataString(ret);',
    'var admin = issuerPath + "/l/broken?ret=" + Uri.EscapeDataString(ret);',
  );
  check("admin-handoff-broken-path-mutant-applied", brokenMutant !== src);
  writeFileSync(file, brokenMutant);
  const brokenKilled = await runHandoffCheck();
  writeFileSync(file, src);
  check("mutant-admin-handoff-broken-path-killed", brokenKilled.includes("FAIL authorize-hands-off-to-admin-sign-in"), handoffLines(brokenKilled).join(" | "));

  // Mutant 2: build Location from request Host / X-Forwarded-* (killed by forged-host check).
  const forgedMutant = src.replace(
    'var admin = issuerPath + "/admin?ret=" + Uri.EscapeDataString(ret);',
    'var admin = c.Request.Scheme + "://" + c.Request.Host + issuerPath + "/admin?ret=" + Uri.EscapeDataString(ret);',
  );
  check("admin-handoff-forged-host-mutant-applied", forgedMutant !== src);
  writeFileSync(file, forgedMutant);
  const forgedKilled = await runHandoffCheck();
  writeFileSync(file, src);
  check("mutant-admin-handoff-forged-host-killed", forgedKilled.includes("FAIL authorize-handoff-ignores-forged-host"), handoffLines(forgedKilled).join(" | "));

  const mutated = src.replace('adminSession ? "/signin"', 'adminSession ? issuerPath + "/admin"');
  check("direct-redirect-mutant-applied", mutated !== src);
  writeFileSync(file, mutated);
  const killed = runSignout();
  writeFileSync(file, src);
  check("mutant-admin-signout-redirects-directly-killed", killed.includes("FAIL admin-sign-out-goes-to-signin"), killed.trim().split("\n").slice(-8).join(" | "));
}

if (failed !== 0) process.exit(1);
