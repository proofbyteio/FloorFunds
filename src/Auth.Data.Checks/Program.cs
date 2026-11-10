// S2a P2 DB integration checks (spec §8-§10). Runs against a migrated, seeded TEST DB named in AUTH_CHECKS_DB (TLD 8:20 P2-2).
// env: AUTH_CHECKS_DB (target DB name), CONNECTION_STRING (owner), ADMIN_API_CONNECTION_STRING, LOGIN_CONNECTION_STRING;
// arg 0: fixtures.json path.
// Test data below is SSE2-FAKE-TEST-ONLY (fake tenant ids, account 000000000000 ARNs). Never prints connection strings.
using System.Text.Json;
using Auth.Data;
using Auth.Data.Entities;
using Auth.Data.Readers;
using Login.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

int pass = 0, fail = 0;
void Ok(string name, bool cond, string? detail = null)
{
    if (cond) { pass++; Console.WriteLine($"PASS {name}"); }
    else { fail++; Console.WriteLine($"FAIL {name}{(detail is null ? "" : " :: " + detail)}"); }
}
async Task Check(string name, Func<Task<bool>> f)
{
    try { Ok(name, await f()); } catch (Exception e) { Ok(name, false, $"{e.GetType().Name}: {e.Message}"); }
}
async Task Throws<T>(string name, Func<Task> f, Func<T, bool>? pred = null) where T : Exception
{
    try { await f(); Ok(name, false, "no exception"); }
    catch (T e) when (pred is null || pred(e)) { Ok(name, true); }
    catch (Exception e) { Ok(name, false, $"{e.GetType().Name}: {e.Message}"); }
}

