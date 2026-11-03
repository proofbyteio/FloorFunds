using System.Net;

namespace Login.StandIn;

// contract 1.3-1.5, 2.9; standin-control 1.1, 1.3-1.5.
public sealed record StandInPorts(int Front, int Back, int Control);

public static class Listeners
{
    public const string ControlPrefix = "/_standin";
    static readonly string[] BackOnly = { "/connect/token", "/connect/revoke", "/connect/introspect" };
    static readonly string[] BackAlso = { "/.well-known/openid-configuration", "/.well-known/jwks" };

    public static StandInPorts Read(IConfiguration c) => new(
        c.GetValue("StandIn:FrontPort", 18080), c.GetValue("StandIn:BackPort", 18081), c.GetValue("StandIn:ControlPort", 18082));

    // Every listener binds 127.0.0.1 only. ASPNETCORE_URLS is ignored because Kestrel is configured explicitly.
    public static void Bind(WebApplicationBuilder b, StandInPorts p)
    {
        b.WebHost.UseUrls();
        b.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, p.Front);
            k.Listen(IPAddress.Loopback, p.Back);
            k.Listen(IPAddress.Loopback, p.Control);
        });
    }

    /// <summary>Which listener a request arrived on. The local port is used, never the Host header.</summary>
    public static string Kind(HttpContext ctx, StandInPorts p) =>
        ctx.Connection.LocalPort == p.Control ? "control" : ctx.Connection.LocalPort == p.Back ? "back" : "front";

    public static void UseListenerRules(WebApplication app, StandInPorts p, string issuerPath)
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";
            var rel = path.StartsWith(issuerPath + "/", StringComparison.Ordinal) ? path[issuerPath.Length..] : path;
            switch (Kind(ctx, p))
            {
                case "control":
                    ctx.Response.Headers.CacheControl = "no-store";
                    var ip = ctx.Connection.RemoteIpAddress;
                    if (ip is null || !IPAddress.IsLoopback(ip)) { ctx.Response.StatusCode = 403; return; }
                    if (!path.StartsWith(ControlPrefix + "/", StringComparison.Ordinal)) { ctx.Response.StatusCode = 404; return; }
                    break;
                case "back":
                    if (!BackOnly.Contains(rel) && !BackAlso.Contains(rel)) { ctx.Response.StatusCode = 404; return; }
                    break;
                default:
                    if (path.StartsWith(ControlPrefix, StringComparison.Ordinal) || BackOnly.Contains(rel)) { ctx.Response.StatusCode = 404; return; }
                    break;
            }
            await next();
        });
    }
}
