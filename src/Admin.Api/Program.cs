using Admin.Api;

// Config: appsettings.json (optional), environment (Entra__Authority, ...), command line. Refuses to start on any invalid value.
var cfg = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddEnvironmentVariables().AddCommandLine(args).Build();
WebApplication app;
try { app = await AdminApiHost.BuildAsync(AdminApiOptions.FromConfiguration(cfg)); }
catch (Exception e)
{
    // Messages name config keys only; other exception messages are not printed (they could carry a connection string).
    Console.Error.WriteLine("admin-api refused to start: " + (e is InvalidOperationException ? e.Message : e.GetType().Name));
    return 2;
}
await app.RunAsync();
return 0;