// [TLD 2026-11-04 11:21 Q2b] [TLD 2026-11-04 11:57 o1-o3] [TLD 2026-11-04 12:04 F1] scratch-DB self-test modes (verify
// script; not part of the counted DB run, which k1 pins at 162):
//   --scratch-info       print host / port / maintenance DB / SHOW server_version / current_user (no connection string)
//   --scratch-crash [--backdate-hours N]   create + mark + migrate a scratch DB (runid backdated N h), print its name, then
//                        exit 70 WITHOUT the finally (simulated crash)
//   --scratch-hold <name> <seconds>        hold one open connection to a scratch DB (live-connection case), then exit 0
//   --scratch-decoy-create / --scratch-decoy-drop <name>   an UNMARKED ff_scratch_decoy* DB for the o2 cases (exact-name drop)
//   --scratch-drop <name>  DropAsync one MARKED scratch DB (verify-script cleanup; refuses unmarked / not ours) [SSE2 09e31f68
//                        M2]: exit 2 with a clear message (no crash) when it has connections (pre-check, or SQLSTATE 55006);
//                        prints "nothing to drop" (exit 0) when neither the DB nor a marked role existed
//   --scratch-sweep      the start-up sweep alone; exit 3 when an unmarked ff_scratch_* DB / role, or [SSE2 09e31f68 L1] a
//                        marked one with a missing, invalid or future runid, is found (o2)
// [SSE2 09e31f68 M3] the main run, --scratch-selftest and --scratch-sweep first take the single-runner advisory lock
// (ScratchLock, key 7378711910451082339) on the kit target DB connection (CONNECTION_STRING); a second concurrent run exits 5
// at once with "another scratch run holds the lock". [TLD 2026-11-04 13:54 item 1] A lock target outside ff_sse / ff_sse2 /
// ff_kit_* exits 2 with "FATAL scratch-lock: refuse: ..." (ScratchLock.Refusal, no connection, no value echoed), never rc 134. The CLI has no "own DB" concept: --scratch-drop drops any MARKED,
// ff_scratchadm-owned scratch DB by name, not only one this process made (SSE2 09e31f68 Info).
//   --scratch-snapshot   print the o3 cluster snapshot (roles, memberships, tablespaces, databases, role settings)
//   --scratch-selftest <old> <young> <live>  guards, loopback / port / maint-DB rules, o1 sweep cases, o2 decoy, o3 snapshot,
//                        [TLD 12:33] role sweep cases, ff_scratchadm attribute / membership check, maint-guard refusals,
//                        [TLD 13:15 KF-A] SCRAM verifier, cleartext-free CREATE ROLE, SCRAM sign-in
//   --scratch-lookalike-create             [TLD 12:33] an UNMARKED ff_scratch_<old runid>_admin_api role (look-alike case)
//   --scratch-cleanup-role <name>          [TLD 12:33] exact-name cleanup of an UNMARKED ff_scratch_* role made on purpose
//                                          (or leaked by mutation a): no sessions -> marked, then dropped via the marked path
//   --scratch-admincheck  [TLD 12:33] print the ff_scratchadm row, its memberships and any violation; exit 4 on a violation
// Every maintenance-DB statement goes through MaintConn / MaintSql (allowlist, TLD 12:33 item 3); none is issued raw here.
// [SSE2 04dba71d Low] every --scratch-* mode validates the admin target first: a refused connection-string key (Options,
// Passfile, SSL Mode, Target Session Attributes ...), a non-loopback host, a bad port / maintenance DB or an unset variable
// exits 2 with the refusal message (it names the key, never a value) instead of an unhandled exception (rc 134).
if (args.Length >= 1 && args[0].StartsWith("--scratch-", StringComparison.Ordinal))
{
    try { ScratchDb.AdminCs(); }
    catch (InvalidOperationException e) { Console.Error.WriteLine($"FATAL scratch: {e.Message}"); return 2; }
}
if (args is ["--scratch-info"])
{
    var (h, p, m) = ScratchDb.Target();
    var acs = ScratchDb.AdminCs();
    await using var c = await ScratchDb.OpenMaint(acs);
    var r = await c.RowAsync(MaintSql.Info);
    Console.WriteLine($"scratch-info: host={h} port={p} maint_db={m} (connected db={r[2]}) server_version={r[0]} user={r[1]}");
    return 0;
}
if (args.Length >= 1 && args[0] == "--scratch-crash")
{
    var hours = args is [_, "--backdate-hours", var hs] ? double.Parse(hs, System.Globalization.CultureInfo.InvariantCulture) : 0;
    var acs = ScratchDb.AdminCs();
    var n = await ScratchDb.CreateMarkedAsync(acs, ScratchDb.NewName(TimeSpan.FromHours(hours)));
    try
    {
        await ScratchDb.PrepareAsync(acs, n);
        Console.WriteLine($"scratch-crash: created {n}; exiting 70 without the finally");
        Environment.Exit(70);
    }
    finally { await ScratchDb.DropAsync(acs, n); }
}
if (args is ["--scratch-hold", var holdName, var holdSecs])
{
    var acs = ScratchDb.AdminCs();
    await using var c = new NpgsqlConnection(ScratchDb.OwnerCs(acs, holdName));
    await c.OpenAsync();
    Console.WriteLine($"scratch-hold: connected to {holdName}");
    await Task.Delay(TimeSpan.FromSeconds(int.Parse(holdSecs, System.Globalization.CultureInfo.InvariantCulture)));
    return 0;
}
if (args is ["--scratch-decoy-create"])
{
    var acs = ScratchDb.AdminCs();
    var decoy = "ff_scratch_decoy" + Guid.NewGuid().ToString("N")[..8];
    ScratchDb.GuardName(decoy);
    await ScratchDb.CreateUnmarkedAsync(acs, decoy, role: false); // deliberately UNMARKED
    Console.WriteLine($"scratch-decoy: created {decoy} (unmarked)");
    return 0;
}
if (args is ["--scratch-decoy-drop", var dropDecoy])
{
    // Exact name we created, ff_scratch_decoy prefix, still unmarked, owned by us, no connections: marked, then dropped
    // through the marked-only path (the maint guard never drops an unmarked DB) [TLD 12:33].
    var acs = ScratchDb.AdminCs();
    ScratchDb.GuardName(dropDecoy);
    var msg = await ScratchDb.CleanupUnmarkedAsync(acs, dropDecoy, role: false, prefix: "ff_scratch_decoy");
    Console.WriteLine($"scratch-decoy: {msg}");
    return msg.StartsWith("refuse", StringComparison.Ordinal) ? 2 : 0;
}
if (args is ["--scratch-lookalike-create"])
{
    var la = ScratchDb.NewName(TimeSpan.FromHours(2)) + "_admin_api";
    await ScratchDb.CreateUnmarkedAsync(ScratchDb.AdminCs(), la, role: true);
    Console.WriteLine($"scratch-lookalike: created {la} (unmarked role)");
    return 0;
}
if (args is ["--scratch-cleanup-role", var cleanRole])
{
    var msg = await ScratchDb.CleanupUnmarkedAsync(ScratchDb.AdminCs(), cleanRole, role: true, prefix: "ff_scratch_");
    Console.WriteLine($"scratch-cleanup-role: {msg}");
    return msg.StartsWith("refuse", StringComparison.Ordinal) ? 2 : 0;
}
if (args is ["--scratch-admincheck"])
{
    var acs = ScratchDb.AdminCs();
    await using (var m = await ScratchDb.OpenMaint(acs))
    {
        Console.WriteLine($"scratch-admincheck: row {await m.ScalarAsync(MaintSql.AdminRole)}");
        foreach (var l in await m.ColumnAsync(MaintSql.AdminMembers)) Console.WriteLine($"scratch-admincheck: membership {l}");
    }
    var v = await ScratchDb.AdminRoleViolationsAsync(acs);
    foreach (var x in v) Console.WriteLine($"FATAL scratch-admincheck: {x}");
    Console.WriteLine($"scratch-admincheck: {(v.Count == 0 ? "ok" : v.Count + " violation(s)")}");
    return v.Count == 0 ? 0 : 4;
}
if (args is ["--scratch-drop", var dropMarked])
{
    var (rc, msg) = await ScratchDb.DropCliAsync(ScratchDb.AdminCs(), dropMarked); // marked + ours + unconnected only
    Console.WriteLine(msg);
    return rc;
}
if (args is ["--scratch-sweep"])
{
    var kitCs = Environment.GetEnvironmentVariable("CONNECTION_STRING") ?? throw new InvalidOperationException("CONNECTION_STRING (kit target DB, for the scratch lock) not set");
    if (ScratchLock.Refusal(kitCs) is { } lockRefused) { Console.Error.WriteLine($"FATAL scratch-lock: {lockRefused}"); return 2; } // [TLD 13:54 item 1]
    await using var lk = await ScratchLock.TryAcquireAsync(kitCs);
    if (lk is null) { Console.WriteLine(ScratchLock.BusyMessage(kitCs)); return 5; }
    var res = await ScratchDb.SweepAsync(ScratchDb.AdminCs(), currentName: null);
    return res.Unmarked.Count + res.UnmarkedRoles.Count + res.BadRunId.Count > 0 ? 3 : 0;
}
if (args is ["--scratch-snapshot"])
{
    Console.Write(await ScratchDb.ClusterSnapshotAsync(ScratchDb.AdminCs()));
    return 0;
}
if (args is ["--scratch-selftest", var crashedOld, var crashedYoung, var crashedLive])
{
    var kitCs = Environment.GetEnvironmentVariable("CONNECTION_STRING") ?? throw new InvalidOperationException("CONNECTION_STRING (kit target DB, for the scratch lock) not set");
    if (ScratchLock.Refusal(kitCs) is { } lockRefused) { Console.Error.WriteLine($"FATAL scratch-lock: {lockRefused}"); return 2; } // [TLD 13:54 item 1]
    await using var lk = await ScratchLock.TryAcquireAsync(kitCs);
    if (lk is null) { Console.WriteLine(ScratchLock.BusyMessage(kitCs)); return 5; }
    var acs = ScratchDb.AdminCs();
    // [SSE2 09e31f68 F4] every env override below goes through WithEnv: the previous value is restored (not nulled) and the
    // three SCRATCH_* values are compared with the start-of-test snapshot after each block.
    string[] envKeys = ["SCRATCH_PG_HOST", "SCRATCH_PG_PORT", "SCRATCH_MAINT_DB"];
    string EnvSnap() => string.Join(" ", envKeys.Select(k => $"{k}={Environment.GetEnvironmentVariable(k) ?? "<unset>"}"));
    var env0 = EnvSnap(); int envBlocks = 0, envDrift = 0;
    string WithEnv(string k, string v, Func<string> f)
    {
        var old = Environment.GetEnvironmentVariable(k);
        Environment.SetEnvironmentVariable(k, v);
        try { return f(); }
        finally { Environment.SetEnvironmentVariable(k, old); envBlocks++; if (EnvSnap() != env0) envDrift++; }
    }
    Console.WriteLine($"INFO F4 env at start: {env0}");
    // [SSE2 09e31f68 M3] single-runner lock: held by this process; a second try on another connection is refused at once.
    await Check($"M3 lock: a second pg_try_advisory_lock({ScratchLock.Key}) on the kit DB is refused while this run holds it [SSE2 09e31f68]", async () =>
    {
        await using var second = await ScratchLock.TryAcquireAsync(kitCs);
        return second is null && ScratchLock.BusyMessage(kitCs).Contains(ScratchLock.Busy);
    });
    foreach (var notKit in new[] { "postgres", "template1", "ff_scratch_abcdef123456" })
        await Throws<InvalidOperationException>($"M3 lock: refused on a non-kit DB ({notKit}), never the maintenance DB", () => ScratchLock.TryAcquireAsync(new NpgsqlConnectionStringBuilder(kitCs) { Database = notKit }.ConnectionString), e => e.Message.Contains("kit target DB"));
    Ok("13:54 lock pre-check: ScratchLock.Refusal gives the refusal (no name echoed) for postgres / ff_sse3 / ff_kit_ + 21 chars, null for ff_sse / ff_sse2 / ff_kit_x [TLD 13:54 item 1]",
        new[] { "postgres", "ff_sse3", "ff_kit_" + new string('a', 21) }.All(d => ScratchLock.Refusal(new NpgsqlConnectionStringBuilder(kitCs) { Database = d }.ConnectionString) is { } r && r == ScratchLock.NotKit && !r.Contains(d))
        && new[] { "ff_sse", "ff_sse2", "ff_kit_x" }.All(d => ScratchLock.Refusal(new NpgsqlConnectionStringBuilder(kitCs) { Database = d }.ConnectionString) is null));
    Ok("scratch: FindOnConnectionAsync is not public (internal, no InternalsVisibleTo) [TLD 11:21]",
        typeof(NpgsqlExternalLoginReader).GetMethod("FindOnConnectionAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is null
        && typeof(NpgsqlExternalLoginReader).GetMethod("FindOnConnectionAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) is { IsAssembly: true }
        && !typeof(NpgsqlExternalLoginReader).Assembly.GetCustomAttributes(typeof(System.Runtime.CompilerServices.InternalsVisibleToAttribute), false).Any());
    foreach (var bad in new[] { "ff_sse", "ff_sse2", "postgres", "template0", "template1", "ff_maint_scratch", "FF_SSE", "ff_scratch_", "ff_scratch_ab", "ff_scratch_ABCDEF12", "ff_scratch_abc-def12", "ff_kit_ssee1a" })
    {
        await Throws<InvalidOperationException>($"scratch guard: refuses '{bad}' (create, drop, owner cs)", async () => { ScratchDb.GuardName(bad); await Task.CompletedTask; });
        await Throws<InvalidOperationException>($"scratch guard: DropAsync('{bad}') refuses before touching the server", () => ScratchDb.DropAsync(acs, bad));
    }
    // [TLD 12:04] loopback-only host, refused by pure validation before any connection (ScratchDb.Opens must not move).
    var rawCs = Environment.GetEnvironmentVariable("SCRATCH_ADMIN_CONNECTION_STRING")!;
    var opens0 = ScratchDb.Opens;
    foreach (var badHost in new[] { "10.0.0.1", "db.example.invalid", "localhost", "/var/run/postgresql", "127.0.0.2", "::ffff:127.0.0.1", "127.0.0.1,10.0.0.1", "0.0.0.0" })
    {
        await Throws<InvalidOperationException>($"F1 loopback: SCRATCH_PG_HOST={badHost} -> refused, no connection [TLD 12:04]", async () =>
        {
            WithEnv("SCRATCH_PG_HOST", badHost, ScratchDb.AdminCs);
            await Task.CompletedTask;
        }, e => e.Message.Contains("loopback"));
        await Throws<InvalidOperationException>($"F1 loopback: connection string Host={badHost} -> refused, no connection [TLD 12:04]", async () =>
        { ScratchDb.ValidateAdminCs(new NpgsqlConnectionStringBuilder(rawCs) { Host = badHost }.ConnectionString); await Task.CompletedTask; }, e => e.Message.Contains("loopback"));
    }
    Ok("F1 loopback: no maintenance connection attempted by any refusal (ScratchDb.Opens unchanged)", ScratchDb.Opens == opens0);
    Ok("F1: ::1 accepted as a loopback host (validation only)", ScratchDb.ValidateAdminCs(new NpgsqlConnectionStringBuilder(rawCs) { Host = "::1" }.ConnectionString, "::1") is { Length: > 0 });
    foreach (var badDb in new[] { "ff_sse", "ff_sse2", "template0", "template1", "ff_scratch_abcdef123456", "Postgres;x" })
        await Throws<InvalidOperationException>($"F1: SCRATCH_MAINT_DB={badDb} -> refused", async () =>
        {
            WithEnv("SCRATCH_MAINT_DB", badDb, ScratchDb.AdminCs);
            await Task.CompletedTask;
        }, e => e.Message.Contains("maintenance DB"));
    {
        var (th, tp, tm) = ScratchDb.Target();
        var ab = new NpgsqlConnectionStringBuilder(acs);
        bool unset = new[] { "SCRATCH_PG_HOST", "SCRATCH_PG_PORT", "SCRATCH_MAINT_DB" }.All(k => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k)));
        Ok($"F1: effective target host={th} port={tp} maint_db={tm} is what the connection uses{(unset ? " (env unset -> defaults 127.0.0.1 / 5432 / postgres)" : "")}",
            ab.Host == th && ab.Port == tp && ab.Database == tm && (!unset || (th, tp, tm) == ("127.0.0.1", 5432, "postgres")));
    }
    // Port override honoured: an unused loopback port -> connection refused; the default port works. No second cluster.
    int freePort;
    { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); freePort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
    await Check($"F1 port: SCRATCH_PG_PORT={freePort} (unused) -> connection refused on 127.0.0.1:{freePort}", async () =>
    {
        var cs = WithEnv("SCRATCH_PG_PORT", freePort.ToString(System.Globalization.CultureInfo.InvariantCulture), ScratchDb.AdminCs);
        if (new NpgsqlConnectionStringBuilder(cs).Port != freePort) return false;
        try { await ScratchDb.StateAsync(cs, "ff_scratch_portprobe1"); return false; }
        catch (NpgsqlException e) when (e.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }) { return true; }
    });
    await Check("F1 port: the default/configured port works (StateAsync on a missing name -> (false,false))", async () => (await ScratchDb.StateAsync(acs, "ff_scratch_portprobe1")) == (false, false));
    await Throws<InvalidOperationException>("F1: SCRATCH_PG_PORT=abc -> refused", async () =>
    {
        WithEnv("SCRATCH_PG_PORT", "abc", ScratchDb.AdminCs);
        await Task.CompletedTask;
    });
    Ok($"F4: SCRATCH_PG_HOST / PORT / MAINT_DB unchanged after each of the {envBlocks} override blocks ({EnvSnap()}) and AdminCs() still gives the same target [SSE2 09e31f68]",
        envBlocks >= 16 && envDrift == 0 && EnvSnap() == env0 && ScratchDb.AdminCs() == acs, $"blocks={envBlocks} drift={envDrift}");
    // [SSE2 09e31f68 L2] names / DDL anchored with \z: a trailing newline is refused (guard, DropAsync, maint-guard classify).
    var nl = "ff_scratch_abcdef\n";
    await Throws<InvalidOperationException>("L2: GuardName refuses 'ff_scratch_abcdef\\n' (\\z anchor, no trailing newline)", async () => { ScratchDb.GuardName(nl); await Task.CompletedTask; });
    await Throws<InvalidOperationException>("L2: DropAsync('ff_scratch_abcdef\\n') refuses before touching the server", () => ScratchDb.DropAsync(acs, nl));
    Ok("L2: maint-guard does not classify DDL with a trailing newline (DROP DATABASE / DROP ROLE / CREATE DATABASE + \\n)",
        MaintSql.Classify("DROP DATABASE \"ff_scratch_abcdef\"\n").kind is null && MaintSql.Classify("DROP ROLE \"ff_scratch_abcdef_login\"\n").kind is null
        && MaintSql.Classify("CREATE DATABASE \"ff_scratch_abcdef\" TEMPLATE template0 ENCODING 'UTF8'\n").kind is null
        && MaintSql.Classify("DROP DATABASE \"ff_scratch_abcdef\"").kind == "drop-db");
    // [SSE2 09e31f68 Info] admin connection-string keys outside the allowlist are refused by pure validation.
    var opens1 = ScratchDb.Opens;
    foreach (var (key, val) in new[] { ("Passfile", "/tmp/x.pgpass"), ("Options", "-c role=postgres"), ("Target Session Attributes", "any"), ("Include Error Detail", "true"), ("SSL Mode", "Disable") })
        await Throws<InvalidOperationException>($"Info: admin connection-string key '{key}' refused (allowlist Host/Port/Database/Username/Password/Pooling)", async () =>
        { ScratchDb.ValidateAdminCs(new System.Data.Common.DbConnectionStringBuilder { ConnectionString = rawCs, [key] = val }.ConnectionString); await Task.CompletedTask; },
        e => e.Message.Contains($"key '{key}'", StringComparison.OrdinalIgnoreCase) && !e.Message.Contains(val));
    Ok("Info: no connection attempted by the key refusals; the env admin connection string passes", ScratchDb.Opens == opens1 && ScratchDb.ValidateAdminCs(rawCs) is { Length: > 0 });
    // o1 sweep cases.
    Ok("o1: runid carries a UTC timestamp (NewName -> RunIdUtc within 5 s of now)", ScratchDb.RunIdUtc(ScratchDb.NewName()) is { } ts && Math.Abs((DateTime.UtcNow - ts).TotalSeconds) < 5);
    await Check($"scratch crash: crashed runs left their MARKED DBs behind ({crashedOld} old, {crashedYoung} young, {crashedLive} old + live connection)", async () =>
        (await ScratchDb.StateAsync(acs, crashedOld)) == (true, true) && (await ScratchDb.StateAsync(acs, crashedYoung)) == (true, true) && (await ScratchDb.StateAsync(acs, crashedLive)) == (true, true));
    Ok("o1: backdated runids parse as > 1 h old, the young one as <= 1 h", ScratchDb.RunIdUtc(crashedOld) is { } to && DateTime.UtcNow - to > ScratchDb.MinAge
        && ScratchDb.RunIdUtc(crashedLive) is { } tl && DateTime.UtcNow - tl > ScratchDb.MinAge && ScratchDb.RunIdUtc(crashedYoung) is { } ty && DateTime.UtcNow - ty <= ScratchDb.MinAge);
    await Check($"o1: {crashedLive} has a live connection in pg_stat_activity (held by --scratch-hold)", async () => await ScratchDb.ConnectionsAsync(acs, crashedLive) > 0);
    // [TLD 2026-11-04 13:15, KF-A] a role password reaches the server only as a client-side SCRAM-SHA-256 verifier; random per
    // run, in memory only. Runs before this self-test creates anything, so a refusal (mutation m1) stops it with nothing leaked.
    const string rfcVector = "SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==$WG5d8oPm3OtcPnkdi4Uo7BkeZkBFzpcXkuLmtbsT4qY=:wfPLwcE6nTWhTAmQ7tl2KeoiWGPlZqQxSrmfPwDl2dU=";
    var vre = new System.Text.RegularExpressions.Regex("^" + MaintSql.ScramVerifierRe + "\\z");
    Ok($"KF-A: ScramVerifier('pencil', RFC 7677 salt) == {rfcVector} (RFC 5802/7677 derivation; same value as the kit's db/setup.sh scram())",
        ScratchDb.ScramVerifier("pencil", Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==")) == rfcVector);
    var pwA = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    var (vA1, vA2) = (ScratchDb.ScramVerifier(pwA), ScratchDb.ScramVerifier(pwA));
    Ok("KF-A: a verifier has the exact guard shape, never contains the password, and a fresh 16-byte salt each call", vre.IsMatch(vA1) && vre.IsMatch(vA2) && vA1 != vA2 && !vA1.Contains(pwA) && !vA2.Contains(pwA));
    foreach (var (badPw, label) in new[] { ("", "an empty"), ("caf\u00e9", "a non-ASCII"), ("a\nb", "a newline") })
        await Throws<ArgumentException>($"KF-A: ScramVerifier refuses {label} password (printable ASCII only, as the kit)", async () => { ScratchDb.ScramVerifier(badPw); await Task.CompletedTask; });
    var kfaRole = ScratchDb.NewName() + "_login";
    var kfaShape = $"CREATE ROLE \"{kfaRole}\" LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD ";
    Ok("KF-A maint-guard: CREATE ROLE ... PASSWORD '<SCRAM-SHA-256$4096 verifier>' is allowlisted", MaintSql.Classify(kfaShape + $"'{vA1}'").kind == "create-role");
    Ok("KF-A maint-guard: CREATE ROLE ... PASSWORD '<32 hex cleartext>' is refused [TLD 13:15]", MaintSql.Classify(kfaShape + $"'{pwA}'").kind is null);
    foreach (var (badV, label) in new[] { ("md5" + new string('a', 32), "an md5 hash"), (vA1.Replace("$4096:", "$4095:"), "a verifier with 4095 iterations"), ("abc123", "a short cleartext"),
                                          (vA1 + "' SUPERUSER '", "a verifier + quote injection"), (vA1[..^1], "a truncated verifier"), ("scram-sha-256" + vA1[13..], "a lower-case prefix") })
        Ok($"KF-A maint-guard: CREATE ROLE ... PASSWORD with {label} is refused", MaintSql.Classify(kfaShape + $"'{badV}'").kind is null);
    var kfaPw = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // memory only
    var kfaSent = new List<string>(); List<string> kfaSample = [];
    var kfaCreated = false; var kfaOn = true;
    // [SSE2 04dba71d KF-A] every maintenance statement of the whole self-test is captured (allSent); at the end every
    // PASSWORD in it must be followed by 'SCRAM-SHA-256$4096:' (same rule SSE2 applies to the log_statement=all server log).
    var allSent = new List<string>();
    MaintConn.Trace = q => { lock (allSent) { allSent.Add(q); if (kfaOn) kfaSent.Add(q); } };
    try
    {
        try { await ScratchDb.CreateMarkedRoleAsync(acs, kfaRole, kfaPw, afterCreate: async () => kfaSample = await ScratchDb.OwnSessionQueriesAsync(acs)); kfaCreated = true; }
        catch (InvalidOperationException e) when (e.Message.Contains("maint-guard")) { Console.WriteLine($"INFO KF-A: {e.Message}"); }
        finally { kfaOn = false; }
        Ok($"KF-A: CreateMarkedRoleAsync({kfaRole}, password) passes the maint guard", kfaCreated);
        if (kfaCreated)
        {
            var creates = kfaSent.Where(q => q.StartsWith("CREATE ROLE", StringComparison.Ordinal)).ToList();
            Ok("KF-A (b) statement text: exactly one CREATE ROLE was sent and its PASSWORD is a SCRAM-SHA-256$4096 verifier",
                creates is [var c1] && c1.StartsWith(kfaShape, StringComparison.Ordinal) && vre.IsMatch(c1[(kfaShape.Length + 1)..^1]), $"{creates.Count} CREATE ROLE");
            Ok($"KF-A (b) statement text: the cleartext password is in none of the {kfaSent.Count} statements sent on maintenance connections", kfaSent.Count >= 3 && kfaSent.All(q => !q.Contains(kfaPw)));
            Ok($"KF-A (b) pg_stat_activity sampled during the CREATE ({kfaSample.Count} other ff_scratchadm session(s)): the creating session's query is the CREATE ROLE with the verifier; the cleartext is in no query",
                kfaSample.Any(q => q.StartsWith(kfaShape + "'SCRAM-SHA-256$4096:", StringComparison.Ordinal)) && kfaSample.All(q => !q.Contains(kfaPw)));
            Ok("KF-A (b) [SSE2 04dba71d]: after every PASSWORD in the statement text and in the pg_stat_activity sample comes 'SCRAM-SHA-256$4096:' (or there is no PASSWORD)",
                kfaSent.Concat(kfaSample).All(q => !MaintSql.PwNotVerifier.IsMatch(q)));
            var pol = (await ScratchDb.LogPolicyAsync(acs)).Split('|');
            Console.WriteLine($"INFO KF-A server: log_statement={pol[0]} password_encryption={pol[1]}; ff_scratchadm may SET log_statement: {pol[2]}; may read pg_authid: {pol[3]}"
                + (pol[2] == "false" && pol[0] != "all" ? " -> log_statement=all not settable (superuser-only GUC, no SET grant); statement-text capture + pg_stat_activity sample used instead [TLD 13:15 (b)]" : ""));
            Console.WriteLine("INFO KF-A (c): pg_authid.rolpassword is read by the verify script (superuser, read-only, first 14 chars only) while this role is held; not here");
            var signIn = new NpgsqlConnectionStringBuilder(acs) { Username = kfaRole, Password = kfaPw, Database = crashedYoung, Pooling = false, RequireAuth = "ScramSHA256" };
            await Check("KF-A (a): the role signs in with the cleartext password, Require Auth=ScramSHA256 (SCRAM only) -> the stored verifier is right", async () =>
            {
                await using var kc = new NpgsqlConnection(signIn.ConnectionString);
                await kc.OpenAsync();
                await using var cmd = new NpgsqlCommand("SELECT current_user", kc);
                var who = (string?)await cmd.ExecuteScalarAsync() == kfaRole;
                if (Environment.GetEnvironmentVariable("SCRATCH_KFA_HOLD_FILE") is { Length: > 0 } hold)
                { // verify script only: it reads pg_authid.rolpassword (prefix) for this role while the file exists (60 s max)
                    File.WriteAllText(hold, kfaRole + "\n");
                    for (var i = 0; i < 600 && File.Exists(hold); i++) await Task.Delay(100);
                }
                return who;
            });
            await Throws<PostgresException>("KF-A (a): a wrong password is refused (28P01)", async () =>
            {
                await using var kc = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(signIn.ConnectionString) { Password = "wrong-" + kfaPw[..8] }.ConnectionString);
                await kc.OpenAsync();
            }, e => e.SqlState == "28P01");
        }
    }
    finally
    {
        for (var i = 0; i < 50 && await ScratchDb.RoleSessionsAsync(acs, kfaRole) > 0; i++) await Task.Delay(100);
        if (kfaCreated) await ScratchDb.DropAsync(acs, kfaRole[..^"_login".Length]); // marked, no DB: drops the marked role
    }
    Ok($"KF-A: {kfaRole} dropped again", !(await ScratchDb.RolesAsync(acs)).Any(x => x.role == kfaRole));
    if (!kfaCreated)
    {
        Console.WriteLine("KF-A: stopping the self-test here (nothing else was created yet)");
        Console.WriteLine($"\n{pass} passed, {fail} failed");
        return 1;
    }
    var own = await ScratchDb.CreateMarkedAsync(acs, ScratchDb.NewName(TimeSpan.FromHours(3))); // this run's own DB, backdated 3 h, no connection
    var decoy = "ff_scratch_decoy" + Guid.NewGuid().ToString("N")[..8];
    ScratchDb.GuardName(decoy);
    await ScratchDb.CreateUnmarkedAsync(acs, decoy, role: false); // deliberately UNMARKED
    await Check($"scratch decoy: unmarked {decoy} created (no marker)", async () => (await ScratchDb.StateAsync(acs, decoy)) == (true, false));
    await Throws<InvalidOperationException>("scratch: DropAsync refuses the unmarked decoy", () => ScratchDb.DropAsync(acs, decoy), e => e.Message.Contains("marker"));
    await Throws<InvalidOperationException>("scratch: CreateMarkedAsync refuses an existing name", () => ScratchDb.CreateMarkedAsync(acs, decoy), e => e.Message.Contains("already exists"));
    // [TLD 12:33 item 1] role cases: stand-alone MARKED per-run-shaped roles (no DB): stale, young, stale + a live session as
    // it; and an UNMARKED look-alike. The session role gets a LOGIN password that exists only in this process's memory.
    var staleRole = ScratchDb.NewName(TimeSpan.FromHours(2)) + "_login";
    var youngRole = ScratchDb.NewName() + "_login";
    var sessRole = ScratchDb.NewName(TimeSpan.FromHours(2)) + "_login";
    var lookalike = ScratchDb.NewName(TimeSpan.FromHours(2)) + "_admin_api";
    await ScratchDb.CreateMarkedRoleAsync(acs, staleRole);
    await ScratchDb.CreateMarkedRoleAsync(acs, youngRole);
    var sessPw = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    List<string> sessSample = [];
    await ScratchDb.CreateMarkedRoleAsync(acs, sessRole, sessPw, afterCreate: async () => sessSample = await ScratchDb.OwnSessionQueriesAsync(acs));
    Ok($"KF-A (b) [TLD 13:15, SSE2 04dba71d]: the session role {sessRole} (was cleartext PASSWORD '<32 hex>') is created with a verifier: pg_stat_activity during its CREATE shows 'SCRAM-SHA-256$4096:' and never the cleartext",
        sessSample.Any(q => q.Contains($"CREATE ROLE \"{sessRole}\"") && q.Contains("PASSWORD 'SCRAM-SHA-256$4096:")) && sessSample.All(q => !q.Contains(sessPw) && !MaintSql.PwNotVerifier.IsMatch(q)));
    await ScratchDb.CreateUnmarkedAsync(acs, lookalike, role: true);
    var sessConn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(acs) { Username = sessRole, Password = sessPw, Database = crashedYoung, Pooling = false }.ConnectionString);
    await sessConn.OpenAsync();
    await Check($"role sweep setup: {sessRole} has a session in pg_stat_activity; {lookalike} exists unmarked", async () =>
        await ScratchDb.RoleSessionsAsync(acs, sessRole) > 0 && (await ScratchDb.RolesAsync(acs)).Contains((lookalike, "")));
    var sw = await ScratchDb.SweepAsync(acs, currentName: own);
    await Check("o1: old marked DB with no connections -> dropped (and its marked roles)", async () =>
        sw.Dropped.Contains(crashedOld) && !(await ScratchDb.StateAsync(acs, crashedOld)).exists);
    await Check("o1: marked DB younger than 1 h -> kept", async () => sw.Kept.Contains(crashedYoung) && (await ScratchDb.StateAsync(acs, crashedYoung)) == (true, true));
    await Check("o1: old marked DB with a live connection -> kept", async () => sw.Kept.Contains(crashedLive) && (await ScratchDb.StateAsync(acs, crashedLive)) == (true, true));
    await Check("o1: the run's own DB (old, no connection) -> never dropped", async () => sw.Kept.Contains(own) && !sw.Dropped.Contains(own) && (await ScratchDb.StateAsync(acs, own)) == (true, true));
    await Check("o2: unmarked decoy -> reported (Unmarked), not dropped", async () => sw.Unmarked.SequenceEqual([decoy]) && (await ScratchDb.StateAsync(acs, decoy)) == (true, false));
    Ok("o1: exactly one DB dropped by the sweep", sw.Dropped.SequenceEqual([crashedOld]));
    var rolesNow = await ScratchDb.RolesAsync(acs);
    bool HasRole(string r) => rolesNow.Any(x => x.role == r);
    Ok($"12:33 role sweep: stale marked role {staleRole} (2 h, DB gone, no session) -> dropped", sw.RolesDropped.Contains(staleRole) && !HasRole(staleRole));
    Ok($"12:33 role sweep: young marked role {youngRole} -> kept", sw.RolesKept.Contains(youngRole) && HasRole(youngRole));
    Ok($"12:33 role sweep: stale marked role {sessRole} with a live session -> kept", sw.RolesKept.Contains(sessRole) && HasRole(sessRole));
    Ok($"12:33 role sweep: unmarked look-alike {lookalike} -> reported (UnmarkedRoles, FATAL), not dropped", sw.UnmarkedRoles.SequenceEqual([lookalike]) && rolesNow.Contains((lookalike, "")));
    Ok("12:33 role sweep: roles of kept DBs (young, live) kept; the old DB's roles went with it", new[] { crashedYoung, crashedLive }.All(d => HasRole(d + "_admin_api") && HasRole(d + "_login"))
        && !HasRole(crashedOld + "_admin_api") && !HasRole(crashedOld + "_login"));
    Ok("12:33 role sweep: exactly one stand-alone role dropped", sw.RolesDropped.SequenceEqual([staleRole]));
    await sessConn.DisposeAsync();
    for (var i = 0; i < 50 && await ScratchDb.RoleSessionsAsync(acs, sessRole) > 0; i++) await Task.Delay(100);
    var sw2 = await ScratchDb.SweepAsync(acs, currentName: own);
    Ok($"12:33 role sweep: once the session ends, {sessRole} is dropped by the next sweep", sw2.RolesDropped.SequenceEqual([sessRole]) && !(await ScratchDb.RolesAsync(acs)).Any(x => x.role == sessRole));
    // [SSE2 09e31f68 M2] --scratch-drop (DropCliAsync) on a marked DB with a live connection: rc 2, clear message, no crash.
    var (m2rc, m2msg) = await ScratchDb.DropCliAsync(acs, crashedLive);
    Console.WriteLine($"INFO M2: --scratch-drop {crashedLive} while held -> rc {m2rc}: {m2msg}");
    await Check($"M2: --scratch-drop on marked {crashedLive} with a live connection -> rc 2, no crash, DB and its two roles intact [SSE2 09e31f68]", async () =>
        m2rc == 2 && (await ScratchDb.StateAsync(acs, crashedLive)) == (true, true)
        && (await ScratchDb.RolesAsync(acs)).Count(x => x.role.StartsWith(crashedLive + "_", StringComparison.Ordinal) && x.comment == ScratchDb.Marker) == 2);
    Ok("M2: the refusal names the open connection(s) (pre-check before any DROP)", m2msg.Contains("connection(s) in pg_stat_activity"), m2msg);
    var (n0rc, n0msg) = await ScratchDb.DropCliAsync(acs, "ff_scratch_nothing" + Guid.NewGuid().ToString("N")[..6]);
    Ok("L2: --scratch-drop on a name that never existed -> rc 0, 'nothing to drop', never 'dropped'", n0rc == 0 && n0msg.Contains("nothing to drop") && !n0msg.Contains("dropped"), n0msg);
    // [SSE2 09e31f68 L1] marked objects we own whose runid is missing, invalid or in the future: reported loudly (BadRunId,
    // FATAL; --scratch-sweep exits 3, the main run fails), never kept silently and never dropped.
    var l1NoRunDb = "ff_scratch_l1norunid" + Guid.NewGuid().ToString("N")[..6];
    var l1FutureDb = ScratchDb.NewName(TimeSpan.FromHours(-24));
    var l1BadDateRole = "ff_scratch_20991399999999" + Guid.NewGuid().ToString("N")[..6] + "_login";
    var l1FutureRole = ScratchDb.NewName(TimeSpan.FromHours(-24)) + "_admin_api";
    var l1NoRunRole = "ff_scratch_l1norunid" + Guid.NewGuid().ToString("N")[..6] + "_login";
    await ScratchDb.CreateMarkedAsync(acs, l1NoRunDb);
    await ScratchDb.CreateMarkedAsync(acs, l1FutureDb);
    foreach (var r in new[] { l1BadDateRole, l1FutureRole, l1NoRunRole }) await ScratchDb.CreateMarkedRoleAsync(acs, r);
    var sw3 = await ScratchDb.SweepAsync(acs, currentName: own);
    string[] l1All = [l1NoRunDb, l1FutureDb, l1BadDateRole, l1FutureRole, l1NoRunRole];
    await Check($"L1: marked DBs / roles with a missing, invalid or future runid -> reported by name (BadRunId, FATAL), none dropped or silently kept [SSE2 09e31f68]", async () =>
        sw3.BadRunId.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(l1All.OrderBy(x => x, StringComparer.Ordinal))
        && !sw3.Kept.Intersect(l1All).Any() && !sw3.RolesKept.Intersect(l1All).Any() && sw3.Dropped.Count == 0 && sw3.RolesDropped.Count == 0
        && (await ScratchDb.StateAsync(acs, l1NoRunDb)) == (true, true) && (await ScratchDb.StateAsync(acs, l1FutureDb)) == (true, true)
        && new[] { l1BadDateRole, l1FutureRole, l1NoRunRole }.All(r => sw3.BadRunId.Contains(r)) && (await ScratchDb.RolesAsync(acs)).Count(x => l1All.Contains(x.role)) == 3);
    foreach (var d in new[] { l1NoRunDb, l1FutureDb, l1BadDateRole[..^"_login".Length], l1FutureRole[..^"_admin_api".Length], l1NoRunRole[..^"_login".Length] })
        await ScratchDb.DropAsync(acs, d); // exact names made above, via the marked path
    await Check("L1: the five L1 objects are gone after exact-name cleanup", async () =>
        !(await ScratchDb.ListAsync(acs, markedOnly: false)).Intersect(l1All).Any() && !(await ScratchDb.RolesAsync(acs)).Any(x => l1All.Contains(x.role)));
    // [TLD 12:33 item 1] ff_scratchadm: documented attributes, only the PG17 implicit ADMIN-OPTION memberships on marked per-run roles.
    await using (var m = await ScratchDb.OpenMaint(acs))
    {
        var row = await m.ScalarAsync(MaintSql.AdminRole) as string;
        var mem = await m.ColumnAsync(MaintSql.AdminMembers);
        foreach (var l in mem) Console.WriteLine($"INFO ff_scratchadm membership (role|member|admin|inherit|set|grantor_is_bootstrap|role comment): {l}");
        Ok($"12:33 ff_scratchadm attributes = {ScratchDb.AdminRoleExpected}", row == ScratchDb.AdminRoleExpected, row);
        var other = mem.Where(l => !l.StartsWith(lookalike + "|", StringComparison.Ordinal)).ToList();
        Ok("12:33 ff_scratchadm memberships (besides the look-alike's) are exactly the PG17 implicit grants on live marked per-run roles (ADMIN true, INHERIT false, SET false, grantor bootstrap)",
            other.Count > 0 && other.All(l => l.Split('|') is [var r, "ff_scratchadm", "true", "false", "false", "true", "floorfunds-scratch"] && rolesNow.Contains((r, "floorfunds-scratch"))));
    }
    await Check($"12:33 ff_scratchadm check: flags exactly the implicit membership on the UNMARKED look-alike {lookalike} (not an allowed membership)", async () =>
        (await ScratchDb.AdminRoleViolationsAsync(acs)) is [var only] && only.StartsWith($"membership: {lookalike}|ff_scratchadm|", StringComparison.Ordinal));
    // [TLD 12:33 item 3] maint guard: reads name only the listed catalogs; anything not allowlisted is refused before sending.
    var fromRe = new System.Text.RegularExpressions.Regex(@"\b(?:FROM|JOIN)\s+([a-z_]+)");
    var named = MaintSql.Reads.SelectMany(q => fromRe.Matches(q).Select(x => x.Groups[1].Value)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    Ok($"maint-guard: reads name only allowlisted catalogs ({string.Join(", ", named)})", named.All(MaintSql.Catalogs.Contains)
        && MaintSql.Reads.All(q => !q.Replace("'pg_authid'", "").Contains("pg_authid")));
    foreach (var good in new[] { $"CREATE DATABASE \"{own}\" TEMPLATE template0 ENCODING 'UTF8'", $"COMMENT ON ROLE \"{own}_login\" IS 'floorfunds-scratch'", $"DROP ROLE \"{own}_admin_api\"" })
        Ok($"maint-guard: allowlisted shape classified ({MaintSql.Head(good)})", MaintSql.Classify(good).kind is not null);
    Ok("maint-guard: refusal text never echoes literals", !MaintSql.Head("CREATE ROLE \"x\" LOGIN PASSWORD 'abc123'").Contains("abc123"));
    await using (var m = await ScratchDb.OpenMaint(acs))
        foreach (var bad in new[] { "SELECT 1", "SELECT rolname FROM pg_authid", "ALTER ROLE ff_scratchadm SUPERUSER", "SELECT datname FROM pg_database; SELECT 1",
                                    "DO $$BEGIN NULL; END$$", "SET ROLE postgres", "COMMENT ON DATABASE \"ff_scratch_zzzzzz999999\" IS 'other'" })
            await Throws<InvalidOperationException>($"maint-guard: refused on the maintenance connection: {bad.Split(' ')[0]} {bad.Split(' ')[1]}", () => m.ExecAsync(bad), e => e.Message.Contains("maint-guard"));
    await using (var m = await ScratchDb.OpenMaint(acs))
    {
        await Throws<InvalidOperationException>("maint-guard: DROP DATABASE of a missing / unmarked ff_scratch_* name refused", () => m.ExecAsync($"DROP DATABASE \"{decoy}\""), e => e.Message.Contains("maint-guard"));
        await Throws<InvalidOperationException>("maint-guard: DROP ROLE of an unmarked ff_scratch_* role refused", () => m.ExecAsync($"DROP ROLE \"{lookalike}\""), e => e.Message.Contains("maint-guard"));
        await Throws<InvalidOperationException>("maint-guard: CREATE DATABASE on a protected / non-scratch name refused", () => m.ExecAsync("CREATE DATABASE \"ff_sse3\" TEMPLATE template0 ENCODING 'UTF8'"), e => e.Message.Contains("maint-guard"));
    }
    // o3: the snapshot check catches a created cluster-level role (mutation), and is identical once it is dropped again.
    var snap0 = await ScratchDb.ClusterSnapshotAsync(acs);
    var mutRole = "ff_scratch_mut" + Guid.NewGuid().ToString("N")[..8] + "_login";
    await ScratchDb.CreateMarkedRoleAsync(acs, mutRole);
    var snap1 = await ScratchDb.ClusterSnapshotAsync(acs);
    Ok($"o3 mutation: CREATE ROLE {mutRole} -> snapshot differs (caught)", snap1 != snap0 && ScratchDb.Diff(snap0, snap1).Contains(mutRole), ScratchDb.Diff(snap0, snap1));
    await ScratchDb.DropAsync(acs, mutRole[..^"_login".Length]); // no DB of that name; drops the marked role
    Ok("o3: after DROP ROLE the snapshot is identical again", await ScratchDb.ClusterSnapshotAsync(acs) == snap0);
    // Cleanup: own via the marked path, young / live via the marked path (the hold has ended or is ignored by DropAsync? no FORCE:
    // the verify script waits for the hold to end first), decoy by exact name.
    await ScratchDb.DropAsync(acs, own);
    foreach (var n in new[] { crashedYoung, crashedLive })
        try { await ScratchDb.DropAsync(acs, n); } catch (PostgresException e) when (e.SqlState == "55006") { Console.WriteLine($"cleanup: {n} still in use"); }
    Console.WriteLine($"cleanup: {await ScratchDb.CleanupUnmarkedAsync(acs, decoy, role: false, prefix: "ff_scratch_decoy")}");
    Console.WriteLine($"cleanup: {await ScratchDb.CleanupUnmarkedAsync(acs, lookalike, role: true, prefix: "ff_scratch_")}");
    await ScratchDb.DropAsync(acs, youngRole[..^"_login".Length]); // stand-alone marked role, no DB
    await Check("12:33 ff_scratchadm: after cleanup AdminRoleViolationsAsync -> none (only the held live DB's two roles remain as memberships)", async () => (await ScratchDb.AdminRoleViolationsAsync(acs)) is { Count: 0 });
    // The live DB is still held by --scratch-hold here (DropAsync never uses FORCE), so it is the only one allowed to remain;
    // the verify script drops it with --scratch-sweep after the hold ends and then checks 0 DBs / 0 roles cluster-wide.
    await Check($"scratch: after the self-test only the held {crashedLive} is left (no other ff_scratch_* DB, marked or not)", async () =>
        (await ScratchDb.ListAsync(acs, markedOnly: false)).SequenceEqual([crashedLive]));
    await Check($"scratch: the only ff_scratch_* roles left are {crashedLive}'s two", async () =>
    {
        return string.Join(' ', (await ScratchDb.RolesAsync(acs)).Select(x => x.role)) == $"{crashedLive}_admin_api {crashedLive}_login";
    });
    MaintConn.Trace = null;
    {
        var pwStmts = allSent.Count(q => MaintSql.PwAny.IsMatch(q));
        Ok($"KF-A (b) [SSE2 04dba71d]: whole self-test statement text ({allSent.Count} maintenance statements, {pwStmts} with PASSWORD): every PASSWORD is followed by 'SCRAM-SHA-256$4096:'; neither test password appears",
            pwStmts == 2 && allSent.All(q => !MaintSql.PwNotVerifier.IsMatch(q) && !q.Contains(kfaPw) && !q.Contains(sessPw)));
    }
    Console.WriteLine($"\n{pass} passed, {fail} failed");
    return fail == 0 ? 0 : 1;
}

