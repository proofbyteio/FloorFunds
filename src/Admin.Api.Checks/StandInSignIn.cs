using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

/// <summary>
/// Drives the stand-in's real admin sign-in, then the issuer and the admin API.
/// The suites' own tokens are minted here in the checks. This path is the one the shell uses.
/// </summary>
static class StandInSignIn
{
    const string Sa = "5f0c3e1a-0003-4000-8000-000000000c0a";
    const string Pa = "5f0c3e1a-0003-4000-8000-000000000c04";
    const string Partner = "5f0c3e1a-0001-4000-8000-000000000001";
    const string SaAssignment = "5f0c3e1a-0064-4000-8000-0000000000d1";
    const string PaAssignment = "5f0c3e1a-0003-4000-8000-000000000c14";

    public static async Task<(bool Ok, string Detail)> Run(string ownerCs, string adminCs, string issuerCs)
    {
        var dll = Path.Combine(RepoRoot(), "src", "Login.StandIn", "bin", "Debug", "net10.0", "Login.StandIn.dll");
        if (!File.Exists(dll)) return (false, "stand-in build is missing");
        var issuerDll = Path.Combine(RepoRoot(), "src", "Auth.Issuer", "bin", "Debug", "net10.0", "Auth.Issuer.dll");
        if (!File.Exists(issuerDll)) return (false, "issuer build is missing");
        var state = "/tmp/req064-signin";
        Directory.CreateDirectory(state);
        await File.WriteAllTextAsync(Path.Combine(state, "allowlist.json"),
            """{"entries":[{"value":"127.0.0.1/32"},{"value":"::1/128"}]}""" + "\n");
        var front = FreeBase();
        var issuerName = $"http://127.0.0.1:{front}/standin";
        var redirect = "http://127.0.0.1:18390/signin-oidc";
        var previous = await Issuers(ownerCs);
        Process? standIn = null;
        Process? issuer = null;
        WebApplication? api = null;
        string? jwks = null;
        try
        {
            await Bind(ownerCs, issuerName);
            standIn = StartStandIn(dll, front, state, issuerName, redirect);
            if (!await WaitJwks(front)) return (false, "stand-in did not publish jwks");
            jwks = Path.Combine(Path.GetTempPath(), $"standin-jwks-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(jwks, await new HttpClient().GetStringAsync($"http://127.0.0.1:{front}/standin/.well-known/jwks"));
            var started = await StartIssuer(issuerDll, issuerCs, issuerName, jwks);
            issuer = started.Process;
            if (issuer is null) return (false, "issuer did not listen");
            var issuerUrl = started.Url;
            var schema = Path.Combine(AppContext.BaseDirectory, "contract", "audit-events.schema.json");
            var opt = new Admin.Api.AdminApiOptions
            {
                ConnectionString = adminCs,
                EntraAuthority = "https://login.microsoftonline.com",
                EntraClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc",
                EntraSecretRef = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/req064-AbCdEf",
                SecretAccountIds = new HashSet<string> { "000000000000" },
                SecretRegions = new HashSet<string> { "us-west-2" },
                TokenIssuer = issuerName,
                IssuerUrl = issuerUrl,
                SigningKeys = [],
                AuditSchemaPath = schema,
                Urls = $"http://127.0.0.1:{FreePort()}",
            }.Validated();
            api = await Admin.Api.AdminApiHost.BuildAsync(opt);
            await api.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(opt.Urls.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
            var secret = (await File.ReadAllTextAsync("/tmp/ff-db/admin-dev-shell.secret")).Trim();
            var passwords = Passwords(state, front);
            foreach (var (user, username) in new[] { (Sa, "super_admin"), (Pa, "partner_admin") })
            {
                var token = await AccessToken(front, issuerName, redirect, username, passwords[username], secret);
                if (token is null) return (false, username + " sign-in did not return an access token");
                var jti = Payload(token).GetProperty("jti").GetString() ?? "";
                if (!Guid.TryParseExact(jti, "D", out _)) return (false, username + " jti is not a uuid");
                var opened = await Post(issuerUrl + "/v1/session", token);
                if (opened.Status != 200) return (false, username + " /v1/session " + opened.Status);
                foreach (var path in new[] { "/api/nav", "/api/orgs/locations", "/api/screens/catalog" })
                {
                    var res = await Send(http, path, token);
                    if (res.Status != 200) return (false, username + " " + path + " " + res.Status + " " + Clip(res.Body));
                }
            }
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (api is not null) { await api.StopAsync(); await api.DisposeAsync(); }
            Kill(issuer);
            Kill(standIn);
            if (jwks is not null) try { File.Delete(jwks); } catch { }
            await Restore(ownerCs, previous);
        }
    }

    static async Task<Dictionary<string, string>> Issuers(string ownerCs)
    {
        var found = new Dictionary<string, string>();
        await using var c = await Open(ownerCs);
        await using var cmd = new NpgsqlCommand("SELECT user_id::text, issuer FROM auth.user_issuer WHERE user_id = ANY($1)", c);
        cmd.Parameters.Add(new() { Value = new[] { Guid.Parse(Sa), Guid.Parse(Pa) } });
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) found[r.GetString(0)] = r.GetString(1);
        return found;
    }

    static async Task Bind(string ownerCs, string issuer)
    {
        await using var seed = await Req064Suite.OpenTestSeed(NpgsqlDataSource.Create(ownerCs));
        await using var sc = await seed.OpenConnectionAsync();
        await Exec(sc, """
            INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, status, version, granted_by, granted_at)
            SELECT $1, u.user_id, 'super_admin', 'global', 'active', 1, u.user_id, now()
              FROM auth.app_user u
             WHERE u.user_id = $2
            """, Guid.Parse(SaAssignment), Guid.Parse(Sa));
        await Exec(sc, """
            INSERT INTO auth.admin_assignment (
              assignment_id, user_id, role_id, scope_kind, partner_id, location_id, status, version, granted_by, granted_at, revoked_at)
            SELECT $1, u.user_id, 'partner_admin', 'partner', $2, NULL, 'active', 1, u.user_id, now(), NULL
              FROM auth.app_user u
             WHERE u.user_id = $3
            """, Guid.Parse(PaAssignment), Guid.Parse(Partner), Guid.Parse(Pa));
        await Exec(sc, """
            INSERT INTO auth.user_issuer (user_id, issuer) VALUES ($1, $3), ($2, $3)
            """, Guid.Parse(Sa), Guid.Parse(Pa), issuer);
    }

    static async Task Exec(NpgsqlConnection c, string sql, params object[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p });
        await cmd.ExecuteNonQueryAsync();
    }

