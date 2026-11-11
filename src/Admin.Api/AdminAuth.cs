using System.Net;
using System.Text;
using System.Text.Json;

namespace Admin.Api;

/// <summary>
/// Authenticated token identity. AdminType and PartnerId are the sign-in claims.
/// They are not authority. Issuer is the verified iss. The resolver maps (iss, sub) to a user.
/// </summary>
public sealed record AdminIdentity(Guid UserId, string AdminType, Guid? PartnerId, string Issuer = "");

public enum BuiltInRole { None, SuperAdmin, PartnerAdmin }

/// <summary>
/// What the caller holds after the assignment lookup. PartnerIds and LocationIds are the union of active
/// assignments (DEC-199). OrgViewLocations is the injected org:V reader set used by the existing checks.
/// PartnerReasonBoundsWrite is org:partner_reason_bounds:write: Super Admin and ANY Partner Admin, for ANY partner.
/// </summary>
public sealed record AdminGrant(
    BuiltInRole Role,
    bool LoginMethodWrite,
    bool OrgCreateEdit,
    IReadOnlySet<Guid> OrgViewLocations,
    bool PartnerReasonBoundsWrite = false,
    IReadOnlySet<Guid>? PartnerIds = null,
    IReadOnlySet<Guid>? LocationIds = null,
    long PermVersion = 0)
{
    public static readonly AdminGrant Nothing = new(BuiltInRole.None, false, false, new HashSet<Guid>());
    public IReadOnlySet<Guid> AssignedPartners => PartnerIds ?? Empty;
    public IReadOnlySet<Guid> AssignedLocations => LocationIds ?? Empty;
    static readonly HashSet<Guid> Empty = [];
}

/// <summary>
/// Server-side role lookup inside the request transaction. It sees the authenticated identity and the
/// open transaction, never request data. A failure denies.
/// </summary>
public interface IAdminDirectory
{
    Task<AdminGrant> ResolveAsync(AdminIdentity id, Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, CancellationToken ct);
}

/// <summary>The issuer process is not reachable. The host answers 503 and does not open a session itself.</summary>
public sealed class IssuerUnavailableException : Exception;

/// <summary>What the issuer process returns after it has verified the token and opened a session.</summary>
public sealed record IssuedSession(string SessionToken, AdminIdentity Identity);

/// <summary>
/// Sends the raw bearer token to the issuer process. This process does not verify the signature
/// and does not hold the issuer role. Null is a rejected token (401). A connection failure throws
/// <see cref="IssuerUnavailableException"/> (503).
/// </summary>
public sealed class SessionIssuer(AdminApiOptions o)
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public async Task<IssuedSession?> OpenAsync(HttpRequest req, CancellationToken ct)
    {
        var hs = req.Headers.Authorization;
        if (hs.Count != 1 || hs[0] is not { } h || !h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = h[7..].Trim();
        if (token.Length is 0 or > 8192) return null;
        HttpResponseMessage res;
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.IssuerUrl), "/v1/session"))
            { Content = new StringContent(token, Encoding.UTF8, "text/plain") };
            res = await _http.SendAsync(msg, ct);
        }
        catch (HttpRequestException) { throw new IssuerUnavailableException(); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new IssuerUnavailableException(); }
        using (res)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized) return null;
            if (res.StatusCode != HttpStatusCode.OK) throw new IssuerUnavailableException();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var session = root.GetProperty("session").GetString();
            var sub = root.GetProperty("sub").GetString();
            var iss = root.GetProperty("iss").GetString() ?? "";
            var adminType = root.GetProperty("admin_type").GetString() ?? "";
            if (session is not { Length: 64 } || !Guid.TryParseExact(sub, "D", out var userId))
                throw new IssuerUnavailableException();
            Guid? partner = null;
            if (root.TryGetProperty("partner_id", out var p) && p.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(p.GetString(), "D", out var pid))
                partner = pid;
            return new IssuedSession(session, new AdminIdentity(userId, adminType, partner, iss));
        }
    }
}
