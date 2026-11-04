// Deployment Entra values: the ONE source of truth for oidc_authority, oidc_client_id and secret_ref of every entra location
// [TLD 2026-11-04 9:49, D-51] [TLD 2026-11-04 9:50 oidc_*] [TLD 2026-11-04 10:02 Q1] [TLD 2026-11-04 10:12]. Built once at startup
// and handed to the readers. No hot reload: a config change applies to all entra locations after a restart, with no row rewrite.
// The per-row copies in auth.location_login_config are kept only because the DB CHECK needs them (db/ frozen) and are never read
// (V11-ROWSECRETCLEANUP).
// [TLD 2026-11-04 10:58 F-A1, clarified 10:59] The constructor is internal and there is no InternalsVisibleTo: the ONLY way to get an
// instance is Create(), which runs the db8f360 rules (the same ones AdminApiOptions.Validated used; Validated().Entra() now goes
// through Create). So an unchecked value such as "not-an-arn" can never be built.
using System.Text.RegularExpressions;
using Login.Abstractions;

namespace Auth.Data;

public sealed class EntraDeployment
{
    /// <summary>Entra:Authority, validated (spec §5.2, A-8).</summary>
    public string Authority { get; }
    /// <summary>Entra:ClientId, validated and lower-case canonical.</summary>
    public string ClientId { get; }
    /// <summary>Entra:SecretRef, opaque (ToString "[redacted]"); only the secret-resolver path calls Reveal().</summary>
    public SecretReference SecretRef { get; }

    internal EntraDeployment(string authority, string clientId, SecretReference secretRef) =>
        (Authority, ClientId, SecretRef) = (authority, clientId, secretRef);

    /// <summary>Reason codes of a refused Create. A code never contains (or is derived from) the refused value.</summary>
    public static class Reasons
    {
        public const string AuthorityInvalid = "entra_authority_invalid";
        public const string ClientIdInvalid = "entra_client_id_invalid";
        public const string AllowedAccountIdsInvalid = "secret_ref_allowed_account_ids_invalid";
        public const string AllowedRegionsInvalid = "secret_ref_allowed_regions_invalid";
        public const string SecretRefNotAllowed = "entra_secret_ref_not_allowed";
    }

    /// <summary>Either Value (validated) or Reason (one of <see cref="Reasons"/>), never both. ToString prints the reason only.</summary>
    public sealed class Result
    {
        public EntraDeployment? Value { get; }
        public string? Reason { get; }
        public bool Ok => Value is not null;
        internal Result(EntraDeployment? value, string? reason) => (Value, Reason) = (value, reason);
        public override string ToString() => Ok ? "EntraDeployment.Result { ok }" : $"EntraDeployment.Result {{ {Reason} }}";
    }

    static readonly Regex AuthorityRe = new(@"^https://[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?(?::[0-9]{1,5})?\z", RegexOptions.CultureInvariant);
    static readonly Regex AccountRe = new(@"^[0-9]{12}\z", RegexOptions.CultureInvariant);
    static readonly Regex RegionRe = new(@"^[a-z]{2}(?:-[a-z]+)+-[0-9]\z", RegexOptions.CultureInvariant);
    static readonly Regex ArnRe = new(@"^arn:aws:secretsmanager:([a-z0-9-]+):([0-9]{12}):secret:[A-Za-z0-9/_+=.@-]+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The only factory. Runs, in this order, the db8f360 rules: Entra:Authority https://host[:port] (no path, query, fragment,
    /// userinfo, trailing slash); Entra:ClientId a non-empty GUID (stored lower-case canonical); SecretRef:AllowedAccountIds and
    /// SecretRef:AllowedRegions non-empty and well-formed; Entra:SecretRef an AWS Secrets Manager ARN (A-1, at most 2048 chars) in an
    /// allowed account and region (K4). Null values are refused like invalid ones. Never throws for a bad value.
    /// </summary>
    public static Result Create(string? authority, string? clientId, string? secretRef, IReadOnlySet<string>? allowedAccountIds, IReadOnlySet<string>? allowedRegions)
    {
        if (authority is null || !IsValidAuthority(authority)) return new(null, Reasons.AuthorityInvalid);
        if (!Guid.TryParseExact(clientId, "D", out var cid) || cid == Guid.Empty) return new(null, Reasons.ClientIdInvalid);
        if (allowedAccountIds is null || allowedAccountIds.Count == 0 || allowedAccountIds.Any(a => a is null || !AccountRe.IsMatch(a))) return new(null, Reasons.AllowedAccountIdsInvalid);
        if (allowedRegions is null || allowedRegions.Count == 0 || allowedRegions.Any(r => r is null || !RegionRe.IsMatch(r))) return new(null, Reasons.AllowedRegionsInvalid);
        if (secretRef is null || !SecretRefAllowed(secretRef, allowedAccountIds, allowedRegions)) return new(null, Reasons.SecretRefNotAllowed);
        return new(new EntraDeployment(authority, cid.ToString("D"), new SecretReference(secretRef)), null);
    }

    // [SSE2 review 06dc8ac..ba85c77 (3fac1d52) F-C1, under TLD 10:58 F-A1] Nothing public except Create and its return surface
    // (Reasons + consts, Result + Value/Reason/Ok/ToString) and the instance getters: the two predicates are internal; Admin.Api's
    // wrappers go through Create. Admin.Api.Checks "F-C1: public surface" pins this by reflection.
    internal static bool IsValidAuthority(string v) =>
        AuthorityRe.IsMatch(v) && Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == "https" && u.UserInfo == "" &&
        u.AbsolutePath == "/" && u.Query == "" && u.Fragment == "" && !u.Host.Contains("..") && (u.IsDefaultPort || u.Port is > 0 and <= 65535);

    /// <summary>Entra:SecretRef shape (A-1) AND the K4 deployment allowlist of account id and region.</summary>
    internal static bool SecretRefAllowed(string v, IReadOnlySet<string> accountIds, IReadOnlySet<string> regions)
    {
        if (v.Length > 2048) return false;
        var m = ArnRe.Match(v);
        return m.Success && regions.Contains(m.Groups[1].Value) && accountIds.Contains(m.Groups[2].Value);
    }

    public override string ToString() => "EntraDeployment { [redacted] }";
}
