// Fixture-backed implementations of the draft interfaces (TLD-S2B-10 / TLD-S2B-22): used until SSE's P1 reader and
// interfaces land (plan step 8). No SQL here; never hand-create tables.
using System.Text.Json;
using Login.Entra.Contracts;
using Login.Abstractions;
namespace Login.Entra.Fixtures;

public sealed class EntraFixture
{
    public List<FixtureLocation> Locations { get; set; } = [];
    public List<FixtureLink> Links { get; set; } = [];
    public List<FixtureAssignment> Assignments { get; set; } = [];
    public Dictionary<string, string> Secrets { get; set; } = [];
    public List<FixtureUser> Users { get; set; } = [];
    /// <summary>Deployment-level Entra values (TLD 9:50, TLD 10:12, D-51): one authority, client id and secret ref for
    /// every entra location, as SSE's NpgsqlLocationLoginConfigReader takes them from EntraDeployment (06dc8ac).
    /// Mutable in tests via <see cref="FixtureEntraStore.Mutate"/>; the store reads it on every call, never caches.</summary>
    public FixtureDeployment Deployment { get; set; } = new();
    /// <summary>Test knob: make every reader call throw (SSE contract: readers throw on DB errors).</summary>
    public bool ReadersThrow { get; set; }

    public static EntraFixture Load(string path) =>
        JsonSerializer.Deserialize<EntraFixture>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException(path);
}

public sealed record FixtureLocation(string Slug, LocationLoginConfig Config);
public sealed record FixtureLink(string Issuer, Guid TenantId, Guid ObjectId, Guid UserId, string? Email);
/// <summary>Status is the raw DB text; parsed with UserStatusText.Parse (unknown → throw, fail closed).</summary>
public sealed record FixtureUser(Guid UserId, Guid? PartnerId, string Status, DateTimeOffset? LockedUntil)
{
    /// <summary>Test-only: hand the reader a raw enum value (e.g. default/Unknown) that UserStatusText.Parse never yields.</summary>
    public UserStatus? RawStatus { get; init; }
}
public sealed record FixtureAssignment(Guid UserId, Guid LocationId);

/// <summary>Fixture stand-in for Auth.Data.EntraDeployment (SSE 06dc8ac). Unlike the real type it allows null/blank
/// values, so tests can prove the request-time client_missing backstop (TLD 10:12). SSE2-FAKE-TEST-ONLY.</summary>
public sealed class FixtureDeployment
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? SecretRef { get; set; }

    /// <summary>Same rule as SSE's reader: entra rows get the deployment values, database rows get null for all three.
    /// Row-level authority/client/secret values are ignored (the reader no longer selects them).</summary>
    public LocationLoginConfig Apply(LocationLoginConfig c)
    {
        var entra = c.ProviderKind == ProviderKind.Entra;
        return c with
        {
            OidcAuthority = entra ? Authority : null,
            OidcClientId = entra ? ClientId : null,
            SecretRef = entra && !string.IsNullOrWhiteSpace(SecretRef) ? new SecretReference(SecretRef) : null,
        };
    }
}

/// <summary>Thread-safe, mutable in tests (row_version bump, etc.).</summary>
public sealed class FixtureEntraStore(EntraFixture f) : ILocationLoginConfigReader, IExternalLoginReader, IUserLocationReader, ISecretResolver
{
    private readonly object _gate = new();
    public EntraFixture Data => f;

    public Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(Fill(f.Locations.FirstOrDefault(l => l.Config.LocationId == locationId)?.Config)); }

    public Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct)
    { lock (_gate) return Task.FromResult(Fill(f.Locations.FirstOrDefault(l => string.Equals(l.Slug, slug, StringComparison.Ordinal))?.Config)); }

    // Fresh copy per call from the current Deployment (no caching), like SSE's reader over its startup EntraDeployment.
    private LocationLoginConfig? Fill(LocationLoginConfig? c) => c is null ? null : f.Deployment.Apply(c);

    // (issuer, tid, oid) only — Email is in the fixture only to prove it is never used (ENT-03).
    public Task<ExternalLoginMatch?> FindUserAsync(ProviderKind providerKind, string issuer, Guid tenantId, Guid subject, CancellationToken ct)
    {
        if (providerKind != ProviderKind.Entra) throw new ArgumentOutOfRangeException(nameof(providerKind));
        lock (_gate)
        {
            Guard(ct);
            var l = f.Links.FirstOrDefault(x => string.Equals(x.Issuer, issuer, StringComparison.Ordinal) && x.TenantId == tenantId && x.ObjectId == subject);
            if (l is null) return Task.FromResult<ExternalLoginMatch?>(null);
            // FK external_login → app_user: a link with no user row is a broken DB; throw (fail closed), never null.
            var u = f.Users.FirstOrDefault(x => x.UserId == l.UserId) ?? throw new InvalidOperationException("fixture: external_login without app_user row");
            return Task.FromResult<ExternalLoginMatch?>(new(u.UserId, u.PartnerId, u.RawStatus ?? UserStatusText.Parse(u.Status), u.LockedUntil));
        }
    }

    private void Guard(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (f.ReadersThrow) throw new InvalidOperationException("fixture reader: simulated DB error");
    }

    public Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Assignments.Any(a => a.UserId == userId && a.LocationId == locationId)); }

    public Task<string?> ResolveAsync(SecretReference secretRef, CancellationToken ct)
    {
        lock (_gate)
        {
            ResolveCalls++; LastResolvedRef = secretRef.Reveal();
            return Task.FromResult(f.Secrets.TryGetValue(LastResolvedRef, out var s) ? s : null);
        }
    }

    /// <summary>Test probe (fake refs only, SSE2-FAKE-TEST-ONLY): how often the resolver ran and the last ref it got.</summary>
    public int ResolveCalls { get; private set; }
    public string? LastResolvedRef { get; private set; }

    public void Mutate(Action<EntraFixture> m) { lock (_gate) m(f); }
}

/// <summary>The ONLY place LocationLoginConfig is constructed (named arguments, never positional). Authority,
/// client id and secret ref are deployment-level (D-51) and come from <see cref="FixtureDeployment.Apply"/>, so a
/// row carries none of them.</summary>
public static class FixtureConfig
{
    public static LocationLoginConfig Build(Guid locationId, Guid partnerId, string slug, ProviderKind providerKind, string displayName,
        IReadOnlyList<Guid> tenants, bool allowDbFallback, bool mfaRequired, long rowVersion = 1) =>
        new(LocationId: locationId, PartnerId: partnerId, Slug: slug, ProviderKind: providerKind, DisplayName: displayName,
            EntraAllowedTenantIds: tenants, OidcAuthority: null, OidcClientId: null,
            AllowDbFallback: allowDbFallback, MfaRequired: mfaRequired, RowVersion: rowVersion);
}
