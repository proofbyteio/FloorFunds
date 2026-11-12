using System.Text;
using Catalog.Contracts;
using Catalog.Contracts.Doubles;
using Catalog.Transport;

namespace Catalog.ScratchAuth;

/// <summary>
/// In-process fail-closed boundary in front of the accepted catalog double.
/// A synthetic access token is checked first. The catalog call, when reached, uses CatalogRole.None.
/// admin_type is not a catalog role. There is no socket, database, outbox, or production key.
/// </summary>
public sealed class CatalogScratchBoundary
{
    private readonly FrozenAdminTokenGate _gate;
    private readonly CatalogHttpTransport _transport;

    public CatalogScratchBoundary(string issuer, Microsoft.IdentityModel.Tokens.SecurityKey publicKey)
    {
        _gate = new FrozenAdminTokenGate(issuer, publicKey);
        Repository = new InMemoryCatalogRepository();
        _transport = new CatalogHttpTransport(new CatalogDomainService(Repository, new FixedClock(), new CountingIds()));
    }

    public InMemoryCatalogRepository Repository { get; }

    public SyntheticHttpResponse Handle(ReadOnlyMemory<byte> request)
    {
        if (!TryOneBearer(request.Span, out var token, out var forwarded))
            return InvalidToken();
        if (!_gate.TryAuthenticate(token, out var userId))
            return InvalidToken();
        var context = new CatalogHttpServerContext(
            CatalogPrincipalFactory.Denied(userId),
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"));
        return _transport.Handle(context, forwarded);
    }

    public SyntheticHttpResponse Handle(string request) => Handle(Encoding.UTF8.GetBytes(request));

    private static bool TryOneBearer(ReadOnlySpan<byte> raw, out string token, out byte[] forwarded)
    {
        token = "";
        forwarded = [];
        var split = IndexOf(raw, "\r\n\r\n"u8);
        if (split < 0)
            return false;
        var lines = Split(raw[..split]);
        if (lines.Count == 0)
            return false;
        var kept = new List<ReadOnlyMemory<byte>>(lines.Count);
        string? bearer = null;
        var authorizationCount = 0;
        for (var i = 1; i < lines.Count; i++)
        {
            if (!TryHeader(lines[i].Span, out var name, out var value))
                return false;
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                authorizationCount++;
                bearer = value;
            }
            else
            {
                kept.Add(lines[i]);
            }
        }
        if (authorizationCount != 1 || !TryBearer(bearer, out token))
            return false;
        forwarded = Join(lines[0].Span, kept, raw[(split + 4)..]);
        return true;
    }

    private static bool TryBearer(string? value, out string token)
    {
        token = "";
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;
        token = value[7..].Trim();
        return token.Length is > 0 and <= FrozenAdminTokenGate.MaximumTokenLength;
    }

    private static bool TryHeader(ReadOnlySpan<byte> line, out string name, out string value)
    {
        name = "";
        value = "";
        if (line.Length == 0 || line[0] is (byte)' ' or (byte)'\t')
            return false;
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
            return false;
        name = Encoding.ASCII.GetString(line[..colon]);
        var start = colon + 1;
        while (start < line.Length && line[start] is (byte)' ' or (byte)'\t')
            start++;
        var end = line.Length;
        while (end > start && line[end - 1] is (byte)' ' or (byte)'\t')
            end--;
        value = Encoding.ASCII.GetString(line[start..end]);
        return true;
    }

    private static byte[] Join(ReadOnlySpan<byte> requestLine, List<ReadOnlyMemory<byte>> headers, ReadOnlySpan<byte> body)
    {
        var length = requestLine.Length + 2 + body.Length + 2;
        foreach (var header in headers)
            length += header.Length + 2;
        var buffer = new byte[length];
        var offset = 0;
        requestLine.CopyTo(buffer);
        offset += requestLine.Length;
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
        foreach (var header in headers)
        {
            header.Span.CopyTo(buffer.AsSpan(offset));
            offset += header.Length;
            buffer[offset++] = (byte)'\r';
            buffer[offset++] = (byte)'\n';
        }
        buffer[offset++] = (byte)'\r';
        buffer[offset++] = (byte)'\n';
        body.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    private static List<ReadOnlyMemory<byte>> Split(ReadOnlySpan<byte> head)
    {
        var lines = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        for (var i = 0; i + 1 < head.Length; i++)
        {
            if (head[i] == (byte)'\r' && head[i + 1] == (byte)'\n')
            {
                lines.Add(new ReadOnlyMemory<byte>(head[start..i].ToArray()));
                start = i + 2;
                i++;
            }
        }
        if (start < head.Length)
            lines.Add(new ReadOnlyMemory<byte>(head[start..].ToArray()));
        else if (head.Length >= 2 && head[^2] == (byte)'\r' && head[^1] == (byte)'\n')
            lines.Add(ReadOnlyMemory<byte>.Empty);
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

    private static SyntheticHttpResponse InvalidToken()
    {
        const string authenticate = "Bearer error=\"invalid_token\"";
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WWW-Authenticate"] = authenticate,
            ["Cache-Control"] = "no-store",
            ["Content-Length"] = "0",
        };
        var raw = "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: " + authenticate + "\r\nCache-Control: no-store\r\nContent-Length: 0\r\n\r\n";
        return new SyntheticHttpResponse
        {
            StatusCode = 401,
            ReasonPhrase = "Unauthorized",
            Body = "",
            Raw = raw,
            Headers = headers,
        };
    }

    private sealed class FixedClock : ICatalogClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 11, 8, 22, 40, 0, TimeSpan.Zero);
    }

    private sealed class CountingIds : ICatalogIds
    {
        private int _n;

        public Guid NewId()
        {
            _n++;
            var bytes = new byte[16];
            BitConverter.TryWriteBytes(bytes, _n);
            bytes[7] = 0x40;
            bytes[8] = 0x80;
            return new Guid(bytes);
        }
    }
}
