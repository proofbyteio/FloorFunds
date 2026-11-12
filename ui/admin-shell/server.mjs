// Dev-only single origin for the admin shell. Listens on loopback, proxies /api to
// Admin.Api and /standin to the stand-in, and keeps the access token in this process.
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const root = path.dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.ADMIN_SHELL_PORT || 5088);
const apiOrigin = process.env.ADMIN_API_ORIGIN || "http://127.0.0.1:18090";
const standinOrigin = process.env.STANDIN_FRONT_ORIGIN || "http://127.0.0.1:18080";
const tokenUrl = process.env.STANDIN_TOKEN_URL || "http://127.0.0.1:18081/standin/connect/token";
const publicOrigin = `http://127.0.0.1:${port}`;

if (process.env.ADMIN_DEV_SHELL !== "1") {
  console.error("admin shell refused to start: ADMIN_DEV_SHELL must be 1");
  process.exit(2);
}
if (process.env.ASPNETCORE_ENVIRONMENT !== "Development") {
  console.error("admin shell refused to start: ASPNETCORE_ENVIRONMENT must be Development");
  process.exit(2);
}
if (!Number.isInteger(port) || port < 1024 || port > 65535) {
  console.error("admin shell refused to start: ADMIN_SHELL_PORT is not a loopback port");
  process.exit(2);
}

const sessions = new Map();

function b64url(buf) {
  return Buffer.from(buf).toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function cookie(req) {
  const raw = req.headers.cookie || "";
  for (const part of raw.split(";")) {
    const i = part.indexOf("=");
    if (i > 0 && part.slice(0, i).trim() === "admin_shell") return part.slice(i + 1).trim();
  }
  return "";
}

function setCookie(res, id) {
  res.setHeader("set-cookie", `admin_shell=${id}; HttpOnly; SameSite=Lax; Path=/`);
}

function readBody(req, limit = 65536) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let n = 0;
    req.on("data", (c) => {
      n += c.length;
      if (n > limit) reject(new Error("too_large"));
      else chunks.push(c);
    });
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", reject);
  });
}

// PQ-284 reversible default. The picker submits an id from the server list.
// Swap this check together with the partner <select> in shell.js.
async function enforceExistingPartner(token, body) {
  let parsed;
  try { parsed = JSON.parse(body.toString("utf8")); } catch { return null; }
  if (!parsed || typeof parsed !== "object" || typeof parsed.partner_id !== "string") return { status: 422, error: "partner_not_in_list" };
  const listed = await fetch(apiOrigin + "/api/screens/location-partners", { headers: { authorization: "Bearer " + token } });
  if (!listed.ok) return { status: 403, error: "forbidden" };
  const ids = new Set((await listed.json()).partners ?? []);
  if (!ids.has(parsed.partner_id)) return { status: 422, error: "partner_not_in_list" };
  return null;
}

function sendJson(res, status, obj) {
  const raw = Buffer.from(JSON.stringify(obj));
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", "content-length": raw.length });
  res.end(raw);
}

async function proxy(req, res, target, body, extra) {
  const headers = { ...req.headers, host: new URL(target).host };
  delete headers.connection;
  delete headers["transfer-encoding"];
  if (extra) Object.assign(headers, extra);
  if (body) headers["content-length"] = String(body.length);
  const upstream = await fetch(target, { method: req.method, headers, body: body && req.method !== "GET" && req.method !== "HEAD" ? body : undefined, redirect: "manual" });
  const out = Buffer.from(await upstream.arrayBuffer());
  const hop = new Set(["connection", "transfer-encoding", "keep-alive", "content-encoding"]);
  const headersOut = {};
  upstream.headers.forEach((value, key) => { if (!hop.has(key)) headersOut[key] = value; });
  headersOut["content-length"] = String(out.length);
  res.writeHead(upstream.status, headersOut);
  res.end(out);
}

async function exchange(code, verifier) {
  const secret = fs.readFileSync("/tmp/ff-db/admin-dev-shell.secret", "utf8").trim();
  const basic = Buffer.from("admin_dev_shell:" + secret).toString("base64");
  const body = new URLSearchParams({
    grant_type: "authorization_code",
    code,
    redirect_uri: publicOrigin + "/signin-oidc",
    code_verifier: verifier,
  });
  const res = await fetch(tokenUrl, {
    method: "POST",
    headers: { authorization: "Basic " + basic, "content-type": "application/x-www-form-urlencoded" },
    body,
  });
  if (!res.ok) return null;
  const json = await res.json();
  return typeof json.access_token === "string" ? json.access_token : null;
}