string Env(string k) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{k} not set");
var ownerCs = Env("CONNECTION_STRING");
var adminCs = Env("ADMIN_API_CONNECTION_STRING");
var loginCs = Env("LOGIN_CONNECTION_STRING");
var fixturesPath = args.Length > 0 ? args[0] : "Login.StandIn/fixtures.json";
// Checks write committed SSE2-FAKE-TEST-ONLY rows and alter constraints inside rolled-back transactions, so the target DB
// is named explicitly (AUTH_CHECKS_DB), all three connection strings must point at it, and a safety guard refuses anything
// that is not an engineers' test DB (ff_sse, ff_sse2) or a db/ kit scratch DB (ff_kit_*). SSE runs only ff_sse / ff_kit_*.
var dbName = Environment.GetEnvironmentVariable("AUTH_CHECKS_DB") ?? "";
static bool IsTestDb(string n) => n is "ff_sse" or "ff_sse2" || System.Text.RegularExpressions.Regex.IsMatch(n, "^ff_kit_[a-z0-9_]{1,45}$");
if (!IsTestDb(dbName))
{ Console.Error.WriteLine("refuse: set AUTH_CHECKS_DB to a test DB: ff_sse, ff_sse2 or ff_kit_<name> (throwaway kit DB)"); return 2; }
if (new[] { ownerCs, adminCs, loginCs }.Any(cs => new NpgsqlConnectionStringBuilder(cs).Database != dbName))
{ Console.Error.WriteLine("refuse: every connection string's Database must equal AUTH_CHECKS_DB"); return 2; }
// [SSE2 09e31f68 M3] fail-fast single runner: before any write to the kit DB or any scratch work, take the session advisory
// lock on the kit target DB (owner connection); held until the process exits. A concurrent run exits 5 at once.
if (ScratchLock.Refusal(ownerCs) is { } lockRefusedMain) { Console.Error.WriteLine($"FATAL scratch-lock: {lockRefusedMain}"); return 2; } // [TLD 13:54 item 1]
await using var scratchLock = await ScratchLock.TryAcquireAsync(ownerCs);
if (scratchLock is null) { Console.WriteLine(ScratchLock.BusyMessage(ownerCs)); return 5; }
string adminRole = dbName + "_admin_api", loginRole = dbName + "_login";
string[] LlcMutable = ["provider_kind", "display_name", "entra_allowed_tenant_ids", "oidc_authority", "oidc_client_id", "secret_ref",
                       "allow_db_fallback", "mfa_required", "updated_by", "updated_at", "row_version"];

