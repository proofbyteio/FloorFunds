using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// standin-control §3: the password policy, shared by PUT /_standin/users and (later) every real set-password path.
public static class PasswordPolicy
{
    public const string Pci = "pci", Nevada = "nevada_mics7";
    // Rule ids are output in the §3 table order.
    public static List<string> Validate(string password, string username, IReadOnlySet<string> presets, Func<string, bool> isReused)
    {
        var v = new List<string>();
        var pw = password.Normalize(NormalizationForm.FormKC);
        var runes = pw.EnumerateRunes().ToList();
        if (runes.Count < 12) v.Add("min_length");
        if (runes.Count > 128) v.Add("max_length");
        // [ASSUMPTION A8] letter = Unicode L*, number = Nd after NFKC (SA Proposal, LD row 120); OQ-SSE2-30 still OPEN.
        bool letter = runes.Any(Rune.IsLetter), number = runes.Any(r => Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber);
        if (presets.Contains(Pci) && !(letter && number)) v.Add("letters_and_numbers");
        if (presets.Contains(Nevada))
        {
            // [ASSUMPTION A9] the 4 types are uppercase, lowercase, digit and other.
            int types = new[] { runes.Any(Rune.IsUpper), runes.Any(Rune.IsLower), number,
                runes.Any(r => !Rune.IsLetter(r) && !Rune.IsDigit(r)) }.Count(b => b);
            if (types < 2) v.Add("two_of_four_types");
        }
        var u = username.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        if (u.Length > 0 && pw.ToLowerInvariant().Contains(u, StringComparison.Ordinal)) v.Add("contains_username");
        if (isReused(pw)) v.Add("reused");
        return v;
    }
}

// RFC 6238 with BCL HMAC-SHA1. [ASSUMPTION A10] SHA1, 30 s step, 6 digits (RFC defaults; authenticator-app compatible).
public static class Totp
{
    public static long Step(DateTimeOffset now) => now.ToUnixTimeSeconds() / 30;
    public static string Code(byte[] secret, DateTimeOffset now) => CodeAt(secret, Step(now));
    // SEC76 (TL 9:42): G1 +-30 s window, and a step works once per user: the matched step must be later than the last used one.
    public static bool Accept(StandInUser u, string code, DateTimeOffset now)
    {
        if (code.Length != 6) return false;
        var cur = Step(now);
        foreach (var s in new[] { cur - 1, cur, cur + 1 })
            if (s > u.LastTotpStep && CodeAt(u.TotpSecret, s) == code) { u.LastTotpStep = s; return true; }
        return false;
    }
    // Control helper: the earliest step in the window not yet used, or null.
    public static long? NextUnused(StandInUser u, DateTimeOffset now) { var cur = Step(now); for (var s = cur - 1; s <= cur + 1; s++) if (s > u.LastTotpStep) return s; return null; }
    public static string CodeAt(byte[] secret, long step)
    {
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var h = HMACSHA1.HashData(secret, counter);
        int o = h[^1] & 0x0f;
        int bin = ((h[o] & 0x7f) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3];
        return (bin % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}

public sealed class StandInUser
{
    public required string Username { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; set; }
    public required List<(string AppCode, string Role)> Roles { get; set; }
    public required List<string> LocationSlugs { get; set; }
    public int PermVersion { get; set; } = 1; // [ASSUMPTION A21] FIXTURE perm_version, default 1.
    // contract 6.3 lockout state; cleared by /_standin/reset (users are rebuilt).
    public int FailedPasswords { get; set; }   // SEC77 (TL 9:42): wrong passwords AND wrong TOTP codes; reset only by a full sign-in
    public long LastTotpStep { get; set; } = long.MinValue; // SEC76
    public DateTimeOffset? LockedUntil { get; set; }
    // /admin users (contract 5.12, 8.10): admin_type super|partner, no location. FIXTURE recovery codes are kept as SHA-256 hashes only.
    // [TL Decision 9:26 PM, (b)] FIXTURE-only: the amr a test user's location session carries ([pwd], [wiaormfa]) to prove rule 2 refusals.
    public string[]? FixtureAmr { get; init; }
    public string? AdminType { get; init; }
    public string? PartnerId { get; init; }
    public HashSet<string> RecoveryCodeHashes { get; } = new(StringComparer.Ordinal);
    // 7.8 re-enrol rate limit: failed totp_code tries on POST /mfa/reenroll (cleared by /_standin/reset; users are rebuilt).
    public List<DateTimeOffset> ReenrollFailures { get; } = new();
    public byte[] TotpSecret { get; set; } = RandomNumberGenerator.GetBytes(20);
    // Newest first; the current password is entry 0. PBKDF2 hashes, never the plaintext.
    public List<(byte[] Salt, byte[] Hash)> PasswordHistory { get; } = new();

    public static (byte[], byte[]) Hash(string pwNfkc, byte[]? salt = null)
    {
        salt ??= RandomNumberGenerator.GetBytes(16);
        return (salt, Rfc2898DeriveBytes.Pbkdf2(pwNfkc, salt, 10_000, HashAlgorithmName.SHA256, 32));
    }
    // standin-control §3 `reused`: one of the last 4 passwords.
    public bool Reused(string pwNfkc) => PasswordHistory.Take(4).Any(e => CryptographicOperations.FixedTimeEquals(Hash(pwNfkc, e.Salt).Item2, e.Hash));
    public void SetPassword(string pwNfkc) { PasswordHistory.Insert(0, Hash(pwNfkc)); if (PasswordHistory.Count > 4) PasswordHistory.RemoveAt(4); }
}

public static class UserFixtures
{
    public static readonly string[] Statuses = { "active", "disabled", "locked" }; // [ASSUMPTION A11]

    // Partner preset per location slug, from the fixtures.
    public static Dictionary<string, string> PresetBySlug(JsonObject fx) =>
        fx["partners"]!.AsArray().SelectMany(p => p!["locations"]!.AsArray()
            .Select(l => (Slug: (string)l!["slug"]!, Preset: (string)p!["preset"]!))).ToDictionary(x => x.Slug, x => x.Preset);

    public static HashSet<string> AppCodes(JsonObject fx) => fx["apps"]!.AsArray().Select(a => (string)a!["app_code"]!).ToHashSet();
}
