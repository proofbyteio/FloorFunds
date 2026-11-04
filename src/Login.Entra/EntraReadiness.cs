using Microsoft.IdentityModel.Protocols.OpenIdConnect;
namespace Login.Entra;

/// <summary>Why Entra counts as down (plan §c). Logged to the app log only until v1.1 (V11-ENTRA-FALLBACK).</summary>
public enum DownReason { None, DiscoveryFailed, DiscoveryTimeout, JwksFailed, JwksTimeout, TokenEndpointFailed, TokenEndpointTimeout, IdpTemporarilyUnavailable }

public sealed record Readiness(bool Up, DownReason Reason, OpenIdConnectConfiguration? Configuration);

/// <summary>D1 + D2: discovery and JWKS reachable within the timeout, or a last-good copy no older than LkgMaxAge.</summary>
public sealed class EntraReadiness(PerLocationConfigurationManager configs, EntraOptions opts, TimeProvider clock)
{
    public async Task<Readiness> CheckAsync(EntraLocation loc, CancellationToken ct)
    {
        OpenIdConnectConfiguration cfg;
        try { cfg = await configs.GetConfigurationAsync(loc, ct).ConfigureAwait(false); }
        catch (Exception) { return new(false, Classify(loc), null); }

        var docs = configs.Docs(loc);
        var now = clock.GetUtcNow();
        bool Fresh(string? addr) => addr is not null && docs.LastSuccess.TryGetValue(addr, out var t) && now - t <= opts.LkgMaxAge;
        if (!Fresh(loc.MetadataAddress)) return new(false, docs.LastError.GetValueOrDefault(loc.MetadataAddress) == "timeout" ? DownReason.DiscoveryTimeout : DownReason.DiscoveryFailed, null);
        if (!Fresh(cfg.JwksUri) || cfg.SigningKeys.Count == 0)
            return new(false, docs.LastError.GetValueOrDefault(cfg.JwksUri ?? "") == "timeout" ? DownReason.JwksTimeout : DownReason.JwksFailed, null);
        return new(true, DownReason.None, cfg);
    }

    private DownReason Classify(EntraLocation loc)
    {
        var docs = configs.Docs(loc);
        if (docs.LastError.TryGetValue(loc.MetadataAddress, out var d))
            return d == "timeout" ? DownReason.DiscoveryTimeout : DownReason.DiscoveryFailed;
        var jwks = docs.LastError.FirstOrDefault(kv => kv.Key != loc.MetadataAddress);
        if (jwks.Key is not null) return jwks.Value == "timeout" ? DownReason.JwksTimeout : DownReason.JwksFailed;
        return DownReason.DiscoveryFailed;
    }
}
