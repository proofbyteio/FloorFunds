using System.Text.Json.Nodes;

namespace Login.StandIn;

// Audit emission into the control feed (GET /_standin/audit). The envelope follows contract/audit-events.schema.json.
public static class Audit
{
    public const string SchemaVersion = "1.0.0"; // [ASSUMPTION A17] the stand-in emits schema_version 1.0.0.
    public const string Service = "login-standin"; // [ASSUMPTION A18] source.service = "login-standin".

    public static JsonObject Build(DateTimeOffset now, string eventType, string outcome, string? userId, JsonObject details,
        JsonObject? scope = null, string? ip = null, string? userAgent = null, string? sessionId = null)
    {
        var actor = new JsonObject { ["user_id"] = userId };
        if (sessionId is not null) actor["session_id"] = sessionId;
        var source = new JsonObject { ["service"] = Service };
        if (ip is not null) source["ip"] = ip;
        if (userAgent is not null) source["user_agent"] = userAgent;
        var e = new JsonObject
        {
            ["event_id"] = Guid.CreateVersion7(now).ToString(),
            ["schema"] = "cachd.log",
            ["schema_version"] = SchemaVersion,
            ["category"] = "event",
            ["event_type"] = eventType,
            ["occurred_at"] = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["outcome"] = outcome,
            ["is_audit"] = true,
            ["actor"] = actor,
            ["source"] = source,
            ["details"] = details,
        };
        if (scope is not null) e["scope"] = scope;
        return e;
    }

    // Test-only key for username_hmac (schema: stand-in key is test-only). [ASSUMPTION A19] the fixed key is "standin-audit-test-key".
    static readonly byte[] HmacKey = "standin-audit-test-key"u8.ToArray();
    public static string UsernameHmac(string username) =>
        Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(HmacKey,
            System.Text.Encoding.UTF8.GetBytes(username.Trim().Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant())));
}
