using System.Globalization;
using System.Text;
using System.Text.Json;
using Catalog.Contracts;
using Catalog.Contracts.Doubles;

namespace Catalog.Transport;

/// <summary>
/// Closed HTTP parsing and serialization in front of the accepted catalog double.
/// There is no socket, host, database, or auth middleware. The principal is the caller-supplied
/// server context. Body fields and extra headers cannot choose an actor, tenant, or owner.
/// Status codes are the proposed contract mapping, not an auth or security acceptance.
/// </summary>
public sealed class CatalogHttpTransport
{
    private const int IdempotencyKeyLimit = 200;

    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Type", "Content-Length", "Accept", "Idempotency-Key", "If-Match",
    };

    private static readonly HashSet<string> AuthorityFields = new(StringComparer.Ordinal)
    {
        "actor", "actor_user_id", "user_id", "admin_type", "on_behalf_of", "impersonate",
        "delegated_actor", "owner_partner_id", "owner", "acting_as", "partner_header",
        "schema_version", "idempotency_key", "correlation_id", "source_request_id",
    };

    private readonly CatalogDomainService _service;

    public CatalogHttpTransport(CatalogDomainService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public SyntheticHttpResponse Handle(CatalogHttpServerContext context, string rawRequest)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rawRequest);
        return Handle(context, Encoding.UTF8.GetBytes(rawRequest));
    }

    public SyntheticHttpResponse Handle(CatalogHttpServerContext context, ReadOnlyMemory<byte> rawRequest)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HttpFraming.TryRead(rawRequest.Span, out var framed, out var failure) || framed is null)
            return MapFraming(failure);
        if (!TryHeaders(framed, out failure))
            return HttpMessages.Respond(failure);
        if (!TryRoute(framed.Target, out var parsed, out failure))
            return HttpMessages.Respond(failure);
        if (!TryBindMethod(framed.Method, parsed, out var route, out failure))
            return HttpMessages.Respond(failure);
        if (!TryMedia(framed, route, out failure))
            return HttpMessages.Respond(failure);
        if (!TryUnusedHeaders(framed, route, out failure))
            return HttpMessages.Respond(failure);

        var call = new CatalogCall(framed.Headers.GetValueOrDefault("Idempotency-Key"), context.CorrelationId, context.SourceRequestId);
        return route.Kind switch
        {
            RouteKind.List => FromOutcome(List(context, route)),
            RouteKind.Read => One(context, route, (actor, family, id) => _service.Read(actor, family, id)),
            RouteKind.History => One(context, route, (actor, family, id) => _service.History(actor, family, id)),
            RouteKind.Preview => Preview(context, framed, route),
            RouteKind.Create => Write(context, framed, route, call, versioned: false, body => CatalogCommandParser.ParseCreate(route.FamilyWire, body)),
            RouteKind.ClaimEdit => Write(context, framed, route, call, versioned: true, body => CatalogCommandParser.ParseClaimEdit(route.FamilyWire, route.EntryId, body)),
            RouteKind.EntryActivation => Write(context, framed, route, call, versioned: true, body => CatalogCommandParser.ParseEntryActivation(route.FamilyWire, route.EntryId, body)),
            RouteKind.PartnerActivation => Write(context, framed, route, call, versioned: true, body => CatalogCommandParser.ParsePartnerActivation(route.FamilyWire, route.EntryId, route.ScopeId, body)),
            RouteKind.LocationActivation => Write(context, framed, route, call, versioned: true, body => CatalogCommandParser.ParseLocationActivation(route.FamilyWire, route.EntryId, route.ScopeId, body)),
            _ => HttpMessages.Respond(new HttpFailure(400, "unknown_route", "unknown_route")),
        };
    }

    private static SyntheticHttpResponse FromOutcome(CatalogOutcome outcome) =>
        HttpMessages.Respond((int)outcome.Status, ResponseJson.Outcome(outcome));

    private CatalogOutcome List(CatalogHttpServerContext context, Route route)
    {
        if (!CatalogFamilies.TryParse(route.FamilyWire, out var family))
            return new CatalogOutcome { Status = ProposedStatus.BadRequest, Code = "invalid_family", Detail = "invalid_family", Wrote = false };
        return _service.List(context.Principal, family);
    }

    private SyntheticHttpResponse One(CatalogHttpServerContext context, Route route, Func<CatalogPrincipal, CatalogFamily, Guid, CatalogOutcome> read)
    {
        if (!CatalogFamilies.TryParse(route.FamilyWire, out var family))
            return HttpMessages.Respond(new HttpFailure(400, "invalid_family", "invalid_family"));
        var outcome = read(context.Principal, family, route.EntryId);
        return HttpMessages.Respond((int)outcome.Status, ResponseJson.Outcome(outcome));
    }

    private SyntheticHttpResponse Preview(CatalogHttpServerContext context, FramedRequest request, Route route)
    {
        if (!CatalogFamilies.TryParse(route.FamilyWire, out var family))
            return HttpMessages.Respond(new HttpFailure(400, "invalid_family", "invalid_family"));
        if (!TryClosedPreviewBody(request.Body, out var failure))
            return HttpMessages.Respond(failure);
        var outcome = _service.Preview(context.Principal, family, route.EntryId, context.Graph);
        return HttpMessages.Respond((int)outcome.Status, ResponseJson.Outcome(outcome));
    }

    private SyntheticHttpResponse Write(
        CatalogHttpServerContext context,
        FramedRequest request,
        Route route,
        CatalogCall call,
        bool versioned,
        Func<string, CatalogCommand> parse)
    {
        if (string.IsNullOrWhiteSpace(call.IdempotencyKey) || call.IdempotencyKey.Length > IdempotencyKeyLimit)
            return HttpMessages.Respond(new HttpFailure(400, "idempotency_key_required", "idempotency_key_required"));
        if (!TryVersion(request, versioned, out var body, out var failure))
            return HttpMessages.Respond(failure);
        var outcome = _service.Execute(context.Principal, call, parse(body));
        return HttpMessages.Respond((int)outcome.Status, ResponseJson.Outcome(outcome));
    }

    private static SyntheticHttpResponse MapFraming(HttpFailure failure)
    {
        if (failure.Detail == "query")
            return HttpMessages.Respond(new HttpFailure(400, "unknown_query", "unknown_query"));
        return HttpMessages.Respond(failure);
    }

    private static bool TryHeaders(FramedRequest request, out HttpFailure failure)
    {
        foreach (var name in request.Headers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!AllowedHeaders.Contains(name))
            {
                failure = new HttpFailure(400, "unknown_header", name.ToLowerInvariant());
                return false;
            }
        }
        if (request.Headers.TryGetValue("Accept", out var accept) && !AcceptsJson(accept))
        {
            failure = new HttpFailure(400, "unsupported_media_type", "accept");
            return false;
        }
        failure = default;
        return true;
    }

    private static bool AcceptsJson(string accept)
    {
        var type = accept.Split(';', 2)[0].Trim();
        return type is "*/*" or "application/json";
    }

    private static bool TryMedia(FramedRequest request, Route route, out HttpFailure failure)
    {
        var hasBody = request.Body.Length > 0;
        if (!route.AllowsBody && hasBody)
        {
            failure = new HttpFailure(400, "unexpected_body", "unexpected_body");
            return false;
        }
        if (!hasBody)
        {
            failure = default;
            return true;
        }
        if (!request.Headers.TryGetValue("Content-Type", out var contentType) || !IsJson(contentType))
        {
            failure = new HttpFailure(400, "unsupported_media_type", "content_type");
            return false;
        }
        failure = default;
        return true;
    }

    private static bool IsJson(string contentType)
    {
        var type = contentType.Split(';', 2)[0].Trim();
        if (!type.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return false;
        var parameters = contentType.Split(';').Skip(1);
        foreach (var parameter in parameters)
        {
            var parts = parameter.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase)
                && !parts[1].Trim().Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool TryUnusedHeaders(FramedRequest request, Route route, out HttpFailure failure)
    {
        if (!route.IsWrite && request.Headers.ContainsKey("Idempotency-Key"))
        {
            failure = new HttpFailure(400, "idempotency_not_applicable", "idempotency_key");
            return false;
        }
        if (!route.Versioned && request.Headers.ContainsKey("If-Match"))
        {
            failure = new HttpFailure(400, "precondition_not_applicable", "if_match");
            return false;
        }
        failure = default;
        return true;
    }

    private static bool TryVersion(FramedRequest request, bool versioned, out string body, out HttpFailure failure)
    {
        body = request.Body;
        failure = default;
        if (!versioned)
            return true;
        if (!request.Headers.TryGetValue("If-Match", out var ifMatch))
        {
            failure = new HttpFailure(428, "catalog_precondition_required", "expected_version");
            return false;
        }
        if (!TryParseIfMatch(ifMatch, out var token))
        {
            failure = new HttpFailure(400, "version_domain", "expected_version");
            return false;
        }
        if (!TryMergeVersion(request.Body, token, out body, out var detail))
        {
            var status = detail == "contradictory_precondition" ? 400 : 400;
            var code = detail is "invalid_json" or "duplicate_field" ? "invalid_dto" : detail!;
            failure = new HttpFailure(status, code, detail!);
            return false;
        }
        return true;
    }

    private static bool TryParseIfMatch(string raw, out VersionToken token)
    {
        token = default;
        var text = raw.Trim();
        if (text.Length == 0 || text.Contains(',') || text.Contains('*') || text.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            return false;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
            if (text.Contains('"'))
                return false;
        }
        if (text == "ABSENT")
        {
            token = VersionToken.Absent;
            return true;
        }
        if (text.Length == 0 || text.Any(ch => ch is < '0' or > '9') || (text.Length > 1 && text[0] == '0'))
            return false;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            return false;
        token = VersionToken.AtLeastOne(number);
        return true;
    }

    private static bool TryMergeVersion(string json, VersionToken token, out string merged, out string? detail)
    {
        merged = json;
        detail = null;
        if (json.Length == 0)
        {
            detail = "invalid_json";
            return false;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            detail = "invalid_json";
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                detail = "invalid_json";
                return false;
            }
            JsonElement? existing = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("expected_version"))
                    continue;
                if (existing is not null)
                {
                    detail = "duplicate_field";
                    return false;
                }
                existing = property.Value.Clone();
            }
            if (existing is JsonElement present)
            {
                if (!VersionAgrees(present, token))
                {
                    detail = "contradictory_precondition";
                    return false;
                }
                return true;
            }
        }

        merged = InsertVersion(json, token);
        return true;
    }

    private static bool VersionAgrees(JsonElement element, VersionToken token)
    {
        if (token.IsAbsent)
            return element.ValueKind == JsonValueKind.String && element.GetString() == "ABSENT";
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number == token.Number;
    }

    private static string InsertVersion(string json, VersionToken token)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!first)
                builder.Append(',');
            first = false;
            builder.Append('"').Append(Escape(property.Name)).Append("\":").Append(property.Value.GetRawText());
        }
        if (!first)
            builder.Append(',');
        builder.Append("\"expected_version\":");
        if (token.IsAbsent)
            builder.Append("\"ABSENT\"");
        else
            builder.Append(token.Number!.Value.ToString(CultureInfo.InvariantCulture));
        builder.Append('}');
        return builder.ToString();
    }

    private static string Escape(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch is '"' or '\\')
                builder.Append('\\');
            builder.Append(ch);
        }
        return builder.ToString();
    }

    private static bool TryClosedPreviewBody(string body, out HttpFailure failure)
    {
        if (body.Length == 0)
        {
            failure = default;
            return true;
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            failure = new HttpFailure(400, "invalid_dto", "invalid_json");
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = new HttpFailure(400, "invalid_dto", "invalid_json");
                return false;
            }
            string? authority = null;
            string? unknown = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(property.Name))
                {
                    failure = new HttpFailure(400, "invalid_dto", "duplicate_field");
                    return false;
                }
                if (authority is null && AuthorityFields.Contains(property.Name))
                    authority = property.Name;
                else if (unknown is null)
                    unknown = property.Name;
            }
            if (authority is not null)
            {
                failure = new HttpFailure(400, "authority_field_rejected", authority);
                return false;
            }
            if (unknown is not null)
            {
                failure = new HttpFailure(400, "unknown_field", unknown);
                return false;
            }
        }
        failure = default;
        return true;
    }

    private static bool TryBindMethod(string method, Route parsed, out Route route, out HttpFailure failure)
    {
        failure = default;
        switch (parsed.Kind)
        {
            case RouteKind.List when method == "GET":
                route = parsed with { AllowsBody = false, IsWrite = false, Versioned = false };
                return true;
            case RouteKind.List when method == "POST":
                route = parsed with { Kind = RouteKind.Create, AllowsBody = true, IsWrite = true, Versioned = false };
                return true;
            case RouteKind.Read when method == "GET":
                route = parsed;
                return true;
            case RouteKind.Read when method == "PATCH":
                route = parsed with { Kind = RouteKind.ClaimEdit, AllowsBody = true, IsWrite = true, Versioned = true };
                return true;
            default:
                var expected = parsed.Kind switch
                {
                    RouteKind.History => "GET",
                    RouteKind.Preview or RouteKind.EntryActivation or RouteKind.PartnerActivation or RouteKind.LocationActivation => "POST",
                    _ => "",
                };
                if (method == expected)
                {
                    route = parsed;
                    return true;
                }
                route = default;
                failure = new HttpFailure(400, "method_not_allowed", "method_not_allowed");
                return false;
        }
    }

    private static bool TryRoute(string target, out Route route, out HttpFailure failure)
    {
        route = default;
        var segments = target.Split('/');
        if (segments.Length is < 5 or > 9 || segments[0].Length != 0 || segments[1] != "admin" || segments[2] != "catalogs")
        {
            failure = new HttpFailure(400, "unknown_route", "unknown_route");
            return false;
        }
        if (segments.Skip(1).Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            failure = new HttpFailure(400, "unknown_route", "unknown_route");
            return false;
        }
        var family = segments[3];
        if (segments[4] != "entries")
        {
            failure = new HttpFailure(400, "unknown_route", "unknown_route");
            return false;
        }
        if (segments.Length == 5)
        {
            route = new Route(RouteKind.List, family, Guid.Empty, null, AllowsBody: false, IsWrite: false, Versioned: false);
            failure = default;
            return true;
        }
        if (!Guid.TryParseExact(segments[5], "D", out var entryId) || entryId == Guid.Empty)
        {
            failure = new HttpFailure(400, "invalid_dto", "entry_id");
            return false;
        }
        if (segments.Length == 6)
        {
            route = new Route(RouteKind.Read, family, entryId, null, AllowsBody: false, IsWrite: false, Versioned: false);
            failure = default;
            return true;
        }
        if (segments.Length == 7)
        {
            route = segments[6] switch
            {
                "history" => new Route(RouteKind.History, family, entryId, null, false, false, false),
                "activation-preview" => new Route(RouteKind.Preview, family, entryId, null, true, false, false),
                "entry-activation" => new Route(RouteKind.EntryActivation, family, entryId, null, true, true, true),
                _ => default,
            };
            if (route.Kind == RouteKind.None)
            {
                failure = new HttpFailure(400, "unknown_route", "unknown_route");
                return false;
            }
            failure = default;
            return true;
        }
        if (segments.Length == 9 && segments[8] == "activation" && segments[6] is "partners" or "locations")
        {
            var kind = segments[6] == "partners" ? RouteKind.PartnerActivation : RouteKind.LocationActivation;
            route = new Route(kind, family, entryId, segments[7], true, true, true);
            failure = default;
            return true;
        }
        failure = new HttpFailure(400, "unknown_route", "unknown_route");
        return false;
    }

    private enum RouteKind
    {
        None = 0,
        List = 1,
        Read = 2,
        History = 3,
        Preview = 4,
        Create = 5,
        ClaimEdit = 6,
        EntryActivation = 7,
        PartnerActivation = 8,
        LocationActivation = 9,
    }

    private readonly record struct Route(RouteKind Kind, string FamilyWire, Guid EntryId, string? ScopeId, bool AllowsBody, bool IsWrite, bool Versioned);
}
