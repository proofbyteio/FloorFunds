import http from "node:http";
import { openIssuerSession } from "./session-hop.mjs";

let failed = 0;
function check(name, ok, detail) {
  if (ok) console.log("PASS " + name);
  else { failed += 1; console.log("FAIL " + name + (detail ? " :: " + detail : "")); }
}

const seen = [];
const codes = [200, 401, 503, 500];
let n = 0;
const server = http.createServer((req, res) => {
  seen.push(req.url || "");
  const code = codes[n++] ?? 500;
  res.writeHead(code);
  res.end(code === 200 ? "{}" : "no");
});
await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
const port = server.address().port;
const base = `http://127.0.0.1:${port}`;

const ok = await openIssuerSession(base, "jwt");
const unauth = await openIssuerSession(base, "jwt");
const down = await openIssuerSession(base, "jwt");
const other = await openIssuerSession(base, "jwt");
const foreign = await openIssuerSession("http://192.0.2.1:9", "jwt");
const refused = await openIssuerSession("http://127.0.0.1:1", "jwt");
server.close();

const labels = [ok, unauth, down, other].map((r) => r.status + " " + r.error).join(",");
check("session-hop-labels",
  labels === "200 ok,401 unauthorized,503 issuer_unavailable,502 issuer_rejected"
  && foreign.status === 503 && foreign.error === "issuer_unavailable"
  && refused.status === 503 && refused.error === "issuer_unavailable",
  labels + ` foreign=${foreign.error} refused=${refused.error}`);
check("session-hop-has-no-reopen",
  seen.length === 4 && seen.every((url) => url === "/v1/session"),
  seen.join(" "));

if (failed !== 0) process.exit(1);
