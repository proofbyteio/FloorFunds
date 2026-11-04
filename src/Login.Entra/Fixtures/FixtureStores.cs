// Fixture-backed implementations of the draft interfaces (TLD-S2B-10 / TLD-S2B-22): used until SSE's P1 reader and
// interfaces land (plan step 8). No SQL here; never hand-create tables.
using System.Text.Json;
using Login.Entra.Contracts;
using Login.Entra.Drafts;
namespace Login.Entra.Fixtures;

public sealed class EntraFixture
{
    public List<FixtureLocation> Locations { get; set; } = [];
    public List<FixtureLink> Links { get; set; } = [];
    public List<FixtureAssignment> Assignments { get; set; } = [];
    public Dictionary<string, string> Secrets { get; set; } = [];
    public List<AppUserRecord> Users { get; set; } = [];
    /// <summary>Test knob: make every reader call throw (SSE contract: readers throw on DB errors).</summary>
    public bool ReadersThrow { get; set; }

    public static EntraFixture Load(string path) =>
        JsonSerializer.Deserialize<EntraFixture>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException(path);
}

public sealed record FixtureLocation(string Slug, LocationLoginConfig Config);
public sealed record FixtureLink(string Issuer, Guid TenantId, Guid ObjectId, Guid UserId, string? Email);
public sealed record FixtureAssignment(Guid UserId, Guid LocationId);

/// <summary>Thread-safe, mutable in tests (row_version bump, etc.).</summary>
public sealed class FixtureEntraStore(EntraFixture f) : IAppUserReader, ILocationLoginConfigReader, IExternalLoginLookup, ILocationAssignmentCheck, ISecretResolver
{
    private readonly object _gate = new();
    public EntraFixture Data => f;

    public Task<LocationLoginConfig?> GetAsync(Guid locationId, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => l.Config.LocationId == locationId)?.Config); }

    public Task<LocationLoginConfig?> GetBySlugAsync(string slug, CancellationToken ct)
    { lock (_gate) return Task.FromResult(f.Locations.FirstOrDefault(l => string.Equals(l.Slug, slug, StringComparison.Ordinal))?.Config); }

    // (issuer, tid, oid) only — Email is in the fixture only to prove it is never used (ENT-03).
    public Task<Guid?> FindAsync(string issuer, Guid tenantId, Guid subjectOid, CancellationToken ct)
    {
        lock (_gate)
        {
            Guard(ct);
            var l = f.Links.FirstOrDefault(x => x.Issuer == issuer && x.TenantId == tenantId && x.ObjectId == subjectOid);
            return Task.FromResult(l?.UserId);
        }
    }

    public Task<AppUserRecord?> FindUserAsync(Guid userId, CancellationToken ct)
    { lock (_gate) { Guard(ct); return Task.FromResult(f.Users.FirstOrDefault(u => u.UserId == userId)); } }

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
