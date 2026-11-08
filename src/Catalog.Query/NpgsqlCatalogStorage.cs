using Catalog.Read;

namespace Catalog.Query;

/// <summary>Delegates to the SELECT-only reader. This type adds no SQL.</summary>
public sealed class NpgsqlCatalogStorage : ICatalogStorage
{
    private readonly NpgsqlCatalogReader _reader;

    public NpgsqlCatalogStorage(NpgsqlCatalogReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken)
        => _reader.ListEntriesAsync(scope, cancellationToken);

    public Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
        => _reader.FindEntryAsync(scope, entryId, cancellationToken);

    public Task<IReadOnlyList<CatalogEntryChangeRow>> ListChangesAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
        => _reader.ListEntryChangesAsync(scope, entryId, cancellationToken);
}
