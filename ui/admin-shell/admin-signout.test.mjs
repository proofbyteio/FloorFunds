// F-09. Admin sign-out ends at /signin. The shell then uses the existing admin handoff.
import { spawnSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
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
const oauth = readFileSync(join(repo, "src/Login.StandIn/OAuth.cs"), "utf8");
check("authorize-hands-off-to-admin-sign-in", oauth.includes('issuerPath + "/admin?ret="') && oauth.includes("return Results.Redirect(admin)"));

if (!process.argv.includes("--child")) {
  const run = () => {
    const r = spawnSync("dotnet", ["run", "--project", "src/Login.StandIn/Login.StandIn.csproj", "--no-restore", "-p:IncludeStandIn=true", "--", "--admin-signout-check"], { cwd: repo, encoding: "utf8" });
    return (r.stdout || "") + (r.stderr || "");
  };
  const good = run();
  check("admin-sign-out-goes-to-signin", good.includes("PASS admin-sign-out-goes-to-signin"), good.trim().split("\n").slice(-8).join(" | "));

  const file = join(repo, "src/Login.StandIn/OAuth.cs");
  const src = readFileSync(file, "utf8");
  const mutated = src.replace('adminSession ? "/signin"', 'adminSession ? issuerPath + "/admin"');
  check("direct-redirect-mutant-applied", mutated !== src);
  writeFileSync(file, mutated);
  const killed = run();
  writeFileSync(file, src);
  check("mutant-admin-signout-redirects-directly-killed", killed.includes("FAIL admin-sign-out-goes-to-signin"), killed.trim().split("\n").slice(-8).join(" | "));
}

if (failed !== 0) process.exit(1);
