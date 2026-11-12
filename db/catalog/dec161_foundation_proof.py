#!/usr/bin/env python3
"""Disposable proofs for the DEC-161 catalog foundation.

Historical scratch proof only. Nothing in the product imports or runs this file.
The statements below set app.admin_type because that was the DEC-161 scratch
session. Product authorization does not read that setting. It reads
app.resolved_global_admin and the resolved partner and location lists from
auth.resolve_grant.

Refuses every database except ff_kit_dec161_proof1 on 127.0.0.1:5433.
Harness EXECUTE for the scratch role is issued here, not by the migration.
"""
from __future__ import annotations

import subprocess
import sys
import threading
import time
from pathlib import Path

PSQL = "/usr/lib/postgresql/16/bin/psql"
HOST = "127.0.0.1"
PORT = "5433"
DB = "ff_kit_dec161_proof1"
OWNER = "dec161mig"
API = "ff_kit_dec161_proof1_admin_api"
LOGIN = "ff_kit_dec161_proof1_login"
SCRATCH = "ff_kit_dec161_proof1_scratch"
ROOT = Path("/agent/repos/FloorFunds")
DOWN = ROOT / "src/Auth.Data/Migrations/CatalogMasterData.Down.sql"

SA = "44444444-4444-4444-8444-444444444444"
PA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
PB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
ACTOR_A = "22222222-2222-4222-8222-222222222222"
ACTOR_B = "33333333-3333-4333-8333-333333333333"

FEE = "10000000-0000-4000-8000-000000000001"
FEE_B = "10000000-0000-4000-8000-000000000002"
CAT = "10000000-0000-4000-8000-000000000011"
MFR = "10000000-0000-4000-8000-000000000012"
MODEL = "10000000-0000-4000-8000-000000000013"
CAP = "10000000-0000-4000-8000-000000000014"
CLAIM = "10000000-0000-4000-8000-000000000015"
REL = "10000000-0000-4000-8000-000000000016"
PMFR = "10000000-0000-4000-8000-000000000021"
PMODEL = "10000000-0000-4000-8000-000000000022"
PCLAIM = "10000000-0000-4000-8000-000000000023"
GSHARE = "10000000-0000-4000-8000-000000000031"
PSHARE = "10000000-0000-4000-8000-000000000032"

results: list[tuple[str, bool, str]] = []


def psql(user: str, sql: str, stop: bool = False) -> str:
    proc = subprocess.run(
        [PSQL, "-h", HOST, "-p", PORT, "-U", user, "-d", DB, "-v", f"ON_ERROR_STOP={'1' if stop else '0'}", "-f", "-"],
        input=sql,
        text=True,
        capture_output=True,
        check=False,
    )
    return (proc.stdout or "") + (proc.stderr or "")


def scalar(user: str, sql: str) -> str:
    proc = subprocess.run(
        [PSQL, "-h", HOST, "-p", PORT, "-U", user, "-d", DB, "-v", "ON_ERROR_STOP=1", "-t", "-A", "-f", "-"],
        input=sql,
        text=True,
        capture_output=True,
        check=False,
    )
    if proc.returncode != 0:
        raise RuntimeError(proc.stderr or proc.stdout)
    return (proc.stdout or "").strip()


def record(name: str, ok: bool, detail: str) -> None:
    results.append((name, ok, detail))
    print(f"{'PASS' if ok else 'FAIL'} {name}: {detail}")


def gucs(who: str, reason: str = "reason one", correlation: str = "corr-1") -> str:
    # Historical scratch session. app.admin_type here is not a product authorization read.
    if who == "sa":
        return f"""
        SELECT set_config('app.admin_type','super', true);
        SELECT set_config('app.partner_id','', true);
        SELECT set_config('app.actor_user_id','{SA}', true);
        SELECT set_config('app.correlation_id','{correlation}', true);
        SELECT set_config('app.reason','{reason}', true);
        """
    partner = PA if who == "pa" else PB
    actor = ACTOR_A if who == "pa" else ACTOR_B
    return f"""
    SELECT set_config('app.admin_type','partner', true);
    SELECT set_config('app.partner_id','{partner}', true);
    SELECT set_config('app.actor_user_id','{actor}', true);
    SELECT set_config('app.correlation_id','{correlation}', true);
    SELECT set_config('app.reason','{reason}', true);
    """


def create(entry: str, family: str, cls: str, owner: str | None, code: str, payload: str, count: int = 1, active: str = "true") -> str:
    own = "NULL" if owner is None else f"'{owner}'::uuid"
    return (
        f"SELECT catalog.apply_entry_create('{entry}'::uuid, '{family}', '{cls}', {own}, "
        f"'{code}', {active}, {payload}, {count});"
    )


FEE_JSON = """'{"definition":"card surcharge","display_name":"Surcharge"}'::jsonb"""


def expect_error(name: str, user: str, body: str, sqlstate: str, message: str) -> None:
    out = psql(user, "\\set VERBOSITY verbose\nBEGIN;\n" + body + "\nROLLBACK;\n")
    ok = f"ERROR:  {sqlstate}:" in out and message in out
    record(name, ok, f"state={sqlstate} message={message}" if ok else out[-800:])


def expect_ok(name: str, user: str, body: str) -> None:
    out = psql(user, "\\set VERBOSITY verbose\n" + body)
    ok = "ERROR:" not in out
    record(name, ok, "ok" if ok else out[-800:])


