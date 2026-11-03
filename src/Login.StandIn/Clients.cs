using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// contract 4.9 clients, 7.6 client_secret_basic (secret stored hashed). Loaded from the FIXTURE "clients" array.
public sealed record OAuthClient(string ClientId, string AppCode, string ApiScope, string Aud, bool Cachd, bool OrgClaims, bool Refresh,
    string[] RedirectUris, string[] PostLogoutRedirectUris, byte[] SecretHash);

public static class Clients
{
    public static readonly string[] ApiScopes = { "cash_advance_api", "backoffice_api" };

    public static Dictionary<string, OAuthClient> Load(JsonObject fx) =>
        fx["clients"]!.AsArray().Select(c => new OAuthClient((string)c!["client_id"]!, (string)c["app_code"]!, (string)c["api_scope"]!,
            (string)c["aud"]!, (bool)c["cachd"]!, (bool)c["org_claims"]!, (bool)c["refresh"]!,
            c["redirect_uris"]!.AsArray().Select(x => (string)x!).ToArray(),
            c["post_logout_redirect_uris"]!.AsArray().Select(x => (string)x!).ToArray(),
            SHA256.HashData(Encoding.UTF8.GetBytes((string)c["secret"]!)))).ToDictionary(c => c.ClientId, StringComparer.Ordinal);

    // RFC 6749 2.3.1 client_secret_basic. private_key_jwt is advertised (7.1) but the fixtures hold no client keys yet:
    // [ASSUMPTION A24 / BLOCKER B2] an assertion is refused with invalid_client until a TL ruling gives client JWKs.
    public static OAuthClient? Authenticate(HttpRequest req, IReadOnlyDictionary<string, OAuthClient> clients, IFormCollection form)
    {
        if (form.ContainsKey("client_assertion") || form.ContainsKey("client_assertion_type")) return null;
        var h = req.Headers.Authorization.ToString();
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
}
