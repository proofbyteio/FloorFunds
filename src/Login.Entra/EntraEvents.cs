using System.IdentityModel.Tokens.Jwt;
using Login.Entra.Contracts;
using Login.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
namespace Login.Entra;

/// <summary>OIDC events for scheme "entra-oidc": plan §a.3 checks 1–11 plus D3/D4 down handling.</summary>
public sealed class EntraOidcEvents(
    ILocationLoginConfigReader configs, IExternalLoginReader links, IUserLocationReader assignments,
    ISecretResolver secrets, IEntraHost host, PerLocationConfigurationManager configMgr, EntraAccessTokenValidator atv,
    ReplayCache replay, EntraOptions opts, TimeProvider clock, ILogger<EntraOidcEvents> log) : OpenIdConnectEvents
{
    public const string LocationIdItem = "location_id";
    public const string RowVersionItem = "row_version";
    internal const string TokenDownItem = "entra.token.down";

    public override Task RedirectToIdentityProvider(RedirectContext ctx)
    {
        var loc = Loc(ctx.HttpContext) ?? throw new InvalidOperationException("fail closed: no location");
        var m = ctx.ProtocolMessage;
        m.ClientId = loc.ClientId;
        m.Scope = $"openid profile api://{loc.ClientId}/{opts.RequiredScope}"; // no offline_access (TLD-S2B-07)
        m.Prompt = "login";                                                    // TLD-S2B-08
        m.MaxAge = "0";
        return Task.CompletedTask;
    }

    public override async Task MessageReceived(MessageReceivedContext ctx)
    {
        // Location comes ONLY from the protected state (plan §a, ENT-05).
        // The handler has already unprotected the state (Data Protection) into ctx.Properties; tampered state → null → fail closed.
        var props = ctx.Properties;
        if (props is null || !props.Items.TryGetValue(LocationIdItem, out var lid) || !Guid.TryParse(lid, out var locationId)
            || !props.Items.TryGetValue(RowVersionItem, out var rvs) || !long.TryParse(rvs, out var rv))
        { await Fail(ctx.HttpContext, null, "bad_credentials", "state_invalid:" + (props is null ? "unprotect" : string.Join(",", props.Items.Keys)), "", null); ctx.HandleResponse(); return; }

        var cfg = await configs.GetAsync(locationId, ctx.HttpContext.RequestAborted);
        var loc = EntraLocation.TryCreate(cfg, opts.AllowInsecureTestAuthority, out var why);
        if (loc is null) { await Fail(ctx.HttpContext, locationId, null, "config:" + why, "", null); ctx.HandleResponse(); return; }
        if (loc.Config.RowVersion != rv) { await Fail(ctx.HttpContext, locationId, "bad_credentials", "config_changed", "", null); ctx.HandleResponse(); return; }
        ctx.HttpContext.Items[PerLocationConfigurationManager.ItemKey] = loc;

        if (ctx.ProtocolMessage.Error is "temporarily_unavailable" or "server_error") // D4
        { await Down(ctx.HttpContext, loc, DownReason.IdpTemporarilyUnavailable); ctx.HandleResponse(); }
    }

    public override async Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext ctx)
    {
        var loc = Loc(ctx.HttpContext)!;
        var secret = await secrets.ResolveAsync(loc.SecretRef, ctx.HttpContext.RequestAborted);
        if (string.IsNullOrEmpty(secret)) { await Fail(ctx.HttpContext, loc.Config.LocationId, null, "secret_unresolved", "", null); ctx.HandleResponse(); return; }
        ctx.TokenEndpointRequest!.ClientId = loc.ClientId;
        ctx.TokenEndpointRequest.ClientSecret = secret;
    }

    public override async Task TokenValidated(TokenValidatedContext ctx)
    {
        var http = ctx.HttpContext;
        var loc = Loc(http)!;
        var id = ctx.SecurityToken;
        string? C(string n) => id.Claims.FirstOrDefault(c => c.Type == n)?.Value;
        var tid = C("tid"); var oid = C("oid"); var iss = id.Issuer;
        var subject = $"{oid}@{tid}";
        async Task Refuse(string v1, string detail) { await Fail(http, loc.Config.LocationId, v1, detail, subject, null); ctx.HandleResponse(); }

        // 2 tenant (TLD-S2B-18: exactly the one tenant; MSA refused)
        if (!Guid.TryParse(tid, out var tidG) || tidG == EntraLocation.MsaTenant || tidG != loc.TenantId) { await Refuse("tenant_mismatch", "tenant"); return; }
        if (!string.Equals(iss, loc.Authority, StringComparison.Ordinal)) { await Refuse("tenant_mismatch", "issuer_not_tenant_authority"); return; }
        if (!Guid.TryParse(oid, out var oidG)) { await Refuse("bad_credentials", "oid_missing"); return; }
        // 3 guest
        if (C("acct") == "1" || (C("idp") is { } idp && idp != iss)) { await Refuse("bad_credentials", "guest_account"); return; }
        // 4 access token for our API
        var cfg = await configMgr.GetConfigurationAsync(loc, http.RequestAborted);
        var at = await atv.ValidateAsync(ctx.TokenEndpointResponse?.AccessToken, loc, cfg.SigningKeys, tid!, oid!);
        if (!at.Ok) { await Refuse("bad_credentials", "token_invalid:" + at.Detail); return; }
        // 5 replay
        if (!replay.TryUse(at.Token!.GetPayloadValue<string>("uti"), at.Token.ValidTo == DateTime.MinValue ? clock.GetUtcNow().AddHours(1) : new DateTimeOffset(at.Token.ValidTo, TimeSpan.Zero)))
        { await Refuse("bad_credentials", "uti_replay"); return; }
        // 6 auth_time required, ≤ MaxAuthAge (TLD-S2B-08)
        if (!long.TryParse(C("auth_time"), out var atSec)) { await Refuse("stale_auth_time", "auth_time_missing"); return; }
        var authTime = DateTimeOffset.FromUnixTimeSeconds(atSec);
        var now = clock.GetUtcNow();
        if (authTime > now + opts.ClockSkew || now - authTime > opts.MaxAuthAge) { await Refuse("stale_auth_time", "stale_auth_time"); return; }
        // 7 link on (issuer, tid, oid) only (TLD-S2B-06)
        // Abstractions: null = no external_login row; throws on DB error or unknown status (fail closed, UserStatusText.Parse).
        var user = await links.FindUserAsync(ProviderKind.Entra, loc.Authority, tidG, oidG, http.RequestAborted);
        if (user is null) { await Refuse("bad_credentials", "unknown_subject"); return; }
        // 8 assignment
        if (!await assignments.IsAssignedAsync(user.UserId, loc.Config.LocationId, http.RequestAborted)) { await Refuse("bad_credentials", "location_not_assigned"); return; }
        // 9 user state: explicit checks first so the reason codes survive; MaySignIn is the final guard
        // (only an explicit Active and not locked passes; never trust default(UserStatus), SSE N1).
        if (user.LockedUntil is { } lu && lu > now) { await Refuse("locked", "locked"); return; }
        if (user.Status == UserStatus.Disabled) { await Refuse("bad_credentials", "disabled"); return; }
        if (user.Status != UserStatus.Active || !user.MaySignIn(now)) { await Refuse("bad_credentials", "user_status_unknown"); return; }
        // 10 amr (TLD-S2B-12/13/20)
        var amr = AmrMapper.Map(at.Token.TryGetPayloadValue<string[]>("amr", out var raw) ? raw : null);
        if (loc.Config.MfaRequired && !amr.Contains("mfa")) { await Refuse("mfa_missing", "mfa_missing"); return; }
        if (amr.Count == 0) { await Refuse("bad_credentials", "amr_missing"); return; }
        // 11 success
        await host.WriteAuditAsync(new("auth.login.succeeded", "entra", null, subject, loc.Config.LocationId, user.UserId, amr, authTime), http.RequestAborted);
        await host.CompleteSignInAsync(http, new(user.UserId, user.PartnerId, loc.Config.LocationId, tidG, oidG, amr, authTime, ctx.Properties?.RedirectUri));
        ctx.HandleResponse(); // we never keep Entra tokens or an external cookie
    }

    public override async Task AuthenticationFailed(AuthenticationFailedContext ctx)
    {
        var http = ctx.HttpContext;
        var loc = Loc(http);
        if (loc is not null && http.Items[TokenDownItem] is DownReason d) { await Down(http, loc, d); ctx.HandleResponse(); return; }
        var detail = ctx.Exception is OpenIdConnectProtocolException pe ? "protocol:" + Short(pe.Message) : "token_invalid:" + ctx.Exception.GetType().Name;
        var reason = "bad_credentials";
        if (ctx.Exception is Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException ie
            && ie.Message.Contains(EntraServiceCollectionExtensions.TenantMismatchMessage, StringComparison.Ordinal))
        { reason = "tenant_mismatch"; detail = "tenant"; }
        await Fail(http, loc?.Config.LocationId, reason, detail, "", null);
        ctx.HandleResponse();
    }

    public override async Task RemoteFailure(RemoteFailureContext ctx)
    {
        var http = ctx.HttpContext;
        var loc = Loc(http);
        if (loc is not null && http.Items[TokenDownItem] is DownReason d) { await Down(http, loc, d); ctx.HandleResponse(); return; }
        await Fail(http, loc?.Config.LocationId, null, "remote_failure:" + ctx.Failure?.GetType().Name, "", null);
        ctx.HandleResponse();
    }

    private static string Short(string s) => s.Length > 80 ? s[..80] : s;
    private static EntraLocation? Loc(HttpContext http) => http.Items[PerLocationConfigurationManager.ItemKey] as EntraLocation;

    /// <summary>Refusal: v1 audit with an existing reason (TLD-S2B-09); the detail goes to the app log only.</summary>
    private async Task Fail(HttpContext http, Guid? locationId, string? v1Reason, string detail, string subject, IReadOnlyList<string>? amr)
    {
        var eventId = Guid.NewGuid();
        log.LogWarning("entra.refused event={EventId} location={Location} v1_reason={Reason} detail={Detail} v11=V11-ENTRA-FALLBACK",
            eventId, locationId, v1Reason ?? "(no audit)", detail);
        if (v1Reason is not null)
            await host.WriteAuditAsync(new("auth.login.failed", "entra", v1Reason, subject, locationId, null, amr, null), http.RequestAborted);
        await host.RenderErrorAsync(http, locationId);
    }

    /// <summary>Server-side fallback decision (TLD-S2B-15). Down reasons are app-log only until v1.1 (TLD-S2B-09).</summary>
    internal async Task Down(HttpContext http, EntraLocation loc, DownReason reason)
        => await FallbackDecision.ApplyAsync(http, loc, reason, host, log);
}

