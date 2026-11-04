namespace Login.Entra;

/// <summary>Contract 5.1 rule 2 for Entra sessions: hand-off needs mapped mfa and auth_time ≤ MaxAuthAge (server-side, TLD-S2B-08).</summary>
public static class HandoffRule
{
    public static bool Allows(IReadOnlyList<string> mappedAmr, DateTimeOffset authTime, DateTimeOffset now, TimeSpan maxAuthAge)
        => mappedAmr.Contains("mfa") && authTime <= now.AddSeconds(30) && now - authTime <= maxAuthAge;
}
