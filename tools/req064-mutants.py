#!/usr/bin/env python3
"""REQ-064 mutants. Each one fails, then passes after restore.

Every fail run and every pass run starts from a freshly dropped kit database.
Req064 / issuer / helpers use an unseeded DB (test-seed role only).
Auth.Data.Checks mutants use --seed so fixtures.json users are present.
"""
import os, re, subprocess, pathlib, textwrap

ROOT = pathlib.Path(os.environ.get("MUTANTS_ROOT", str(pathlib.Path(__file__).resolve().parents[1])))
OUT = pathlib.Path(os.environ.get("MUTANTS_OUT", "/opt/cursor/artifacts/mutants-run23"))
OUT.mkdir(parents=True, exist_ok=True)
DB = os.environ.get("MUTANTS_DB", "ff_kit_req064")
DEFINER = DB + "_auth_definer"
OWNER = DB
WARNINGS = []

def load_env():
    env = os.environ.copy()
    for path in ("/tmp/req064-env.sh", "/tmp/ff-db/ff_scratchadm.env"):
        for line in open(path):
            line = line.strip()
            if line.startswith("export "):
                line = line[len("export "):]
            if not line or line.startswith("#") or "=" not in line:
                continue
            k, v = line.split("=", 1)
            if len(v) >= 2 and v[0] == v[-1] and v[0] in "'\"":
                v = v[1:-1]
            env[k] = v
    return env

