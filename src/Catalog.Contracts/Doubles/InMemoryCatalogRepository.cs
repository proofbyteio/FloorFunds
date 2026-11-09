using Catalog.Contracts;

namespace Catalog.Contracts.Doubles;

internal enum EntryOrigin
{
    Fixture = 1,
    Command = 2,
}

internal sealed class StoredEntry
{
    public required Guid EntryId { get; init; }
    public required CatalogFamily Family { get; init; }
    public required EntryClass Class { get; init; }
    public required Guid? OwnerPartnerId { get; init; }
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public required string? Description { get; init; }
    public required long EntryVersion { get; init; }
    public required IReadOnlyDictionary<string, string> Fields { get; init; }
    public required EntryOrigin Origin { get; init; }
}

internal sealed class StoredHistory
{
    public required Guid ChangeId { get; init; }
    public required Guid EntryId { get; init; }
    public required CatalogFamily Family { get; init; }
    public required Guid ActorUserId { get; init; }
    public required string ChangeKind { get; init; }
    public required long EntryVersion { get; init; }
    public required IReadOnlyList<string> ChangedFields { get; init; }
    public required IReadOnlyDictionary<string, string?>? Before { get; init; }
    public required IReadOnlyDictionary<string, string?> After { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

internal sealed record IdempotencyRecord(string Key, string BindingHash, CatalogOutcome Outcome);

public interface ICatalogClock
{
    DateTimeOffset UtcNow { get; }
}

public interface ICatalogIds
{
    Guid NewId();
}

/// <summary>
/// Explicit in-memory double. There is no database adapter behind this type.
/// Commit copies the next state and publishes it in one swap, or leaves the previous state in place.
/// </summary>
public sealed class InMemoryCatalogRepository
{
    private readonly object _gate = new();
    private Dictionary<Guid, StoredEntry> _entries = new();
    private List<StoredHistory> _history = new();
    private List<AuditIntent> _intents = new();
    private Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);

    public InMemoryCatalogRepository()
    {
        Scopes = new ScopeVersionTable();
    }

    internal ScopeVersionTable Scopes { get; }

    public Exception? CommitFault { get; set; }
    public Action? BeforeCommit { get; set; }
    public int CommitAttempts { get; private set; }
    public int VersionComparisons { get; private set; }

    public int EntryCount { get { lock (_gate) return _entries.Count; } }
    public int HistoryCount { get { lock (_gate) return _history.Count; } }
    public int IntentCount { get { lock (_gate) return _intents.Count; } }
    public int IdempotencyCount { get { lock (_gate) return _idempotency.Count; } }

    public IReadOnlyList<AuditIntent> Intents
    {
        get { lock (_gate) return _intents.ToArray(); }
    }

    public void InstallFixture(
        Guid entryId,
        CatalogFamily family,
        EntryClass entryClass,
        Guid? ownerPartnerId,
        string code,
        string displayName,
        string? description,
        long entryVersion,
        IReadOnlyDictionary<string, string>? fields)
    {
        if (entryId == Guid.Empty || entryVersion < 1)
            throw new ArgumentException("A fixture entry needs an id and a version of 1 or greater.");
        if (entryClass == EntryClass.Global && ownerPartnerId is not null)
            throw new ArgumentException("A global fixture has no owner.");
        if (entryClass == EntryClass.Private && (ownerPartnerId is null || ownerPartnerId == Guid.Empty))
            throw new ArgumentException("A private fixture carries its owner.");
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A fixture needs a code and a display name.");
        var storedFields = fields is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(fields, StringComparer.Ordinal);
        if (storedFields.ContainsKey("economic_role") || storedFields.ContainsKey("classification"))
            throw new ArgumentException("A fixture cannot invent fee classification.");
        if (family == CatalogFamily.FeeType && storedFields.Count > 0)
            throw new ArgumentException("A fee fixture is names only.");
        if (family == CatalogFamily.ModelHardwareClaim)
        {
            var allowed = new HashSet<string>(AuditAllowlist.ClaimEditValueKeys, StringComparer.Ordinal)
            {
                "model_id", "capability_entry_id", "option_key", "source_version",
            };
            if (storedFields.Keys.Any(key => !allowed.Contains(key)))
                throw new ArgumentException("A claim fixture has a field outside the closed set.");
        }

        var entry = new StoredEntry
        {
            EntryId = entryId,
            Family = family,
            Class = entryClass,
            OwnerPartnerId = ownerPartnerId,
            Code = code,
            DisplayName = displayName,
            Description = description,
            EntryVersion = entryVersion,
            Fields = storedFields,
            Origin = EntryOrigin.Fixture,
        };
        lock (_gate)
        {
            if (_entries.ContainsKey(entryId))
                throw new ArgumentException("Fixture entry id is already present.");
            var next = new Dictionary<Guid, StoredEntry>(_entries) { [entryId] = entry };
            _entries = next;
        }
    }

    internal StoredEntry? Find(Guid entryId)
    {
        lock (_gate)
            return _entries.TryGetValue(entryId, out var entry) ? entry : null;
    }

    internal IReadOnlyList<StoredEntry> FindByCode(CatalogFamily family, string code)
    {
        lock (_gate)
            return _entries.Values.Where(entry => entry.Family == family && string.Equals(entry.Code, code, StringComparison.Ordinal)).ToArray();
    }

    internal IReadOnlyList<StoredEntry> ListFamily(CatalogFamily family)
    {
        lock (_gate)
            return _entries.Values.Where(entry => entry.Family == family).OrderBy(entry => entry.EntryId).ToArray();
    }

    internal IReadOnlyList<StoredHistory> HistoryFor(Guid entryId)
    {
        lock (_gate)
            return _history.Where(row => row.EntryId == entryId).ToArray();
    }

    internal IdempotencyRecord? FindIdempotency(string key)
    {
        lock (_gate)
            return _idempotency.TryGetValue(key, out var record) ? record : null;
    }

    internal bool VersionMatches(Guid entryId, long expected)
    {
        lock (_gate)
        {
            VersionComparisons++;
            return _entries.TryGetValue(entryId, out var entry) && entry.EntryVersion == expected;
        }
    }

    internal long EntryVersionOrZero(Guid entryId)
    {
        lock (_gate)
            return _entries.TryGetValue(entryId, out var entry) ? entry.EntryVersion : 0;
    }

    internal void Commit(StoredEntry entry, StoredHistory history, AuditIntent intent, IdempotencyRecord idempotency)
    {
        BeforeCommit?.Invoke();
        lock (_gate)
        {
            CommitAttempts++;
            if (CommitFault is not null)
                throw new CatalogCommitRolledBackException("injected");
            if (_idempotency.ContainsKey(idempotency.Key))
                throw new InvalidOperationException("Idempotency key was already committed.");
            var entries = new Dictionary<Guid, StoredEntry>(_entries) { [entry.EntryId] = entry };
            var historyRows = new List<StoredHistory>(_history) { history };
            var intents = new List<AuditIntent>(_intents) { intent };
            var keys = new Dictionary<string, IdempotencyRecord>(_idempotency, StringComparer.Ordinal)
            {
                [idempotency.Key] = idempotency,
            };
            _entries = entries;
            _history = historyRows;
            _intents = intents;
            _idempotency = keys;
        }
    }
}

public sealed class CatalogCommitRolledBackException(string message) : InvalidOperationException(message);
