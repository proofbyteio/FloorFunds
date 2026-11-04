using System.Text.RegularExpressions;
using Auth.Data;
using Login.Abstractions;
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
    /// <summary>
    /// Entra:SecretRef, the ONE app registration's client-credential reference per deployment [TLD 2026-11-04 9:49, D-51 TL 7:40]:
    /// an AWS Secrets Manager ARN (A-1) whose account id and region are in the SecretRef allowlists. Server-filled like
    /// Entra:ClientId; clients never send it. Never logged, never echoed (not even in a startup refusal).
    /// </summary>
    public required string EntraSecretRef { get; init; }
    /// <summary>SecretRef:AllowedAccountIds / AllowedRegions (K4, TLD 2026-11-04 8:44).</summary>
    public required IReadOnlySet<string> SecretAccountIds { get; init; }
    public required IReadOnlySet<string> SecretRegions { get; init; }
    /// <summary>BackofficeAuth:Issuer, the iss of access_token_admin.</summary>
    public required string TokenIssuer { get; init; }
    /// <summary>From BackofficeAuth:JwksPath (a JWKS file) in production; set directly by the in-process checks.</summary>
    public required IReadOnlyList<SecurityKey> SigningKeys { get; init; }
    public required string AuditSchemaPath { get; init; }
    public string Urls { get; init; } = "";
    /// <summary>
    /// Whole-body read deadline [TLD 2026-11-04 9:53/10:02, P3-7]: a body (under the 64 KiB cap) not fully received in time gets
    /// 408 request_timeout. Code default 10 s (DRAFT; no config key); the in-process checks set a short value.
    /// </summary>
    public TimeSpan BodyReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

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
            EntraSecretRef = Req("Entra:SecretRef"),
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
        // After the allowlists (it is checked against them). The message names the keys only: never the ARN, account id or region.
        if (!SecretRefAllowed(EntraSecretRef)) throw new InvalidOperationException("config Entra:SecretRef must be an AWS Secrets Manager ARN in an allowed account and region (SecretRef:AllowedAccountIds / SecretRef:AllowedRegions)");
        if (!Uri.TryCreate(TokenIssuer, UriKind.Absolute, out var iss) || iss.Scheme is not ("https" or "http")) throw new InvalidOperationException("config BackofficeAuth:Issuer must be an absolute URI");
        if (SigningKeys.Count == 0 || SigningKeys.Any(k => k is not (RsaSecurityKey or JsonWebKey { Kty: "RSA" })))
            throw new InvalidOperationException("config BackofficeAuth:JwksPath must hold at least one RSA signing key (RS256 only)");
        if (!File.Exists(AuditSchemaPath)) throw new InvalidOperationException("audit-events.schema.json not found (Audit:SchemaPath)");
        _ = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (BodyReadTimeout <= TimeSpan.Zero || BodyReadTimeout > TimeSpan.FromSeconds(60)) throw new InvalidOperationException("BodyReadTimeout must be in (0, 60] s");
        return this with { EntraClientId = cid.ToString("D") };
    }

    public static bool IsValidAuthority(string v) =>
        AuthorityRe.IsMatch(v) && Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo == "" &&
        u.AbsolutePath == "/" && u.Query == "" && u.Fragment == "" && !u.Host.Contains("..") && (u.IsDefaultPort || u.Port is > 0 and <= 65535);

    static readonly Regex ArnRe = new(@"^arn:aws:secretsmanager:([a-z0-9-]+):([0-9]{12}):secret:[A-Za-z0-9/_+=.@-]+\z", RegexOptions.CultureInvariant);

    /// <summary>Records print every member by default; this one holds a connection string and Entra:SecretRef.</summary>
    public override string ToString() => "AdminApiOptions { [redacted] }";

    /// <summary>Entra:SecretRef shape (A-1) AND the K4 deployment allowlist of account id and region (startup only since TLD 9:49).</summary>
    public bool SecretRefAllowed(string v)
    {
        if (v.Length > 2048) return false;
        var m = ArnRe.Match(v);
        return m.Success && SecretRegions.Contains(m.Groups[1].Value) && SecretAccountIds.Contains(m.Groups[2].Value);
    }

    /// <summary>
    /// The deployment Entra values for the login readers and the views [TLD 2026-11-04 10:02 Q1]. Runs Validated() first (the rules
    /// above, unchanged since db8f360, TLD 10:12), so an invalid value never reaches a reader. Built once at startup; no hot reload.
    /// </summary>
    public EntraDeployment Entra()
    {
        var v = Validated();
        return new EntraDeployment(v.EntraAuthority, v.EntraClientId, new SecretReference(v.EntraSecretRef));
    }
}
