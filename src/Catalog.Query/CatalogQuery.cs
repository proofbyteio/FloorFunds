using Catalog.Contracts;
using Catalog.Read;

namespace Catalog.Query;

/// <summary>
/// Fail-closed projection in front of the raw catalog reader.
/// The rule is the accepted contract <c>Visible</c> predicate. PQ-252.SA2 stays open.
/// Callers pass a synthetic <see cref="CatalogPrincipal"/>. This type does not read tokens.
/// </summary>
public sealed class CatalogQuery
{
    public const string ProofBoundary = "scratch_failclosed_projection_not_pq252_sa2_closure";

    private readonly ICatalogStorage _storage;

    public CatalogQuery(ICatalogStorage storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async Task<CatalogQueryResult> ListAsync(CatalogPrincipal actor, CatalogFamily family, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsManagementRole)
            return CatalogQueryResult.Forbidden();

        var scope = ScopeFor(actor);
        var wire = CatalogFamilies.Wire(family);
        var rows = await _storage.ListEntriesAsync(scope, cancellationToken);
        var entries = rows
            .Where(row => row.Family == wire && Visible(actor, row))
            .OrderBy(row => row.Code, StringComparer.Ordinal)
            .ThenBy(row => row.EntryId)
            .Select(Project)
            .ToArray();
        return CatalogQueryResult.OkList(entries);
    }

    public async Task<CatalogQueryResult> ReadAsync(
        CatalogPrincipal actor,
        CatalogFamily family,
        Guid entryId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(actor);
        var authorized = await AuthorizeAsync(actor, family, entryId, cancellationToken);
        if (authorized is null)
            return CatalogQueryResult.NotFound();
        if (expectedVersion is long version && version != authorized.Row.RowVersion)
            return CatalogQueryResult.VersionConflict();
        return CatalogQueryResult.OkEntry(Project(authorized.Row));
    }

    public async Task<CatalogQueryResult> HistoryAsync(
        CatalogPrincipal actor,
        CatalogFamily family,
        Guid entryId,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(actor);
        var authorized = await AuthorizeAsync(actor, family, entryId, cancellationToken);
        if (authorized is null)
            return CatalogQueryResult.NotFound();
        if (expectedVersion is long version && version != authorized.Row.RowVersion)
            return CatalogQueryResult.VersionConflict();

        var changes = await _storage.ListChangesAsync(authorized.Scope, entryId, authorized.Row.Family, cancellationToken);
        if (changes.Any(change => change.EntryId != entryId))
            throw new InvalidOperationException("catalog history returned a different entry.");
        var history = changes
            .OrderBy(change => change.RowVersion)
            .ThenBy(change => change.ChangeId)
            .Select(change => HistoryAllowlist.Project(authorized.Row.Family, change))
            .ToArray();
        return CatalogQueryResult.OkHistory(history);
    }

    private async Task<Authorized?> AuthorizeAsync(
        CatalogPrincipal actor,
        CatalogFamily family,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        if (!actor.IsManagementRole)
            return null;
        var scope = ScopeFor(actor);
        var row = await _storage.FindEntryAsync(scope, entryId, cancellationToken);
        if (row is null || row.Family != CatalogFamilies.Wire(family) || !Visible(actor, row))
            return null;
        return new Authorized(scope, row);
    }

    private static CatalogReadScope ScopeFor(CatalogPrincipal actor) => actor.Role switch
    {
        CatalogRole.SuperAdmin => CatalogReadScope.Super(),
        CatalogRole.PartnerAdmin => CatalogReadScope.Partner(actor.PartnerId!.Value),
        _ => throw new InvalidOperationException("A non-management principal has no catalog storage scope."),
    };

    /// <summary>
    /// Accepted contract Visible. Global rows are visible to management roles.
    /// A private model_hardware_claim is visible to Super Admin and to the owning Partner Admin.
    /// Every other private family is visible only to the owning Partner Admin.
    /// </summary>
    private static bool Visible(CatalogPrincipal actor, CatalogEntryRow entry)
    {
        if (!actor.IsManagementRole)
            return false;
        if (entry.OwnerPartnerId is null)
            return true;
        if (entry.Family == CatalogFamilies.Wire(CatalogFamily.ModelHardwareClaim))
            return actor.Role == CatalogRole.SuperAdmin || actor.PartnerId == entry.OwnerPartnerId;
        return actor.Role == CatalogRole.PartnerAdmin && actor.PartnerId == entry.OwnerPartnerId;
    }

    private static CatalogQueryEntry Project(CatalogEntryRow row) => new(
        row.EntryId,
        row.Family,
        row.OwnerPartnerId is null ? "global" : "private",
        row.OwnerPartnerId,
        row.Code,
        row.RowVersion);

    private sealed record Authorized(CatalogReadScope Scope, CatalogEntryRow Row);
}
