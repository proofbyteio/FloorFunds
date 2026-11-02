using Cachd.Login.TestKit;
using static Cachd.Login.TestKit.PasswordClass;

namespace Cachd.Login.ContractTests;

/// <summary>
/// PW-xx: password-class contract set (TL Decision rev 11:32, login doc 5c68265c §7 L337 + §16.17 L1310; Q28 L1339).
/// Fixture passwords only (TEST-ONLY), none is a real credential.
/// Verdicts are written only where the doc states them; anything unstated is PENDING with an OQ id.
///   Nevada: >= 12 chars, >= 2 of 4 classes after NFKC (Lu, Ll, Nd, everything else = symbol), breached/common list on top.
///   PCI:    >= 12 chars, "letters and numbers" (§7 8.3.6). Which Unicode categories count as a PCI "letter"/"number" is unstated (OQ-SSE2-30).
///   Both:   128 hard max, no truncation, NFKC before hashing, reject if it contains the username.
/// Server decides; Angular hints are advisory (doc L1310). So the LIVE tests set passwords through the server, never the UI hint.
/// </summary>
public class PasswordClassTests
{
    public record Case(string Id, string Password, Classes NevadaClasses, string Nevada, string Pci, string Why);

    const string A = "accept", R = "reject", P = "pending";
    const Classes U = Classes.Upper, L = Classes.Lower, D = Classes.Digit, S = Classes.Symbol;

    // Username used for the contains-username case (fixture persona name is unstated: OQ-SSE2-05).
    public const string Username = "test-cashier";

    public static readonly Case[] Set =
    {
        new("PW01", "zqxvbnmlkjhg", L, R, R, "ASCII lowercase only: 1 class; PCI no digit"),
        new("PW02", "zqxvbnmlkjh7", L | D, A, A, "lower + digit: 2 classes; PCI letter+number"),
        new("PW03", "839201746385", D, R, R, "digits only: 1 class; PCI no letter"),
        new("PW04", "ÉCLAIRZQXVBN", U, R, R, "É is Lu (doc: 'É counts as uppercase'): all upper = 1 class; PCI no digit"),
        new("PW05", "Éclairzqxvbn", U | L, A, R, "accented upper + lower = 2 classes; PCI no digit"),
        new("PW06", "éèêëàâäôöûüç", L, R, R, "accented lowercase only: 1 class"),
        new("PW07", "ᾈᾈᾈᾈᾈᾈzqxvbn", S | L, A, P, "U+1F88 is Lt and survives NFKC: Lt = symbol (TL 11:32) -> 2 classes. PCI: is Lt a letter? OQ-SSE2-30"),
        new("PW08", "ǅǅǅǅǅǅǅǅǅǅǅǅ", U | L, A, R, "U+01C5 (Lt) NFKC -> 'Dž' (Lu+Ll): 2 classes after NFKC; 12 chars before, 24 after"),
        new("PW09", "zqxvbnmlkjhq\u0308", L | S, A, R, "q + U+0308: no precomposed form, Mn stays = symbol -> 2 classes"),
        new("PW10", "zqxvbnmlkjhe\u0301", L, R, R, "e + U+0301 composes to é (Ll) under NFKC -> still 1 class"),
        new("PW11", "密码密码密码密码密码密码", S, R, P, "CJK (Lo) = symbol only: 1 class. PCI: is Lo a letter? OQ-SSE2-30"),
        new("PW12", "密码密码密码密码zqxv", S | L, A, R, "CJK symbol + lower = 2 classes; PCI no digit"),
        new("PW13", "zqxvbnᵃᵇᶜᵈᵉᶠ", L, R, R, "superscript letters (Lm) NFKC -> Ll: 1 class. Without NFKC a client would count Lm as symbol and allow it"),
        new("PW14", "zqxvbnmlkjh²", L | D, A, A, "superscript two NFKC -> '2' (Nd): 2 classes"),
        new("PW15", "zqxv bnml kjh", L | S, A, R, "space = symbol: 2 classes"),
        new("PW16", "𝐀𝐁𝐂𝐃𝐄𝐅zqxvbn", U | L, A, R, "non-BMP math bold capitals NFKC -> A-F (Lu): needs Rune, not char"),
        new("PW17", "zqxvbnmlkjh😀", L | S, A, R, "emoji (So, non-BMP) = symbol"),
        new("PW18", "٣٤٥٦٧٨٩٠١٢zq", D | L, A, P, "Arabic-Indic digits are Nd: 2 classes. PCI: does a non-ASCII Nd count as a number? OQ-SSE2-30"),
        new("PW19", "zqxvbnmlk7", L | D, R, R, "10 chars: below the 12 minimum"),
        new("PW20", "aaaaaaaaaaa1", L | D, R, R, "doc's own example: passes 2 of 4 but is on the breached/common list"),
        new("PW21", "xx" + Username + "9Z", L | S | D | U, R, R, "contains the username (Q28)"),
        new("PW22", new string('z', 127) + "7", L | D, A, A, "128 chars: the hard max is accepted"),
        new("PW23", new string('z', 128) + "7", L | D, R, R, "129 chars: rejected, never truncated (Q28)"),
    };

