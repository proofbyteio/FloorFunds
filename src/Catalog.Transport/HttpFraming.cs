using System.Text;

namespace Catalog.Transport;

internal sealed class FramedRequest
{
    public required string Method { get; init; }
    public required string Target { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required string Body { get; init; }
}

internal readonly record struct HttpFailure(int Status, string Code, string Detail);

internal static class HttpFraming
{
    public const int MaxMessageBytes = 65536;
    public const int MaxBodyBytes = 8192;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryRead(ReadOnlySpan<byte> raw, out FramedRequest? request, out HttpFailure failure)
    {
        request = null;
        if (raw.Length > MaxMessageBytes)
        {
            failure = new HttpFailure(400, "request_too_large", "request_too_large");
            return false;
        }
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            failure = new HttpFailure(400, "malformed_http", "bom");
            return false;
        }

        var split = IndexOf(raw, "\r\n\r\n"u8);
        if (split < 0)
        {
            failure = new HttpFailure(400, "malformed_http", "framing");
            return false;
        }

        var head = raw[..split];
        var bodyBytes = raw[(split + 4)..];
        string method;
        string target;
        Dictionary<string, string> headers;
        try
        {
            if (Contains(head, "\n"u8) && !CrlfOnly(head))
            {
                failure = new HttpFailure(400, "malformed_http", "line_ending");
                return false;
            }

            var lines = SplitCrlf(head);
            if (lines.Count == 0)
            {
                failure = new HttpFailure(400, "malformed_http", "request_line");
                return false;
            }
            if (!TryRequestLine(lines[0].Span, out method, out target, out var lineDetail))
            {
                failure = new HttpFailure(400, "malformed_http", lineDetail);
                return false;
            }

            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Count; i++)
            {
                var line = lines[i].Span;
                if (line.Length == 0 || line[0] is (byte)' ' or (byte)'\t')
                {
                    failure = new HttpFailure(400, "malformed_http", "header_fold");
                    return false;
                }
                var colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    failure = new HttpFailure(400, "malformed_http", "header");
                    return false;
                }
                var nameBytes = line[..colon];
                if (!IsToken(nameBytes))
                {
                    failure = new HttpFailure(400, "malformed_http", "header_name");
                    return false;
                }
                var name = StrictUtf8.GetString(nameBytes);
                var value = StrictUtf8.GetString(line[(colon + 1)..]).Trim();
                if (value.Contains('\0', StringComparison.Ordinal))
                {
                    failure = new HttpFailure(400, "malformed_http", "header_value");
                    return false;
                }
                if (!headers.TryAdd(name, value))
                {
                    failure = new HttpFailure(400, "malformed_http", "duplicate_header");
                    return false;
                }
            }
        }
        catch (DecoderFallbackException)
        {
            failure = new HttpFailure(400, "malformed_http", "invalid_utf8");
            return false;
        }

        if (!headers.ContainsKey("Host"))
        {
            failure = new HttpFailure(400, "malformed_http", "host");
            return false;
        }
        if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Content-Encoding"))
        {
            failure = new HttpFailure(400, "malformed_http", "transfer_encoding");
            return false;
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (bodyBytes.Length != 0)
            {
                failure = new HttpFailure(400, "malformed_http", "content_length");
                return false;
            }
        }
        else if (!TryLength(lengthText, out var length))
        {
            failure = new HttpFailure(400, "malformed_http", "content_length");
            return false;
        }
        else if (length > MaxBodyBytes)
        {
            failure = new HttpFailure(400, "request_too_large", "request_too_large");
            return false;
        }
        else if (length != bodyBytes.Length)
        {
            failure = new HttpFailure(400, "malformed_http", "content_length");
            return false;
        }

        string body;
        try
        {
            body = bodyBytes.Length == 0 ? "" : StrictUtf8.GetString(bodyBytes);
        }
        catch (DecoderFallbackException)
        {
            failure = new HttpFailure(400, "malformed_http", "invalid_utf8");
            return false;
        }

        request = new FramedRequest
        {
            Method = method,
            Target = target,
            Headers = headers,
            Body = body,
        };
        failure = default;
        return true;
    }

    private static bool TryRequestLine(ReadOnlySpan<byte> line, out string method, out string target, out string detail)
    {
        method = "";
        target = "";
        detail = "request_line";
        var text = StrictUtf8.GetString(line);
        var parts = text.Split(' ');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        if (parts[2] != "HTTP/1.1")
        {
            detail = "http_version";
            return false;
        }
        if (parts[0].Any(ch => ch is < 'A' or > 'Z'))
            return false;
        if (parts[1].Contains('%') || parts[1].Contains('\\') || parts[1].Contains('?') || parts[1].Contains('#'))
        {
            detail = parts[1].Contains('?') ? "query" : "target";
            return false;
        }
        if (!parts[1].StartsWith('/') || parts[1].Contains("//", StringComparison.Ordinal))
        {
            detail = "target";
            return false;
        }
        method = parts[0];
        target = parts[1];
        return true;
    }

    private static bool TryLength(string text, out int length)
    {
        length = 0;
        if (text.Length == 0 || text.Length > 5 || text.Any(ch => ch is < '0' or > '9'))
            return false;
        if (text.Length > 1 && text[0] == '0')
            return false;
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out length)
            && length >= 0;
    }

    private static bool IsToken(ReadOnlySpan<byte> name)
    {
        foreach (var b in name)
        {
            var ok = b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-';
            if (!ok)
                return false;
        }
        return true;
    }

    private static bool CrlfOnly(ReadOnlySpan<byte> head)
    {
        for (var i = 0; i < head.Length; i++)
        {
            if (head[i] != (byte)'\n')
                continue;
            if (i == 0 || head[i - 1] != (byte)'\r')
                return false;
        }
        return true;
    }

    private static List<ReadOnlyMemory<byte>> SplitCrlf(ReadOnlySpan<byte> head)
    {
        var lines = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        var array = head.ToArray();
        for (var i = 0; i + 1 < array.Length; i++)
        {
            if (array[i] == (byte)'\r' && array[i + 1] == (byte)'\n')
            {
                lines.Add(array.AsMemory(start, i - start));
                start = i + 2;
                i++;
            }
        }
        if (start != array.Length)
            lines.Add(array.AsMemory(start, array.Length - start));
        return lines;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) => IndexOf(haystack, needle) >= 0;
}
