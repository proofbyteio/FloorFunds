using Catalog.Read;

namespace Catalog.Query;

/// <summary>
/// Storage seam over the raw reader. The query boundary authorizes before it asks for history.
/// </summary>
public interface ICatalogStorage
{
    Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken);

    Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogEntryChangeRow>> ListChangesAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken);
}
