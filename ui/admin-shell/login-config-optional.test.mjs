// F-04. login_config is optional on create. A location without it cannot be signed into.
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
function run(project, arg) {
  const args = ["run", "--project", project, "--no-restore"];
  if (project.includes("Login.StandIn")) args.push("-p:IncludeStandIn=true");
  args.push("--", arg);
  const r = spawnSync("dotnet", args, { cwd: repo, encoding: "utf8" });
  return (r.stdout || "") + (r.stderr || "");
}

if (!process.argv.includes("--child")) {
  const created = run("src/Admin.Api/Admin.Api.csproj", "--name-required-check");
  check("create-allows-a-location-without-login-config", created.includes("PASS create-allows-a-location-without-login-config"), created.trim().split("\n").slice(-6).join(" | "));
  const sign = run("src/Login.StandIn/Login.StandIn.csproj", "--location-signin-check");
  check("location-without-login-config-cannot-sign-in", sign.includes("PASS location-without-login-config-cannot-sign-in"), sign.trim().split("\n").slice(-8).join(" | "));

  const gate = join(repo, "src/Login.StandIn/LocationSignIn.cs");
  const src = readFileSync(gate, "utf8");
  const mutated = src.replace("=> loginConfigPresent;", "=> true;");
  check("sign-in-mutant-applied", mutated !== src);
  writeFileSync(gate, mutated);
  const killed = run("src/Login.StandIn/Login.StandIn.csproj", "--location-signin-check");
  writeFileSync(gate, src);
  check("mutant-location-without-login-config-can-sign-in-killed", killed.includes("FAIL location-without-login-config-cannot-sign-in"), killed.trim().split("\n").slice(-8).join(" | "));
}

if (failed !== 0) process.exit(1);
