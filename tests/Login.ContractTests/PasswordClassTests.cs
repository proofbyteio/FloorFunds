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
        new("PW24", "\uFB00zqxvbnmlk7", L | D, A, A, "OQ-31: U+FB00 'ff' ligature: 11 code points before NFKC, 12 after -> meets the minimum"),
        new("PW25", new string('z', 126) + "7" + "\U0001D400", U | L | D, A, A, "OQ-31: 128 code points (non-BMP last; 129 UTF-16 units) -> accepted"),
        new("PW26", new string('z', 126) + "7" + "\uFB00", L | D, R, R, "OQ-31: 128 code points before NFKC, 129 after -> rejected"),
        new("PW27", "xxTEST-CASHIER9z", U | L | D | S, R, R, "OQ-32: username match is case-insensitive"),
        new("PW28", "xx\uFF54\uFF45\uFF53\uFF54-\uFF43\uFF41\uFF53\uFF48\uFF49\uFF45\uFF52" + "9Z", L | S | D | U, R, R, "OQ-32: fullwidth username matches after NFKC"),
    };

    public static TheoryData<string> Ids => new(Set.Select(c => c.Id));
    static Case Get(string id) => Set.Single(c => c.Id == id);

    /// PW-ORACLE: the expected classes in the table agree with .NET 10's Unicode tables under the doc's rule. Runs now.
    [Theory, MemberData(nameof(Ids)), Trait("check", "pw-class")]
    public void PW00_table_matches_doc_rule_on_this_runtime(string id)
    {
        var c = Get(id);
        Assert.Equal(c.NevadaClasses, Of(c.Password));
        // Nevada verdict follows from the rule (OQ-SSE2-31/32 ruled) for every case not decided by the breached list (PW20).
        if (id is not "PW20") Assert.Equal(c.Nevada, NevadaByRule(c.Password, Username) ? A : R);
    }

    /// Live: the SERVER applies the Nevada preset. Needs a password-set/change route on the stand-in (UI contract).
    [Theory(Skip = Pending.ControlApi + " OQ-SSE2-29 ruled: PUT /_standin/users/{u} with the shared validator; nevada fixture partner (CR-13)."),
     MemberData(nameof(Ids)), Trait("check", "pw-class"), Trait("target", "both")]
    public void PW30_server_nevada_verdict(string id) => _ = Get(id);

    [Theory(Skip = Pending.ControlApi + " As PW30, pci fixture partner; PW07/PW11/PW18 stay pending on OQ-SSE2-30."),
     MemberData(nameof(Ids)), Trait("check", "pw-class"), Trait("target", "both")]
    public void PW31_server_pci_verdict(string id) => _ = Get(id);

    /// OQ-SSE2-31 ruled: code points after NFKC. Oracle self-check.
    [Fact, Trait("check", "pw-class")]
    public void PW32_length_is_code_points_after_nfkc()
    {
        Assert.Equal(24, Length("ǅǅǅǅǅǅǅǅǅǅǅǅ")); Assert.Equal(12, Length("\uFB00zqxvbnmlk7"));
        Assert.Equal(128, Length(new string('z', 126) + "7" + "\U0001D400")); Assert.Equal(129, (new string('z', 126) + "7" + "\U0001D400").Length);
    }

    /// OQ-SSE2-32 ruled. Oracle self-check.
    [Theory, Trait("check", "pw-class")]
    [InlineData("xxtest-cashier9Z", " test-cashier ", true)] [InlineData("xxTEST-CASHIER9z", "test-cashier", true)]
    [InlineData("xx\uFF54\uFF45\uFF53\uFF54-\uFF43\uFF41\uFF53\uFF48\uFF49\uFF45\uFF52" + "9Z", "test-cashier", true)] [InlineData("xxtest_cashier9Z", "test-cashier", false)]
    public void PW33_username_match_trim_nfkc_lower_ordinal(string pw, string user, bool contains) => Assert.Equal(contains, ContainsUsername(pw, user));

    [Fact(Skip = Pending.SignInDriver + " OQ-SSE2-33 ruled yes: NFKC before hashing, so zqxvbnmlkjh² and zqxvbnmlkjh2 sign in identically."),
     Trait("check", "pw-class"), Trait("target", "both")]
    public void PW34_nfkc_equivalent_passwords_sign_in_identically() { }
}
