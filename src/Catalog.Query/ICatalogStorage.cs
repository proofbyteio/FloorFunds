using Catalog.Read;

namespace Catalog.Query;

/// <summary>
/// Storage seam over the raw reader. The query boundary authorizes before it asks for history.
/// </summary>
public interface ICatalogStorage
{
    Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken);

    Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AllowlistedHistoryScalars>> ListChangesAsync(CatalogReadScope scope, Guid entryId, string family, CancellationToken cancellationToken);
}

/// <summary>
/// Scalar history fields already named by an accepted allowlist.
/// This row is not a JSON document and has no actor column.
/// </summary>
public sealed record AllowlistedHistoryScalars(
    Guid ChangeId,
    Guid EntryId,
    string ChangeKind,
    long RowVersion,
    DateTimeOffset OccurredAt,
    string? FeeCode,
    string? FeeDisplayName,
    string? FeeDescription,
    string? BeforeClaimValue,
    string? AfterClaimValue,
    string? BeforeAvailability,
    string? AfterAvailability,
    string? BeforeSourceUrl,
    string? AfterSourceUrl,
    string? BeforeCheckedAt,
    string? AfterCheckedAt,
    string? BeforeClaimDisplayName,
    string? AfterClaimDisplayName,
    string? BeforeClaimDescription,
    string? AfterClaimDescription);
