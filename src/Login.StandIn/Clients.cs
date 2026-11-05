using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Login.StandIn;

// contract 4.9 clients, 7.6 client auth. Loaded from the FIXTURE "clients" array.
// AppCode/ApiScope/Aud are null for the hub: [TL Decision 9:26 PM, H1 revised] the hub is openid-only and gets NO access token;
// its id_token aud = client_id. It is not an app_code and carries no roles.
public sealed record OAuthClient(string ClientId, string? AppCode, string? ApiScope, string? Aud, bool Cachd, bool OrgClaims, bool Refresh,
    string[] RedirectUris, string[] PostLogoutRedirectUris, byte[] SecretHash, string? StartUrl, bool Handoff, RSA PublicKey, string KeyKid);

public static class Clients
{
    public static readonly string[] ApiScopes = { "cash_advance_api", "backoffice_api" };
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    // [TL Decision 9:16 PM, B2] FIXTURE client keys: fresh RSA-2048 per client at every start and reset (7.6 allows RS256 only).
    // The private key is written only under /tmp (never the repo), PKCS#8 PEM, file mode 0600; the public key stays in memory.
    public static Dictionary<string, OAuthClient> Load(JsonObject fx, string keyDir)
    {
        var full = Path.GetFullPath(keyDir);
        if (!full.StartsWith("/tmp/", StringComparison.Ordinal)) throw new InvalidOperationException("FIXTURE client keys may only be written under /tmp");
        Directory.CreateDirectory(full);
        var d = new Dictionary<string, OAuthClient>(StringComparer.Ordinal);
        foreach (var c in fx["clients"]!.AsArray())
        {
            var id = (string)c!["client_id"]!;
            var rsa = RSA.Create(2048);
            var kid = "fixture-" + id + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var path = Path.Combine(full, id + ".FIXTURE.key.pem");
            File.WriteAllText(path, "# FIXTURE client key for the local stand-in only; kid " + kid + "\n" + rsa.ExportPkcs8PrivateKeyPem() + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var pub = RSA.Create(); pub.ImportRSAPublicKey(rsa.ExportRSAPublicKey(), out _); rsa.Dispose();
            d[id] = new OAuthClient(id, (string?)c["app_code"], (string?)c["api_scope"], (string?)c["aud"], (bool)c["cachd"]!, (bool)c["org_claims"]!, (bool)c["refresh"]!,
                c["redirect_uris"]!.AsArray().Select(x => (string)x!).ToArray(),
                c["post_logout_redirect_uris"]!.AsArray().Select(x => (string)x!).ToArray(),
                SHA256.HashData(Encoding.UTF8.GetBytes((string)c["secret"]!)), (string?)c["start_url"], (bool?)c["handoff"] ?? false, pub, kid);
        }
        return d;
    }

    // RFC 6749 2.3.1 client_secret_basic, or 7.6 private_key_jwt (RFC 7523). Exactly one method per request. Caller holds the gate.
    public static OAuthClient? Authenticate(HttpRequest req, IReadOnlyDictionary<string, OAuthClient> clients, IFormCollection form,
        string issuer, DateTimeOffset now, Dictionary<string, DateTimeOffset> usedJti)
    {
        var h = req.Headers.Authorization.ToString();
        bool hasAssertion = form.ContainsKey("client_assertion") || form.ContainsKey("client_assertion_type");
        if (hasAssertion) return h.Length > 0 ? null : Assertion(clients, form, issuer, now, usedJti);
        if (!h.StartsWith("Basic ", StringComparison.Ordinal)) return null;
        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..].Trim())); } catch (FormatException) { return null; }
        var i = raw.IndexOf(':');
        if (i <= 0) return null;
        var id = Uri.UnescapeDataString(raw[..i].Replace('+', ' '));
        var secret = Uri.UnescapeDataString(raw[(i + 1)..].Replace('+', ' '));
        if (!clients.TryGetValue(id, out var c)) return null;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), c.SecretHash) ? c : null;
    }

    // 7.6: RS256 only (pinned before key lookup; none/HS*/ES* refused); iss = sub = client_id; aud = the issuer exactly;
    // jti required, refused on reuse until exp; exp - iat <= 60; skew +-30 s (G1).
    static OAuthClient? Assertion(IReadOnlyDictionary<string, OAuthClient> clients, IFormCollection form, string issuer, DateTimeOffset now, Dictionary<string, DateTimeOffset> usedJti)
    {
        if (form["client_assertion_type"].ToString() != AssertionType || form["client_assertion"].Count != 1) return null;
        var parts = form["client_assertion"].ToString().Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var hdr = StandInJson.ParseObject(Base64UrlEncoder.Decode(parts[0])); if (hdr is null) return null;
            if ((string?)hdr["alg"] != "RS256" || hdr.Any(kv => kv.Key is not ("alg" or "kid" or "typ"))) return null;
            var cl = StandInJson.ParseObject(Base64UrlEncoder.Decode(parts[1])); if (cl is null) return null;
            var iss = (string?)cl["iss"];
            if (iss is null || (string?)cl["sub"] != iss || !clients.TryGetValue(iss, out var c)) return null;
            if (form.ContainsKey("client_id") && form["client_id"].ToString() != iss) return null;
            if (!c.PublicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlEncoder.DecodeBytes(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;
            if (cl["aud"] is not JsonValue av || (string?)av != issuer) return null;
            var jti = (string?)cl["jti"];
            long n = now.ToUnixTimeSeconds(), iat = (long)cl["iat"]!, exp = (long)cl["exp"]!;
            if (string.IsNullOrEmpty(jti) || iat > n + 30 || exp <= n - 30 || exp <= iat || exp - iat > 60) return null;
            foreach (var k in usedJti.Where(kv => kv.Value < now.AddSeconds(-30)).Select(kv => kv.Key).ToList()) usedJti.Remove(k);
            var key = iss + "\n" + jti;
            if (usedJti.ContainsKey(key)) return null;
            usedJti[key] = DateTimeOffset.FromUnixTimeSeconds(exp);
            return c;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or ArgumentException or CryptographicException or NullReferenceException) { return null; }
    }
}
