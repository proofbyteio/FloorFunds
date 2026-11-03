using System.Text.Json.Nodes;
using Json.Schema;

namespace Cachd.Login.TestKit;

/// Validates one emitted audit event against the root of contract/audit-events.schema.json (allOf envelope + oneOf event),
/// with format validation on (same options as ContractArtifactTests, finding CR-01).
public static class AuditSchema
{
    static JsonSchema? _s;
    static readonly EvaluationOptions Opts = new() { RequireFormatValidation = true, OutputFormat = OutputFormat.List };
    static JsonSchema S
    {
        get
        {
            if (_s is not null) return _s;
            var o = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Contract, "audit-events.schema.json")))!.AsObject();
            o.Remove("$id"); o["$id"] = $"urn:sse2:audit:{Guid.NewGuid():N}";
            return _s = JsonSchema.FromText(o.ToJsonString());
        }
    }
    /// null when valid; otherwise the first few error messages.
    public static string? Validate(JsonObject e)
    {
        var r = S.Evaluate(System.Text.Json.JsonDocument.Parse(e.ToJsonString()).RootElement, Opts);
        if (r.IsValid) return null;
        var msgs = (r.Details ?? new List<EvaluationResults>()).Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}")).Distinct().Take(4);
        return string.Join("; ", msgs);
    }
}
