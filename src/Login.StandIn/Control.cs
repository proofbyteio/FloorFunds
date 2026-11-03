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
    public Dictionary<string, StandInUser> Users { get; } = new(StringComparer.Ordinal);
    public object Gate => _gate;
    public Dictionary<string, StandInPc> Pcs { get; } = new(StringComparer.Ordinal);
    // Sign-in transactions, sessions, codes and tokens (SI1-14..16). Guarded by Gate.
    public AuthStore Auth { get; } = new();
    public Dictionary<string, OAuthClient> OAuthClients { get; private set; } = new();

    // Appends to the control feed. Callers build the event with Audit.Build.
    public void Emit(JsonObject e) { lock (_gate) _audit.Add(e); }

    // [TL Decision 9:16 PM, B2] FIXTURE client private keys go here (must be under /tmp; checked in Clients.Load).
    public static string StateDir { get; set; } = "/tmp/sse-d0/standin-18080";
    public static string ClientKeyDir => Environment.GetEnvironmentVariable("STANDIN_CLIENT_KEY_DIR") ?? Path.Combine(StateDir, "fixture-client-keys");
    public StandInState(string fixturePath) { _fixturePath = fixturePath; Reset(); }

    public void Reset()
    {
        lock (_gate)
        {
            Fixtures = JsonNode.Parse(File.ReadAllText(_fixturePath))!.AsObject();
            OutboxFault = "ok"; DeviceRegistryFault = "ok";
            _audit.Clear();
            Clock.Reset();
            Users.Clear();
            Pcs.Clear();
            foreach (var u in Fixtures["users"]!.AsArray())
            {
                var user = new StandInUser { Username = (string)u!["username"]!, UserId = (string)u["user_id"]!, Status = (string)u["status"]!,
                    Roles = u["roles"]!.AsArray().Select(r => ((string)r!["app_code"]!, (string)r["role"]!)).ToList(),
                    LocationSlugs = u["location_slugs"]!.AsArray().Select(x => (string)x!).ToList(),
                    PermVersion = (int?)u["perm_version"] ?? 1, AdminType = (string?)u["admin_type"], FixtureAmr = u["fixture_amr"]?.AsArray().Select(x => (string)x!).ToArray(), PartnerId = (string?)u["partner_id"] };
                foreach (var rc in u["recovery_codes"]?.AsArray() ?? new JsonArray()) user.RecoveryCodeHashes.Add(AuthStore.H((string)rc!));
                user.SetPassword(((string)u["password"]!).Normalize(System.Text.NormalizationForm.FormKC));
                Users[user.Username] = user;
            }
            Auth.Clear();
            OAuthClients = Clients.Load(Fixtures, ClientKeyDir);
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
        app.MapPut(P + "/users/{username}", async (string username, HttpRequest r) =>
        {
            var o = await ReadObject(r);
            if (o is null || username.Length is 0 or > 64) return Bad();
            string? pw; string? status; List<(string, string)> roles; List<string> slugs;
            try
            {
                pw = o["password"]?.GetValue<string>(); status = o["status"]?.GetValue<string>();
                roles = (o["roles"]?.AsArray() ?? new JsonArray()).Select(x => (x!["app_code"]!.GetValue<string>(), x["role"]!.GetValue<string>())).ToList();
                slugs = (o["location_slugs"]?.AsArray() ?? new JsonArray()).Select(x => x!.GetValue<string>()).ToList();
            }
            catch (Exception e) when (e is InvalidOperationException or NullReferenceException or FormatException) { return Bad(); }
            if (pw is null || status is null || !UserFixtures.Statuses.Contains(status)) return Bad();
            lock (st.Gate)
            {
                var presetBySlug = UserFixtures.PresetBySlug(st.Fixtures);
                var apps = UserFixtures.AppCodes(st.Fixtures);
                if (slugs.Any(s => !presetBySlug.ContainsKey(s)) || roles.Any(x => !apps.Contains(x.Item1))) return Bad();
                // [ASSUMPTION A12] the presets of every partner the user's locations belong to apply; no location = both presets.
                var presets = slugs.Count == 0 ? new HashSet<string> { PasswordPolicy.Pci, PasswordPolicy.Nevada } : slugs.Select(s => presetBySlug[s]).ToHashSet();
                st.Users.TryGetValue(username, out var existing);
                var nf = pw.Normalize(System.Text.NormalizationForm.FormKC);
                var violations = PasswordPolicy.Validate(pw, username, presets, p => existing?.Reused(p) ?? false);
                if (violations.Count > 0) return Results.Json(new { violations }, statusCode: 422);
                var user = existing ?? new StandInUser { Username = username, UserId = Guid.NewGuid().ToString(), Status = status, Roles = roles, LocationSlugs = slugs };
                user.Status = status; user.Roles = roles; user.LocationSlugs = slugs;
                user.SetPassword(nf);
                st.Users[username] = user;
                return Results.Json(new { user_id = user.UserId });
            }
        });
        app.MapGet(P + "/users/{username}/totp", (string username) =>
        {
            lock (st.Gate)
                return st.Users.TryGetValue(username, out var u) ? Results.Json(new { code = Totp.Code(u.TotpSecret, st.Clock.Now) }) : Results.NotFound();
        });
        app.MapPut(P + "/pcs/{pc_id}", async (string pc_id, HttpRequest r) =>
        {
            var o = await ReadObject(r);
            if (o is null || !Login.StandIn.Pcs.ValidId(pc_id)) return Bad();
            string? mode, slug;
            try { mode = o["mode"]?.GetValue<string>(); slug = o["location_slug"]?.GetValue<string>(); }
            catch (InvalidOperationException) { return Bad(); }
            if (mode is null || slug is null || !Login.StandIn.Pcs.Modes.Contains(mode)) return Bad();
            lock (st.Gate)
            {
                if (!UserFixtures.PresetBySlug(st.Fixtures).ContainsKey(slug)) return Bad();
                st.Pcs[pc_id] = new StandInPc(pc_id, mode, slug);
            }
            return Results.Json(new { cookie = new { name = "standin_pc", value = pc_id } });
        });
        app.MapGet(P + "/audit", (int? since) =>
        {
            var (ev, next) = st.AuditSince(since ?? 0);
            return Results.Json(new { events = ev, next });
        });
    }
}