await using var ownerDs = NpgsqlDataSource.Create(ownerCs);
await using var adminDs = NpgsqlDataSource.Create(adminCs);
await using var loginDs = NpgsqlDataSource.Create(loginCs);

// ---------- SSE2-FAKE-TEST-ONLY ids ----------
Guid P_PCI = Guid.Parse("5f0c3e1a-0001-4000-8000-000000000001"), P_NV = Guid.Parse("5f0c3e1a-0002-4000-8000-000000000002");
Guid L_PCI = Guid.Parse("5f0c3e1a-0001-4000-8000-0000000000a1"), L_NV = Guid.Parse("5f0c3e1a-0002-4000-8000-0000000000b1");
Guid T1 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa01"), T2 = Guid.Parse("5f0c3e1a-00fa-4000-8000-00000000fa02"); // SSE2-FAKE-TEST-ONLY tenants
Guid U_C01 = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c01"), U_C03 = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c03");
Guid U_DIS = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c71"), U_LCK = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c72"); // checks-only users
Guid S1 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000001"), S2 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000002"),
     S3 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000003"), S9 = Guid.Parse("5f0c3e1a-00fb-4000-8000-000000000009");
Guid ACTOR = Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c0a");
string Iss(Guid t) => $"https://login.microsoftonline.com/{t}/v2.0";
const string FakeArn = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/pci-main-AbCdEf"; // SSE2-FAKE-TEST-ONLY

async Task BindResolved(NpgsqlConnection c, NpgsqlTransaction? tx, string adminType, Guid? partner)
{
    // The flags are cleared. The principal is the only authorization input.
    Guid? who = adminType switch
    {
        "super" => Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c73"),
        "partner" when partner == P_PCI => Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c74"),
        "partner" when partner == P_NV => Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c75"),
        _ => null,
    };
    if (who is Guid id)
        await Exec(c, tx, "SELECT set_config('app.request_iss','https://auth-data.test.invalid',true), set_config('app.request_sub',$1,true), set_config('app.resolved_global_admin','',true), set_config('app.resolved_partner_ids','',true), set_config('app.resolved_location_ids','',true), set_config('app.admin_type','',true), set_config('app.partner_id','',true)", id.ToString("D"));
    else
        await Exec(c, tx, "SELECT set_config('app.request_iss','',true), set_config('app.request_sub','',true), set_config('app.resolved_global_admin','',true), set_config('app.resolved_partner_ids','',true), set_config('app.resolved_location_ids','',true), set_config('app.admin_type',$1,true), set_config('app.partner_id','',true)", adminType);
}
async Task Exec(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] ps)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
}
async Task<T> Scalar<T>(NpgsqlConnection c, NpgsqlTransaction? tx, string sql, params object?[] ps)
{
    await using var cmd = new NpgsqlCommand(sql, c, tx);
    foreach (var p in ps) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
    return (T)(await cmd.ExecuteScalarAsync())!;
}
// Runs sql inside a rolled-back transaction on a fresh connection; returns the SQLSTATE or "ok".
async Task<string> Try(NpgsqlDataSource ds, string sql, string? adminType = null, Guid? partner = null)
{
    await using var c = await ds.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    try
    {
        if (adminType is not null) await BindResolved(c, tx, adminType, partner);
        await Exec(c, tx, sql);
        return "ok";
    }
    catch (PostgresException e) { return e.SqlState; }
    finally { try { await tx.RollbackAsync(); } catch { } }
}
async Task<long> CountAs(NpgsqlDataSource ds, string sql, string? adminType, Guid? partner)
{
    await using var c = await ds.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    if (adminType is not null) await BindResolved(c, tx, adminType, partner);
    var n = await Scalar<long>(c, tx, sql);
    await tx.CommitAsync();
    return n;
}

// ================= 1. model / snapshot / schema =================
await using (var db = new AuthDbContext(AuthDb.Options(ownerCs)))
{
    var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
    var differ = db.GetService<IMigrationsModelDiffer>();
    var designModel = db.GetService<IDesignTimeModel>().Model;
    Ok("model: hand-kept snapshot exists", snapshot is not null);
    IModel snapModel = snapshot!.Model;
    if (snapModel is IMutableModel mm) snapModel = mm.FinalizeModel();
    snapModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapModel);
    Ok("model: snapshot == AuthDbContext (IMigrationsModelDiffer.HasDifferences false)",
        !differ.HasDifferences(snapModel.GetRelationalModel(), designModel.GetRelationalModel()));
    var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
    Ok("model: applied migrations == [S2aInitial, S2aLlcColumnUpdate, S2aPartnerReasonBounds, CatalogMasterData, Req064AssignmentResolver, Req064RoleScope, Req064HistoryPartner, Req064Issuer, Req064Principal]", applied.SequenceEqual(["20261104160000_S2aInitial", "20261104170000_S2aLlcColumnUpdate", "20261105080000_S2aPartnerReasonBounds", "20261108190000_CatalogMasterData", "20261110160000_Req064AssignmentResolver", "20261110200000_Req064RoleScope", "20261110210000_Req064HistoryPartner", "20261110220000_Req064Issuer", "20261110230000_Req064Principal"]), string.Join(",", applied));
    Ok("model: no pending migrations", !(await db.Database.GetPendingMigrationsAsync()).Any());

    await using var c = await ownerDs.OpenConnectionAsync();
    var bad = new List<string>();
    int cols = 0;
    foreach (var et in designModel.GetEntityTypes())
    {
        var table = et.GetTableName()!;
        var so = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, "auth");
        var dbCols = new Dictionary<string, (string type, bool nullable)>();
        await using (var cmd = new NpgsqlCommand("SELECT column_name, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull FROM information_schema.columns ic JOIN pg_attribute a ON a.attrelid = ('auth.' || ic.table_name)::regclass AND a.attname = ic.column_name WHERE table_schema = 'auth' AND table_name = $1", c))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) dbCols[r.GetString(0)] = (r.GetString(1), r.GetBoolean(2));
        }
        foreach (var p in et.GetProperties())
        {
            cols++;
            var col = p.GetColumnName(so)!;
            if (!dbCols.Remove(col, out var d)) { bad.Add($"{table}.{col} missing"); continue; }
            if (d.type != p.GetColumnType()) bad.Add($"{table}.{col} type {d.type} != {p.GetColumnType()}");
            if (d.nullable != p.IsColumnNullable(so)) bad.Add($"{table}.{col} nullability");
        }
        bad.AddRange(dbCols.Keys.Select(k => $"{table}.{k} not in EF model"));
    }
    Ok($"schema: {designModel.GetEntityTypes().Count()} EF tables / {cols} columns == pg_catalog (name, type, nullability)", bad.Count == 0, string.Join("; ", bad));
    Ok("schema: exactly 13 tables in auth (Slice A: + partner_reason_bounds; REQ-064: + admin_assignment, user_issuer, login_write_principal)", await Scalar<long>(c, null, "SELECT count(*) FROM pg_tables WHERE schemaname='auth'") == 13);
    Ok("schema: exactly one sequence auth.resolver_read_seq (REQ-064)", await Scalar<long>(c, null, "SELECT count(*) FROM pg_sequences WHERE schemaname='auth'") == 1
        && await Scalar<string>(c, null, "SELECT string_agg(sequencename, ',' ORDER BY sequencename) FROM pg_sequences WHERE schemaname='auth'") == "resolver_read_seq");
    Ok("schema: history table in public, not auth", await Scalar<bool>(c, null, "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AND to_regclass('auth.\"__EFMigrationsHistory\"') IS NULL"));
    var idx = await Scalar<long>(c, null, "SELECT count(*) FROM pg_indexes WHERE schemaname='auth' AND indexname IN ('location_partner_ix','llc_tenants_gin','llcc_location_at_ix','external_login_user_ix','user_location_location_ix','oidc_replay_expires_ix','session_user_live_ix','session_last_seen_ix','location_slug_key','session_cookie_key_hash_key')");
    Ok("schema: the 10 spec indexes exist", idx == 10, idx.ToString());
    Ok("schema: llc_tenants_gin is GIN", await Scalar<string>(c, null, "SELECT am.amname::text FROM pg_class i JOIN pg_am am ON am.oid=i.relam WHERE i.relname='llc_tenants_gin'") == "gin");
    Ok("schema: session_user_live_ix is partial (revoked_at IS NULL)", (await Scalar<string>(c, null, "SELECT indexdef FROM pg_indexes WHERE indexname='session_user_live_ix'")).Contains("WHERE (revoked_at IS NULL)"));
}

