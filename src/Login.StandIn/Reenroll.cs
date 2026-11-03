using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;

namespace Login.StandIn;

// Contract 7.8: MFA re-enrolment for a recovery_reenroll session (DEC-085, DEC-087, DEC-093; TL 8:37/8:41/8:48 PM; step 3 resumed TL 9:50 PM).
// Front only (listener rules), same-origin, antiforgery, no-store; any other session (or none) gets 404.
public static class Reenroll
{
    public const int CodeCount = 10; // [ASSUMPTION A49] 10 new recovery codes (contract states count >= 1 only).

    static string B32(byte[] b)
    {
        const string A = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"; var sb = new StringBuilder(); int buf = 0, bits = 0;
        foreach (var x in b) { buf = (buf << 8) | x; bits += 8; while (bits >= 5) { sb.Append(A[(buf >> (bits - 5)) & 31]); bits -= 5; } }
        if (bits > 0) sb.Append(A[(buf << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static void Map(WebApplication app, StandInState st, string issuerPath, StandInPorts ports, IConfiguration cfg)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        var max = cfg.GetValue("LoginService:Mfa:ReenrollRateLimit:MaxFailures", 5);
        var window = cfg.GetValue("LoginService:Mfa:ReenrollRateLimit:Window", TimeSpan.FromMinutes(15));
        string P = issuerPath + "/mfa/reenroll";
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();
        string? Ua(HttpContext c) => c.Request.Headers.UserAgent.ToString() is { Length: > 0 } u ? u : null;
        bool SameOrigin(HttpContext c) =>
            !c.Request.Headers.TryGetValue("Origin", out var o) || o.ToString() == $"http://localhost:{ports.Front}" || o.ToString() == $"http://127.0.0.1:{ports.Front}";
        LoginSession? Rec(HttpContext c) => SignIn.CurrentSession(st, c, st.Clock.Now) is { Kind: "recovery_reenroll" } s ? s : null;
        JsonObject Ev(HttpContext c, LoginSession s, string type, string outcome, JsonObject d)
        {
            var e = Audit.Build(st.Clock.Now, type, outcome, s.UserId, d, Handoff.SessionScope(st, s), Ip(c), Ua(c), s.Sid);
            e["session_kind"] = s.Kind; if (s.AdminType is not null) e["admin_type"] = s.AdminType;
            return e;
        }
        // UX 03 D-92: admin states show "Admin sign-in" (no partner/location names); location states a location line.
        // [ASSUMPTION A58] fixtures carry no location display name, so the line shows the slug.
        string Sub(LoginSession s) => s.AdminType is not null ? "Admin sign-in" : "Location: " + s.Slug;
        JsonObject Target(LoginSession s) => new() { ["type"] = "user", ["id"] = s.UserId };
        IResult Page(HttpContext c, LoginSession s, string? error, int status = 200)
        {
            var key = B32(s.PendingSecret!);
            var uri = $"otpauth://totp/Login:{Uri.EscapeDataString(s.Username)}?secret={key}&issuer=Login&digits=6&period=30";
            var t = af.GetAndStoreTokens(c).RequestToken!;
            // [GAP A51] no QR image: a QR encoder needs a package (TL ruling) or a hand-written encoder; manual key + otpauth link only.
            return Ui.Page("Set up your code app again", Ui.Render("reenroll", ("error", Ui.Error(error)), ("key", key), ("uri", uri),
                ("action", P), ("af", t), ("signout", issuerPath + "/connect/endsession"), ("sub", Sub(s))), null, status);
        }

        app.MapGet(P, (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            lock (st.Gate)
            {
                if (Rec(c) is not { } s || s.NewRecoveryCodes is not null) return Results.NotFound();
                s.LastSeen = st.Clock.Now;
                s.PendingSecret ??= RandomNumberGenerator.GetBytes(20); // held on the session until confirmed
                return Page(c, s, null);
            }
        });

        app.MapPost(P, async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            lock (st.Gate) { if (Rec(c) is not { NewRecoveryCodes: null, PendingSecret: not null }) return Results.NotFound(); }
            if (!SameOrigin(c)) return Results.BadRequest();
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var code = (await c.Request.ReadFormAsync())["totp_code"].ToString().Trim();
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                if (Rec(c) is not { NewRecoveryCodes: null, PendingSecret: not null } s) return Results.NotFound();
                var user = st.Users.Values.First(u => u.UserId == s.UserId);
                s.LastSeen = now;
                user.ReenrollFailures.RemoveAll(t => now - t >= window);
                // [ASSUMPTION A50] "5 failures per 15 min": the 6th try inside the window is refused (429) without checking the code.
                if (user.ReenrollFailures.Count >= max)
                {
                    st.Emit(Ev(c, s, "auth.mfa.failed", "failure", new JsonObject { ["method"] = "totp", ["step"] = "reenroll", ["reason"] = "rate_limited" }));
                    return Page(c, s, SignIn.Generic, 429);
                }
                // G1 +-30 s against the pending secret; SEC76: the matched step is recorded so the same code cannot sign in afterwards.
                var cur = Totp.Step(now); long? hit = null;
                if (code.Length == 6) foreach (var stp in new[] { cur - 1, cur, cur + 1 }) if (Totp.CodeAt(s.PendingSecret!, stp) == code) { hit = stp; break; }
                bool ok = hit is not null;
                if (!ok)
                {
                    user.ReenrollFailures.Add(now);
                    st.Emit(Ev(c, s, "auth.mfa.failed", "failure", new JsonObject { ["method"] = "totp", ["step"] = "reenroll" }));
                    return Page(c, s, SignIn.Generic);
                }
                var codes = Enumerable.Range(0, CodeCount).Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant()).ToList();
                var e1 = Ev(c, s, "auth.mfa.reenrolled", "success", new JsonObject { ["method"] = "totp", ["old_factor_revoked"] = true, ["old_recovery_codes_revoked"] = true });
                var e2 = Ev(c, s, "auth.mfa.recovery_codes_issued", "success", new JsonObject { ["count"] = codes.Count });
                e1["target"] = Target(s); e2["target"] = Target(s);
                if (!SignIn.TryEmit(st, e1)) return Page(c, s, SignIn.Generic); // no event, no change (A25 pattern)
                st.Emit(e2);
                // Old secret and every old recovery code revoked at once.
                user.TotpSecret = s.PendingSecret!; s.PendingSecret = null; user.LastTotpStep = hit!.Value;
                user.RecoveryCodeHashes.Clear(); foreach (var rc in codes) user.RecoveryCodeHashes.Add(AuthStore.H(rc));
                user.ReenrollFailures.Clear();
                s.NewRecoveryCodes = codes; // shown once by GET .../recovery-codes; never logged
                return Results.Redirect(P + "/recovery-codes", false, false);
            }
        });

        app.MapGet(P + "/recovery-codes", (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                if (Rec(c) is not { NewRecoveryCodes: { } codes } s) return Results.NotFound();
                // DEC-087: shown once, then the session ends exactly like a sign-out (revoked, cookie cleared). A second GET has no session -> 404.
                s.NewRecoveryCodes = null;
                st.Auth.RevokeSession(s);
                st.Emit(Ev(c, s, "auth.session.revoked", "success", new JsonObject { ["reason"] = "reenrolled" }));
                c.Response.Cookies.Delete(SignIn.SessionCookie, new CookieOptions { Path = issuerPath });
                // DEC-093: back to sign-in of the same kind. [ASSUMPTION A52] the redirect is the page's Continue link, since a 302 would hide the codes.
                var next = s.AdminType is not null ? issuerPath + "/admin" : issuerPath + "/l/" + s.Slug;
                var li = string.Concat(codes.Select(x => $"<li><code>{WebUtility.HtmlEncode(x)}</code></li>"));
                return Ui.Page("Your authenticator is set up", Ui.Render("recovery-codes", ("codes", li), ("next", next), ("sub", Sub(s))));
            }
        });
    }
}