def guard() -> None:
    name = scalar(OWNER, "SELECT current_database();")
    if name != DB:
        raise SystemExit(f"refusing database {name}")
    owner = scalar(OWNER, "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database();")
    if owner != OWNER:
        raise SystemExit(f"refusing owner {owner}")
    attrs = scalar(SCRATCH, "SELECT (NOT rolsuper AND NOT rolbypassrls AND rolcanlogin AND NOT rolcreatedb)::text FROM pg_roles WHERE rolname = current_user;")
    if attrs != "true":
        raise SystemExit(f"scratch attributes {attrs}")


def static_checks() -> None:
    sql = "\n".join((ROOT / "src/Auth.Data/Migrations" / n).read_text() for n in [
        "CatalogMasterData.Up.sql", "CatalogMasterData.Functions.sql", "CatalogMasterData.Behavior.sql",
        "CatalogMasterData.Triggers.sql", "CatalogMasterData.Grants.sql", "CatalogMasterData.Down.sql"])
    banned = ["PAX", "Genmega", "WorldPay", "Fiserv", "Visa", "Mastercard", "Discover", "Amex"]
    hits = [w for w in banned if w in sql]
    record("P2-seed-names", not hits, "none" if not hits else ",".join(hits))
    record("L4-no-cause-edge", "CREATE TABLE catalog.cause" not in sql and "cascade_walk" not in sql and "dependency_edge" not in sql, "absent")
    record("no-economic-role-column-text", "economic_role text" not in sql and "economic_role TEXT" not in sql, "absent")
    marker = scalar(OWNER, """
      SELECT string_agg(prosrc, '\n') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE n.nspname = 'catalog' AND proname IN ('_sealed_marker','_insert_marker','_seal_marker','tg_tx_marker_stmt')
    """)
    record("M4-marker-source", "pg_current_xact_id" in marker and "txid_current" not in marker, "xid8 predicate")
    both = scalar(OWNER, """
      SELECT (position('apply_entry_create' in prosrc) > 0)::text
      FROM pg_proc WHERE proname = '_insert_marker'
    """)
    # Both definers call _insert_marker. Confirm each body does.
    bodies = scalar(OWNER, """
      SELECT string_agg(proname || ':' || (position('_insert_marker' in prosrc) > 0)::text, ',')
      FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE n.nspname = 'catalog' AND proname IN ('apply_entry_create','apply_claim_edit')
    """)
    record("marker-both-definers", "apply_entry_create:true" in bodies and "apply_claim_edit:true" in bodies, bodies)
    trig = scalar(OWNER, """
      SELECT string_agg(c.relname, ',' ORDER BY c.relname)
      FROM pg_trigger t
      JOIN pg_class c ON c.oid = t.tgrelid
      JOIN pg_proc p ON p.oid = t.tgfoid
      WHERE p.proname = 'tg_tx_marker_stmt' AND NOT t.tgisinternal
    """)
    forbidden = {"tx_marker", "edit_coord", "history_pending", "entry_change"}
    record("marker-trigger-tables", not (set(trig.split(",")) & forbidden) and "registered_entry" in trig and "processor" in trig, trig)
    col = scalar(OWNER, """
      SELECT count(*) FROM information_schema.columns
      WHERE table_schema = 'catalog' AND table_name = 'fee_type' AND column_name = 'economic_role'
    """)
    record("fee-type-no-economic-role", col == "0", col)
    default = scalar(OWNER, """
      SELECT coalesce(column_default, 'null') FROM information_schema.columns
      WHERE table_schema = 'catalog' AND table_name = 'registered_entry' AND column_name = 'is_active'
    """)
    record("is-active-no-default", default == "null", default)
    unknown = scalar(OWNER, """
      SELECT count(*) FROM information_schema.columns
      WHERE table_schema = 'catalog' AND table_name = 'card_funding_class' AND column_name = 'is_classification_unknown'
    """)
    record("funding-unknown-column-remains", unknown == "1", unknown)


def down_empty() -> None:
    out = psql(OWNER, f"\\set VERBOSITY verbose\nBEGIN;\n\\i {DOWN}\nSELECT 'schema_left' FROM pg_namespace WHERE nspname = 'catalog';\nROLLBACK;\n", stop=True)
    ok = "ERROR:" not in out and "schema_left" not in out
    still = scalar(OWNER, "SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog'")
    auth = scalar(OWNER, "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'auth' AND c.relkind = 'r'")
    record("DOWN-R1-empty-rollback", ok and still == "1" and int(auth) > 0, f"rolled_back schema={still} auth_tables={auth}")


def down_audit() -> None:
    def one(detail: str, event_type: str, expect_raise: bool, label: str) -> None:
        eid = "99999999-9999-4999-8999-999999999999"
        sql = f"""
        \\set VERBOSITY verbose
        BEGIN;
        INSERT INTO auth.audit_outbox (event_id, occurred_at, event_type, envelope)
        VALUES ('{eid}'::uuid, clock_timestamp(), '{event_type}',
          jsonb_build_object('event_type','{event_type}','event_id','{eid}','details', {detail}));
        \\i {DOWN}
        ROLLBACK;
        """
        out = psql(OWNER, sql)
        raised = "catalog down refused: catalog audit" in out
        dropped = "DROP SCHEMA" in out
        schema = scalar(OWNER, "SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog'")
        ok = schema == "1" and raised == expect_raise and (dropped if not expect_raise else not dropped)
        record(label, ok, f"raised={raised} dropped={dropped} schema={schema}")
    one("jsonb_build_object('catalog_entry_id','x')", "auth.provider.changed", True, "DOWN-R2-audit-catalog-entry")
    one("jsonb_build_object('family','fee_type')", "catalog.entry.created", True, "DOWN-R2-audit-event-family")
    one("jsonb_build_object('subject','catalog')", "auth.provider.changed", False, "DOWN-R2-subject-only-not-audit")


