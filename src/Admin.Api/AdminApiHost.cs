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
        b.WebHost.UseKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 1 << 20; });
        if (opt.Urls.Length > 0) b.WebHost.UseUrls(opt.Urls);
        b.Services.AddSingleton(opt);
        b.Services.AddSingleton(ds);
        b.Services.AddSingleton(new AuditOutbox(schema));
        b.Services.AddSingleton(new AdminTokenValidator(opt));
        b.Services.AddSingleton(directory ?? new TokenAdminDirectory());
        b.Services.AddSingleton(clock ?? TimeProvider.System);
        b.Services.AddSingleton<LoginConfigApi>();
        var app = b.Build();
        var api = app.Services.GetRequiredService<LoginConfigApi>();
        app.MapGet("/api/orgs/locations/{location_id}/login-config", (HttpContext ctx, string location_id) => api.GetAsync(ctx, location_id));
        app.MapPut("/api/orgs/locations/{location_id}/login-config", (HttpContext ctx, string location_id) => api.PutAsync(ctx, location_id));
        app.MapGet("/api/orgs/locations/{location_id}/login-config/changes", (HttpContext ctx, string location_id) => api.ChangesAsync(ctx, location_id));
        app.MapPost("/api/orgs/locations", (Func<HttpContext, Task<IResult>>)(ctx => api.PostAsync(ctx)));
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
                           WHERE n.nspname = 'auth' AND pg_has_role(current_user, t.relowner, 'MEMBER'))
                   OR EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspname = 'auth' AND pg_has_role(current_user, n.nspowner, 'MEMBER'))
            FROM pg_roles r WHERE r.rolname = current_user
            """, c);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("startup guard: role not found");
        if (r.GetBoolean(0)) throw new InvalidOperationException("startup guard: the API role is a superuser");
        if (r.GetBoolean(1)) throw new InvalidOperationException("startup guard: the API role has BYPASSRLS");
        if (r.GetBoolean(2)) throw new InvalidOperationException("startup guard: the API role owns auth objects");
    }
}
