namespace Catalog.Read;

/// <summary>
/// Explicit catalog actor. There is no ambient tenant and no default of super.
/// Super carries no partner. Partner requires a non-empty id.
/// </summary>
public sealed class CatalogReadScope
{
    private readonly string _adminType;
    private readonly Guid? _partnerId;

    private CatalogReadScope(string adminType, Guid? partnerId, string? sessionToken = null)
    {
        _adminType = adminType;
        _partnerId = partnerId;
        SessionToken = sessionToken;
    }

    /// <summary>Raw session token for begin_request. Null until the caller or CatalogSessions supplies one.</summary>
    public string? SessionToken { get; }

    public CatalogReadScope WithSession(string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new CatalogReadContextException("catalog read context is missing or invalid.");
        return new(_adminType, _partnerId, token);
    }

    /// <summary>Scope-kind label for logs. It is not written to app.admin_type.</summary>
    public string AdminType => _adminType;
    public Guid? PartnerId => _partnerId;
    public string ResolvedGlobal => _adminType == "super" ? "on" : "";
    public string ResolvedPartnerCsv => _partnerId is Guid id ? id.ToString("D") : "";

    public bool IsValid =>
        (_adminType == "super" && _partnerId is null)
        || (_adminType == "partner" && _partnerId is Guid id && id != Guid.Empty);

    public static CatalogReadScope Super() => new("super", null);

    public static CatalogReadScope Partner(Guid partnerId)
    {
        if (partnerId == Guid.Empty)
            throw new CatalogReadContextException("catalog read context is missing or invalid.");
        return new("partner", partnerId);
    }

    /// <summary>
    /// Accepts only the exact tokens super and partner. A super scope with a partner id is invalid.
    /// Partner text must be a canonical Guid (D). Empty, malformed, and blank values fail closed.
    /// </summary>
    public static CatalogReadScope Parse(string? adminType, string? partnerText)
    {
        if (adminType == "super")
        {
            if (!string.IsNullOrEmpty(partnerText))
                throw new CatalogReadContextException("catalog read context is missing or invalid.");
            return Super();
        }
        if (adminType == "partner")
        {
            if (partnerText is null || !Guid.TryParseExact(partnerText, "D", out var id) || id == Guid.Empty)
                throw new CatalogReadContextException("catalog read context is missing or invalid.");
            return Partner(id);
        }
        throw new CatalogReadContextException("catalog read context is missing or invalid.");
    }
}

public sealed class CatalogReadContextException : InvalidOperationException
{
    public CatalogReadContextException(string message) : base(message) { }
}

public sealed class CatalogReadRoleException : InvalidOperationException
{
    public CatalogReadRoleException(string message) : base(message) { }
}

/// <summary>
/// Expired, revoked, and logged-out sessions are one result. The SQL texts stay distinct.
/// </summary>
public sealed class CatalogSessionClosedException : Exception
{
    public const int StatusCode = 401;
    public const string Body = "{\"error\":\"session_expired\"}";
    public const string Authenticate = "Bearer";

    public CatalogSessionClosedException() : base("session_expired") { }
}
