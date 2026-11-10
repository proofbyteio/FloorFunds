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
        var opt = new Admin.Api.AdminApiOptions
        {
            ConnectionString = adminCs,
            EntraAuthority = "https://login.microsoftonline.com",
            EntraClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc",
            EntraSecretRef = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/req064-AbCdEf",
            SecretAccountIds = new HashSet<string> { "000000000000" },
            SecretRegions = new HashSet<string> { "us-west-2" },
            TokenIssuer = Issuer,
            SigningKeys = [pub],
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
                && write.Status == 403 && seeB.Status == 200,
                $"nav {navSa.Status} {Clip(navSa.Body)} held {held.Status} write {write.Status} seeB {seeB.Status} {Clip(seeB.Body)}");

            var before = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1", Apa);
            var after = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = NULL WHERE assignment_id = $1", Apa);
            Check("T6 revoke-takes-effect-on-next-request", before.Status == 200 && after.Status == 403,
                $"before {before.Status} {Clip(before.Body)} after {after.Status} {Clip(after.Body)}");

            var navCash = await Send(http, HttpMethod.Get, "/api/nav", cashTok);
            var cashLoc = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", cashTok);
            var cashScreen = await Send(http, HttpMethod.Get, "/api/screens/create-client", cashTok);
            Check("T7 cashier-empty-nav-every-endpoint-403",
                navCash.Status == 200 && Screens(navCash.Body).Count == 0 && cashLoc.Status == 403 && cashScreen.Status == 403);

            var hidden = await Send(http, HttpMethod.Get, "/api/screens/create-client", paTok);
            var hiddenPost = await Send(http, HttpMethod.Post, "/api/screens/create-client", paTok);
            Check("T9 hidden-create-client-direct-403", hidden.Status == 403 && hiddenPost.Status == 403);

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
            var mixGrant = await ScalarBool(owner, """
                SELECT outcome = 'grant' AND role_wire = 'partner_admin' AND NOT global_admin
                   AND partner_ids = ARRAY[$3]::uuid[] AND NOT ($4 = ANY (partner_ids))
                  FROM auth.resolve_grant($1, $2)
                """, Issuer, Mix, Pa, Pb);
            var mixScreens = mixNav.Status == 200 ? Screens(mixNav.Body) : new List<string>();
            Check("pa-at-a-cashier-at-b-keeps-each-role",
                mixGrant && mixA.Status == 200 && mixB.Status == 404 && mixClient.Status == 403
                && mixScreens.Contains("create_location") && mixScreens.Contains("config") && !mixScreens.Contains("create_client"),
                $"grant {mixGrant} A {mixA.Status} B {mixB.Status} nav {mixNav.Status} {Clip(mixNav.Body)} client {mixClient.Status}");
            Check("history-label-uses-the-record-partner", await HistoryLabel(owner));

            _ = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{La}/login-config", paTok);
            _ = await Send(http, HttpMethod.Get, $"/api/orgs/locations/{Lb}/login-config", saTok);
            var leaked = await PoolSample(app);
            Check("T11 pooled-connection-does-not-inherit-guc", leaked.Count == 0, string.Join(",", leaked));

            var forgedCount = await ScalarInTx(admin,
                "SELECT set_config('app.admin_type','super', true), set_config('app.resolved_global_admin','', true), set_config('app.resolved_partner_ids','', true)",
                "SELECT (SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1) + (SELECT count(*) FROM catalog.entry_change WHERE entry_id = $1)",
                Fee);
            var resolvedCount = await ScalarInTx(admin,
                "SELECT set_config('app.resolved_global_admin','on', true)",
                "SELECT count(*) FROM catalog.registered_entry WHERE entry_id = $1", Fee);
            Check("T12 admin-type-super-without-resolved-flag-sees-nothing", forgedCount == 0 && resolvedCount == 1, $"{forgedCount},{resolvedCount}");

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

            await app.StopAsync();
        }
        finally
        {
            await Cleanup(owner);
        }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
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
        await using (var set = new NpgsqlCommand("""
            SELECT set_config('app.resolved_global_admin', '', true),
                   set_config('app.resolved_partner_ids', $1, true),
                   set_config('app.resolved_location_ids', '', true),
                   set_config('app.admin_type', '', true),
                   set_config('app.partner_id', '', true)
            """, c, tx))
        {
            set.Parameters.Add(new() { Value = Pa.ToString("D") });
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
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task<string> Revoke(Guid id)
        {
            await using var c = await owner.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            if (Interlocked.Increment(ref started) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await using var cmd = new NpgsqlCommand("UPDATE auth.admin_assignment SET revoked_at = now() WHERE assignment_id = $1 AND revoked_at IS NULL", c, tx);
                cmd.Parameters.Add(new() { Value = id });
                var n = await cmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
                return n == 1 ? "ok" : "miss";
            }
            catch (PostgresException ex)
            {
                return ex.SqlState + ":" + ex.MessageText;
            }
        }
        var results = await Task.WhenAll(Revoke(Asa), Revoke(Asa2));
        var oks = results.Count(r => r == "ok");
        var fails = results.Count(r => r == "P0001:last_active_super_admin");
        Check("last-active-sa-concurrency", oks == 1 && fails == 1, string.Join(" | ", results));
        await Exec(owner, "UPDATE auth.admin_assignment SET revoked_at = NULL WHERE assignment_id = ANY($1)", new[] { Asa, Asa2 });
    }

    static async Task<bool> HistoryLabel(NpgsqlDataSource owner)
    {
        // Both is Partner Admin at Pa and at Pb. The actor list is ordered Pa then Pb,
        // so the old label (the first partner) would store Pa on a row owned by Pb.
        await using var c = await owner.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var hist = Guid.Parse("5f0c3e1a-0064-4000-8000-000000000042");
        try
        {
            await using (var grant = new NpgsqlCommand("""
                SELECT (SELECT count(*) FROM unnest(partner_ids) x WHERE x = $3) = 1
                   AND (SELECT count(*) FROM unnest(partner_ids) x WHERE x = $4) = 1
                   AND role_wire = 'partner_admin' AND NOT global_admin
                  FROM auth.resolve_grant($1, $2)
                """, c, tx))
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
        await ExecQuiet(owner, "DELETE FROM auth.location_login_config_change WHERE location_id = ANY($1)", new[] { La, Lb });
        await ExecQuiet(owner, "DELETE FROM auth.location_login_config WHERE location_id = ANY($1)", new[] { La, Lb });
        await ExecQuiet(owner, "DELETE FROM auth.location WHERE location_id = ANY($1)", new[] { La, Lb });
        await ExecQuiet(owner, "DELETE FROM auth.admin_assignment WHERE user_id = ANY($1) OR assignment_id = ANY($2)",
            new[] { Sa, Sa2, Upa, Both, None, Cash, Mix, Guid.Parse("5f0c3e1a-0064-4000-8000-000000000037") },
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
                       coalesce(current_setting('app.partner_id', true), '')
                """, c);
            var v = (string)(await cmd.ExecuteScalarAsync())!;
            if (v != "||||") leaked.Add(v);
        }
        foreach (var c in conns) await c.DisposeAsync();
        return leaked;
    }

    static string Mint(RsaSecurityKey key, Guid user, string adminType, Guid? partner) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = Claims(user, adminType, partner),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            TokenType = "at+jwt",
        });

    static Dictionary<string, object> Claims(Guid user, string adminType, Guid? partner)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = Issuer, ["aud"] = Admin.Api.AdminApiOptions.Audience, ["sub"] = user.ToString("D"),
            ["admin_type"] = adminType, ["iat"] = now, ["nbf"] = now, ["exp"] = now + 600,
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
