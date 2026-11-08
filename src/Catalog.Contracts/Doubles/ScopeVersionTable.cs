using Catalog.Contracts;

namespace Catalog.Contracts.Doubles;

/// <summary>
/// In-memory version table for the switch contract. Activation commands in this slice do not call it.
/// ABSENT is the missing row. Insert from ABSENT stores version 1. Zero is never stored.
/// An entry version is not accepted as a substitute for a missing switch.
/// </summary>
internal sealed class ScopeVersionTable
{
    private readonly Dictionary<string, long> _rows = new(StringComparer.Ordinal);

    public int Count => _rows.Count;
    public int MutationCount { get; private set; }
    public bool ContainsZero => _rows.Values.Any(value => value < 1);

    public VersionToken Read(string key) =>
        _rows.TryGetValue(key, out var version) ? VersionToken.AtLeastOne(version) : VersionToken.Absent;

    public void InsertFromAbsent(string key)
    {
        MutationCount++;
        if (_rows.ContainsKey(key))
            throw new ScopeVersionConflictException();
        _rows[key] = 1;
    }

    public void UpdateExisting(string key, long expected)
    {
        MutationCount++;
        if (expected < 1)
            throw new ScopeVersionDomainException();
        if (!_rows.TryGetValue(key, out var current) || current != expected)
            throw new ScopeVersionConflictException();
        _rows[key] = current + 1;
    }
}

internal sealed class ScopeVersionConflictException : InvalidOperationException
{
}

internal sealed class ScopeVersionDomainException : InvalidOperationException
{
}
