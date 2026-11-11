using System.Text;
using System.Text.Json;

namespace Login.StandIn;

/// <summary>
/// Tells the issuer process to revoke every live request session for this sign-out.
/// The caller secret is the stand-in's own setting. The admin host does not hold it.
/// When the stand-in is not pointed at an issuer, sign-out still clears the in-memory session.
/// </summary>
public static class RequestSessionRevoker
{
    public static async Task NotifyAsync(string iss, string userId)
    {
        var url = Environment.GetEnvironmentVariable("STANDIN_ISSUER_URL");
        var secret = Environment.GetEnvironmentVariable("STANDIN_CALLER_SECRET");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(userId)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var msg = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/v1/revoke");
            msg.Headers.TryAddWithoutValidation("X-Caller-Secret", secret);
            msg.Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, string> { ["iss"] = iss, ["sub"] = userId }),
                Encoding.UTF8, "application/json");
            await http.SendAsync(msg);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }
}
