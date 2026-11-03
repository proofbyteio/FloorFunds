// Stand-in login service (SI1). Contract: tag contract-v1 (e363090).
using Login.StandIn;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var bindHost = cfg["StandIn:BindHost"] ?? "127.0.0.1";
var refusal = StartupGuard.Check(builder.Environment.EnvironmentName, Environment.GetEnvironmentVariable, new[] { bindHost });
if (refusal is not null)
{
    Console.Error.WriteLine($"Login.StandIn refused to start: {refusal}");
    return 2;
}
var ports = Listeners.Read(cfg);
Listeners.Bind(builder, ports);
// contract 1.2: the issuer path always contains /standin.
var issuer = (cfg["StandIn:Issuer"] ?? $"http://localhost:{ports.Front}/standin").TrimEnd('/');
var issuerPath = new Uri(issuer).AbsolutePath.TrimEnd('/');
if (!issuerPath.Contains("/standin")) { Console.Error.WriteLine("Login.StandIn refused to start: issuer path must contain /standin"); return 2; }

var app = builder.Build();
// Slice 3: the allowlist runs first on every front request.
var allow = new Allowlist(cfg["StandIn:AllowlistJson"] ?? "/tmp/sse-d0/allowlist-active.json");
Console.WriteLine($"allowlist loaded={allow.Loaded} entries={allow.Count}");
Allowlist.Use(app, ports, allow);
Listeners.UseListenerRules(app, ports, issuerPath);
using var keys = new SigningKeys();
var (disco, jwks) = Discovery.Build(issuer, keys);
Discovery.Map(app, issuerPath, disco, jwks);
var state = new StandInState(cfg["StandIn:Fixtures"] ?? Path.Combine(AppContext.BaseDirectory, "fixtures.json"));
Control.Map(app, state);
app.Run();
return 0;
