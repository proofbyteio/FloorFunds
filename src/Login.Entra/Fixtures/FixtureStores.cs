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
    /// <summary>Test knob: make every reader call throw (SSE contract: readers throw on DB errors).</summary>
    public bool ReadersThrow { get; set; }

    public static EntraFixture Load(string path) =>
        JsonSerializer.Deserialize<EntraFixture>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException(path);
}

public sealed record FixtureLocation(string Slug, LocationLoginConfig Config);
public sealed record FixtureLink(string Issuer, Guid TenantId, Guid ObjectId, Guid UserId, string? Email);
/// <summary>Status is the raw DB text; parsed with UserStatusText.Parse (unknown → throw, fail closed).</summary>
public sealed record FixtureUser(Guid UserId, Guid? PartnerId, string Status, DateTimeOffset? LockedUntil);
public sealed record FixtureAssignment(Guid UserId, Guid LocationId);

/// <summary>Thread-safe, mutable in tests (row_version bump, etc.).</summary>
public sealed class FixtureEntraStore(EntraFixture f) : ILocationLoginConfigReader, IExternalLoginReader, IUserLocationReader, ISecretResolver
{
    private readonly object _gate = new();
    public EntraFixture Data => f;

    public Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => l.Config.LocationId == locationId)?.Config); }

    public Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => string.Equals(l.Slug, slug, StringComparison.Ordinal))?.Config); }

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
            return Task.FromResult<ExternalLoginMatch?>(new(u.UserId, u.PartnerId, UserStatusText.Parse(u.Status), u.LockedUntil));
        }
    }

    private void Guard(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (f.ReadersThrow) throw new InvalidOperationException("fixture reader: simulated DB error");
    }

    public Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Assignments.Any(a => a.UserId == userId && a.LocationId == locationId)); }

    public Task<string?> ResolveAsync(string secretRef, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Secrets.TryGetValue(secretRef, out var s) ? s : null); }

    public void Mutate(Action<EntraFixture> m) { lock (_gate) m(f); }
}

/// <summary>The ONLY place LocationLoginConfig is constructed (named arguments, never positional), so the
/// coming Abstractions change (SecretRef → SecretReference, not positional) touches one spot.</summary>
public static class FixtureConfig
{
    public static LocationLoginConfig Build(Guid locationId, Guid partnerId, string slug, ProviderKind providerKind, string displayName,
        IReadOnlyList<Guid> tenants, string? authority, string? clientId, string? secretRef, bool allowDbFallback, bool mfaRequired, long rowVersion = 1) =>
        new(LocationId: locationId, PartnerId: partnerId, Slug: slug, ProviderKind: providerKind, DisplayName: displayName,
            EntraAllowedTenantIds: tenants, OidcAuthority: authority, OidcClientId: clientId, SecretRef: secretRef,
            AllowDbFallback: allowDbFallback, MfaRequired: mfaRequired, RowVersion: rowVersion);

    public static LocationLoginConfig WithoutSecret(LocationLoginConfig c) => c with { SecretRef = null };
}