// ================= 2. roles, grants, functions, RLS flags =================
await using (var c = await ownerDs.OpenConnectionAsync())
{
    foreach (var r in new[] { adminRole, loginRole })
    {
        Ok($"role {r}: LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION",
            await Scalar<bool>(c, null, "SELECT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication FROM pg_roles WHERE rolname=$1", r));
        Ok($"role {r}: owns nothing in auth", await Scalar<long>(c, null, "SELECT count(*) FROM pg_class cl JOIN pg_namespace n ON n.oid=cl.relnamespace WHERE n.nspname='auth' AND pg_get_userbyid(cl.relowner)=$1", r) == 0);
        Ok($"role {r}: CONNECT on db, USAGE (not CREATE) on schema auth",
            await Scalar<bool>(c, null, "SELECT has_database_privilege($1, current_database(), 'CONNECT') AND has_schema_privilege($1, 'auth', 'USAGE') AND NOT has_schema_privilege($1, 'auth', 'CREATE')", r));
    }
    Ok("schema auth: no PUBLIC privileges", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_namespace n, aclexplode(n.nspacl) x WHERE n.nspname='auth' AND x.grantee=0)"));
    Ok("database: no PUBLIC CONNECT (ff-db.sh REVOKE)", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_database d, aclexplode(d.datacl) x WHERE d.datname=current_database() AND x.grantee=0 AND x.privilege_type='CONNECT')"));

    // §8.4 matrix: every (table, role, privilege) at table level.
    var privs = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" };
    var expect = new Dictionary<(string, string), string[]>
    {
        [("location", adminRole)] = ["SELECT", "INSERT"], [("location", loginRole)] = ["SELECT"],
        [("location_login_config", adminRole)] = ["SELECT", "INSERT"], [("location_login_config", loginRole)] = ["SELECT"],
        [("location_login_config_change", adminRole)] = ["SELECT", "INSERT"], [("location_login_config_change", loginRole)] = [],
        [("app_user", adminRole)] = [], [("app_user", loginRole)] = ["SELECT"],
        [("external_login", adminRole)] = [], [("external_login", loginRole)] = ["SELECT"],
        [("user_location", adminRole)] = [], [("user_location", loginRole)] = ["SELECT"],
        [("audit_outbox", adminRole)] = ["INSERT"], [("audit_outbox", loginRole)] = ["INSERT"],
        [("oidc_replay", adminRole)] = [], [("oidc_replay", loginRole)] = ["SELECT", "INSERT", "DELETE"],
        [("session", adminRole)] = [], [("session", loginRole)] = ["SELECT", "INSERT", "DELETE"],
        [("partner_reason_bounds", adminRole)] = ["SELECT", "INSERT"], [("partner_reason_bounds", loginRole)] = [],
    };
    var mism = new List<string>();
    foreach (var ((t, r), want) in expect)
        foreach (var p in privs)
            if (await Scalar<bool>(c, null, "SELECT has_table_privilege($1, 'auth.' || $2, $3)", r, t, p) != want.Contains(p)) mism.Add($"{t}/{r}/{p}");
    Ok($"grants: table-level matrix ({expect.Count * privs.Length} cells) == spec §8.4", mism.Count == 0, string.Join(", ", mism));
    foreach (var t in expect.Keys.Select(k => k.Item1).Distinct())
        Ok($"grants: PUBLIC has nothing on auth.{t}", await Scalar<bool>(c, null, "SELECT NOT EXISTS (SELECT 1 FROM pg_class cl, aclexplode(cl.relacl) x WHERE cl.oid = ('auth.' || $1)::regclass AND x.grantee = 0)", t));

    // Column-level UPDATE, every column of every auth table, both roles (P2-1: no UPDATE on PKs / created_* / immutable).
    var updAllowed = new Dictionary<(string, string), string[]>
    {
        [("location", adminRole)] = ["name"],
        [("location_login_config", adminRole)] = LlcMutable,
        [("partner_reason_bounds", adminRole)] = ["reason_min_length", "reason_max_length", "updated_by", "updated_at", "row_version"],
        [("session", loginRole)] = ["last_seen_at", "revoked_at", "revoked_reason", "interactive_used_at", "handoff_used_at", "cage_consumed_at", "code_issued"],
    };
    var colMism = new List<string>();
    var cols = new List<(string T, string C)>();
    await using (var cmd = new NpgsqlCommand("SELECT table_name, column_name FROM information_schema.columns WHERE table_schema='auth' ORDER BY 1, 2", c))
    await using (var rr = await cmd.ExecuteReaderAsync()) while (await rr.ReadAsync()) cols.Add((rr.GetString(0), rr.GetString(1)));
    foreach (var (t, col) in cols)
        foreach (var r in new[] { adminRole, loginRole })
            if (await Scalar<bool>(c, null, "SELECT has_column_privilege($1, 'auth.' || $2, $3, 'UPDATE')", r, t, col)
                != (updAllowed.TryGetValue((t, r), out var ok) && ok.Contains(col))) colMism.Add($"{t}.{col}/{r}");
    Ok($"grants: column UPDATE ({cols.Count} columns x 2 roles) = location(name) + location_login_config(11 mutable) + partner_reason_bounds(5 mutable, not partner_id) for admin_api; session(7) for login; nothing else",
        cols.Count > 0 && colMism.Count == 0, string.Join(", ", colMism));
    Ok("grants: admin_api UPDATE on location_login_config location_id/created_at/created_by = none (P2-1)",
        !await Scalar<bool>(c, null, "SELECT has_column_privilege($1, 'auth.location_login_config', 'location_id', 'UPDATE') OR has_column_privilege($1, 'auth.location_login_config', 'created_at', 'UPDATE') OR has_column_privilege($1, 'auth.location_login_config', 'created_by', 'UPDATE')", adminRole));

    foreach (var f in new[] { "auth.location_partner(uuid)", "auth.tenant_bound_elsewhere(uuid,uuid)" })
    {
        Ok($"function {f}: SECURITY DEFINER, search_path pinned", await Scalar<bool>(c, null, "SELECT prosecdef AND proconfig = ARRAY['search_path=pg_catalog, auth'] FROM pg_proc WHERE oid = $1::regprocedure", f));
        Ok($"function {f}: EXECUTE admin_api yes, login no, PUBLIC no",
            await Scalar<bool>(c, null, "SELECT has_function_privilege($2, $1::regprocedure, 'EXECUTE') AND NOT has_function_privilege($3, $1::regprocedure, 'EXECUTE') AND NOT EXISTS (SELECT 1 FROM pg_proc p, aclexplode(p.proacl) x WHERE p.oid = $1::regprocedure AND x.grantee = 0)", f, adminRole, loginRole));
    }
    Ok("default privileges: owner's functions in this DB get no PUBLIC EXECUTE (global pg_default_acl entry, no PUBLIC grantee)",
        await Scalar<bool>(c, null, "SELECT EXISTS (SELECT 1 FROM pg_default_acl d WHERE d.defaclnamespace = 0 AND d.defaclobjtype = 'f' AND d.defaclrole = (SELECT oid FROM pg_roles WHERE rolname = current_user) AND NOT EXISTS (SELECT 1 FROM aclexplode(d.defaclacl) x WHERE x.grantee = 0))"));
    await using (var tx = await c.BeginTransactionAsync())
    {
        await Exec(c, tx, "CREATE FUNCTION auth.zz_probe() RETURNS int LANGUAGE sql AS 'SELECT 1'");
        Ok("default privileges: a new auth function is not executable by login or PUBLIC (probe, rolled back)",
            !await Scalar<bool>(c, tx, "SELECT has_function_privilege($1, 'auth.zz_probe()', 'EXECUTE')", loginRole)
            && await Scalar<bool>(c, tx, "SELECT NOT EXISTS (SELECT 1 FROM pg_proc p, aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) x WHERE p.oid = 'auth.zz_probe()'::regprocedure AND x.grantee = 0)"));
        await tx.RollbackAsync();
    }
    var rls = new List<string>();
    await using (var cmd = new NpgsqlCommand("SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class cl JOIN pg_namespace n ON n.oid=cl.relnamespace WHERE n.nspname='auth' AND relkind='r'", c))
    await using (var rr = await cmd.ExecuteReaderAsync())
        while (await rr.ReadAsync())
        {
            bool want = rr.GetString(0) is "admin_assignment" or "location" or "location_login_config" or "location_login_config_change" or "partner_reason_bounds";
            if (rr.GetBoolean(1) != want || rr.GetBoolean(2) != want) rls.Add(rr.GetString(0));
        }
    Ok("RLS: ENABLE+FORCE on exactly admin_assignment, location, location_login_config, location_login_config_change, partner_reason_bounds", rls.Count == 0, string.Join(",", rls));
    // K2 (SSE2 d787f76 review): the owner role is read from pg_database, not assumed equal to the DB name (kit --role != --db).
    var dbOwner = await Scalar<string>(c, null, "SELECT pg_get_userbyid(datdba)::text FROM pg_database WHERE datname = current_database()");
    Ok($"RLS: policy set == spec (owner {dbOwner} from pg_database/admin/login per table; no login policy on change)",
        await Scalar<string>(c, null, "SELECT string_agg(tablename || ':' || policyname || ':' || cmd || ':' || array_to_string(roles, '+'), ' ' ORDER BY tablename, policyname) FROM pg_policies WHERE schemaname='auth'")
        == $"admin_assignment:admin_assignment_owner:ALL:{dbOwner} " +
           $"location:location_admin:ALL:{adminRole} location:location_login:SELECT:{loginRole} location:location_owner:ALL:{dbOwner} " +
           $"location_login_config:llc_admin:ALL:{adminRole} location_login_config:llc_login:SELECT:{loginRole} location_login_config:llc_owner:ALL:{dbOwner} " +
           $"location_login_config_change:llcc_admin:ALL:{adminRole} location_login_config_change:llcc_owner:ALL:{dbOwner} " +
           $"partner_reason_bounds:prb_admin:ALL:{adminRole} partner_reason_bounds:prb_owner:ALL:{dbOwner}");
}

// ================= 3. seed == SI1 fixtures =================
await using (var c = await ownerDs.OpenConnectionAsync())
{
    using var fx = JsonDocument.Parse(await File.ReadAllTextAsync(fixturesPath));
    var slugPartner = new Dictionary<string, Guid>();
    foreach (var p in fx.RootElement.GetProperty("partners").EnumerateArray())
        foreach (var l in p.GetProperty("locations").EnumerateArray())
            slugPartner[l.GetProperty("slug").GetString()!] = p.GetProperty("partner_id").GetGuid();
    var want = new SortedSet<string>();
    foreach (var u in fx.RootElement.GetProperty("users").EnumerateArray())
    {
        Guid? partner = u.TryGetProperty("admin_type", out var at)
            ? (at.GetString() == "super" ? null : u.GetProperty("partner_id").GetGuid())
            : slugPartner[u.GetProperty("location_slugs")[0].GetString()!];
        want.Add($"{u.GetProperty("user_id").GetGuid()}|{partner}|{u.GetProperty("status").GetString()}");
    }
    var got = new SortedSet<string>();
    await using (var cmd = new NpgsqlCommand("SELECT user_id, partner_id, status FROM auth.app_user WHERE user_id::text NOT LIKE '%-000000000c7_' AND user_id::text NOT LIKE '5f0c3e1a-0064-%'", c))
    await using (var r = await cmd.ExecuteReaderAsync())
        while (await r.ReadAsync()) got.Add($"{r.GetGuid(0)}|{(r.IsDBNull(1) ? null : r.GetGuid(1))}|{r.GetString(2)}");
    Ok($"seed: auth.app_user == fixtures.json users ({want.Count}: id, derived partner_id, status)", want.SetEquals(got), $"want {want.Count} got {got.Count}");
}

// ================= 4. checks-only data (SSE2-FAKE-TEST-ONLY) =================
// K3 (SSE2 d787f76 review): the checks commit their own rows and count exact row sets, so they need the post-seed state:
// refuse cleanly (rc 3, before writing anything) when any business/check row is present, and delete their own rows at the
// end (finally), so a re-run on the same DB passes. A crash mid-run leaves rows -> the next run refuses (reset: update 0, update, seed).
const string StateSql = """
    SELECT (SELECT count(*) FROM auth.location) + (SELECT count(*) FROM auth.location_login_config)
         + (SELECT count(*) FROM auth.location_login_config_change) + (SELECT count(*) FROM auth.external_login)
         + (SELECT count(*) FROM auth.user_location) + (SELECT count(*) FROM auth.session) + (SELECT count(*) FROM auth.oidc_replay)
         + (SELECT count(*) FROM auth.audit_outbox) + (SELECT count(*) FROM auth.app_user WHERE user_id::text LIKE '%-000000000c7_')
         + (SELECT count(*) FROM auth.partner_reason_bounds)
    """;
