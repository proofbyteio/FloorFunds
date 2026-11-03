using System.Net;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// Front-listener network allowlist: the first middleware on every front request (task slice 3; POC ip-allowlist semantics).
// [ASSUMPTION A13] .NET has no SQLite reader without a package, so the active rows are exported read-only
//   from a /tmp copy of allowlist.db to JSON (/tmp/sse-d0/export-allowlist.py). The real DB is never opened.
// [ASSUMPTION A14] there are no trusted proxies: only the TCP peer address counts, and X-Forwarded-For is ignored (as in the POC).
// [ASSUMPTION A15] a missing or unreadable file means fail closed: every front request is blocked.
public sealed class Allowlist
{
    readonly List<(IPAddress Net, int Prefix)> _nets = new();
    public bool Loaded { get; }
    public int Count => _nets.Count;

    public Allowlist(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return;
            var o = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var e in o["entries"]!.AsArray())
            {
                var v = (string)e!["value"]!; var i = v.IndexOf('/');
                var ip = IPAddress.Parse(i < 0 ? v : v[..i]);
                _nets.Add((ip, i < 0 ? ip.GetAddressBytes().Length * 8 : int.Parse(v[(i + 1)..])));
            }
            Loaded = true;
        }
        catch (Exception) { _nets.Clear(); Loaded = false; }
    }

    public bool Allows(IPAddress? ip)
    {
        if (!Loaded || ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var a = ip.GetAddressBytes();
        foreach (var (net, prefix) in _nets)
        {
            var b = net.GetAddressBytes();
            if (a.Length != b.Length) continue;
            int full = prefix / 8, rem = prefix % 8; bool ok = true;
            for (int k = 0; k < full && ok; k++) ok = a[k] == b[k];
            if (ok && rem > 0) { int m = 0xFF << (8 - rem) & 0xFF; ok = (a[full] & m) == (b[full] & m); }
            if (ok) return true;
        }
        return false;
    }

    static readonly byte[] Body = "403 Forbidden: access denied\n"u8.ToArray();

    public static void Use(WebApplication app, StandInPorts p, Allowlist list)
    {
        app.Use(async (ctx, next) =>
        {
            if (Listeners.Kind(ctx, p) != "front" || list.Allows(ctx.Connection.RemoteIpAddress)) { await next(); return; }
            var r = ctx.Response;
            r.StatusCode = 403;
            r.ContentType = "text/plain; charset=utf-8";
            r.Headers.CacheControl = "no-store";
            r.Headers.Connection = "close";
            r.Headers.Server = "server"; // errors.md: Server: server, Date, X-Request-ID
            r.Headers["X-Request-ID"] = Guid.NewGuid().ToString();
            r.ContentLength = Body.Length;
            // errors.md: the body is empty for HEAD. Allowlist 403s are not put on the outbox (LD §8 L431), so no audit event is emitted.
            if (!HttpMethods.IsHead(ctx.Request.Method)) await r.Body.WriteAsync(Body);
        });
    }
}
