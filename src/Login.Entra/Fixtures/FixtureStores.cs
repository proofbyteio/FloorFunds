// Fixture-backed implementations of the draft interfaces (TLD-S2B-10 / TLD-S2B-22): used until SSE's P1 reader and
// interfaces land (plan step 8). No SQL here; never hand-create tables.
using System.Text.Json;
using Login.Entra.Contracts;
namespace Login.Entra.Fixtures;

public sealed class EntraFixture
{
    public List<FixtureLocation> Locations { get; set; } = [];
    public List<FixtureLink> Links { get; set; } = [];
    public List<FixtureAssignment> Assignments { get; set; } = [];
    public Dictionary<string, string> Secrets { get; set; } = [];

    public static EntraFixture Load(string path) =>
        JsonSerializer.Deserialize<EntraFixture>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException(path);
}

public sealed record FixtureLocation(string Slug, LocationLoginConfig Config);
public sealed record FixtureLink(string Issuer, Guid TenantId, Guid ObjectId, Guid UserId, string Username, string? Email, bool Disabled = false, bool Locked = false);
public sealed record FixtureAssignment(Guid UserId, Guid LocationId);

/// <summary>Thread-safe, mutable in tests (row_version bump, etc.).</summary>
public sealed class FixtureEntraStore(EntraFixture f) : ILocationLoginConfigReader, IExternalLoginLookup, ILocationAssignmentCheck, ISecretResolver
{
    private readonly object _gate = new();
    public EntraFixture Data => f;

    public Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => l.Config.LocationId == locationId)?.Config); }

    public Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => string.Equals(l.Slug, slug, StringComparison.Ordinal))?.Config); }

    // (issuer, tid, oid) only — Email is in the fixture only to prove it is never used (ENT-03).
    public Task<ExternalLoginUser?> FindAsync(string issuer, Guid tenantId, Guid objectId, CancellationToken ct)
    {
        lock (_gate)
        {
            var l = f.Links.FirstOrDefault(x => x.Issuer == issuer && x.TenantId == tenantId && x.ObjectId == objectId);
            return Task.FromResult(l is null ? null : new ExternalLoginUser(l.UserId, l.Username, l.Disabled, l.Locked));
        }
    }

    public Task<bool> IsAssignedAsync(Guid userId, Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Assignments.Any(a => a.UserId == userId && a.LocationId == locationId)); }

    public Task<string?> ResolveAsync(string secretRef, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Secrets.TryGetValue(secretRef, out var s) ? s : null); }

    public void Mutate(Action<EntraFixture> m) { lock (_gate) m(f); }
}
