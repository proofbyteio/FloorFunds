namespace Catalog.Contracts;

public enum ProposedStatus
{
    Ok = 200,
    Created = 201,
    BadRequest = 400,
    Forbidden = 403,
    NotFound = 404,
    Conflict = 409,
    Unprocessable = 422,
    PreconditionRequired = 428,
    Unavailable = 503,
}

public sealed record AllowedAction(string Action, bool Allowed, string Gate);

/// <summary>Missing activation is ABSENT. Active is null, and the number is null rather than a stored zero.</summary>
public sealed record ActivationProjection(string Token, long? Number, bool? Active)
{
    public static ActivationProjection Absent { get; } = new("ABSENT", null, null);
}

public sealed record EntryProjection(
    Guid EntryId,
    string Family,
    string EntryClass,
    Guid? OwnerPartnerId,
    string ScopeKind,
    string Code,
    string DisplayName,
    string? Description,
    long EntryVersion,
    IReadOnlyDictionary<string, string> Fields,
    ActivationProjection EntryActivation,
    ActivationProjection PartnerSwitch,
    ActivationProjection LocationSwitch,
    string EffectiveAvailability,
    IReadOnlyList<AllowedAction> AllowedActions,
    string ProofBoundary);

public sealed record EntryList(IReadOnlyList<EntryProjection> Entries, string Pagination);

public sealed record HistoryProjection(
    Guid ChangeId,
    Guid EntryId,
    string ChangeKind,
    long EntryVersion,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyDictionary<string, string?>? Before,
    IReadOnlyDictionary<string, string?> After,
    DateTimeOffset OccurredAt);

public sealed record PreviewReading(
    string Impact,
    int? AffectedCount,
    bool ApplyAllowed,
    string? FixtureRevision,
    int? FixtureDeclaredEdgeCount,
    bool RuntimeConfiguration,
    bool AuthorizesWrites);

public sealed record GraphReading(
    string State,
    string Impact,
    int? AffectedCount,
    bool ApplyAllowed,
    string? FixtureRevision,
    int? FixtureDeclaredEdgeCount,
    bool RuntimeConfiguration,
    bool AuthorizesWrites);

/// <summary>Contract result of the double. Status codes are the accepted mapping labels, not a live HTTP stack.</summary>
public sealed record CatalogOutcome
{
    public ProposedStatus Status { get; init; }
    public string Code { get; init; } = "";
    public string ProofBoundary { get; init; } = ProofBoundaryNames.InMemoryDouble;
    public bool Wrote { get; init; }
    public string? Gate { get; init; }
    public string? Detail { get; init; }
    public Guid? EntryId { get; init; }
    public long? EntryVersion { get; init; }
    public string? PartnerTargetCheck { get; init; }
    public string? LocationMembership { get; init; }
    public string? AuditIntentStatus { get; init; }
    public AuditIntent? Intent { get; init; }
    public EntryProjection? Entry { get; init; }
    public EntryList? List { get; init; }
    public IReadOnlyList<HistoryProjection>? History { get; init; }
    public PreviewReading? Preview { get; init; }
    public GraphReading? Graph { get; init; }

    public static CatalogOutcome NotFound() => new()
    {
        Status = ProposedStatus.NotFound,
        Code = "not_found",
        Detail = "not_found",
        Wrote = false,
    };
}

public static class OutcomeCompare
{
    public static bool SameRefusal(CatalogOutcome left, CatalogOutcome right) =>
        left.Status == right.Status
        && left.Code == right.Code
        && left.Detail == right.Detail
        && left.Wrote == right.Wrote
        && left.Gate == right.Gate
        && left.EntryId == right.EntryId
        && left.EntryVersion == right.EntryVersion
        && left.PartnerTargetCheck == right.PartnerTargetCheck
        && left.LocationMembership == right.LocationMembership
        && left.AuditIntentStatus == right.AuditIntentStatus
        && left.Intent == right.Intent
        && left.Entry == right.Entry
        && left.Preview == right.Preview
        && left.ProofBoundary == right.ProofBoundary;
}
