using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
namespace Login.Entra;

/// <summary>Records the last success/failure of each back-channel document fetch (discovery D1, JWKS D2).</summary>
internal sealed class TrackingDocumentRetriever(HttpClient http, bool requireHttps, TimeProvider clock) : IDocumentRetriever
{
    private readonly HttpDocumentRetriever _inner = new(http) { RequireHttps = requireHttps };
    public ConcurrentDictionary<string, DateTimeOffset> LastSuccess { get; } = new();
    public ConcurrentDictionary<string, string> LastError { get; } = new();

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        try
        {
            var doc = await _inner.GetDocumentAsync(address, cancel).ConfigureAwait(false);
            LastSuccess[address] = clock.GetUtcNow();
            LastError.TryRemove(address, out _);
            return doc;
        }
        catch (Exception ex)
        {
            LastError[address] = IsTimeout(ex) ? "timeout" : "failed";
            throw;
        }
    }

    internal static bool IsTimeout(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is TaskCanceledException or TimeoutException) return true;
        return false;
    }
}

/// <summary>
/// The handler's single IConfigurationManager. It picks the per-authority framework ConfigurationManager (which keeps the
/// last-good copy, TLD-S2B-03) for the location bound to the current request (HttpContext.Items, set from protected state).
/// </summary>
public sealed class PerLocationConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>
{
    public const string ItemKey = "entra.location";
    private readonly IHttpContextAccessor _http;
    private readonly EntraOptions _opts;
    private readonly TimeProvider _clock;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<(string, long), (ConfigurationManager<OpenIdConnectConfiguration> Mgr, TrackingDocumentRetriever Docs)> _byAuthority = new();

    public PerLocationConfigurationManager(IHttpContextAccessor http, EntraOptions opts, TimeProvider clock, HttpMessageHandler? handler = null)
    {
        _http = http; _opts = opts; _clock = clock;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = opts.BackchannelTimeout; // no inline retry
    }

    private (ConfigurationManager<OpenIdConnectConfiguration> Mgr, TrackingDocumentRetriever Docs) For(EntraLocation loc)
        => _byAuthority.GetOrAdd((loc.Authority, loc.Config.RowVersion), _ =>
        {
            var docs = new TrackingDocumentRetriever(_client, requireHttps: !_opts.AllowInsecureTestAuthority, _clock);
            var mgr = new ConfigurationManager<OpenIdConnectConfiguration>(loc.MetadataAddress, new OpenIdConnectConfigurationRetriever(), docs)
            {
                AutomaticRefreshInterval = _opts.LkgMaxAge < TimeSpan.FromHours(12) ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(12),
                RefreshInterval = _opts.RefreshInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : _opts.RefreshInterval,
            };
            return (mgr, docs);
        });

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(EntraLocation loc, CancellationToken ct) => For(loc).Mgr.GetConfigurationAsync(ct);

    public void RequestRefresh(EntraLocation loc) => For(loc).Mgr.RequestRefresh();

    internal TrackingDocumentRetriever Docs(EntraLocation loc) => For(loc).Docs;

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        var loc = _http.HttpContext?.Items[ItemKey] as EntraLocation
                  ?? throw new InvalidOperationException("no Entra location bound to this request (fail closed)");
        return GetConfigurationAsync(loc, cancel);
    }

    public void RequestRefresh()
    {
        if (_http.HttpContext?.Items[ItemKey] is EntraLocation loc) RequestRefresh(loc);
    }
}