public static class FallbackDecision
{
    public static async Task ApplyAsync(HttpContext http, EntraLocation loc, DownReason reason, IEntraHost host, ILogger log)
    {
        log.LogWarning("entra.down location={Location} fallback_reason={Reason} fallback={Fallback} v11=V11-ENTRA-FALLBACK",
            loc.Config.LocationId, reason, loc.Config.AllowDbFallback ? "used" : "disabled");
        if (loc.Config.AllowDbFallback) await host.RenderDatabaseFormAsync(http, loc.Config, viaFallback: true);
        else await host.RenderErrorAsync(http, loc.Config.LocationId);
    }
}

/// <summary>Observes the token endpoint (D3): 5xx, 429, connect error or timeout → down. 4xx OAuth errors are not down.</summary>
public sealed class TokenEndpointObserver(IHttpContextAccessor http) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var r = await base.SendAsync(request, ct);
            if ((int)r.StatusCode >= 500 || (int)r.StatusCode == 429) Mark(DownReason.TokenEndpointFailed);
            return r;
        }
        catch (Exception ex)
        {
            Mark(TrackingDocumentRetriever.IsTimeout(ex) || ex is OperationCanceledException ? DownReason.TokenEndpointTimeout : DownReason.TokenEndpointFailed);
            throw;
        }
    }
    private void Mark(DownReason d) { if (http.HttpContext is { } c) c.Items[EntraOidcEvents.TokenDownItem] = d; }
}