await using (var c = await ownerDs.OpenConnectionAsync())
{
    var leftover = await Scalar<long>(c, null, StateSql);
    if (leftover != 0)
    {
        Console.Error.WriteLine($"refuse: {dbName} is not in the post-seed state ({leftover} rows in auth location/config/change/link/session/replay/outbox tables or checks-only users), e.g. from an interrupted run. Reset it (runner update 0, update, seed) or use a fresh kit DB.");
        return 3;
    }
}
async Task CleanupCheckData()
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    var locs = new[] { L_PCI, L_NV, Guid.Parse("5f0c3e1a-00ee-4000-8000-0000000000e1") }; // = LX
    await Exec(c, tx, "DELETE FROM auth.user_location WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.external_login WHERE subject = ANY($1)", new[] { S1, S2, S3, S9 });
    await Exec(c, tx, "ALTER TABLE auth.admin_assignment DISABLE TRIGGER USER");
    await Exec(c, tx, "ALTER TABLE auth.app_user DISABLE TRIGGER USER");
    await Exec(c, tx, "DELETE FROM auth.user_issuer WHERE user_id = ANY($1)", new[] { Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c73"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c74"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c75") });
    await Exec(c, tx, "DELETE FROM auth.admin_assignment WHERE user_id = ANY($1)", new[] { Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c73"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c74"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c75") });
    await Exec(c, tx, "DELETE FROM auth.app_user WHERE user_id = ANY($1)", new[] { U_DIS, U_LCK, Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c73"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c74"), Guid.Parse("5f0c3e1a-0003-4000-8000-000000000c75") });
    await Exec(c, tx, "ALTER TABLE auth.app_user ENABLE TRIGGER USER");
    await Exec(c, tx, "ALTER TABLE auth.admin_assignment ENABLE TRIGGER USER");
    await Exec(c, tx, "DELETE FROM auth.location_login_config_change WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.location_login_config WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.location WHERE location_id = ANY($1)", locs);
    await Exec(c, tx, "DELETE FROM auth.partner_reason_bounds WHERE partner_id = ANY($1)", new[] { P_PCI, P_NV });
    await tx.CommitAsync();
}
// [TLD 2026-11-04 11:21 Q2b] scratch DB for the N3/N1 probes: start-up sweep of leftover MARKED scratch DBs (crashed runs),
// then create + mark this run's ff_scratch_<runid>; it is migrated in the try and dropped in the finally (also on failure).
// [TLD 11:57 o1-o3]: the sweep drops only old (> 1 h), unconnected, marked leftovers; an unmarked ff_scratch_* DB is not
// dropped, it is printed as FATAL and no scratch DB is made, so N3/N1/Q2 FAIL and the run exits non-zero. Q2 also needs the
// cluster snapshot (roles etc.) taken before CREATE to be identical after the DROP.
string? scratchAdminCs = null, scratchName = null, scratchErr = null, scratchSnap = null;
NpgsqlDataSource? scratchDs = null;
try
{
    scratchAdminCs = ScratchDb.AdminCs();
    var runName = ScratchDb.NewName();
    var sweep = await ScratchDb.SweepAsync(scratchAdminCs, runName);
    if (sweep.Unmarked.Count > 0) throw new InvalidOperationException($"unmarked ff_scratch_* database(s) present, not dropped: {string.Join(", ", sweep.Unmarked)} [TLD 11:57 o2]");
    if (sweep.BadRunId.Count > 0) throw new InvalidOperationException($"marked ff_scratch_* object(s) with a missing, invalid or future runid present, not dropped: {string.Join(", ", sweep.BadRunId)} [SSE2 09e31f68 L1]");
    if (sweep.UnmarkedRoles.Count > 0) throw new InvalidOperationException($"unmarked ff_scratch_* look-alike role(s) present, not dropped: {string.Join(", ", sweep.UnmarkedRoles)} [TLD 12:33]");
    if (await ScratchDb.AdminRoleViolationsAsync(scratchAdminCs) is { Count: > 0 } adminViol)
        throw new InvalidOperationException($"ff_scratchadm differs from the documented kit role: {string.Join("; ", adminViol)} [TLD 12:33]");
    scratchSnap = await ScratchDb.ClusterSnapshotAsync(scratchAdminCs);
    scratchName = await ScratchDb.CreateMarkedAsync(scratchAdminCs, runName); // set only once CREATE + mark succeeded
}
catch (Exception e) { scratchErr = $"{e.GetType().Name}: {e.Message}"; Console.WriteLine($"FATAL scratch: setup failed ({scratchErr}); the N3/N1 probes and Q2 will FAIL"); }
try
{
await using (var c = await ownerDs.OpenConnectionAsync())
await using (var tx = await c.BeginTransactionAsync())
{
    await Exec(c, tx, "INSERT INTO auth.location VALUES ($1,$2,'pci-main','PCI main (SSE2-FAKE-TEST-ONLY)'),($3,$4,'nevada-main','Nevada main (SSE2-FAKE-TEST-ONLY)')", L_PCI, P_PCI, L_NV, P_NV);
    await Exec(c, tx, """
        INSERT INTO auth.location_login_config (location_id, provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at)
        VALUES ($1,'entra','PCI Entra (SSE2-FAKE-TEST-ONLY)', ARRAY[$2]::uuid[], $3, '5f0c3e1a-00fc-4000-8000-000000000001', $4, true, true, $5, now(), $5, now()),
               ($6,'database','Nevada password', '{}', NULL, NULL, NULL, false, false, $5, now(), $5, now())
        """, L_PCI, T1, Iss(T1), FakeArn, ACTOR, L_NV);
    await Exec(c, tx, """
        INSERT INTO auth.location_login_config_change VALUES
          ('5f0c3e1a-00fd-4000-8000-000000000001',$1,$3,'super','initial config (SSE2-FAKE-TEST-ONLY)',now(),'created','{provider_kind}',NULL,'{"provider_kind":"entra"}',1),
          ('5f0c3e1a-00fd-4000-8000-000000000002',$2,$3,'super',NULL,now(),'created','{provider_kind}',NULL,'{"provider_kind":"database"}',1)
        """, L_PCI, L_NV, ACTOR);
    await Exec(c, tx, "INSERT INTO auth.app_user VALUES ($1,$3,'disabled',NULL),($2,$3,'active',now() + interval '1 hour')", U_DIS, U_LCK, P_PCI);
    await Exec(c, tx, "INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra',$1,$2,$3,$4),('entra',$1,$2,$5,$6),('entra',$1,$2,$7,$8)",
        Iss(T1), T1, S1, U_C01, S2, U_DIS, S3, U_LCK);
    await Exec(c, tx, "INSERT INTO auth.user_location (user_id, location_id) VALUES ($1,$2)", U_C01, L_PCI);
    await Exec(c, tx, "INSERT INTO auth.partner_reason_bounds VALUES ($1, 20, 300, $2, now(), 1)", P_PCI, ACTOR); // Slice A
    await Exec(c, tx, """
        INSERT INTO auth.app_user (user_id, partner_id, status) VALUES
          ('5f0c3e1a-0003-4000-8000-000000000c73', NULL, 'active'),
          ('5f0c3e1a-0003-4000-8000-000000000c74', NULL, 'active'),
          ('5f0c3e1a-0003-4000-8000-000000000c75', NULL, 'active')
        """);
    await Exec(c, tx, """
        INSERT INTO auth.user_issuer (user_id, issuer) VALUES
          ('5f0c3e1a-0003-4000-8000-000000000c73', 'https://auth-data.test.invalid'),
          ('5f0c3e1a-0003-4000-8000-000000000c74', 'https://auth-data.test.invalid'),
          ('5f0c3e1a-0003-4000-8000-000000000c75', 'https://auth-data.test.invalid')
        """);
    await Exec(c, tx, """
        INSERT INTO auth.admin_assignment (assignment_id, user_id, role_id, scope_kind, partner_id, status, version, granted_by, granted_at) VALUES
          ('5f0c3e1a-0003-4000-8000-000000000c73', '5f0c3e1a-0003-4000-8000-000000000c73', 'super_admin', 'global', NULL, 'active', 1, '5f0c3e1a-0003-4000-8000-000000000c73', now()),
          ('5f0c3e1a-0003-4000-8000-000000000c74', '5f0c3e1a-0003-4000-8000-000000000c74', 'partner_admin', 'partner', $1, 'active', 1, '5f0c3e1a-0003-4000-8000-000000000c73', now()),
          ('5f0c3e1a-0003-4000-8000-000000000c75', '5f0c3e1a-0003-4000-8000-000000000c75', 'partner_admin', 'partner', $2, 'active', 1, '5f0c3e1a-0003-4000-8000-000000000c73', now())
        """, P_PCI, P_NV);
    await tx.CommitAsync();
}

// ================= 5. RLS behaviour =================
const string cntLoc = "SELECT count(*) FROM auth.location", cntCfg = "SELECT count(*) FROM auth.location_login_config", cntChg = "SELECT count(*) FROM auth.location_login_config_change";
await Check("RLS admin_api: no app.admin_type -> 0 locations, 0 configs, 0 changes (fail closed)", async () =>
    await CountAs(adminDs, cntLoc, null, null) == 0 && await CountAs(adminDs, cntCfg, null, null) == 0 && await CountAs(adminDs, cntChg, null, null) == 0);
await Check("RLS admin_api: admin_type=bogus -> 0 rows", async () =>
    await CountAs(adminDs, cntLoc, "bogus", P_PCI) == 0 && await AdminTypeSuperAlone(cntLoc) == 0);
async Task<long> AdminTypeSuperAlone(string sql)
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, "SELECT set_config('app.admin_type','super',true), set_config('app.resolved_global_admin','',true), set_config('app.resolved_partner_ids','',true), set_config('app.resolved_location_ids','',true)");
    return await Scalar<long>(c, tx, sql);
}
await Check("RLS admin_api: partner without app.partner_id -> 0 rows", async () => await CountAs(adminDs, cntLoc, "partner", null) == 0);
await Check("RLS admin_api: super -> all 2 locations / 2 configs / 2 changes", async () =>
    await CountAs(adminDs, cntLoc, "super", null) == 2 && await CountAs(adminDs, cntCfg, "super", null) == 2 && await CountAs(adminDs, cntChg, "super", null) == 2);
await Check("RLS admin_api: partner pci -> only pci rows (1/1/1)", async () =>
    await CountAs(adminDs, cntLoc + $" WHERE partner_id = '{P_PCI}'", "partner", P_PCI) == 1 && await CountAs(adminDs, cntLoc, "partner", P_PCI) == 1
    && await CountAs(adminDs, cntCfg, "partner", P_PCI) == 1 && await CountAs(adminDs, cntChg, "partner", P_PCI) == 1
    && await CountAs(adminDs, $"SELECT count(*) FROM auth.location_login_config WHERE location_id = '{L_NV}'", "partner", P_PCI) == 0);
await Check("RLS admin_api: settings are transaction-local (next tx on same connection sees 0)", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using (var tx = await c.BeginTransactionAsync())
    {
        await BindResolved(c, tx, "super", null);
        if (await Scalar<long>(c, tx, cntLoc) != 2) return false;
        await tx.CommitAsync();
    }
    return await Scalar<long>(c, null, cntLoc) == 0;
});
await Check("RLS admin_api: partner pci cannot INSERT a nevada location (WITH CHECK, 42501)", async () =>
    await Try(adminDs, $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_NV}', 'zz-x', 'x')", "partner", P_PCI) == "42501");
await Check("RLS admin_api: partner pci UPDATE of nevada config touches 0 rows; own config 1 row", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await BindResolved(c, tx, "partner", P_PCI);
    await using var u1 = new NpgsqlCommand($"UPDATE auth.location_login_config SET mfa_required = mfa_required WHERE location_id = '{L_NV}'", c, tx);
    await using var u2 = new NpgsqlCommand($"UPDATE auth.location_login_config SET mfa_required = mfa_required WHERE location_id = '{L_PCI}'", c, tx);
    var a = await u1.ExecuteNonQueryAsync(); var b = await u2.ExecuteNonQueryAsync();
    await tx.RollbackAsync();
    return a == 0 && b == 1;
});
await Check("RLS admin_api: partner pci cannot INSERT a change row for nevada (42501)", async () =>
    await Try(adminDs, $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_NV}', '{ACTOR}', 'partner', NULL, now(), 'updated', '{{mfa_required}}', '{{}}', '{{}}', 2)", "partner", P_PCI) == "42501");
await Check("RLS admin_api: partner pci CAN insert own change row", async () =>
    await Try(adminDs, $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'partner', NULL, now(), 'updated', '{{mfa_required}}', '{{}}', '{{}}', 2)", "partner", P_PCI) == "ok");
await Check("RLS admin_api: UPDATE location.slug denied (column grant, 42501); name allowed", async () =>
    await Try(adminDs, $"UPDATE auth.location SET slug = 'zz' WHERE location_id = '{L_PCI}'", "super") == "42501"
    && await Try(adminDs, $"UPDATE auth.location SET name = 'renamed' WHERE location_id = '{L_PCI}'", "super") == "ok");
await Check("P2-1 admin_api: UPDATE location_login_config.location_id denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET location_id = gen_random_uuid() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: UPDATE location_login_config.created_at denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET created_at = now() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: UPDATE location_login_config.created_by denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET created_by = gen_random_uuid() WHERE location_id = '{L_PCI}'", "super") == "42501");
await Check("P2-1 admin_api: §8.5 UPDATE of all 11 mutable columns allowed (rolled back)", async () =>
    await Try(adminDs, $"UPDATE auth.location_login_config SET provider_kind = provider_kind, display_name = display_name, entra_allowed_tenant_ids = entra_allowed_tenant_ids, oidc_authority = oidc_authority, oidc_client_id = oidc_client_id, secret_ref = secret_ref, allow_db_fallback = allow_db_fallback, mfa_required = mfa_required, updated_by = '{ACTOR}', updated_at = now(), row_version = row_version + 1 WHERE location_id = '{L_PCI}'", "super") == "ok");
await Check("P2-1 admin_api: UPDATE on change table / audit_outbox denied (append-only, 42501)", async () =>
    await Try(adminDs, "UPDATE auth.location_login_config_change SET at = now()", "super") == "42501"
    && await Try(adminDs, "UPDATE auth.audit_outbox SET occurred_at = now()", "super") == "42501");
await Check("RLS admin_api: DELETE denied on location/config/change (42501)", async () =>
    await Try(adminDs, "DELETE FROM auth.location", "super") == "42501" && await Try(adminDs, "DELETE FROM auth.location_login_config", "super") == "42501"
    && await Try(adminDs, "DELETE FROM auth.location_login_config_change", "super") == "42501");
await Check("definer fn: partner pci admin attributes nevada location to its partner (404 vs forbidden_scope)", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await BindResolved(c, tx, "partner", P_PCI);
    var p = await Scalar<Guid>(c, tx, "SELECT auth.location_partner($1)", L_NV);
    var none = await Scalar<object>(c, tx, "SELECT auth.location_partner(gen_random_uuid())");
    return p == P_NV && none is DBNull;
});
await Check("definer fn: tenant_bound_elsewhere(T1, nevada)=true, (T1, pci)=false, (T2, nevada)=false", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    return await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T1, P_NV)
        && !await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T1, P_PCI)
        && !await Scalar<bool>(c, null, "SELECT auth.tenant_bound_elsewhere($1,$2)", T2, P_NV);
});
await Check("login: sees all locations and configs (no settings needed)", async () => await CountAs(loginDs, cntLoc, null, null) == 2 && await CountAs(loginDs, cntCfg, null, null) == 2);
await Check("login: SELECT change table denied (42501)", async () => await Try(loginDs, cntChg) == "42501");
await Check("login: cannot execute definer fns (42501)", async () => await Try(loginDs, $"SELECT auth.location_partner('{L_PCI}')") == "42501");
await Check("login: cannot write location/config/app_user/external_login/user_location (42501)", async () =>
    await Try(loginDs, "UPDATE auth.location_login_config SET mfa_required = true") == "42501" && await Try(loginDs, "INSERT INTO auth.location VALUES (gen_random_uuid(), gen_random_uuid(), 'zz', 'x')") == "42501"
    && await Try(loginDs, "UPDATE auth.app_user SET status = 'active'") == "42501" && await Try(loginDs, "DELETE FROM auth.external_login") == "42501"
    && await Try(loginDs, "DELETE FROM auth.user_location") == "42501");
await Check("login: session UPDATE of user_id denied; of revoked_at/revoked_reason allowed", async () =>
    await Try(loginDs, "UPDATE auth.session SET user_id = gen_random_uuid()") == "42501" && await Try(loginDs, "UPDATE auth.session SET revoked_at = now(), revoked_reason = 'logout'") == "ok");
await Check("admin_api: cannot read app_user/external_login/user_location/session/oidc_replay; cannot SELECT audit_outbox (42501)", async () =>
    await Try(adminDs, "SELECT 1 FROM auth.app_user") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.external_login") == "42501"
    && await Try(adminDs, "SELECT 1 FROM auth.user_location") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.session") == "42501"
    && await Try(adminDs, "SELECT 1 FROM auth.oidc_replay") == "42501" && await Try(adminDs, "SELECT 1 FROM auth.audit_outbox") == "42501");
// Slice A (DEC-111 write / LS-OQ-61 read): any admin sees and writes every partner's row; nothing without a scope.
const string cntPrb = "SELECT count(*) FROM auth.partner_reason_bounds";
await Check("Slice A RLS prb: no app.admin_type / bogus / partner without app.partner_id -> 0 rows (fail closed)", async () =>
    await CountAs(adminDs, cntPrb, null, null) == 0 && await CountAs(adminDs, cntPrb, "bogus", P_PCI) == 0 && await CountAs(adminDs, cntPrb, "partner", null) == 0);
