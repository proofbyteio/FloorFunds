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

    /// <summary>
    /// DEC-107 (PM t149u/t150u; TLD 2026-11-04 16:32 Q1=A): the screen 9 reason length, counted in code points after trim, from
    /// LoginConfig:ReasonMinLength / LoginConfig:ReasonMaxLength. Snapshotted once at startup (like Entra:*); no hot reload.
    /// Code defaults, one constant each: min 10, max 500, Confirmed DEC-108 (PM, Nov 5 2026 6:47 AM PT; Mohanraj verbatim "10 to 500"); t151u via TL: not 100.
    /// </summary>
    public const int DefaultReasonMinLength = 10; // Confirmed DEC-108
    public const int DefaultReasonMaxLength = 500;
    /// <summary>
    /// Hard bounds for the two keys = the DB CHECK llcc_reason (db/migrations.sql L94: char_length(reason) BETWEEN 10 AND 500, frozen),
    /// so config can never accept a reason the DB refuses [TLD 2026-11-04 16:32 Q1=A]. Startup requires
    /// ReasonFloor &lt;= min &lt;= max &lt;= ReasonCeiling. Widening needs a new migration (follow-up DB-WIDEN, PQ-238).
    /// </summary>
    public const int ReasonFloor = 10, ReasonCeiling = 500;
    public int ReasonMinLength { get; init; } = DefaultReasonMinLength;
    public int ReasonMaxLength { get; init; } = DefaultReasonMaxLength;

    public static AdminApiOptions FromConfiguration(IConfiguration c)
    {
        string Req(string k) => c[k] is { Length: > 0 } v ? v : throw new InvalidOperationException($"config {k} is required");
        static HashSet<string> List(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        int Int(string k, int dflt) => c[k] is not { } v ? dflt
            : int.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
            : throw new InvalidOperationException($"config {k} must be an integer");
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
            ReasonMinLength = Int("LoginConfig:ReasonMinLength", DefaultReasonMinLength),
            ReasonMaxLength = Int("LoginConfig:ReasonMaxLength", DefaultReasonMaxLength),
        };
    }

    /// <summary>Throws InvalidOperationException (key name only) on any invalid value; returns the normalised options.</summary>
    public AdminApiOptions Validated() => ValidatedWith(out _);

    /// <summary>
    /// The Entra:* / SecretRef:* rules run in EntraDeployment.Create [TLD 2026-11-04 10:58 F-A1]: one rule set, the db8f360 order and
    /// messages. Each refusal reason maps to the message naming the config key, never its value.
    /// </summary>
    AdminApiOptions ValidatedWith(out EntraDeployment entra)
    {
        var r = EntraDeployment.Create(EntraAuthority, EntraClientId, EntraSecretRef, SecretAccountIds, SecretRegions);
        if (!r.Ok) throw new InvalidOperationException(r.Reason switch
        {
            EntraDeployment.Reasons.AuthorityInvalid => "config Entra:Authority must be https://host[:port] (no path, query, fragment, userinfo or trailing slash)",
            EntraDeployment.Reasons.ClientIdInvalid => "config Entra:ClientId must be a GUID",
            EntraDeployment.Reasons.AllowedAccountIdsInvalid => "config SecretRef:AllowedAccountIds must list 12-digit AWS account ids",
            EntraDeployment.Reasons.AllowedRegionsInvalid => "config SecretRef:AllowedRegions must list AWS region names",
            // The message names the keys only: never the ARN, account id or region.
            EntraDeployment.Reasons.SecretRefNotAllowed => "config Entra:SecretRef must be an AWS Secrets Manager ARN in an allowed account and region (SecretRef:AllowedAccountIds / SecretRef:AllowedRegions)",
            _ => "config Entra:* refused (" + r.Reason + ")",
        });
        entra = r.Value!;
        if (!Uri.TryCreate(TokenIssuer, UriKind.Absolute, out var iss) || iss.Scheme is not ("https" or "http")) throw new InvalidOperationException("config BackofficeAuth:Issuer must be an absolute URI");
        if (SigningKeys.Count == 0 || SigningKeys.Any(k => k is not (RsaSecurityKey or JsonWebKey { Kty: "RSA" })))
            throw new InvalidOperationException("config BackofficeAuth:JwksPath must hold at least one RSA signing key (RS256 only)");
        if (!File.Exists(AuditSchemaPath)) throw new InvalidOperationException("audit-events.schema.json not found (Audit:SchemaPath)");
        _ = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (BodyReadTimeout <= TimeSpan.Zero || BodyReadTimeout > TimeSpan.FromSeconds(60)) throw new InvalidOperationException("BodyReadTimeout must be in (0, 60] s");
        if (ReasonMinLength < ReasonFloor || ReasonMaxLength > ReasonCeiling || ReasonMinLength > ReasonMaxLength)
            throw new InvalidOperationException($"config LoginConfig:ReasonMinLength / LoginConfig:ReasonMaxLength must satisfy {ReasonFloor} <= min <= max <= {ReasonCeiling} (DB CHECK llcc_reason)");
        return this with { EntraClientId = entra.ClientId };
    }

    // [F-C1, SSE2 review 3fac1d52] Through EntraDeployment.Create (its predicates are internal). Create checks the authority first and
    // returns AuthorityInvalid exactly when the authority rule fails, so "Reason != AuthorityInvalid" is the old predicate.
    public static bool IsValidAuthority(string v)
    {
        ArgumentNullException.ThrowIfNull(v); // unchanged: the old Regex.IsMatch(null) threw ArgumentNullException
        return EntraDeployment.Create(v, null, null, null, null).Reason != EntraDeployment.Reasons.AuthorityInvalid;
    }

    /// <summary>Records print every member by default; this one holds a connection string and Entra:SecretRef.</summary>
    public override string ToString() => "AdminApiOptions { [redacted] }";

    /// <summary>Entra:SecretRef shape (A-1) AND the K4 deployment allowlist of account id and region (startup only since TLD 9:49).</summary>
    /// [F-C1, SSE2 review 3fac1d52] Through EntraDeployment.Create with a fixed valid authority / client id, so only the list and ARN
    /// rules decide. Same answer as before for every options object whose lists pass Validated() (the only ones the host uses);
    /// stricter (false) when SecretRef:AllowedAccountIds / AllowedRegions are themselves malformed (Create refuses the list first).
    public bool SecretRefAllowed(string v)
    {
        _ = v.Length; // unchanged: null threw NullReferenceException before
        return EntraDeployment.Create(ProbeAuthority, ProbeClientId, v, SecretAccountIds, SecretRegions).Ok;
    }
    const string ProbeAuthority = "https://login.microsoftonline.com", ProbeClientId = "00000000-0000-4000-8000-000000000001";

    /// <summary>
    /// The deployment Entra values for the login readers and the views [TLD 2026-11-04 10:02 Q1]. Runs Validated(), whose Entra
    /// rules ARE EntraDeployment.Create (the only factory, TLD 10:58 F-A1), so an invalid value never reaches a reader.
    /// Built once at startup; no hot reload.
    /// </summary>
    public EntraDeployment Entra()
    {
        _ = ValidatedWith(out var entra);
        return entra;
    }
}
