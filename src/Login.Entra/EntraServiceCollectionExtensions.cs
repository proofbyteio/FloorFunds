using Login.Entra.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
namespace Login.Entra;

public static class EntraServiceCollectionExtensions
{
    /// <summary>
    /// Registers Entra sign-in. The host must register ILocationLoginConfigReader, IExternalLoginLookup,
    /// ILocationAssignmentCheck, ISecretResolver and IEntraHost (fixture-backed until SSE's land, TLD-S2B-10).
    /// </summary>
    public static AuthenticationBuilder AddEntraLocationSignIn(this AuthenticationBuilder auth, IHostEnvironment env, Action<EntraOptions>? configure = null)
    {
        var opts = new EntraOptions();
        configure?.Invoke(opts);
        if (opts.AllowInsecureTestAuthority && !env.IsEnvironment("Testing"))
            throw new InvalidOperationException("AllowInsecureTestAuthority is only allowed in the Testing environment (ENT-CFG-01)");
        var s = auth.Services;
        s.AddHttpContextAccessor();
        s.TryAddSingleton(TimeProvider.System);
        s.AddSingleton(opts);
        s.AddSingleton(sp => new PerLocationConfigurationManager(sp.GetRequiredService<IHttpContextAccessor>(), opts, sp.GetRequiredService<TimeProvider>()));
        s.AddSingleton<EntraReadiness>();
        s.AddSingleton<EntraAccessTokenValidator>();
        s.AddSingleton<ReplayCache>();
        s.AddScoped<EntraOidcEvents>();
        s.AddScoped<EntraLocationEndpoint>();
        s.AddScoped<EntraSignOut>();

        auth.AddCookie(EntraOptions.ExternalCookieScheme, o => o.Cookie.Name = "__entra_ext"); // never written: we HandleResponse() before sign-in
        auth.AddOpenIdConnect(EntraOptions.Scheme, o =>
        {
            o.ClientId = "per-location"; // replaced per request (RedirectToIdentityProvider / AuthorizationCodeReceived / AudienceValidator)
            o.CallbackPath = opts.CallbackPath;
            o.SignInScheme = EntraOptions.ExternalCookieScheme;
            o.ResponseType = "code";
            o.UsePkce = true;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.MapInboundClaims = false;
            o.Scope.Clear();
            o.BackchannelTimeout = opts.BackchannelTimeout;
            o.RequireHttpsMetadata = !opts.AllowInsecureTestAuthority;
            o.DisableTelemetry = true;
            o.EventsType = typeof(EntraOidcEvents);
            if (opts.AllowInsecureTestAuthority)
            {
                o.CorrelationCookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
                o.NonceCookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
                o.CorrelationCookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
                o.NonceCookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
            }
            o.TokenValidationParameters = new TokenValidationParameters
            {
            TryAllIssuerSigningKeys = false, // unknown kid must fail, not fall through to any key
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                RequireSignedTokens = true,
                ClockSkew = opts.ClockSkew,
                NameClaimType = "name",
            };
        });
        s.AddOptions<OpenIdConnectOptions>(EntraOptions.Scheme).Configure<PerLocationConfigurationManager, IHttpContextAccessor>((o, mgr, http) =>
        {
            o.ConfigurationManager = mgr;
            o.BackchannelHttpHandler = new TokenEndpointObserver(http) { InnerHandler = new HttpClientHandler() };
            EntraLocation Cur() => http.HttpContext?.Items[PerLocationConfigurationManager.ItemKey] as EntraLocation
                                   ?? throw new SecurityTokenValidationException("no location bound (fail closed)");
            o.TokenValidationParameters.AudienceValidator = (aud, _, _) => aud.Contains(Cur().ClientId, StringComparer.Ordinal);
            // iss GUID must equal tid (Microsoft.IdentityModel.Validators); exact-authority check follows in TokenValidated.
            o.TokenValidationParameters.IssuerValidator = (iss, tok, p) =>
            {
                var cur = Cur();
                // Wrong / MSA tenant is a distinct v1 audit reason (tenant_mismatch), so tag it before the generic check.
                var tid = tok is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken j && j.TryGetPayloadValue<string>("tid", out var t) ? t : null;
                if (!Guid.TryParse(tid, out var g) || g != cur.TenantId)
                    throw new SecurityTokenInvalidIssuerException(TenantMismatchMessage);
                return AadIssuerValidator.GetAadIssuerValidator(cur.Authority).Validate(iss, tok, WithIssuer(p, cur.Authority));
            };
        });
        return auth;
    }

    internal const string TenantMismatchMessage = "entra: tid does not match the location tenant";

    private static TokenValidationParameters WithIssuer(TokenValidationParameters p, string authority)
    {
        var c = p.Clone();
        c.ValidIssuers = [authority.Replace(authority.Split('/')[^2], "{tenantid}", StringComparison.Ordinal)];
        return c;
    }
}
