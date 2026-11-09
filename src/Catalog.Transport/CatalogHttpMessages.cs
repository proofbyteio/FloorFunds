using System.Globalization;
using System.Text;
using Catalog.Contracts;

namespace Catalog.Transport;

public static class CatalogHttpMapping
{
    public const string Name = "proposed_http_mapping_not_auth_or_security_acceptance";
    public const string HeaderValue = "proposed-not-acceptance";
    public const string ProofHeader = "in-memory-double";
}

/// <summary>
/// Server or test principal context. The HTTP body and request headers cannot supply or replace it.
/// The graph is an explicit test fixture. It is not read from the request and it does not authorize a write.
/// </summary>
public sealed record CatalogHttpServerContext
{
    public CatalogHttpServerContext(CatalogPrincipal principal, string correlationId, string sourceRequestId, GraphView? graph = null)
    {
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));
        CorrelationId = correlationId ?? "";
        SourceRequestId = sourceRequestId ?? "";
        Graph = graph ?? GraphView.Default;
    }

    public CatalogPrincipal Principal { get; }
    public string CorrelationId { get; }
    public string SourceRequestId { get; }
    public GraphView Graph { get; }
}

public sealed class SyntheticHttpResponse
{
    public required int StatusCode { get; init; }
    public required string ReasonPhrase { get; init; }
    public required string Body { get; init; }
    public required string Raw { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}

internal static class HttpMessages
{
    public static SyntheticHttpResponse Respond(HttpFailure failure) =>
        Respond(failure.Status, ResponseJson.Transport(failure.Code, failure.Detail));

    public static SyntheticHttpResponse Respond(int status, string body)
    {
        var reason = Reason(status);
        var length = Encoding.UTF8.GetByteCount(body);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Content-Length"] = length.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store",
            ["X-Catalog-Mapping"] = CatalogHttpMapping.HeaderValue,
            ["X-Catalog-Proof"] = CatalogHttpMapping.ProofHeader,
        };
        var raw = new StringBuilder();
        raw.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
        raw.Append("Content-Type: ").Append(headers["Content-Type"]).Append("\r\n");
        raw.Append("Content-Length: ").Append(headers["Content-Length"]).Append("\r\n");
        raw.Append("Cache-Control: ").Append(headers["Cache-Control"]).Append("\r\n");
        raw.Append("X-Catalog-Mapping: ").Append(headers["X-Catalog-Mapping"]).Append("\r\n");
        raw.Append("X-Catalog-Proof: ").Append(headers["X-Catalog-Proof"]).Append("\r\n");
        raw.Append("\r\n");
        raw.Append(body);
        return new SyntheticHttpResponse
        {
            StatusCode = status,
            ReasonPhrase = reason,
            Body = body,
            Raw = raw.ToString(),
            Headers = headers,
        };
    }

    public static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Content",
        428 => "Precondition Required",
        503 => "Service Unavailable",
        _ => throw new InvalidOperationException("HTTP status is outside the proposed catalog mapping."),
    };
}
