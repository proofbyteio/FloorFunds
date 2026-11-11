using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

/// <summary>
/// REQ-064 gate. Names map to the security note: T4, T5, T6, T7, T9, T10, T11, T12,
/// plus cross-partner deny, no-assignment deny, the two-partner union, and the last-active Super Admin guard.
/// </summary>
static class Req064Suite
{
    const string Issuer = "https://req064.test.invalid";
    static readonly Guid Pa = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000011");
    static readonly Guid Pb = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000012");
    static readonly Guid La = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000021");
    static readonly Guid Lb = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000022");
    static readonly Guid Sa = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000031");
    static readonly Guid Sa2 = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000032");
    static readonly Guid Upa = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000033");
    static readonly Guid Both = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000034");
    static readonly Guid None = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000035");
    static readonly Guid Cash = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000036");
    static readonly Guid Mix = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000038");
    static readonly Guid Asa = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a1");
    static readonly Guid Asa2 = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a2");
    static readonly Guid Apa = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a3");
    static readonly Guid AbothA = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a4");
    static readonly Guid AbothB = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a5");
    static readonly Guid Acash = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a6");
    static readonly Guid AmixA = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a8");
    static readonly Guid AmixB = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a9");
    static readonly Guid Fee = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000041");

    static int _pass, _fail;

