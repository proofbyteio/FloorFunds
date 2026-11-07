using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Npgsql;

namespace Admin.Api;

public static class AdminApiHost
{
    /// <summary>
    /// Builds the API (fail closed): options validated, audit schema loaded (unknown keyword = throw), DB role guard (§8.3).
    /// <paramref name="directory"/> defaults to the INTERIM token directory; only the in-process checks pass another one
    /// (and <paramref name="extraLogger"/>, to read the error log line).
    /// </summary>
    public static async Task<WebApplication> BuildAsync(AdminApiOptions options, IAdminDirectory? directory = null, TimeProvider? clock = null,
        ILoggerProvider? extraLogger = null)
    {
        var opt = options.Validated();
        var schema = JsonSchemaLite.Load(await File.ReadAllTextAsync(opt.AuditSchemaPath));
        var ds = NpgsqlDataSource.Create(opt.ConnectionString);
        await GuardRoleAsync(ds);

        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // Request logs at Information would record paths per request; keep framework logs at Warning (no bodies, no headers).
        b.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        if (extraLogger is not null) b.Logging.AddProvider(extraLogger);
        // No global body-size limit here [TLD 2026-11-04 10:02, P3-10]: the 1 MiB one was dead (lifted per request by RequestEnvelope;
        // every read is bounded by the API).
        b.WebHost.UseKestrel(k => { k.AddServerHeader = false; });
        if (opt.Urls.Length > 0) b.WebHost.UseUrls(opt.Urls);
        b.Services.AddSingleton(opt);
        b.Services.AddSingleton(ds);
        b.Services.AddSingleton(new AuditOutbox(schema));
        b.Services.AddSingleton(new AdminTokenValidator(opt));
        b.Services.AddSingleton(directory ?? new TokenAdminDirectory());
        b.Services.AddSingleton(clock ?? TimeProvider.System);
        b.Services.AddSingleton<LoginConfigApi>();
        var app = b.Build();
        app.Use(RequestEnvelope.InvokeAsync); // before routing: 405 and unrouted 404 get X-Request-ID too [TLD 2026-11-04 9:28, P3-4]
        var api = app.Services.GetRequiredService<LoginConfigApi>();
        app.MapGet("/api/orgs/locations/{location_id}/login-config", (HttpContext ctx, string location_id) => api.GetAsync(ctx, location_id));
        app.MapPut("/api/orgs/locations/{location_id}/login-config", (HttpContext ctx, string location_id) => api.PutAsync(ctx, location_id));
        app.MapGet("/api/orgs/locations/{location_id}/login-config/changes", (HttpContext ctx, string location_id) => api.ChangesAsync(ctx, location_id));
        app.MapPost("/api/orgs/locations", (Func<HttpContext, Task<IResult>>)(ctx => api.PostAsync(ctx)));
        app.MapPost("/api/orgs/partners/{partner_id}/clients", (HttpContext ctx, string partner_id) => api.PostClientAsync(ctx, partner_id));
        app.MapGet("/api/orgs/partners/{partner_id}/clients", (HttpContext ctx, string partner_id) => api.GetClientsAsync(ctx, partner_id));
        app.MapGet("/api/orgs/partners/{partner_id}/clients/{client_id}", (HttpContext ctx, string partner_id, string client_id) => api.GetClientAddressAsync(ctx, partner_id, client_id));
        app.MapGet("/api/orgs/login-config/limits", (Func<HttpContext, Task<IResult>>)(ctx => api.LimitsAsync(ctx))); // DEC-107 (TLD 16:32 Q2); ?partner_id= Slice A
        // Slice A (TLD 06:59 route, LD 12b1d9cd): per-partner reason-length bounds.
        app.MapGet("/api/orgs/partners/{partner_id}/login-config/reason-bounds", (HttpContext ctx, string partner_id) => api.GetReasonBoundsAsync(ctx, partner_id));
        app.MapPut("/api/orgs/partners/{partner_id}/login-config/reason-bounds", (HttpContext ctx, string partner_id) => api.PutReasonBoundsAsync(ctx, partner_id));
        app.MapGet("/api/ref/countries", (Func<HttpContext, Task<IResult>>)(ctx => api.GetCountriesAsync(ctx)));
        app.MapGet("/api/ref/time-zones", (Func<HttpContext, Task<IResult>>)(ctx => api.GetTimeZonesAsync(ctx)));
        return app;
    }

