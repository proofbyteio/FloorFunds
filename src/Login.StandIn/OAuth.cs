using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Login.StandIn;

// contract 4 (authorize), 7 (token), 9 (revoke), 10 (introspect), 11 (endsession).
public static class OAuth
{
    const int CodeLifetime = 60, RefreshLeeway = 30;

    static IResult BadLink(HttpContext c)
    {
        // 4.8: our own error page, never a redirect, never an echo.
        c.Response.Headers.CacheControl = "no-store";
        c.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        return Ui.Page("Sign-in link not valid", Ui.Render("bad-link"), null, 400);
    }

    static IResult Back(string redirectUri, string issuer, string? state, params (string K, string V)[] kv)
    {
        var q = new Dictionary<string, string?>();
        foreach (var (k, v) in kv) q[k] = v;
        if (state is not null) q["state"] = state;
        q["iss"] = issuer; // 4.10: on every authorization response.
        return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, q));
    }

    static IResult TokenError(HttpContext c, string error, int status = 400)
    {
        c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.Pragma = "no-cache";
        if (error == "invalid_client") c.Response.Headers.WWWAuthenticate = "Basic";
        if (error == "temporarily_unavailable") c.Response.Headers.RetryAfter = "5"; // 7.7 [ASSUMPTION A27] 5 s.
        return Results.Json(new { error }, statusCode: status);
    }

    public static void Map(WebApplication app, StandInState st, string issuerPath, string issuer, StandInPorts ports, TokenIssuer tokens)
    {
        var af = app.Services.GetRequiredService<IAntiforgery>();
        string? Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString();

        app.MapGet(issuerPath + "/connect/authorize", (HttpContext c) =>
        {
            var q = c.Request.Query;
            string? Q(string k) => q.TryGetValue(k, out var v) && v.Count == 1 ? v[0] : null;
            string? clientId = Q("client_id"), redirectUri = Q("redirect_uri"), state = Q("state");
            lock (st.Gate)
            {
                if (clientId is null || redirectUri is null || !st.OAuthClients.TryGetValue(clientId, out var client) || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
                    return BadLink(c);
                c.Response.Headers.CacheControl = "no-store";
                if (Q("response_type") != "code") return Back(redirectUri, issuer, state, ("error", "unsupported_response_type"));
                var challenge = Q("code_challenge");
                if (challenge is null || Q("code_challenge_method") != "S256" || challenge.Length is < 43 or > 128)
                    return Back(redirectUri, issuer, state, ("error", "invalid_request"));
                // 4.9 scopes: openid required; only this client's API scope; offline_access only for refresh clients.
                var scopes = (Q("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                var allowed = new HashSet<string> { "openid" };
                if (client.ApiScope is not null) allowed.Add(client.ApiScope);
                if (client.Refresh) allowed.Add("offline_access");
                if (!scopes.Contains("openid") || scopes.Any(s => !allowed.Contains(s))) return Back(redirectUri, issuer, state, ("error", "invalid_scope"));
                var prompt = Q("prompt");
                if (prompt is not null and not ("none" or "login")) return Back(redirectUri, issuer, state, ("error", "invalid_request"));
                int? maxAge = null;
                if (Q("max_age") is { } ma) { if (!int.TryParse(ma, out var m) || m < 0) return Back(redirectUri, issuer, state, ("error", "invalid_request")); maxAge = m; }
                var now = st.Clock.Now;
                var s = SignIn.CurrentSession(st, c, now);
                bool isHandoffClient = client.Handoff;
                // [ASSUMPTION A23] with no session the user goes to the sign-in page of the location cookie's slug, else the first fixture location.
                string firstSlug = (string)st.Fixtures["partners"]![0]!["locations"]![0]!["slug"]!;
                var slug = s is { AdminType: null } ? s.Slug : (c.Request.Cookies.TryGetValue("standin_loc", out var lc) && SignIn.Location(st.Fixtures, lc) is not null ? lc : firstSlug);
                var keep = q.Where(kv => kv.Key is not ("prompt" or "hub_launch")).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
                string signInUrl(bool resign)
                {
                    var u = QueryHelpers.AddQueryString(issuerPath + "/l/" + slug, "ret", QueryHelpers.AddQueryString(issuerPath + "/connect/authorize", keep));
                    return resign ? u + "&resign=1" : u;
                }
                // 5.7: a presented hub_launch is consumed on the first check, whatever happens next (also when there is no session: A40).
                // OQ-56 (TL 9:42, replaces A41): a hub_launch shown to any other client is burned too, then ignored (like A40).
                var hlRaw = Q("hub_launch");
                HubLaunch? hl = null; bool hlFresh = false;
                if (hlRaw is not null && st.Auth.HubLaunches.TryGetValue(AuthStore.H(hlRaw), out hl)) { hlFresh = !hl.Used; hl.Used = true; }
                if (!isHandoffClient) { hlRaw = null; hl = null; hlFresh = false; }

                if (s is not null && (s.Kind == "recovery_reenroll" || s.AdminType is not null))
                {
                    // 5.13 then 5.12 [ASSUMPTION A34 order], before rules 1-6; [ASSUMPTION A35] applied to every client, not only the hand-off.
                    var rec = s.Kind == "recovery_reenroll";
                    var e = Audit.Build(now, "auth.app_access.denied", "denied", s.UserId, new JsonObject { ["reason"] = rec ? "recovery_session" : "not_location_user" },
                        Handoff.SessionScope(st, s), Ip(c), null, s.Sid);
                    e["target"] = new JsonObject { ["type"] = "app", ["id"] = client.AppCode ?? client.ClientId }; e["session_kind"] = s.Kind;
                    if (s.AdminType is not null) e["admin_type"] = s.AdminType;
                    st.Emit(e);
                    return Back(redirectUri, issuer, state, ("error", "access_denied"));
                }

                bool fresh = s is not null && s.CodeIssued == false && (now - s.AuthTime).TotalSeconds <= 60;
                // [TL Decision 9:26 PM, A32] interactive = now - auth_time <= 300 s AND the session's last authentication was a real
                // credential entry (password + MFA) made for this authorize (ViaReturn), not SSO reuse, a refresh, or prompt=none.
                // auth_time is set only by credential entry (never by SSO or refresh), and the entry is spent by the first interactive code.
                if (isHandoffClient) fresh = s is not null && s.ViaReturn && !s.CodeIssued && !s.InteractiveUsed && prompt != "none" && !Handoff.Over(now, s.AuthTime, Handoff.AuthAgeMs);
                bool needLogin = s is null || (prompt == "login" && !fresh) || (maxAge is { } mx && (now - s.AuthTime).TotalSeconds > mx);
                if (needLogin)
                {
                    // 4.6
                    if (prompt == "none") return Back(redirectUri, issuer, state, ("error", "login_required"));
                    return Results.Redirect(signInUrl(isHandoffClient && s is not null));
                }
                var loc = SignIn.Location(st.Fixtures, s!.Slug)!;
                var user = st.Users.Values.First(u => u.UserId == s.UserId);
                string method = fresh ? "interactive" : "sso";
                if (isHandoffClient && !fresh)
                {
                    // 5.1 rule 4 (5.7): missing, expired (60 s, no skew), used, or wrong sid/client -> notfromhub, then the sign-in page.
                    bool r4 = hl is not null && hlFresh && !Handoff.Over(now, hl.IssuedAt, Handoff.LaunchMs) && hl.Sid == s.Sid && hl.ClientId == client.ClientId;
                    if (!r4)
                    {
                        var e = Audit.Build(now, "auth.app_access.denied", "denied", s.UserId, new JsonObject { ["reason"] = "notfromhub" }, SignIn.Scope(loc), Ip(c), null, s.Sid);
                        e["target"] = new JsonObject { ["type"] = "app", ["id"] = client.AppCode }; e["session_kind"] = s.Kind;
                        st.Emit(e);
                        if (prompt == "none") return Back(redirectUri, issuer, state, ("error", "interaction_required"));
                        return Results.Redirect(signInUrl(true));
                    }
                    // Rules 1, 2, 3, 5, 6: no event (5.10), re-prompt (5.3).
                    if (Handoff.RuleFailure(st, s, user, client, now) is { } _)
                    {
                        if (prompt == "none") return Back(redirectUri, issuer, state, ("error", "interaction_required"));
                        return Results.Redirect(signInUrl(true));
                    }
                    if (prompt == "none") return Back(redirectUri, issuer, state, ("error", "interaction_required")); // the interstitial needs the page
                    // 5.8: the interstitial; the tx binds nonce + hub_launch + session + client_id server-side.
                    var tx = new SignInTx { Id = TokenIssuer.NewHandle(), Slug = s.Slug, Purpose = "handoff", Stage = "handoff", Sid = s.Sid, ClientId = client.ClientId,
                        LaunchHash = AuthStore.H(hlRaw!), Authorize = keep, NonceAt = now,
                        PcId = c.Request.Cookies.TryGetValue(SignIn.PcCookie, out var pc) ? pc : null };
                    st.Auth.Tx[tx.Id] = tx;
                    return Handoff.Interstitial(c, af, tx, issuerPath, ports.Control);
                }
                // 5.1 rule 6: a cage_consumed session is not SSO-eligible for anything (errors.md: prompt=none -> interaction_required).
                if (s.CageConsumed && !fresh)
                {
                    if (prompt == "none") return Back(redirectUri, issuer, state, ("error", "interaction_required"));
                    return Results.Redirect(signInUrl(true));
                }
                return IssueCode(c, client, s, user, loc, redirectUri, state, challenge, Q("nonce"), scopes, method, prompt, maxAge, now);
            }
        });

        IResult IssueCode(HttpContext c, OAuthClient client, LoginSession s, StandInUser user, LocationInfo loc, string redirectUri, string? state, string challenge,
            string? nonce, IEnumerable<string> scopes, string method, string? prompt, int? maxAge, DateTimeOffset now)
        {
            s.LastSeen = now;
            // 4.3; the hub has no app_code and no roles (H1), so no role check for it.
            if (client.AppCode is not null && !user.Roles.Any(r => r.AppCode == client.AppCode))
            {
                st.Emit(Audit.Build(now, "auth.app_access.denied", "denied", s.UserId, new JsonObject { ["reason"] = "no_role" },
                    SignIn.Scope(loc), Ip(c), null, s.Sid).Also(e => { e["target"] = new JsonObject { ["type"] = "app", ["id"] = client.AppCode }; e["session_kind"] = s.Kind; }));
                return Back(redirectUri, issuer, state, ("error", "access_denied"));
            }
            // 4.5 / 5.6: the event and the code in one step; no event, no code. hub_handoff keeps the original auth_time and sid.
            var details = new JsonObject { ["method"] = method, ["auth_time"] = s.AuthTime.ToUnixTimeSeconds(), ["amr"] = new JsonArray(s.Amr.Select(a => (JsonNode)a!).ToArray()) };
            if (prompt is not null) details["prompt"] = prompt;
            if (maxAge is not null) details["max_age"] = maxAge;
            var ev = Audit.Build(now, "auth.app.signed_in", "success", s.UserId, details, SignIn.Scope(loc), Ip(c), null, s.Sid);
            // [ASSUMPTION A39] the hub has no app_code, so its target id is its client_id.
            ev["action"] = "login"; ev["target"] = new JsonObject { ["type"] = "app", ["id"] = client.AppCode ?? client.ClientId }; ev["session_kind"] = s.Kind;
            if (!SignIn.TryEmit(st, ev)) return Back(redirectUri, issuer, state, ("error", "temporarily_unavailable"));
            if (method == "hub_handoff") { s.HandoffUsed = true; s.CageConsumed = true; } // rules 3 and 6
            if (method == "interactive" && client.Handoff) s.InteractiveUsed = true;
            var code = TokenIssuer.NewHandle();
            st.Auth.Codes[AuthStore.H(code)] = new CodeGrant(client.ClientId, redirectUri, challenge, nonce, string.Join(' ', scopes), s.Sid, now);
            s.CodeIssued = true;
            return Back(redirectUri, issuer, state, ("code", code));
        }

        // 5.8: the interstitial re-POSTs authorize with device_attestation in the body only.
        app.MapPost(issuerPath + "/connect/authorize", async (HttpContext c) =>
        {
            SignIn.Headers(c, ports.Control);
            if (!c.Request.HasFormContentType) return Results.BadRequest();
            try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var f = await c.Request.ReadFormAsync();
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                if (!c.Request.Cookies.TryGetValue(Handoff.TxCookie, out var hx) || !st.Auth.Tx.TryGetValue(hx, out var tx) || tx.Purpose != "handoff") return BadLink(c);
                st.Auth.Tx.Remove(tx.Id); // one use
                c.Response.Cookies.Delete(Handoff.TxCookie, new CookieOptions { Path = issuerPath });
                var client = st.OAuthClients[tx.ClientId!];
                var a = tx.Authorize!;
                string redirectUri = a["redirect_uri"]!; string? state = a.GetValueOrDefault("state");
                var s = SignIn.CurrentSession(st, c, now);
                st.Auth.Sessions.TryGetValue(tx.Sid!, out var bound);
                var loc = SignIn.Location(st.Fixtures, tx.Slug)!;
                var slugRet = QueryHelpers.AddQueryString(issuerPath + "/l/" + tx.Slug, "ret", QueryHelpers.AddQueryString(issuerPath + "/connect/authorize", a)) + "&resign=1";
                // 5.8b: the session must be the one the tx is bound to; a mismatch is attestation_invalid.
                var (ok, remote) = Attestation.Check(st, tx, f["device_attestation"].ToString(), issuer, "handoff", now);
                if (s is null || s.Sid != tx.Sid || f.ContainsKey("hub_launch")) ok = false;
                var uid = bound?.UserId ?? s?.UserId;
                JsonObject Deny(string reason)
                {
                    var e = Audit.Build(now, "auth.app_access.denied", "denied", uid, new JsonObject { ["reason"] = reason }, SignIn.Scope(loc), Ip(c), null, tx.Sid);
                    e["target"] = new JsonObject { ["type"] = "app", ["id"] = client.AppCode }; e["session_kind"] = bound?.Kind ?? "normal";
                    return e;
                }
                if (!ok) { st.Emit(Deny("attestation_invalid")); return Results.Redirect(slugRet + "&agent=down"); } // 5.5: password + MFA, then Cash Advance opens
                if (remote) { st.Emit(Deny("remote_session")); return Ui.Page("Sign-in is blocked", Ui.Render("screen-8a")); } // 5.4
                var user = st.Users.Values.First(u => u.UserId == s!.UserId);
                if (Handoff.RuleFailure(st, s!, user, client, now) is not null) return Results.Redirect(slugRet); // rules re-checked at completion
                int? maxAge = a.TryGetValue("max_age", out var ma) && int.TryParse(ma, out var m) ? m : null;
                var scopes = (a.GetValueOrDefault("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct();
                return IssueCode(c, client, s!, user, loc, redirectUri, state, a["code_challenge"]!, a.GetValueOrDefault("nonce"), scopes, "hub_handoff", null, maxAge, now);
            }
        });

        app.MapPost(issuerPath + "/connect/token", async (HttpContext c) =>
        {
            if (!c.Request.HasFormContentType) return TokenError(c, "invalid_request");
            var f = await c.Request.ReadFormAsync();
            lock (st.Gate)
            {
                var client = Clients.Authenticate(c.Request, st.OAuthClients, f, issuer, st.Clock.Now, st.Auth.AssertionJti);
                if (client is null) return TokenError(c, "invalid_client", 401);
                // 7.7 [ASSUMPTION A28] the outbox fault makes the token endpoint unavailable; nothing is consumed or issued.
                if (st.OutboxFault == "fail") return TokenError(c, "temporarily_unavailable", 503);
                var now = st.Clock.Now;
                var grant = f["grant_type"].ToString();
                LoginSession? s; string scope; string? nonce = null; Family fam; RefreshRec? usedRefresh = null;
                if (grant == "authorization_code")
                {
                    var code = f["code"].ToString();
                    if (!st.Auth.Codes.TryGetValue(AuthStore.H(code), out var g) || g.ClientId != client.ClientId) return TokenError(c, "invalid_grant");
                    if (g.Used)
                    {
                        if (g.FamilyId is not null) st.Auth.RevokeFamily(g.FamilyId); // RFC 6749 10.5: revoke what the code gave.
                        return TokenError(c, "invalid_grant");
                    }
                    g.Used = true;
                    if ((now - g.IssuedAt).TotalSeconds > CodeLifetime || f["redirect_uri"].ToString() != g.RedirectUri) return TokenError(c, "invalid_grant");
                    var verifier = f["code_verifier"].ToString();
                    var calc = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
                    if (verifier.Length is < 43 or > 128 || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(calc), Encoding.ASCII.GetBytes(g.Challenge)))
                        return TokenError(c, "invalid_grant");
                    if (!st.Auth.Sessions.TryGetValue(g.Sid, out s) || s.Revoked || SessionCap.Expired(st, s, now)) return TokenError(c, "invalid_grant");
                    scope = g.Scope; nonce = g.Nonce;
                    fam = new Family { Id = TokenIssuer.NewHandle(), ClientId = client.ClientId, Sid = s.Sid, Scope = scope };
                    st.Auth.Families[fam.Id] = fam; g.FamilyId = fam.Id;
                }
                else if (grant == "refresh_token")
                {
                    if (!st.Auth.Refresh.TryGetValue(AuthStore.H(f["refresh_token"].ToString()), out var rr) || !st.Auth.Families.TryGetValue(rr.FamilyId, out fam!)
                        || fam.ClientId != client.ClientId || fam.Revoked) return TokenError(c, "invalid_grant");
                    s = st.Auth.Sessions[fam.Sid];
                    var loc = SignIn.Location(st.Fixtures, s.Slug)!;
                    if (rr.RotatedAt is { } ra && (now - ra).TotalSeconds > RefreshLeeway)
                    {
                        // 7.2 reuse after the leeway.
                        st.Auth.RevokeFamily(fam.Id);
                        var e = Audit.Build(now, "auth.refresh.reuse_detected", "failure", s.UserId, new JsonObject(), SignIn.Scope(loc), Ip(c), null, s.Sid);
                        e["session_kind"] = s.Kind; st.Emit(e);
                        return TokenError(c, "invalid_grant");
                    }
                    var user = st.Users.Values.FirstOrDefault(u => u.UserId == s.UserId);
                    // 7.3 status re-check.
                    if (user is null || user.Status != "active" || user.LockedUntil > now)
                    {
                        if (!s.Revoked)
                        {
                            st.Auth.RevokeSession(s);
                            var e = Audit.Build(now, "auth.session.revoked", "success", s.UserId, new JsonObject { ["reason"] = "user_disabled" }, SignIn.Scope(loc), Ip(c), null, s.Sid);
                            e["session_kind"] = s.Kind; st.Emit(e);
                        }
                        return TokenError(c, "invalid_grant");
                    }
                    // 7.3 role loss: only this client's family.
                    if (client.AppCode is not null && !user.Roles.Any(r => r.AppCode == client.AppCode)) { st.Auth.RevokeFamily(fam.Id); return TokenError(c, "invalid_grant"); }
                    if (s.Revoked || SessionCap.Expired(st, s, now)) { st.Auth.RevokeFamily(fam.Id); return TokenError(c, "invalid_grant"); }
                    // OQ-52: replay inside the 30 s leeway -> the same successor pair, no new chain.
                    if (rr.Successor is { } prev)
                    { c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.Pragma = "no-cache"; return Results.Json(prev.DeepClone()); }
                    rr.RotatedAt ??= now; usedRefresh = rr;
                    scope = fam.Scope;
                }
                else return TokenError(c, "unsupported_grant_type");

                var u2 = st.Users.Values.First(u => u.UserId == s.UserId);
                var l2 = SignIn.Location(st.Fixtures, s.Slug)!;
                var idt = tokens.Id(s, client, now, nonce);
                st.Auth.IdTokenSid[AuthStore.H(idt)] = s.Sid;
                JsonObject body;
                // [TL Decision 9:26 PM, H1 revised] an openid-only client (the hub, Aud null) gets an id_token and no access token.
                if (client.Aud is null) body = new JsonObject { ["id_token"] = idt, ["scope"] = scope };
                else
                {
                    var (at, jti, _) = tokens.Access(s, client, scope, now, u2, new JsonLookup(l2.PartnerId, l2.CachdClientId));
                    st.Auth.Access[jti] = new AccessRec(jti, client.ClientId, s.Sid, fam.Id);
                    body = new JsonObject { ["access_token"] = at, ["token_type"] = "Bearer", ["expires_in"] = TokenIssuer.AccessLifetime, ["id_token"] = idt, ["scope"] = scope };
                }
                if (scope.Split(' ').Contains("offline_access"))
                {
                    var rt = TokenIssuer.NewHandle();
                    st.Auth.Refresh[AuthStore.H(rt)] = new RefreshRec { FamilyId = fam.Id };
                    body["refresh_token"] = rt;
                }
                if (usedRefresh is not null) usedRefresh.Successor = (JsonObject)body.DeepClone();
                c.Response.Headers.CacheControl = "no-store"; c.Response.Headers.Pragma = "no-cache";
                return Results.Json(body);
            }
        });

        // 9: RFC 7009.
        app.MapPost(issuerPath + "/connect/revoke", async (HttpContext c) =>
        {
            if (!c.Request.HasFormContentType) return TokenError(c, "invalid_request");
            var f = await c.Request.ReadFormAsync();
            var token = f["token"].ToString();
            var jwt = token.Count(ch => ch == '.') == 2 ? await tokens.Validate(token, "at+jwt", false, st.Clock.Now) : null;
            lock (st.Gate)
            {
                var client = Clients.Authenticate(c.Request, st.OAuthClients, f, issuer, st.Clock.Now, st.Auth.AssertionJti);
                if (client is null) return TokenError(c, "invalid_client", 401);
                if (st.Auth.Refresh.TryGetValue(AuthStore.H(token), out var rr) && st.Auth.Families.TryGetValue(rr.FamilyId, out var fam) && fam.ClientId == client.ClientId)
                    st.Auth.RevokeFamily(fam.Id); // 9.2
                else if (jwt is not null && st.Auth.Access.TryGetValue(jwt.Id, out var a) && a.ClientId == client.ClientId) a.Revoked = true;
                c.Response.Headers.CacheControl = "no-store";
                return Results.Ok();
            }
        });

        // 10: RFC 7662; inactive is exactly {"active":false}.
        app.MapPost(issuerPath + "/connect/introspect", async (HttpContext c) =>
        {
            if (!c.Request.HasFormContentType) return TokenError(c, "invalid_request");
            var f = await c.Request.ReadFormAsync();
            var token = f["token"].ToString();
            var now = st.Clock.Now;
            var jwt = token.Count(ch => ch == '.') == 2 ? await tokens.Validate(token, "at+jwt", true, now) : null;
            lock (st.Gate)
            {
                var client = Clients.Authenticate(c.Request, st.OAuthClients, f, issuer, st.Clock.Now, st.Auth.AssertionJti);
                if (client is null) return TokenError(c, "invalid_client", 401);
                c.Response.Headers.CacheControl = "no-store";
                // [ASSUMPTION A29] introspect answers for access tokens only; a refresh token is reported inactive.
                if (jwt is null || !st.Auth.Access.TryGetValue(jwt.Id, out var a) || a.Revoked || a.ClientId != client.ClientId)
                    return Results.Json(new { active = false });
                return Results.Json(new { active = true, sub = jwt.Subject, client_id = a.ClientId, exp = jwt.GetPayloadValue<long>("exp"),
                    iat = jwt.GetPayloadValue<long>("iat"), scope = jwt.GetPayloadValue<string>("scope"), token_type = "Bearer" });
            }
        });

        // 11: RP-initiated logout.
        async Task<IResult> EndSession(HttpContext c)
        {
            SignIn.Headers(c, ports.Control);
            var isPost = HttpMethods.IsPost(c.Request.Method);
            IFormCollection? form = isPost && c.Request.HasFormContentType ? await c.Request.ReadFormAsync() : null;
            if (isPost) { try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); } }
            string? P(string k) => form is not null && form.ContainsKey(k) ? form[k].ToString() : c.Request.Query.TryGetValue(k, out var v) ? v.ToString() : null;
            var hint = P("id_token_hint");
            var hinted = hint is null ? null : await tokens.Validate(hint, "JWT", false, st.Clock.Now); // 11.2a ignores exp.
            lock (st.Gate)
            {
                var now = st.Clock.Now;
                LoginSession? s = null;
                if (hinted is not null && st.OAuthClients.ContainsKey(hinted.GetPayloadValue<string>("aud") ?? "")
                    && st.Auth.IdTokenSid.TryGetValue(AuthStore.H(hint!), out var sid)) st.Auth.Sessions.TryGetValue(sid, out s);
                else if (isPost && P("confirm") == "yes" && c.Request.Cookies.TryGetValue(SignIn.SessionCookie, out var csid)) st.Auth.Sessions.TryGetValue(csid, out s);
                else if (c.Request.Cookies.ContainsKey(SignIn.SessionCookie))
                {
                    // 11.2d: no valid hint, so confirm first (logout CSRF).
                    var t = af.GetAndStoreTokens(c).RequestToken!;
                    return SignIn.Html("screen-signout-confirm", $"""
<p>Sign out of all apps?</p><form method="post" action="{issuerPath}/connect/endsession">
<input type="hidden" name="__RequestVerificationToken" value="{System.Net.WebUtility.HtmlEncode(t)}"><input type="hidden" name="confirm" value="yes">
<button data-testid="signout-confirm" type="submit">Sign out</button></form>
""");
                }
                var slug = s is { AdminType: null } ? s.Slug : (string)st.Fixtures["partners"]![0]!["locations"]![0]!["slug"]!;
                if (s is not null && !s.Revoked)
                {
                    st.Auth.RevokeSession(s);
                    var e = Audit.Build(now, "auth.session.revoked", "success", s.UserId, new JsonObject { ["reason"] = "sign_out" }, Handoff.SessionScope(st, s), Ip(c), null, s.Sid);
                    e["session_kind"] = s.Kind; if (s.AdminType is not null) e["admin_type"] = s.AdminType; st.Emit(e); // 11.3: one per session.
                }
                c.Response.Cookies.Delete(SignIn.SessionCookie, new CookieOptions { Path = issuerPath });
                // 11.2c: the end state is always the session's /l/{slug}. [ASSUMPTION A30] post_logout_redirect_uri is never followed.
                return Results.Redirect(issuerPath + "/l/" + slug);
            }
        }
        app.MapGet(issuerPath + "/connect/endsession", (Delegate)EndSession);
        app.MapPost(issuerPath + "/connect/endsession", (Delegate)EndSession);
    }

    static JsonObject Also(this JsonObject o, Action<JsonObject> a) { a(o); return o; }
}