def down_external() -> None:
    sql = f"""
    \\set VERBOSITY verbose
    BEGIN;
    CREATE TABLE public.dec161_ext_dep (id uuid PRIMARY KEY, entry_id uuid REFERENCES catalog.registered_entry(entry_id));
    \\i {DOWN}
    ROLLBACK;
    """
    out = psql(OWNER, sql)
    schema = scalar(OWNER, "SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog'")
    gone = scalar(OWNER, "SELECT count(*) FROM pg_class WHERE relname = 'dec161_ext_dep'")
    record("DOWN-R3-external-fk", "catalog down refused: external dependent" in out and "public.dec161_ext_dep" in out and schema == "1" and gone == "0", out[-500:])


def harness() -> None:
    sql = f"""
    GRANT USAGE ON SCHEMA catalog TO {SCRATCH};
    GRANT EXECUTE ON FUNCTION catalog.apply_entry_create(uuid, text, text, uuid, text, boolean, jsonb, integer) TO {SCRATCH};
    GRANT EXECUTE ON FUNCTION catalog.apply_claim_edit(uuid, bigint, jsonb) TO {SCRATCH};
    CREATE FUNCTION catalog._proof_marker(p_entry uuid, OUT sealed boolean, OUT matches boolean, OUT typ text)
      LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $fn$
    BEGIN
      SELECT m.sealed, m.txn_id = pg_current_xact_id(), pg_typeof(m.txn_id)::text
      INTO sealed, matches, typ
      FROM catalog.tx_marker m WHERE m.entry_id = p_entry;
    END $fn$;
    REVOKE ALL ON FUNCTION catalog._proof_marker(uuid) FROM PUBLIC;
    GRANT EXECUTE ON FUNCTION catalog._proof_marker(uuid) TO {SCRATCH};
    """
    expect_ok("harness-execute-only", OWNER, sql)
    extra = scalar(OWNER, f"""
      SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
      WHERE n.nspname = 'catalog'
        AND has_function_privilege('{SCRATCH}', p.oid, 'EXECUTE')
        AND proname NOT IN ('apply_entry_create','apply_claim_edit','_proof_marker')
    """)
    record("harness-no-other-execute", extra == "0", extra)


