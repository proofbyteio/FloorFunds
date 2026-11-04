using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Admin.Api;

/// <summary>
/// Minimal JSON Schema draft 2020-12 validator for the keyword subset used by contract/audit-events.schema.json
/// (spec §8.5: every outbox row is validated before insert; fail closed). No package: none is approved for .NET.
/// Fail closed: any keyword not in <see cref="Known"/> makes the schema load throw, so the API refuses to start
/// instead of silently ignoring a constraint. Only local "#/..." refs are supported. Cross-checked against Python
/// jsonschema (Draft202012Validator + FormatChecker, date-time via an RFC 3339 regex) by Admin.Api.Checks/crosscheck.py
/// on the corpus Admin.Api.Checks writes (every emitted event plus 9 mutations each).
/// </summary>
public sealed class JsonSchemaLite
{
    static readonly HashSet<string> Assertions =
    [
        "type", "enum", "const", "required", "properties", "additionalProperties", "items", "minItems", "maxItems",
        "uniqueItems", "minLength", "maxLength", "pattern", "minimum", "format", "$ref", "allOf", "anyOf", "oneOf",
        "not", "if", "then", "else", "$defs",
    ];
    static readonly HashSet<string> Annotations = ["$schema", "$id", "$comment", "title", "description", "examples", "default"];
    static readonly HashSet<string> Formats = ["uuid", "date-time"];

    readonly JsonNode _root;
    readonly Dictionary<string, Regex> _regex = new(StringComparer.Ordinal);

    JsonSchemaLite(JsonNode root) { _root = root; }

    public static JsonSchemaLite Load(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("schema is null");
        var s = new JsonSchemaLite(root);
        s.Compile(root, "#");
        return s;
    }

    void Compile(JsonNode? node, string path)
    {
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
        if (node is not JsonObject o) throw new InvalidOperationException($"schema at {path} is not an object or boolean");
        foreach (var (k, val) in o)
        {
            if (Annotations.Contains(k)) continue;
            if (!Assertions.Contains(k)) throw new InvalidOperationException($"unsupported schema keyword '{k}' at {path}");
            switch (k)
            {
                case "properties":
                case "$defs":
                    foreach (var (pk, pv) in (JsonObject)val!) Compile(pv, $"{path}/{k}/{pk}");
                    break;
                case "additionalProperties": case "items": case "not": case "if": case "then": case "else":
                    Compile(val, $"{path}/{k}");
                    break;
                case "allOf": case "anyOf": case "oneOf":
                    var arr = (JsonArray)val!;
                    if (arr.Count == 0) throw new InvalidOperationException($"empty {k} at {path}");
                    for (int i = 0; i < arr.Count; i++) Compile(arr[i], $"{path}/{k}/{i}");
                    break;
                case "$ref":
                    var r = val!.GetValue<string>();
                    if (Resolve(r) is null) throw new InvalidOperationException($"unresolvable $ref '{r}' at {path}");
                    break;
                case "pattern":
                    GetRegex(val!.GetValue<string>());
                    break;
                case "format":
                    var f = val!.GetValue<string>();
                    if (!Formats.Contains(f)) throw new InvalidOperationException($"unsupported format '{f}' at {path}");
                    break;
                case "type":
                    foreach (var t in val is JsonArray ta ? ta.Select(x => x!.GetValue<string>()) : [val!.GetValue<string>()])
                        if (t is not ("object" or "array" or "string" or "integer" or "number" or "boolean" or "null"))
                            throw new InvalidOperationException($"unknown type '{t}' at {path}");
                    break;
            }
        }
    }

    Regex GetRegex(string p)
    {
        if (!_regex.TryGetValue(p, out var re))
            _regex[p] = re = new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return re;
    }

    JsonNode? Resolve(string r)
    {
        if (!r.StartsWith('#')) return null;
        JsonNode? cur = _root;
        foreach (var raw in r.Length <= 2 ? [] : r[2..].Split('/'))
        {
            var seg = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
            cur = cur is JsonObject o && o.TryGetPropertyValue(seg, out var n) ? n : null;
            if (cur is null) return null;
        }
        return cur;
    }

