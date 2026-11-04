using System.Collections.Concurrent;
namespace Login.Entra;

/// <summary>uti replay guard (proposal §7.1 row 20). [ASSUMPTION A-S2B-04] in-memory for the stand-in phase; Postgres later.</summary>
public sealed class ReplayCache(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public bool TryUse(string uti, DateTimeOffset expires)
    {
        var now = clock.GetUtcNow();
        foreach (var kv in _seen) if (kv.Value < now) _seen.TryRemove(kv.Key, out _);
        return _seen.TryAdd(uti, expires);
    }
}
