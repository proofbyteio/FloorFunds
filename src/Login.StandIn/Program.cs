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
StandInState.StateDir = Listeners.StateDir(ports);
Directory.CreateDirectory(StandInState.StateDir);
File.WriteAllText(Path.Combine(StandInState.StateDir, "server.pid"), Environment.ProcessId.ToString());
Listeners.Bind(builder, ports);
// contract 1.2: the issuer path always contains /standin.
var issuer = (cfg["StandIn:Issuer"] ?? $"http://localhost:{ports.Front}/standin").TrimEnd('/');
var issuerPath = new Uri(issuer).AbsolutePath.TrimEnd('/');
if (!issuerPath.Contains("/standin")) { Console.Error.WriteLine("Login.StandIn refused to start: issuer path must contain /standin"); return 2; }

builder.Services.AddAntiforgery(o => { o.Cookie.Name = "standin_af"; o.FormFieldName = "__RequestVerificationToken"; o.Cookie.Path = issuerPath; });
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
// SI1-13..16: tokens, sign-in, authorize, token/revoke/introspect/endsession.
var tokens = new TokenIssuer(issuer, keys);
SignIn.Map(app, state, issuerPath, issuer, ports);
OAuth.Map(app, state, issuerPath, issuer, ports, tokens);
Handoff.Map(app, state, issuerPath, issuer, ports);
app.Run();
return 0;
