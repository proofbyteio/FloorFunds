using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Admin.Api.Checks;

/// <summary>
/// Starts the issuer process for the in-process checks. The admin host is given only the URL.
/// The connection string and the public key stay in this process's environment for the child.
/// </summary>
sealed class IssuerHarness : IAsyncDisposable
{
    readonly Process _process;
    readonly string _jwks;
    public string Url { get; }
    public string CallerSecret { get; }

    IssuerHarness(Process process, string url, string jwks, string callerSecret)
    {
        _process = process;
        Url = url;
        _jwks = jwks;
        CallerSecret = callerSecret;
    }

    public static async Task<IssuerHarness> Start(RsaSecurityKey publicKey, string expectedIssuer, string connectionString)
    {
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Auth.Issuer", "bin", "Debug", "net10.0", "Auth.Issuer.dll"));
        if (!File.Exists(dll)) throw new InvalidOperationException("issuer build is missing");
        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var jwks = Path.Combine(Path.GetTempPath(), $"issuer-jwks-{Guid.NewGuid():N}.json");
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(publicKey);
        await File.WriteAllTextAsync(jwks, JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E } } }));
        var callerSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var psi = new ProcessStartInfo("dotnet", [dll, url])
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ISSUER_CONNECTION_STRING"] = connectionString;
        psi.Environment["ISSUER_ISS"] = expectedIssuer;
        psi.Environment["ISSUER_JWKS"] = jwks;
        psi.Environment["ISSUER_CALLER_SECRET"] = callerSecret;
        psi.Environment.Remove("ADMIN_API_CONNECTION_STRING");
        psi.Environment.Remove("CONNECTION_STRING");
        psi.Environment.Remove("LOGIN_CONNECTION_STRING");
        psi.Environment.Remove("STANDIN_CALLER_SECRET");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("issuer did not start");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null && e.Data.Contains("issuer: listening", StringComparison.Ordinal))
                ready.TrySetResult();
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var done = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(15)));
        if (done != ready.Task || process.HasExited)
        {
            try { process.Kill(); } catch { }
            throw new InvalidOperationException("issuer did not listen");
        }
        return new IssuerHarness(process, url, jwks, callerSecret);
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { await _process.WaitForExitAsync(); } catch { }
        _process.Dispose();
        try { File.Delete(_jwks); } catch { }
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
