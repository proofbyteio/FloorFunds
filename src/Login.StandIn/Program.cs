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
Listeners.UseListenerRules(app, ports, issuerPath);
using var keys = new SigningKeys();
var (disco, jwks) = Discovery.Build(issuer, keys);
Discovery.Map(app, issuerPath, disco, jwks);
app.Run();
return 0;
