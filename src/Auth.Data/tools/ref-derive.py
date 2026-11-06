#!/usr/bin/env python3
"""Independent archive derivation for the TLD-REF1 correction.

Does not import ref-seed.py. Reads the locked archive and writes the country
pairs and release source URLs. A missing fmt does not erase a present require.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import sys
import tarfile
import xml.etree.ElementTree as ET
from pathlib import PurePosixPath

ARCHIVE_SHA256 = "7330a82bd9b4d99cbae975a943b5938adefb54b687f297b092bf375771b1813e"
PREFIX = "dec145-146-refdata-20261106"


def fail(msg: str) -> None:
    print(f"ref-derive: {msg}", file=sys.stderr)
    raise SystemExit(2)


def load(path: str) -> dict[str, bytes]:
    raw = open(path, "rb").read()
    if hashlib.sha256(raw).hexdigest() != ARCHIVE_SHA256 or len(raw) != 35662359:
        fail("archive identity mismatch")
    data: dict[str, bytes] = {}
    with tarfile.open(fileobj=io.BytesIO(raw), mode="r:gz") as tar:
        for m in tar.getmembers():
            p = PurePosixPath(m.name)
            if p.is_absolute() or ".." in p.parts or len(p.parts) < 2 or p.parts[0] != PREFIX:
                fail("unsafe archive member")
            if not m.isfile() or m.issym() or m.islnk():
                fail("refusing non-regular member")
            q = "/".join(p.parts[1:])
            f = tar.extractfile(m)
            if f is None or q in data:
                fail("unreadable member")
            data[q] = f.read()
    manifest = {}
    for line in data["SHA256SUMS"].decode().splitlines():
        m = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        if not m:
            fail("manifest line")
        manifest[m.group(2)] = m.group(1)
    if set(manifest) != set(data) - {"SHA256SUMS"}:
        fail("manifest inventory")
    for rel, digest in manifest.items():
        if hashlib.sha256(data[rel]).hexdigest() != digest:
            fail(f"manifest digest {rel}")
    return data


def regular_codes(xml: bytes) -> list[str]:
    root = ET.fromstring(xml)
    found: set[str] = set()
    for e in root.findall(".//id"):
        if e.attrib.get("type") != "region" or e.attrib.get("idStatus") != "regular":
            continue
        for token in (e.text or "").split():
            if "~" not in token:
                found.add(token)
                continue
            lo, hi = token.split("~")
            hi = lo[: len(lo) - len(hi)] + hi
            if not (len(lo) == len(hi) == 2 and lo[0] == hi[0]):
                fail(f"bad range {token}")
            found.update(lo[0] + chr(c) for c in range(ord(lo[1]), ord(hi[1]) + 1))
    if len(found) != 257 or any(not re.fullmatch(r"[A-Z]{2}", c) for c in found):
        fail("regular keyspace")
    return sorted(found)


def names(xml: bytes, codes: list[str]) -> dict[str, str]:
    root = ET.fromstring(xml)
    en = {
        n.attrib["type"]: "".join(n.itertext())
        for n in root.findall(".//territories/territory")
        if "alt" not in n.attrib
    }
    if any(c not in en for c in codes):
        fail("missing name")
    return {c: en[c] for c in codes}


def flags(data: dict[str, bytes], codes: list[str]) -> dict[str, dict[str, bool | None]]:
    files = {}
    for path, raw in data.items():
        m = re.fullmatch(r"address/countries/([A-Z]{2})\.json", path)
        if m:
            files[m.group(1)] = json.loads(raw)
    out = {}
    for code in codes:
        obj = files.get(code)
        if obj is not None and "fmt" in obj:
            toks = re.findall(r"%(.)", obj["fmt"])
            fmt = ("S" in toks, "Z" in toks)
        else:
            fmt = (None, None)
        if obj is not None and "require" in obj:
            req = obj["require"]
            if not isinstance(req, str):
                fail(f"require type {code}")
            source = ("S" in req, "Z" in req)
        else:
            source = (None, None)
        out[code] = {
            "fmt_has_region": fmt[0], "fmt_has_postal": fmt[1],
            "source_require_region": source[0], "source_require_postal": source[1],
        }
    return out


def source_urls(text: str) -> dict[str, str]:
    sections: dict[str, list[str]] = {}
    current = ""
    for line in text.splitlines():
        if line and not line.startswith("-") and not line.startswith(" "):
            current = line.strip()
            sections.setdefault(current, [])
        elif current and line.startswith("- "):
            sections[current].append(line[2:])
    address_line = sections["Address metadata"][0]
    prefix = "Request URL for the index: "
    if not address_line.startswith(prefix):
        fail("address provenance")
    urls = {
        "cldr-region-49": sections["CLDR"][0],
        "address-applicability-20261106": address_line[len(prefix):],
        "tzdata-2026f": sections["Time zones"][0],
    }
    for url in urls.values():
        if not url.startswith("https://") or any(ch.isspace() for ch in url):
            fail("source url shape")
    if "gstatic.com" in " ".join(urls.values()):
        fail("redirect used as source url")
    return urls


def document(data: dict[str, bytes]) -> dict:
    codes = regular_codes(data["cldr/region.xml"])
    name_map = names(data["cldr/en.xml"], codes)
    flag_map = flags(data, codes)
    urls = source_urls(data["PROVENANCE.md"].decode())
    return {
        "countries": [
            {"country_code": c, "name": name_map[c], **flag_map[c]}
            for c in codes
        ],
        "source_urls": urls,
    }


def tri(v):
    if v is None:
        return None
    return bool(v)


def compare(got: dict, expect_path: str) -> None:
    other = json.loads(open(expect_path, encoding="utf-8").read())
    mismatches = []
    by = {c["country_code"]: c for c in other["countries"]}
    if len(by) != 257 or len(got["countries"]) != 257:
        fail("country cardinality")
    for row in got["countries"]:
        w = by.get(row["country_code"])
        if w is None:
            mismatches.append(row["country_code"] + " missing")
            continue
        for key in ("name", "fmt_has_region", "fmt_has_postal", "source_require_region", "source_require_postal"):
            left = row[key]
            right = w[key]
            if key != "name":
                left, right = tri(left), tri(right)
            if left != right:
                mismatches.append(f"{row['country_code']}.{key}")
    for rel, url in got["source_urls"].items():
        match = [r for r in other["releases"] if r["release_id"] == rel]
        if len(match) != 1 or match[0].get("source_url") != url:
            mismatches.append(f"source_url {rel}")
    if mismatches:
        print("ref-derive: mismatches " + ", ".join(mismatches), file=sys.stderr)
        raise SystemExit(2)
    print(f"ref-derive: 257 countries and {len(got['source_urls'])} source urls match {expect_path}")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--archive", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--compare")
    args = ap.parse_args()
    doc = document(load(args.archive))
    text = json.dumps(doc, ensure_ascii=False, indent=2) + "\n"
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    if args.compare:
        compare(doc, args.compare)
    else:
        ag = next(c for c in doc["countries"] if c["country_code"] == "AG")
        print("ref-derive: AG", ag["fmt_has_region"], ag["fmt_has_postal"], ag["source_require_region"], ag["source_require_postal"])


if __name__ == "__main__":
    main()
