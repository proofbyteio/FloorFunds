using System.Net.Http;

namespace Cachd.Login.TestKit;

/// <summary>
/// Which login implementation the shared suite runs against (build plan §3.4: LOGIN_CONTRACT_TARGET=standin|real).
/// The URL variables are NOT specified by the plan or contract yet: names are SSE2's proposal, tracked as OQ-SSE2-03.
/// </summary>
public static class Target
{
    public const string EnvTarget = "LOGIN_CONTRACT_TARGET";
    public const string EnvFront = "LOGIN_FRONT_URL";     // e.g. http://localhost:5080 (plan §3.3 "e.g. 5080")
    public const string EnvBack = "LOGIN_BACK_URL";       // e.g. http://127.0.0.1:8084 (plan §3.3)
    public const string EnvControl = "LOGIN_STANDIN_CONTROL_URL"; // stand-in only, 127.0.0.1 control listener (contract standin-control.md 1.1, CR-05)

    public static string? Name => Environment.GetEnvironmentVariable(EnvTarget)?.Trim().ToLowerInvariant();
    public static bool IsStandIn => Name == "standin";
    public static bool IsReal => Name == "real";
    public static string? FrontUrl => Environment.GetEnvironmentVariable(EnvFront)?.TrimEnd('/');
    public static string? BackUrl => Environment.GetEnvironmentVariable(EnvBack)?.TrimEnd('/');
    public static string? ControlUrl => Environment.GetEnvironmentVariable(EnvControl)?.TrimEnd('/');

    /// <summary>Null when a live target is configured and answering; otherwise a human-readable skip reason.</summary>
    public static string? LiveSkipReason(bool needsBack = false)
    {
        if (Name is null) return $"no target: {EnvTarget} is not set (standin|real). Nothing to test live yet.";
        if (Name is not ("standin" or "real")) return $"{EnvTarget}='{Name}' is not one of standin|real.";
        if (IsReal) return "target 'real' does not exist yet (build plan: real service is slice S2).";
        if (FrontUrl is null) return $"{EnvFront} is not set (variable name proposed by SSE2, OQ-SSE2-03).";
        if (!Reachable(FrontUrl)) return $"stand-in front listener not reachable at {FrontUrl} (stand-in not built/running yet).";
        if (needsBack && (BackUrl is null || !Reachable(BackUrl))) return $"back-channel listener not set/reachable ({EnvBack}={BackUrl ?? "unset"}).";
        return null;
    }

    /// <summary>For tests tagged standin-only (control API /_standin/*).</summary>
    public static string? StandInOnlySkipReason()
    {
        if (!IsStandIn) return $"standin-only test; {EnvTarget}={Name ?? "unset"}.";
        var live = LiveSkipReason();
        if (live is not null) return live;
        if (ControlUrl is null || !Reachable(ControlUrl)) return $"stand-in control listener not set/reachable ({EnvControl}={ControlUrl ?? "unset"}).";
        return null;
    }

    private static readonly Dictionary<string, bool> Cache = new();
    public static bool Reachable(string baseUrl)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(baseUrl, out var ok)) return ok;
            try
            {
                using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var r = h.GetAsync(baseUrl).GetAwaiter().GetResult();
                ok = true;
            }
            catch { ok = false; }
            Cache[baseUrl] = ok;
            return ok;
        }
    }

    public static HttpClient NewClient(bool followRedirects = false) =>
        new(new HttpClientHandler { AllowAutoRedirect = followRedirects, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
}
