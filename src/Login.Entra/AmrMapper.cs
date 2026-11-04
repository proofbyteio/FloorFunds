namespace Login.Entra;

/// <summary>Entra amr → contract 8.10 closed enum. Plan §a amr table; TLD-S2B-13 (rsa dropped); wiaormfa never mfa.</summary>
public static class AmrMapper
{
    public static readonly IReadOnlySet<string> ClosedEnum = new HashSet<string>(StringComparer.Ordinal)
        { "pwd", "otp", "mfa", "hwk", "swk", "pin", "fpt", "face", "rba", "wia", "sms", "user", "kba", "mca" };

    public static IReadOnlyList<string> Map(IEnumerable<string>? entraAmr)
    {
        var outSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in entraAmr ?? [])
        {
            switch (a)
            {
                case "mfa": case "ngcmfa": outSet.Add("mfa"); break;
                case "pwd": outSet.Add("pwd"); break;
                case "otp": outSet.Add("otp"); break;
                case "wia": outSet.Add("wia"); break;
                default: break; // rsa, wiaormfa, fed, none, unknown: dropped
            }
        }
        return outSet.ToList();
    }
}
