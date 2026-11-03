// Server-rendered ui/login-hub templates (TL build-order step 2). Ported from the UX mockups
// (/tmp/sse-d0/ux-snap-2132, md5s in ui/login-hub/README.md); testids from contract/ui-driver.md.
// [ASSUMPTION A46] Angular is not installable here (no package fetch), so the screens are static HTML
// templates loaded at startup and filled by plain {{name}} (HTML-encoded) / {{{name}}} (raw) substitution.
using System.Net;
using System.Text.RegularExpressions;

namespace Login.StandIn;

public static class Ui
{
    static Dictionary<string, string> T = new();
    public static string Dir = "";
    public static string IssuerPath = "/standin";
    public static int AgentPort;

    // Every ui/ page: CSP with frame-ancestors 'none' (TL 9:32); no inline script or style, scripts are ui/login-hub/js files (TL 9:38).
    public static string Csp(int agentPort) =>
        $"default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self'; connect-src 'self' http://localhost:{agentPort}; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
    // Fallback for every other stand-in response (JSON, 403, redirects, control/back listeners).
    public const string DefaultCsp = "default-src 'none'; frame-ancestors 'none'";

    public static void Load(string? dir)
    {
        Dir = dir ?? Path.Combine(AppContext.BaseDirectory, "ui");
        foreach (var n in new[] { "layout", "card", "error-banner", "signin", "resign-in", "screen-8b", "totp", "screen-8a", "handoff", "bad-link", "reenroll", "recovery-codes" })
        {
            var p = Path.Combine(Dir, n + ".html");
            if (!File.Exists(p)) throw new InvalidOperationException($"ui template missing: {p}");
            T[n] = File.ReadAllText(p);
        }
    }

    static readonly Regex Slot = new(@"\{\{\{(\w+)\}\}\}|\{\{(\w+)\}\}", RegexOptions.Compiled);
    public static string Render(string name, params (string k, string v)[] vars)
    {
        var d = vars.ToDictionary(x => x.k, x => x.v);
        return Slot.Replace(T[name], m => m.Groups[1].Success
            ? d.GetValueOrDefault(m.Groups[1].Value, "")
            : WebUtility.HtmlEncode(d.GetValueOrDefault(m.Groups[2].Value, "")));
    }

    public static string Error(string? msg) =>
        Render("error-banner", ("message", msg ?? ""), ("hidden", msg is null ? " hidden" : ""));

    // js: name of a ui/login-hub/js file (no inline script, so the CSP needs no 'unsafe-inline' or nonce).
    public static IResult Page(string title, string content, string? js = null, int status = 200) => new PageResult(
        Render("layout", ("title", title), ("base", IssuerPath), ("content", content),
            ("scripts", js is null ? "" : $"<script src=\"{WebUtility.HtmlEncode(IssuerPath)}/ui/js/{js}.js\"></script>")), status);

    sealed class PageResult(string html, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext c)
        {
            c.Response.Headers.ContentSecurityPolicy = Csp(AgentPort);
            c.Response.Headers.CacheControl = "no-store";
            c.Response.StatusCode = status;
            c.Response.ContentType = "text/html; charset=utf-8";
            return c.Response.WriteAsync(html);
        }
    }

    public static void MapAssets(WebApplication app)
    {
        app.MapGet(IssuerPath + "/ui/theme.css", (HttpContext c) => Asset(c, "theme.css", "text/css; charset=utf-8"));
        app.MapGet(IssuerPath + "/ui/js/{name}", (string name, HttpContext c) =>
            name is "signin.js" or "handoff.js" ? Asset(c, "js/" + name, "text/javascript; charset=utf-8") : Results.NotFound());
        app.MapGet(IssuerPath + "/ui/fonts/{name}", (string name, HttpContext c) =>
            name is "Poppins-Regular.ttf" or "Poppins-Medium.ttf" or "Poppins-SemiBold.ttf" ? Asset(c, "fonts/" + name, "font/ttf") : Results.NotFound());
    }
    static IResult Asset(HttpContext c, string rel, string type)
    {
        c.Response.Headers.ContentSecurityPolicy = DefaultCsp;
        return Results.File(Path.Combine(Dir, rel), type);
    }
}
