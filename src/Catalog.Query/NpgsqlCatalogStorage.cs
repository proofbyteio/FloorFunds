using Catalog.Read;
using Npgsql;

namespace Catalog.Query;

/// <summary>
/// List and find use the raw reader. History scalars use the allowlisted select.
/// Both use the same admin_api data source. Neither type is a write path.
/// </summary>
public sealed class NpgsqlCatalogStorage : ICatalogStorage
{
    private readonly NpgsqlCatalogReader _reader;
    private readonly NpgsqlAllowlistedHistory _history;

    public NpgsqlCatalogStorage(NpgsqlDataSource dataSource)
    {
        if (dataSource is null)
            throw new ArgumentNullException(nameof(dataSource));
        _reader = new NpgsqlCatalogReader(dataSource);
        _history = new NpgsqlAllowlistedHistory(dataSource);
    }

    public Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken)
        => _reader.ListEntriesAsync(scope, cancellationToken);

    public Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken)
        => _reader.FindEntryAsync(scope, entryId, cancellationToken);

    public Task<IReadOnlyList<AllowlistedHistoryScalars>> ListChangesAsync(CatalogReadScope scope, Guid entryId, string family, CancellationToken cancellationToken)
        => _history.ListAsync(scope, entryId, family, cancellationToken);
}
