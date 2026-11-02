using System.Security.Cryptography;
using System.Text;

namespace Cachd.Login.TestKit;

/// <summary>RFC 7636 PKCE. The login service accepts S256 only (doc §4.5, plan §3.3).</summary>
public static class Pkce
{
    public static string NewVerifier() => B64Url(RandomNumberGenerator.GetBytes(32));
    public static string S256(string verifier) => B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
