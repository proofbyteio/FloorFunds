// S2a P4 (F-A3): `--ui-host <ui/orgs dir>` mode of Admin.Api.Checks. Runs the real Admin.Api (already started by Program.cs on
// AUTH_CHECKS_DB, real PostgreSQL) plus a TEST-ONLY static + proxy host on another free port in 18130-18139, so a headless browser
// (ui/orgs/tests/e2e.mjs) can drive screen 9 as each persona. Not a product host (OQ-S2a-1 stays open): it stands in for the
// /admin session by adding the persona's Bearer token on the server side; the page itself never sees a token.
// Seeds through the API as the Super Admin (never by hand-made SQL), prints one JSON line, waits for stdin to close, then
// prints one JSON line of what the DB holds (change rows per actor, events per type) and returns; Program.cs cleans up.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;

static class UiHost
{
    public sealed record Persona(string Token, string AdminType);

    // Same rule as ui/login-hub: no inline script or style anywhere; only same-origin assets and fetches.
    public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; img-src 'self'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    { [".css"] = "text/css; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".ttf"] = "font/ttf" };

    public static async Task<int> RunAsync(string uiDir, int apiPort, int uiPort, IReadOnlyDictionary<string, Persona> personas,
        HashSet<Guid> readerLocations, Guid partnerA, Guid partnerB, Guid[] tenants, NpgsqlDataSource ownerDs)
    {
        uiDir = Path.GetFullPath(uiDir);
        var page = await File.ReadAllTextAsync(Path.Combine(uiDir, "location-sign-in.html"));
        const string hostAttrs = "data-api-base=\"/api\" data-admin-type=\"\"";
        if (!page.Contains(hostAttrs, StringComparison.Ordinal)) { Console.Error.WriteLine("ui-host: page has no host attributes"); return 2; }

        using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
        // ---- seed via the API as the Super Admin ----
        async Task<JsonObject> Create(Guid partner, string name, object cfg)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orgs/locations")
            { Content = new StringContent(JsonSerializer.Serialize(new { partner_id = partner.ToString("D"), name, login_config = cfg }), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personas["sa"].Token);
            using var res = await api.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (res.StatusCode != HttpStatusCode.Created) throw new InvalidOperationException($"ui-host seed {name}: {(int)res.StatusCode} {body}");
            return JsonNode.Parse(body)!.AsObject();
        }
        object Db(string d) => new { provider_kind = "database", display_name = d, entra_allowed_tenant_ids = Array.Empty<string>(), allow_db_fallback = false, mfa_required = true };
        object Entra(string d, Guid t) => new { provider_kind = "entra", display_name = d, entra_allowed_tenant_ids = new[] { t.ToString("D") }, allow_db_fallback = false, mfa_required = true };
        var l1 = await Create(partnerA, "UI Cage 1", Db("UI Casino A"));            // PA partner-edit db -> entra; RD view only
        var l2 = await Create(partnerA, "UI Cage 2", Entra("UI Casino A", tenants[0])); // SA view entra (card Set), downgrade
        var l3 = await Create(partnerB, "UI Cage B", Entra("UI Casino B", tenants[1])); // tenants[1] bound to partner B: tenant-taken
        var l4 = await Create(partnerA, "UI Cage 4", Db("UI Casino A"));            // SA view db (card Not set), conflict
        Guid Id(JsonObject o) => Guid.Parse((string)o["location_id"]!);
        readerLocations.Add(Id(l1)); readerLocations.Add(Id(l2)); // org:V reader of L1/L2 only

        // ---- hostile API text (SSE2 1ec73d38: live XSS probe; TLD 2026-11-04 14:18 (A)) ----
        // L5 carries the payloads in every free-text field the API accepts (location name, display_name, reason). The probes
        // put them in fields the API must refuse (UUID / unknown keys); their outcomes are reported, API validation is unchanged.
        string[] xp = ["<img src=x onerror=window.__xss=1>", "<script>window.__xss=1</script>", "\"><svg onload=window.__xss=1>", "javascript:window.__xss=1"];
        string xName = string.Join(" ", xp), xDname = string.Join(" ", xp[..3]), xReason = string.Join(" ", xp[3], xp[0], xp[1], xp[2]);
        var l5 = await Create(partnerA, xName, new { provider_kind = "database", display_name = xDname, entra_allowed_tenant_ids = Array.Empty<string>(),
            allow_db_fallback = false, mfa_required = true, reason = xReason });
        readerLocations.Add(Id(l5));
        async Task<JsonObject> Probe(string label, object body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/orgs/locations")
            { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", personas["sa"].Token);
            using var res = await api.SendAsync(req);
            var fields = new JsonArray();
            if (JsonNode.Parse(await res.Content.ReadAsStringAsync()) is JsonObject eb && eb["fields"] is JsonArray fa)
                foreach (var f in fa) fields.Add($"{(string?)f?["field"]}:{(string?)f?["code"]}");
            return new JsonObject { ["probe"] = label, ["status"] = (int)res.StatusCode, ["fields"] = fields };
        }
        var probes = new JsonArray
        {
            await Probe("tenant id = javascript: URL", new { partner_id = partnerA.ToString("D"), name = "UI XSS probe 1", login_config = new { provider_kind = "entra", display_name = "UI Casino A",
                entra_allowed_tenant_ids = new[] { xp[3] }, allow_db_fallback = false, mfa_required = true } }),
            await Probe("tenant id = <img onerror>", new { partner_id = partnerA.ToString("D"), name = "UI XSS probe 2", login_config = new { provider_kind = "entra", display_name = "UI Casino A",
                entra_allowed_tenant_ids = new[] { xp[0] }, allow_db_fallback = false, mfa_required = true } }),
            await Probe("partner_id = <svg onload>", new { partner_id = xp[2], name = "UI XSS probe 3", login_config = Db("UI Casino A") }),
            await Probe("oidc_authority = javascript: URL (only link-like field)", new { partner_id = partnerA.ToString("D"), name = "UI XSS probe 4", login_config = new { provider_kind = "entra",
                display_name = "UI Casino A", entra_allowed_tenant_ids = new[] { tenants[2].ToString("D") }, oidc_authority = xp[3], allow_db_fallback = false, mfa_required = true } }),
            await Probe("provider_kind = <script>", new { partner_id = partnerA.ToString("D"), name = "UI XSS probe 5", login_config = new { provider_kind = xp[1], display_name = "UI Casino A",
                entra_allowed_tenant_ids = Array.Empty<string>(), allow_db_fallback = false, mfa_required = true } }),
        };

        // ---- static + proxy host ----
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        b.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        b.WebHost.UseKestrel(k => { k.AddServerHeader = false; });
        b.WebHost.UseUrls($"http://127.0.0.1:{uiPort}");
        await using var app = b.Build();
        app.Use((c, next) => { c.Response.OnStarting(() => { c.Response.Headers.ContentSecurityPolicy = Csp; c.Response.Headers.XFrameOptions = "DENY";
            c.Response.Headers.XContentTypeOptions = "nosniff"; c.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; }); return next(c); });
        app.MapGet("/as/{persona}/orgs/{**rest}", (string persona, string rest) =>
        {
            if (!personas.TryGetValue(persona, out var p)) return Results.NotFound();
            var html = page.Replace(hostAttrs, $"data-api-base=\"/as/{persona}/api\" data-admin-type=\"{p.AdminType}\"", StringComparison.Ordinal);
            return Results.Text(html, "text/html; charset=utf-8");
        });
        app.MapGet("/ui/orgs/{**path}", (string path) =>
        {
            var ext = Path.GetExtension(path);
            var full = Path.GetFullPath(Path.Combine(uiDir, path));
            var allowed = (path.StartsWith("js/", StringComparison.Ordinal) && ext == ".mjs") || (path.StartsWith("fonts/", StringComparison.Ordinal) && ext == ".ttf") || path == "theme.css" || path == "orgs.css";
            if (!allowed || !full.StartsWith(uiDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return Results.NotFound();
            return Results.File(full, Types[ext]);
        });
        app.Map("/as/{persona}/api/{**rest}", async (HttpContext c, string persona, string rest) =>
        {
            if (!personas.TryGetValue(persona, out var p)) { c.Response.StatusCode = 404; return; }
            using var req = new HttpRequestMessage(new HttpMethod(c.Request.Method), "/api/" + rest + c.Request.QueryString.Value);
            if (p.Token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", p.Token);
            if (c.Request.Headers.IfMatch.Count > 0) req.Headers.TryAddWithoutValidation("If-Match", c.Request.Headers.IfMatch.ToString());
            req.Headers.TryAddWithoutValidation("Accept", c.Request.Headers.Accept.ToString());
            if (c.Request.ContentLength > 0 || c.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                var ms = new MemoryStream(); await c.Request.Body.CopyToAsync(ms);
                req.Content = new ByteArrayContent(ms.ToArray());
                if (c.Request.ContentType is { } ct) req.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
            }
            using var res = await api.SendAsync(req);
            c.Response.StatusCode = (int)res.StatusCode;
            foreach (var h in new[] { "ETag", "X-Request-ID", "Location", "WWW-Authenticate" })
                if (res.Headers.TryGetValues(h, out var v)) c.Response.Headers[h] = string.Join(",", v);
            if (res.Content.Headers.ContentType is { } rct) c.Response.ContentType = rct.ToString();
            await res.Content.CopyToAsync(c.Response.Body);
        });
        await app.StartAsync();

        var info = new JsonObject
        {
            ["ui"] = $"http://127.0.0.1:{uiPort}",
            ["partner_a"] = partnerA.ToString("D"), ["partner_b"] = partnerB.ToString("D"),
            ["l1"] = Id(l1).ToString("D"), ["l2"] = Id(l2).ToString("D"), ["l3"] = Id(l3).ToString("D"), ["l4"] = Id(l4).ToString("D"),
            ["t_free"] = tenants[2].ToString("D"), ["t_taken"] = tenants[1].ToString("D"),
            ["l5"] = Id(l5).ToString("D"), ["xss"] = new JsonObject { ["name"] = xName, ["display_name"] = xDname, ["reason"] = xReason, ["probes"] = probes },
            ["users"] = new JsonObject(personas.Where(kv => kv.Value.Token.Length > 0).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(Sub(kv.Value.Token))))),
        };
        Console.WriteLine("UIHOST " + info.ToJsonString());
        await Console.In.ReadToEndAsync(); // the e2e closes stdin when done
        await app.StopAsync();

        // ---- what real PG holds after the run (read as the owner) ----
        var summary = new JsonObject();
        await using (var c = await ownerDs.OpenConnectionAsync())
        {
            var byActor = new JsonObject();
            await using (var cmd = new NpgsqlCommand("SELECT actor_user_id::text, count(*) FROM auth.location_login_config_change GROUP BY 1 ORDER BY 1", c))
            await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) byActor[r.GetString(0)] = r.GetInt64(1);
            var byType = new JsonObject();
            await using (var cmd = new NpgsqlCommand("SELECT envelope->>'event_type', coalesce(envelope->'actor'->>'user_id',''), count(*) FROM auth.audit_outbox GROUP BY 1, 2 ORDER BY 1, 2", c))
            await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) byType[$"{r.GetString(0)} {r.GetString(1)}"] = r.GetInt64(2);
            summary["changes_by_actor"] = byActor; summary["events_by_type_actor"] = byType;
            await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM auth.location", c)) summary["locations"] = (long)(await cmd.ExecuteScalarAsync())!;
            // TLD 14:18 (A): the stores keep the hostile text byte-for-byte (UTF-8 hex), no server-side HTML escaping anywhere.
            var x = new JsonObject();
            async Task<JsonArray> Hex(string sql)
            {
                var a = new JsonArray();
                await using var cmd = new NpgsqlCommand(sql, c); cmd.Parameters.Add(new() { Value = Id(l5) });
                await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) a.Add(r.IsDBNull(0) ? null : r.GetString(0));
                return a;
            }
            x["location_name_hex"] = await Hex("SELECT encode(convert_to(name, 'UTF8'), 'hex') FROM auth.location WHERE location_id = $1");
            x["display_name_hex"] = await Hex("SELECT encode(convert_to(display_name, 'UTF8'), 'hex') FROM auth.location_login_config WHERE location_id = $1");
            x["change_reason_hex"] = await Hex("SELECT encode(convert_to(reason, 'UTF8'), 'hex') FROM auth.location_login_config_change WHERE location_id = $1 ORDER BY row_version");
            x["outbox_reason_hex"] = await Hex("SELECT encode(convert_to(envelope->'details'->>'reason', 'UTF8'), 'hex') FROM auth.audit_outbox "
                + "WHERE event_type = 'auth.provider.changed' AND envelope->'details'->>'location_id' = $1::text ORDER BY (envelope->'details'->>'row_version')::bigint");
            await using (var cmd = new NpgsqlCommand(
                """
                SELECT (SELECT count(*) FROM auth.location WHERE name ~ '&(lt|gt|quot|amp|apos|#[0-9x])')
                     + (SELECT count(*) FROM auth.location_login_config WHERE display_name ~ '&(lt|gt|quot|amp|apos|#[0-9x])')
                     + (SELECT count(*) FROM auth.location_login_config_change WHERE reason ~ '&(lt|gt|quot|amp|apos|#[0-9x])')
                     + (SELECT count(*) FROM auth.audit_outbox WHERE envelope::text ~ '&(lt|gt|quot|amp|apos|#[0-9x])')
                """, c)) x["html_entity_rows"] = (long)(await cmd.ExecuteScalarAsync())!;
            summary["xss"] = x;
        }
        Console.WriteLine("UIHOST-DB " + summary.ToJsonString());
        return 0;
    }

    static string Sub(string jwt) => (string)JsonNode.Parse(Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(jwt.Split('.')[1])))!["sub"]!;
}
