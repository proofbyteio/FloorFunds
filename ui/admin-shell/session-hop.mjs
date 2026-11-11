// POST /api/session forwards the access token to the issuer. The issuer path
// has no reopen flag. The error is the issuer's outcome, not a session expiry.

export async function openIssuerSession(issuerUrl, token, fetchImpl = globalThis.fetch) {
  if (typeof issuerUrl !== "string" || !issuerUrl.startsWith("http://127.0.0.1:")) {
    return { status: 503, error: "issuer_unavailable" };
  }
  const target = issuerUrl.replace(/\/$/, "") + "/v1/session";
  let opened;
  try {
    opened = await fetchImpl(target, {
      method: "POST",
      headers: { "content-type": "text/plain" },
      body: token,
    });
  } catch {
    return { status: 503, error: "issuer_unavailable" };
  }
  if (opened.status === 200) return { status: 200, error: "ok" };
  if (opened.status === 401) return { status: 401, error: "unauthorized" };
  if (opened.status === 503) return { status: 503, error: "issuer_unavailable" };
  return { status: 502, error: "issuer_rejected" };
}