    /// <summary>Returns the first few validation errors (empty = valid).</summary>
    public List<string> Validate(JsonNode? instance)
    {
        var errors = new List<string>();
        Check(_root, instance, "", errors);
        return errors;
    }

    bool Check(JsonNode? schema, JsonNode? inst, string at, List<string>? errors)
    {
        if (schema is JsonValue bv && bv.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (bv.GetValueKind() == JsonValueKind.True) return true;
            errors?.Add($"{at}: false schema"); return false;
        }
        var s = (JsonObject)schema!;
        bool ok = true;
        void Fail(string m) { ok = false; errors?.Add($"{at}: {m}"); }
        var kind = KindOf(inst);

        foreach (var (k, val) in s)
        {
            switch (k)
            {
                case "type":
                    var types = val is JsonArray ta ? ta.Select(x => x!.GetValue<string>()).ToList() : [val!.GetValue<string>()];
                    if (!types.Any(t => TypeMatches(t, inst, kind))) Fail($"type {kind} not in [{string.Join(",", types)}]");
                    break;
                case "const":
                    if (!DeepEquals(val, inst)) Fail("const mismatch");
                    break;
                case "enum":
                    if (!((JsonArray)val!).Any(e => DeepEquals(e, inst))) Fail("not in enum");
                    break;
                case "required":
                    if (inst is JsonObject ro)
                        foreach (var req in (JsonArray)val!)
                            if (!ro.ContainsKey(req!.GetValue<string>())) Fail($"required '{req}' missing");
                    break;
                case "properties":
                    if (inst is JsonObject po)
                        foreach (var (pk, ps) in (JsonObject)val!)
                            if (po.TryGetPropertyValue(pk, out var pv) && !Check(ps, pv, $"{at}/{pk}", errors)) ok = false;
                    break;
                case "additionalProperties":
                    if (inst is JsonObject ao)
                    {
                        var props = s["properties"] as JsonObject;
                        foreach (var (ak, av) in ao)
                            if (props is null || !props.ContainsKey(ak))
                                if (!Check(val, av, $"{at}/{ak}", errors)) ok = false;
                    }
                    break;
                case "items":
                    if (inst is JsonArray ia)
                        for (int i = 0; i < ia.Count; i++) if (!Check(val, ia[i], $"{at}/{i}", errors)) ok = false;
                    break;
                case "minItems":
                    if (inst is JsonArray mi && mi.Count < val!.GetValue<int>()) Fail("too few items");
                    break;
                case "maxItems":
                    if (inst is JsonArray ma && ma.Count > val!.GetValue<int>()) Fail("too many items");
                    break;
                case "uniqueItems":
                    if (inst is JsonArray ua && val!.GetValue<bool>())
                        for (int i = 0; i < ua.Count; i++) for (int j = i + 1; j < ua.Count; j++)
                            if (DeepEquals(ua[i], ua[j])) { Fail("duplicate items"); i = ua.Count; break; }
                    break;
                case "minLength":
                    if (kind == JsonValueKind.String && CodePoints(inst!.GetValue<string>()) < val!.GetValue<int>()) Fail("too short");
                    break;
                case "maxLength":
                    if (kind == JsonValueKind.String && CodePoints(inst!.GetValue<string>()) > val!.GetValue<int>()) Fail("too long");
                    break;
                case "pattern":
                    if (kind == JsonValueKind.String && !GetRegex(val!.GetValue<string>()).IsMatch(inst!.GetValue<string>())) Fail("pattern mismatch");
                    break;
                case "minimum":
                    if (kind == JsonValueKind.Number && Num(inst!) < Num(val!)) Fail("below minimum");
                    break;
                case "format":
                    if (kind == JsonValueKind.String && !FormatOk(val!.GetValue<string>(), inst!.GetValue<string>())) Fail($"bad format {val}");
                    break;
                case "$ref":
                    if (!Check(Resolve(val!.GetValue<string>()), inst, at, errors)) ok = false;
                    break;
                case "allOf":
                    foreach (var sub in (JsonArray)val!) if (!Check(sub, inst, at, errors)) ok = false;
                    break;
                case "anyOf":
                    if (!((JsonArray)val!).Any(sub => Check(sub, inst, at, null))) Fail("anyOf: no branch matched");
                    break;
                case "oneOf":
                    var n = ((JsonArray)val!).Count(sub => Check(sub, inst, at, null));
                    if (n != 1) Fail($"oneOf: {n} branches matched");
                    break;
                case "not":
                    if (Check(val, inst, at, null)) Fail("not: schema matched");
                    break;
                case "if":
                    if (Check(val, inst, at, null))
                    { if (s["then"] is { } th && !Check(th, inst, at, errors)) ok = false; }
                    else if (s["else"] is { } el && !Check(el, inst, at, errors)) ok = false;
                    break;
            }
        }
        return ok;
    }

