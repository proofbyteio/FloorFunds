using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Catalog.ScratchAuth;

/// <summary>
/// Resource-server check for one synthetic access_token_admin.
/// Signature, audience, lifetime, and header rules follow the Admin API validator and errors.md.
/// Required claim shape follows claims.schema.json access_token_admin. This type does not grant a catalog role.
/// </summary>
public sealed class FrozenAdminTokenGate
{
    public const string Audience = "backoffice-api";
    public const int MaximumTokenLength = 8192;

    private static readonly HashSet<string> PayloadNames = new(StringComparer.Ordinal)
    {
        "iss", "sub", "aud", "exp", "iat", "nbf", "jti", "scope", "client_id", "login_provider",
        "amr", "auth_time", "x_standin", "session_kind", "admin_type", "partner_id", "cachd_client_id", "perm_version",
    };

    private static readonly HashSet<string> AmrValues = new(StringComparer.Ordinal)
    {
        "pwd", "otp", "mfa", "hwk", "swk", "pin", "fpt", "face", "rba", "wia", "sms", "user", "kba", "mca",
    };

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };
    private readonly TokenValidationParameters _parameters;

    public FrozenAdminTokenGate(string issuer, SecurityKey publicKey)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            throw new ArgumentException("The scratch issuer is required.", nameof(issuer));
        if (publicKey is not RsaSecurityKey)
            throw new ArgumentException("The scratch validation key must be RSA.", nameof(publicKey));
        _parameters = new TokenValidationParameters
        {
            ValidIssuer = issuer,
            IssuerSigningKeys = [publicKey],
            ValidAudience = Audience,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidTypes = ["at+jwt"],
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }

    public bool TryAuthenticate(string token, out Guid userId)
    {
        userId = Guid.Empty;
        if (token.Length is 0 or > MaximumTokenLength)
            return false;
        TokenValidationResult result;
        try
        {
            result = _handler.ValidateTokenAsync(token, _parameters).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return false;
        }
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            return false;
        try
        {
            return TryClaims(jwt, out userId);
        }
        catch (Exception)
        {
            userId = Guid.Empty;
            return false;
        }
    }

    private static bool TryClaims(JsonWebToken jwt, out Guid userId)
    {
        userId = Guid.Empty;
        using var header = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedHeader));
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        if (!HeaderAccepted(header.RootElement) || payload.RootElement.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in payload.RootElement.EnumerateObject())
        {
            if (!PayloadNames.Contains(property.Name))
                return false;
        }
        if (!payload.RootElement.TryGetProperty("sub", out var sub) || !Uuid(sub, out userId) || userId == Guid.Empty)
            return false;
        if (!payload.RootElement.TryGetProperty("aud", out var aud) || aud.ValueKind != JsonValueKind.String || aud.GetString() != Audience)
            return false;
        if (jwt.Audiences.Count() != 1)
            return false;
        if (!payload.RootElement.TryGetProperty("admin_type", out var adminType) || adminType.GetString() is not ("super" or "partner"))
            return false;
        if (!RequiredText(payload.RootElement, "iss") || !RequiredText(payload.RootElement, "jti", 16) || !RequiredText(payload.RootElement, "scope")
            || !RequiredText(payload.RootElement, "client_id") || !NonNegative(payload.RootElement, "iat") || !NonNegative(payload.RootElement, "exp")
            || !NonNegative(payload.RootElement, "auth_time") || !LoginProvider(payload.RootElement) || !Amr(payload.RootElement) || !Session(payload.RootElement))
            return false;
        if (payload.RootElement.TryGetProperty("nbf", out _) && !NonNegative(payload.RootElement, "nbf"))
            return false;
        if (payload.RootElement.TryGetProperty("location_id", out _))
            return false;
        if (!OptionalUuid(payload.RootElement, "cachd_client_id") || !OptionalCount(payload.RootElement, "perm_version") || !StandIn(payload.RootElement))
            return false;
        var partnerPresent = payload.RootElement.TryGetProperty("partner_id", out var partner);
        if (adminType.GetString() == "partner")
            return partnerPresent && Uuid(partner, out var partnerId) && partnerId != Guid.Empty;
        return !partnerPresent;
    }

    private static bool HeaderAccepted(JsonElement header)
    {
        if (header.ValueKind != JsonValueKind.Object)
            return false;
        if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256")
            return false;
        if (!header.TryGetProperty("typ", out var typ) || typ.GetString() != "at+jwt")
            return false;
        return header.TryGetProperty("kid", out var kid) && kid.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(kid.GetString());
    }

    private static bool RequiredText(JsonElement payload, string name, int minimum = 1)
    {
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: var length } && length >= minimum;
    }

    private static bool NonNegative(JsonElement payload, string name)
    {
        return payload.TryGetProperty(name, out var value) && Integer(value, out var number) && number >= 0;
    }

    private static bool Integer(JsonElement value, out long number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number)
            return false;
        var raw = value.GetRawText();
        foreach (var character in raw)
        {
            if (character is < '0' or > '9')
                return false;
        }
        return long.TryParse(raw, out number);
    }

    private static bool Uuid(JsonElement value, out Guid id)
    {
        id = Guid.Empty;
        return value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out id);
    }

    private static bool OptionalUuid(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
            return true;
        return Uuid(value, out var id) && id != Guid.Empty;
    }

    private static bool OptionalCount(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value))
            return true;
        return Integer(value, out var number) && number >= 0;
    }

    private static bool LoginProvider(JsonElement payload)
    {
        return payload.TryGetProperty("login_provider", out var value) && value.GetString() is "database" or "entra";
    }

    private static bool Session(JsonElement payload)
    {
        if (!payload.TryGetProperty("session_kind", out var value))
            return false;
        var kind = value.GetString();
        return kind == "normal";
    }

    private static bool Amr(JsonElement payload)
    {
        if (!payload.TryGetProperty("amr", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || !AmrValues.Contains(name) || !seen.Add(name))
                return false;
        }
        return true;
    }

    private static bool StandIn(JsonElement payload)
    {
        if (!payload.TryGetProperty("x_standin", out var value))
            return true;
        return value.ValueKind == JsonValueKind.True;
    }
}
