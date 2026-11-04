namespace Login.Entra;

public sealed class EntraOptions
{
    public const string Scheme = "entra-oidc";
    public const string ExternalCookieScheme = "entra-ext";
    public string CallbackPath { get; set; } = "/signin-entra";
    /// <summary>Back-channel timeout for discovery, JWKS and token (TLD-S2B-03). No inline retry.</summary>
    public TimeSpan BackchannelTimeout { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Last-good discovery/JWKS counts as available up to this age (TLD-S2B-03, interim).</summary>
    public TimeSpan LkgMaxAge { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>auth_time must be at most this old at sign-in and for the hand-off (TLD-S2B-08).</summary>
    public TimeSpan MaxAuthAge { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Scope on our API that the access token must carry (proposal §7.1).</summary>
    public string RequiredScope { get; set; } = "login.verify";
    /// <summary>http authorities allowed ONLY when this is true AND the host environment is "Testing" (ENT-CFG-01).</summary>
    public bool AllowInsecureTestAuthority { get; set; }
}