    static JsonValueKind KindOf(JsonNode? n) => n switch
    {
        null => JsonValueKind.Null,
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        JsonValue v => v.GetValueKind(),
        _ => JsonValueKind.Undefined,
    };

    static bool TypeMatches(string t, JsonNode? inst, JsonValueKind k) => t switch
    {
        "object" => k == JsonValueKind.Object,
        "array" => k == JsonValueKind.Array,
        "string" => k == JsonValueKind.String,
        "boolean" => k is JsonValueKind.True or JsonValueKind.False,
        "null" => k == JsonValueKind.Null,
        "number" => k == JsonValueKind.Number,
        "integer" => k == JsonValueKind.Number && decimal.TryParse(inst!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d),
        _ => false,
    };

    static decimal Num(JsonNode n) => decimal.Parse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    internal static int CodePoints(string s)
    {
        int c = 0;
        foreach (var _ in s.EnumerateRunes()) c++;
        return c;
    }

    static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        var ka = KindOf(a); var kb = KindOf(b);
        if (ka == JsonValueKind.Number && kb == JsonValueKind.Number) return Num(a!) == Num(b!);
        if (ka != kb) return false;
        return ka switch
        {
            JsonValueKind.Object => ((JsonObject)a!).Count == ((JsonObject)b!).Count &&
                ((JsonObject)a!).All(p => ((JsonObject)b!).TryGetPropertyValue(p.Key, out var bv) && DeepEquals(p.Value, bv)),
            JsonValueKind.Array => ((JsonArray)a!).Count == ((JsonArray)b!).Count &&
                ((JsonArray)a!).Select((x, i) => DeepEquals(x, ((JsonArray)b!)[i])).All(x => x),
            JsonValueKind.String => a!.GetValue<string>() == b!.GetValue<string>(),
            _ => true, // true/false/null: kind equality is value equality
        };
    }

    static readonly Regex UuidRe = new("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.CultureInvariant);
    static readonly Regex DateTimeRe = new(@"^(\d{4})-(\d{2})-(\d{2})[Tt](\d{2}):(\d{2}):(\d{2})(\.\d+)?([Zz]|[+-](\d{2}):(\d{2}))$", RegexOptions.CultureInvariant);

    /// <summary>RFC 4122 textual uuid; RFC 3339 date-time (leap second 60 accepted like the reference).</summary>
    static bool FormatOk(string format, string v)
    {
        if (format == "uuid") return UuidRe.IsMatch(v);
        var m = DateTimeRe.Match(v);
        if (!m.Success || v.Any(ch => ch > 127)) return false;
        int y = int.Parse(m.Groups[1].Value), mo = int.Parse(m.Groups[2].Value), d = int.Parse(m.Groups[3].Value);
        int h = int.Parse(m.Groups[4].Value), mi = int.Parse(m.Groups[5].Value), se = int.Parse(m.Groups[6].Value);
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y == 0 ? 2000 : y, mo)) return false;
        if (h > 23 || mi > 59 || se > 60) return false;
        if (m.Groups[9].Success && (int.Parse(m.Groups[9].Value) > 23 || int.Parse(m.Groups[10].Value) > 59)) return false;
        return true;
    }
}
