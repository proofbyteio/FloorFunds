namespace Catalog.Query;

/// <summary>
/// Closed read result. No actor id, no hidden count, no raw total, no audit intent.
/// A hidden row and a missing id use the same <see cref="CatalogQueryResult.NotFound"/> value.
/// </summary>
public sealed record CatalogQueryResult
{
    public int Status { get; init; }
    public string Code { get; init; } = "";
    public string Detail { get; init; } = "";
    public string ProofBoundary { get; init; } = CatalogQuery.ProofBoundary;
    public string? Pagination { get; init; }
    public CatalogQueryEntry? Entry { get; init; }
    public IReadOnlyList<CatalogQueryEntry>? Entries { get; init; }
    public IReadOnlyList<CatalogQueryHistory>? History { get; init; }

    public static CatalogQueryResult NotFound() => new()
    {
        Status = 404,
        Code = "not_found",
        Detail = "not_found",
    };

    public static CatalogQueryResult Forbidden() => new()
    {
        Status = 403,
        Code = "no_management_grant",
        Detail = "no_management_grant",
    };

    public static CatalogQueryResult VersionConflict() => new()
    {
        Status = 409,
        Code = "catalog_version_conflict",
        Detail = "catalog_version_conflict",
    };

    public static CatalogQueryResult OkList(IReadOnlyList<CatalogQueryEntry> entries) => new()
    {
        Status = 200,
        Code = "ok",
        Detail = "ok",
        Pagination = "not_a_cursor_contract",
        Entries = entries,
    };

    public static CatalogQueryResult OkEntry(CatalogQueryEntry entry) => new()
    {
        Status = 200,
        Code = "ok",
        Detail = "ok",
        Entry = entry,
    };

    public static CatalogQueryResult OkHistory(IReadOnlyList<CatalogQueryHistory> history) => new()
    {
        Status = 200,
        Code = "ok",
        Detail = "ok",
        History = history,
    };
}

/// <summary>Visible entry. Class is global or private. The database class token is not copied.</summary>
public sealed record CatalogQueryEntry(
    Guid EntryId,
    string Family,
    string EntryClass,
    Guid? OwnerPartnerId,
    string Code,
    long EntryVersion);

/// <summary>
/// Closed history row. Same members as the accepted <c>HistoryProjection</c>.
/// Actor identity is not a member. Before and after contain allowlisted value keys only.
/// </summary>
public sealed record CatalogQueryHistory(
    Guid ChangeId,
    Guid EntryId,
    string ChangeKind,
    long EntryVersion,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyDictionary<string, string?>? Before,
    IReadOnlyDictionary<string, string?> After,
    DateTimeOffset OccurredAt);