def actor_proofs() -> None:
    fee = create(FEE_B, "fee_type", "descriptive", PA, "otherfee", FEE_JSON)
    expect_error("P2-partner-outside-six", SCRATCH, gucs("pa") + create(FEE_B, "currency", "descriptive", None, "usd",
        """'{"iso_alpha3":"USD","iso_numeric3":"840","minor_unit":2,"display_name":"US Dollar"}'::jsonb"""), "P0001", "catalog_create_actor")
    expect_error("P3-partner-claim", SCRATCH, gucs("pa") + create(PCLAIM, "model_hardware_claim", "model_evidence", PA, "claimx",
        """'{"claim_value":"x","availability":"standard","option_key":"k","source_version":"1","checked_at":"2026-01-01T00:00:00Z","display_name":"X"}'::jsonb"""), "P0001", "catalog_create_actor")
    expect_error("P4-partner-release", SCRATCH, gucs("pa") + create(REL, "catalog_release", "provenance", None, "relx",
        """'{"release_family":"vendor","version":"1","imported_at":"2026-01-01T00:00:00Z","display_name":"Rel"}'::jsonb"""), "P0001", "catalog_create_actor")
    expect_error("P5-partner-global-six", SCRATCH, gucs("pa") + create(FEE_B, "fee_type", "descriptive", None, "globalfee", FEE_JSON), "P0001", "catalog_create_actor")
    expect_error("P5-partner-other-owner", SCRATCH, gucs("pa") + create(FEE_B, "fee_type", "descriptive", PB, "otherown", FEE_JSON), "P0001", "catalog_create_actor")
    expect_error("A2-two-registry-rows", SCRATCH, gucs("pa") + create(FEE_B, "fee_type", "descriptive", PA, "tworows", FEE_JSON, count=2), "P0001", "catalog_create_row_count")
    expect_error("actor-context-missing", SCRATCH, create(FEE_B, "fee_type", "descriptive", PA, "nocontext", FEE_JSON), "P0001", "catalog_actor_context")
    expect_error("economic-role-unknown-field", SCRATCH, gucs("pa") + create(FEE_B, "fee_type", "descriptive", PA, "econ",
        """'{"definition":"x","display_name":"X","economic_role":"customer_charge"}'::jsonb"""), "P0001", "catalog_column_matrix_unsettled")
    left = scalar(OWNER, f"SELECT count(*) FROM catalog.registered_entry WHERE entry_id = '{FEE_B}'")
    record("denials-write-nothing", left == "0", left)
    expect_error("P7-admin-execute-create", API, gucs("sa") + fee, "42501", "permission denied")
    expect_error("E8-admin-execute-edit", API, gucs("sa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 1, '{{\"claim_value\":\"z\"}}'::jsonb);", "42501", "permission denied")
    expect_error("login-no-select", LOGIN, "SELECT count(*) FROM catalog.registered_entry;", "42501", "permission denied")


def create_fee() -> None:
    body = f"""
    BEGIN;
    {gucs('pa', reason='original reason', correlation='corr-create')}
    {create(FEE, 'fee_type', 'descriptive', PA, 'surcharge', FEE_JSON)}
    SELECT sealed::text || ' ' || typ || ' ' || matches::text FROM catalog._proof_marker('{FEE}'::uuid);
    SELECT set_config('app.actor_user_id','{ACTOR_B}', true);
    SELECT set_config('app.reason','replaced reason', true);
    SELECT set_config('app.correlation_id','corr-replaced', true);
    COMMIT;
    """
    out = psql(SCRATCH, "\\set VERBOSITY verbose\n" + body)
    sealed = "true xid8 true" in out
    record("M1-A1-sealed-after-return", sealed and "ERROR:" not in out, out[-400:] if "ERROR:" in out else "sealed xid8 matches")
    hist = scalar(OWNER, f"""
      SELECT change_kind || ' ' || actor_user_id::text || ' ' || coalesce(reason,'') || ' ' || source || ' ' || (before IS NULL)::text
      FROM catalog.entry_change WHERE entry_id = '{FEE}'
    """)
    record("H5-actor-not-reread", hist == f"created {ACTOR_A} original reason corr-create true", hist)
    match = scalar(OWNER, f"SELECT count(*) FROM catalog.tx_marker WHERE entry_id = '{FEE}' AND txn_id = pg_current_xact_id()")
    record("M4-old-marker-does-not-match", match == "0", match)
    rows = scalar(OWNER, f"SELECT count(*) FROM catalog.fee_type WHERE entry_id = '{FEE}'")
    record("A1-one-subtype", rows == "1", rows)


def second_statement() -> None:
    body = f"""
    {gucs('pa')}
    {create(FEE_B, 'fee_type', 'descriptive', PA, 'secondfee', FEE_JSON)}
    {create('10000000-0000-4000-8000-000000000099', 'fee_type', 'descriptive', PA, 'thirdfee', FEE_JSON)}
    """
    expect_error("M2-K2a-second-statement", SCRATCH, body, "P0001", "catalog_transaction_action")
    left = scalar(OWNER, "SELECT count(*) FROM catalog.registered_entry WHERE code IN ('secondfee','thirdfee')")
    record("K2a-no-committed-second", left == "0", left)


def savepoint_and_constraints() -> None:
    sv = f"""
    BEGIN;
    {gucs('pa')}
    SAVEPOINT sv;
    {create(FEE_B, 'fee_type', 'descriptive', PA, 'savefee', FEE_JSON)}
    SELECT 'during ' || sealed::text FROM catalog._proof_marker('{FEE_B}'::uuid);
    ROLLBACK TO sv;
    COMMIT;
    """
    out = psql(SCRATCH, "\\set VERBOSITY verbose\n" + sv)
    left = scalar(OWNER, f"""
      SELECT (SELECT count(*) FROM catalog.registered_entry WHERE entry_id = '{FEE_B}')::text
        || ' ' || (SELECT count(*) FROM catalog.tx_marker WHERE entry_id = '{FEE_B}')::text
    """)
    record("SV1-savepoint", "ERROR:" not in out and "during true" in out and left == "0 0", left if left != "0 0" else "cleared together")
    m5 = f"""
    BEGIN;
    {gucs('pa')}
    {create(FEE_B, 'fee_type', 'descriptive', PA, 'setfee', FEE_JSON)}
    SET CONSTRAINTS ALL IMMEDIATE;
    SELECT 'still ' || sealed::text || ' ' || matches::text FROM catalog._proof_marker('{FEE_B}'::uuid);
    ROLLBACK;
    """
    out = psql(SCRATCH, "\\set VERBOSITY verbose\n" + m5)
    record("M5-set-constraints-keeps-marker", "ERROR:" not in out and "still true true" in out, "kept" if "still true true" in out else out[-400:])


def direct_and_cycle() -> None:
    expect_error("K2n-scratch-processor-insert", SCRATCH, """
      INSERT INTO catalog.processor (entry_id, family, code, provider_name, display_name)
      VALUES ('10000000-0000-4000-8000-000000000077'::uuid, 'processor', 'rawp', 'Raw', 'Raw');
    """, "42501", "permission denied")
    expect_error("K1-self-parent", SCRATCH, gucs("sa") + create(
        "10000000-0000-4000-8000-000000000041", "processor", "descriptive", None, "selfp",
        """'{"provider_name":"Self","display_name":"Self","parent_global_id":"10000000-0000-4000-8000-000000000041"}'::jsonb"""),
        "P0001", "catalog_processor_cycle")
    k2b = f"""
    \\set VERBOSITY verbose
    BEGIN;
    {gucs('sa')}
    INSERT INTO catalog.registered_entry (entry_id, family, entry_class, owner_partner_id, code, is_active, row_version, created_by, created_at, updated_by, updated_at)
    VALUES
      ('10000000-0000-4000-8000-000000000051'::uuid, 'processor', 'descriptive', NULL, 'proca', true, 1, '{SA}'::uuid, clock_timestamp(), '{SA}'::uuid, clock_timestamp()),
      ('10000000-0000-4000-8000-000000000052'::uuid, 'processor', 'descriptive', NULL, 'procb', true, 1, '{SA}'::uuid, clock_timestamp(), '{SA}'::uuid, clock_timestamp());
    INSERT INTO catalog.processor (entry_id, family, code, owner_partner_id, provider_name, display_name, parent_global_id, parent_global_family)
    VALUES
      ('10000000-0000-4000-8000-000000000051'::uuid, 'processor', 'proca', NULL, 'A', 'A', '10000000-0000-4000-8000-000000000052'::uuid, 'processor'),
      ('10000000-0000-4000-8000-000000000052'::uuid, 'processor', 'procb', NULL, 'B', 'B', '10000000-0000-4000-8000-000000000051'::uuid, 'processor');
    COMMIT;
    """
    out = psql(OWNER, k2b)
    left = scalar(OWNER, "SELECT count(*) FROM catalog.registered_entry WHERE code IN ('proca','procb')")
    record("K2b-owner-cycle", "catalog_processor_cycle" in out and left == "0", f"left={left}")


def commit_one(name: str, who: str, statement: str) -> None:
    expect_ok(name, SCRATCH, f"BEGIN;\n{gucs(who)}\n{statement}\nCOMMIT;\n")


def reference_graph() -> None:
    commit_one("P6-category", "sa", create(CAT, "device_category", "descriptive", None, "kiosk", """'{"definition":"A kiosk","display_name":"Kiosk"}'::jsonb"""))
    commit_one("P6-manufacturer", "sa", create(MFR, "manufacturer", "descriptive", None, "acme", """'{"legal_name":"Acme","display_name":"Acme"}'::jsonb"""))
    commit_one("P6-model", "sa", create(MODEL, "device_model", "descriptive", None, "m1",
        f"""'{{"model_name":"M1","manufacturer_global_id":"{MFR}","category_entry_id":"{CAT}","display_name":"M1"}}'::jsonb"""))
    commit_one("P6-capability", "sa", create(CAP, "hardware_capability_kind", "descriptive", None, "nfc", """'{"measurement_kind":"boolean","display_name":"NFC"}'::jsonb"""))
    commit_one("P6-claim", "sa", create(CLAIM, "model_hardware_claim", "model_evidence", None, "nfcclaim",
        f"""'{{"model_global_id":"{MODEL}","capability_entry_id":"{CAP}","claim_value":"yes","availability":"standard","option_key":"nfc","source_version":"1","checked_at":"2026-01-01T00:00:00Z","display_name":"NFC"}}'::jsonb"""))
    commit_one("P6-release", "sa", create(REL, "catalog_release", "provenance", None, "rel1", """'{"release_family":"vendor","version":"2026.1","imported_at":"2026-01-01T00:00:00Z","display_name":"Rel 1"}'::jsonb"""))
    missing = "SELECT catalog.apply_claim_edit('10000000-0000-4000-8000-000000000088'::uuid, 1, '{\"claim_value\":\"no\"}'::jsonb);"
    expect_error("E7a-missing", SCRATCH, gucs("pa") + missing, "23503", "catalog_reference_not_found")
    expect_error("E7c-visible-global-partner", SCRATCH, gucs("pa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 1, '{{\"claim_value\":\"no\"}}'::jsonb);", "P0001", "catalog_claim_edit_actor")
    expect_error("E7d-wrong-version", SCRATCH, gucs("sa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 9, '{{\"claim_value\":\"no\"}}'::jsonb);", "P0001", "catalog_version_conflict")
    missing_mfr = create("10000000-0000-4000-8000-000000000061", "device_model", "descriptive", None, "missingm",
        f"""'{{"model_name":"H","manufacturer_global_id":"10000000-0000-4000-8000-000000000062","category_entry_id":"{CAT}","display_name":"H"}}'::jsonb""")
    expect_error("N1-missing-manufacturer", SCRATCH, gucs("sa") + missing_mfr, "23503", "catalog_reference_not_found")


def private_claim() -> None:
    bmfr = "10000000-0000-4000-8000-000000000024"
    commit_one("private-manufacturer", "pa", create(PMFR, "manufacturer", "descriptive", PA, "acmep", """'{"legal_name":"Acme Private","display_name":"Acme P"}'::jsonb"""))
    commit_one("private-model", "pa", create(PMODEL, "device_model", "descriptive", PA, "pm1",
        f"""'{{"model_name":"PM1","manufacturer_private_id":"{PMFR}","manufacturer_private_owner":"{PA}","category_entry_id":"{CAT}","display_name":"PM1"}}'::jsonb"""))
    commit_one("pb-manufacturer", "pb", create(bmfr, "manufacturer", "descriptive", PB, "betaco", """'{"legal_name":"Beta","display_name":"Beta"}'::jsonb"""))
    hidden = create("10000000-0000-4000-8000-000000000063", "device_model", "descriptive", PA, "hidm",
        f"""'{{"model_name":"Hid","manufacturer_private_id":"{bmfr}","manufacturer_private_owner":"{PA}","category_entry_id":"{CAT}","display_name":"Hid"}}'::jsonb""")
    expect_error("N1-hidden-manufacturer", SCRATCH, gucs("pa") + hidden, "23503", "catalog_reference_not_found")
    commit_one("P6-sa-private-claim", "sa", create(PCLAIM, "model_hardware_claim", "model_evidence", PA, "pclaim",
        f"""'{{"model_private_id":"{PMODEL}","model_private_owner":"{PA}","capability_entry_id":"{CAP}","claim_value":"no","availability":"optional","option_key":"nfc","source_version":"1","checked_at":"2026-01-01T00:00:00Z","display_name":"NFC private"}}'::jsonb"""))


def lock_proofs() -> None:
    holder_sql = f"""
    SET application_name = 'dec161_holder';
    BEGIN;
    SELECT entry_id FROM catalog.registered_entry WHERE entry_id IN ('{CLAIM}','{PCLAIM}','{FEE}') FOR UPDATE;
    SELECT pg_sleep(25);
    COMMIT;
    """
    holder = subprocess.Popen(
        [PSQL, "-h", HOST, "-p", PORT, "-U", OWNER, "-d", DB, "-f", "-"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    assert holder.stdin is not None
    holder.stdin.write(holder_sql)
    holder.stdin.close()
    time.sleep(0.6)

    def timed(name: str, user: str, body: str, sqlstate: str, message: str, fast: bool) -> None:
        t0 = time.perf_counter()
        out = psql(user, "\\set VERBOSITY verbose\nBEGIN;\nSET statement_timeout = '2000ms';\n" + body + "\nROLLBACK;\n")
        dt = time.perf_counter() - t0
        state_ok = f"ERROR:  {sqlstate}:" in out and message in out
        time_ok = dt < 1.5 if fast else 1.5 <= dt < 6
        record(name, state_ok and time_ok, f"dt={dt:.2f}s")

    timed("E7c-locked-no-wait", SCRATCH, gucs("pa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 1, '{{\"claim_value\":\"no\"}}'::jsonb);", "P0001", "catalog_claim_edit_actor", True)
    timed("E7b-hidden-no-wait", SCRATCH, gucs("pb") + f"SELECT catalog.apply_claim_edit('{PCLAIM}'::uuid, 1, '{{\"claim_value\":\"yes\"}}'::jsonb);", "23503", "catalog_reference_not_found", True)
    timed("E7a-missing-no-wait", SCRATCH, gucs("pa") + "SELECT catalog.apply_claim_edit('10000000-0000-4000-8000-000000000087'::uuid, 1, '{\"claim_value\":\"no\"}'::jsonb);", "23503", "catalog_reference_not_found", True)
    timed("FLAG-R1-hidden-update", SCRATCH, f"UPDATE catalog.registered_entry SET is_active = false WHERE entry_id = '{FEE}';", "42501", "permission denied", True)
    timed("FLAG-R1-missing-update", SCRATCH, "UPDATE catalog.registered_entry SET is_active = false WHERE entry_id = '10000000-0000-4000-8000-000000000086';", "42501", "permission denied", True)
    timed("E7d-wrong-version-waits", SCRATCH, gucs("sa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 9, '{{\"claim_value\":\"no\"}}'::jsonb);", "57014", "statement timeout", False)
    psql(OWNER, "SELECT pg_cancel_backend(pid) FROM pg_stat_activity WHERE application_name = 'dec161_holder';")
    holder.wait(timeout=10)


def claim_edit() -> None:
    body = f"""
    {gucs('pa', correlation='corr-edit')}
    SELECT catalog.apply_claim_edit('{PCLAIM}'::uuid, 1, '{{"claim_value":"yes"}}'::jsonb);
    SELECT catalog.apply_claim_edit('{PCLAIM}'::uuid, 2, '{{"claim_value":"later"}}'::jsonb);
    """
    expect_error("M3-statement-after-edit", SCRATCH, body, "P0001", "catalog_transaction_action")
    body = f"""
    BEGIN;
    {gucs('pa', correlation='corr-edit')}
    SELECT catalog.apply_claim_edit('{PCLAIM}'::uuid, 1, '{{"claim_value":"yes"}}'::jsonb);
    COMMIT;
    """
    expect_ok("E9-owning-partner-edit", SCRATCH, body)
    hist = scalar(OWNER, f"""
      SELECT count(*) FILTER (WHERE change_kind = 'updated') || ' ' ||
             count(*) FILTER (WHERE change_kind = 'created') || ' ' ||
             min(row_version) FILTER (WHERE change_kind = 'updated') || ' ' ||
             bool_and(before->'subtype'->>'claim_value' = 'no') FILTER (WHERE change_kind = 'updated') || ' ' ||
             bool_and(after->'subtype'->>'claim_value' = 'yes') FILTER (WHERE change_kind = 'updated')
      FROM catalog.entry_change WHERE entry_id = '{PCLAIM}'
    """)
    record("E9-one-updated-history", hist == "1 1 2 true true", hist)
    version = scalar(OWNER, f"SELECT row_version::text || ' ' || is_active::text FROM catalog.registered_entry WHERE entry_id = '{PCLAIM}'")
    record("E9-version-and-flag", version == "2 true", version)
    coord = scalar(OWNER, f"SELECT count(*) FROM catalog.edit_coord WHERE entry_id = '{PCLAIM}'")
    record("E9-coord-consumed", coord == "0", coord)
    expect_error("noop", SCRATCH, gucs("pa") + f"SELECT catalog.apply_claim_edit('{PCLAIM}'::uuid, 2, '{{\"claim_value\":\"yes\"}}'::jsonb);", "P0001", "catalog_noop")
    expect_error("patch-is-active", SCRATCH, gucs("sa") + f"SELECT catalog.apply_claim_edit('{CLAIM}'::uuid, 1, '{{\"is_active\":false}}'::jsonb);", "P0001", "catalog_column_matrix_unsettled")
    expect_error("E5-direct-subtype", SCRATCH, f"UPDATE catalog.model_hardware_claim SET claim_value = 'forged' WHERE entry_id = '{PCLAIM}';", "42501", "permission denied")
    expect_error("E6-direct-claim-update", SCRATCH, f"UPDATE catalog.model_hardware_claim SET display_name = 'forged' WHERE entry_id = '{CLAIM}';", "42501", "permission denied")
    expect_error("owner-flag-absent", OWNER, gucs("sa") + f"UPDATE catalog.registered_entry SET is_active = false WHERE entry_id = '{FEE}';", "P0001", "catalog_flag_definer_absent")


def grants() -> None:
    g1 = """
    DO $g1$
    DECLARE r record; col text;
    BEGIN
      FOR r IN
        SELECT c.relname, c.oid FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'catalog' AND c.relkind = 'r'
      LOOP
        BEGIN
          EXECUTE format('INSERT INTO catalog.%I DEFAULT VALUES', r.relname);
          RAISE EXCEPTION 'insert succeeded %', r.relname;
        EXCEPTION WHEN insufficient_privilege THEN NULL;
        END;
        BEGIN
          EXECUTE format('DELETE FROM catalog.%I WHERE false', r.relname);
          RAISE EXCEPTION 'delete succeeded %', r.relname;
        EXCEPTION WHEN insufficient_privilege THEN NULL;
        END;
        BEGIN
          EXECUTE format('TRUNCATE catalog.%I', r.relname);
          RAISE EXCEPTION 'truncate succeeded %', r.relname;
        EXCEPTION WHEN insufficient_privilege THEN NULL;
        END;
        SELECT a.attname INTO col FROM pg_attribute a
        WHERE a.attrelid = r.oid AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum LIMIT 1;
        BEGIN
          EXECUTE format('UPDATE catalog.%I SET %I = %I WHERE false', r.relname, col, col);
          RAISE EXCEPTION 'update succeeded %', r.relname;
        EXCEPTION WHEN insufficient_privilege THEN NULL;
        END;
      END LOOP;
    END
    $g1$;
    """
    expect_ok("G1-admin-dml", API, g1)
    expect_error("G5-scratch-select-coord", SCRATCH, "SELECT count(*) FROM catalog.edit_coord;", "42501", "permission denied")
    expect_error("H1-scratch-history", SCRATCH, "INSERT INTO catalog.entry_change DEFAULT VALUES; INSERT INTO catalog.history_pending DEFAULT VALUES;", "42501", "permission denied")
    psql(OWNER, f"GRANT INSERT, UPDATE, DELETE, TRUNCATE, SELECT ON catalog.edit_coord TO {SCRATCH};", stop=True)
    out = psql(SCRATCH, f"""
      \\set VERBOSITY verbose
      INSERT INTO catalog.edit_coord (entry_id, txn_id, expected_version, before, patch)
      VALUES ('{PCLAIM}'::uuid, 1, 1, '{{}}'::jsonb, '{{}}'::jsonb);
    """)
    psql(OWNER, f"REVOKE ALL ON catalog.edit_coord FROM {SCRATCH};", stop=True)
    record("G5p-grant-still-denied", "ERROR:  42501:" in out and "row-level security" in out, out[-400:])
    seen = scalar(API, f"""
      BEGIN;
      SELECT set_config('app.admin_type','partner', true);
      SELECT set_config('app.partner_id','{PB}', true);
      SELECT count(*) FROM catalog.registered_entry WHERE entry_id IN ('{PCLAIM}','{PMODEL}','{FEE}');
      COMMIT;
    """)
    record("H4-N2-hidden-from-other-partner", [ln.strip() for ln in seen.splitlines() if ln.strip().isdigit()][-1] == "0", seen)
    own = scalar(API, f"""
      BEGIN;
      SELECT set_config('app.admin_type','partner', true);
      SELECT set_config('app.partner_id','{PA}', true);
      SELECT count(*) FROM catalog.registered_entry WHERE entry_id IN ('{FEE}','{PCLAIM}');
      COMMIT;
    """)
    record("partner-sees-own", [ln.strip() for ln in own.splitlines() if ln.strip().isdigit()][-1] == "2", own)


def concurrency() -> None:
    a = f"""
    \\set VERBOSITY verbose
    BEGIN;
    {gucs('sa', correlation='corr-c1a')}
    {create(GSHARE, 'fee_type', 'descriptive', None, 'sharedcode', FEE_JSON)}
    SELECT pg_sleep(2);
    COMMIT;
    """
    b = f"""
    \\set VERBOSITY verbose
    SELECT pg_sleep(0.4);
    BEGIN;
    {gucs('sa', correlation='corr-c1b')}
    {create(PSHARE, 'fee_type', 'descriptive', PA, 'sharedcode', FEE_JSON)}
    COMMIT;
    """
    pa = subprocess.Popen([PSQL, "-h", HOST, "-p", PORT, "-U", SCRATCH, "-d", DB, "-f", "-"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    pb = subprocess.Popen([PSQL, "-h", HOST, "-p", PORT, "-U", SCRATCH, "-d", DB, "-f", "-"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    box: dict[str, tuple[str, str]] = {}
    def go(name: str, proc: subprocess.Popen[str], sql: str) -> None:
        out, err = proc.communicate(input=sql, timeout=20)
        box[name] = (out, err)
    ta = threading.Thread(target=go, args=("a", pa, a))
    tb = threading.Thread(target=go, args=("b", pb, b))
    ta.start()
    time.sleep(0.3)
    tb.start()
    time.sleep(0.8)
    locks = scalar(OWNER, "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'")
    ta.join(timeout=20)
    tb.join(timeout=20)
    oa, ea = box["a"]
    ob, eb = box["b"]
    both = scalar(OWNER, f"SELECT count(*) FROM catalog.registered_entry WHERE entry_id IN ('{GSHARE}','{PSHARE}')")
    ok = pa.returncode == 0 and pb.returncode == 0 and "ERROR:" not in (oa + ea + ob + eb) and both == "2" and locks == "0"
    record("C1-same-code-global-and-private", ok, f"rows={both} advisory={locks} a={(ea or oa)[-200:]}")


def k3() -> None:
    # Literal mutual parents cannot commit: the first commit cannot see the other transaction's row.
    t1 = f"""
    SET application_name = 'dec161_k3_t1';
    \\set VERBOSITY verbose
    BEGIN;
    {gucs('sa')}
    INSERT INTO catalog.registered_entry (entry_id, family, entry_class, code, is_active, row_version, created_by, created_at, updated_by, updated_at)
    VALUES ('10000000-0000-4000-8000-000000000071'::uuid, 'processor', 'descriptive', 'cyc1', true, 1, '{SA}'::uuid, clock_timestamp(), '{SA}'::uuid, clock_timestamp());
    INSERT INTO catalog.processor (entry_id, family, code, provider_name, display_name, parent_global_id, parent_global_family)
    VALUES ('10000000-0000-4000-8000-000000000071'::uuid, 'processor', 'cyc1', 'C1', 'C1', '10000000-0000-4000-8000-000000000072'::uuid, 'processor');
    SELECT pg_sleep(4);
    COMMIT;
    """
    t2 = f"""
    \\set VERBOSITY verbose
    SELECT pg_sleep(0.5);
    BEGIN;
    SET statement_timeout = '2000ms';
    {gucs('sa')}
    INSERT INTO catalog.registered_entry (entry_id, family, entry_class, code, is_active, row_version, created_by, created_at, updated_by, updated_at)
    VALUES ('10000000-0000-4000-8000-000000000072'::uuid, 'processor', 'descriptive', 'cyc2', true, 1, '{SA}'::uuid, clock_timestamp(), '{SA}'::uuid, clock_timestamp());
    INSERT INTO catalog.processor (entry_id, family, code, provider_name, display_name, parent_global_id, parent_global_family)
    VALUES ('10000000-0000-4000-8000-000000000072'::uuid, 'processor', 'cyc2', 'C2', 'C2', '10000000-0000-4000-8000-000000000071'::uuid, 'processor');
    COMMIT;
    """
    p1 = subprocess.Popen([PSQL, "-h", HOST, "-p", PORT, "-U", OWNER, "-d", DB, "-f", "-"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    p2 = subprocess.Popen([PSQL, "-h", HOST, "-p", PORT, "-U", OWNER, "-d", DB, "-f", "-"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    box: dict[str, tuple[str, str]] = {}
    def go(name: str, proc: subprocess.Popen[str], sql: str) -> None:
        out, err = proc.communicate(input=sql, timeout=20)
        box[name] = (out, err)
    t_one = threading.Thread(target=go, args=("t1", p1, t1))
    t_two = threading.Thread(target=go, args=("t2", p2, t2))
    t_one.start()
    time.sleep(0.3)
    t_two.start()
    t_two.join(timeout=20)
    t_one.join(timeout=20)
    o1, e1 = box["t1"]
    o2, e2 = box["t2"]
    left = scalar(OWNER, "SELECT count(*) FROM catalog.registered_entry WHERE code IN ('cyc1','cyc2')")
    waited = "57014" in (o2 + e2) or "statement timeout" in (o2 + e2)
    t1_failed = "catalog_reference_not_found" in (o1 + e1) and "violates foreign key" not in (o1 + e1)
    record("K3-lock-then-not-a-committed-cycle", waited and t1_failed and left == "0", f"waited={waited} t1_failed={t1_failed} left={left} t1={ (o1+e1)[-300:] }")


def down_populated() -> None:
    out = psql(OWNER, f"\\set VERBOSITY verbose\nBEGIN;\n\\i {DOWN}\nROLLBACK;\n")
    schema = scalar(OWNER, "SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog'")
    rows = scalar(OWNER, "SELECT count(*) FROM catalog.registered_entry")
    record("DOWN-R2-populated", "catalog down refused: populated" in out and schema == "1" and int(rows) > 0, f"rows={rows}")


def main() -> int:
    guard()
    static_checks()
    down_empty()
    down_audit()
    down_external()
    harness()
    actor_proofs()
    create_fee()
    second_statement()
    savepoint_and_constraints()
    direct_and_cycle()
    reference_graph()
    private_claim()
    lock_proofs()
    claim_edit()
    grants()
    concurrency()
    k3()
    down_populated()
    failed = [name for name, ok, _ in results if not ok]
    print(f"\n{len(results) - len(failed)} passed, {len(failed)} failed")
    log = ROOT.parents[2] / "opt/cursor/artifacts/dec161-proof-log.txt"
    # ROOT is /agent/repos/FloorFunds; artifacts are absolute.
    log = Path("/opt/cursor/artifacts/dec161-proof-log.txt")
    log.write_text("\n".join(f"{'PASS' if ok else 'FAIL'}\t{name}\t{detail}" for name, ok, detail in results) + "\n")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
