using Catalog.Contracts;
using Catalog.Read;

namespace Catalog.Query;

public static class CatalogReadProbe
{
    public static async Task<int> Run()
    {
        var cashier = new CatalogPrincipal(Guid.Parse("44444444-4444-4444-8444-444444444444"), CatalogRole.Cashier, null);
        bool forbidden;
        try
        {
            var denied = await new CatalogQuery(new UnusedStorage()).ListAsync(cashier, CatalogFamily.FeeType, CancellationToken.None);
            forbidden = denied.Status == 403;
        }
        catch { forbidden = false; }
        Console.WriteLine((forbidden ? "PASS " : "FAIL ") + "catalog-api-403-without-permission");
        var allowed = CatalogReadAuthorization.MayRead(
            CatalogPrincipalFactory.Management(Guid.Parse("55555555-5555-4555-8555-555555555555"), CatalogRole.SuperAdmin, null));
        Console.WriteLine((allowed ? "PASS " : "FAIL ") + "catalog-api-allowed-with-permission");
        return forbidden && allowed ? 0 : 1;
    }

    sealed class UnusedStorage : ICatalogStorage
    {
        public Task<IReadOnlyList<CatalogEntryRow>> ListEntriesAsync(CatalogReadScope scope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("catalog read storage was called");

        public Task<CatalogEntryRow?> FindEntryAsync(CatalogReadScope scope, Guid entryId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("catalog read storage was called");

        public Task<IReadOnlyList<AllowlistedHistoryScalars>> ListChangesAsync(CatalogReadScope scope, Guid entryId, string family, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("catalog read storage was called");
    }
}