    public static async Task<int> RunAsync(string ownerCs, string adminCs)
    {
        await using var owner = NpgsqlDataSource.Create(ownerCs);
        await using var admin = NpgsqlDataSource.Create(adminCs);
        await Cleanup(owner);
        var others = await ScalarLong(owner, """
            SELECT count(DISTINCT a.user_id) FROM auth.admin_assignment a
            JOIN auth.app_user u ON u.user_id = a.user_id
            WHERE a.role_id = 'super_admin' AND a.scope_kind = 'global' AND a.status = 'active' AND a.revoked_at IS NULL
              AND u.status = 'active' AND (u.locked_until IS NULL OR u.locked_until <= now())
            """);
        if (others != 0)
        {
            Console.Error.WriteLine("refuse: req064 checks need no other active global super admin");
            return 2;
        }
        using var rsa = RSA.Create(2048);
        var sign = new RsaSecurityKey(rsa) { KeyId = "req064" };
        var pub = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "req064" };
        var schema = Path.Combine(AppContext.BaseDirectory, "contract", "audit-events.schema.json");
        var issuerCs = Environment.GetEnvironmentVariable("ISSUER_CONNECTION_STRING");
        if (string.IsNullOrEmpty(issuerCs)) throw new InvalidOperationException("ISSUER_CONNECTION_STRING is not set");
        await using var issuerProc = await Admin.Api.Checks.IssuerHarness.Start(pub, Issuer, issuerCs);
        var opt = new Admin.Api.AdminApiOptions
        {
            ConnectionString = adminCs,
            EntraAuthority = "https://login.microsoftonline.com",
            EntraClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc",
            EntraSecretRef = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/req064-AbCdEf",
            SecretAccountIds = new HashSet<string> { "000000000000" },
            SecretRegions = new HashSet<string> { "us-west-2" },
            TokenIssuer = Issuer,
            IssuerUrl = issuerProc.Url,
            SigningKeys = [],
            AuditSchemaPath = schema,
        }.Validated();
        try
        {
            await Seed(owner);
            Check("location-rls-partner-predicate-hides-other-partner", await PartnerPredicate(admin));
            var port = FreePort();
            await using var app = await Admin.Api.AdminApiHost.BuildAsync(opt with { Urls = $"http://127.0.0.1:{port}" });
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var saTok = Mint(sign, Sa, "super", null);
            var paTok = Mint(sign, Upa, "super", null);
            var bothTok = Mint(sign, Both, "partner", Pa);
            var noneTok = Mint(sign, None, "super", null);
            var cashTok = Mint(sign, Cash, "partner", Pa);
            var mixTok = Mint(sign, Mix, "super", null);
            var same = await IssuerSession(issuerProc.Url, paTok);
            var again = await IssuerSession(issuerProc.Url, paTok);
            Check("reused-jwt-returns-the-same-session", same.Length == 64 && same == again);
            Check("reused-jti-rejected-by-a-second-open", await ReusedJtiRejected(paTok));
            using (var other = RSA.Create(2048))
            {
                var forgedJwt = Mint(new RsaSecurityKey(other) { KeyId = "req064" }, Upa, "partner", Pa);
                Check("forged-jwt-rejected", await IssuerStatus(issuerProc.Url, forgedJwt) == 401);
            }
            var expiredClaims = Claims(Upa, "partner", Pa);
            expiredClaims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
            expiredClaims["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
            var wrongAud = Claims(Upa, "partner", Pa);
            wrongAud["aud"] = "login-api";
            var wrongIss = Claims(Upa, "partner", Pa);
            wrongIss["iss"] = "https://evil.invalid";
            string Signed(Dictionary<string, object> claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Claims = claims,
                SigningCredentials = new SigningCredentials(sign, SecurityAlgorithms.RsaSha256),
                TokenType = "at+jwt",
            });
            Check("expired-jwt-rejected", await IssuerStatus(issuerProc.Url, Signed(expiredClaims)) == 401);
            Check("wrong-aud-jwt-rejected", await IssuerStatus(issuerProc.Url, Signed(wrongAud)) == 401);
            Check("wrong-iss-jwt-rejected", await IssuerStatus(issuerProc.Url, Signed(wrongIss)) == 401);

            var navNone = await Send(http, HttpMethod.Get, "/api/nav", noneTok);
            var locNone = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", noneTok);
            Check("T4 forged-super-without-assignment-403-empty-nav",
                navNone.Status == 200 && Screens(navNone.Body).Count == 0 && locNone.Status == 403,
                $"nav {navNone.Status} {Clip(navNone.Body)} loc {locNone.Status} {Clip(locNone.Body)}");

            var navSa = await Send(http, HttpMethod.Get, "/api/nav", saTok);
            var screens = Screens(navSa.Body);
            var held = await Send(http, HttpMethod.Get, "/api/screens/create-client", saTok);
            var write = await Send(http, HttpMethod.Post, "/api/screens/create-client", saTok);
            var seeB = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", saTok);
            Check("T5 explicit-global-sa-sees-private-and-create-client-nav",
                navSa.Status == 200 && screens.Contains("create_client") && screens.Contains("create_location") && screens.Contains("config")
                && held.Status == 200 && held.Body.Contains("\"write\":\"held\"", StringComparison.Ordinal)
                && write.Status == 403 && write.Body.Contains("\"error\":\"client_create_pending_pq278\"", StringComparison.Ordinal) && seeB.Status == 200,
                $"nav {navSa.Status} {Clip(navSa.Body)} held {held.Status} write {write.Status} seeB {seeB.Status} {Clip(seeB.Body)}");

            var before = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1", Apa);
            var after = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = NULL WHERE assignment_id = $1", Apa);
            paTok = Mint(sign, Upa, "super", null);
            Check("T6 revoke-takes-effect-on-next-request", before.Status == 200 && after.Status == 403,
                $"before {before.Status} {Clip(before.Body)} after {after.Status} {Clip(after.Body)}");
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1", Apa);
            var freshRevoked = Mint(sign, Upa, "super", null);
            var stillDenied = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", freshRevoked);
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = NULL WHERE assignment_id = $1", Apa);
            paTok = Mint(sign, Upa, "super", null);
            Check("revoked-assignment-denies-a-new-session", stillDenied.Status == 403, $"status {stillDenied.Status}");

            var navCash = await Send(http, HttpMethod.Get, "/api/nav", cashTok);
            var cashLoc = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", cashTok);
            var cashScreen = await Send(http, HttpMethod.Get, "/api/screens/create-client", cashTok);
            Check("T7 cashier-empty-nav-every-endpoint-403",
                navCash.Status == 200 && Screens(navCash.Body).Count == 0 && cashLoc.Status == 403 && cashScreen.Status == 403);

            var hidden = await Send(http, HttpMethod.Get, "/api/screens/create-client", paTok);
            var hiddenPost = await Send(http, HttpMethod.Post, "/api/screens/create-client", paTok);
            Check("T9 hidden-create-client-direct-403", hidden.Status == 403 && hiddenPost.Status == 403
                && hiddenPost.Body.Contains("\"error\":\"client_create_pending_pq278\"", StringComparison.Ordinal));

            var forged = new HttpRequestMessage(HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config?partner_id={Pb}&admin_type=super");
            forged.Headers.TryAddWithoutValidation("Authorization", "Bearer " + paTok);
            forged.Headers.TryAddWithoutValidation("X-Partner-Id", Pb.ToString("D"));
            var forgedRes = await Read(await http.SendAsync(forged));
            var own = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config?partner_id={Pb}", paTok);
            Check("T10 forged-partner-id-ignored", forgedRes.Status == 404 && own.Status == 200);

            var mixA = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", mixTok);
            var mixB = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", mixTok);
            var mixNav = await Send(http, HttpMethod.Get, "/api/nav", mixTok);
            var mixClient = await Send(http, HttpMethod.Get, "/api/screens/create-client", mixTok);
            var mixGrant = await ScalarBoolAs(admin, Mix, """
                SELECT outcome = 'grant' AND role_wire = 'partner_admin' AND NOT global_admin
                   AND partner_ids = ARRAY[$3]::uuid[] AND NOT ($4 = ANY (partner_ids))
                  FROM auth.resolve_grant($1, $2)
                """, Issuer, Mix, Pa, Pb);
            var mixScreens = mixNav.Status == 200 ? Screens(mixNav.Body) : new List<string>();
            Check("pa-at-a-cashier-at-b-keeps-each-role",
                mixGrant && mixA.Status == 200 && mixB.Status == 404 && mixClient.Status == 403
                && mixScreens.Contains("create_location") && mixScreens.Contains("config") && !mixScreens.Contains("create_client"),
                $"grant {mixGrant} A {mixA.Status} B {mixB.Status} nav {mixNav.Status} {Clip(mixNav.Body)} client {mixClient.Status}");
            Check("history-label-uses-the-record-partner", await HistoryLabel(owner, admin));
            var wrongIssuer = await OpenDenied("https://not-the-issuer.example", Mix);
            var ignoredArgs = await ScalarBoolAs(admin, Mix,
                "SELECT outcome = 'grant' AND role_wire = 'partner_admin' AND partner_ids = ARRAY[$3]::uuid[] FROM auth.resolve_grant($1, $2)",
                "https://not-the-issuer.example", Mix, Pa);
            var unbound = await ScalarBool(admin,
                "SELECT outcome = 'deny' AND role_wire = 'none' FROM auth.resolve_grant($1, $2)",
                Issuer, Mix);
            Check("wrong-issuer-does-not-resolve", wrongIssuer && ignoredArgs && unbound, $"open {wrongIssuer} ignored {ignoredArgs} unbound {unbound}");
            Check("login-write-records-the-request-principal", await LoginWritePrincipal(admin, owner));
            Check("token-admin-type-does-not-choose-the-principal", await TokenPrincipalRowCount(http, admin, owner, paTok));
            var bareRows = await ScalarLong(admin, """
                SELECT count(*) FROM auth.location
                """);
            // The count above is outside a transaction, so it has no principal.
            var forgedFlags = await ScalarInTx(admin, """
                SELECT set_config('app.request_iss', '', true),
                       set_config('app.request_sub', '', true),
                       set_config('app.resolved_global_admin', 'on', true),
                       set_config('app.resolved_partner_ids', '5f0c3e1a-0064-4000-8000-000000000011,5f0c3e1a-0064-4000-8000-000000000012', true),
                       set_config('app.resolved_location_ids', '5f0c3e1a-0064-4000-8000-000000000021,5f0c3e1a-0064-4000-8000-000000000022', true),
                       set_config('app.admin_type', 'super', true)
                """, "SELECT count(*) FROM auth.location");
            var noAssignment = await ScalarInTx(admin, """
                SELECT set_config('app.request_iss', 'https://req064.test.invalid', true),
                       set_config('app.request_sub', '5f0c3e1a-0064-4000-8000-000000000035', true),
                       set_config('app.resolved_global_admin', 'on', true),
                       set_config('app.admin_type', 'super', true)
                """, "SELECT count(*) FROM auth.location");
            Check("flags-without-a-principal-see-nothing", bareRows == 0 && forgedFlags == 0 && noAssignment == 0,
                $"bare {bareRows} flags {forgedFlags} none {noAssignment}");
            Check("reason-bounds-read-stays-inside-the-actor-partner", await BoundsRead(admin, owner));
            await Dec111BoundsWrite(http, admin, owner, paTok);
            Check("location-only-assignment-sees-that-location", await LocationOnly(admin, owner));

            _ = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            _ = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", saTok);
            var leaked = await PoolSample(app);
            Check("T11 pooled-connection-does-not-inherit-guc", leaked.Count == 0, string.Join(",", leaked));
            Check("reused-connection-does-not-keep-a-principal", await PoolResetsPrincipal(app));

            var forgedCount = await ScalarInTx(admin,
                "SELECT set_config('app.admin_type','super', true), set_config('app.resolved_global_admin','', true), set_config('app.resolved_partner_ids','', true)",
                "SELECT (SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1) + (SELECT count(*) FROM catalog.entry_change WHERE entry_id = $1)",
                Fee);
            var resolvedCount = await ScalarInTx(admin,
                "SELECT set_config('app.resolved_global_admin','on', true)",
                "SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1", Fee);
            var principalCount = await CountAs(admin, Sa, "SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1", Fee);
            Check("T12 flag-does-not-grant-and-principal-does", forgedCount == 0 && resolvedCount == 0 && principalCount == 1,
                $"{forgedCount},{resolvedCount},{principalCount}");

            var cross = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", paTok);
            Check("cross-partner-deny", cross.Status == 404);
            Check("no-assignment-deny", locNone.Status == 403);
            var bothA = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", bothTok);
            var bothB = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", bothTok);
            Check("two-partner-union", bothA.Status == 200 && bothB.Status == 200);

            var seq0 = await ScalarLong(owner, "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM auth.resolver_read_seq");
            _ = await Send(http, HttpMethod.Get, "/api/nav", saTok);
            var seq1 = await ScalarLong(owner, "SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM auth.resolver_read_seq");
            Check("resolver-store-is-read", seq1 == seq0 + 1, $"{seq0}->{seq1}");

            Check("unknown-kid-401", (await Send(http, HttpMethod.Get, "/api/nav", Sign(rsa, "not-a-key", Payload(Sa, "super", null)))).Status == 401);
            Check("missing-kid-401", (await Send(http, HttpMethod.Get, "/api/nav", Sign(rsa, null, Payload(Sa, "super", null)))).Status == 401);
            var dup = Payload(Sa, "super", null).Replace("\"sub\":", "\"sub\":\"" + Sa2.ToString("D") + "\",\"sub\":", StringComparison.Ordinal);
            Check("duplicate-sub-401", (await Send(http, HttpMethod.Get, "/api/nav", Sign(rsa, "req064", dup))).Status == 401);

            await GuardPaths(owner);
            await Concurrency(owner);
            await SessionProofs(owner, admin);
            var extraUser = await ExecOk(owner,
                "INSERT INTO auth.app_user (user_id, partner_id, status) VALUES ('5f0c3e1a-0064-4000-8000-000000000037', NULL, 'active')");
            var extra = extraUser && await ExecOk(owner, """
                INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, status, version, granted_by, granted_at)
                VALUES ('5f0c3e1a-0064-4000-8000-0000000000a7', '5f0c3e1a-0064-4000-8000-000000000037', 'super_admin', 'global', 'active', 1, $1, now())
                """, Sa);
            await Exec(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = '5f0c3e1a-0064-4000-8000-0000000000a7'");
            await Exec(owner, "DELETE FROM auth.app_user WHERE user_id = '5f0c3e1a-0064-4000-8000-000000000037'");
            Check("insert-another-super-admin-succeeds", extra);

            var principal = Admin.Api.AssignmentPrincipal.TryFromGrant(Sa, new Admin.Api.AdminGrant(Admin.Api.BuiltInRole.SuperAdmin, true, true, new HashSet<Guid>(), true));
            var denied = Admin.Api.AssignmentPrincipal.TryFromGrant(None, Admin.Api.AdminGrant.Nothing);
            Check("catalog-principal-from-resolved-grant",
                principal is { Role: Catalog.Contracts.CatalogRole.SuperAdmin, PartnerId: null } && denied is null);
            var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AdminApiHost.cs"));
            Check("no-revoke-or-assign-api",
                !host.Contains("/api/assign", StringComparison.Ordinal) && !host.Contains("/api/revoke", StringComparison.Ordinal)
                && !host.Contains("MapPost(\"/api/admin", StringComparison.Ordinal));
            var options = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AdminApiOptions.cs"));
            var auth = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AdminAuth.cs"));
            var api = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "LoginConfigApi.cs"));
            Check("host-does-not-read-issuer-credentials",
                !host.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !options.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !auth.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !api.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !api.Contains("FF_ISSUER_PASSWORD", StringComparison.Ordinal)
                && !api.Contains("set_config('app.request_iss'", StringComparison.Ordinal));

            await app.StopAsync();
        }
        finally
        {
            await Cleanup(owner);
        }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    static async Task SessionProofs(NpgsqlDataSource owner, NpgsqlDataSource admin)
    {
        var unknown = new string('a', 64);
        Check("unknown-token-denied", await Denied(admin, "SELECT auth.begin_request($1)", unknown));
        Check("truncated-token-denied", await Denied(admin, "SELECT auth.begin_request($1)", unknown[..16]));
        Check("forged-guc-does-not-grant", await ForgedGuc(admin));

        var expiredOpen = await OpenDeniedExp();
        Check("expired-token-rejected-at-open", expiredOpen);

        string? minted = null;
        var capped = "threw";
        try
        {
            minted = await Mint(Upa, exp: DateTimeOffset.UtcNow.AddHours(2));
            capped = await ScalarText(owner, """
                SELECT CASE WHEN expires_at <= created_at + interval '15 minutes'
                             AND expires_at > created_at + interval '14 minutes'
                            THEN 'capped' ELSE 'open' END
                  FROM auth.request_session
                 WHERE token_hash = sha256(convert_to($1, 'UTF8'))
                """, minted);
        }
        catch (PostgresException ex)
        {
            capped = ex.SqlState + " " + ex.MessageText;
        }
        Check("expiry-is-min-of-now-plus-15m-and-jwt-exp", capped == "capped", capped);
        var token = minted ?? await Mint(Upa);
        Check("admin-api-cannot-open-session", await Denied(admin, "SELECT auth.open_session($1, $2, $3, $4)", Issuer, Upa.ToString("D"), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
        Check("ttl-over-15-minutes-rejected", await TtlRejected(owner));
        Check("thirty-third-live-session-rejected", await LiveCap(owner));
        Check("six-hundred-first-open-rejected", await OpenRate(owner));

        await using (var c = await admin.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using (var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx))
            {
                b.Parameters.Add(new() { Value = token });
                await b.ExecuteNonQueryAsync();
            }
            var rebound = await DeniedOn(c, tx, "SELECT auth.begin_request($1)", token);
            Check("double-begin-request-rejected", rebound);
            await tx.RollbackAsync();
        }

        await using (var c = await admin.OpenConnectionAsync())
        {
            await using (var tx = await c.BeginTransactionAsync())
            {
                await using var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx);
                b.Parameters.Add(new() { Value = token });
                await b.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }
            await using var next = await c.BeginTransactionAsync();
            var principal = await ScalarOn(c, next, "SELECT auth.current_principal() IS NULL");
            Check("binding-does-not-survive-commit", principal);
            await next.RollbackAsync();
        }

        await using (var c = await admin.OpenConnectionAsync())
        {
            await using (var tx = await c.BeginTransactionAsync())
            {
                await using var b = new NpgsqlCommand("""
                    SELECT auth.begin_request($1), query FROM pg_stat_activity WHERE pid = pg_backend_pid()
                    """, c, tx);
                b.Parameters.Add(new() { Value = token });
                await using var reader = await b.ExecuteReaderAsync();
                await reader.ReadAsync();
                var shown = reader.GetString(1);
                Check("begin-request-query-text-is-a-parameter", shown.Contains("$1", StringComparison.Ordinal) && !shown.Contains(token, StringComparison.Ordinal));
                var body = await ScalarText(owner, "SELECT pg_get_functiondef('auth.begin_request(text)'::regprocedure)");
                Check("begin-request-body-hashes-the-parameter", body.Contains("convert_to(p_token", StringComparison.Ordinal) && !body.Contains("format(", StringComparison.Ordinal));
                await reader.DisposeAsync();
                await tx.RollbackAsync();
            }
        }

        var revoked = await Mint(Sa2);
        await Exec(owner, "SELECT auth.revoke_session($1)", revoked);
        Check("revoked-session-denied-on-next-action", await Denied(admin, "SELECT auth.begin_request($1)", revoked));

        var live = await Mint(Upa);
        await Exec(owner, "UPDATE auth.admin_assignment SET status = 'disabled' WHERE user_id = $1 AND role_id = 'partner_admin'", Upa);
        var nextDenied = await Denied(admin, "SELECT auth.begin_request($1)", live);
        await Exec(owner, "UPDATE auth.admin_assignment SET status = 'active' WHERE user_id = $1 AND role_id = 'partner_admin'", Upa);
        Check("revocation-applies-on-the-next-action", nextDenied);
        Check("host-password-cannot-log-in-as-issuer", await HostPasswordRejected());
        var fresh = await Mint(Sa);
        Check("parallel-worker-does-not-drop-the-principal", await ParallelStillResolves(admin, fresh));
        Check("current-principal-is-parallel-restricted", await ScalarText(owner, "SELECT proparallel::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'auth' AND p.proname = 'current_principal'") == "r");
        Check("pooled-connection-keeps-no-binding", await PooledBindingClears(admin, fresh));
    }

