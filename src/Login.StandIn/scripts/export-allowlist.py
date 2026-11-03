# Export active allowlist rows from the /tmp COPY (opened read-only) to JSON for the stand-in. Never opens the real DB.
import sqlite3, json, sys
src, dst = '/tmp/sse-d0/allowlist-copy.db', '/tmp/sse-d0/allowlist-active.json'
c = sqlite3.connect(f'file:{src}?mode=ro', uri=True)
rows = c.execute("select entry_id,partner_id,client_id,location_id,entry_type,value from network_allowlist_entry where is_active=1 order by value").fetchall()
json.dump({"source": src, "entries": [dict(zip(["entry_id","partner_id","client_id","location_id","entry_type","value"], r)) for r in rows]}, open(dst, 'w'), indent=1)
print(dst, len(rows))
