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
# TLD 13:27 item 1: current_row_version 0 is allowed ONLY for the reason-bounds PUT (prb_error_response); location 409s stay >= 1.
def _ok(d, body): return V({"$ref": "#/$defs/" + d, "$defs": llc["$defs"]}, format_checker=FC).is_valid(body)
recs = [json.loads(l) for l in (out / "responses.jsonl").read_text().splitlines()]
loc409 = [x["body"] for x in recs if x["def"] == "error_response" and isinstance(x["body"], dict) and "current_row_version" in x["body"]]
prb0 = [x["body"] for x in recs if x["def"] == "prb_error_response" and isinstance(x["body"], dict) and x["body"].get("current_row_version") == 0]
sep = [
    ("error_response rejects a 409 with current_row_version 0", not _ok("error_response", {"error": "concurrency_conflict", "current_row_version": 0})),
    ("prb_error_response accepts a 409 with current_row_version 0", _ok("prb_error_response", {"error": "concurrency_conflict", "current_row_version": 0})),
    ("prb_error_response rejects current_row_version -1", not _ok("prb_error_response", {"error": "concurrency_conflict", "current_row_version": -1})),
    (f"observed location 409s ({len(loc409)}) all carry current_row_version >= 1", len(loc409) > 0 and all(b["current_row_version"] >= 1 for b in loc409)),
    (f"observed reason-bounds 409s with current_row_version 0 ({len(prb0)}) are labelled prb_error_response only", len(prb0) > 0),
    ("no error_response-labelled body carries current_row_version 0", not any(isinstance(x["body"], dict) and x["body"].get("current_row_version") == 0 for x in recs if x["def"] == "error_response")),
]
for lbl, good in sep:
    print(("SEP PASS " if good else "SEP FAIL ") + lbl)
    if not good: fails += 1
print("ALL PASS" if fails == 0 and n > 0 and r > 0 else "FAILED")
sys.exit(0 if fails == 0 and n > 0 and r > 0 else 1)