    static async Task<bool> HostPasswordRejected()
    {
        var adminCs = Environment.GetEnvironmentVariable("ADMIN_API_CONNECTION_STRING");
        var issuerCs = Environment.GetEnvironmentVariable("ISSUER_CONNECTION_STRING");
        if (string.IsNullOrEmpty(adminCs) || string.IsNullOrEmpty(issuerCs)) return false;
        var admin = new NpgsqlConnectionStringBuilder(adminCs);
        var issuer = new NpgsqlConnectionStringBuilder(issuerCs);
        if (admin.Username == issuer.Username) return false;
        admin.Username = issuer.Username;
        try
        {
            await using var c = new NpgsqlConnection(admin.ConnectionString);
            await c.OpenAsync();
            return false;
        }
        catch (Exception) { return true; }
    }

    static async Task<bool> ParallelStillResolves(NpgsqlDataSource admin, string token)
    {
        await using var c = await admin.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx))
        {
            b.Parameters.Add(new() { Value = token });
            await b.ExecuteNonQueryAsync();
        }
        var armed = false;
        foreach (var guc in new[] { "SET LOCAL debug_parallel_query = on", "SET LOCAL force_parallel_mode = on" })
        {
            await using (var sp = new NpgsqlCommand("SAVEPOINT parallel_guc", c, tx))
                await sp.ExecuteNonQueryAsync();
            try
            {
                await using var set = new NpgsqlCommand(guc, c, tx);
                await set.ExecuteNonQueryAsync();
                armed = true;
                break;
            }
            catch (PostgresException)
            {
                await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT parallel_guc", c, tx);
                await rb.ExecuteNonQueryAsync();
            }
        }
        await using var q = new NpgsqlCommand("SELECT auth.current_principal()", c, tx);
        var principal = await q.ExecuteScalarAsync();
        await tx.RollbackAsync();
        return armed && principal is Guid;
    }

    static async Task<bool> PooledBindingClears(NpgsqlDataSource admin, string token)
    {
        int pid;
        await using (var c = await admin.OpenConnectionAsync())
        {
            pid = Convert.ToInt32(await new NpgsqlCommand("SELECT pg_backend_pid()", c).ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await using var tx = await c.BeginTransactionAsync();
            await using var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx);
            b.Parameters.Add(new() { Value = token });
            await b.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            await using var discard = new NpgsqlCommand("DISCARD ALL", c);
            await discard.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < 8; i++)
        {
            await using var c = await admin.OpenConnectionAsync();
            var now = Convert.ToInt32(await new NpgsqlCommand("SELECT pg_backend_pid()", c).ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            if (now != pid) continue;
            await using var tx = await c.BeginTransactionAsync();
            var principal = await ScalarOn(c, tx, "SELECT auth.current_principal() IS NULL");
            await tx.RollbackAsync();
            return principal;
        }
        return false;
    }

    static async Task<bool> ForgedGuc(NpgsqlDataSource admin)
    {
        await using var c = await admin.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using var set = new NpgsqlCommand("SELECT set_config('app.request_iss', $1, true), set_config('app.request_sub', $2, true)", c, tx);
        set.Parameters.Add(new() { Value = Issuer });
        set.Parameters.Add(new() { Value = Upa.ToString("D") });
        await set.ExecuteNonQueryAsync();
        await using var q = new NpgsqlCommand("SELECT count(*) FROM auth.location WHERE location_id = $1", c, tx);
        q.Parameters.Add(new() { Value = La });
        var n = Convert.ToInt64(await q.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        await tx.RollbackAsync();
        return n == 0;
    }

    static async Task<bool> OpenDeniedExp()
    {
        try { await Mint(Upa, exp: DateTimeOffset.UtcNow.AddMinutes(-1)); return false; }
        catch (PostgresException ex) when (ex.SqlState == "42501") { return true; }
    }

    static async Task<bool> TtlRejected(NpgsqlDataSource owner)
    {
        try
        {
            await Exec(owner, """
                INSERT INTO auth.request_session (token_hash, jti, iss, sub, user_id, created_at, expires_at)
                VALUES (sha256(convert_to('ttl-over', 'UTF8')), gen_random_uuid(), $2, $3, $1, clock_timestamp(), clock_timestamp() + interval '16 minutes')
                """, Upa, Issuer, Upa.ToString("D"));
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == "23514") { return true; }
    }

    static async Task<bool> LiveCap(NpgsqlDataSource owner)
    {
        await Exec(owner, "DELETE FROM auth.request_binding WHERE user_id = $1", Sa2);
        await Exec(owner, "DELETE FROM auth.request_session WHERE user_id = $1", Sa2);
        var opened = 0;
        try
        {
            for (var i = 0; i < 40; i++)
            {
                await Mint(Sa2);
                opened++;
            }
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == "54000" && ex.MessageText == "session_cap")
        {
            return opened == 32;
        }
        finally
        {
            await Exec(owner, "DELETE FROM auth.request_binding WHERE user_id = $1", Sa2);
            await Exec(owner, "DELETE FROM auth.request_session WHERE user_id = $1", Sa2);
        }
    }

    static async Task<bool> OpenRate(NpgsqlDataSource owner)
    {
        await Exec(owner, """
            INSERT INTO auth.request_session (token_hash, jti, iss, sub, user_id, created_at, expires_at, revoked_at)
            SELECT sha256(convert_to('rate-' || g::text, 'UTF8')), gen_random_uuid(), $2, $3, $1,
                   clock_timestamp(), clock_timestamp() + interval '5 minutes', clock_timestamp()
              FROM generate_series(1, 600) g
            """, Sa2, Issuer, Sa2.ToString("D"));
        try
        {
            await Mint(Sa2);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == "54000" && ex.MessageText == "session_rate")
        {
            return true;
        }
        finally
        {
            await Exec(owner, "DELETE FROM auth.request_session WHERE user_id = $1 AND revoked_at IS NOT NULL", Sa2);
        }
    }

    static async Task<bool> Denied(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        try { await Exec(ds, sql, ps); return false; }
        catch (PostgresException ex) when (ex.SqlState == "42501") { return true; }
    }

    static async Task<bool> DeniedOn(NpgsqlConnection c, NpgsqlTransaction tx, string sql, params object?[] ps)
    {
        await using var sp = new NpgsqlCommand("SAVEPOINT denied_probe", c, tx);
        await sp.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync();
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState is "42501" or "25001")
        {
            await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT denied_probe", c, tx);
            await rb.ExecuteNonQueryAsync();
            return true;
        }
    }

    static async Task<bool> ScalarOn(NpgsqlConnection c, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        return await cmd.ExecuteScalarAsync() is true;
    }

    static async Task<string> ScalarTextOn(NpgsqlConnection c, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    static async Task GuardPaths(NpgsqlDataSource owner)
    {
        Check("last-active-sa-revoke", await FailsLast(owner, "UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1", Asa));
        Check("last-active-sa-assignment-disable", await FailsLast(owner, "UPDATE auth.admin_assignment SET status = 'disabled' WHERE assignment_id = $1", Asa));
        Check("last-active-sa-disable", await FailsLast(owner, "UPDATE auth.app_user SET status = 'disabled' WHERE user_id = $1", Sa));
        Check("last-active-sa-lock", await FailsLast(owner, "UPDATE auth.app_user SET locked_until = now() + interval '1 hour' WHERE user_id = $1", Sa));
        Check("last-active-sa-scope-change", await FailsLast(owner,
            "UPDATE auth.admin_assignment SET role_id = 'cashier', scope_kind = 'partner', partner_id = '5f0c3e1a-0064-4000-8000-000000000011' WHERE assignment_id = $1", Asa));
        Check("last-active-sa-role-change", await FailsLast(owner,
            "UPDATE auth.admin_assignment SET role_id = 'partner_admin', scope_kind = 'partner', partner_id = '5f0c3e1a-0064-4000-8000-000000000011' WHERE assignment_id = $1", Asa));
        Check("last-active-sa-delete", await FailsLast(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", Asa));
        Check("last-active-sa-user-delete", await FailsLast(owner, "DELETE FROM auth.app_user WHERE user_id = $1", Sa));
        Check("last-active-sa-truncate", await FailsStatement(owner, "TRUNCATE auth.admin_assignment"));
        Check("last-active-sa-user-truncate", await FailsStatement(owner, "TRUNCATE auth.app_user CASCADE"));
        Check("last-active-sa-truncate-guard", await TruncateGuard(owner));
        var disabledOne = await ExecOk(owner, "UPDATE auth.admin_assignment SET status = 'disabled' WHERE assignment_id = $1", Asa2);
        if (disabledOne)
            await Exec(owner, "UPDATE auth.admin_assignment SET status = 'active' WHERE assignment_id = $1", Asa2);
        Check("disable-one-of-two-super-admins-succeeds", disabledOne);
        var paDelete = await ExecOk(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", Apa);
        if (paDelete)
            await Exec(owner, """
                INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, partner_id, status, version, granted_by, granted_at)
                VALUES ($1, $2, 'partner_admin', 'partner', $3, 'active', 1, $4, now())
                """, Apa, Upa, Pa, Sa);
        Check("delete-partner-admin-while-sa-remains", paDelete);
    }

    static async Task<bool> PartnerPredicate(NpgsqlDataSource admin)
    {
        await using var c = await admin.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Begin(c, tx, Upa);
        await using (var set = new NpgsqlCommand("""
            SELECT set_config('app.resolved_global_admin', 'on', true),
                   set_config('app.resolved_partner_ids', $1, true),
                   set_config('app.resolved_location_ids', $2, true),
                   set_config('app.admin_type', 'super', true),
                   set_config('app.partner_id', $1, true)
            """, c, tx))
        {
            set.Parameters.Add(new() { Value = Pb.ToString("D") });
            set.Parameters.Add(new() { Value = Lb.ToString("D") });
            await set.ExecuteNonQueryAsync();
        }
        async Task<long> Count(Guid id)
        {
            await using var q = new NpgsqlCommand("SELECT count(*) FROM auth.location WHERE location_id = $1", c, tx);
            q.Parameters.Add(new() { Value = id });
            return (long)(await q.ExecuteScalarAsync())!;
        }
        var own = await Count(La);
        var other = await Count(Lb);
        await tx.RollbackAsync();
        return own == 1 && other == 0;
    }

    static async Task<bool> TruncateGuard(NpgsqlDataSource owner)
    {
        // BEFORE TRUNCATE is tgtype bit 5 (32). The row guard does not fire for TRUNCATE.
        var triggers = await ScalarLong(owner, """
            SELECT count(*)
              FROM pg_trigger t
              JOIN pg_proc p ON p.oid = t.tgfoid
              JOIN pg_class c ON c.oid = t.tgrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'auth'
               AND p.proname = '_last_sa_truncate_guard'
               AND NOT t.tgisinternal
               AND (t.tgtype & 32) <> 0
               AND c.relname IN ('admin_assignment', 'app_user')
            """);
        return triggers == 2
            && await FailsStatement(owner, "TRUNCATE auth.admin_assignment")
            && await FailsStatement(owner, "TRUNCATE auth.app_user CASCADE");
    }

    static async Task<bool> FailsStatement(NpgsqlDataSource owner, string sql)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            await cmd.ExecuteNonQueryAsync();
            return false;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState == "P0001" && ex.MessageText == "last_active_super_admin";
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    static async Task<bool> FailsLast(NpgsqlDataSource owner, string sql, Guid id)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using (var other = new NpgsqlCommand("UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1", c, tx))
            {
                other.Parameters.Add(new() { Value = Asa2 });
                if (await other.ExecuteNonQueryAsync() != 1) return false;
            }
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.Add(new() { Value = id });
            await cmd.ExecuteNonQueryAsync();
            return false;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState == "P0001" && ex.MessageText == "last_active_super_admin";
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    static async Task Concurrency(NpgsqlDataSource owner)
    {
        // Session 1 stays inside the guarded transaction after its revoke has passed the
        // check. Session 2 then revokes the other Super Admin. The advisory lock makes
        // session 2 wait, so it sees the committed revoke and refuses. Without the lock
        // both revokes pass.
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> First()
        {
            await using var c = await owner.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            try
            {
                await using var cmd = new NpgsqlCommand("UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1 AND revoked_at IS NULL", c, tx);
                cmd.Parameters.Add(new() { Value = Asa });
                var n = await cmd.ExecuteNonQueryAsync();
                holding.TrySetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                await tx.CommitAsync();
                return n == 1 ? "ok" : "miss";
            }
            catch (PostgresException ex)
            {
                holding.TrySetResult();
                return ex.SqlState + ":" + ex.MessageText;
            }
        }
        async Task<string> Second()
        {
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var c = await owner.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            try
            {
                await using var cmd = new NpgsqlCommand("UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1 AND revoked_at IS NULL", c, tx);
                cmd.Parameters.Add(new() { Value = Asa2 });
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var n = await cmd.ExecuteNonQueryAsync(cancel.Token);
                await tx.CommitAsync(cancel.Token);
                return n == 1 ? "ok" : "miss";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState + ":" + ex.MessageText;
            }
        }
        var results = await Task.WhenAll(First(), Second());
        var left = await ScalarLong(owner, """
            SELECT count(*) FROM auth.admin_assignment a
            JOIN auth.app_user u ON u.user_id = a.user_id
            WHERE a.role_id = 'super_admin' AND a.scope_kind = 'global' AND a.status = 'active' AND a.revoked_at IS NULL
              AND u.status = 'active'
            """);
        var oks = results.Count(r => r == "ok");
        var fails = results.Count(r => r == "P0001:last_active_super_admin");
        Check("last-active-sa-concurrency", oks == 1 && fails == 1 && left >= 1, string.Join(" | ", results) + $" left {left}");
        await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = NULL WHERE assignment_id = ANY($1)", new[] { Asa, Asa2 });
    }

    static async Task<bool> HistoryLabel(NpgsqlDataSource owner, NpgsqlDataSource admin)
    {
        // Both is Partner Admin at Pa and at Pb. The actor list is ordered Pa then Pb,
        // so the old label (the first partner) would store Pa on a row owned by Pb.
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var hist = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000042");
        try
        {
            await using var grantConn = await admin.OpenConnectionAsync();
            await using var grantTx = await grantConn.BeginTransactionAsync();
            await Begin(grantConn, grantTx, Both);
            await using (var grant = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM unnest(partner_ids) x WHERE x = $3) = 1
                   AND (SELECT count(*) FROM unnest(partner_ids) x WHERE x = $4) = 1
                   AND role_wire = 'partner_admin' AND NOT global_admin
                  FROM auth.resolve_grant($1, $2)
                """, grantConn, grantTx))
            {
                grant.Parameters.Add(new() { Value = Issuer });
                grant.Parameters.Add(new() { Value = Both });
                grant.Parameters.Add(new() { Value = Pa });
                grant.Parameters.Add(new() { Value = Pb });
                if (await grant.ExecuteScalarAsync() is not true)
                {
                    Console.WriteLine("FAIL history-label: Both is not Partner Admin at both partners");
                    return false;
                }
            }
            await using (var guc = new NpgsqlCommand("""
                SELECT set_config('app.resolved_global_admin', '', true),
                       set_config('app.resolved_partner_ids', $1 || ',' || $2, true),
                       set_config('app.resolved_location_ids', '', true),
                       set_config('app.actor_user_id', $3, true),
                       set_config('app.correlation_id', 'req064-history-label', true),
                       set_config('app.reason', 'history label', true),
                       set_config('app.admin_type', '', true),
                       set_config('app.partner_id', '', true)
                """, c, tx))
            {
                guc.Parameters.Add(new() { Value = Pa.ToString("D") });
                guc.Parameters.Add(new() { Value = Pb.ToString("D") });
                guc.Parameters.Add(new() { Value = Both.ToString("D") });
                await guc.ExecuteNonQueryAsync();
            }
            await using (var fn = new NpgsqlCommand("""
                SELECT catalog.history_partner($1, ARRAY[$2, $3]::uuid[], false) = $1
                   AND catalog.history_partner($2, ARRAY[$3, $2]::uuid[], false) = $2
                   AND catalog.history_partner(NULL, ARRAY[$2]::uuid[], false) = $2
                   AND catalog.history_partner(NULL, ARRAY[$2, $3]::uuid[], true) IS NULL
                """, c, tx))
            {
                fn.Parameters.Add(new() { Value = Pb });
                fn.Parameters.Add(new() { Value = Pa });
                fn.Parameters.Add(new() { Value = Pb });
                if (await fn.ExecuteScalarAsync() is not true)
                {
                    Console.WriteLine("FAIL history-label: history_partner did not follow the record");
                    return false;
                }
            }
            await using (var entry = new NpgsqlCommand("""
                INSERT INTO catalog.registered_entry (
                  entry_id, family, entry_class, owner_partner_id, code, is_active, row_version,
                  created_by, created_at, updated_by, updated_at)
                VALUES ($1, 'fee_type', 'descriptive', $2, 'req064hist', true, 1, $3, clock_timestamp(), $3, clock_timestamp())
                """, c, tx))
            {
                entry.Parameters.Add(new() { Value = hist });
                entry.Parameters.Add(new() { Value = Pb });
                entry.Parameters.Add(new() { Value = Both });
                await entry.ExecuteNonQueryAsync();
            }
            await using var label = new NpgsqlCommand(
                "SELECT actor_partner_id FROM catalog.history_pending WHERE entry_id = $1", c, tx);
            label.Parameters.Add(new() { Value = hist });
            var stored = await label.ExecuteScalarAsync();
            if (stored is not Guid partner || partner != Pb)
            {
                Console.WriteLine($"FAIL history-label: stored {stored}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL history-label :: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            try { await tx.RollbackAsync(); } catch (ObjectDisposedException) { }
        }
    }

    static async Task Seed(NpgsqlDataSource owner)
    {
        await Exec(owner, """
            INSERT INTO auth.app_user (user_id, partner_id, status) VALUES
              ($1, NULL, 'active'), ($2, NULL, 'active'), ($3, NULL, 'active'),
              ($4, NULL, 'active'), ($5, NULL, 'active'), ($6, NULL, 'active'),
              ($7, NULL, 'active')
            """, Sa, Sa2, Upa, Both, None, Cash, Mix);
        await Exec(owner, """
            INSERT INTO auth.user_issuer (user_id, issuer)
            SELECT x, $1 FROM unnest($2::uuid[]) AS x
            ON CONFLICT (user_id) DO UPDATE SET issuer = EXCLUDED.issuer
            """, Issuer, new[] { Sa, Sa2, Upa, Both, None, Cash, Mix });
        await Exec(owner, """
            INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, partner_id, status, version, granted_by, granted_at) VALUES
              ($1, $2, 'super_admin', 'global', NULL, 'active', 1, $2, now()),
              ($3, $4, 'super_admin', 'global', NULL, 'active', 1, $2, now()),
              ($5, $6, 'partner_admin', 'partner', $7, 'active', 1, $2, now()),
              ($8, $9, 'partner_admin', 'partner', $7, 'active', 1, $2, now()),
              ($10, $9, 'partner_admin', 'partner', $11, 'active', 1, $2, now()),
              ($12, $13, 'cashier', 'partner', $7, 'active', 1, $2, now()),
              ($14, $15, 'partner_admin', 'partner', $7, 'active', 1, $2, now()),
              ($16, $15, 'cashier', 'partner', $11, 'active', 1, $2, now())
            """, Asa, Sa, Asa2, Sa2, Apa, Upa, Pa, AbothA, Both, AbothB, Pb, Acash, Cash, AmixA, Mix, AmixB);
        await Exec(owner, "INSERT INTO auth.location VALUES ($1,$2,'req064-a','Req064 A'),($3,$4,'req064-b','Req064 B')", La, Pa, Lb, Pb);
        await Exec(owner, """
            INSERT INTO auth.location_login_config (location_id, provider_kind, display_name, entra_allowed_tenant_ids, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at)
            VALUES ($1,'database','A','{}',false,false,$3,now(),$3,now()),
                   ($2,'database','B','{}',false,false,$3,now(),$3,now())
            """, La, Lb, Sa);
        if (await ScalarLong(owner, "SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1", Fee) > 0)
            return;
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var guc = new NpgsqlCommand("""
            SELECT set_config('app.resolved_global_admin','on', true),
                   set_config('app.actor_user_id', $1, true),
                   set_config('app.correlation_id', 'req064-fixture', true),
                   set_config('app.reason', 'req064 fixture', true)
            """, c, tx))
        {
            guc.Parameters.Add(new() { Value = Sa.ToString("D") });
            await guc.ExecuteNonQueryAsync();
        }
        await using (var entry = new NpgsqlCommand("""
            INSERT INTO catalog.registered_entry (
              entry_id, family, entry_class, owner_partner_id, code, is_active, row_version,
              created_by, created_at, updated_by, updated_at)
            VALUES ($1, 'fee_type', 'descriptive', $2, 'req064fee', true, 1, $3, clock_timestamp(), $3, clock_timestamp())
            """, c, tx))
        {
            entry.Parameters.Add(new() { Value = Fee });
            entry.Parameters.Add(new() { Value = Pb });
            entry.Parameters.Add(new() { Value = Sa });
            await entry.ExecuteNonQueryAsync();
        }
        await using (var fee = new NpgsqlCommand("""
            INSERT INTO catalog.fee_type (entry_id, family, code, owner_partner_id, definition, display_name)
            VALUES ($1, 'fee_type', 'req064fee', $2, 'names only fixture', 'req064')
            """, c, tx))
        {
            fee.Parameters.Add(new() { Value = Fee });
            fee.Parameters.Add(new() { Value = Pb });
            await fee.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    static async Task<bool> LocationOnly(NpgsqlDataSource admin, NpgsqlDataSource owner)
    {
        var sibling = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000023");
        var grant = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000ab");
        try
        {
            await Exec(owner, "INSERT INTO auth.location VALUES ($1,$2,'req064-a2','Req064 A2')", sibling, Pa);
            await Exec(owner, """
                INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, location_id, status, version, granted_by, granted_at)
                VALUES ($1, $2, 'cashier', 'location', $3, 'active', 1, $4, now())
                """, grant, None, La, Sa);
            var own = await CountAs(admin, None, "SELECT count(*) FROM auth.location WHERE location_id = $1", La);
            var sib = await CountAs(admin, None, "SELECT count(*) FROM auth.location WHERE location_id = $1", sibling);
            var other = await CountAs(admin, None, "SELECT count(*) FROM auth.location WHERE location_id = $1", Lb);
            var ok = own == 1 && sib == 0 && other == 0;
            if (!ok) Console.WriteLine($"FAIL location-only: own {own} sibling {sib} other {other}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL location-only: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", grant);
            await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = $1", sibling);
        }
    }

    static async Task Dec111BoundsWrite(HttpClient http, NpgsqlDataSource admin, NpgsqlDataSource owner, string paTok)
    {
        var locGrant = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000ac");
        try
        {
            await Exec(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
            var none = await TrySaveBounds(admin, None, Pa);
            var noneRows = await ScalarLong(owner, "SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = $1", Pa);
            Check("dec-111-no-assignment-cannot-write-bounds", none.Denied && none.Version is null && noneRows == 0,
                $"denied {none.Denied} version {none.Version} rows {noneRows} {none.Detail}");
            await Exec(owner, """
                INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, location_id, status, version, granted_by, granted_at)
                VALUES ($1, $2, 'cashier', 'location', $3, 'active', 1, $4, now())
                """, locGrant, None, La, Sa);
            var loc = await TrySaveBounds(admin, None, Pa);
            var locB = await TrySaveBounds(admin, None, Pb);
            var locRows = await ScalarLong(owner, "SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
            Check("dec-111-location-only-cannot-write-bounds", loc.Denied && locB.Denied && locRows == 0,
                $"a {loc.Denied} {loc.Detail} b {locB.Denied} {locB.Detail} rows {locRows}");
            await Exec(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", locGrant);
            var flags = await TrySaveBounds(admin, null, Pa, flags: true);
            var flagRows = await ScalarLong(owner, "SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = $1", Pa);
            Check("dec-111-flags-without-principal-cannot-write-bounds", flags.Denied && flagRows == 0,
                $"denied {flags.Denied} version {flags.Version} rows {flagRows} {flags.Detail}");
            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/orgs/partners/{Pb:D}/login-config/reason-bounds");
            put.Headers.TryAddWithoutValidation("Authorization", "Bearer " + paTok);
            put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
            put.Content = new StringContent("""{"reason_min_length":12,"reason_max_length":24}""", Encoding.UTF8, "application/json");
            var wrote = await Read(await http.SendAsync(put));
            var stored = await ScalarLong(owner, """
                SELECT count(*) FROM auth.partner_reason_bounds
                 WHERE partner_id = $1 AND reason_min_length = 12 AND reason_max_length = 24
                """, Pb);
            Check("dec-111-pa-of-a-writes-partner-b", wrote.Status == 200 && stored == 1,
                $"put {wrote.Status} {Clip(wrote.Body)} stored {stored}");
        }
        catch (Exception ex)
        {
            Check("dec-111-pa-of-a-writes-partner-b", false, ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", locGrant);
            await ExecQuiet(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
            await ExecQuiet(owner, "DELETE FROM auth.login_write_principal WHERE target_id = ANY($1)", new[] { Pa, Pb });
            await ExecQuiet(owner, """
                DELETE FROM auth.audit_outbox
                 WHERE event_type = 'auth.partner_reason_bounds.changed'
                   AND envelope::text LIKE '%' || $1 || '%'
                """, Pb.ToString("D"));
        }
    }

    readonly record struct SaveAttempt(bool Denied, long? Version, string Detail);

    static async Task<SaveAttempt> TrySaveBounds(NpgsqlDataSource admin, Guid? user, Guid partner, bool flags = false)
    {
        await using var c = await admin.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (flags)
        {
            await using var set = new NpgsqlCommand("""
                SELECT set_config('app.resolved_global_admin', 'on', true),
                       set_config('app.resolved_partner_ids', '5f0c3e1a-0064-4000-8000-000000000011,5f0c3e1a-0064-4000-8000-000000000012', true),
                       set_config('app.resolved_location_ids', '5f0c3e1a-0064-4000-8000-000000000021', true),
                       set_config('app.admin_type', 'super', true)
                """, c, tx);
            await set.ExecuteNonQueryAsync();
        }
        else if (user is Guid id)
            await Begin(c, tx, id);
        try
        {
            await using var call = new NpgsqlCommand("SELECT auth.save_partner_bounds($1, 0, 10, 20, $2, now())", c, tx);
            call.Parameters.Add(new() { Value = partner });
            call.Parameters.Add(new() { Value = Sa });
            var raw = await call.ExecuteScalarAsync();
            long? version = raw is long n ? n : null;
            await tx.RollbackAsync();
            return new SaveAttempt(false, version, raw is null ? "null" : raw.GetType().Name);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            try { await tx.RollbackAsync(); } catch (ObjectDisposedException) { }
            return new SaveAttempt(true, null, ex.SqlState);
        }
    }

    static async Task<bool> BoundsRead(NpgsqlDataSource admin, NpgsqlDataSource owner)
    {
        var view = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000aa");
        try
        {
            await Exec(owner, """
                INSERT INTO auth.partner_reason_bounds (partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version)
                VALUES ($1, 10, 20, $3, now(), 1), ($2, 30, 40, $3, now(), 1)
                """, Pa, Pb, Sa);
            await Exec(owner, """
                INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, location_id, status, version, granted_by, granted_at)
                VALUES ($1, $2, 'cashier', 'location', $3, 'active', 1, $4, now())
                """, view, Cash, La, Sa);
            async Task<long> See(Guid? user, Guid partner)
                => await CountAs(admin, user, "SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = $1", partner);
            var cashA = await See(Cash, Pa);
            var cashB = await See(Cash, Pb);
            var paA = await See(Upa, Pa);
            var paB = await See(Upa, Pb);
            var saA = await See(Sa, Pa);
            var saB = await See(Sa, Pb);
            var flagA = await CountAs(admin, null, """
                SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = $1
                """, Pa);
            var ok = cashA == 1 && cashB == 0 && paA == 1 && paB == 0 && saA == 1 && saB == 1 && flagA == 0;
            if (!ok) Console.WriteLine($"FAIL bounds-read: cash {cashA}/{cashB} pa {paA}/{paB} sa {saA}/{saB} flag {flagA}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL bounds-read: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            await ExecQuiet(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
            await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", view);
        }
    }

    static async Task<bool> TokenPrincipalRowCount(HttpClient http, NpgsqlDataSource admin, NpgsqlDataSource owner, string paTok)
    {
        Guid? created = null;
        try
        {
            await Exec(owner, """
                INSERT INTO auth.partner_reason_bounds
                  (partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version)
                VALUES ($1, 11, 22, $2, now(), 1)
                """, Pb, Sa);
            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/orgs/partners/{Pa:D}/login-config/reason-bounds");
            put.Headers.TryAddWithoutValidation("Authorization", "Bearer " + paTok);
            put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
            put.Content = new StringContent("""{"reason_min_length":10,"reason_max_length":20}""", Encoding.UTF8, "application/json");
            var wrote = await Read(await http.SendAsync(put));
            var post = new HttpRequestMessage(HttpMethod.Post, "/api/orgs/locations");
            post.Headers.TryAddWithoutValidation("Authorization", "Bearer " + paTok);
            post.Content = new StringContent(
                "{\"partner_id\":\"" + Pa.ToString("D") + "\",\"name\":\"Req064 Token Proof\",\"login_config\":{" +
                "\"provider_kind\":\"database\",\"display_name\":\"Req064 Token Proof\",\"entra_allowed_tenant_ids\":[]," +
                "\"allow_db_fallback\":false,\"mfa_required\":true}}",
                Encoding.UTF8, "application/json");
            var made = await Read(await http.SendAsync(post));
            if (made.Status == 201)
            {
                using var doc = JsonDocument.Parse(made.Body);
                created = doc.RootElement.GetProperty("location_id").GetGuid();
            }
            var boundsSubject = await ScalarText(owner, """
                SELECT subject::text FROM auth.login_write_principal
                 WHERE kind = 'partner_reason_bounds' AND target_id = $1
                 ORDER BY recorded_at DESC LIMIT 1
                """, Pa);
            var locationSubject = created is null ? "" : await ScalarText(owner, """
                SELECT subject::text FROM auth.login_write_principal
                 WHERE kind = 'location_login_config' AND target_id = $1
                 ORDER BY recorded_at DESC LIMIT 1
                """, created);
            long other = -1;
            if (boundsSubject.Length > 0)
            {
                await using var c = await admin.OpenConnectionAsync();
                await using var tx = await c.BeginTransactionAsync();
                await Begin(c, tx, Guid.Parse(boundsSubject));
                await using var count = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM auth.location WHERE location_id = $1)
                         + (SELECT count(*) FROM auth.partner_reason_bounds WHERE partner_id = $2)
                    """, c, tx);
                count.Parameters.Add(new() { Value = Lb });
                count.Parameters.Add(new() { Value = Pb });
                other = Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
                await tx.RollbackAsync();
            }
            var ok = wrote.Status == 200 && made.Status == 201
                && boundsSubject == Upa.ToString("D") && locationSubject == Upa.ToString("D") && other == 0;
            if (!ok)
                Console.WriteLine($"FAIL token-principal: put {wrote.Status} {Clip(wrote.Body)} post {made.Status} {Clip(made.Body)} bounds {boundsSubject} location {locationSubject} other {other}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL token-principal: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
        finally
        {
            if (created is { } id)
            {
                await ExecQuiet(owner, "ALTER TABLE auth.location_login_config_change DISABLE TRIGGER USER");
                await ExecQuiet(owner, "ALTER TABLE auth.location DISABLE TRIGGER USER");
                await ExecQuiet(owner, "DELETE FROM auth.location_login_config_change WHERE location_id = $1", id);
                await ExecQuiet(owner, "DELETE FROM auth.location_login_config WHERE location_id = $1", id);
                await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = $1", id);
                await ExecQuiet(owner, "ALTER TABLE auth.location ENABLE TRIGGER USER");
                await ExecQuiet(owner, "ALTER TABLE auth.location_login_config_change ENABLE TRIGGER USER");
                await ExecQuiet(owner, "DELETE FROM auth.audit_outbox WHERE envelope::text LIKE '%' || $1 || '%'", id.ToString("D"));
            }
            await ExecQuiet(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
            await ExecQuiet(owner, "DELETE FROM auth.login_write_principal WHERE target_id = ANY($1)", new[] { Pa, Pb });
            await ExecQuiet(owner, """
                DELETE FROM auth.audit_outbox
                 WHERE event_type = 'auth.partner_reason_bounds.changed'
                   AND envelope::text LIKE '%' || $1 || '%'
                """, Pa.ToString("D"));
        }
    }

    static async Task<bool> LoginWritePrincipal(NpgsqlDataSource admin, NpgsqlDataSource owner)
    {
        async Task Record(Guid user, Guid target)
        {
            await using var c = await admin.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await Begin(c, tx, user);
            await using var call = new NpgsqlCommand("SELECT auth.record_login_write('location_login_config', $1)", c, tx);
            call.Parameters.Add(new() { Value = target });
            await call.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        try
        {
            await Record(Upa, La);
            await Record(Sa, Lb);
            var denied = false;
            try
            {
                await Exec(admin, """
                    INSERT INTO auth.login_write_principal (write_id, kind, target_id, issuer, subject)
                    VALUES (gen_random_uuid(), 'location_login_config', $1, $2, $3)
                    """, La, Issuer, Sa);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501") { denied = true; }
            var upa = await ScalarBool(owner, """
                SELECT issuer = $1 AND subject = $2 FROM auth.login_write_principal
                 WHERE kind = 'location_login_config' AND target_id = $3
                 ORDER BY recorded_at DESC LIMIT 1
                """, Issuer, Upa, La);
            var sa = await ScalarBool(owner, """
                SELECT issuer = $1 AND subject = $2 FROM auth.login_write_principal
                 WHERE kind = 'location_login_config' AND target_id = $3
                 ORDER BY recorded_at DESC LIMIT 1
                """, Issuer, Sa, Lb);
            if (!denied || !upa || !sa)
            {
                Console.WriteLine($"FAIL login-write: denied {denied} upa {upa} sa {sa}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL login-write: " + ex.GetType().Name + " " + ex.Message);
            return false;
        }
    }

    static async Task Cleanup(NpgsqlDataSource owner)
    {
        await ExecQuiet(owner, "ALTER TABLE auth.admin_assignment DISABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE auth.app_user DISABLE TRIGGER USER");
        // catalog.tg_no_delete refuses a hard delete. The harness lifts it only for this fixture id.
        await ExecQuiet(owner, "ALTER TABLE catalog.fee_type DISABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.ref_private DISABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.registered_entry DISABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.entry_change DISABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.history_pending DISABLE TRIGGER USER");
        await ExecQuiet(owner, "DELETE FROM catalog.fee_type WHERE entry_id = $1", Fee);
        await ExecQuiet(owner, "DELETE FROM catalog.ref_private WHERE entry_id = $1", Fee);
        await ExecQuiet(owner, "DELETE FROM catalog.entry_change WHERE entry_id = $1", Fee);
        await ExecQuiet(owner, "DELETE FROM catalog.history_pending WHERE entry_id = $1", Fee);
        await ExecQuiet(owner, "DELETE FROM catalog.registered_entry WHERE entry_id = $1", Fee);
        await ExecQuiet(owner, "ALTER TABLE catalog.fee_type ENABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.ref_private ENABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.registered_entry ENABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.entry_change ENABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE catalog.history_pending ENABLE TRIGGER USER");
        await ExecQuiet(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { Pa, Pb });
        await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000aa"));
        await ExecQuiet(owner, "DELETE FROM auth.login_write_principal");
        await ExecQuiet(owner, "DELETE FROM auth.location_login_config_change WHERE location_id = ANY($1)", new[] { La, Lb });
        await ExecQuiet(owner, "DELETE FROM auth.location_login_config WHERE location_id = ANY($1)", new[] { La, Lb });
        await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE assignment_id = $1", Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000ab"));
        await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = $1", Guid.Parse("5f0c3e1a-0064-4000-8000-000000000023"));
        await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = ANY($1)", new[] { La, Lb });
        var reqUsers = new[] { Sa, Sa2, Upa, Both, None, Cash, Mix, Guid.Parse("5f0c3e1a-0064-4000-8000-000000000037") };
        await ExecQuiet(owner, "DELETE FROM auth.request_binding WHERE user_id = ANY($1)", reqUsers);
        await ExecQuiet(owner, "DELETE FROM auth.request_session WHERE user_id = ANY($1)", reqUsers);
        await ExecQuiet(owner, "DELETE FROM auth.user_issuer WHERE user_id = ANY($1)", reqUsers);
        await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE user_id = ANY($1) OR assignment_id = ANY($2)",
            reqUsers,
            new[] { Asa, Asa2, Apa, AbothA, AbothB, Acash, AmixA, AmixB, Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000a7") });
        await ExecQuiet(owner, "DELETE FROM auth.app_user WHERE user_id = ANY($1)",
            new[] { Sa, Sa2, Upa, Both, None, Cash, Mix, Guid.Parse("5f0c3e1a-0064-4000-8000-000000000037") });
        await ExecQuiet(owner, "ALTER TABLE auth.admin_assignment ENABLE TRIGGER USER");
        await ExecQuiet(owner, "ALTER TABLE auth.app_user ENABLE TRIGGER USER");
    }

    static string Clip(string body) => body.Length <= 180 ? body : body[..180];

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

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("PASS " + name); }
        else { _fail++; Console.WriteLine("FAIL " + name + (detail is null ? "" : " :: " + detail)); }
    }

    static async Task<List<string>> PoolSample(WebApplication app)
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        var leaked = new List<string>();
        var conns = new List<NpgsqlConnection>();
        for (var i = 0; i < 4; i++)
        {
            var c = await ds.OpenConnectionAsync();
            conns.Add(c);
            await using var cmd = new NpgsqlCommand("""
                SELECT coalesce(current_setting('app.resolved_global_admin', true), '') || '|' ||
                       coalesce(current_setting('app.resolved_partner_ids', true), '') || '|' ||
                       coalesce(current_setting('app.resolved_location_ids', true), '') || '|' ||
                       coalesce(current_setting('app.admin_type', true), '') || '|' ||
                       coalesce(current_setting('app.partner_id', true), '') || '|' ||
                       coalesce(current_setting('app.request_iss', true), '') || '|' ||
                       coalesce(current_setting('app.request_sub', true), '')
                """, c);
            var v = (string)(await cmd.ExecuteScalarAsync())!;
            if (v != "||||||") leaked.Add(v);
        }
        foreach (var c in conns) await c.DisposeAsync();
        return leaked;
    }

    static async Task<bool> PoolResetsPrincipal(WebApplication app)
    {
        var ds = app.Services.GetRequiredService<NpgsqlDataSource>();
        int pid;
        await using (var c = await ds.OpenConnectionAsync())
        {
            pid = Convert.ToInt32(await new NpgsqlCommand("SELECT pg_backend_pid()", c).ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            await using var set = new NpgsqlCommand("""
                SELECT set_config('app.request_iss', 'https://req064.test.invalid', false),
                       set_config('app.request_sub', '5f0c3e1a-0064-4000-8000-000000000031', false)
                """, c);
            await set.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < 8; i++)
        {
            await using var c = await ds.OpenConnectionAsync();
            var now = Convert.ToInt32(await new NpgsqlCommand("SELECT pg_backend_pid()", c).ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            if (now != pid) continue;
            var v = (string)(await new NpgsqlCommand("""
                SELECT coalesce(current_setting('app.request_iss', true), '') || '|' ||
                       coalesce(current_setting('app.request_sub', true), '')
                """, c).ExecuteScalarAsync())!;
            return v == "|";
        }
        Console.WriteLine("FAIL pool reset: planted backend was not reused");
        return false;
    }

    static string Mint(RsaSecurityKey key, Guid user, string adminType, Guid? partner) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = Claims(user, adminType, partner),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });

    static async Task<int> IssuerStatus(string url, string jwt)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var res = await http.PostAsync(url.TrimEnd('/') + "/v1/session", new StringContent(jwt));
        return (int)res.StatusCode;
    }

    static async Task<string> IssuerSession(string url, string jwt)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var res = await http.PostAsync(url.TrimEnd('/') + "/v1/session", new StringContent(jwt));
        if (!res.IsSuccessStatusCode) return "";
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("session").GetString() ?? "";
    }

    static async Task<bool> ReusedJtiRejected(string jwt)
    {
        var json = Base64UrlEncoder.Decode(jwt.Split('.')[1]);
        using var doc = JsonDocument.Parse(json);
        var jti = Guid.Parse(doc.RootElement.GetProperty("jti").GetString()!);
        var sub = Guid.Parse(doc.RootElement.GetProperty("sub").GetString()!);
        try { await Mint(sub, jti: jti); return false; }
        catch (PostgresException ex) when (ex.SqlState == "23505") { return true; }
    }

    static Dictionary<string, object> Claims(Guid user, string adminType, Guid? partner)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer, ["aud"] = Admin.Api.AdminApiOptions.Audience, ["sub"] = user.ToString("D"),
            ["admin_type"] = adminType, ["iat"] = now, ["nbf"] = now, ["exp"] = now + 600,
            ["jti"] = Guid.NewGuid().ToString("D"),
        };
        if (partner is Guid p) claims["partner_id"] = p.ToString("D");
        return claims;
    }

    static string Payload(Guid user, string adminType, Guid? partner)
    {
        var claims = Claims(user, adminType, partner);
        return JsonSerializer.Serialize(claims);
    }

    static string Sign(RSA rsa, string? kid, string payloadJson)
    {
        var headerJson = kid is null
            ? """{"alg":"RS256","typ":"at+jwt"}"""
            : "{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"kid\":\"" + kid + "\"}";
        var header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var data = Encoding.ASCII.GetBytes(header + "." + payload);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64UrlEncoder.Encode(sig);
    }

    static async Task<(int Status, string Body)> Send(HttpClient http, HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return await Read(await http.SendAsync(req));
    }

    static async Task<(int Status, string Body)> Read(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, body);
    }

    static List<string> Screens(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("screens").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
    }

    static async Task Exec(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task ExecQuiet(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        try { await Exec(ds, sql, ps); } catch { }
    }

    static async Task<bool> ExecOk(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        try { await Exec(ds, sql, ps); return true; }
        catch (Exception ex) { Console.WriteLine("FAIL sql :: " + ex.GetType().Name + " " + ex.Message); return false; }
    }

    static NpgsqlDataSource? _issuer;
    static readonly Dictionary<Guid, string> _sessions = new();

    static async Task<NpgsqlDataSource> IssuerDs()
    {
        var cs = Environment.GetEnvironmentVariable("ISSUER_CONNECTION_STRING");
        if (string.IsNullOrEmpty(cs)) throw new InvalidOperationException("ISSUER_CONNECTION_STRING is not set");
        return _issuer ??= NpgsqlDataSource.Create(cs);
    }

    static async Task<string> Mint(Guid user, string? iss = null, DateTimeOffset? exp = null, Guid? jti = null)
    {
        await using var c = await (await IssuerDs()).OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.open_session($1, $2, $3, $4)", c);
        cmd.Parameters.Add(new() { Value = iss ?? Issuer });
        cmd.Parameters.Add(new() { Value = user.ToString("D") });
        cmd.Parameters.Add(new() { Value = jti ?? Guid.NewGuid() });
        cmd.Parameters.Add(new() { Value = exp ?? DateTimeOffset.UtcNow.AddMinutes(5) });
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    static async Task Begin(NpgsqlConnection c, NpgsqlTransaction tx, Guid user)
    {
        if (!_sessions.TryGetValue(user, out var token)) _sessions[user] = token = await Mint(user);
        await using (var sp = new NpgsqlCommand("SAVEPOINT session_bind", c, tx))
            await sp.ExecuteNonQueryAsync();
        try
        {
            await using var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx);
            b.Parameters.Add(new() { Value = token });
            await b.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT session_bind", c, tx);
            await rb.ExecuteNonQueryAsync();
            _sessions[user] = token = await Mint(user);
            await using var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx);
            b.Parameters.Add(new() { Value = token });
            await b.ExecuteNonQueryAsync();
        }
    }

    static async Task<long> CountAs(NpgsqlDataSource ds, Guid? user, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        if (user is Guid id) await Begin(c, tx, id);
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        await tx.RollbackAsync();
        return value is long n ? n : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    static async Task<bool> ScalarBoolAs(NpgsqlDataSource ds, Guid user, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await Begin(c, tx, user);
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        await tx.RollbackAsync();
        return value is true;
    }

    static async Task<bool> OpenDenied(string iss, Guid user)
    {
        try { await Mint(user, iss); return false; }
        catch (PostgresException ex) when (ex.SqlState == "42501") { return true; }
    }

    static async Task<long> ScalarInTx(NpgsqlDataSource ds, string setupSql, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var setup = new NpgsqlCommand(setupSql, c, tx))
            await setup.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        await tx.RollbackAsync();
        return value is long n ? n : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    static async Task<bool> ScalarBool(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        return value is true;
    }

    static async Task<string> ScalarText(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    static async Task<long> ScalarLong(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        var value = await cmd.ExecuteScalarAsync();
        return value is long n ? n : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