    public static TheoryData<string> Ids => new(Set.Select(c => c.Id));
    static Case Get(string id) => Set.Single(c => c.Id == id);

    /// PW-ORACLE: the expected classes in the table agree with .NET 10's Unicode tables under the doc's rule. Runs now.
    [Theory, MemberData(nameof(Ids)), Trait("check", "pw-class")]
    public void PW00_table_matches_doc_rule_on_this_runtime(string id)
    {
        var c = Get(id);
        Assert.Equal(c.NevadaClasses, Of(c.Password));
        // Nevada verdict follows from classes and length for every case not decided by a list (PW20, PW21, PW23).
        if (id is not ("PW20" or "PW21" or "PW23"))
        {
            var byRule = Count(Of(c.Password)) >= 2 && c.Password.Length >= 12 ? A : R; // length unit: OQ-SSE2-31 (only ASCII-length-safe cases use it)
            if (id is not ("PW07" or "PW16" or "PW17")) Assert.Equal(c.Nevada, byRule);
        }
    }

    /// Live: the SERVER applies the Nevada preset. Needs a password-set/change route on the stand-in (UI contract).
    [Theory(Skip = "PENDING OQ-SSE2-05/29: no specified way for a test to set a password on the stand-in (UI contract / control API) and which fixture location uses the Nevada preset (OQ-SSE2-13)."),
     MemberData(nameof(Ids)), Trait("check", "pw-class"), Trait("target", "both")]
    public void PW30_server_nevada_verdict(string id) => _ = Get(id);

    [Theory(Skip = "PENDING OQ-SSE2-05/29: as PW30, PCI baseline preset."),
     MemberData(nameof(Ids)), Trait("check", "pw-class"), Trait("target", "both")]
    public void PW31_server_pci_verdict(string id) => _ = Get(id);

    [Fact(Skip = "PENDING OQ-SSE2-31: is the 12-char minimum / 128 max counted before or after NFKC, and in UTF-16 units, code points or graphemes? (PW08: 12 before, 24 after; 127 'z' + one non-BMP char = 128 code points, 129 UTF-16 units)"),
     Trait("check", "pw-class"), Trait("target", "both")]
    public void PW32_length_unit_and_nfkc_order() { }

    [Fact(Skip = "PENDING OQ-SSE2-32: is the contains-username check case-insensitive, and applied before or after NFKC? Are leading/trailing spaces kept?"),
     Trait("check", "pw-class"), Trait("target", "both")]
    public void PW33_username_match_case_and_normalisation() { }

    [Fact(Skip = "PENDING OQ-SSE2-33: does a password that differs only by NFKC (e.g. 'zqxvbnmlkjh²' vs 'zqxvbnmlkjh2') verify as the same password at sign-in? Q28 says NFKC before hashing, so it should; test needs sign-in driver (OQ-SSE2-05)."),
     Trait("check", "pw-class"), Trait("target", "both")]
    public void PW34_nfkc_equivalent_passwords_sign_in_identically() { }
}
