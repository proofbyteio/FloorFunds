namespace Catalog.Read;

public sealed record CatalogEntryRow(
    Guid EntryId,
    string Family,
    string EntryClass,
    Guid? OwnerPartnerId,
    string Code,
    bool IsActive,
    long RowVersion);

public sealed record CatalogFeeTypeRow(
    Guid EntryId,
    Guid? OwnerPartnerId,
    string Code,
    string Definition,
    string DisplayName,
    string? Description,
    bool IsActive,
    long RowVersion);

public sealed record CatalogEntryChangeRow(
    Guid ChangeId,
    Guid EntryId,
    string ChangeKind,
    long RowVersion,
    string ScopeLevel);
