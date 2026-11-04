using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Admin.Api;

/// <summary>
/// Deployment config for the S2a admin API, validated once at startup; any invalid value makes the API refuse to start
/// (fail closed; spec §3.0 A-21, §5.2, §8.3). Error messages name the config key, never its value.
/// </summary>
public sealed record AdminApiOptions
{
    public const string Audience = "backoffice-api";
    public required string ConnectionString { get; init; }
    /// <summary>Entra:Authority, https://host[:port] base (spec §5.2, A-8).</summary>
    public required string EntraAuthority { get; init; }
    /// <summary>Entra:ClientId, a GUID (lower-case canonical once validated).</summary>
    public required string EntraClientId { get; init; }
    /// <summary>SecretRef:AllowedAccountIds / AllowedRegions (K4, TLD 2026-11-04 8:44).</summary>
    public required IReadOnlySet<string> SecretAccountIds { get; init; }
    public required IReadOnlySet<string> SecretRegions { get; init; }
    /// <summary>BackofficeAuth:Issuer, the iss of access_token_admin.</summary>
    public required string TokenIssuer { get; init; }
    /// <summary>From BackofficeAuth:JwksPath (a JWKS file) in production; set directly by the in-process checks.</summary>
    public required IReadOnlyList<SecurityKey> SigningKeys { get; init; }
    public required string AuditSchemaPath { get; init; }
    public string Urls { get; init; } = "";

    static readonly Regex AuthorityRe = new(@"^https://[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(?::[0-9]{1,5})?\z", RegexOptions.CultureInvariant);
    static readonly Regex AccountRe = new(@"^[0-9]{12}\z", RegexOptions.CultureInvariant);
    static readonly Regex RegionRe = new(@"^[a-z]{2}(?:-[a-z]+)+-[0-9]\z", RegexOptions.CultureInvariant);

    public static AdminApiOptions FromConfiguration(IConfiguration c)
    {
        string Req(string k) => c[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"config {k} is required");
        static HashSet<string> List(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var jwksPath = Req("BackofficeAuth:JwksPath");
        var jwks = new JsonWebKeySet(File.ReadAllText(jwksPath));
        return new AdminApiOptions
        {
            ConnectionString = c["ADMIN_API_CONNECTION_STRING"] is { Length: > 0 } cs ? cs : throw new InvalidOperationException("ADMIN_API_CONNECTION_STRING is required"),
            EntraAuthority = Req("Entra:Authority"),
            EntraClientId = Req("Entra:ClientId"),
            SecretAccountIds = List(c["SecretRef:AllowedAccountIds"]),
            SecretRegions = List(c["SecretRef:AllowedRegions"]),
            TokenIssuer = Req("BackofficeAuth:Issuer"),
            SigningKeys = jwks.GetSigningKeys().ToList(),
            AuditSchemaPath = c["Audit:SchemaPath"] is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "contract", "audit-events.schema.json"),
            Urls = c["Urls"] ?? "",
        };
    }

    /// <summary>Throws InvalidOperationException (key name only) on any invalid value; returns the normalised options.</summary>
    public AdminApiOptions Validated()
    {
        if (!IsValidAuthority(EntraAuthority)) throw new InvalidOperationException("config Entra:Authority must be https://host[:port] (no path, query, fragment, userinfo or trailing slash)");
        if (!Guid.TryParseExact(EntraClientId, "D", out var cid) || cid == Guid.Empty) throw new InvalidOperationException("config Entra:ClientId must be a GUID");
        if (SecretAccountIds.Count == 0 || SecretAccountIds.Any(a => !AccountRe.IsMatch(a))) throw new InvalidOperationException("config SecretRef:AllowedAccountIds must list 12-digit AWS account ids");
        if (SecretRegions.Count == 0 || SecretRegions.Any(r => !RegionRe.IsMatch(r))) throw new InvalidOperationException("config SecretRef:AllowedRegions must list AWS region names");
        if (!Uri.TryCreate(TokenIssuer, UriKind.Absolute, out var iss) || iss.Scheme is not ("https" or "http")) throw new InvalidOperationException("config BackofficeAuth:Issuer must be an absolute URI");
        if (SigningKeys.Count == 0 || SigningKeys.Any(k => k is not (RsaSecurityKey or JsonWebKey { Kty: "RSA" })))
            throw new InvalidOperationException("config BackofficeAuth:JwksPath must hold at least one RSA signing key (RS256 only)");
        if (!File.Exists(AuditSchemaPath)) throw new InvalidOperationException("audit-events.schema.json not found (Audit:SchemaPath)");
        _ = new NpgsqlConnectionStringBuilder(ConnectionString);
        return this with { EntraClientId = cid.ToString("D") };
    }

    public static bool IsValidAuthority(string v) =>
        AuthorityRe.IsMatch(v) && Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo == "" &&
        u.AbsolutePath == "/" && u.Query == "" && u.Fragment == "" && !u.Host.Contains("..") && (u.IsDefaultPort || u.Port is > 0 and <= 65535);

    static readonly Regex ArnRe = new(@"^arn:aws:secretsmanager:([a-z0-9-]+):([0-9]{12}):secret:[A-Za-z0-9/_+=.@-]+\z", RegexOptions.CultureInvariant);

    /// <summary>secret_ref_input shape (A-1) AND the K4 deployment allowlist of account id and region.</summary>
    public bool SecretRefAllowed(string v)
    {
        if (v.Length > 2048) return false;
        var m = ArnRe.Match(v);
        return m.Success && SecretRegions.Contains(m.Groups[1].Value) && SecretAccountIds.Contains(m.Groups[2].Value);
    }
}
