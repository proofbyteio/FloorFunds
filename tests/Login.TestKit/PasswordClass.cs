using System.Globalization;
using System.Text;

namespace Cachd.Login.TestKit;

/// <summary>
/// Reference classifier for the Nevada MICS #7 "2 of 4 character types" rule, written ONLY from the login doc
/// (rev 11:32, md5 5c68265c, §7 L337, decision rows 107-108): NFKC first, then per Unicode scalar (Rune):
/// Lu = upper, Ll = lower, Nd = digit, everything else (incl. Lt, Lm, Lo, M*, spaces, punctuation, emoji) = symbol.
/// Used to (a) self-check the expected values in the password-class table and (b) never as the system under test.
/// </summary>
public static class PasswordClass
{
    [Flags] public enum Classes { None = 0, Upper = 1, Lower = 2, Digit = 4, Symbol = 8 }

    public static string Nfkc(string s) => s.Normalize(NormalizationForm.FormKC);

    public static Classes Of(string password)
    {
        var c = Classes.None;
        foreach (var r in Nfkc(password).EnumerateRunes())
            c |= Rune.GetUnicodeCategory(r) switch
            {
                UnicodeCategory.UppercaseLetter => Classes.Upper,
                UnicodeCategory.LowercaseLetter => Classes.Lower,
                UnicodeCategory.DecimalDigitNumber => Classes.Digit,
                _ => Classes.Symbol,
            };
        return c;
    }

    /// OQ-SSE2-31 (TL Addendum 2): length = Unicode code points (Rune) AFTER NFKC.
    public static int Length(string password) => Nfkc(password).EnumerateRunes().Count();

    /// OQ-SSE2-32: username normalised like username_hmac (trim, NFKC, lower invariant); password NFKC + lower invariant; ordinal contains.
    public static bool ContainsUsername(string password, string username)
        => Nfkc(password).ToLowerInvariant().Contains(Nfkc(username.Trim()).ToLowerInvariant(), StringComparison.Ordinal);

    /// Nevada verdict by rule (list checks excluded): 12 <= Length <= 128, >= 2 classes, no username.
    public static bool NevadaByRule(string password, string username)
        => Length(password) is >= 12 and <= 128 && Count(Of(password)) >= 2 && !ContainsUsername(password, username);

    public static int Count(Classes c) => System.Numerics.BitOperations.PopCount((uint)c);
}
