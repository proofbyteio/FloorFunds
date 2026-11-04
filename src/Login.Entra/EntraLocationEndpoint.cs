using Login.Entra.Contracts;
using Login.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
namespace Login.Entra;

/// <summary>GET /l/{slug} branch (plan §a request flow). The slug only selects the method; the location goes into protected state.</summary>
public sealed class EntraLocationEndpoint(ILocationLoginConfigReader configs, EntraReadiness readiness, IEntraHost host, EntraOptions opts, ILogger<EntraLocationEndpoint> log)
{
    public enum Outcome { NotFoundOrFailClosed, Database, Challenge, Fallback, Error }

    public async Task<Outcome> HandleAsync(HttpContext http, string slug, string? returnUrl)
    {
        var cfg = await configs.GetBySlugAsync(slug, http.RequestAborted); // TLD-S2B-17
        if (cfg is null)
        {   // TLD-S2B-16: no config row → fail closed (no form). App log only; no v1 reason fits (open item).
            log.LogWarning("entra.config_missing slug_len={Len}", slug.Length);
            await host.RenderErrorAsync(http, null);
            return Outcome.NotFoundOrFailClosed;
        }
        if (cfg.ProviderKind == ProviderKind.Database) { await host.RenderDatabaseFormAsync(http, cfg, viaFallback: false); return Outcome.Database; }

        var loc = EntraLocation.TryCreate(cfg, opts.AllowInsecureTestAuthority, out var why);
        if (loc is null) { log.LogWarning("entra.config_invalid location={L} why={W}", cfg.LocationId, why); await host.RenderErrorAsync(http, cfg.LocationId); return Outcome.Error; }

        http.Items[PerLocationConfigurationManager.ItemKey] = loc;
        var r = await readiness.CheckAsync(loc, http.RequestAborted);
        if (!r.Up)
        {
            await FallbackDecision.ApplyAsync(http, loc, r.Reason, host, log);
            return loc.Config.AllowDbFallback ? Outcome.Fallback : Outcome.Error;
        }
        var props = new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) };
        props.Items[EntraOidcEvents.LocationIdItem] = loc.Config.LocationId.ToString("D");
        props.Items[EntraOidcEvents.RowVersionItem] = loc.Config.RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await http.ChallengeAsync(EntraOptions.Scheme, props);
        return Outcome.Challenge;
    }

    private static string SafeReturn(string? r) => r is { Length: > 0 } && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