ENV = load_env()
ENV["PATH"] = "/home/box/.dotnet:" + ENV.get("PATH", "")
ENV["DOTNET_ROOT"] = "/home/box/.dotnet"
ENV["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
ENV["DOTNET_NOLOGO"] = "1"
def password_of(cs):
    for part in cs.split(";"):
        if part.startswith("Password="):
            return part.split("=", 1)[1]
    raise SystemExit("password missing")

ENV["FF_DB_PASSWORD"] = password_of(ENV["CONNECTION_STRING"])
ENV["FF_ADMIN_API_PASSWORD"] = password_of(ENV["ADMIN_API_CONNECTION_STRING"])
ENV["FF_LOGIN_PASSWORD"] = password_of(ENV["LOGIN_CONNECTION_STRING"])
pw = ENV.get("FF_ISSUER_PASSWORD", "")
ENV["ISSUER_CONNECTION_STRING"] = (
    "Host=127.0.0.1;Port=5432;Database=" + DB + ";Username=" + DB + "_issuer;Password=" + pw)

def sudo_psql(sql, file=False):
    cmd = ["sudo", "-u", "postgres", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", DB]
    if file:
        cmd += ["-f", sql]
        r = subprocess.run(cmd, capture_output=True, text=True)
    else:
        cmd += ["-At" if not sql.lstrip().upper().startswith("CREATE") and not sql.lstrip().upper().startswith("ALTER") and not sql.lstrip().upper().startswith("GRANT") and not sql.lstrip().upper().startswith("REVOKE") and "\n" not in sql.strip() else "-c"]
        # Always use -c for a single script via stdin to avoid -At on DDL.
        r = subprocess.run(
            ["sudo", "-u", "postgres", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", DB, "-f", "-"],
            input=sql, capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit("psql failed: " + (r.stderr or r.stdout)[:600])
    return r.stdout

def sudo_at(sql):
    r = subprocess.run(
        ["sudo", "-u", "postgres", "psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-d", DB, "-c", sql],
        capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit("psql -At failed: " + r.stderr[:400])
    return r.stdout.strip()

def git_checkout(*paths):
    subprocess.run(["git", "checkout", "--", *paths], cwd=ROOT, check=True)

def patch(path, old, new):
    file = ROOT / path
    text = file.read_text()
    if old not in text:
        raise SystemExit(f"pattern missing in {path}: {old[:100]!r}")
    file.write_text(text.replace(old, new, 1))

def run(name, cmd, build=None):
    if build:
        br = subprocess.run(build, cwd=ROOT, env=ENV, capture_output=True, text=True)
        (OUT / f"{name}-build.log").write_text(br.stdout + br.stderr)
        (OUT / f"{name}-build.rc").write_text(str(br.returncode) + "\n")
        if br.returncode != 0:
            (OUT / f"{name}.log").write_text("BUILD FAILED\n" + br.stdout + br.stderr)
            (OUT / f"{name}.rc").write_text("1\n")
            print(name, "BUILD FAIL")
            return 1
    r = subprocess.run(cmd, cwd=ROOT, env=ENV, capture_output=True, text=True)
    text = r.stdout + r.stderr
    (OUT / f"{name}.log").write_text(text)
    (OUT / f"{name}.rc").write_text(str(r.returncode) + "\n")
    fails = [ln for ln in text.splitlines() if ln.startswith("FAIL") or "passed" in ln or "ALL PASS" in ln or ln.startswith("RESULT")]
    print(name, "rc", r.returncode, " | ".join(fails[-4:]))
    return r.returncode

REQ = ["dotnet", "run", "--project", "src/Admin.Api.Checks/Admin.Api.Checks.csproj", "--no-build", "--", "--req064"]
REQ_BUILD = ["dotnet", "build", "src/Admin.Api.Checks/Admin.Api.Checks.csproj", "-v", "q", "--nologo"]
READ = ["dotnet", "run", "--project", "src/Catalog.Read.Checks/Catalog.Read.Checks.csproj", "--no-build"]
READ_BUILD = ["bash", "-c", "dotnet build src/Auth.Data.Migrator/Auth.Data.Migrator.csproj -v q --nologo && dotnet build src/Catalog.Read.Checks/Catalog.Read.Checks.csproj -v q --nologo"]
QUERY = ["dotnet", "run", "--project", "src/Catalog.Query.Checks/Catalog.Query.Checks.csproj", "--no-build"]
QUERY_BUILD = ["dotnet", "build", "src/Catalog.Query.Checks/Catalog.Query.Checks.csproj", "-v", "q", "--nologo"]

def fn_def(sig):
    return sudo_at(f"SELECT pg_get_functiondef('{sig}'::regprocedure)")

def replace_body(defn, new_body):
    mark = "$function$"
    start = defn.index("AS " + mark)
    end = defn.rindex(mark)
    return defn[:start] + "AS " + mark + "\n" + new_body + "\n" + mark + defn[end + len(mark):]

def install_fn(defn, sig, owner, grant_admin):
    sql = defn if defn.rstrip().endswith(";") else defn.rstrip() + ";"
    sql += f"\nALTER FUNCTION {sig} OWNER TO {owner};\n"
    sql += f"REVOKE ALL ON FUNCTION {sig} FROM PUBLIC;\n"
    if grant_admin:
        sql += f"GRANT EXECUTE ON FUNCTION {sig} TO {DB}_admin_api;\n"
    sudo_psql(sql)

def policy_using(name):
    return sudo_at(
        "SELECT pg_get_expr(polqual, polrelid) || ' || ' || coalesce(pg_get_expr(polwithcheck, polrelid), '') "
        f"FROM pg_policy WHERE polname = '{name}'")

def set_policy(table, name, using, check):
    if not using or not str(using).strip():
        raise SystemExit(f"empty USING for policy {name} on {table}")
    sql = f"ALTER POLICY {name} ON {table} USING ({using})"
    if check and str(check).strip():
        sql += f" WITH CHECK ({check})"
    sql += ";"
    sudo_psql(sql)

SAVED = {}

def remember(sig, grant):
    SAVED[sig] = (fn_def(sig), grant)

def replace_fn(sig, old, new):
    defn = fn_def(sig)
    if old not in defn:
        raise SystemExit(f"body missing in {sig}: {old[:80]!r}")
    sql = defn.replace(old, new, 1)
    if not sql.rstrip().endswith(";"):
        sql += ";"
    sudo_psql(sql)

def mutate_body(sig, old, new, grant):
    defn = fn_def(sig)
    if old not in defn:
        raise SystemExit(f"body missing in {sig}: {old[:80]!r}")
    owner = DEFINER if grant else OWNER
    install_fn(defn.replace(old, new, 1), sig, owner, grant)

def restore_fn(sig):
    defn, grant = SAVED[sig]
    owner = DEFINER if grant else OWNER
    install_fn(defn, sig, owner, grant)

def reseed(which, seeded=False):
    # Unseeded: role only. db/seed/*.sql inserts a global SA and req064 refuses.
    # Seeded: Auth.Data.Checks needs fixtures.json users (PUBLIC TEMP, scratch, etc.).
    args = ["bash", "db/setup.sh", "--db", DB, "--role", DB, "--force", "--drop"]
    if seeded:
        args.append("--seed")
    r = subprocess.run(args, cwd=ROOT, env=ENV, capture_output=True, text=True)
    (OUT / f"reseed-{which}.log").write_text(r.stdout + r.stderr)
    (OUT / f"reseed-{which}.rc").write_text(str(r.returncode) + "\n")
    print("reseed", which, "rc", r.returncode, "seeded" if seeded else "unseeded", flush=True)
    if r.returncode != 0:
        raise SystemExit("reseed failed " + which)
    if seeded:
        # setup.sh --seed already installed test-seed-role and fixtures.
        return
    seed_env = pathlib.Path(f"/tmp/ff-db/{DB}_test_seed.env")
    pw = seed_env.read_text().split("TEST_SEED_PASSWORD=", 1)[1].strip().split("\n", 1)[0]
    script = (f"\\set test_seed_password '{pw}'\n\\c {DB}\n"
              + (ROOT / "db" / "kit" / "test-seed-role.sql").read_text())
    rr = subprocess.run(
        ["sudo", "-u", "postgres", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", "postgres", "-f", "-"],
        input=script, capture_output=True, text=True)
    (OUT / f"reseed-role-{which}.log").write_text(rr.stdout + rr.stderr)
    (OUT / f"reseed-role-{which}.rc").write_text(str(rr.returncode) + "\n")
    if rr.returncode != 0:
        raise SystemExit("test-seed-role failed " + which)

def safe_restore(name, restore, seeded=False):
    try:
        restore()
    except SystemExit as ex:
        # A full reseed rebuilds the product schema. Do not re-ADD objects that
        # the reseed already installed (unique indexes, check constraints).
        print("restore-failed", name, ex, "; reseeding instead", flush=True)
        reseed(name + "-restore-retry", seeded=seeded)

def cycle(name, apply, restore, cmd, build=None, seeded=False):
    only = os.environ.get("MUTANT_ONLY", "")
    if only and name not in only.split(","):
        print("skip", name)
        return
    try:
        reseed(name + "-fail", seeded=seeded)
        apply()
        rc = run(name + "-fail", cmd, build)
        if rc == 0:
            print("WARNING", name, "did not fail", flush=True)
            WARNINGS.append(name + " did not fail")
    finally:
        safe_restore(name, restore, seeded=seeded)
    reseed(name + "-pass", seeded=seeded)
    rc = run(name + "-pass", cmd, build)
    if rc != 0:
        print("WARNING", name, "did not pass after restore", flush=True)
        WARNINGS.append(name + " did not pass after restore")

def cycle_files(name, apply, restore, cmd, build=None):
    """File-only mutant cycle: no DB reseed (UX / server / shell)."""
    only = os.environ.get("MUTANT_ONLY", "")
    if only and name not in only.split(","):
        print("skip", name)
        return
    try:
        apply()
        rc = run(name + "-fail", cmd, build)
        if rc == 0:
            print("WARNING", name, "did not fail", flush=True)
            WARNINGS.append(name + " did not fail")
    finally:
        restore()
    rc = run(name + "-pass", cmd, build)
    if rc != 0:
        print("WARNING", name, "did not pass after restore", flush=True)
        WARNINGS.append(name + " did not pass after restore")

def main():
    # Capture policies and function bodies from a freshly installed kit DB.
    reseed("baseline-capture")
    remember("auth.resolve_grant(text, uuid)", True)
    remember("auth.actor_can_location(uuid, uuid)", True)
    remember("auth.record_login_write(text, uuid)", True)
    remember("auth._last_sa_guard()", False)
    remember("auth._last_sa_truncate_guard()", False)
    remember("catalog.history_partner(uuid, uuid[], boolean)", False)
    remember("auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz)", True)
    # A2 split location_admin into select/insert/update policies.
    loc_using = sudo_at("SELECT pg_get_expr(polqual, polrelid) FROM pg_policy WHERE polname = 'location_admin_select'")
    loc_check = sudo_at("SELECT coalesce(pg_get_expr(polwithcheck, polrelid), '') FROM pg_policy WHERE polname = 'location_admin_select'")
    prb_using = sudo_at("SELECT pg_get_expr(polqual, polrelid) FROM pg_policy WHERE polname = 'prb_select'")
    if not loc_using.strip():
        raise SystemExit("location_admin_select USING was empty after baseline reseed")

    # m1 directory skips the resolver
    def m1_apply():
        patch("src/Admin.Api/AssignmentResolver.cs",
              "    {\n        await using var cmd = new NpgsqlCommand(",
              "    {\n        if (Environment.TickCount >= int.MinValue)\n            return new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true);\n        await using var cmd = new NpgsqlCommand(")
    cycle("m1-directory-skips-resolver", m1_apply,
          lambda: git_checkout("src/Admin.Api/AssignmentResolver.cs"), REQ, REQ_BUILD)

    # m2 skip kid
    def m2_apply():
        patch("src/Auth.Issuer/Program.cs",
              'if (!kids.Contains(kid.GetString() ?? "")) return false;',
              'if (kid.GetString() == "no-such-kid") return false;')
    cycle("m2-skip-kid", m2_apply, lambda: git_checkout("src/Auth.Issuer/Program.cs"), REQ, REQ_BUILD)

    # m3 duplicate sub
    def m3_apply():
        patch("src/Auth.Issuer/Program.cs",
              """        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(t.EncodedPayload), new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 16 });
        return payload.RootElement.ValueKind == JsonValueKind.Object;""",
              "        return true;")
    cycle("m3-allow-duplicate-sub", m3_apply, lambda: git_checkout("src/Auth.Issuer/Program.cs"), REQ, REQ_BUILD)

    # m4 last-SA row guard does not raise
    noop = SAVED["auth._last_sa_guard()"][0]
    noop_mut = replace_body(noop, "BEGIN\n  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;\n  RETURN NEW;\nEND")
    cycle("m4-last-sa-no-raise",
          lambda: install_fn(noop_mut, "auth._last_sa_guard()", OWNER, False),
          lambda: restore_fn("auth._last_sa_guard()"), REQ)

    # m4b MEDIUM-2: remove DELETE from the last-SA row trigger (UPDATE stays)
    def m4b_remove_delete():
        sudo_psql("""
DROP TRIGGER IF EXISTS admin_assignment_last_sa ON auth.admin_assignment;
CREATE TRIGGER admin_assignment_last_sa
  BEFORE UPDATE ON auth.admin_assignment
  FOR EACH ROW EXECUTE FUNCTION auth._last_sa_guard();
""")
    def m4b_restore_delete():
        sudo_psql("""
DROP TRIGGER IF EXISTS admin_assignment_last_sa ON auth.admin_assignment;
CREATE TRIGGER admin_assignment_last_sa
  BEFORE UPDATE OR DELETE ON auth.admin_assignment
  FOR EACH ROW EXECUTE FUNCTION auth._last_sa_guard();
""")
    cycle("m4b-last-sa-delete-trigger-removed", m4b_remove_delete, m4b_restore_delete, REQ)

    # m7 no nextval (SSE2 MEDIUM-1 / SA M-1: PERFORM NULL must FAIL resolver-store-is-read)
    cycle("m7-no-nextval",
          lambda: mutate_body("auth.resolve_grant(text, uuid)",
                              "PERFORM nextval('auth.resolver_read_seq');", "PERFORM NULL;", True),
          lambda: restore_fn("auth.resolve_grant(text, uuid)"), REQ)

    # m8 cache the last grant
    def m8_apply():
        text = (ROOT / "src/Admin.Api/AssignmentResolver.cs").read_text()
        text = text.replace(
            "public sealed class DbAdminDirectory : IAdminDirectory\n{",
            "public sealed class DbAdminDirectory : IAdminDirectory\n{\n    static AdminGrant? _last;\n", 1)
        old = """        if (global && roleWire == "super_admin")
            return new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true,
                new HashSet<Guid>(partners), new HashSet<Guid>(locations), version);"""
        new = """        if (global && roleWire == "super_admin")
        {
            _last = new AdminGrant(BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true,
                new HashSet<Guid>(partners), new HashSet<Guid>(locations), version);
            return _last;
        }"""
        if old not in text:
            raise SystemExit("m8 pattern missing")
        text = text.replace(old, new, 1)
        text = text.replace(
            "    {\n        await using var cmd = new NpgsqlCommand(",
            "    {\n        if (_last is not null) return _last;\n        await using var cmd = new NpgsqlCommand(", 1)
        (ROOT / "src/Admin.Api/AssignmentResolver.cs").write_text(text)
    cycle("m8-cache-grant", m8_apply, lambda: git_checkout("src/Admin.Api/AssignmentResolver.cs"), REQ, REQ_BUILD)

    # m9 ignore revoked_at
    def m9_apply():
        defn = fn_def("auth.resolve_grant(text, uuid)")
        mut = defn.replace(" AND a.revoked_at IS NULL", "")
        if "revoked_at IS NULL" in mut:
            raise SystemExit("m9 left a revoked_at predicate")
        install_fn(mut, "auth.resolve_grant(text, uuid)", DEFINER, True)
    cycle("m9-ignore-revoked-at", m9_apply, lambda: restore_fn("auth.resolve_grant(text, uuid)"), REQ)

    # m10 both layers: C# partner check off, and the moved predicate returns true
    def m10_apply():
        patch("src/Admin.Api/LoginConfigApi.cs",
              'if (who.IsPartnerAdmin && !who.Grant.AssignedPartners.Contains(real)) throw Err(404, "not_found");',
              'if (false && who.IsPartnerAdmin && !who.Grant.AssignedPartners.Contains(real)) throw Err(404, "not_found");')
        mutate_body("auth.actor_can_location(uuid, uuid)",
                    "SELECT auth.actor_is_global()\n          OR auth.actor_can_partner(p_partner)\n          OR auth.actor_has_location(p_location)",
                    "SELECT true", True)
    def m10_restore():
        git_checkout("src/Admin.Api/LoginConfigApi.cs")
        restore_fn("auth.actor_can_location(uuid, uuid)")
    cycle("m10-drop-partner-filter", m10_apply, m10_restore, REQ, REQ_BUILD)

    # m11 location predicate only (select policy)
    cycle("m11-location-predicate",
          lambda: set_policy("auth.location", "location_admin_select", "true", None),
          lambda: set_policy("auth.location", "location_admin_select", loc_using, loc_check or None), REQ)

    # m12 truncate guard no-op
    trunc = SAVED["auth._last_sa_truncate_guard()"][0]
    trunc_mut = replace_body(trunc, "BEGIN\n  RETURN NULL;\nEND")
    cycle("m12-truncate-guard",
          lambda: install_fn(trunc_mut, "auth._last_sa_truncate_guard()", OWNER, False),
          lambda: restore_fn("auth._last_sa_truncate_guard()"), REQ)

    # H-A role filter removed from the partner id aggregate
    cycle("ha-first-partner",
          lambda: mutate_body("auth.resolve_grant(text, uuid)",
                              "WHERE a.scope_kind = 'partner' AND a.role_id = 'partner_admin' AND a.partner_id IS NOT NULL",
                              "WHERE a.scope_kind = 'partner' AND a.partner_id IS NOT NULL", True),
          lambda: restore_fn("auth.resolve_grant(text, uuid)"), REQ)

    # history label is the first actor partner
    def hist_apply():
        defn = fn_def("catalog.history_partner(uuid, uuid[], boolean)")
        mut = replace_body(defn, "SELECT (p_actor)[1]")
        install_fn(mut, "catalog.history_partner(uuid, uuid[], boolean)", OWNER, False)
    cycle("history-first-partner", hist_apply,
          lambda: restore_fn("catalog.history_partner(uuid, uuid[], boolean)"), REQ)

    # M-A ignore issuer
    cycle("ma-ignore-issuer",
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             "JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = p_iss\n   WHERE u.user_id::text = p_sub",
                             "JOIN auth.user_issuer i ON i.user_id = u.user_id\n   WHERE u.user_id::text = p_sub"),
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             "JOIN auth.user_issuer i ON i.user_id = u.user_id\n   WHERE u.user_id::text = p_sub",
                             "JOIN auth.user_issuer i ON i.user_id = u.user_id AND i.issuer = p_iss\n   WHERE u.user_id::text = p_sub"), REQ)

    # H-B flag is trusted again inside the moved predicate
    cycle("hb-flag-trust",
          lambda: mutate_body("auth.actor_can_location(uuid, uuid)",
                              "SELECT auth.actor_is_global()",
                              "SELECT current_setting('app.resolved_global_admin', true) = 'on'\n      OR auth.actor_is_global()",
                              True),
          lambda: restore_fn("auth.actor_can_location(uuid, uuid)"), REQ)

    # C-4 first user instead of the request principal
    cycle("c4-first-user",
          lambda: mutate_body("auth.record_login_write(text, uuid)",
                              "SELECT s.iss, s.sub INTO v_iss, v_sub",
                              "SELECT s.iss, (SELECT user_id::text FROM auth.app_user ORDER BY user_id LIMIT 1) INTO v_iss, v_sub",
                              True),
          lambda: restore_fn("auth.record_login_write(text, uuid)"), REQ)

    # M-B a location id in a GUC opens every partner's bounds
    cycle("mb-location-opens-bounds",
          lambda: set_policy("auth.partner_reason_bounds", "prb_select",
                             "auth.actor_is_global() OR auth.actor_is_any_admin() OR coalesce(current_setting('app.resolved_location_ids', true), '') <> ''",
                             None),
          lambda: set_policy("auth.partner_reason_bounds", "prb_select", prb_using, None), REQ)

    # DEC-111 write gate widened to location-only and no-assignment callers
    cycle("dec111-widen-write",
          lambda: mutate_body("auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz)",
                              "IF NOT auth.actor_is_any_admin() THEN\n        RAISE EXCEPTION 'partner_reason_bounds' USING ERRCODE = '42501';",
                              "IF false THEN\n        RAISE EXCEPTION 'partner_reason_bounds' USING ERRCODE = '42501';",
                              True),
          lambda: restore_fn("auth.save_partner_bounds(uuid, bigint, integer, integer, uuid, timestamptz)"), REQ)

    # location arm removed
    cycle("location-arm-removed",
          lambda: mutate_body("auth.actor_can_location(uuid, uuid)",
                              "OR auth.actor_has_location(p_location)",
                              "OR false", True),
          lambda: restore_fn("auth.actor_can_location(uuid, uuid)"), REQ)

    # last-SA locks removed
    def locks_apply():
        for sig in ("auth._last_sa_guard()", "auth._last_sa_truncate_guard()"):
            mutate_body(sig, "PERFORM pg_advisory_xact_lock(814064204);", "PERFORM 1;", False)
    def locks_restore():
        restore_fn("auth._last_sa_guard()")
        restore_fn("auth._last_sa_truncate_guard()")
    cycle("last-sa-locks-removed", locks_apply, locks_restore, REQ)

    # (g) token admin_type chooses a seeded super admin
    def g_apply():
        patch("src/Auth.Issuer/Program.cs",
              'session = await Open(accepted.Token.Issuer, accepted.User.ToString("D"), accepted.Jti, accepted.Exp, accepted.Iat);',
              'session = await Open(accepted.Token.Issuer, accepted.AdminType == "super" ? "5f0c3e1a-0064-4000-8000-000000000031" : accepted.User.ToString("D"), accepted.Jti, accepted.Exp, accepted.Iat);')
    cycle("g-token-sourced-principal", g_apply,
          lambda: git_checkout("src/Auth.Issuer/Program.cs"), REQ, REQ_BUILD)

    # (i) and m6: Clear is a no-op. The read suite is the one that samples with reset disabled.
    def clear_apply():
        patch("src/Catalog.Read/CatalogSessionSql.cs",
              "SELECT set_config('app.resolved_global_admin', '', false),",
              "SELECT 1; -- removed\n        SELECT set_config('app.resolved_global_admin', '', false),")
    # That still runs the real clear after SELECT 1 because it's one statement list... 
    # Replace the whole command with SELECT 1.
    def clear_apply2():
        file = ROOT / "src/Catalog.Read/CatalogSessionSql.cs"
        text = file.read_text()
        start = text.index("    public const string Clear =")
        end = text.index("    public const string CatalogIssuer", start)
        text = text[:start] + "    public const string Clear =\n        \"\"\"\n        SELECT 1\n        \"\"\";\n\n" + text[end:]
        file.write_text(text)
    cycle("m6-reader-clear-noop", clear_apply2,
          lambda: git_checkout("src/Catalog.Read/CatalogSessionSql.cs"), READ, READ_BUILD)
    cycle("i-clear-removed", clear_apply2,
          lambda: git_checkout("src/Catalog.Read/CatalogSessionSql.cs"), READ, READ_BUILD)

    # C-1 no pool reset
    def reset_apply():
        patch("src/Admin.Api/AdminApiHost.cs",
              "{ NoResetOnClose = false }",
              "{ NoResetOnClose = true }")
    cycle("c1-no-pool-reset", reset_apply,
          lambda: git_checkout("src/Admin.Api/AdminApiHost.cs"), REQ, REQ_BUILD)

    # m5 grants text trusts admin_type again. The query suite reads the file.
    def m5_apply():
        patch("src/Auth.Data/Migrations/CatalogMasterData.Grants.sql",
              "    WHEN current_setting('app.resolved_global_admin', true) = 'on' THEN true",
              "    WHEN current_setting('app.admin_type', true) = 'super' THEN true")
    cycle("m5-admin-type-branch", m5_apply,
          lambda: git_checkout("src/Auth.Data/Migrations/CatalogMasterData.Grants.sql"), QUERY)


    # The binding is ignored and the GUC is the principal.
    _principal = (
        "      SELECT s.user_id\n"
        "        FROM auth.request_binding b\n"
        "        JOIN auth.request_session s ON s.token_hash = b.token_hash\n"
        "       WHERE b.backend_pid = pg_backend_pid()\n"
        "         AND b.xact_id = pg_current_xact_id()\n"
        "         AND s.revoked_at IS NULL\n"
        "         AND s.expires_at > clock_timestamp()"
    )
    _guc = "      SELECT nullif(current_setting('app.request_sub', true), '')::uuid"
    cycle("guc-trusted-again",
          lambda: replace_fn("auth.current_principal()", _principal, _guc),
          lambda: replace_fn("auth.current_principal()", _guc, _principal), REQ)

    cycle("open-session-granted-to-admin",
          lambda: sudo_psql("GRANT EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) TO ff_kit_req064_admin_api"),
          lambda: sudo_psql("REVOKE EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM ff_kit_req064_admin_api"), REQ)

    def ttl_restore():
        sudo_psql("DELETE FROM auth.request_binding; DELETE FROM auth.request_session;")
        sudo_psql("ALTER TABLE auth.request_session DROP CONSTRAINT IF EXISTS request_session_ttl")
        sudo_psql("ALTER TABLE auth.request_session ADD CONSTRAINT request_session_ttl CHECK (expires_at <= created_at + interval '15 minutes')")
    cycle("ttl-check-removed",
          lambda: sudo_psql("ALTER TABLE auth.request_session DROP CONSTRAINT request_session_ttl"),
          ttl_restore, REQ)

    cycle("principal-skips-revoked",
          lambda: replace_fn("auth.current_principal()",
                             "AND s.revoked_at IS NULL",
                             "AND (TRUE)"),
          lambda: replace_fn("auth.current_principal()",
                             "AND (TRUE)",
                             "AND s.revoked_at IS NULL"), REQ)

    cycle("parallel-safe",
          lambda: sudo_psql("ALTER FUNCTION auth.current_principal() PARALLEL SAFE"),
          lambda: sudo_psql("ALTER FUNCTION auth.current_principal() PARALLEL RESTRICTED"), REQ)

    cycle("binding-allows-rebind",
          lambda: replace_fn("auth.begin_request(text)",
                             "RAISE EXCEPTION 'session_rebind' USING ERRCODE = '25001';",
                             "NULL;"),
          lambda: replace_fn("auth.begin_request(text)",
                             "NULL;",
                             "RAISE EXCEPTION 'session_rebind' USING ERRCODE = '25001';"), REQ)

    cycle("token-interpolated",
          lambda: replace_fn("auth.begin_request(text)",
                             "sha256(convert_to(p_token, 'UTF8'))",
                             "sha256(convert_to(format('%s', p_token), 'UTF8'))"),
          lambda: replace_fn("auth.begin_request(text)",
                             "sha256(convert_to(format('%s', p_token), 'UTF8'))",
                             "sha256(convert_to(p_token, 'UTF8'))"), REQ)

    def jti_restore():
        sudo_psql("DELETE FROM auth.request_binding; DELETE FROM auth.request_session;")
        sudo_psql("ALTER TABLE auth.request_session DROP CONSTRAINT IF EXISTS request_session_jti_key")
        sudo_psql("ALTER TABLE auth.request_session ADD CONSTRAINT request_session_jti_key UNIQUE (jti)")
    cycle("jti-uniqueness-removed",
          lambda: sudo_psql("ALTER TABLE auth.request_session DROP CONSTRAINT request_session_jti_key"),
          jti_restore, REQ)

    cycle("expiry-not-capped",
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             "v_exp := least(p_exp, v_created + interval '15 minutes');",
                             "v_exp := p_exp;"),
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             "v_exp := p_exp;",
                             "v_exp := least(p_exp, v_created + interval '15 minutes');"), REQ)

    cycle("session-cap-removed",
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             ") >= 32 THEN",
                             ") >= 100000 THEN"),
          lambda: replace_fn("auth.open_session(text, text, uuid, timestamptz, timestamptz)",
                             ") >= 100000 THEN",
                             ") >= 32 THEN"), REQ)


    ISSUER = ["dotnet", "run", "--project", "src/Admin.Api.Checks/Admin.Api.Checks.csproj", "--no-build", "--", "--issuer"]
    PROBE = ["dotnet", "run", "--project", "src/Admin.Api.Checks/Admin.Api.Checks.csproj", "--no-build", "--", "--probe"]
    AUTH = ["dotnet", "run", "--project", "src/Auth.Data.Checks/Auth.Data.Checks.csproj", "--no-build", "--", "src/Login.StandIn/fixtures.json"]
    AUTH_BUILD = ["dotnet", "build", "src/Auth.Data.Checks/Auth.Data.Checks.csproj", "-v", "q", "--nologo"]

    cycle("r-expiry-not-enforced",
          lambda: replace_fn("auth.begin_request(text)",
                             "AND s.expires_at > clock_timestamp()",
                             "AND (TRUE)"),
          lambda: replace_fn("auth.begin_request(text)",
                             "AND (TRUE)",
                             "AND s.expires_at > clock_timestamp()"), REQ)

    cycle("r-public-temp",
          lambda: sudo_psql("GRANT TEMP ON DATABASE ff_kit_req064 TO PUBLIC"),
          lambda: sudo_psql("REVOKE TEMP ON DATABASE ff_kit_req064 FROM PUBLIC"), AUTH,
          seeded=True)

    def skip_revoke():
        patch("src/Auth.Issuer/Program.cs",
              "try { await RevokeLogin(tokenIss, sub); }",
              "try { if (tokenIss.Length < 0) await RevokeLogin(tokenIss, sub); }")
    cycle("skip-revoke", skip_revoke, lambda: git_checkout("src/Auth.Issuer/Program.cs"), ISSUER, REQ_BUILD)

    def jti_cross():
        patch("src/Auth.Issuer/Program.cs",
              """            if (cached.User != accepted.User || !string.Equals(cached.Iss, accepted.Token.Issuer, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                return;
            }
            // A revoked row or an iat at or before login_cutoff is not served from the cache.
            if (!await SessionStillOpen(cached.Session, accepted.Iat))""",
              """            // A revoked row or an iat at or before login_cutoff is not served from the cache.
            if (!await SessionStillOpen(cached.Session, accepted.Iat))""")
    cycle("jti-cross-subject", jti_cross, lambda: git_checkout("src/Auth.Issuer/Program.cs"), PROBE, REQ_BUILD)

    def secret_skip():
        patch("src/Auth.Issuer/Program.cs",
              "if (!CallerSecretOk(ctx.Request))",
              "if (CallerSecretOk(ctx.Request) && false)")
    cycle("caller-secret-skipped", secret_skip, lambda: git_checkout("src/Auth.Issuer/Program.cs"), ISSUER, REQ_BUILD)

    def guard_allow():
        patch("src/Admin.Api/AdminApiHost.cs",
              """                   r.rolname = current_database() || '_issuer',
                   has_function_privilege(current_user, 'auth.open_session(text,text,uuid,timestamptz,timestamptz)', 'EXECUTE')
                   OR has_function_privilege(current_user, 'auth.revoke_session(text)', 'EXECUTE')
                   OR has_function_privilege(current_user, 'auth.revoke_login(text,text)', 'EXECUTE'),""",
              """                   false,
                   false,""")
    cycle("guard-allows-issuer", guard_allow, lambda: git_checkout("src/Admin.Api/AdminApiHost.cs"), ISSUER, REQ_BUILD)

    def log_allow():
        patch("src/Admin.Api/AdminApiHost.cs",
              "        RefuseStatementLogging(r.GetString(5), r.GetString(6), r.GetString(7));\n",
              "")
    cycle("host-ignores-statement-logging", log_allow, lambda: git_checkout("src/Admin.Api/AdminApiHost.cs"), ISSUER, REQ_BUILD)

    _actor_from = (
        "      SELECT g.outcome, g.user_id, g.global_admin, g.partner_ids\n"
        "        INTO v_outcome, v_user, v_global, v_partners\n"
        "        FROM auth.resolve_grant('', '00000000-0000-0000-0000-000000000000'::uuid) g;"
    )
    _actor_guc = (
        "  v_outcome := 'grant';\n"
        "  v_user := nullif(current_setting('app.actor_user_id', true), '')::uuid;\n"
        "  v_global := current_setting('app.resolved_global_admin', true) = 'on';\n"
        "  v_partners := CASE WHEN coalesce(current_setting('app.resolved_partner_ids', true), '') = '' THEN ARRAY[]::uuid[]\n"
        "                     ELSE ARRAY[split_part(current_setting('app.resolved_partner_ids', true), ',', 1)::uuid] END;"
    )
    remember("catalog._actor()", False)
    cycle("actor-trusts-resolved-guc",
          lambda: replace_fn("catalog._actor()", _actor_from, _actor_guc),
          lambda: restore_fn("catalog._actor()"), ISSUER)

    cycle("owner-open-session-restored",
          lambda: sudo_psql("GRANT EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) TO ff_kit_req064"),
          lambda: sudo_psql("REVOKE EXECUTE ON FUNCTION auth.open_session(text, text, uuid, timestamptz, timestamptz) FROM ff_kit_req064"), REQ)

    def scratch_marked():
        patch("src/Auth.Data.Checks/ScratchDb.cs",
              "=> after == before && !stillExists && !marked.Contains(runName);",
              "=> after == before && !stillExists && marked.Count == 0;")
    cycle("scratch-marked-count", scratch_marked, lambda: git_checkout("src/Auth.Data.Checks/ScratchDb.cs"), AUTH, AUTH_BUILD,
          seeded=True)

    STANDIN_BUILD = ["dotnet", "build", "src/Login.StandIn/Login.StandIn.csproj", "-v", "q", "--nologo", "-p:IncludeStandIn=true"]
    ISSUER_BUILD = ["dotnet", "build", "src/Auth.Issuer/Auth.Issuer.csproj", "-v", "q", "--nologo"]

    def jti_handle():
        patch("src/Login.StandIn/Tokens.cs",
              'var jti = Guid.NewGuid().ToString("D");',
              "var jti = NewHandle();")
    cycle("access-jti-is-newhandle", jti_handle, lambda: git_checkout("src/Login.StandIn/Tokens.cs"), REQ, STANDIN_BUILD)

    def expiry_403():
        patch("src/Admin.Api/LoginConfigApi.cs",
              'ex.MessageText is "session_expired" or "session_denied"',
              'ex.MessageText is "session_expired"')
        patch("src/Admin.Api/LoginConfigApi.cs",
              """            catch (SessionClosedException)
            {
                return Closed(ctx);
            }
""",
              "")
    cycle("closed-session-split", expiry_403, lambda: git_checkout("src/Admin.Api/LoginConfigApi.cs"), REQ, REQ_BUILD)

    def shell_skip():
        patch("ui/admin-shell/shell.js",
              "if (res.status === 401)",
              "if (false && res.status === 401)")
    cycle("shell-skips-signin", shell_skip, lambda: git_checkout("ui/admin-shell/shell.js"), REQ)

    OPEN = "auth.open_session(text, text, uuid, timestamptz, timestamptz)"
    cycle("open-session-has-no-login-lock",
          lambda: replace_fn(OPEN, "PERFORM auth._lock_login(v_user);\n", "NULL;\n"),
          lambda: replace_fn(OPEN, "NULL;\n", "PERFORM auth._lock_login(v_user);\n"), REQ)
    cycle("open-session-ignores-revoked-before",
          lambda: replace_fn(OPEN, "AND p_iat <= c.revoked_before", "AND FALSE"),
          lambda: replace_fn(OPEN, "AND FALSE", "AND p_iat <= c.revoked_before"), REQ)

    def bounds_skip():
        patch("src/Admin.Api/LoginConfigApi.cs",
              """            catch (PostgresException ex) when (IsClosedSession(ex))
            {
                await tx2.RollbackAsync();
                throw new ClosedSession();
            }""",
              """            catch (PostgresException ex) when (false && IsClosedSession(ex))
            {
                await tx2.RollbackAsync();
                throw new ClosedSession();
            }""")
    cycle("bounds-retry-ignores-closed-session", bounds_skip, lambda: git_checkout("src/Admin.Api/LoginConfigApi.cs"), REQ, REQ_BUILD)

    def status_before():
        replace_fn(OPEN,
            "   WHERE u.user_id::text = p_sub;\n",
            "   WHERE u.user_id::text = p_sub\n"
            "     AND u.status = 'active'\n"
            "     AND (u.locked_until IS NULL OR u.locked_until <= v_created);\n")
        replace_fn(OPEN,
            "  IF NOT EXISTS (SELECT 1 FROM auth.app_user u WHERE u.user_id = v_user AND u.status = 'active' AND (u.locked_until IS NULL OR u.locked_until <= v_created)) THEN\n"
            "    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';\n"
            "  END IF;\n",
            "")
    def status_after():
        replace_fn(OPEN,
            "   WHERE u.user_id::text = p_sub\n"
            "     AND u.status = 'active'\n"
            "     AND (u.locked_until IS NULL OR u.locked_until <= v_created);\n",
            "   WHERE u.user_id::text = p_sub;\n")
        replace_fn(OPEN,
            "  PERFORM auth._lock_login(v_user);\n",
            "  PERFORM auth._lock_login(v_user);\n"
            "  IF NOT EXISTS (SELECT 1 FROM auth.app_user u WHERE u.user_id = v_user AND u.status = 'active' AND (u.locked_until IS NULL OR u.locked_until <= v_created)) THEN\n"
            "    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';\n"
            "  END IF;\n")
    cycle("status-check-before-lock", status_before, status_after, REQ)

    IAT = (
        "  IF p_iat > clock_timestamp() + interval '1 second' THEN\n"
        "    RAISE EXCEPTION 'session_denied' USING ERRCODE = '42501';\n"
        "  END IF;\n"
    )
    def future_off():
        replace_fn(OPEN, IAT, "")
    def future_on():
        replace_fn(OPEN,
            "  IF EXISTS (\n"
            "    SELECT 1 FROM auth.login_cutoff c\n"
            "     WHERE c.user_id = v_user\n"
            "       AND p_iat <= c.revoked_before\n",
            IAT +
            "  IF EXISTS (\n"
            "    SELECT 1 FROM auth.login_cutoff c\n"
            "     WHERE c.user_id = v_user\n"
            "       AND p_iat <= c.revoked_before\n")
    cycle("future-iat-accepted", future_off, future_on, REQ)

    cycle("cutoff-comparison-off-by-one",
          lambda: replace_fn(OPEN, "AND p_iat <= c.revoked_before", "AND p_iat < c.revoked_before"),
          lambda: replace_fn(OPEN, "AND p_iat < c.revoked_before", "AND p_iat <= c.revoked_before"), REQ)

    cycle("low1-issuer-name-only",
          lambda: replace_fn(OPEN, "AND i.issuer = p_iss",
                             "AND split_part(i.issuer, '/', 3) = split_part(p_iss, '/', 3)"),
          lambda: replace_fn(OPEN, "AND split_part(i.issuer, '/', 3) = split_part(p_iss, '/', 3)",
                             "AND i.issuer = p_iss"), REQ)

    def cutoff_grant():
        sudo_psql(
            "GRANT INSERT, UPDATE, DELETE ON TABLE auth.login_cutoff TO ff_kit_req064;\n"
            "DROP POLICY IF EXISTS login_cutoff_owner ON auth.login_cutoff;\n"
            "CREATE POLICY login_cutoff_owner ON auth.login_cutoff FOR ALL TO ff_kit_req064 USING (true) WITH CHECK (true);")
    def cutoff_revoke():
        sudo_psql(
            "REVOKE INSERT, UPDATE, DELETE ON TABLE auth.login_cutoff FROM ff_kit_req064;\n"
            "DROP POLICY IF EXISTS login_cutoff_owner ON auth.login_cutoff;\n"
            "CREATE POLICY login_cutoff_owner ON auth.login_cutoff FOR SELECT TO ff_kit_req064 USING (true);")
    cycle("owner-can-write-login-cutoff", cutoff_grant, cutoff_revoke, REQ)

    def catalog_unmap():
        patch("src/Catalog.Read/NpgsqlCatalogReader.cs",
              "throw new CatalogSessionClosedException();",
              "throw;")
    cycle("catalog-closed-session-not-mapped", catalog_unmap,
          lambda: git_checkout("src/Catalog.Read/NpgsqlCatalogReader.cs"), READ, READ_BUILD)

    def issuer_skew():
        patch("src/Auth.Issuer/Program.cs", "const int IatFutureSeconds = 1;", "const int IatFutureSeconds = 120;")
    cycle("issuer-accepts-future-iat", issuer_skew,
          lambda: git_checkout("src/Auth.Issuer/Program.cs"), REQ, REQ_BUILD)

    def cache_replay():
        patch("src/Auth.Issuer/Program.cs",
              """            if (!await SessionStillOpen(cached.Session, accepted.Iat))
            {
                await WriteClosed(ctx);
                return;
            }
""",
              """            if (!await SessionStillOpen(cached.Session, accepted.Iat) && false)
            {
                await WriteClosed(ctx);
                return;
            }
""")
    cycle("revoked-session-replayed-from-cache", cache_replay,
          lambda: git_checkout("src/Auth.Issuer/Program.cs"), REQ, REQ_BUILD)

    def detect_off():
        patch("db/kit/database.sql",
              "-- intermediate-req064-recreate\n\\set drop 1",
              "-- intermediate-req064-recreate\n\\set drop 0")
    cycle("intermediate-history-migrated-in-place", detect_off,
          lambda: git_checkout("db/kit/database.sql"), REQ)

    def cutoff_truncate():
        sudo_psql("GRANT TRUNCATE ON TABLE auth.login_cutoff TO ff_kit_req064;")
    def cutoff_no_truncate():
        sudo_psql("REVOKE TRUNCATE ON TABLE auth.login_cutoff FROM ff_kit_req064;")
    cycle("owner-can-truncate-login-cutoff", cutoff_truncate, cutoff_no_truncate, REQ)

    def still_open_owner():
        sudo_psql("ALTER FUNCTION auth.session_still_open(text, timestamptz) OWNER TO ff_kit_req064;")
    def still_open_definer():
        sudo_psql("ALTER FUNCTION auth.session_still_open(text, timestamptz) OWNER TO ff_kit_req064_auth_definer;")
    cycle("owner-can-replace-session-still-open", still_open_owner, still_open_definer, REQ)

    def issuer_fn_owner():
        sudo_psql("ALTER FUNCTION auth.session_issuer() OWNER TO ff_kit_req064;")
    def issuer_fn_definer():
        sudo_psql("ALTER FUNCTION auth.session_issuer() OWNER TO ff_kit_req064_auth_definer;")
    cycle("security-definer-left-with-owner", issuer_fn_owner, issuer_fn_definer, REQ)

    def anything_main():
        patch("db/kit/database.sql",
              """   WHERE "MigrationId" IN (
     '20261110160000_Req064AssignmentResolver',
     '20261110200000_Req064RoleScope',
     '20261110210000_Req064HistoryPartner',
     '20261110220000_Req064Issuer',
     '20261110230000_Req064Principal',
     '20261110240000_Req064ReasonRead',
     '20261110250000_Req064Definer',
     '20261110260000_Req064RlsPrincipal',
     '20261110270000_Req064LocationSave',
     '20261110280000_Req064RevokeTemp',
     '20261110290000_Req064Session',
     '20261110300000_Req064RevokeLogin',
     '20261110310000_Req064Actor')""",
              """   WHERE "MigrationId" NOT IN (
     '20261104160000_S2aInitial',
     '20261104170000_S2aLlcColumnUpdate',
     '20261105080000_S2aPartnerReasonBounds',
     '20261108190000_CatalogMasterData')""")
    cycle("anything-not-on-main-recreates", anything_main,
          lambda: git_checkout("db/kit/database.sql"), REQ)

    HELPERS = ["dotnet", "run", "--project", "src/Admin.Api.Checks/Admin.Api.Checks.csproj", "--no-build", "--", "--helpers"]

    def last_sa_owner():
        sudo_psql("ALTER FUNCTION auth._last_sa_guard() OWNER TO " + OWNER + ";")
    def last_sa_definer():
        sudo_psql("ALTER FUNCTION auth._last_sa_guard() OWNER TO " + DEFINER + ";")
    cycle("owner-can-replace-last-sa-guard", last_sa_owner, last_sa_definer, HELPERS)

    def location_owner():
        sudo_psql("ALTER FUNCTION auth.location_partner(uuid) OWNER TO " + OWNER + ";")
    def location_definer():
        sudo_psql("ALTER FUNCTION auth.location_partner(uuid) OWNER TO " + DEFINER + ";")
    cycle("owner-can-replace-location-partner", location_owner, location_definer, HELPERS)

    def unlisted_apply():
        sudo_psql("""
CREATE FUNCTION auth.zz_unlisted_definer() RETURNS void
  LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
AS $$ BEGIN NULL; END $$;
ALTER FUNCTION auth.zz_unlisted_definer() OWNER TO %s;
""" % OWNER)
    def unlisted_drop():
        sudo_psql("DROP FUNCTION IF EXISTS auth.zz_unlisted_definer();")
    cycle("unlisted-owner-definer", unlisted_apply, unlisted_drop, HELPERS)

    def expect(name, text):
        only = os.environ.get("MUTANT_ONLY", "")
        if only and name not in only.split(","):
            return
        path = OUT / f"{name}-fail.log"
        if not path.exists():
            print("WARNING", name, "log missing", flush=True)
            WARNINGS.append(name + " log missing")
            return
        log = path.read_text(errors="replace")
        if text not in log:
            print("WARNING", name, "log missing", text, flush=True)
            WARNINGS.append(name + " log missing " + text)
        else:
            print("NAMED", name, text, flush=True)

    # --- UX / server mutants (UPDATE 36/38): file-only, no DB reseed ---
    ORGS = ["node", "ui/admin-shell/orgs-routes.test.mjs"]
    GATES = ["node", "ui/admin-shell/route-gates.test.mjs"]
    SCALARS = ["node", "ui/admin-shell/location-name-scalars.test.mjs"]
    EXPIRED = ["node", "ui/admin-shell/session-expired-reason.test.mjs"]
    REACH = ["node", "ui/admin-shell/browser-reachability.test.mjs"]

    # F-20: remove both /orgs server routes
    def f20_apply():
        patch("ui/admin-shell/server.mjs",
              'if (orgsNew || orgsSignIn || url.pathname === "/")',
              'if (url.pathname === "/" && false && (orgsNew || orgsSignIn))')
    cycle_files("f20-orgs-server-routes-removed", f20_apply,
                lambda: git_checkout("ui/admin-shell/server.mjs"), ORGS)

    # F-21: create gate removed (need: null)
    def f21_create_apply():
        patch("ui/admin-shell/shell.js",
              'create: { label: "Create location", need: "create_location" }',
              'create: { label: "Create location", need: null }')
    cycle_files("f21-create-gate-removed", f21_create_apply,
                lambda: git_checkout("ui/admin-shell/shell.js"), GATES)

    # F-21: config gate removed
    def f21_config_apply():
        patch("ui/admin-shell/shell.js",
              'config: { label: "Sign-in config", need: "config" }',
              'config: { label: "Sign-in config", need: null }')
    cycle_files("f21-config-gate-removed", f21_config_apply,
                lambda: git_checkout("ui/admin-shell/shell.js"), GATES)

    # F-23: break catalog family route serving
    def f23_apply():
        patch("ui/admin-shell/server.mjs",
              'if (url.pathname === "/catalog" || url.pathname === "/catalog/")',
              'if (url.pathname === "/katalog" || url.pathname === "/katalog/")')
    cycle_files("f23-catalog-family-route-broken", f23_apply,
                lambda: git_checkout("ui/admin-shell/server.mjs"), ORGS)

    # F-TL-B: drop expiry sentence
    def ftb_apply():
        patch("src/Login.StandIn/Handoff.cs",
              'reason == "expired"\n                ? $"""<p class="card__subtitle" data-testid="session-expired-sentence">For security, {E(Ui.ProductName)} asks for your sign-in again.</p>"""\n                : "";',
              'reason == "expired"\n                ? ""\n                : "";')
    cycle_files("ftlb-drop-expiry-sentence", ftb_apply,
                lambda: git_checkout("src/Login.StandIn/Handoff.cs"), EXPIRED)

    # F-24: revert client to UTF-16 .length
    def f24_apply():
        patch("ui/admin-shell/shell.js",
              'if ([...name].length > 200) return "name_invalid";',
              'if (name.length > 200) return "name_invalid";')
    cycle_files("f24-client-utf16-length", f24_apply,
                lambda: git_checkout("ui/admin-shell/shell.js"), SCALARS)

    # href-served: create href points at a path the server does not serve
    def href_create_apply():
        patch("ui/admin-shell/shell.js",
              'a.href = "/orgs/" + id + "/locations/new";',
              'a.href = "/orgs/" + id + "/locations/create";')
    cycle_files("href-create-not-served", href_create_apply,
                lambda: git_checkout("ui/admin-shell/shell.js"), ORGS)

    # href-served: sign-in href broken shape
    def href_signin_apply():
        patch("ui/admin-shell/shell.js",
              'config.href = "/orgs/" + row.partner_id + "/locations/" + row.location_id + "/sign-in";',
              'config.href = "/orgs/" + row.partner_id + "/locations/" + row.location_id + "/signin";')
    cycle_files("href-signin-not-served", href_signin_apply,
                lambda: git_checkout("ui/admin-shell/shell.js"), ORGS)

    expect("access-jti-is-newhandle", "FAIL stand-in-sign-in-opens-session-nav-locations-catalog")
    expect("closed-session-split", "FAIL closed-sessions-are-byte-identical")
    expect("shell-skips-signin", "FAIL shell-goes-to-signin-on-closed-session")
    expect("open-session-has-no-login-lock", "FAIL open-session-waits-on-the-login-lock")
    expect("open-session-ignores-revoked-before", "FAIL jwt-issued-before-logout-cannot-open")
    expect("bounds-retry-ignores-closed-session", "FAIL bounds-retry-closed-session-is-401")
    expect("status-check-before-lock", "FAIL open-during-a-lock-does-not-grant-after-expiry")
    expect("future-iat-accepted", "FAIL future-iat-cannot-beat-logout-or-skew")
    expect("cutoff-comparison-off-by-one", "FAIL same-second-re-sign-in-after-logout-is-denied")
    expect("low1-issuer-name-only", "FAIL issuer-name-alone-does-not-open")
    expect("owner-can-write-login-cutoff", "FAIL owner-cannot-write-login-cutoff")
    expect("catalog-closed-session-not-mapped", "FAIL catalog-closed-sessions-are-one-exception")
    expect("issuer-accepts-future-iat", "FAIL issuer-rejects-iat-beyond-one-second")
    expect("revoked-session-replayed-from-cache", "FAIL revoked-token-is-not-served-from-the-cache")
    expect("intermediate-history-migrated-in-place", "FAIL force-recreates-an-intermediate-req064-database")
    expect("owner-can-truncate-login-cutoff", "FAIL owner-cannot-truncate-login-cutoff")
    expect("owner-can-replace-session-still-open", "FAIL owner-cannot-replace-session-still-open")
    expect("security-definer-left-with-owner", "FAIL security-definer-owner-is-the-definer-role")
    expect("anything-not-on-main-recreates", "FAIL future-main-id-is-kept")
    expect("owner-can-replace-last-sa-guard", "FAIL owner-cannot-replace-last-sa-guards")
    expect("owner-can-replace-location-partner", "FAIL owner-cannot-replace-location-helpers")
    expect("unlisted-owner-definer", "FAIL security-definer-owner-is-the-definer-role")
    expect("m7-no-nextval", "FAIL resolver-store-is-read")

    expect("m4b-last-sa-delete-trigger-removed", "FAIL last-active-sa-delete")
    expect("f20-orgs-server-routes-removed", "FAIL server-serves-orgs-new")
    expect("f21-create-gate-removed", "FAIL pages-has-create-gated-by-create_location")
    expect("f21-config-gate-removed", "FAIL pages-has-config-gated-by-config")
    expect("f23-catalog-family-route-broken", "FAIL server-serves-catalog")
    expect("ftlb-drop-expiry-sentence", "FAIL expiry-sentence-mutant-applied")
    expect("f24-client-utf16-length", "FAIL utf16-mutant-applied")
    expect("href-create-not-served", "FAIL every-orgs-href-matches-served-shape")
    expect("href-signin-not-served", "FAIL every-orgs-href-matches-served-shape")

    print("MUTANTS_DONE")
    if WARNINGS:
        print("MUTANTS_WARNINGS", len(WARNINGS), flush=True)
        for w in WARNINGS:
            print("WARNING_DETAIL", w, flush=True)
        raise SystemExit(2)

if __name__ == "__main__":
    exit_code = 0
    try:
        main()
    except SystemExit as e:
        exit_code = e.code if isinstance(e.code, int) else 1
        (OUT / "crash.txt").write_text(str(e) + "\n")
    except Exception as e:
        exit_code = 1
        (OUT / "crash.txt").write_text(repr(e) + "\n")
        raise
    finally:
        git_checkout(
            "src/Admin.Api/AssignmentResolver.cs",
            "src/Admin.Api/AdminAuth.cs",
            "src/Admin.Api/LoginConfigApi.cs",
            "src/Admin.Api/AdminApiHost.cs",
            "src/Catalog.Read/CatalogSessionSql.cs",
            "src/Auth.Data/Migrations/CatalogMasterData.Grants.sql",
            "src/Auth.Issuer/Program.cs",
            "src/Auth.Data.Checks/ScratchDb.cs",
            "src/Login.StandIn/Tokens.cs",
            "src/Login.StandIn/Handoff.cs",
            "ui/admin-shell/shell.js",
            "ui/admin-shell/server.mjs",
            "src/Catalog.Read/NpgsqlCatalogReader.cs",
            "db/kit/database.sql")
        for sig, (defn, grant) in SAVED.items():
            owner = DEFINER if grant else OWNER
            try:
                install_fn(defn, sig, owner, grant)
            except SystemExit as ex:
                print("restore-fn-failed", sig, ex, flush=True)
        try:
            sudo_psql("""
DROP TRIGGER IF EXISTS admin_assignment_last_sa ON auth.admin_assignment;
CREATE TRIGGER admin_assignment_last_sa
  BEFORE UPDATE OR DELETE ON auth.admin_assignment
  FOR EACH ROW EXECUTE FUNCTION auth._last_sa_guard();
""")
        except SystemExit as ex:
            print("restore-trigger-failed", ex, flush=True)
        print("restored", flush=True)
        print("MUTANTS_EXIT:" + str(exit_code), flush=True)
    raise SystemExit(exit_code)
