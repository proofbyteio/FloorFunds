using Login.Entra.Contracts;
using Login.Entra.Drafts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
namespace Login.Entra;

/// <summary>Contract 11.1: OUR session is revoked first, always; then (Entra locations only) the Entra end-session URL.</summary>
public sealed class EntraSignOut(ILocationLoginConfigReader configs, EntraReadiness readiness, EntraOptions opts, ILogger<EntraSignOut> log)
{
    /// <returns>The Entra end-session URL to 302 to, or null (host redirects to its own page).</returns>
    public async Task<string?> SignOutAsync(HttpContext http, Guid locationId, string postLogoutRedirectUri, Func<Task> revokeOurSession)
    {
        await revokeOurSession(); // step 1: ours, before anything that can fail
        try
        {
            var loc = EntraLocation.TryCreate(await configs.GetAsync(locationId, http.RequestAborted), opts.AllowInsecureTestAuthority, out _);
            if (loc is null) return null;
            http.Items[PerLocationConfigurationManager.ItemKey] = loc;
            var r = await readiness.CheckAsync(loc, http.RequestAborted);
            if (!r.Up || string.IsNullOrEmpty(r.Configuration!.EndSessionEndpoint))
            { log.LogInformation("entra_endsession=skipped location={L} reason={R}", locationId, r.Reason); return null; }
            log.LogInformation("entra_endsession=redirected location={L}", locationId);
            // No id_token_hint (we keep no Entra tokens); no logout_hint (TLD-S2B-14, next slice).
            return r.Configuration.EndSessionEndpoint
                   + "?client_id=" + Uri.EscapeDataString(loc.ClientId)
                   + "&post_logout_redirect_uri=" + Uri.EscapeDataString(postLogoutRedirectUri);
        }
        catch (Exception ex)
        {
            log.LogWarning("entra_endsession=skipped location={L} error={E}", locationId, ex.GetType().Name);
            return null;
        }
    }
}
