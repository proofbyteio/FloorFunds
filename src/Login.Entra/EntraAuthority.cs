using Login.Entra.Contracts;
using Login.Entra.Drafts;
namespace Login.Entra;

/// <summary>Validated Entra settings for one location. Authority = base + "/{tid}/v2.0", exactly one tenant (TLD-S2B-18).</summary>
public sealed record EntraLocation(LocationLoginConfig Config, Guid TenantId, string Authority, string MetadataAddress, string ClientId, string SecretRef)
{
    public static readonly Guid MsaTenant = Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");

    /// <summary>Null means invalid config → fail closed (TLD-S2B-16).</summary>
    public static EntraLocation? TryCreate(LocationLoginConfig? c, bool allowHttp, out string why)
    {
        why = "";
        if (c is null) { why = "config_missing"; return null; }
        if (c.ProviderKind != LoginProviderKind.Entra) { why = "not_entra"; return null; }
        if (c.EntraAllowedTenantIds is not { Count: 1 }) { why = "tenant_count"; return null; }
        var tid = c.EntraAllowedTenantIds[0];
        if (tid == Guid.Empty || tid == MsaTenant) { why = "tenant_invalid"; return null; }
        if (string.IsNullOrWhiteSpace(c.OidcClientId) || string.IsNullOrWhiteSpace(c.SecretRef)) { why = "client_missing"; return null; }
        if (!Uri.TryCreate(c.OidcAuthority, UriKind.Absolute, out var b) || !string.IsNullOrEmpty(b.Query) || !string.IsNullOrEmpty(b.Fragment))
        { why = "authority_invalid"; return null; }
        if (b.Scheme != Uri.UriSchemeHttps && !(allowHttp && b.Scheme == Uri.UriSchemeHttp && b.IsLoopback)) { why = "authority_not_https"; return null; }
        var authority = c.OidcAuthority!.TrimEnd('/') + "/" + tid.ToString("D") + "/v2.0";
        return new EntraLocation(c, tid, authority, authority + "/.well-known/openid-configuration", c.OidcClientId!, c.SecretRef!);
    }
}
