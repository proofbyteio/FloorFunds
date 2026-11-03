using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// standin-control 2.2: the test clock. Every server time check must read Now from here.
public sealed class TestClock
{
    long _offsetTicks;
    public DateTimeOffset Now => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));
    public void Advance(long seconds) => Interlocked.Add(ref _offsetTicks, TimeSpan.FromSeconds(seconds).Ticks);
    // [ASSUMPTION A5] reset puts the clock back to real time.
    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
    public string Rfc3339 => Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

// In-memory state (PLAN §3.3: no database). Reset reloads the fixtures (standin-control 2.1, §4).
public sealed class StandInState
{
    readonly string _fixturePath;
    readonly object _gate = new();
    public TestClock Clock { get; } = new();
    public JsonObject Fixtures { get; private set; } = new();
    public string OutboxFault { get; private set; } = "ok";
    public string DeviceRegistryFault { get; private set; } = "ok";
    readonly List<JsonObject> _audit = new();

    public StandInState(string fixturePath) { _fixturePath = fixturePath; Reset(); }

    public void Reset()
    {
        lock (_gate)
        {
            Fixtures = JsonNode.Parse(File.ReadAllText(_fixturePath))!.AsObject();
            OutboxFault = "ok"; DeviceRegistryFault = "ok";
            _audit.Clear();
            Clock.Reset();
            // Users, sessions, nonces, hub_launch and lockouts will live in this object and clear here too.
        }
    }

    public void SetFaults(string? outbox, string? registry)
    { lock (_gate) { if (outbox is not null) OutboxFault = outbox; if (registry is not null) DeviceRegistryFault = registry; } }

    public (JsonObject[] Events, int Next) AuditSince(int since)
    { lock (_gate) { var s = Math.Clamp(since, 0, _audit.Count); return (_audit.Skip(s).Select(e => (JsonObject)e.DeepClone()).ToArray(), _audit.Count); } }
}

public static class Control
{
    static IResult Bad() => Results.Json(new { error = "invalid_request" }, statusCode: 400);

    static async Task<JsonObject?> ReadObject(HttpRequest r)
    {
        try { return (await JsonNode.ParseAsync(r.Body)) as JsonObject; } catch (JsonException) { return null; }
    }

    public static void Map(WebApplication app, StandInState st)
    {
        const string P = Listeners.ControlPrefix;
        app.MapPost(P + "/reset", () => { st.Reset(); return Results.NoContent(); });
        app.MapGet(P + "/clock", () => Results.Json(new { now = st.Clock.Rfc3339 }));
        app.MapPost(P + "/clock/advance", async (HttpRequest r) =>
        {
            var o = await ReadObject(r);
            // n must be a non-negative integer; time never goes back.
            if (o?["seconds"] is not JsonValue v || !v.TryGetValue(out long n) || n < 0) return Bad();
            st.Clock.Advance(n);
            return Results.Json(new { now = st.Clock.Rfc3339 });
        });
        app.MapPut(P + "/faults", async (HttpRequest r) =>
        {
            var o = await ReadObject(r);
            if (o is null || o.Any(kv => kv.Key is not ("outbox" or "device_registry"))) return Bad();
            string? ob = o["outbox"]?.GetValue<string>(), dr = o["device_registry"]?.GetValue<string>();
            if (ob is not (null or "ok" or "fail") || dr is not (null or "ok" or "unavailable")) return Bad();
            st.SetFaults(ob, dr);
            return Results.NoContent();
        });
        app.MapGet(P + "/audit", (int? since) =>
        {
            var (ev, next) = st.AuditSince(since ?? 0);
            return Results.Json(new { events = ev, next });
        });
    }
}
