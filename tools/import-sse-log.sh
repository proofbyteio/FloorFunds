#!/usr/bin/env bash
# import-sse-log.sh — append NEW entries from SSE's contract/SSE-BUILD-LOG-PENDING.md (as committed at <commit>) to docs/BUILD-LOG.md.
# Owner: SSE2. Process (TL, 11:38 AM PT and 12:08 PM PT): SSE writes only contract/SSE-BUILD-LOG-PENDING.md; SSE2 imports into docs/BUILD-LOG.md.
# Usage: tools/import-sse-log.sh <commit> [repo-dir]        e.g. tools/import-sse-log.sh feat/contract-v1
# Read-only on git: uses `git show <sha>:contract/SSE-BUILD-LOG-PENDING.md` and `git log`; no checkout, no index, no HEAD/branch change.
# Entries are '## ' or '### ' headings; the '# ' title and the preamble before the first entry are not imported.
# Each imported entry is copied byte-for-byte between markers carrying the sha256 of its text:
#   <!-- sse-import sha256=<hash> first-seen=<sha> imported-from=<sha> head="<heading>" -->  ...  <!-- /sse-import -->
# first-seen = the oldest commit (in <commit>'s history of that file) whose file already contains the exact entry text.
# Dedup: an entry whose hash is already in BUILD-LOG.md is skipped, so re-running is a no-op ("imported 0").
# If SSE later edits an old entry, its new text gets a new hash and is appended marked "(revised)"; the log stays append-only.
# Note: text that SSE appends after an entry's last bullet (before the next heading) belongs to that entry, so the entry
# counts as revised. New entries are appended at the end (chronological, since SSE appends in time order).
set -euo pipefail
commit="${1:?usage: import-sse-log.sh <commit> [repo]}"
repo="${2:-$(cd "$(dirname "$0")/.." && pwd)}"
log="$repo/docs/BUILD-LOG.md"; file=contract/SSE-BUILD-LOG-PENDING.md
[ -f "$log" ] || { echo "no $log" >&2; exit 2; }
sha=$(git -C "$repo" rev-parse --short "$commit^{commit}")
# Guard: never import an OLDER commit than one already imported (its older entry text would look "revised").
for x in $(grep -o 'imported-from=[0-9a-f]*' "$log" | cut -d= -f2 | sort -u); do
  if [ "$x" != "$sha" ] && git -C "$repo" merge-base --is-ancestor "$sha" "$x" 2>/dev/null; then
    echo "skip: $sha is older than already-imported $x; imported 0"; exit 0; fi
done
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
git -C "$repo" show "$sha:$file" >"$tmp/target.md"
i=0; for c in $(git -C "$repo" log --reverse --format=%h "$sha" -- "$file"); do
  i=$((i+1)); git -C "$repo" show "$c:$file" >"$tmp/$(printf %04d $i)-$c.md" 2>/dev/null || true; done
python3 - "$tmp" "$log" "$sha" <<'PY'
import sys, hashlib, re, glob, os
tmp, log, sha = sys.argv[1:]
def entries(text):
    return [p.rstrip("\n") + "\n" for p in re.split(r'(?m)^(?=#{2,3} )', text) if re.match(r'#{2,3} ', p)]
target = entries(open(f"{tmp}/target.md", encoding="utf-8").read())
hist = [(os.path.basename(f)[5:-3], set(entries(open(f, encoding="utf-8").read()))) for f in sorted(glob.glob(f"{tmp}/[0-9]*.md"))]
cur = open(log, encoding="utf-8").read()
have = set(re.findall(r'<!-- sse-import sha256=([0-9a-f]{64})', cur))
heads = set(re.findall(r'<!-- sse-import sha256=[0-9a-f]{64} [^>]*head="([^"]*)"', cur))
new, skipped = [], 0
for body in target:
    h = hashlib.sha256(body.encode()).hexdigest()
    if h in have: skipped += 1; continue
    head = body.splitlines()[0].lstrip("#").strip().replace('"', "'")
    first = next((c for c, es in hist if body in es), sha)
    rev = " (revised)" if head in heads else ""
    new.append(f'<!-- sse-import sha256={h} first-seen={first} imported-from={sha} head="{head}"{rev} -->\n'
               f'> **Imported verbatim from SSE**{rev}: `contract/SSE-BUILD-LOG-PENDING.md`, first seen in `{first}`, imported from `{sha}`.\n\n'
               f'{body}<!-- /sse-import -->\n')
if new:
    with open(log, "a", encoding="utf-8") as f:
        f.write("\n" if cur.endswith("\n") else "\n\n"); f.write("\n".join(new))
print(f"imported {len(new)}, skipped {skipped} (already present) from {sha}")
PY
