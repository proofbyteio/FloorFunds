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

    public static int Count(Classes c) => System.Numerics.BitOperations.PopCount((uint)c);
}