    static Task Restore(string ownerCs, Dictionary<string, string> previous)
    {
        // The runner recreates the scratch database. Do not delete or update the rows.
        _ = ownerCs;
        _ = previous;
        return Task.CompletedTask;
    }

    static Process StartStandIn(string dll, int front, string state, string issuer, string redirect)
    {
        var psi = new ProcessStartInfo("dotnet", [dll])
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["STANDIN_ALLOW"] = "1";
        psi.Environment["STANDIN_PORT_BASE"] = front.ToString();
        psi.Environment["STANDIN_STATE_DIR"] = state;
        psi.Environment["ADMIN_DEV_SHELL"] = "1";
        psi.Environment["ADMIN_DEV_SHELL_REDIRECT"] = redirect;
        psi.Environment["StandIn__Issuer"] = issuer;
        psi.Environment["StandIn__AllowlistJson"] = Path.Combine(state, "allowlist.json");
        psi.Environment["StandIn__BindHost"] = "127.0.0.1";
        foreach (var name in new[] { "AWS_EXECUTION_ENV", "ECS_CONTAINER_METADATA_URI", "ECS_CONTAINER_METADATA_URI_V4" })
            psi.Environment.Remove(name);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("stand-in did not start");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    static async Task<(Process? Process, string Url)> StartIssuer(string dll, string connectionString, string iss, string jwks)
    {
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var psi = new ProcessStartInfo("dotnet", [dll, url])
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ISSUER_CONNECTION_STRING"] = connectionString;
        psi.Environment["ISSUER_ISS"] = iss;
        psi.Environment["ISSUER_JWKS"] = jwks;
        psi.Environment.Remove("ISSUER_CALLER_SECRET");
        var process = Process.Start(psi);
        if (process is null) return (null, url);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && e.Data.Contains("issuer: listening", StringComparison.Ordinal))
                ready.TrySetResult();
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var done = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(15)));
        if (done != ready.Task || process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (null, url);
        }
        return (process, url);
    }

    static async Task<bool> WaitJwks(int front)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 40; i++)
        {
            try
            {
                var res = await http.GetAsync($"http://127.0.0.1:{front}/standin/.well-known/jwks");
                if (res.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        return false;
    }

    static Dictionary<string, string> Passwords(string state, int front)
    {
        var path = Path.Combine(state, $"standin-{front}", "fixture-secrets.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var found = new Dictionary<string, string>();
        foreach (var p in doc.RootElement.GetProperty("passwords").EnumerateObject())
            found[p.Name] = p.Value.GetString() ?? "";
        return found;
    }

    static async Task<string?> AccessToken(int front, string issuer, string redirect, string username, string password, string secret)
    {
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = "admin_dev_shell",
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = "openid backoffice_api",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var authorize = issuer + "/connect/authorize?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false, UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var page = await FollowGet(http, authorize);
        if (page.Status != 200) return null;
        var token = Field(page.Body, "__RequestVerificationToken");
        var ret = Field(page.Body, "ret");
        if (token is null) return null;
        var posted = await PostForm(http, issuer + "/admin", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = username,
            ["password"] = password,
            ["ret"] = ret ?? "",
        });
        if (posted.Status != 200 || !posted.Body.Contains("field-admin-totp", StringComparison.Ordinal)) return null;
        var totpToken = Field(posted.Body, "__RequestVerificationToken");
        var code = await Totp(front, username);
        if (totpToken is null || code is null) return null;
        var mfa = await PostForm(http, issuer + "/admin/mfa", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = totpToken,
            ["totp_code"] = code,
        });
        if (mfa.Status is < 300 or >= 400 || mfa.Location is null) return null;
        var back = await FollowGet(http, Absolute(issuer + "/admin/mfa", mfa.Location));
        if (back.Location is null || !back.Location.Contains("code=", StringComparison.Ordinal)) return null;
        var codeValue = Query(back.Location, "code");
        if (codeValue is null) return null;
        using var tokenHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{front + 1}/standin/connect/token");
        msg.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin_dev_shell:" + secret)));
        msg.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = codeValue,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        });
        HttpResponseMessage exchangedMsg;
        try { exchangedMsg = await tokenHttp.SendAsync(msg); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException("POST token", ex); }
        var exchanged = await Read(exchangedMsg);
        if (exchanged.Status != 200) return null;
        using var doc = JsonDocument.Parse(exchanged.Body);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    static async Task<string?> Totp(int front, string username)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var res = await http.GetAsync($"http://127.0.0.1:{front + 2}/_standin/users/{username}/totp");
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    static async Task<(int Status, string Body, string? Location)> FollowGet(HttpClient http, string url)
    {
        for (var i = 0; i < 5; i++)
        {
            HttpResponseMessage sent;
            try { sent = await http.GetAsync(url); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException("GET " + url, ex); }
            var res = await Read(sent);
            if (res.Status is < 300 or >= 400) return res;
            if (res.Location is null) return res;
            if (res.Location.Contains("code=", StringComparison.Ordinal) && res.Location.Contains("signin-oidc", StringComparison.Ordinal)) return res;
            url = Absolute(url, res.Location);
        }
        return (0, "", null);
    }

    static async Task<(int Status, string Body, string? Location)> PostForm(HttpClient http, string url, Dictionary<string, string> fields)
    {
        HttpResponseMessage sent;
        try { sent = await http.PostAsync(url, new FormUrlEncodedContent(fields)); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException("POST " + url, ex); }
        return await Read(sent);
    }

    static async Task<(int Status, string Body, string? Location)> Read(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        var loc = res.Headers.Location?.ToString();
        return ((int)res.StatusCode, body, loc);
    }

    static async Task<(int Status, string Body)> Post(string url, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var res = await http.PostAsync(url, new StringContent(token));
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    static async Task<(int Status, string Body)> Send(HttpClient http, string path, string token)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, path);
        msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var res = await http.SendAsync(msg);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    static string? Field(string html, string name)
    {
        var mark = $"name=\"{name}\" value=\"";
        var at = html.IndexOf(mark, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + mark.Length;
        var end = html.IndexOf('"', start);
        return end < 0 ? null : WebUtility.HtmlDecode(html[start..end]);
    }

    static string? Query(string url, string key)
    {
        var q = url.IndexOf('?', StringComparison.Ordinal);
        if (q < 0) return null;
        foreach (var part in url[(q + 1)..].Split('&'))
        {
            var i = part.IndexOf('=');
            if (i > 0 && Uri.UnescapeDataString(part[..i]) == key)
                return Uri.UnescapeDataString(part[(i + 1)..]);
        }
        return null;
    }

    static string Absolute(string current, string location)
    {
        if (location.StartsWith("http://", StringComparison.Ordinal) || location.StartsWith("https://", StringComparison.Ordinal))
            return location;
        return new Uri(new Uri(current), location).ToString();
    }

    static JsonElement Payload(string jwt)
    {
        var json = Base64UrlEncoder.Decode(jwt.Split('.')[1]);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static async Task<NpgsqlConnection> Open(string cs)
    {
        var c = new NpgsqlConnection(cs);
        await c.OpenAsync();
        return c;
    }

    static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    static string Clip(string body) => body.Length <= 120 ? body : body[..120];

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Admin.Api", "AdminApiHost.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static int FreeBase()
    {
        for (var port = 18300; port < 18480; port += 3)
        {
            if (CanBind(port) && CanBind(port + 1) && CanBind(port + 2)) return port;
        }
        throw new InvalidOperationException("no free stand-in ports");
    }

    static bool CanBind(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
