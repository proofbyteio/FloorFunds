using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;

namespace Login.StandIn;

// F-TL-B / DEC-220. Behavioral: authorize for the admin_dev_shell client with no
// admin session must 302 to issuerPath + "/admin?ret=" + escaped ret.
public static class AdminHandoffAuthorizeProbe
{
    public static int Run(WebApplication app, string issuerPath, StandInPorts ports)
    {
        app.Start();
        try
        {
            var redirect = Environment.GetEnvironmentVariable("ADMIN_DEV_SHELL_REDIRECT")
                ?? throw new InvalidOperationException("ADMIN_DEV_SHELL_REDIRECT is required");
            var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            if (challenge.Length < 43) challenge = (challenge + new string('a', 43))[..43];

            var qs =
                "client_id=" + Uri.EscapeDataString(Clients.DevShellClientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString("openid backoffice_api")
                + "&state=st1"
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256";
            var authorizePath = issuerPath + "/connect/authorize?" + qs;

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = http.GetAsync($"http://127.0.0.1:{ports.Front}{authorizePath}").GetAwaiter().GetResult();
            var loc = resp.Headers.Location?.ToString() ?? "";
            if (Uri.TryCreate(loc, UriKind.Absolute, out var abs))
                loc = abs.PathAndQuery;

            var prefix = issuerPath + "/admin?ret=";
            string? detail = null;
            var ok = resp.StatusCode == HttpStatusCode.Redirect && loc.StartsWith(prefix, StringComparison.Ordinal);
            if (ok)
            {
                var retEscaped = loc[prefix.Length..];
                var ret = Uri.UnescapeDataString(retEscaped);
                // ret is the authorize URL the admin page will resume; must be this issuer's authorize with the same client.
                ok = ret.StartsWith(issuerPath + "/connect/authorize?", StringComparison.Ordinal)
                    && ret.Contains("client_id=" + Clients.DevShellClientId, StringComparison.Ordinal)
                    && ret.Contains("redirect_uri=", StringComparison.Ordinal);
                if (!ok) detail = $"ret={ret}";
            }
            else
            {
                detail = $"status={(int)resp.StatusCode} loc={loc}";
            }

            Console.WriteLine((ok ? "PASS " : "FAIL ") + "authorize-hands-off-to-admin-sign-in"
                + (detail is null ? "" : " :: " + detail));
            return ok ? 0 : 1;
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }
}
