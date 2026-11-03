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
var app = builder.Build();
app.MapGet("/", () => Results.NotFound());
app.Run();
return 0;
