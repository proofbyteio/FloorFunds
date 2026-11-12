using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;

namespace Login.StandIn;

// F-TL-B / DEC-220. Behavioral: authorize for admin_dev_shell with no admin session
// must 302 to exactly issuerPath + "/admin?ret=" + Uri.EscapeDataString(ret).
// issuer / issuerPath are config-only (Program.cs StandIn:Issuer), never from Host.
public static class AdminHandoffAuthorizeProbe
{
    public static int Run(WebApplication app, string issuer, string issuerPath, StandInPorts ports)
    {
        app.Start();
        try
        {
            var redirect = Environment.GetEnvironmentVariable("ADMIN_DEV_SHELL_REDIRECT")
                ?? throw new InvalidOperationException("ADMIN_DEV_SHELL_REDIRECT is required");
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (challenge.Length < 43) challenge = (challenge + new string('a', 43))[..43];

            // Same keep shape the authorize handler reads from the query (excl. prompt/hub_launch).
            var keep = new Dictionary<string, string?>
            {
                ["client_id"] = Clients.DevShellClientId,
                ["redirect_uri"] = redirect,
                ["response_type"] = "code",
                ["scope"] = "openid backoffice_api",
                ["state"] = "st1",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            };
            var authorizePath = QueryHelpers.AddQueryString(issuerPath + "/connect/authorize", keep);
            var expectedRet = QueryHelpers.AddQueryString(issuerPath + "/connect/authorize", keep);
            // Real product Location is path-relative (curl on 96410c75 / tip).
            var expectedLoc = issuerPath + "/admin?ret=" + Uri.EscapeDataString(expectedRet);

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            var failed = 0;

            // (b) Exact Location equality — raw header string, no PathAndQuery stripping.
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ports.Front}{authorizePath}"))
            using (var resp = http.SendAsync(req).GetAwaiter().GetResult())
            {
                var loc = resp.Headers.Location?.ToString() ?? "";
                var ok = resp.StatusCode == HttpStatusCode.Redirect && loc == expectedLoc;
                Console.WriteLine((ok ? "PASS " : "FAIL ") + "authorize-hands-off-to-admin-sign-in"
                    + (ok ? "" : $" :: status={(int)resp.StatusCode} loc={loc} expected={expectedLoc}"));
                if (!ok) failed = 1;
            }

            // (c) Forged Host / X-Forwarded-* must not change Location (config issuer only).
            // Always run even if (b) failed so forged-host mutants still print their FAIL line.
            using (var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ports.Front}{authorizePath}"))
            {
                req.Headers.Host = "evil.example";
                req.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
                req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                var loc = resp.Headers.Location?.ToString() ?? "";
                var ok = resp.StatusCode == HttpStatusCode.Redirect
                    && loc == expectedLoc
                    && !loc.Contains("evil.example", StringComparison.OrdinalIgnoreCase)
                    && !loc.StartsWith("https://", StringComparison.Ordinal);
                Console.WriteLine((ok ? "PASS " : "FAIL ") + "authorize-handoff-ignores-forged-host"
                    + (ok ? "" : $" :: status={(int)resp.StatusCode} loc={loc} expected={expectedLoc}"));
                if (!ok) failed = 1;
            }

            // Quiet reference so issuer (config) is visibly tied to the probe contract.
            _ = issuer;
            return failed;
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