function serveFile(res, file) {
  const types = { ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".mjs": "text/javascript; charset=utf-8", ".ttf": "font/ttf", ".woff2": "font/woff2", ".svg": "image/svg+xml", ".txt": "text/plain; charset=utf-8" };
  const raw = fs.readFileSync(file);
  res.writeHead(200, { "content-type": types[path.extname(file)] || "application/octet-stream", "content-length": raw.length, "cache-control": "no-store" });
  res.end(raw);
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, publicOrigin);
    // Attaches a session this process already created. It does not accept a token.
    if (url.pathname === "/dev-session") {
      const existing = sessions.get(url.searchParams.get("id") || "");
      if (!existing?.token) { sendJson(res, 404, { error: "not_found" }); return; }
      res.writeHead(302, { location: "/", "set-cookie": `admin_shell=${url.searchParams.get("id")}; HttpOnly; SameSite=Lax; Path=/`, "cache-control": "no-store" });
      res.end();
      return;
    }
    if (url.pathname === "/signin") {
      const id = b64url(crypto.randomBytes(32));
      const verifier = b64url(crypto.randomBytes(32));
      const state = b64url(crypto.randomBytes(16));
      const challenge = b64url(crypto.createHash("sha256").update(verifier).digest());
      sessions.set(id, { verifier, state, token: "" });
      const q = new URLSearchParams({
        client_id: "admin_dev_shell",
        redirect_uri: publicOrigin + "/signin-oidc",
        response_type: "code",
        scope: "openid backoffice_api",
        state,
        code_challenge: challenge,
        code_challenge_method: "S256",
      });
      setCookie(res, id);
      res.writeHead(302, { location: "/standin/connect/authorize?" + q.toString() });
      res.end();
      return;
    }
    if (url.pathname === "/signin-oidc") {
      const session = sessions.get(cookie(req));
      if (!session || url.searchParams.get("state") !== session.state) {
        sendJson(res, 400, { error: "invalid_state" });
        return;
      }
      const token = await exchange(url.searchParams.get("code") || "", session.verifier);
      if (!token) { sendJson(res, 400, { error: "invalid_grant" }); return; }
      session.token = token;
      session.verifier = "";
      res.writeHead(302, { location: "/" });
      res.end();
      return;
    }
    if (url.pathname.startsWith("/standin/")) {
      const body = req.method === "GET" || req.method === "HEAD" ? null : await readBody(req);
      await proxy(req, res, standinOrigin + url.pathname + url.search, body);
      return;
    }
    if (url.pathname.startsWith("/api/")) {
      const session = sessions.get(cookie(req));
      if (!session?.token) { sendJson(res, 401, { error: "unauthorized" }); return; }
      const body = req.method === "GET" || req.method === "HEAD" ? null : await readBody(req);
      if (req.method === "POST" && url.pathname === "/api/orgs/locations") {
        const refused = await enforceExistingPartner(session.token, body);
        if (refused) { sendJson(res, refused.status, { error: refused.error }); return; }
      }
      await proxy(req, res, apiOrigin + url.pathname + url.search, body, { authorization: "Bearer " + session.token });
      return;
    }
    if (url.pathname === "/address/country-rules.json") {
      const file = path.resolve(root, "../address/country-rules.json");
      if (!fs.existsSync(file)) { sendJson(res, 404, { error: "not_found" }); return; }
      serveFile(res, file);
      return;
    }
    if (url.pathname.startsWith("/theme/")) {
      const themeRoot = path.resolve(root, "../theme");
      const file = path.resolve(themeRoot, url.pathname.slice("/theme/".length));
      if (!file.startsWith(themeRoot + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
        sendJson(res, 404, { error: "not_found" });
        return;
      }
      serveFile(res, file);
      return;
    }
    const rel = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
    const file = path.resolve(root, rel);
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      sendJson(res, 404, { error: "not_found" });
      return;
    }
    if (file.endsWith("server.mjs") || file.endsWith("start.sh")) { sendJson(res, 404, { error: "not_found" }); return; }
    serveFile(res, file);
  } catch (e) {
    console.error("admin shell request failed:", e instanceof Error ? e.name : "error");
    if (!res.headersSent) sendJson(res, 500, { error: "server_error" });
    else res.end();
  }
});

server.listen(port, "127.0.0.1", () => {
  console.log("admin shell " + publicOrigin);
});
