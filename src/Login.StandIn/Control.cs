using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Login.StandIn;

// standin-control 2.2: the test clock. Every server time check must read Now from here.
public sealed class TestClock
{
    long _offsetTicks;
    DateTimeOffset? _frozen; // OQ-55 (TL 9:42): a frozen clock moves only by advance, so live tests hit exact ms boundaries.
    readonly object _g = new();
    public DateTimeOffset Now { get { lock (_g) return _frozen ?? DateTimeOffset.UtcNow.AddTicks(_offsetTicks); } }
    public void Advance(long seconds) => AdvanceMs(checked(seconds * 1000));
    public void AdvanceMs(long ms) { lock (_g) { var t = TimeSpan.FromMilliseconds(ms).Ticks; if (_frozen is { } f) _frozen = f.AddTicks(t); else _offsetTicks += t; } }
    public void Freeze(bool on) { lock (_g) { if (on) _frozen ??= DateTimeOffset.UtcNow.AddTicks(_offsetTicks); else if (_frozen is { } f) { _offsetTicks = (f - DateTimeOffset.UtcNow).Ticks; _frozen = null; } } }
    public bool Frozen { get { lock (_g) return _frozen is not null; } }
    // [ASSUMPTION A5] reset puts the clock back to real time (and unfreezes it).
    public void Reset() { lock (_g) { _offsetTicks = 0; _frozen = null; } }
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

    // [TL Decision 9:16 PM, B2] FIXTURE client private keys go here (must be under the stand-in temp root; checked in Clients.Load).
    public static string StateDir { get; set; } = Path.Combine(Listeners.StateRoot, "standin-18080"); // G8
    public static string ClientKeyDir => Environment.GetEnvironmentVariable("STANDIN_CLIENT_KEY_DIR") ?? Path.Combine(StateDir, "fixture-client-keys");
    // F14 (TL 9:42): client secrets and recovery codes are not in the repo. They are generated once per process start (so a
    // reset keeps them stable for the driver) and written to <state dir>/fixture-secrets.json (0600), which the driver reads.
    JsonObject? _secrets;
    void InjectSecrets(JsonObject fx)
    {
        static string R(int n) => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(n)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (_secrets is null)
        {
            var cl = new JsonObject(); var rc = new JsonObject();
            foreach (var c in fx["clients"]!.AsArray()) cl[(string)c!["client_id"]!] = R(32);
            foreach (var u in fx["users"]!.AsArray())
                if ((int?)u!["recovery_codes_count"] is int k and > 0)
                    rc[(string)u["username"]!] = new JsonArray(Enumerable.Range(0, k).Select(_ => (JsonNode)("FIXTURE-RC-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)))).ToArray());
            // TL 9:50 PM: a fixture password "GENERATED" (super_admin) is generated too.
            var pw = new JsonObject();
            foreach (var u in fx["users"]!.AsArray())
                if ((string?)u!["password"] == "GENERATED") pw[(string)u["username"]!] = "Fx-" + R(18) + "-9a";
            _secrets = new JsonObject { ["_label"] = "FIXTURE secrets, generated at start; never commit", ["clients"] = cl, ["recovery_codes"] = rc, ["passwords"] = pw };
            var path = Path.Combine(StateDir, "fixture-secrets.json");
            File.WriteAllText(path, "");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(path, _secrets.ToJsonString());
        }
        foreach (var c in fx["clients"]!.AsArray()) c!["secret"] = (string)_secrets["clients"]![(string)c["client_id"]!]!;
        foreach (var u in fx["users"]!.AsArray())
        {
            if (_secrets["recovery_codes"]![(string)u!["username"]!] is JsonArray a) u["recovery_codes"] = a.DeepClone();
            if (_secrets["passwords"]![(string)u["username"]!] is JsonValue p) u["password"] = (string)p!;
        }
    }

    public StandInState(string fixturePath) { _fixturePath = fixturePath; Reset(); }

    public void Reset()
    {
        lock (_gate)
        {
            Fixtures = JsonNode.Parse(File.ReadAllText(_fixturePath))!.AsObject();
            InjectSecrets(Fixtures);
            OutboxFault = "ok"; DeviceRegistryFault = "ok"; SessionCap.Reset();
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
        // OQ-55 (TL 9:42) [v1.1 control addition]: freeze / unfreeze; while frozen only advance moves time.
        app.MapPost(P + "/clock/freeze", async (HttpRequest r) =>
        {
            var o = await ReadObject(r);
            if (o?["frozen"] is not JsonValue v || !v.TryGetValue(out bool on) || o.Count != 1) return Bad();
            st.Clock.Freeze(on);
            return Results.Json(new { now = st.Clock.Rfc3339, frozen = st.Clock.Frozen });
        });
        app.MapPost(P + "/clock/advance", async (HttpRequest r) =>
        {
            var o = await ReadObject(r);
            // n must be a non-negative integer; time never goes back.
            // OQ-55: exactly one of seconds or ms.
            if (o is null || o.Count != 1) return Bad();
            if (o["ms"] is JsonValue mv) { if (!mv.TryGetValue(out long ms) || ms < 0) return Bad(); st.Clock.AdvanceMs(ms); }
            else if (o["seconds"] is not JsonValue v || !v.TryGetValue(out long n) || n < 0) return Bad();
            else st.Clock.Advance(n);
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
            {
                // SEC76: the code for the earliest unused step in the +-30 s window; 409 when all are used (advance the clock 30 s).
                if (!st.Users.TryGetValue(username, out var u)) return Results.NotFound();
                return Totp.NextUnused(u, st.Clock.Now) is { } s ? Results.Json(new { code = Totp.CodeAt(u.TotpSecret, s) }) : Results.Json(new { error = "no_unused_step" }, statusCode: 409);
            }
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
