using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Login.StandIn;

// One /l/{slug} sign-in transaction (6.1, 6.4). The id is the pre-session cookie value; it is reissued after the password step (4.11).
public sealed class SignInTx
{
    public required string Id { get; set; }
    public required string Slug { get; init; }
    public string? Nonce { get; set; }
    public DateTimeOffset NonceAt { get; set; }
    public string? PcId { get; set; }
    public string Stage { get; set; } = "password"; // password | totp
    // A44 (TL 9:38): the re-prompt variant shown at GET is kept on the tx so a failed retry re-shows it (8B message included).
    public bool Resign { get; set; } // re-prompt (5.3): page-resign-in ids
    public bool AgentDown { get; set; }
    public string? Username { get; set; }
    public string Attestation { get; set; } = "missing"; // 6.2 valid | missing | invalid
    public string? ReturnTo { get; set; }
    // Hand-off interstitial transactions (5.8b): bound to nonce + hub_launch + login session + client_id.
    public string Purpose { get; init; } = "signin"; // signin | handoff | admin
    public string? Sid { get; init; }
    public string? ClientId { get; init; }
    public string? LaunchHash { get; init; }
    public Dictionary<string, string?>? Authorize { get; init; }
}

public sealed record HubLaunch(string Sid, string ClientId, DateTimeOffset IssuedAt) { public bool Used { get; set; } }

// A login session (4.11, 5.1, 5.12, 7.4, 7.8). Kind is recovery_reenroll only for an /admin recovery-code sign-in (A33).
public sealed class LoginSession
{
    public required string Sid { get; init; }
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Slug { get; init; }
    public required string LocationId { get; init; }
    public required DateTimeOffset AuthTime { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public string[] Amr { get; init; } = { "pwd", "otp", "mfa" };
    public string Kind { get; init; } = "normal";
    public bool Revoked { get; set; }
    public bool CodeIssued { get; set; }
    public string? AdminType { get; init; }          // set only for /admin sessions (no location)
    public string? PartnerId { get; init; }
    public bool Interactive { get; init; } = true;  // rule 1: every stand-in session comes from an interactive sign-in
    public bool ViaReturn { get; init; }            // created by a sign-in that returns to authorize (re-prompt, 5.3)
    public bool InteractiveUsed { get; set; }       // A32 ruling: the credential entry has been spent on one interactive code
    public bool HandoffUsed { get; set; }           // rule 3 cash_advance_handoff_used
    public bool CageConsumed { get; set; }          // rule 6: not SSO-eligible for anything
}

public sealed record CodeGrant(string ClientId, string RedirectUri, string Challenge, string? Nonce, string Scope, string Sid, DateTimeOffset IssuedAt)
{ public bool Used { get; set; } public string? FamilyId { get; set; } }

public sealed class RefreshRec
{
    public required string FamilyId { get; init; }
    public DateTimeOffset? RotatedAt { get; set; }
}

public sealed class Family
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string Sid { get; init; }
    public required string Scope { get; init; }
    public bool Revoked { get; set; }
}

public sealed record AccessRec(string Jti, string ClientId, string Sid, string? FamilyId) { public bool Revoked { get; set; } }

public sealed class AuthStore
{
    public Dictionary<string, SignInTx> Tx { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, LoginSession> Sessions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CodeGrant> Codes { get; } = new(StringComparer.Ordinal);       // key: SHA-256 of the code
    public Dictionary<string, RefreshRec> Refresh { get; } = new(StringComparer.Ordinal);    // key: SHA-256 of the token
    public Dictionary<string, Family> Families { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AccessRec> Access { get; } = new(StringComparer.Ordinal);      // key: jti
    public Dictionary<string, string> IdTokenSid { get; } = new(StringComparer.Ordinal);     // key: SHA-256 of the ID token
    public Dictionary<string, ECDsa> DeviceKeys { get; } = new(StringComparer.Ordinal);      // fake agent keys (6.8), in memory
    public Dictionary<string, HubLaunch> HubLaunches { get; } = new(StringComparer.Ordinal); // key: SHA-256 of hub_launch (5.7 stored hashed)
    public Dictionary<string, DateTimeOffset> AssertionJti { get; } = new(StringComparer.Ordinal); // 7.6 jti replay cache
    public void Clear() { Tx.Clear(); Sessions.Clear(); Codes.Clear(); Refresh.Clear(); Families.Clear(); Access.Clear(); IdTokenSid.Clear(); HubLaunches.Clear(); AssertionJti.Clear();
        foreach (var k in DeviceKeys.Values) k.Dispose(); DeviceKeys.Clear(); }
    public static string H(string v) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(v)));

