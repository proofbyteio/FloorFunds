using System.Net;
using System.Net.Sockets;
namespace Login.Entra;

/// <summary>Never 8080/8081; never 18080–18082 (SI1 stand-in); only 18090+; and the port must be free.</summary>
public static class PortGuard
{
    public static void Check(int port)
    {
        if (port is 8080 or 8081) throw new InvalidOperationException($"port {port} is forbidden (8080/8081)");
        if (port is >= 18080 and <= 18082) throw new InvalidOperationException($"port {port} is reserved for the SI1 stand-in");
        if (port < 18090 || port > 18199) throw new InvalidOperationException($"port {port} is outside the SSE2 range 18090–18199");
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); }
        catch (SocketException) { throw new InvalidOperationException($"port {port} is already in use"); }
    }
}
