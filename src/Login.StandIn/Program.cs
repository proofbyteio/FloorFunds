// Stand-in login service (SI1). Contract: tag contract-v1 (e363090).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => Results.NotFound());
app.Run();
