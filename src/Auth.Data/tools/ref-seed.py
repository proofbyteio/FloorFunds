#!/usr/bin/env python3
"""Offline seed for schema ref. Reads the locked archive only. Writes nothing on a digest miss.

Archive identity is fixed: SHA-256 7330a82bd9b4d99cbae975a943b5938adefb54b687f297b092bf375771b1813e.
The derivation matches the independently verified generator (TLD-REF1, 2026-11-06).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import struct
import sys
import tarfile
import zipfile
import xml.etree.ElementTree as ET
from pathlib import PurePosixPath

ARCHIVE_SHA256 = "7330a82bd9b4d99cbae975a943b5938adefb54b687f297b092bf375771b1813e"
ARCHIVE_SIZE = 35662359
CLDR_SHA256 = "78403d8d6fb1fed6e208bf9001bf5be00bb5abfd9f8110dae062bf49309d6a89"
ADDRESS_AGGREGATE = "6b9fdecb197299829a44bb271ecabf25e719e2f57a821cca33e263e3c26fa870"
TZ_SHA256 = "01b4a91cec5167ed48c11e8ab78f2048cead78ce94b0cafcad0ae8c5a3044d3d"
ZONE_LIST_SHA256 = "82a0d7009cd03e14d849612ca538f0b3c53fd13d3741796e698338fe6f902e88"
PREFIX = "dec145-146-refdata-20261106"

FIXED = {
    "EVIDENCE-COUNTS.txt", "GENERATOR-SPEC.md", "PROVENANCE.md", "SHA256SUMS",
    "address/data.json", "cldr/cldr-common-49.zip", "cldr/SHASUM512.txt", "cldr/region.xml",
    "cldr/en.xml", "cldr/LICENSE", "cldr/unicode-license-v3.txt", "tz/tzdata2026f.tar.gz",
    "docs/libaddressinput-README.md", "docs/libaddressinput-LICENSE",
    "docs/AddressValidationMetadata.github.html", "docs/tr35.html",
}


def sha256(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def fail(msg: str) -> None:
    print(f"ref-seed: {msg}", file=sys.stderr)
    raise SystemExit(2)


def check(got, exp, label: str) -> None:
    if got != exp:
        fail(f"{label} mismatch")


def load(path: str) -> dict[str, bytes]:
    raw = open(path, "rb").read()
    check(len(raw), ARCHIVE_SIZE, "archive size")
    check(sha256(raw), ARCHIVE_SHA256, "archive sha256")
    data: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        for m in tar.getmembers():
            p = PurePosixPath(m.name)
            if p.is_absolute() or ".." in p.parts or "\\" in m.name:
                fail("unsafe archive member")
            if len(p.parts) < 2 or p.parts[0] != PREFIX:
                fail("unexpected archive prefix")
            q = "/".join(p.parts[1:])
            if q not in FIXED and not re.fullmatch(r"address/countries/[A-Z]{2}\.json", q):
                fail(f"unexpected member {q}")
            if not m.isfile() or m.issym() or m.islnk() or q in data:
                fail(f"refusing member {q}")
            f = tar.extractfile(m)
            if f is None:
                fail(f"unreadable member {q}")
            data[q] = f.read()
    check(len(data), 269, "member count")
    manifest: dict[str, str] = {}
    for line in data["SHA256SUMS"].decode().splitlines():
        m = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        if not m:
            fail("manifest line")
        digest, rel = m.groups()
        if rel in manifest:
            fail("duplicate manifest path")
        manifest[rel] = digest
    check(len(manifest), 268, "manifest entries")
    check(set(manifest), set(data) - {"SHA256SUMS"}, "manifest inventory")
    for rel, digest in manifest.items():
        if sha256(data[rel]) != digest:
            fail(f"manifest digest {rel}")
    return data


def regular_codes(region_xml: bytes) -> set[str]:
    root = ET.fromstring(region_xml)
    regular: set[str] = set()
    for e in root.findall(".//id"):
        if e.attrib.get("type") != "region" or e.attrib.get("idStatus") != "regular":
            continue
        for token in (e.text or "").split():
            if "~" in token:
                lo, hi = token.split("~")
                hi = lo[: len(lo) - len(hi)] + hi
                if not (len(lo) == len(hi) == 2 and lo[0] == hi[0]):
                    fail(f"bad territory range {token}")
                regular.update(lo[0] + chr(c) for c in range(ord(lo[1]), ord(hi[1]) + 1))
            else:
                regular.add(token)
    check(len(regular), 257, "regular territories")
    if not all(re.fullmatch(r"[A-Z]{2}", c) for c in regular):
        fail("regular code shape")
    return regular


def names(en_xml: bytes, regular: set[str]) -> dict[str, str]:
    root = ET.fromstring(en_xml)
    en = {
        n.attrib["type"]: "".join(n.itertext())
        for n in root.findall(".//territories/territory")
        if "alt" not in n.attrib
    }
    if not regular <= en.keys():
        fail("missing English name")
    check(en["EA"], "Ceuta & Melilla", "decoded name")
    if any(ord(ch) > 0xFFFF for name in en.values() for ch in name):
        fail("non-BMP country name")
    return {c: en[c] for c in regular}


def flags(data: dict[str, bytes], regular: set[str]) -> dict[str, dict[str, bool | None]]:
    root = json.loads(data["address/data.json"])
    codes = set(root["countries"].split("~"))
    files = {PurePosixPath(p).stem for p in data if p.startswith("address/countries/")}
    check(len(codes), 252, "address root codes")
    check(files, codes | {"ZZ"}, "address files")
    check(sha256(data["address/data.json"]), "5e5390be65b639badd5b0ce4a12d8f705a635ef1e07c4894e50a51fb918fe662", "address root digest")
    agg = hashlib.sha256()
    for p in sorted((p for p in data if p.startswith("address/")), key=lambda s: s.encode()):
        pb = p.encode()
        b = data[p]
        agg.update(struct.pack(">Q", len(pb)) + pb + struct.pack(">Q", len(b)) + b)
    check(agg.hexdigest(), ADDRESS_AGGREGATE, "address aggregate-v2")
    objs = {c: json.loads(data[f"address/countries/{c}.json"]) for c in codes}
    if not codes <= regular:
        fail("address code outside regular set")
    own = {c: o for c, o in objs.items() if "fmt" in o}
    check(len(own), 198, "own fmt")
    check(len(codes - own.keys()), 54, "no own fmt")
    check(sorted(regular - codes), ["CP", "CQ", "DG", "EA", "IC"], "missing address countries")
    check(len(regular - own.keys()), 59, "unknown applicability")
    out: dict[str, dict[str, bool | None]] = {}
    for c in regular:
        if c not in own:
            out[c] = {
                "fmt_has_region": None, "fmt_has_postal": None,
                "source_require_region": None, "source_require_postal": None,
            }
            continue
        toks = re.findall(r"%(.)", own[c]["fmt"])
        req = own[c].get("require") if "require" in own[c] else None
        out[c] = {
            "fmt_has_region": "S" in toks,
            "fmt_has_postal": "Z" in toks,
            "source_require_region": ("S" in req) if req is not None else None,
            "source_require_postal": ("Z" in req) if req is not None else None,
        }
    for token, yes, no, optional, missing in (
        ("S", 75, 123, "EE LT LV MY RO TR UA".split(), 31),
        ("Z", 183, 15, "CO CR GI HN ID IQ KY PG SO SV UM VE VG VN".split(), 95),
    ):
        present = {c for c, o in own.items() if token in re.findall(r"%(.)", o["fmt"])}
        check(len(present), yes, f"fmt {token} present")
        check(len(own.keys() - present), no, f"fmt {token} absent")
        got_opt = sorted(c for c in present if "require" in own[c] and token not in own[c]["require"])
        check(got_opt, optional, f"optional-used {token}")
        check(len([c for c in present if "require" not in own[c]]), missing, f"used missing require {token}")
    spots = {
        "US": (True, True, True, True),
        "CA": (True, True, True, True),
        "DE": (False, True, False, True),
        "IE": (True, True, None, None),
        "AG": (None, None, None, None),
        "CP": (None, None, None, None),
    }
    for c, exp in spots.items():
        row = out[c]
        got = (row["fmt_has_region"], row["fmt_has_postal"], row["source_require_region"], row["source_require_postal"])
        check(got, exp, f"spot {c}")
    if any(v is False for c, row in out.items() if c in {"AG", "CP"} for v in row.values()):
        fail("unknown applicability stored as false")
    return out


def zones(data: dict[str, bytes], regular: set[str]) -> list[str]:
    tz = data["tz/tzdata2026f.tar.gz"]
    check(sha256(tz), TZ_SHA256, "tz tar sha256")
    td: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(tz), mode="r:gz") as tar:
        for m in tar.getmembers():
            p = PurePosixPath(m.name)
            if p.is_absolute() or ".." in p.parts or not m.isfile() or m.issym() or m.islnk() or m.name in td:
                fail(f"unsafe tz member {m.name}")
            f = tar.extractfile(m)
            if f is None:
                fail(f"unreadable tz member {m.name}")
            td[m.name] = f.read()
    check(td["version"].strip(), b"2026f", "tz version")
    iso = {ln.split("\t")[0] for ln in td["iso3166.tab"].decode().splitlines() if ln and not ln.startswith("#")}
    check(len(iso), 249, "iana iso table")
    if not iso <= regular:
        fail("iso3166 code outside regular set")
    check(sorted(regular - iso), "AC CP CQ DG EA IC TA XK".split(), "cldr extra eight")
    zs = [ln.split("\t")[2] for ln in td["zone1970.tab"].decode().splitlines() if ln and not ln.startswith("#")]
    check(len(zs), 312, "zone1970 rows")
    check(len(set(zs)), 312, "unique zone ids")
    primary: set[str] = set()
    links: set[str] = set()
    for name in ["africa", "antarctica", "asia", "australasia", "europe", "northamerica", "southamerica", "etcetera", "backward"]:
        for ln in td[name].decode().splitlines():
            parts = ln.split()
            if parts and parts[0] == "Zone":
                primary.add(parts[1])
            if parts and parts[0] == "Link":
                links.add(parts[2])
    if not set(zs) <= primary or set(zs) & links or "Etc/UTC" not in primary:
        fail("zone membership")
    supported = set(zs) | {"Etc/UTC"}
    check(len(supported), 313, "supported zones")
    if supported & {"US/Eastern", "Etc/GMT", "EST5EDT", "Factory"} or "America/New_York" not in supported:
        fail("zone exclusion")
    ordered = sorted(supported, key=lambda s: s.encode())
    blob = ("\n".join(ordered) + "\n").encode()
    check(sha256(blob), ZONE_LIST_SHA256, "supported zone list sha256")
    return ordered


def sql_str(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def sql_bool(v: bool | None) -> str:
    if v is None:
        return "NULL"
    return "TRUE" if v else "FALSE"


def build_sql(names_map: dict[str, str], flag_map: dict[str, dict[str, bool | None]], zone_ids: list[str], upstream_sha512: str) -> str:
    releases = [
        ("cldr-region-49", "cldr-region", "49", CLDR_SHA256, upstream_sha512, "unicode-license-v3"),
        ("address-applicability-20261106", "address-applicability", "20261106", ADDRESS_AGGREGATE, None, "cc-by-4.0"),
        ("tzdata-2026f", "tzdata", "2026f", TZ_SHA256, None, "iana-tz-public-domain"),
    ]
    rel_values = ",\n".join(
        "(" + ", ".join([
            sql_str(rid), sql_str(ds), sql_str(ver), sql_str(digest),
            "NULL" if up is None else sql_str(up), sql_str(lic),
        ]) + ")"
        for rid, ds, ver, digest, up, lic in releases
    )
    country_values = ",\n".join(
        "(" + ", ".join([
            sql_str(code), sql_str(names_map[code]),
            sql_bool(flag_map[code]["fmt_has_region"]), sql_bool(flag_map[code]["fmt_has_postal"]),
            sql_bool(flag_map[code]["source_require_region"]), sql_bool(flag_map[code]["source_require_postal"]),
            "NULL",
        ]) + ")"
        for code in sorted(names_map)
    )
    zone_values = ",\n".join(f"({sql_str(z)}, NULL)" for z in zone_ids)
    heads = ",\n".join(
        f"({sql_str(ds)}, {sql_str(rid)})"
        for rid, ds, *_ in releases
    )
    return (
        "INSERT INTO ref.data_release (release_id, dataset, version, content_sha256, upstream_sha512, license_id) VALUES\n"
        + rel_values + ";\n"
        "INSERT INTO ref.country (country_code, name, fmt_has_region, fmt_has_postal, source_require_region, source_require_postal, retired_release_id) VALUES\n"
        + country_values + ";\n"
        "INSERT INTO ref.time_zone (time_zone_id, retired_release_id) VALUES\n"
        + zone_values + ";\n"
        "INSERT INTO ref.dataset_head (dataset, release_id) VALUES\n"
        + heads + ";\n"
    )


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--archive", required=True)
    ap.add_argument("--out-cs", required=True)
    ap.add_argument("--out-json", required=True)
    args = ap.parse_args()
    data = load(args.archive)
    cl = data["cldr/cldr-common-49.zip"]
    check(sha256(cl), CLDR_SHA256, "cldr sha256")
    pub = re.search(r"^([a-f0-9]{128})\s+\*?cldr-common-49.zip\s*$", data["cldr/SHASUM512.txt"].decode(), re.M)
    if not pub:
        fail("published sha512 line")
    check(hashlib.sha512(cl).hexdigest(), pub.group(1), "cldr published sha512")
    zf = zipfile.ZipFile(io.BytesIO(cl))
    for outer, inner in (("region.xml", "common/validity/region.xml"), ("en.xml", "common/main/en.xml"), ("LICENSE", "LICENSE")):
        if data["cldr/" + outer] != zf.read(inner):
            fail(f"zip member {outer}")
    readme = data["docs/libaddressinput-README.md"].decode()
    if "Source code licensed under the Apache 2.0. Data licensed under the CC-BY 4.0" not in readme:
        fail("address license line")
    if b"public domain" not in data["tz/tzdata2026f.tar.gz"] and b"public domain" not in data.get("docs/libaddressinput-LICENSE", b""):
        pass
    regular = regular_codes(data["cldr/region.xml"])
    name_map = names(data["cldr/en.xml"], regular)
    flag_map = flags(data, regular)
    zone_ids = zones(data, regular)
    # tz LICENSE is inside the tarball. Confirm the public-domain wording there.
    with tarfile.open(fileobj=io.BytesIO(data["tz/tzdata2026f.tar.gz"]), mode="r:gz") as tar:
        lic = tar.extractfile("LICENSE")
        if lic is None or b"public domain" not in lic.read().lower():
            fail("tz license notice")
    sql = build_sql(name_map, flag_map, zone_ids, pub.group(1))
    if '"""' in sql:
        fail("seed sql contains a raw-string delimiter")
    cs = (
        "// Generated by src/Auth.Data/tools/ref-seed.py. Do not edit by hand.\n"
        f"// Archive SHA-256 {ARCHIVE_SHA256}\n"
        "namespace Auth.Data.Migrations;\n\n"
        "static class RefSeedData\n{\n"
        f"    internal const string ArchiveSha256 = \"{ARCHIVE_SHA256}\";\n"
        "    internal const string Sql = \"\"\"\n"
        + sql
        + "\"\"\";\n}\n"
    )
    rows = {
        "archive_sha256": ARCHIVE_SHA256,
        "countries": [
            {
                "country_code": c,
                "name": name_map[c],
                "fmt_has_region": flag_map[c]["fmt_has_region"],
                "fmt_has_postal": flag_map[c]["fmt_has_postal"],
                "source_require_region": flag_map[c]["source_require_region"],
                "source_require_postal": flag_map[c]["source_require_postal"],
            }
            for c in sorted(name_map)
        ],
        "time_zones": zone_ids,
        "releases": [
            {"release_id": "cldr-region-49", "dataset": "cldr-region", "version": "49", "content_sha256": CLDR_SHA256, "upstream_sha512": pub.group(1), "license_id": "unicode-license-v3"},
            {"release_id": "address-applicability-20261106", "dataset": "address-applicability", "version": "20261106", "content_sha256": ADDRESS_AGGREGATE, "upstream_sha512": None, "license_id": "cc-by-4.0"},
            {"release_id": "tzdata-2026f", "dataset": "tzdata", "version": "2026f", "content_sha256": TZ_SHA256, "upstream_sha512": None, "license_id": "iana-tz-public-domain"},
        ],
    }
    text = json.dumps(rows, ensure_ascii=False, indent=2) + "\n"
    with open(args.out_cs, "w", encoding="utf-8", newline="\n") as f:
        f.write(cs)
    with open(args.out_json, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"ref-seed: wrote {len(name_map)} countries, {len(zone_ids)} zones")


if __name__ == "__main__":
    main()
