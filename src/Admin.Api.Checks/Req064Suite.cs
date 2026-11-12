using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
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

    public static Task<int> RunAsync(string ownerCs, string adminCs) => RunAsync(ownerCs, adminCs, "all");

    public static async Task<int> RunAsync(string ownerCs, string adminCs, string mode)
    {
        var ownerBuilder = new NpgsqlConnectionStringBuilder(ownerCs);
        KitAdmin.Arm(ownerBuilder.Database ?? "", ownerBuilder.Port == 0 ? 5432 : ownerBuilder.Port);
        await using var owner = NpgsqlDataSource.Create(ownerCs);
        if (mode == "race")
        {
            var raceUser = Guid.NewGuid();
            await EnsureUser(owner, raceUser);
            var left = await OpenLogoutOverlaps(owner, raceUser);
            var fourArg = await ScalarBool(owner, "SELECT to_regprocedure('auth.open_session(text,text,uuid,timestamptz)') IS NULL");
            Check("open-logout-80-overlaps-leave-no-live-row", left == 0, left.ToString());
            Check("four-arg-open-session-is-absent", fourArg);
            return _fail == 0 ? 0 : 1;
        }
        if (mode == "helpers")
        {
            await using var helperAdmin = NpgsqlDataSource.Create(adminCs);
            await ProveDefinerHelpers(owner, helperAdmin);
            Console.WriteLine($"{_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }
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
            if (mode is "all" or "probe")
                await CrossSubjectJti(issuerProc, sign, mode == "probe");
            if (mode is "all" or "issuer")
                await IssuerSecurity(issuerProc, sign, owner, admin, opt, issuerCs);
            if (mode == "all")
            {
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
            Check("T6 revoke-takes-effect-on-next-request",
                before.Status == 200 && after.Status == 401 && after.Body == "{\"error\":\"session_expired\"}",
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
            var nameUser = Guid.NewGuid();
            await EnsureUser(owner, nameUser);
            var schemeIss = await OpenDenied("http://req064.test.invalid", nameUser);
            var slashIss = await OpenDenied("https://req064.test.invalid/", nameUser);
            var prefixIss = await OpenDenied("https://req064.test", nameUser);
            var exactIss = await Mint(nameUser);
            Check("issuer-name-alone-does-not-open",
                schemeIss && slashIss && prefixIss && exactIss.Length == 64,
                $"scheme {schemeIss} slash {slashIss} prefix {prefixIss} exact {exactIss.Length}");
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
            await SessionExpiry(http, issuerProc.Url, sign, owner, Sa);
            await ClosedSessions(http, sign, owner);
            await LoginConfigHttp(http, Mint(sign, Sa, "super", null), owner);
            await LoginSerialization(owner, admin);
            await MigratorCannotTruncate(owner, "auth.app_user", "migrator-cannot-truncate-app-user");
            await MigratorCannotTruncate(owner, "auth.admin_assignment", "migrator-cannot-truncate-admin-assignment");
            await SuperuserTruncateRaises(owner, "auth.app_user", "superuser-truncate-app-user-raises-last-active-super-admin");
            await SuperuserTruncateRaises(owner, "auth.admin_assignment", "superuser-truncate-admin-assignment-raises-last-active-super-admin");
            await GrantTruncateMutant(owner);
            await BoundsRetry(http, sign, owner, Sa);
            await SessionDeniedArm(http, sign, owner, Sa);
            var signedIn = await StandInSignIn.Run(ownerCs, adminCs, issuerCs);
            Check("stand-in-sign-in-opens-session-nav-locations-catalog", signedIn.Ok, signedIn.Detail);
            var shell = await NodeScript("session-closed.test.mjs", "PASS shell-goes-to-signin-on-closed-session");
            Check("shell-goes-to-signin-on-closed-session", shell.Ok, shell.Detail);
            var listen = await BashScript("own-listener.test.sh", "PASS own-listener-refuses-a-foreign-pid");
            Check("own-listener-refuses-a-foreign-pid", listen.Ok, listen.Detail);

            var options = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AdminApiOptions.cs"));
            var auth = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AdminAuth.cs"));
            var api = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "LoginConfigApi.cs"));
            var outbox = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Admin.Api", "AuditOutbox.cs"));
            Check("admin-api-create-inserts-location", api.Contains("INSERT INTO auth.location (location_id, partner_id, slug, name) VALUES ($1, $2, $3, $4)", StringComparison.Ordinal));
            Check("admin-api-create-inserts-location-login-config", api.Contains("INSERT INTO auth.location_login_config (", StringComparison.Ordinal));
            Check("admin-api-inserts-config-change", api.Contains("INSERT INTO auth.location_login_config_change (", StringComparison.Ordinal));
            Check("admin-api-update-uses-save-location-login-config", api.Contains("SELECT auth.save_location_login_config(", StringComparison.Ordinal));
            Check("admin-api-bounds-use-save-partner-bounds", api.Contains("SELECT auth.save_partner_bounds(", StringComparison.Ordinal));
            Check("admin-api-writes-the-audit-outbox", outbox.Contains("INSERT INTO auth.audit_outbox (", StringComparison.Ordinal));
            Check("host-does-not-read-issuer-credentials",
                !host.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !options.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !auth.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !api.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal)
                && !host.Contains("ISSUER_CALLER_SECRET", StringComparison.Ordinal)
                && !options.Contains("ISSUER_CALLER_SECRET", StringComparison.Ordinal)
                && !auth.Contains("ISSUER_CALLER_SECRET", StringComparison.Ordinal)
                && !api.Contains("ISSUER_CALLER_SECRET", StringComparison.Ordinal)
                && !api.Contains("FF_ISSUER_PASSWORD", StringComparison.Ordinal)
                && !api.Contains("set_config('app.request_iss'", StringComparison.Ordinal));

            }
            await app.StopAsync();
        }
        catch (Exception ex)
        {
            Check("req064-finished", false, ex.GetType().Name + ": " + ex.Message);
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
        Check("admin-api-cannot-open-session", await Denied(admin, "SELECT auth.open_session($1, $2, $3, $4, $5)", Issuer, Upa.ToString("D"), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow));
        Check("owner-cannot-open-session", await Denied(owner, "SELECT auth.open_session($1, $2, $3, $4, $5)", Issuer, Upa.ToString("D"), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow));
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
        await RevokeSession(revoked);
        Check("revoked-session-denied-on-next-action", await Denied(admin, "SELECT auth.begin_request($1)", revoked));

        var bound = await Mint(Sa);
        await using (var c = await owner.OpenConnectionAsync())
        {
            await using var tx = await c.BeginTransactionAsync();
            await using (var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx))
            {
                b.Parameters.Add(new() { Value = bound });
                await b.ExecuteNonQueryAsync();
            }
            await using (var rev = new NpgsqlCommand("SELECT auth.revoke_session($1)", c, tx))
            {
                rev.Parameters.Add(new() { Value = bound });
                await rev.ExecuteNonQueryAsync();
            }
            var cleared = await ScalarOn(c, tx, "SELECT auth.current_principal() IS NULL");
            Check("revoked-binding-clears-current-principal", cleared);
            await tx.RollbackAsync();
        }

        var stale = await Mint(Sa2);
        KitAdmin.Exec("expired-session-rejected-by-begin-request",
            "UPDATE auth.request_session SET expires_at = clock_timestamp() - interval '1 second' WHERE token_hash = sha256(convert_to(" + SqlText(stale) + ", 'UTF8'))");
        Check("expired-session-rejected-by-begin-request", await Denied(admin, "SELECT auth.begin_request($1)", stale));

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
        await RevokeLogin(Sa2);
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
            await RevokeLogin(Sa2);
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
            await RevokeLogin(Sa2);
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

    static async Task MigratorCannotTruncate(NpgsqlDataSource owner, string table, string name)
    {
        var state = await OwnerDdl(owner, "TRUNCATE " + table);
        Check(name, state == "42501", state);
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
        if (triggers != 2) return false;
        return await SuperuserTruncateIsLastSa(owner, "auth.admin_assignment")
            && await SuperuserTruncateIsLastSa(owner, "auth.app_user");
    }

    static async Task SuperuserTruncateRaises(NpgsqlDataSource owner, string table, string name)
    {
        var detail = await SuperuserTruncateDetail(owner, table);
        Check(name, detail.Contains("P0001", StringComparison.Ordinal) && detail.Contains("last_active_super_admin", StringComparison.Ordinal), detail);
    }

    static async Task<bool> SuperuserTruncateIsLastSa(NpgsqlDataSource owner, string table)
    {
        var detail = await SuperuserTruncateDetail(owner, table);
        return detail.Contains("P0001", StringComparison.Ordinal) && detail.Contains("last_active_super_admin", StringComparison.Ordinal);
    }

    static async Task HistoryPartnerExecuteMutant(NpgsqlDataSource owner)
    {
        string database;
        await using (var c = await owner.OpenConnectionAsync())
            database = (string)(await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync())!;
        var safe = System.Text.RegularExpressions.Regex.IsMatch(database, "^[a-z0-9_]+\\z");
        var detail = "refused";
        if (safe)
        {
            var sql = "BEGIN; GRANT EXECUTE ON FUNCTION catalog.subtype_count(uuid) TO " + database + "_test_seed; "
                + "SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace "
                + "WHERE n.nspname = 'catalog' AND p.proname = 'subtype_count' "
                + "AND has_function_privilege('" + database + "_test_seed', p.oid, 'EXECUTE'); ROLLBACK;";
            var p = Process.Start(new ProcessStartInfo("sudo", ["-u", "postgres", "psql", "-d", database, "-v", "ON_ERROR_STOP=1", "-tAc", sql])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("psql did not start");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            detail = "exit " + p.ExitCode + " " + stdout + stderr;
        }
        Check("mutant-test-seed-execute-on-another-definer-killed",
            detail.Contains("subtype_count", StringComparison.Ordinal), detail);
    }

    public static async Task<NpgsqlDataSource> OpenTestSeed(NpgsqlDataSource owner)
    {
        string database;
        string host;
        int port;
        await using (var c = await owner.OpenConnectionAsync())
        {
            database = (string)(await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync())!;
            host = c.Host ?? "127.0.0.1";
            port = c.Port;
        }
        var path = "/tmp/ff-db/" + database + "_test_seed.env";
        var line = File.ReadAllText(path).Trim();
        const string prefix = "TEST_SEED_PASSWORD=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length <= prefix.Length)
            throw new InvalidOperationException("test seed password file is missing a password");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = database,
            Username = database + "_test_seed",
            Password = line[prefix.Length..],
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    static async Task GrantTruncateMutant(NpgsqlDataSource owner)
    {
        string database;
        string role;
        await using (var c = await owner.OpenConnectionAsync())
        {
            database = (string)(await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync())!;
            role = (string)(await new NpgsqlCommand("SELECT current_user", c).ExecuteScalarAsync())!;
        }
        var safe = System.Text.RegularExpressions.Regex.IsMatch(database, "^[a-z0-9_]+\\z")
            && System.Text.RegularExpressions.Regex.IsMatch(role, "^[a-z0-9_]+\\z");
        var detail = "refused";
        if (safe)
        {
            var sql = "BEGIN; GRANT TRUNCATE ON TABLE auth.app_user TO " + role + "; SET ROLE " + role + "; TRUNCATE auth.app_user;";
            var p = Process.Start(new ProcessStartInfo("sudo", ["-u", "postgres", "psql", "-d", database, "-v", "ON_ERROR_STOP=1", "-c", sql])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("psql did not start");
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            detail = "exit " + p.ExitCode + " " + stdout + stderr;
        }
        var killed = detail.Contains("P0001", StringComparison.Ordinal)
            && detail.Contains("last_active_super_admin", StringComparison.Ordinal)
            && !detail.Contains("42501", StringComparison.Ordinal);
        Check("grant-truncate-mutant-fails-migrator-cannot-truncate-app-user", killed, detail);
    }

    static async Task<string> SuperuserTruncateDetail(NpgsqlDataSource owner, string table)
    {
        string database;
        await using (var c = await owner.OpenConnectionAsync())
            database = (string)(await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync())!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(database, "^[a-z0-9_]+\\z")) return "bad-database";
        if (table is not ("auth.app_user" or "auth.admin_assignment")) return "bad-table";
        var p = Process.Start(new ProcessStartInfo("sudo", ["-u", "postgres", "psql", "-d", database, "-v", "ON_ERROR_STOP=1", "-c", "TRUNCATE " + table])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("psql did not start");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return "exit " + p.ExitCode + " " + stdout + stderr;
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
            await Begin(c, tx, Both);
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
        await using var seed = await OpenTestSeed(owner);
        await Exec(seed, """
            INSERT INTO auth.app_user (user_id, partner_id, status) VALUES
              ($1, NULL, 'active'), ($2, NULL, 'active'), ($3, NULL, 'active'),
              ($4, NULL, 'active'), ($5, NULL, 'active'), ($6, NULL, 'active'),
              ($7, NULL, 'active')
            """, Sa, Sa2, Upa, Both, None, Cash, Mix);
        await Exec(seed, """
            INSERT INTO auth.user_issuer (user_id, issuer)
            SELECT x, $1 FROM unnest($2::uuid[]) AS x
            ON CONFLICT (user_id) DO NOTHING
            """, Issuer, new[] { Sa, Sa2, Upa, Both, None, Cash, Mix });
        await Exec(seed, """
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
        await Exec(seed, "INSERT INTO auth.location VALUES ($1,$2,'req064-a','Req064 A'),($3,$4,'req064-b','Req064 B')", La, Pa, Lb, Pb);
        await Exec(seed, """
            INSERT INTO auth.location_login_config (location_id, provider_kind, display_name, entra_allowed_tenant_ids, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at)
            VALUES ($1,'database','A','{}',false,false,$3,now(),$3,now()),
                   ($2,'database','B','{}',false,false,$3,now(),$3,now())
            """, La, Lb, Sa);
        if (await ScalarLong(owner, "SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1", Fee) > 0)
            return;
        await using var c = await seed.OpenConnectionAsync();
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

    static Task Cleanup(NpgsqlDataSource owner)
    {
        // The runner recreates the scratch database. This suite does not delete its rows.
        _ = owner;
        return Task.CompletedTask;
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

    static Task<int> IssuerStatus(string url, string jwt) => IssuerStatus(url, jwt, false);

    static async Task<int> IssuerStatus(string url, string jwt, bool absolute)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var target = absolute ? url : url.TrimEnd('/') + "/v1/session";
        var res = await http.PostAsync(target, new StringContent(jwt));
        return (int)res.StatusCode;
    }

    static bool SameClosed((int Status, string Body, string Authenticate) got)
        => got.Status == 401 && got.Body == "{\"error\":\"session_expired\"}" && got.Authenticate == "Bearer";

    static async Task SessionExpiry(HttpClient http, string issuerUrl, RsaSecurityKey sign, NpgsqlDataSource owner, Guid user)
    {
        var jwt = Mint(sign, user, "super", null);
        using var claims = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]));
        var jti = Guid.Parse(claims.RootElement.GetProperty("jti").GetString()!);
        var opened = await Send(http, HttpMethod.Get, "/api/nav", jwt);
        Check("session-live-before-expiry", opened.Status == 200, opened.Status.ToString());
        KitAdmin.Exec("expired-session-is-the-unified-401",
            "UPDATE auth.request_session SET expires_at = clock_timestamp() - interval '1 second' WHERE jti = '" + jti + "'");
        var expired = await SendClosed(http, HttpMethod.Get, "/api/nav", jwt);
        var again = await IssuerStatus(issuerUrl, jwt);
        var rows = await ScalarLong(owner, "SELECT count(*) FROM auth.request_session WHERE jti = $1", jti);
        var live = await ScalarLong(owner, "SELECT count(*) FROM auth.request_session WHERE jti = $1 AND revoked_at IS NULL AND expires_at > clock_timestamp()", jti);
        Check("expired-session-is-the-unified-401", SameClosed(expired), expired.Status + " " + expired.Authenticate + " " + Clip(expired.Body));
        Check("expired-session-is-not-replaced", again == 401 && SameClosed(expired) && rows == 1 && live == 0,
            $"issuer {again} rows {rows} live {live}");
        await RevokeSession(jwt);
        var revived = await IssuerStatus(issuerUrl, jwt);
        var nav = await SendClosed(http, HttpMethod.Get, "/api/nav", jwt);
        var still = await ScalarLong(owner, "SELECT count(*) FROM auth.request_session WHERE jti = $1 AND revoked_at IS NOT NULL", jti);
        Check("revoked-session-is-not-replaced", revived == 401 && SameClosed(nav) && still == 1,
            $"issuer {revived} nav {nav.Status} {nav.Authenticate} {Clip(nav.Body)} revoked {still}");
    }

    static async Task ClosedSessions(HttpClient http, RsaSecurityKey sign, NpgsqlDataSource owner)
    {
        var user = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000b0");
        await EnsureUser(owner, user);
        var expiredJwt = Mint(sign, user, "super", null);
        var revokedJwt = Mint(sign, user, "super", null);
        var loggedOutJwt = Mint(sign, user, "super", null);
        var openedExpired = await Send(http, HttpMethod.Get, "/api/nav", expiredJwt);
        var openedRevoked = await Send(http, HttpMethod.Get, "/api/nav", revokedJwt);
        var openedLoggedOut = await Send(http, HttpMethod.Get, "/api/nav", loggedOutJwt);
        Check("closed-session-fixtures-opened", openedExpired.Status == 200 && openedRevoked.Status == 200 && openedLoggedOut.Status == 200,
            $"{openedExpired.Status} {openedRevoked.Status} {openedLoggedOut.Status}");
        KitAdmin.Exec("closed-sessions-are-byte-identical",
            "UPDATE auth.request_session SET expires_at = clock_timestamp() - interval '1 second' WHERE jti = '" + JtiOf(expiredJwt) + "'");
        await RevokeSession(revokedJwt);
        var expired = await SendClosed(http, HttpMethod.Get, "/api/nav", expiredJwt);
        var revoked = await SendClosed(http, HttpMethod.Get, "/api/nav", revokedJwt);
        await using (var c = await (await IssuerDs()).OpenConnectionAsync())
        await using (var cmd = new NpgsqlCommand("SELECT auth.revoke_login($1, $2)", c))
        {
            cmd.Parameters.Add(new() { Value = Issuer });
            cmd.Parameters.Add(new() { Value = user.ToString("D") });
            await cmd.ExecuteNonQueryAsync();
        }
        var loggedOut = await SendClosed(http, HttpMethod.Get, "/api/nav", loggedOutJwt);
        Check("closed-sessions-are-byte-identical",
            SameClosed(expired) && expired == revoked && revoked == loggedOut,
            $"expired {expired.Status} {expired.Authenticate} {Clip(expired.Body)} | revoked {revoked.Status} {revoked.Authenticate} {Clip(revoked.Body)} | logged-out {loggedOut.Status} {loggedOut.Authenticate} {Clip(loggedOut.Body)}");
    }

    static async Task LoginSerialization(NpgsqlDataSource owner, NpgsqlDataSource admin)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "db", "migrations.sql"));
        var oldAt = sql.IndexOf("20261110290000_Req064Session", StringComparison.Ordinal);
        var newAt = sql.IndexOf("20261111180000_Req064SessionClose", StringComparison.Ordinal);
        var oldBody = oldAt >= 0 && newAt > oldAt ? sql[oldAt..newAt] : "";
        var applied = await ScalarLong(owner, """SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId" = '20261111180000_Req064SessionClose'""");
        var def = await ScalarText(owner, "SELECT pg_get_functiondef('auth.open_session(text,text,uuid,timestamptz,timestamptz)'::regprocedure)");
        var gone = await ScalarBool(owner, "SELECT to_regprocedure('auth.open_session(text,text,uuid,timestamptz)') IS NULL");
        Check("shipped-session-migration-still-contains-the-replace",
            oldBody.Contains("DELETE FROM auth.request_session", StringComparison.Ordinal)
            && oldBody.Contains("expires_at <= v_created", StringComparison.Ordinal));
        Check("live-open-session-does-not-replace-an-expired-jti",
            applied == 1 && gone && def.Contains("jti_reused", StringComparison.Ordinal)
            && !def.Contains("DELETE FROM auth.request_session", StringComparison.Ordinal),
            $"applied {applied} gone {gone}");

        var lockUser = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000b1");
        await EnsureUser(owner, lockUser);
        await ClearSessions(owner, lockUser);
        var waited = await OpenWaits(owner, lockUser);
        Check("open-session-waits-on-the-login-lock", waited == "waiting True live 0 len 64", waited);

        var cutoffUser = Guid.NewGuid();
        await EnsureUser(owner, cutoffUser);
        await ClearSessions(owner, cutoffUser);
        var old = await ScalarTime(owner, "SELECT clock_timestamp() - interval '1 hour'");
        var first = await Mint(cutoffUser, iat: old);
        var afterOpen = await LiveSessions(owner, cutoffUser);
        await RevokeLogin(cutoffUser);
        var afterRevoke = await LiveSessions(owner, cutoffUser);
        var denied = false;
        try { await Mint(cutoffUser, iat: old, jti: Guid.NewGuid()); }
        catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText == "session_denied") { denied = true; }
        var still = await LiveSessions(owner, cutoffUser);
        var freshAt = await ScalarTime(owner, "SELECT clock_timestamp()");
        var fresh = await Mint(cutoffUser, iat: freshAt);
        Check("jwt-issued-before-logout-cannot-open",
            first.Length == 64 && afterOpen == 1 && afterRevoke == 0 && denied && still == 0 && fresh.Length == 64,
            $"open {afterOpen} revoke {afterRevoke} denied {denied} still {still} fresh {fresh.Length}");

        var raceUser = Guid.NewGuid();
        await EnsureUser(owner, raceUser);
        var left = await OpenLogoutOverlaps(owner, raceUser);
        Check("open-logout-80-overlaps-leave-no-live-row", left == 0, left.ToString());

        var lockedUser = Guid.NewGuid();
        await EnsureUser(owner, lockedUser);
        await ClearSessions(owner, lockedUser);
        var locked = await OpenBehindLockUpdate(owner, lockedUser);
        Check("open-blocked-behind-a-lock-does-not-open-a-locked-user",
            locked == "waiting True while 0 outcome denied live 0", locked);

        var expiryUser = Guid.NewGuid();
        await EnsureUser(owner, expiryUser);
        await ClearSessions(owner, expiryUser);
        var expiry = await OpenBehindExpiringLock(owner, expiryUser);
        Check("open-during-a-lock-does-not-grant-after-expiry",
            expiry == "waiting True while 0 outcome denied live 0 later 64", expiry);

        var futureUser = Guid.NewGuid();
        await EnsureUser(owner, futureUser);
        await ClearSessions(owner, futureUser);
        var ahead5 = await ScalarTime(owner, "SELECT clock_timestamp() + interval '5 seconds'");
        var ahead60 = await ScalarTime(owner, "SELECT clock_timestamp() + interval '60 seconds'");
        var denied5 = await OpenDeniedAt(futureUser, ahead5);
        var denied60 = await OpenDeniedAt(futureUser, ahead60);
        await RevokeLogin(futureUser);
        var after5 = await OpenDeniedAt(futureUser, ahead5);
        var after60 = await OpenDeniedAt(futureUser, ahead60);
        var futureLive = await LiveSessions(owner, futureUser);
        await Task.Delay(1100 - DateTimeOffset.UtcNow.Millisecond);
        var freshIat = await Mint(futureUser, iat: await ScalarTime(owner, "SELECT clock_timestamp()"));
        Check("future-iat-cannot-beat-logout-or-skew",
            denied5 && denied60 && after5 && after60 && futureLive == 0 && freshIat.Length == 64,
            $"before5 {denied5} before60 {denied60} after5 {after5} after60 {after60} live {futureLive} fresh {freshIat.Length}");

        var sameUser = Guid.NewGuid();
        await EnsureUser(owner, sameUser);
        await ClearSessions(owner, sameUser);
        await RevokeLogin(sameUser);
        var sameSecond = await ScalarTime(owner, "SELECT date_trunc('second', c.revoked_before) FROM auth.login_cutoff c WHERE c.user_id = '" + sameUser.ToString("D") + "'");
        var sameDenied = await OpenDeniedAt(sameUser, sameSecond);
        var exactDenied = await OpenAtRevokedBefore(owner, sameUser);
        Check("same-second-re-sign-in-after-logout-is-denied",
            sameDenied && exactDenied && await LiveSessions(owner, sameUser) == 0,
            $"second {sameDenied} exact {exactDenied}");

        var writes = await OwnerCutoffWrites(owner, lockedUser);
        Check("owner-cannot-write-login-cutoff",
            writes == "priv False insert 42501 update 42501 delete 42501", writes);
        var truncated = await OwnerDdl(owner, "TRUNCATE TABLE auth.login_cutoff");
        Check("owner-cannot-truncate-login-cutoff", truncated == "42501", truncated);
        var replaced = await OwnerDdl(owner, """
            CREATE OR REPLACE FUNCTION auth.session_still_open(p_token text, p_iat timestamptz)
            RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, auth
            AS $$ SELECT true $$
            """);
        var altered = await OwnerDdl(owner, "ALTER FUNCTION auth.session_still_open(text, timestamptz) OWNER TO CURRENT_USER");
        Check("owner-cannot-replace-session-still-open",
            replaced == "42501" && altered == "42501", $"replace {replaced} alter {altered}");
        await ProveDefinerHelpers(owner, admin);
        var history = await BashRepoText("db/tests/intermediate-recreate.sh");
        Check("force-recreates-an-intermediate-req064-database",
            history.Text.Contains("PASS force-recreates-an-intermediate-req064-database", StringComparison.Ordinal), history.Text);
        Check("future-main-id-is-kept",
            history.Text.Contains("PASS future-main-id-is-kept", StringComparison.Ordinal), history.Text);
        Check("each-intermediate-req064-id-is-recreated",
            history.Text.Contains("PASS each-intermediate-req064-id-is-recreated", StringComparison.Ordinal), history.Text);
        Check("non-kit-database-is-refused",
            history.Text.Contains("PASS non-kit-database-is-refused", StringComparison.Ordinal), history.Text);
    }

    static async Task ProveDefinerHelpers(NpgsqlDataSource owner, NpgsqlDataSource admin)
    {
        var lastSa = await OwnerReplaces(owner,
            """
            CREATE OR REPLACE FUNCTION auth._last_sa_guard() RETURNS trigger
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, auth
            AS $$ BEGIN RETURN NULL; END $$
            """,
            "ALTER FUNCTION auth._last_sa_guard() OWNER TO CURRENT_USER",
            """
            CREATE OR REPLACE FUNCTION auth._last_sa_truncate_guard() RETURNS trigger
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, auth
            AS $$ BEGIN RETURN NULL; END $$
            """,
            "ALTER FUNCTION auth._last_sa_truncate_guard() OWNER TO CURRENT_USER");
        Check("owner-cannot-replace-last-sa-guards", lastSa == "42501 42501 42501 42501", lastSa);
        var replaced = await OwnerReplaces(owner,
            """
            CREATE OR REPLACE FUNCTION auth.location_partner(p_location_id uuid) RETURNS uuid
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, auth
            AS $$ SELECT NULL::uuid $$
            """,
            "ALTER FUNCTION auth.location_partner(uuid) OWNER TO CURRENT_USER",
            """
            CREATE OR REPLACE FUNCTION auth.tenant_bound_elsewhere(p_tid uuid, p_partner_id uuid) RETURNS boolean
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, auth
            AS $$ SELECT false $$
            """,
            "ALTER FUNCTION auth.tenant_bound_elsewhere(uuid, uuid) OWNER TO CURRENT_USER");
        Check("owner-cannot-replace-location-helpers", replaced == "42501 42501 42501 42501", replaced);
        var sweep = await SecurityDefinerSweep(owner);
        Check("security-definer-owner-is-the-definer-role", sweep == "", sweep);
        await Run14PolicySweep(owner);

        // The same statements LoginConfigApi sends. admin_api has no resolved GUCs,
        // so RLS hides the rows. The definer functions still see them.
        var loc = Guid.NewGuid();
        var partner = Guid.NewGuid();
        var otherPartner = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var slug = "h" + loc.ToString("N")[..16];
        var actor = Guid.NewGuid();
        try
        {
            await Exec(owner, "INSERT INTO auth.location (location_id, partner_id, slug, name) VALUES ($1, $2, $3, $4)",
                loc, partner, slug, "Req064 helper");
            await Exec(owner, """
                INSERT INTO auth.location_login_config (
                  location_id, provider_kind, display_name, entra_allowed_tenant_ids,
                  oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required,
                  created_by, created_at, updated_by, updated_at)
                VALUES ($1, 'entra', 'Req064 helper', ARRAY[$2]::uuid[],
                        'https://login.microsoftonline.com', '5f0c3e1a-00cc-4000-8000-0000000000cc',
                        'arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/req064-AbCdEf',
                        false, true, $3, clock_timestamp(), $3, clock_timestamp())
                """, loc, tenant, actor);
            var hidden = await ScalarLong(admin, "SELECT count(*) FROM auth.location WHERE location_id = $1", loc);
            var seen = await ScalarText(admin, "SELECT auth.location_partner($1)::text", loc);
            var elsewhere = await ScalarBool(admin, "SELECT auth.tenant_bound_elsewhere($1, $2)", tenant, otherPartner);
            var home = await ScalarBool(admin, "SELECT auth.tenant_bound_elsewhere($1, $2)", tenant, partner);
            Check("login-config-helpers-see-through-the-definer",
                hidden == 0 && seen == partner.ToString("D") && elsewhere && !home,
                $"hidden {hidden} seen {seen} elsewhere {elsewhere} home {home}");
        }
        finally
        {
            await ExecQuiet(owner, "DELETE FROM auth.location_login_config WHERE location_id = $1", loc);
            await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = $1", loc);
        }
    }

    static async Task<string> OwnerReplaces(NpgsqlDataSource owner, params string[] sql)
    {
        var parts = new string[sql.Length];
        for (var i = 0; i < sql.Length; i++) parts[i] = await OwnerDdl(owner, sql[i]);
        return string.Join(' ', parts);
    }

    static async Task<string> SecurityDefinerSweep(NpgsqlDataSource owner)
    {
        // The 22-function pin is gone. Every SECURITY DEFINER is owned by
        // _auth_definer or _catalog_definer. A migrator-owned definer fails.
        var raw = await ScalarText(owner, """
            WITH definers AS (
              SELECT n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' AS sig,
                     r.rolname AS owner_name,
                     r.rolname = current_database() || '_auth_definer' AS auth_owned,
                     r.rolname = current_database() || '_catalog_definer' AS catalog_owned,
                     r.rolname = (
                       SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()
                     ) AS migration_owned
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN pg_roles r ON r.oid = p.proowner
               WHERE p.prosecdef
            ),
            pinned(sig) AS (
              VALUES
                ('catalog._insert_marker(p_entry uuid, p_kind text)'),
                ('catalog._insert_subtype(p_entry uuid, p_family text, p_code text, p_owner uuid, j jsonb)'),
                ('catalog._seal_marker()'),
                ('catalog._sealed_marker()'),
                ('catalog._visible_ref(p_entry uuid, p_family text, p_owner uuid, p_mode text)'),
                ('catalog.apply_claim_edit(p_entry_id uuid, p_expected_version bigint, p_patch jsonb)'),
                ('catalog.apply_entry_create(p_entry_id uuid, p_family text, p_entry_class text, p_owner_partner_id uuid, p_code text, p_is_active boolean, p_subtype jsonb, p_registry_row_count integer)'),
                ('catalog.subtype_count(p_entry uuid)'),
                ('catalog.subtype_json(p_entry uuid)'),
                ('catalog.tg_a_ref_guard()'),
                ('catalog.tg_claim_subtype()'),
                ('catalog.tg_no_delete()'),
                ('catalog.tg_processor_cycle_commit()'),
                ('catalog.tg_processor_parent()'),
                ('catalog.tg_registry_before()'),
                ('catalog.tg_registry_commit()'),
                ('catalog.tg_registry_pending()'),
                ('catalog.tg_registry_update()'),
                ('catalog.tg_scope_refuse()'),
                ('catalog.tg_subtype_no_update()'),
                ('catalog.tg_subtype_seal()'),
                ('catalog.tg_tx_marker_stmt()')
            )
            SELECT CASE
              WHEN EXISTS (SELECT 1 FROM definers WHERE migration_owned) THEN
                'migrator-owned ' || (SELECT string_agg(sig, ', ' ORDER BY sig) FROM definers WHERE migration_owned)
              WHEN (SELECT count(*) FROM pinned p JOIN definers d ON d.sig = p.sig WHERE d.catalog_owned) <> 22 THEN
                'catalog-definer count'
              WHEN EXISTS (SELECT 1 FROM definers WHERE NOT auth_owned AND NOT catalog_owned) THEN
                'other-owner ' || (SELECT string_agg(sig, ', ' ORDER BY sig) FROM definers WHERE NOT auth_owned AND NOT catalog_owned)
              ELSE ''
            END
            """);
        return raw ?? "null";
    }

    static async Task Run14PolicySweep(NpgsqlDataSource owner)
    {
        var migrator = await ScalarText(owner, "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()");
        var forAll = await ScalarText(owner, """
            SELECT coalesce(string_agg(schemaname || '.' || tablename || '.' || policyname, ', ' ORDER BY 1), '')
              FROM pg_policies
             WHERE cmd = 'ALL'
            """);
        Check("no-owner-for-all-policy", forAll == "", forAll);
        var forced = await ScalarText(owner, """
            SELECT coalesce(string_agg(n.nspname || '.' || c.relname, ', ' ORDER BY 1), '')
              FROM pg_class c
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE c.relkind = 'r'
               AND (
                    (n.nspname IN ('auth', 'catalog'))
                    OR (n.nspname = 'public' AND c.relname = '__EFMigrationsHistory')
               )
               AND (
                    NOT c.relrowsecurity
                    OR NOT c.relforcerowsecurity
                    OR pg_get_userbyid(c.relowner) = (
                         SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()
                       )
               )
            """);
        Check("a2-tables-are-forced-and-not-migrator-owned", forced == "", forced);
        var dml = await ScalarText(owner, """
            SELECT coalesce(string_agg(table_schema || '.' || table_name || ':' || privilege_type, ', ' ORDER BY 1), '')
              FROM information_schema.role_table_grants
             WHERE grantee = current_user
               AND privilege_type IN ('INSERT', 'UPDATE', 'DELETE', 'TRUNCATE')
               AND NOT (
                    table_schema = 'public' AND table_name = '__EFMigrationsHistory'
                    AND privilege_type IN ('INSERT', 'UPDATE', 'DELETE')
               )
            """);
        Check("migrator-dml-is-history-only", dml == "", dml + " migrator " + migrator);
        var seed = await ScalarText(owner, """
            WITH expected(schema_name, table_name) AS (
              VALUES
                ('catalog','registered_entry'),
                ('catalog','catalog_release'), ('catalog','transaction_type'), ('catalog','channel'),
                ('catalog','device_category'), ('catalog','manufacturer'), ('catalog','device_model'),
                ('catalog','hardware_capability_kind'), ('catalog','model_hardware_claim'),
                ('catalog','card_network'), ('catalog','card_funding_class'), ('catalog','payment_rail'),
                ('catalog','check_instrument_kind'), ('catalog','check_drawer_class'), ('catalog','check_purpose'),
                ('catalog','processor'), ('catalog','transaction_status'), ('catalog','reason_code'),
                ('catalog','id_document_type'), ('catalog','currency'), ('catalog','fee_type'),
                ('auth','app_user'), ('auth','admin_assignment'), ('auth','user_issuer'), ('auth','location'),
                ('auth','location_login_config'), ('auth','location_login_config_change'),
                ('auth','external_login'), ('auth','user_location'), ('auth','partner_reason_bounds')
            ),
            grants AS (
              SELECT table_schema, table_name, privilege_type
                FROM information_schema.role_table_grants
               WHERE grantee = current_database() || '_test_seed'
            )
            SELECT CASE
              WHEN NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_database() || '_test_seed') THEN 'missing-role'
              WHEN EXISTS (
                SELECT 1 FROM pg_auth_members m
                  JOIN pg_roles r ON r.oid IN (m.roleid, m.member)
                 WHERE r.rolname = current_database() || '_test_seed'
              ) THEN 'membership'
              WHEN EXISTS (SELECT 1 FROM grants WHERE privilege_type NOT IN ('SELECT', 'INSERT')) THEN 'extra-privilege'
              WHEN EXISTS (
                SELECT 1 FROM grants g
                 WHERE NOT EXISTS (
                   SELECT 1 FROM expected e WHERE e.schema_name = g.table_schema AND e.table_name = g.table_name
                 )
              ) THEN 'extra-table'
              WHEN EXISTS (
                SELECT 1 FROM expected e
                 WHERE (
                   SELECT count(DISTINCT privilege_type) FROM grants g
                    WHERE g.table_schema = e.schema_name AND g.table_name = e.table_name
                      AND g.privilege_type IN ('SELECT', 'INSERT')
                 ) <> 2
              ) THEN 'missing-grant'
              WHEN EXISTS (
                SELECT 1 FROM grants
                 WHERE table_schema = 'auth'
                   AND table_name IN ('login_cutoff', 'request_session', 'request_binding')
              ) THEN 'session-grant'
              ELSE ''
            END
            """);
        Check("test-seed-grants-are-insert-select-only", seed == "", seed);
        var path = await ScalarText(owner, """
            SELECT coalesce(string_agg(p.proname, ', ' ORDER BY p.proname), '')
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'catalog'
               AND (p.prosecdef OR p.proname IN (
                    '_actor','_err','_keys_ok','_req_text','_opt_text','_req_bool','_opt_uuid',
                    '_req_uuid','_req_timestamptz','_req_smallint','history_partner'))
               AND NOT (p.proconfig @> ARRAY['search_path=pg_catalog'])
            """);
        Check("catalog-definer-search-path-is-pg-catalog", path == "", path);
        var exec = await ScalarText(owner, """
            SELECT coalesce(string_agg(p.proname, ', ' ORDER BY p.proname), '')
              FROM pg_proc p
              JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'catalog'
               AND (p.prosecdef OR p.proname IN (
                    '_actor','_err','_keys_ok','_req_text','_opt_text','_req_bool','_opt_uuid',
                    '_req_uuid','_req_timestamptz','_req_smallint','history_partner'))
               AND (
                    p.proacl IS NULL
                    OR EXISTS (
                      SELECT 1 FROM aclexplode(p.proacl) e
                       WHERE e.privilege_type = 'EXECUTE' AND e.grantee = 0
                    )
                    OR EXISTS (
                      SELECT 1 FROM aclexplode(p.proacl) e
                        JOIN pg_roles r ON r.oid = e.grantee
                       WHERE e.privilege_type = 'EXECUTE' AND r.rolcanlogin
                         AND NOT (
                           r.rolname = current_database() || '_test_seed'
                           AND p.proname = 'history_partner'
                         )
                    )
               )
            """);
        Check("catalog-definer-has-no-public-or-login-execute", exec == "", exec);
        await HistoryPartnerExecuteMutant(owner);
        var alter = await OwnerDdl(owner, "ALTER POLICY location_owner ON auth.location USING (false)");
        var drop = await OwnerDdl(owner, "DROP TRIGGER app_user_last_sa ON auth.app_user");
        Check("owner-cannot-alter-policy-or-drop-trigger", alter == "42501" && drop == "42501", alter + " " + drop);
    }

    static async Task LoginConfigHttp(HttpClient http, string saTok, NpgsqlDataSource owner)
    {
        var tenant = Guid.NewGuid();
        string Body(Guid partner) =>
            "{\"partner_id\":\"" + partner.ToString("D") + "\",\"name\":\"Req064 Helper " + tenant.ToString("N")[..8] +
            "\",\"login_config\":{\"provider_kind\":\"entra\",\"display_name\":\"Req064 Helper\"," +
            "\"entra_allowed_tenant_ids\":[\"" + tenant.ToString("D") + "\"],\"allow_db_fallback\":false,\"mfa_required\":true}}";
        Guid created = Guid.Empty;
        try
        {
            var first = await SendJson(http, HttpMethod.Post, "/api/orgs/locations", saTok, Body(Pa));
            if (first.Status == 201)
            {
                using var doc = JsonDocument.Parse(first.Body);
                created = doc.RootElement.GetProperty("location_id").GetGuid();
            }
            var got = await Send(http, HttpMethod.Get, "/api/orgs/locations/" + created.ToString("D") + "/login-config", saTok);
            var second = await SendJson(http, HttpMethod.Post, "/api/orgs/locations", saTok, Body(Pb));
            Check("login-config-helpers-still-serve-the-api",
                first.Status == 201 && got.Status == 200 && second.Status == 422
                && second.Body.Contains("tenant_bound_elsewhere", StringComparison.Ordinal),
                $"create {first.Status} {Clip(first.Body)} get {got.Status} bound {second.Status} {Clip(second.Body)}");
        }
        finally
        {
            if (created != Guid.Empty)
            {
                await ExecQuiet(owner, "DELETE FROM auth.location_login_config_change WHERE location_id = $1", created);
                await ExecQuiet(owner, "DELETE FROM auth.location_login_config WHERE location_id = $1", created);
                await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = $1", created);
                await ExecQuiet(owner, "DELETE FROM auth.audit_outbox WHERE envelope::text LIKE '%' || $1 || '%'", created.ToString("D"));
            }
        }
    }

    static async Task<(int Status, string Body)> SendJson(HttpClient http, HttpMethod method, string path, string token, string json)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Read(await http.SendAsync(req));
    }

    static async Task<string> OpenBehindLockUpdate(NpgsqlDataSource owner, Guid user)
    {
        await using var hold = await owner.OpenConnectionAsync();
        await using var tx = await hold.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814064213, hashtext($1::uuid::text))", hold, tx))
        {
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteScalarAsync();
        }
        var open = Task.Run(async () =>
        {
            try
            {
                await Mint(user);
                return "opened";
            }
            catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText == "session_denied")
            {
                return "denied";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        });
        await Task.Delay(800);
        var waiting = !open.IsCompleted;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE auth.app_user SET locked_until = clock_timestamp() + interval '1 hour' WHERE user_id = $1", hold, tx))
        {
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteNonQueryAsync();
        }
        var whileLocked = await LiveSessions(owner, user);
        await tx.CommitAsync();
        var outcome = "timeout";
        try { outcome = await open.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { outcome = ex.GetType().Name; }
        var live = await LiveSessions(owner, user);
        return $"waiting {waiting} while {whileLocked} outcome {outcome} live {live}";
    }

    static async Task<string> OpenBehindExpiringLock(NpgsqlDataSource owner, Guid user)
    {
        await using var hold = await owner.OpenConnectionAsync();
        await using var tx = await hold.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814064213, hashtext($1::uuid::text))", hold, tx))
        {
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteScalarAsync();
        }
        var open = Task.Run(async () =>
        {
            try
            {
                await Mint(user);
                return "opened";
            }
            catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText == "session_denied")
            {
                return "denied";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        });
        await Task.Delay(200);
        await using (var cmd = new NpgsqlCommand(
            "UPDATE auth.app_user SET locked_until = clock_timestamp() + interval '300 milliseconds' WHERE user_id = $1", hold, tx))
        {
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteNonQueryAsync();
        }
        await Task.Delay(800);
        var waiting = !open.IsCompleted;
        var whileLocked = await LiveSessions(owner, user);
        await tx.CommitAsync();
        var outcome = "timeout";
        try { outcome = await open.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { outcome = ex.GetType().Name; }
        var live = await LiveSessions(owner, user);
        string later;
        try { later = (await Mint(user)).Length.ToString(); }
        catch (Exception ex) { later = ex.GetType().Name; }
        return $"waiting {waiting} while {whileLocked} outcome {outcome} live {live} later {later}";
    }

    static async Task<string> OwnerCutoffWrites(NpgsqlDataSource owner, Guid user)
    {
        var priv = await ScalarBool(owner, """
            SELECT has_table_privilege(current_user, 'auth.login_cutoff', 'INSERT')
                OR has_table_privilege(current_user, 'auth.login_cutoff', 'UPDATE')
                OR has_table_privilege(current_user, 'auth.login_cutoff', 'DELETE')
            """);
        var insert = await CutoffWrite(owner, user, "INSERT INTO auth.login_cutoff (user_id, revoked_before) VALUES ($1, clock_timestamp())");
        var update = await CutoffWrite(owner, user, "UPDATE auth.login_cutoff SET revoked_before = clock_timestamp() WHERE user_id = $1");
        var delete = await CutoffWrite(owner, user, "DELETE FROM auth.login_cutoff WHERE user_id = $1");
        return $"priv {priv} insert {insert} update {update} delete {delete}";
    }

    static async Task<string> OwnerDdl(NpgsqlDataSource owner, string sql)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            await cmd.ExecuteNonQueryAsync();
            return "ok";
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    static async Task<string> CutoffWrite(NpgsqlDataSource owner, Guid user, string sql)
    {
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteNonQueryAsync();
            return "ok";
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    static async Task<string> OpenWaits(NpgsqlDataSource owner, Guid user)
    {
        await using var hold = await owner.OpenConnectionAsync();
        await using var tx = await hold.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(814064213, hashtext($1::uuid::text))", hold, tx))
        {
            cmd.Parameters.Add(new() { Value = user });
            await cmd.ExecuteScalarAsync();
        }
        var open = Task.Run(async () => await Mint(user));
        await Task.Delay(800);
        var waiting = !open.IsCompleted;
        var live = await LiveSessions(owner, user);
        await tx.CommitAsync();
        try
        {
            var token = await open.WaitAsync(TimeSpan.FromSeconds(8));
            return $"waiting {waiting} live {live} len {token.Length}";
        }
        catch (Exception ex)
        {
            return $"waiting {waiting} live {live} error {ex.GetType().Name} {ex.Message}";
        }
    }

    static async Task ClearSessions(NpgsqlDataSource owner, Guid user)
    {
        // Revoked rows may remain. A fresh user has none. Do not DELETE.
        await ExecQuiet(owner, "SELECT auth.revoke_login($1, $2::text)", Issuer, user);
    }

    static async Task<long> OpenLogoutOverlaps(NpgsqlDataSource owner, Guid user)
    {
        for (var n = 0; n < 80; n++)
        {
            var iat = await ScalarTime(owner, "SELECT clock_timestamp()");
            var jti = Guid.NewGuid();
            var open = Task.Run(async () =>
            {
                try { await Mint(user, iat: iat, jti: jti); }
                catch (PostgresException ex) when (ex.SqlState is "42501" or "23505") { }
            });
            var revoke = Task.Run(async () => await RevokeLogin(user));
            await Task.WhenAll(open, revoke);
        }
        return await LiveSessions(owner, user);
    }

    static async Task BoundsRetry(HttpClient http, RsaSecurityKey sign, NpgsqlDataSource owner, Guid user)
    {
        var partner = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000ee");
        var jwt = Mint(sign, user, "super", null);
        try
        {
            await Exec(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = $1", partner);
            await Exec(owner, """
                CREATE OR REPLACE FUNCTION auth._test_bounds_dup() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, auth
                AS $fn$
                BEGIN
                  IF pg_trigger_depth() > 1 THEN
                    RETURN NEW;
                  END IF;
                  PERFORM pg_sleep(2);
                  INSERT INTO auth.partner_reason_bounds (
                    partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version)
                  VALUES (NEW.partner_id, NEW.reason_min_length, NEW.reason_max_length, NEW.updated_by, NEW.updated_at, NEW.row_version);
                  RETURN NEW;
                END
                $fn$
                """);
            await Exec(owner, """
                DO $g$
                BEGIN
                  EXECUTE format('GRANT EXECUTE ON FUNCTION auth._test_bounds_dup() TO %I', current_database() || '_auth_definer');
                END
                $g$
                """);
            await Exec(owner, """
                CREATE TRIGGER test_bounds_dup
                  BEFORE INSERT ON auth.partner_reason_bounds
                  FOR EACH ROW EXECUTE FUNCTION auth._test_bounds_dup()
                """);
            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/orgs/partners/{partner:D}/login-config/reason-bounds");
            put.Headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt);
            put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
            put.Content = new StringContent("""{"reason_min_length":10,"reason_max_length":20}""", Encoding.UTF8, "application/json");
            var send = http.SendAsync(put);
            await Task.Delay(400);
            var held = !send.IsCompleted;
            if (held)
                KitAdmin.Exec("bounds-retry-closed-session-is-401",
                    "UPDATE auth.request_session SET expires_at = clock_timestamp() - interval '1 second' WHERE jti = '" + JtiOf(jwt) + "'");
            using var res = await send.WaitAsync(TimeSpan.FromSeconds(10));
            var body = await res.Content.ReadAsStringAsync();
            var authn = res.Headers.WwwAuthenticate.ToString();
            Check("bounds-retry-closed-session-is-401",
                held && (int)res.StatusCode == 401 && body == "{\"error\":\"session_expired\"}" && authn == "Bearer",
                $"held {held} status {(int)res.StatusCode} auth [{authn}] body {Clip(body)}");
        }
        finally
        {
            await Exec(owner, "DROP TRIGGER IF EXISTS test_bounds_dup ON auth.partner_reason_bounds");
            await Exec(owner, "DROP FUNCTION IF EXISTS auth._test_bounds_dup()");
            await Exec(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = $1", partner);
        }
    }

    static async Task SessionDeniedArm(HttpClient http, RsaSecurityKey sign, NpgsqlDataSource owner, Guid user)
    {
        // The issuer has already returned this session. The retry calls begin_request
        // directly, so AdminAuth's session_expired body and Run's SessionClosedException
        // catch are not on this path. Revoking the row, while leaving it unexpired,
        // makes begin_request raise session_denied. Only the session_denied arm of
        // IsClosedSession maps that to the closed-session 401.
        var partner = Guid.Parse("5f0c3e1a-0064-4000-8000-0000000000ef");
        var jwt = Mint(sign, user, "super", null);
        try
        {
            await Exec(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = $1", partner);
            await Exec(owner, """
                CREATE OR REPLACE FUNCTION auth._test_session_denied_dup() RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog, auth
                AS $fn$
                BEGIN
                  IF pg_trigger_depth() > 1 THEN
                    RETURN NEW;
                  END IF;
                  PERFORM pg_sleep(2);
                  INSERT INTO auth.partner_reason_bounds (
                    partner_id, reason_min_length, reason_max_length, updated_by, updated_at, row_version)
                  VALUES (NEW.partner_id, NEW.reason_min_length, NEW.reason_max_length, NEW.updated_by, NEW.updated_at, NEW.row_version);
                  RETURN NEW;
                END
                $fn$
                """);
            await Exec(owner, """
                DO $g$
                BEGIN
                  EXECUTE format('GRANT EXECUTE ON FUNCTION auth._test_session_denied_dup() TO %I', current_database() || '_auth_definer');
                END
                $g$
                """);
            await Exec(owner, """
                CREATE TRIGGER test_session_denied_dup
                  BEFORE INSERT ON auth.partner_reason_bounds
                  FOR EACH ROW EXECUTE FUNCTION auth._test_session_denied_dup()
                """);
            var put = new HttpRequestMessage(HttpMethod.Put, $"/api/orgs/partners/{partner:D}/login-config/reason-bounds");
            put.Headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt);
            put.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
            put.Content = new StringContent("""{"reason_min_length":10,"reason_max_length":20}""", Encoding.UTF8, "application/json");
            var send = http.SendAsync(put);
            await Task.Delay(400);
            var held = !send.IsCompleted;
            if (held)
                await RevokeSession(jwt);
            using var res = await send.WaitAsync(TimeSpan.FromSeconds(10));
            var body = await res.Content.ReadAsStringAsync();
            var authn = res.Headers.WwwAuthenticate.ToString();
            var deniedRow = await ScalarBool(owner, """
                SELECT EXISTS (
                  SELECT 1 FROM auth.request_session
                   WHERE jti = $1 AND revoked_at IS NOT NULL AND expires_at > clock_timestamp()
                )
                """, JtiOf(jwt));
            Check("session-denied-is-the-closed-session-401",
                held && deniedRow && (int)res.StatusCode == 401
                && body == "{\"error\":\"session_expired\"}" && authn == "Bearer",
                $"held {held} denied-row {deniedRow} status {(int)res.StatusCode} auth [{authn}] body {Clip(body)}");
        }
        finally
        {
            await Exec(owner, "DROP TRIGGER IF EXISTS test_session_denied_dup ON auth.partner_reason_bounds");
            await Exec(owner, "DROP FUNCTION IF EXISTS auth._test_session_denied_dup()");
            await Exec(owner, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = $1", partner);
        }
    }

    static async Task SessionStillOpenRules(NpgsqlDataSource owner)
    {
        var user = Guid.NewGuid();
        await EnsureUser(owner, user);
        try
        {
            await ClearSessions(owner, user);
            var token = await Mint(user);
            var open = await IssuerBool("SELECT auth.session_still_open($1, clock_timestamp())", token);
            KitAdmin.Exec("session-still-open-closes-when-expired",
                "UPDATE auth.request_session SET expires_at = clock_timestamp() WHERE token_hash = sha256(convert_to(" + SqlText(token) + ", 'UTF8'))");
            var expired = await IssuerBool("SELECT auth.session_still_open($1, clock_timestamp())", token);
            Check("session-still-open-closes-when-expired", open == true && expired == false, $"open {open} expired {expired}");

            var live = await Mint(user);
            var nullIat = await IssuerCall("SELECT auth.session_still_open($1, NULL::timestamptz)", live);
            var still = await IssuerBool("SELECT auth.session_still_open($1, clock_timestamp())", live);
            Check("session-still-open-denies-null-iat",
                nullIat == "42501:session_denied" && still == true, $"null {nullIat} live {still}");
        }
        finally
        {
            await RevokeLogin(user);
        }
    }

    static async Task<bool?> IssuerBool(string sql, params object?[] ps)
    {
        var text = await IssuerCall(sql, ps);
        return text == "true" ? true : text == "false" ? false : null;
    }

    static async Task<string> IssuerCall(string sql, params object?[] ps)
    {
        await using var c = await (await IssuerDs()).OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var p in ps) cmd.Parameters.Add(new() { Value = p ?? DBNull.Value });
        try
        {
            var value = await cmd.ExecuteScalarAsync();
            return value is bool b ? (b ? "true" : "false") : "other";
        }
        catch (PostgresException ex)
        {
            return ex.SqlState + ":" + ex.MessageText;
        }
    }

    static string SqlText(string value)
    {
        var tag = "k" + Guid.NewGuid().ToString("N");
        return "$" + tag + "$" + value + "$" + tag + "$";
    }

    static Guid JtiOf(string jwt)
    {
        using var claims = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]));
        return Guid.Parse(claims.RootElement.GetProperty("jti").GetString()!);
    }

    static async Task EnsureUser(NpgsqlDataSource owner, Guid user)
    {
        await using var seed = await OpenTestSeed(owner);
        await InsertOnce(seed, """
            INSERT INTO auth.app_user (user_id, partner_id, status)
            VALUES ($1, NULL, 'active')
            """, user);
        await InsertOnce(seed, """
            INSERT INTO auth.user_issuer (user_id, issuer)
            VALUES ($1, $2)
            """, user, Issuer);
    }

    static async Task InsertOnce(NpgsqlDataSource ds, string sql, params object?[] ps)
    {
        try { await Exec(ds, sql, ps); }
        catch (PostgresException ex) when (ex.SqlState == "23505") { }
    }

    static async Task RevokeSession(string token)
    {
        await using var c = await (await IssuerDs()).OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.revoke_session($1)", c);
        cmd.Parameters.Add(new() { Value = token });
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task RevokeLogin(Guid user)
    {
        await using var c = await (await IssuerDs()).OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.revoke_login($1, $2)", c);
        cmd.Parameters.Add(new() { Value = Issuer });
        cmd.Parameters.Add(new() { Value = user.ToString("D") });
        await cmd.ExecuteNonQueryAsync();
    }

    static Task<long> LiveSessions(NpgsqlDataSource owner, Guid user)
        => ScalarLong(owner, "SELECT count(*) FROM auth.request_session WHERE user_id = $1 AND revoked_at IS NULL AND expires_at > clock_timestamp()", user);

    static async Task<DateTimeOffset> ScalarTime(NpgsqlDataSource ds, string sql)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("expected a timestamp"),
        };
    }

    static async Task<(bool Ok, string Detail)> NodeScript(string file, string pass)
    {
        var psi = new ProcessStartInfo("node", [Path.Combine(RepoRoot(), "ui", "admin-shell", file)])
        {
            WorkingDirectory = Path.Combine(RepoRoot(), "ui", "admin-shell"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process is null) return (false, "node did not start");
        var text = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0 && text.Contains(pass, StringComparison.Ordinal), (text + err).Trim());
    }

    static async Task<(int Code, string Text)> BashRepoText(string relative)
    {
        var psi = new ProcessStartInfo("bash", [Path.Combine(RepoRoot(), relative)])
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process is null) return (1, "process did not start");
        var text = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (text + err).Trim());
    }

    static async Task<(bool Ok, string Detail)> FinishScript(ProcessStartInfo psi, string pass)
    {
        using var process = Process.Start(psi);
        if (process is null) return (false, "process did not start");
        var text = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0 && text.Contains(pass, StringComparison.Ordinal), (text + err).Trim());
    }

    static async Task<(bool Ok, string Detail)> BashScript(string file, string pass)
    {
        var psi = new ProcessStartInfo("bash", [Path.Combine(RepoRoot(), "ui", "admin-shell", file)])
        {
            WorkingDirectory = Path.Combine(RepoRoot(), "ui", "admin-shell"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process is null) return (false, "bash did not start");
        var text = await process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode == 0 && text.Contains(pass, StringComparison.Ordinal), (text + err).Trim());
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

    static async Task<(int Status, string Body, string Authenticate)> SendClosed(HttpClient http, HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var res = await http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, body, res.Headers.WwwAuthenticate.ToString());
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

    static async Task<string> Mint(Guid user, string? iss = null, DateTimeOffset? exp = null, Guid? jti = null, DateTimeOffset? iat = null)
    {
        await using var c = await (await IssuerDs()).OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT auth.open_session($1, $2, $3, $4, $5)", c);
        cmd.Parameters.Add(new() { Value = iss ?? Issuer });
        cmd.Parameters.Add(new() { Value = user.ToString("D") });
        cmd.Parameters.Add(new() { Value = jti ?? Guid.NewGuid() });
        cmd.Parameters.Add(new() { Value = exp ?? DateTimeOffset.UtcNow.AddMinutes(5) });
        cmd.Parameters.Add(new() { Value = iat ?? DateTimeOffset.UtcNow });
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

    static async Task<bool> OpenDeniedAt(Guid user, DateTimeOffset iat)
    {
        try { await Mint(user, iat: iat); return false; }
        catch (PostgresException ex) when (ex.SqlState == "42501" && ex.MessageText == "session_denied") { return true; }
    }

    static async Task<bool> OpenAtRevokedBefore(NpgsqlDataSource owner, Guid user)
    {
        var exact = await ScalarTime(owner, "SELECT revoked_before FROM auth.login_cutoff WHERE user_id = '" + user.ToString("D") + "'");
        return await OpenDeniedAt(user, exact);
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

    static async Task CrossSubjectJti(Admin.Api.Checks.IssuerHarness issuer, RsaSecurityKey sign, bool alsoRetry)
    {
        string Signed(Dictionary<string, object> claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(sign, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });
        var firstClaims = Claims(Upa, "partner", Pa);
        var first = Signed(firstClaims);
        var session = await IssuerSession(issuer.Url, first);
        if (alsoRetry)
        {
            var again = await IssuerSession(issuer.Url, first);
            Check("reused-jwt-returns-the-same-session", session.Length == 64 && again == session);
        }
        var other = Claims(Sa, "super", null);
        other["jti"] = firstClaims["jti"];
        var status = await IssuerStatus(issuer.Url, Signed(other));
        Check("reused-jti-rejected-for-a-different-subject", session.Length == 64 && status == 401);
    }

    static async Task CatalogActorIgnoresForgedGuc(NpgsqlDataSource owner, Guid user)
    {
        // iat is the database clock. A client clock behind the cutoff made a post-logout
        // open miss, and ScalarOnText let the P0001 escape as req064-finished.
        var detail = "missing";
        try
        {
            var iat = await ScalarTime(owner, "SELECT clock_timestamp()");
            var token = await Mint(user, iat: iat);
            await using var c = await owner.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            try
            {
                await using (var b = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx))
                {
                    b.Parameters.Add(new() { Value = token });
                    await b.ExecuteNonQueryAsync();
                }
                await using (var guc = new NpgsqlCommand("""
                    SELECT set_config('app.resolved_global_admin', '', true),
                           set_config('app.resolved_partner_ids', 'ffffffff-ffff-4fff-8fff-ffffffffffff', true),
                           set_config('app.actor_user_id', 'ffffffff-ffff-4fff-8fff-ffffffffffff', true),
                           set_config('app.correlation_id', 'req064-actor', true)
                    """, c, tx))
                    await guc.ExecuteNonQueryAsync();
                await using (var sp = new NpgsqlCommand("SAVEPOINT actor_probe", c, tx))
                    await sp.ExecuteNonQueryAsync();
                try
                {
                    detail = await ScalarOnText(c, tx, "SELECT admin_type || '|' || actor_user_id::text FROM catalog._actor()");
                }
                catch (PostgresException ex)
                {
                    await using var rb = new NpgsqlCommand("ROLLBACK TO SAVEPOINT actor_probe", c, tx);
                    await rb.ExecuteNonQueryAsync();
                    var why = await ScalarTextOn(c, tx, """
                        SELECT coalesce(g.outcome, '') || '|' || coalesce(g.user_id::text, '')
                             || '|' || coalesce(auth.current_principal()::text, '')
                          FROM auth.resolve_grant('', '00000000-0000-0000-0000-000000000000'::uuid) g
                        """);
                    detail = ex.SqlState + ":" + ex.MessageText + " grant " + why;
                }
            }
            finally
            {
                try { await tx.RollbackAsync(); } catch (ObjectDisposedException) { }
            }
        }
        catch (PostgresException ex)
        {
            detail = ex.SqlState + ":" + ex.MessageText;
        }
        Check("forged-resolved-guc-does-not-change-catalog-actor",
            detail == "super|" + user.ToString("D"), detail);
    }

    static async Task IssuerSecurity(Admin.Api.Checks.IssuerHarness issuer, RsaSecurityKey sign, NpgsqlDataSource owner, NpgsqlDataSource admin, Admin.Api.AdminApiOptions opt, string issuerCs)
    {
        // Bind the actor proof before any logout in this method. Logout of Sa revokes
        // every live row and sets login_cutoff. A later open of that same user was the
        // resolve_grant miss: catalog._actor raised P0001 and the check did not catch it.
        await CatalogActorIgnoresForgedGuc(owner, Sa2);
        string Signed(Dictionary<string, object> claims) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(sign, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });
        var first = await IssuerSession(issuer.Url, Signed(Claims(Sa, "super", null)));
        var second = await IssuerSession(issuer.Url, Signed(Claims(Sa, "super", null)));
        var other = await IssuerSession(issuer.Url, Signed(Claims(Upa, "super", null)));
        var revoked = await PostRevoke(issuer.Url, issuer.CallerSecret, Issuer, Sa.ToString("D"));
        Check("logout-revokes-every-live-session",
            revoked == 200 && first.Length == 64 && second.Length == 64 && first != second
            && await Denied(admin, "SELECT auth.begin_request($1)", first)
            && await Denied(admin, "SELECT auth.begin_request($1)", second)
            && await BeginOk(admin, other),
            $"revoke {revoked}");
        // Seed cached Sa's token. That row is revoked. Drop it so a later bind mints again.
        _sessions.Remove(Sa);
        // iat is a whole second and revoked_before is clock_timestamp(). A JWT minted
        // later in the logout second is still at or before the cutoff. The next sign-in
        // in this suite waits for the following second.
        await Task.Delay(1100 - DateTimeOffset.UtcNow.Millisecond);
        var live = await IssuerSession(issuer.Url, Signed(Claims(Sa2, "super", null)));
        Check("unauthenticated-revoke-rejected",
            await PostRevoke(issuer.Url, null, Issuer, Sa2.ToString("D")) == 401 && await BeginOk(admin, live));
        Check("wrong-caller-secret-rejected",
            await PostRevoke(issuer.Url, "not-the-secret", Issuer, Sa2.ToString("D")) == 401 && await BeginOk(admin, live));
        Check("admin-api-cannot-revoke-login", await Denied(admin, "SELECT auth.revoke_login($1, $2)", Issuer, Sa.ToString("D")));
        Check("admin-api-cannot-revoke-session", await Denied(admin, "SELECT auth.revoke_session($1)", "not-a-session-token"));
        var oauth = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Login.StandIn", "OAuth.cs"));
        Check("sign-out-notifies-the-issuer", oauth.Contains("RequestSessionRevoker.NotifyAsync(", StringComparison.Ordinal));
        Check("non-loopback-listen-refused", await ListenRefusal("http://192.0.2.1:9", allow: false));
        Check("non-loopback-listen-allowed-when-opted-in", await ListenRefusal("http://192.0.2.1:9", allow: true));
        await using var issuerDs = NpgsqlDataSource.Create(issuerCs);
        var guardMessage = "";
        try { await Admin.Api.AdminApiHost.GuardRoleAsync(issuerDs); }
        catch (InvalidOperationException ex) { guardMessage = ex.Message; }
        Check("guard-refuses-the-issuer-role",
            guardMessage == "startup guard: the API role is the issuer role", guardMessage);
        await SessionStillOpenRules(owner);
        Check("admin-api-cannot-set-log-statement", await Denied(admin, "SET log_statement = 'all'"));
        Check("host-refuses-log-statement-all", await LoggingRefusal(opt, admin, "log_statement", "all"));
        Check("host-refuses-duration-zero-with-parameter-logging", await LoggingRefusal(opt, admin, "log_min_duration_statement", "0"));
        foreach (var user in new[] { Sa, Sa2, Upa })
            await RevokeLogin(user);
        await IssuerRejectsFutureIat(issuer, sign, owner);
        await RevokedTokenIsNotCached(issuer, sign, owner);
    }

    static async Task IssuerRejectsFutureIat(Admin.Api.Checks.IssuerHarness issuer, RsaSecurityKey sign, NpgsqlDataSource owner)
    {
        var user = Guid.NewGuid();
        await EnsureUser(owner, user);
        string Signed(int ahead)
        {
            var claims = Claims(user, "super", null);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            claims["iat"] = now + ahead;
            claims["nbf"] = now - 1;
            claims["exp"] = now + 600;
            return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Claims = claims,
                SigningCredentials = new SigningCredentials(sign, SecurityAlgorithms.RsaSha256),
                TokenType = "at+jwt",
            });
        }
        await WithWidenedIat(owner, async () =>
        {
            var plus5 = await IssuerStatus(issuer.Url, Signed(5));
            var plus60 = await IssuerStatus(issuer.Url, Signed(60));
            var live = await LiveSessions(owner, user);
            Check("issuer-rejects-iat-beyond-one-second", plus5 == 401 && plus60 == 401 && live == 0,
                $"plus5 {plus5} plus60 {plus60} live {live}");
        });
    }

    static async Task RevokedTokenIsNotCached(Admin.Api.Checks.IssuerHarness issuer, RsaSecurityKey sign, NpgsqlDataSource owner)
    {
        var user = Guid.NewGuid();
        await EnsureUser(owner, user);
        var jwt = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = Claims(user, "super", null),
            SigningCredentials = new SigningCredentials(sign, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });
        var first = await IssuerSession(issuer.Url, jwt);
        var revoked = await PostRevoke(issuer.Url, issuer.CallerSecret, Issuer, user.ToString("D"));
        var again = await IssuerStatus(issuer.Url, jwt);
        var live = await LiveSessions(owner, user);
        Check("revoked-token-is-not-served-from-the-cache",
            first.Length == 64 && revoked == 200 && again == 401 && live == 0,
            $"first {first.Length} revoke {revoked} again {again} live {live}");
    }

    static async Task WithWidenedIat(NpgsqlDataSource owner, Func<Task> body)
    {
        var db = await ScalarText(owner, "SELECT current_database()");
        var def = await ScalarText(owner, "SELECT pg_get_functiondef('auth.open_session(text,text,uuid,timestamptz,timestamptz)'::regprocedure)");
        if (!def.Contains("interval '1 second'", StringComparison.Ordinal))
        {
            await body();
            return;
        }
        var widened = ToReplace(def).Replace("interval '1 second'", "interval '2 minutes'", StringComparison.Ordinal);
        var path = Path.Combine(Path.GetTempPath(), "req064-widen-iat.sql");
        await File.WriteAllTextAsync(path, widened);
        SudoPsqlFile(db, path);
        try { await body(); }
        finally
        {
            await File.WriteAllTextAsync(path, ToReplace(def));
            SudoPsqlFile(db, path);
        }
    }

    static string ToReplace(string def)
    {
        var sql = def.Trim();
        if (!sql.StartsWith("CREATE OR REPLACE", StringComparison.Ordinal))
            sql = sql.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION");
        if (!sql.EndsWith(';')) sql += ";";
        return sql + "\n";
    }

    static void SudoPsqlFile(string database, string path)
    {
        var psi = new ProcessStartInfo("sudo", ["-u", "postgres", "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", database, "-f", path])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("psql did not start");
        var err = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("psql " + process.ExitCode + " " + err);
    }

    static async Task<string> ScalarText(NpgsqlDataSource ds, string sql)
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var value = await cmd.ExecuteScalarAsync();
        return value as string ?? "";
    }

    static async Task<string> ScalarOnText(NpgsqlConnection c, NpgsqlTransaction tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        var value = await cmd.ExecuteScalarAsync();
        return value is string s ? s : "";
    }

    static async Task<bool> BeginOk(NpgsqlDataSource ds, string token)
    {
        try
        {
            await using var c = await ds.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            await using var cmd = new NpgsqlCommand("SELECT auth.begin_request($1)", c, tx);
            cmd.Parameters.Add(new() { Value = token });
            await cmd.ExecuteNonQueryAsync();
            await tx.RollbackAsync();
            return true;
        }
        catch (PostgresException) { return false; }
    }

    static async Task<int> PostRevoke(string url, string? secret, string iss, string sub)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var msg = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/v1/revoke");
        if (secret is not null) msg.Headers.TryAddWithoutValidation("X-Caller-Secret", secret);
        msg.Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["iss"] = iss, ["sub"] = sub }), System.Text.Encoding.UTF8, "application/json");
        var res = await http.SendAsync(msg);
        return (int)res.StatusCode;
    }

    static async Task<bool> ListenRefusal(string prefix, bool allow)
    {
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Auth.Issuer", "bin", "Debug", "net10.0", "Auth.Issuer.dll"));
        var psi = new ProcessStartInfo("dotnet", [dll, prefix])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (allow) psi.Environment["ISSUER_ALLOW_NON_LOOPBACK"] = "1";
        psi.Environment.Remove("ISSUER_CONNECTION_STRING");
        psi.Environment.Remove("ISSUER_CALLER_SECRET");
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var refused = p.ExitCode == 2 && err.Contains("refusing non-loopback", StringComparison.Ordinal);
        return allow ? p.ExitCode == 2 && err.Contains("ISSUER_CONNECTION_STRING", StringComparison.Ordinal) && !refused : refused;
    }

    static async Task<bool> LoggingRefusal(Admin.Api.AdminApiOptions opt, NpgsqlDataSource admin, string guc, string value)
    {
        if (guc is not ("log_statement" or "log_min_duration_statement") || value is not ("all" or "0")) return false;
        string role;
        string database;
        await using (var c = await admin.OpenConnectionAsync())
        {
            role = (string)(await new NpgsqlCommand("SELECT current_user", c).ExecuteScalarAsync())!;
            database = (string)(await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync())!;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(role, "^[a-z0-9_]+\\z") || !System.Text.RegularExpressions.Regex.IsMatch(database, "^[a-z0-9_]+\\z"))
            return false;
        void Super(string sql)
        {
            var p = Process.Start(new ProcessStartInfo("sudo", ["-u", "postgres", "psql", "-d", database, "-v", "ON_ERROR_STOP=1", "-c", sql])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException("psql did not start");
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("superuser sql failed");
        }
        try
        {
            Super($"ALTER ROLE {role} SET {guc} = '{value}'");
            try
            {
                await using var app = await Admin.Api.AdminApiHost.BuildAsync(opt);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("startup guard:", StringComparison.Ordinal);
            }
        }
        finally
        {
            Super($"ALTER ROLE {role} RESET {guc}");
        }
    }
}