    // Ends one login session everywhere (11.1): codes, refresh families and access tokens bound to it.
    public void RevokeSession(LoginSession s)
    {
        s.Revoked = true;
        foreach (var f in Families.Values.Where(f => f.Sid == s.Sid)) f.Revoked = true;
        foreach (var c in Codes.Values.Where(c => c.Sid == s.Sid)) c.Used = true;
        foreach (var a in Access.Values.Where(a => a.Sid == s.Sid)) a.Revoked = true;
    }
    public void RevokeFamily(string familyId)
    {
        if (Families.TryGetValue(familyId, out var f)) f.Revoked = true;
        foreach (var a in Access.Values.Where(a => a.FamilyId == familyId)) a.Revoked = true;
    }
}

// The fake device agent (6.8, standin-control 2.4) and the server check (6.7).
public static class Attestation
{
    static string B64(object o) => Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(o));

    static ECDsa Key(AuthStore a, string kid)
    {
        if (!a.DeviceKeys.TryGetValue(kid, out var k)) a.DeviceKeys[kid] = k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return k;
    }

    static string Jws(ECDsa key, string kid, object claims)
    {
        var si = B64(new { alg = "ES256", kid, typ = "cachd-attestation+jwt" }) + "." + B64(claims);
        return si + "." + Base64UrlEncoder.Encode(key.SignData(Encoding.ASCII.GetBytes(si), HashAlgorithmName.SHA256));
    }

    // Caller holds the state gate. Returns null for mode "missing" (the agent does not answer).
    public static string? FakeAgent(StandInState st, SignInTx tx, string issuer, DateTimeOffset now)
    {
        if (tx.PcId is null || !st.Pcs.TryGetValue(tx.PcId, out var pc) || pc.Mode == "missing" || tx.Nonce is null) return null;
        long iat = now.ToUnixTimeSeconds();
        string kid = pc.Mode == "unknown_device" ? "standin-unenrolled-" + pc.PcId[8..] : pc.PcId;
        string nonce = pc.Mode == "replayed" ? "standin-replayed-" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)) : tx.Nonce;
        if (pc.Mode == "expired") iat -= 150;
        var claims = new { nonce, device_id = kid, remote_session = pc.Mode == "remote_session", iat, exp = iat + 60, aud = issuer, purpose = tx.Purpose };
        var key = pc.Mode == "bad_signature" ? Key(st.Auth, "standin-wrong-key") : Key(st.Auth, kid);
        return Jws(key, kid, claims);
    }

    // 6.7 in order. Caller holds the gate. Returns (valid, remote_session).
    public static (bool Ok, bool Remote) Check(StandInState st, SignInTx tx, string? jws, string issuer, string purpose, DateTimeOffset now)
    {
        var taken = tx.Nonce; var takenAt = tx.NonceAt;
        tx.Nonce = null; // (1) compare-and-delete first; every outcome leaves it consumed.
        if (string.IsNullOrEmpty(jws) || taken is null) return (false, false);
        try
        {
            var parts = jws.Split('.');
            if (parts.Length != 3) return (false, false);
            var hdr = JsonNode.Parse(Base64UrlEncoder.Decode(parts[0]))!.AsObject();
            // (2) alg pinned, header allow-list (crit, jwk, jku, x5c, x5u and anything else refused).
            if ((string?)hdr["alg"] != "ES256" || hdr.Count != 3 || (string?)hdr["typ"] != "cachd-attestation+jwt" || hdr["kid"] is not JsonValue) return (false, false);
            var kid = (string)hdr["kid"]!;
            // (3) registry: exact kid, enrolled only. The registry is the fake PC list; unavailable fails closed.
            if (st.DeviceRegistryFault != "ok" || !st.Pcs.TryGetValue(kid, out var dev) || dev.Mode == "revoked_device") return (false, false);
            if (!st.Auth.DeviceKeys.TryGetValue(kid, out var key)) return (false, false);
            var sig = Base64UrlEncoder.DecodeBytes(parts[2]);
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), sig, HashAlgorithmName.SHA256)) return (false, false);
            var c = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
            if ((string?)c["device_id"] != kid) return (false, false);
            // (4) location: the registry records one location per fake device; other_location records a different one.
            var regLoc = dev.Mode == "other_location" ? "standin-other-location" : dev.LocationSlug;
            if (regLoc != tx.Slug) return (false, false);
            // (5) aud and purpose.
            if ((string?)c["aud"] != issuer || (string?)c["purpose"] != purpose) return (false, false);
            // (6) time: nonce age <=60 s with no skew; iat/exp with +-30 s.
            long n = now.ToUnixTimeSeconds(), iat = (long)c["iat"]!, exp = (long)c["exp"]!;
            if ((now - takenAt).TotalSeconds > 60 || iat > n + 30 || exp <= n - 30 || exp <= iat || exp - iat > 60) return (false, false);
            // (7) nonce match.
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes((string?)c["nonce"] ?? ""), Encoding.UTF8.GetBytes(taken))) return (false, false);
            return (true, (bool)c["remote_session"]!);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException) { return (false, false); }
    }
}