await Check("Slice A RLS prb: super -> 1 row; partner nevada sees the pci row (cross-partner read, TO CONFIRM LS-OQ-61)", async () =>
    await CountAs(adminDs, cntPrb, "super", null) == 1 && await CountAs(adminDs, cntPrb, "partner", P_NV) == 1);
await Check("Slice A RLS prb: no scope -> INSERT denied (42501); partner nevada CAN insert/update another partner's row (DEC-111, rolled back)", async () =>
    await Try(adminDs, $"INSERT INTO auth.partner_reason_bounds VALUES ('{P_NV}', 10, 500, '{ACTOR}', now(), 1)") == "42501"
    && await Try(adminDs, $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 10, 500, '{ACTOR}', now(), 1)", "partner", P_NV) == "ok"
    && await PrbUpdateRows(P_NV) == 1);
async Task<int> PrbUpdateRows(Guid asPartner)
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await BindResolved(c, tx, "partner", asPartner);
    await using var u = new NpgsqlCommand($"UPDATE auth.partner_reason_bounds SET reason_min_length = 11, reason_max_length = 499, updated_by = '{ACTOR}', updated_at = now(), row_version = row_version + 1 WHERE partner_id = '{P_PCI}'", c, tx);
    var n = await u.ExecuteNonQueryAsync();
    await tx.RollbackAsync();
    return n;
}
await Check("Slice A grants prb: admin UPDATE partner_id / DELETE / TRUNCATE denied; login SELECT denied (42501)", async () =>
    await Try(adminDs, $"UPDATE auth.partner_reason_bounds SET partner_id = gen_random_uuid() WHERE partner_id = '{P_PCI}'", "super") == "42501"
    && await Try(adminDs, "DELETE FROM auth.partner_reason_bounds", "super") == "42501" && await Try(adminDs, "TRUNCATE auth.partner_reason_bounds") == "42501"
    && await Try(loginDs, cntPrb) == "42501");
await Check("owner: FORCE RLS + owner policy -> owner sees all rows", async () => await CountAs(ownerDs, cntLoc, null, null) == 2);
await Check("admin/login: TRUNCATE denied (42501)", async () => await Try(adminDs, "TRUNCATE auth.location_login_config_change") == "42501" && await Try(loginDs, "TRUNCATE auth.oidc_replay") == "42501");

// ================= 6. constraints (owner, each rolled back) =================
var cfgCols = "(location_id, provider_kind, display_name, entra_allowed_tenant_ids, oidc_authority, oidc_client_id, secret_ref, allow_db_fallback, mfa_required, created_by, created_at, updated_by, updated_at, row_version)";
string NewLoc(string id) => $"INSERT INTO auth.location VALUES ('{id}', '{P_PCI}', 'zz-{id[..8]}', 'x');";
const string LX = "5f0c3e1a-00ee-4000-8000-0000000000e1";
string Cfg(string kind, string tids, string auth, string client, string secret, string fallback, string rv = "1") =>
    NewLoc(LX) + $"INSERT INTO auth.location_login_config {cfgCols} VALUES ('{LX}', {kind}, 'd', {tids}, {auth}, {client}, {secret}, {fallback}, false, '{ACTOR}', now(), '{ACTOR}', now(), {rv})";
string ent = $"'{Iss(T2)}'";
var cases = new (string name, string sql, string state)[]
{
    ("location slug uppercase", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'PCI-x', 'x')", "23514"),
    ("location slug leading hyphen", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '-pci', 'x')", "23514"),
    ("location slug 64 chars", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '{new string('a', 64)}', 'x')", "23514"),
    ("location slug 63 chars ok", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', '{new string('a', 63)}', 'x')", "ok"),
    ("location slug duplicate", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'pci-main', 'x')", "23505"),
    ("location name empty", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'zz-n', '')", "23514"),
    ("location name 201", $"INSERT INTO auth.location VALUES (gen_random_uuid(), '{P_PCI}', 'zz-n', '{new string('n', 201)}')", "23514"),
    ("config entra ok", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "true"), "ok"),
    ("config entra 0 tenants", Cfg("'entra'", "'{}'", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra 2 tenants", Cfg("'entra'", $"ARRAY['{T1}','{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra no secret_ref", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "NULL", "false"), "23514"),
    ("config entra no authority", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", "NULL", "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config entra no client id", Cfg("'entra'", $"ARRAY['{T2}']::uuid[]", ent, "NULL", "'arn:FAKE'", "false"), "23514"),
    ("config database ok", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "false"), "ok"),
    ("config database with tenant", Cfg("'database'", $"ARRAY['{T2}']::uuid[]", "NULL", "NULL", "NULL", "false"), "23514"),
    ("config database with secret_ref", Cfg("'database'", "'{}'", "NULL", "NULL", "'arn:FAKE'", "false"), "23514"),
    ("config database with allow_db_fallback", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "true"), "23514"),
    ("config provider 'Entra' (case)", Cfg("'Entra'", $"ARRAY['{T2}']::uuid[]", ent, "'c'", "'arn:FAKE'", "false"), "23514"),
    ("config row_version 0", Cfg("'database'", "'{}'", "NULL", "NULL", "NULL", "false", "0"), "23514"),
    ("config without location (FK)", $"INSERT INTO auth.location_login_config {cfgCols} VALUES (gen_random_uuid(), 'database', 'd', '{{}}', NULL, NULL, NULL, false, false, '{ACTOR}', now(), '{ACTOR}', now(), 1)", "23503"),
    ("delete location with config (RESTRICT)", $"DELETE FROM auth.location WHERE location_id = '{L_NV}'", "23503"),
    ("change reason 9 chars", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '123456789', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change reason 10 chars ok", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '1234567890', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "ok"),
    ("change reason padded", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', ' 1234567890', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change reason 501", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', '{new string('r', 501)}', now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change admin_type 'owner'", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'owner', NULL, now(), 'updated', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change kind 'deleted'", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', NULL, now(), 'deleted', '{{}}', NULL, '{{}}', 2)", "23514"),
    ("change after NULL", $"INSERT INTO auth.location_login_config_change VALUES (gen_random_uuid(), '{L_PCI}', '{ACTOR}', 'super', NULL, now(), 'updated', '{{}}', NULL, NULL, 2)", "23502"),
    ("app_user status 'locked'", "INSERT INTO auth.app_user VALUES (gen_random_uuid(), NULL, 'locked', NULL)", "23514"),
    ("app_user status 'Active'", "INSERT INTO auth.app_user VALUES (gen_random_uuid(), NULL, 'Active', NULL)", "23514"),
    ("external_login provider 'database'", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('database', '{Iss(T1)}', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer for another tenant", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T2)}', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer trailing slash", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}/', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login issuer http", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', 'http://login.microsoftonline.com/{T1}/v2.0', '{T1}', gen_random_uuid(), '{U_C01}')", "23514"),
    ("external_login duplicate PK", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}', '{T1}', '{S1}', '{U_C03}')", "23505"),
    ("external_login unknown user (FK)", $"INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra', '{Iss(T1)}', '{T1}', gen_random_uuid(), gen_random_uuid())", "23503"),
    ("user_location unknown user (FK)", $"INSERT INTO auth.user_location (user_id, location_id) VALUES (gen_random_uuid(), '{L_PCI}')", "23503"),
    ("delete location with user_location (RESTRICT)", $"DELETE FROM auth.location_login_config WHERE location_id = '{L_PCI}'; DELETE FROM auth.location_login_config_change WHERE location_id = '{L_PCI}'; DELETE FROM auth.location WHERE location_id = '{L_PCI}'", "23503"),
    ("prb min 9", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 9, 500, '{ACTOR}', now(), 1)", "23514"),
    ("prb max 501", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 10, 501, '{ACTOR}', now(), 1)", "23514"),
    ("prb min > max", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 60, 59, '{ACTOR}', now(), 1)", "23514"),
    ("prb 10/500 ok", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 10, 500, '{ACTOR}', now(), 1)", "ok"),
    ("prb min = max ok", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 42, 42, '{ACTOR}', now(), 1)", "ok"),
    ("prb row_version 0", $"INSERT INTO auth.partner_reason_bounds VALUES (gen_random_uuid(), 10, 500, '{ACTOR}', now(), 0)", "23514"),
    ("prb duplicate partner (PK)", $"INSERT INTO auth.partner_reason_bounds VALUES ('{P_PCI}', 10, 500, '{ACTOR}', now(), 1)", "23505"),
    ("audit_outbox event_type mismatch", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"b","event_id":"5f0c3e1a-00ff-4000-8000-000000000001"}')""", "23514"),
    ("audit_outbox event_id mismatch", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"a","event_id":"x"}')""", "23514"),
    ("audit_outbox ok", """INSERT INTO auth.audit_outbox VALUES ('5f0c3e1a-00ff-4000-8000-000000000001', now(), 'a', '{"event_type":"a","event_id":"5f0c3e1a-00ff-4000-8000-000000000001"}')""", "ok"),
    ("oidc_replay key_hash 31 bytes", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 31), 'hex'), now() + interval '10 minutes')", "23514"),
    ("oidc_replay kind 'state'", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('state', decode(repeat('ab', 32), 'hex'), now() + interval '10 minutes')", "23514"),
    ("oidc_replay ttl > 24h", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 32), 'hex'), now() + interval '25 hours')", "23514"),
    ("oidc_replay expires <= created", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('nonce', decode(repeat('ab', 32), 'hex'), now())", "23514"),
    ("oidc_replay duplicate (replay)", "INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('code', decode(repeat('ab', 32), 'hex'), now() + interval '5 minutes'); INSERT INTO auth.oidc_replay (kind, key_hash, expires_at) VALUES ('code', decode(repeat('ab', 32), 'hex'), now() + interval '5 minutes')", "23505"),
};
string Sess(string over) =>
    "INSERT INTO auth.session (session_id, cookie_key_hash, user_id, partner_id, location_id, admin_type, session_kind, login_provider, entra_tenant_id, config_row_version, amr, auth_time, last_seen_at, revoked_at, revoked_reason) SELECT * FROM (SELECT gen_random_uuid(), decode(repeat('cd', 32), 'hex'), "
    + $"'{U_C01}'::uuid, '{P_PCI}'::uuid, '{L_PCI}'::uuid, NULL::text, 'normal'::text, 'entra'::text, '{T1}'::uuid, 1::bigint, ARRAY['pwd','mfa']::text[], now(), now(), NULL::timestamptz, NULL::text) s(a,b,c,d,e,f,g,h,i,j,k,l,m,n,o) " + over;
var sessCases = new (string, string, string)[]
{
    ("session entra ok", Sess(""), "ok"),
    ("session entra without tenant", Sess("").Replace($"'{T1}'::uuid", "NULL::uuid"), "23514"),
    ("session entra without config_row_version", Sess("").Replace("1::bigint", "NULL::bigint"), "23514"),
    ("session entra with admin_type", Sess("").Replace("NULL::text, 'normal'", "'super'::text, 'normal'"), "23514"),
    ("session database with tenant", Sess("").Replace("'entra'::text", "'database'::text"), "23514"),
    ("session no scope (no admin, no location)", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace($"'{L_PCI}'::uuid", "NULL::uuid"), "23514"),
    ("session partner admin without partner", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace("NULL::text, 'normal'", "'partner'::text, 'normal'").Replace($"'{P_PCI}'::uuid", "NULL::uuid"), "23514"),
    ("session amr empty", Sess("").Replace("ARRAY['pwd','mfa']::text[]", "'{}'::text[]"), "23514"),
    ("session amr 'wiaormfa'", Sess("").Replace("ARRAY['pwd','mfa']::text[]", "ARRAY['wiaormfa']::text[]"), "23514"),
    ("session recovery_reenroll with mfa", Sess("").Replace("'entra'::text", "'database'::text").Replace($"'{T1}'::uuid", "NULL::uuid").Replace("'normal'::text", "'recovery_reenroll'::text"), "23514"),
    ("session revoked_at without reason", Sess("").Replace("NULL::timestamptz", "now()"), "23514"),
    ("session revoked_reason format", Sess("").Replace("NULL::timestamptz, NULL::text", "now(), 'Logout!'::text"), "23514"),
    ("session cookie hash 31 bytes", Sess("").Replace("repeat('cd', 32)", "repeat('cd', 31)"), "23514"),
    ("session duplicate cookie hash", Sess("") + "; " + Sess(""), "23505"),
};
foreach (var (name, sql, state) in cases.Concat(sessCases))
{
    var got = await Try(ownerDs, sql);
    Ok($"constraint: {name} -> {state}", got == state, got);
}

// ================= 7. optimistic concurrency (§8.5) =================
await Check("concurrency: two EF contexts (admin_api, super) on row_version 1 -> 2nd SaveChanges throws DbUpdateConcurrencyException; row_version 2", async () =>
{
    async Task<(AuthDbContext db, NpgsqlConnection c, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx)> Open()
    {
        var c = await adminDs.OpenConnectionAsync();
        var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(c).Options);
        var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('app.request_iss','https://auth-data.test.invalid',true), set_config('app.request_sub','5f0c3e1a-0003-4000-8000-000000000c73',true), set_config('app.resolved_global_admin','',true), set_config('app.admin_type','',true), set_config('app.partner_id','',true)");
        return (db, c, tx);
    }
    var (db1, c1, t1) = await Open();
    var (db2, c2, t2) = await Open();
    try
    {
        var r1 = await db1.LocationLoginConfigRows.SingleAsync(x => x.LocationId == L_NV);
        var r2 = await db2.LocationLoginConfigRows.SingleAsync(x => x.LocationId == L_NV);
        if (r1.RowVersion != 1 || r2.RowVersion != 1) return false;
        r1.MfaRequired = true; r1.RowVersion += 1; r1.UpdatedAt = DateTimeOffset.UtcNow;
        await db1.SaveChangesAsync(); await t1.CommitAsync();
        r2.DisplayName = "stale writer"; r2.RowVersion += 1;
        try { await db2.SaveChangesAsync(); return false; }
        catch (DbUpdateConcurrencyException) { }
        await t2.RollbackAsync();
        await using var oc = await ownerDs.OpenConnectionAsync();
        return await Scalar<long>(oc, null, $"SELECT row_version FROM auth.location_login_config WHERE location_id = '{L_NV}'") == 2
            && await Scalar<string>(oc, null, $"SELECT display_name FROM auth.location_login_config WHERE location_id = '{L_NV}'") == "Nevada password";
    }
    finally { await db1.DisposeAsync(); await db2.DisposeAsync(); await c1.DisposeAsync(); await c2.DisposeAsync(); }
});
await Check("concurrency: raw §8.5 UPDATE ... WHERE row_version = stale -> 0 rows; = current -> 1 row", async () =>
{
    await using var c = await adminDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await BindResolved(c, tx, "super", null);
    await using var s = new NpgsqlCommand($"UPDATE auth.location_login_config SET row_version = row_version + 1 WHERE location_id = '{L_NV}' AND row_version = 1", c, tx);
    await using var k = new NpgsqlCommand($"UPDATE auth.location_login_config SET row_version = row_version + 1 WHERE location_id = '{L_NV}' AND row_version = 2", c, tx);
    var a = await s.ExecuteNonQueryAsync(); var b = await k.ExecuteNonQueryAsync();
    await tx.RollbackAsync();
    return a == 0 && b == 1;
});

// ================= 8. readers (as login) =================
// [TLD 2026-11-04 10:02 Q1] deployment config is the source of truth for oidc_* / secret_ref: these values differ from the row's
// (row: Iss(T1) / FakeArn), so the checks below prove the reader returns config, never the per-row copy. Same check count (162).
const string CfgArn = "arn:aws:secretsmanager:us-west-2:000000000000:secret:FAKE-test-only/deploy-cfg-AbCdEf"; // SSE2-FAKE-TEST-ONLY
const string CfgClientId = "5f0c3e1a-00cc-4000-8000-0000000000cc"; // SSE2-FAKE-TEST-ONLY
// [TLD 2026-11-04 10:58 F-A1, 10:59] the constructor is internal (no InternalsVisibleTo): built through the validated factory, allowlisted fakes.
var entraCfg = Auth.Data.EntraDeployment.Create("https://login.microsoftonline.com", CfgClientId, CfgArn,
    new HashSet<string> { "000000000000" }, new HashSet<string> { "us-west-2" }).Value ?? throw new InvalidOperationException("fixture EntraDeployment refused");
var cfgReader = new NpgsqlLocationLoginConfigReader(loginDs, entraCfg);
var extReader = new NpgsqlExternalLoginReader(loginDs);
var ulReader = new NpgsqlUserLocationReader(loginDs);
await Check("reader config: GetAsync(pci-main) -> entra row, tenant T1, row_version 1, oidc_* and SecretRef from deployment config not the row (TLD 10:02 Q1), ToString redacted", async () =>
{
    var c = await cfgReader.GetAsync(L_PCI, default);
    return c is not null && c.ProviderKind == ProviderKind.Entra && c.PartnerId == P_PCI && c.Slug == "pci-main" && c.EntraAllowedTenantIds.SequenceEqual([T1])
        && c.OidcAuthority == "https://login.microsoftonline.com" && c.OidcClientId == CfgClientId && c.AllowDbFallback && c.MfaRequired && c.RowVersion == 1
        && c.SecretRef?.Reveal() == CfgArn && CfgArn != FakeArn
        && !c.ToString().Contains("000000000000") && !JsonSerializer.Serialize(c).Contains("000000000000");
});
await Check("reader config: GetAsync(nevada-main) -> database, SecretRef null, row_version 2 (after concurrency check)", async () =>
{
    var c = await cfgReader.GetAsync(L_NV, default);
    return c is { ProviderKind: ProviderKind.Database, SecretRef: null, OidcAuthority: null, OidcClientId: null, RowVersion: 2 } && c.EntraAllowedTenantIds.Count == 0;
});
await Check("reader config: unknown id -> null", async () => await cfgReader.GetAsync(Guid.NewGuid(), default) is null);
await Check("reader config: GetBySlugAsync(\" PCI-Main \") normalises -> pci-main", async () => (await cfgReader.GetBySlugAsync(" PCI-Main ", default))?.LocationId == L_PCI);
await Check("reader config: GetBySlugAsync(unknown) -> null", async () => await cfgReader.GetBySlugAsync("zz-none", default) is null);
await Check("reader config: location without config row -> null (inner join)", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, $"INSERT INTO auth.location VALUES ('{LX}', '{P_PCI}', 'zz-noconfig', 'x')");
    await tx.CommitAsync();
    var r = await cfgReader.GetAsync(Guid.Parse(LX), default) is null && await cfgReader.GetBySlugAsync("zz-noconfig", default) is null;
    await Exec(c, null, $"DELETE FROM auth.location WHERE location_id = '{LX}'");
    return r;
});

