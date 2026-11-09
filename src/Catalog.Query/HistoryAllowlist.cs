using Catalog.Contracts;

namespace Catalog.Query;

/// <summary>
/// Projects history values only where the accepted contract already names an allowlist.
/// Fee create uses <see cref="AuditAllowlist.FeeValueKeys"/>.
/// Claim edit uses <see cref="AuditAllowlist.ClaimEditValueKeys"/>.
/// Every other family and change kind stays closed: no value keys are copied.
/// </summary>
public static class HistoryAllowlist
{
    public static CatalogQueryHistory Project(string family, AllowlistedHistoryScalars row)
    {
        if (family == CatalogFamilies.Wire(CatalogFamily.FeeType) && row.ChangeKind == "created")
            return FeeCreated(row);
        if (family == CatalogFamilies.Wire(CatalogFamily.ModelHardwareClaim) && row.ChangeKind == "updated")
            return ClaimUpdated(row);
        return Closed(row);
    }

    private static CatalogQueryHistory FeeCreated(AllowlistedHistoryScalars row)
    {
        var after = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changed = new List<string>();
        Add(after, changed, "code", row.FeeCode);
        Add(after, changed, "display_name", row.FeeDisplayName);
        Add(after, changed, "description", row.FeeDescription);
        return new CatalogQueryHistory(
            row.ChangeId,
            row.EntryId,
            row.ChangeKind,
            row.RowVersion,
            changed,
            Before: null,
            after,
            row.OccurredAt);
    }

    private static CatalogQueryHistory ClaimUpdated(AllowlistedHistoryScalars row)
    {
        var pairs = new (string Key, string? Before, string? After)[]
        {
            ("availability", row.BeforeAvailability, row.AfterAvailability),
            ("checked_at", row.BeforeCheckedAt, row.AfterCheckedAt),
            ("claim_value", row.BeforeClaimValue, row.AfterClaimValue),
            ("description", row.BeforeClaimDescription, row.AfterClaimDescription),
            ("display_name", row.BeforeClaimDisplayName, row.AfterClaimDisplayName),
            ("source_url", row.BeforeSourceUrl, row.AfterSourceUrl),
        };
        var before = new Dictionary<string, string?>(StringComparer.Ordinal);
        var after = new Dictionary<string, string?>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var pair in pairs)
        {
            if (pair.Before == pair.After)
                continue;
            changed.Add(pair.Key);
            before[pair.Key] = pair.Before;
            after[pair.Key] = pair.After;
        }
        return new CatalogQueryHistory(
            row.ChangeId,
            row.EntryId,
            row.ChangeKind,
            row.RowVersion,
            changed,
            before,
            after,
            row.OccurredAt);
    }

    private static CatalogQueryHistory Closed(AllowlistedHistoryScalars row) => new(
        row.ChangeId,
        row.EntryId,
        row.ChangeKind,
        row.RowVersion,
        ChangedFields: [],
        Before: null,
        After: new Dictionary<string, string?>(StringComparer.Ordinal),
        row.OccurredAt);

    private static void Add(Dictionary<string, string?> after, List<string> changed, string key, string? value)
    {
        if (value is null)
            return;
        changed.Add(key);
        after[key] = value;
    }
}
