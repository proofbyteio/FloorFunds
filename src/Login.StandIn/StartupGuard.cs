using System.Net;

namespace Login.StandIn;

// PLAN §3.5 "Refuses to start outside dev"; standin-control.md 1.2; contract 8.7.
public static class StartupGuard
{
    static readonly string[] CloudMarkers = { "ECS_CONTAINER_METADATA_URI", "ECS_CONTAINER_METADATA_URI_V4", "AWS_EXECUTION_ENV" };

    /// <summary>Returns null when start is allowed, otherwise the refusal reason (no secrets).</summary>
    public static string? Check(string environmentName, Func<string, string?> env, IEnumerable<string> bindHosts)
    {
        if (!string.Equals(environmentName, "Development", StringComparison.Ordinal))
            return "ASPNETCORE_ENVIRONMENT must be Development";
        if (env("STANDIN_ALLOW") != "1")
            return "STANDIN_ALLOW must be 1";
        foreach (var m in CloudMarkers)
            if (!string.IsNullOrEmpty(env(m))) return $"cloud runtime detected ({m} is set)";
        foreach (var h in bindHosts)
            if (!IsLoopback(h)) return $"bind address '{h}' is not loopback";
        return null;
    }

    // [ASSUMPTION A3] Loopback only; the PLAN also allows "the box's private network", not needed on the box.
    static bool IsLoopback(string host) =>
        host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
}