await Check("reader ext: linked active user (c01) -> Active, partner pci, not locked, MaySignIn", async () =>
{
    var m = await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default);
    return m is not null && m.UserId == U_C01 && m.PartnerId == P_PCI && m.Status == UserStatus.Active && m.LockedUntil is null && m.MaySignIn(DateTimeOffset.UtcNow);
});
await Check("reader ext: disabled user -> Disabled, MaySignIn false", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S2, default) is { Status: UserStatus.Disabled } m && !m.MaySignIn(DateTimeOffset.UtcNow));
await Check("reader ext: locked user -> Active + locked_until in future, MaySignIn false now, true after", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S3, default) is { Status: UserStatus.Active, LockedUntil: { } lu } m
    && !m.MaySignIn(DateTimeOffset.UtcNow) && m.MaySignIn(lu.AddSeconds(1)));
await Check("reader ext: no external_login row -> null", async () => await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S9, default) is null);
await Check("reader ext: issuer must match exactly (trailing slash / upper case -> null)", async () =>
    await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1) + "/", T1, S1, default) is null
    && await extReader.FindUserAsync(ProviderKind.Entra, Iss(T1).ToUpperInvariant(), T1, S1, default) is null);
await Check("reader ext: right subject, other tenant -> null", async () => await extReader.FindUserAsync(ProviderKind.Entra, Iss(T2), T2, S1, default) is null);
await Throws<ArgumentOutOfRangeException>("reader ext: providerKind Database -> ArgumentOutOfRangeException", () => extReader.FindUserAsync(ProviderKind.Database, Iss(T1), T1, S1, default));

// N3/N1 [TLD 2026-11-04 11:21 Q2b]: states the constraints forbid (orphan link, unknown status) are COMMITTED only in this
// run's marked scratch DB (FK/CHECK dropped there), and read back through the PUBLIC FindUserAsync on a scratch data
// source: it must throw, never null/Active. AUTH_CHECKS_DB itself is never altered. Scratch-only fake ids below.
Guid SU_DEL = Guid.Parse("5f0c3e1a-0003-4000-8000-0000000005d1"), SU_LCK = Guid.Parse("5f0c3e1a-0003-4000-8000-0000000005d2"),
     SU_CAP = Guid.Parse("5f0c3e1a-0003-4000-8000-0000000005d3"), orphan = Guid.Parse("5f0c3e1a-0003-4000-8000-0000000000ff");
Guid SS_DEL = Guid.Parse("5f0c3e1a-00fb-4000-8000-0000000005d1"), SS_LCK = Guid.Parse("5f0c3e1a-00fb-4000-8000-0000000005d2"),
     SS_CAP = Guid.Parse("5f0c3e1a-00fb-4000-8000-0000000005d3");
Task<NpgsqlDataSource>? scratchReady = null;
async Task<NpgsqlDataSource> ScratchBroken()
{
    if (scratchAdminCs is null || scratchName is null) throw new InvalidOperationException($"no scratch DB ({scratchErr})");
    await ScratchDb.PrepareAsync(scratchAdminCs, scratchName);
    scratchDs = NpgsqlDataSource.Create(ScratchDb.OwnerCs(scratchAdminCs, scratchName));
    await using var c = await scratchDs.OpenConnectionAsync();
    await using var tx = await c.BeginTransactionAsync();
    await Exec(c, tx, "INSERT INTO auth.app_user VALUES ($1,$4,'active',NULL),($2,$4,'active',NULL),($3,$4,'active',NULL)", SU_DEL, SU_LCK, SU_CAP, P_PCI);
    await Exec(c, tx, "INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra',$1,$2,$3,$4),('entra',$1,$2,$5,$6),('entra',$1,$2,$7,$8)",
        Iss(T1), T1, SS_DEL, SU_DEL, SS_LCK, SU_LCK, SS_CAP, SU_CAP);
    await Exec(c, tx, "ALTER TABLE auth.external_login DROP CONSTRAINT external_login_user_fk");
    await Exec(c, tx, "ALTER TABLE auth.app_user DROP CONSTRAINT app_user_status");
    await Exec(c, tx, "INSERT INTO auth.external_login (provider_kind, issuer, tenant_id, subject, user_id) VALUES ('entra',$1,$2,$3,$4)", Iss(T1), T1, S9, orphan);
    await Exec(c, tx, "DELETE FROM auth.app_user WHERE user_id = $1", SU_DEL);
    await Exec(c, tx, "UPDATE auth.app_user SET status = 'locked' WHERE user_id = $1", SU_LCK);
    await Exec(c, tx, "UPDATE auth.app_user SET status = 'Active' WHERE user_id = $1", SU_CAP);
    await tx.CommitAsync();
    return scratchDs;
}
async Task<ExternalLoginMatch?> ScratchFind(Guid subject) =>
    await new NpgsqlExternalLoginReader(await (scratchReady ??= ScratchBroken())).FindUserAsync(ProviderKind.Entra, Iss(T1), T1, subject, default);
await Throws<InvalidOperationException>("N3: external_login row without app_user row -> throws (never null); FK dropped in the scratch DB",
    () => ScratchFind(S9), e => e.Message.Contains("no auth.app_user row"));
await Throws<InvalidOperationException>("N3: same via deleting the app_user row (FK dropped, scratch DB) -> throws",
    () => ScratchFind(SS_DEL), e => e.Message.Contains("no auth.app_user row"));
// SSE2 review de9c1946 F2: the N1 checks need the reader's own message, so a missing scratch DB ("no scratch DB (...)") FAILS them.
await Throws<InvalidOperationException>("N1/7:53: unknown status 'locked' (CHECK dropped in the scratch DB) -> throws, never Active",
    () => ScratchFind(SS_LCK), e => e.Message.Contains("auth.app_user.status has an unknown value"));
await Throws<InvalidOperationException>("N1/7:53: status 'Active' (case, scratch DB) -> throws", () => ScratchFind(SS_CAP),
    e => e.Message.Contains("auth.app_user.status has an unknown value"));
await Check("N3: FK and CHECK still present in AUTH_CHECKS_DB (drops committed only in the scratch DB)", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    return await Scalar<long>(c, null, "SELECT count(*) FROM pg_constraint WHERE conname IN ('external_login_user_fk','app_user_status')") == 2;
});

await Check("reader user_location: c01@pci-main true; c01@nevada-main false; c03@pci-main false; unknown false", async () =>
    await ulReader.IsAssignedAsync(U_C01, L_PCI, default) && !await ulReader.IsAssignedAsync(U_C01, L_NV, default)
    && !await ulReader.IsAssignedAsync(U_C03, L_PCI, default) && !await ulReader.IsAssignedAsync(Guid.NewGuid(), L_PCI, default));

// F8: DB failure and cancellation throw; never null/false.
var badCs = new NpgsqlConnectionStringBuilder(loginCs) { Password = "wrong-" + Guid.NewGuid().ToString("N"), Pooling = false }.ConnectionString;
await using (var badDs = NpgsqlDataSource.Create(badCs))
{
    await Throws<PostgresException>("F8: config GetAsync with DB auth failure -> throws (28P01)", () => new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetAsync(L_PCI, default), e => e.SqlState == "28P01");
    await Throws<PostgresException>("F8: config GetBySlugAsync with DB failure -> throws", () => new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetBySlugAsync("pci-main", default));
    await Check("F8: GetBySlugAsync(malformed) -> null without touching the DB (broken data source)", async () =>
        await new NpgsqlLocationLoginConfigReader(badDs, entraCfg).GetBySlugAsync("pci_main!", default) is null);
    await Throws<PostgresException>("F8: FindUserAsync with DB failure -> throws", () => new NpgsqlExternalLoginReader(badDs).FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default));
    await Throws<PostgresException>("F8: IsAssignedAsync with DB failure -> throws (never false)", () => new NpgsqlUserLocationReader(badDs).IsAssignedAsync(U_C01, L_PCI, default));
}
await using (var adminAsLogin = NpgsqlDataSource.Create(adminCs))
{
    await Throws<PostgresException>("F8: reader on a role without SELECT (admin_api on external_login) -> throws 42501", () => new NpgsqlExternalLoginReader(adminAsLogin).FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, default), e => e.SqlState == "42501");
    await Throws<PostgresException>("F8: IsAssignedAsync on a role without SELECT -> throws 42501", () => new NpgsqlUserLocationReader(adminAsLogin).IsAssignedAsync(U_C01, L_PCI, default), e => e.SqlState == "42501");
}
using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    await Throws<OperationCanceledException>("F8: config GetAsync cancelled -> OperationCanceledException", () => cfgReader.GetAsync(L_PCI, cts.Token));
    await Throws<OperationCanceledException>("F8: config GetBySlugAsync cancelled -> OperationCanceledException", () => cfgReader.GetBySlugAsync("pci-main", cts.Token));
    await Throws<OperationCanceledException>("F8: FindUserAsync cancelled -> OperationCanceledException", () => extReader.FindUserAsync(ProviderKind.Entra, Iss(T1), T1, S1, cts.Token));
    await Throws<OperationCanceledException>("F8: IsAssignedAsync cancelled -> OperationCanceledException", () => ulReader.IsAssignedAsync(U_C01, L_PCI, cts.Token));
}
await Check("F8: in-flight cancellation (pg_sleep) -> OperationCanceledException", async () =>
{
    await using var c = await loginDs.OpenConnectionAsync();
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    await using var cmd = new NpgsqlCommand("SELECT pg_sleep(5)", c);
    try { await cmd.ExecuteNonQueryAsync(cts.Token); return false; } catch (OperationCanceledException) { return true; }
});

}
finally
{
    try { await CleanupCheckData(); }
    finally
    {
        if (scratchDs is not null) await scratchDs.DisposeAsync();
        if (scratchAdminCs is not null && scratchName is not null)
            try { await ScratchDb.DropAsync(scratchAdminCs, scratchName); }
            catch (Exception e) { scratchErr = $"drop: {e.GetType().Name}: {e.Message}"; }
    }
}
await Check("Q2: this run's scratch DB dropped in the finally; no MARKED ff_scratch_* DB left [TLD 11:21]; cluster snapshot (roles, members, tablespaces, DBs) identical before CREATE and after DROP [TLD 11:57 o3]; ff_scratchadm as documented [TLD 12:33]", async () =>
{
    if (scratchAdminCs is null || scratchName is null || scratchSnap is null) return false;
    var after = await ScratchDb.ClusterSnapshotAsync(scratchAdminCs);
    if (after != scratchSnap) Console.WriteLine($"Q2: cluster snapshot changed: {ScratchDb.Diff(scratchSnap, after)}");
    var adminV = await ScratchDb.AdminRoleViolationsAsync(scratchAdminCs);
    if (adminV.Count > 0) Console.WriteLine($"Q2: ff_scratchadm: {string.Join("; ", adminV)}");
    return after == scratchSnap && adminV.Count == 0 && !(await ScratchDb.StateAsync(scratchAdminCs, scratchName)).exists && (await ScratchDb.ListAsync(scratchAdminCs, markedOnly: true)).Count == 0;
});
await Check("K3: cleanup -> DB back in the post-seed state (re-runnable)", async () =>
{
    await using var c = await ownerDs.OpenConnectionAsync();
    return await Scalar<long>(c, null, StateSql) == 0;
});

Console.WriteLine($"\n{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
