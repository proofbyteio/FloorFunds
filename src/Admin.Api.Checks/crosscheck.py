#!/usr/bin/env python3
"""S2a P3: cross-check JsonSchemaLite (C#) against Python jsonschema Draft 2020-12, and API responses against
location-login-config.schema.json. Input: the dir Admin.Api.Checks was given (corpus.jsonl, responses.jsonl).
No new packages: the venv has jsonschema but not rfc3339-validator, so "date-time" is checked here by an RFC 3339
regex registered on the format checker (otherwise Python would silently skip it and the comparison would be one-sided)."""
import json, pathlib, re, sys
from datetime import datetime
from jsonschema import Draft202012Validator as V, FormatChecker

root = pathlib.Path(__file__).resolve().parents[2]
out = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "/tmp/sse-llc1/p3")
FC = FormatChecker()
RFC3339 = re.compile(r"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")

@FC.checks("date-time")
def _dt(v):
    if not isinstance(v, str): return True
    if not RFC3339.match(v): return False
    s = re.sub(r"[Zz]$", "+00:00", v).replace(":60", ":59")  # leap second accepted, as in the reference
    try: datetime.fromisoformat(s.replace("t", "T"))
    except ValueError: return False
    return True

audit = json.loads((root / "contract/audit-events.schema.json").read_text())
llc = json.loads((root / "contract/location-login-config.schema.json").read_text())
va = V(audit, format_checker=FC)
fails = n = 0
for line in (out / "corpus.jsonl").read_text().splitlines():
    rec = json.loads(line); n += 1
    py = va.is_valid(rec["doc"])
    if py != rec["lite_valid"]:
        fails += 1; print(f"DISAGREE {rec['label']}: lite={rec['lite_valid']} python={py}")
valid = sum(1 for l in (out / "corpus.jsonl").read_text().splitlines() if json.loads(l)["lite_valid"])
print(f"corpus: {n} docs ({valid} valid, {n - valid} invalid), {fails} disagreements")
r = 0
for line in (out / "responses.jsonl").read_text().splitlines():
    rec = json.loads(line); r += 1
    sch = {"$ref": "#/$defs/" + rec["def"], "$defs": llc["$defs"]}
    errs = sorted(e.message[:160] for e in V(sch, format_checker=FC).iter_errors(rec["body"]))
    if errs: fails += 1; print(f"RESPONSE {rec['def']} invalid: {errs}")
print(f"responses: {r} checked against location-login-config.schema.json")
print("ALL PASS" if fails == 0 and n > 0 and r > 0 else "FAILED")
sys.exit(0 if fails == 0 and n > 0 and r > 0 else 1)