    /// <summary>Refuse to start if the connection's role is a superuser, has BYPASSRLS, or owns (or inherits an owner of) auth objects.</summary>
    public static async Task GuardRoleAsync(NpgsqlDataSource ds)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT r.rolsuper, r.rolbypassrls,
                   EXISTS (SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid = t.relnamespace
                           WHERE n.nspname IN ('auth', 'ref') AND pg_has_role(current_user, t.relowner, 'MEMBER'))
                   OR EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspname IN ('auth', 'ref') AND pg_has_role(current_user, n.nspowner, 'MEMBER'))
            FROM pg_roles r WHERE r.rolname = current_user
            """, c);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("startup guard: role not found");
        if (r.GetBoolean(0)) throw new InvalidOperationException("startup guard: the API role is a superuser");
        if (r.GetBoolean(1)) throw new InvalidOperationException("startup guard: the API role has BYPASSRLS");
        if (r.GetBoolean(2)) throw new InvalidOperationException("startup guard: the API role owns auth or ref objects");
    }
}

/// <summary>
/// Middleware around every request (before routing). (1) X-Request-ID: a server UUIDv7, set on every response incl. 405 and unrouted
/// 404; a client value is ignored [TLD 2026-11-04 9:04 OQ-P3-3, 9:20 name, 9:28 P3-4]. (2) A request body the handler did not read to
/// the end (oversize 413, or a 401/403/404/405 that never needed it) [TLD 2026-11-04 9:28, P3-2]: the response gets Connection: close,
/// is flushed, then at most 1 MiB / 2 s more is read and discarded, so the client receives the status and headers instead of a reset.
/// Every read is bounded here or in LoginConfigApi.ReadBodyAsync (64 KiB, BodyReadTimeout), so Kestrel's own body-size limit and
/// minimum body data rate are lifted per request: one deadline (408 request_timeout, P3-7) instead of Kestrel's own abort.
/// (3) [TLD 2026-11-04 9:53/10:02, P3-8] a drain that hits its 2 s bound logs ONE Warning line (request_id, bytes, no exception, no
/// stack trace), unless the request already logged its Warning (408). A request with Expect: 100-continue whose body was never read
/// is not drained at all: the client is waiting for us and has sent nothing, so no 100 Continue is ever produced. [TLD 2026-11-04 10:42]
/// The connection is closed right after that 413 is sent (Kestrel's body limit is lowered below the declared length, so its own
/// end-of-request drain rejects the body at once); a ctx.Abort() after CompleteAsync was measured to lose the 413 (reset) sometimes.
/// </summary>
public static class RequestEnvelope
{
    public const string Header = "X-Request-ID";
    public const int DrainMaxBytes = 1 << 20;
    static readonly TimeSpan DrainMaxTime = TimeSpan.FromSeconds(2);
    static readonly object IdKey = new(), ReadKey = new(), WarnedKey = new();

    public static string Id(HttpContext ctx) => (string)ctx.Items[IdKey]!;
    public static void MarkBodyRead(HttpContext ctx) => ctx.Items[ReadKey] = true;
    /// <summary>The request has logged its one Warning line (408); the drain stays silent.</summary>
    public static void MarkWarned(HttpContext ctx) => ctx.Items[WarnedKey] = true;

    public static async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        var id = Guid.CreateVersion7().ToString("D");
        ctx.Items[IdKey] = id;
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
        if (ctx.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate) rate.MinDataRate = null;
        var canHaveBody = ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? true;
        bool Unread() => canHaveBody && ctx.Items[ReadKey] is not true;
        ctx.Response.OnStarting(() =>
        {
            ctx.Response.Headers[Header] = id;
            if (Unread()) ctx.Response.Headers.Connection = "close";
            return Task.CompletedTask;
        });
        await next(ctx);
        if (!Unread()) return;
        await ctx.Response.CompleteAsync();
        // Expect: 100-continue and nothing read: the client waits for a 100 Continue and has sent no body bytes; nothing to drain [P3-8].
        // Close right after the 413 is sent [TLD 2026-11-04 10:42]: otherwise Kestrel keeps the connection about 5-6 s (its own body
        // drain wait). Not ctx.Abort(): it can cancel the transport output before the flushed 413 reaches the socket (measured: a reset
        // instead of the 413 on 4 or more of 42 Expect requests, POSTs only). The client has sent no body bytes, so Kestrel's close is a FIN, not a reset.
        if (ctx.Request.Headers.Expect.Any(e => string.Equals(e, "100-continue", StringComparison.OrdinalIgnoreCase)))
        {
            // Lower Kestrel's body-size limit below the declared length (the body was never started, so the feature is still writable):
            // Kestrel's own end-of-request drain then rejects the body at once and closes the connection after the 413 has been sent.
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } cap) cap.MaxRequestBodySize = 0;
            return;
        }
        var (end, drained) = await ReadBoundedAsync(ctx, DrainMaxTime, DrainMaxBytes, null);
        if (end == BodyEnd.TimedOut && ctx.Items[WarnedKey] is not true)
            ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Admin.Api.RequestEnvelope")
                .LogWarning("admin-api body drain timed out after {Seconds} s, {Bytes} bytes discarded, request_id={RequestId}",
                    (int)DrainMaxTime.TotalSeconds, drained, id);
        // After a timed-out drain the client has had DrainMaxTime to read the response: close now rather than let Kestrel keep the
        // connection open. (Before P3-8 the cancelled read did this by accident, logging an Error with a stack trace.)
        if (end == BodyEnd.TimedOut) ctx.Abort();
    }

    public enum BodyEnd { Complete, OverLimit, TimedOut, Failed }

    /// <summary>
    /// Reads the request body through its PipeReader, at most <paramref name="max"/> bytes and <paramref name="deadline"/> of wall time
    /// [TLD 2026-11-04 9:53/10:02, P3-7/P3-8]. The deadline uses CancelPendingRead, not a cancellation token: a cancelled token leaves
    /// Kestrel's body reader "already in progress" and Kestrel then logs an Error with a stack trace at the end of the request.
    /// <paramref name="sink"/> receives each segment (null = discard). Never throws for a client fault (reset, bad framing): Failed.
    /// </summary>
    public static async Task<(BodyEnd End, long Bytes)> ReadBoundedAsync(HttpContext ctx, TimeSpan deadline, long max, Action<ReadOnlyMemory<byte>>? sink)
    {
        var reader = ctx.Request.BodyReader;
        long total = 0;
        using var timer = new CancellationTokenSource(deadline);
        using var reg = timer.Token.Register(() => reader.CancelPendingRead());
        try
        {
            while (true)
            {
                var r = await reader.ReadAsync();
                if (r.IsCanceled) { reader.AdvanceTo(r.Buffer.Start); return (BodyEnd.TimedOut, total); }
                var len = r.Buffer.Length;
                if (total + len > max) { reader.AdvanceTo(r.Buffer.End); return (BodyEnd.OverLimit, total + len); }
                if (sink is not null) foreach (var seg in r.Buffer) sink(seg);
                total += len;
                reader.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted) return (BodyEnd.Complete, total);
            }
        }
        catch (Exception x) when (x is BadHttpRequestException or IOException or OperationCanceledException) { return (BodyEnd.Failed, total); }
    }
}
