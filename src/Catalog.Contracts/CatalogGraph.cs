namespace Catalog.Contracts;

/// <summary>One declared fixture edge. The domain service does not walk edges and does not treat them as causes.</summary>
public sealed record FixtureEdge(Guid FromEntryId, Guid ToEntryId);

/// <summary>
/// Graph view for isolated tests. The default is UNCONFIGURED. A fixture is not runtime configuration
/// and does not authorize Off, cascade, or restore.
/// </summary>
public abstract record GraphView
{
    public static readonly GraphView Default = new Unconfigured();

    public sealed record Unconfigured : GraphView;

    public sealed record Fixture(string Revision, IReadOnlyList<FixtureEdge> Edges) : GraphView
    {
        public bool IsRuntimeConfiguration => false;
        public bool AuthorizesWrites => false;
    }
}

public static class CatalogGates
{
    public const string ClaimReactivationPendingPq272 = "claim_reactivation_pending_pq272";
    public const string GraphUnconfiguredAllOff = "graph_unconfigured_all_off";
    public const string GraphDependentRestoreUnavailable = "graph_dependent_restore_unavailable";
    public const string CreateDtoUnresolved = "create_dto_unresolved";
    public const string PrivateCodePolicyUnresolved = "private_code_policy_unresolved";
    public const string PrivatePartnerSwitchUnresolved = "private_partner_switch_representation_unresolved";
    public const string FixtureDoesNotAuthorize = "fixture_graph_does_not_authorize";
    public const string SaPrivateVisibilityFailClosed = "sa_private_non_claim_visibility_unresolved";
}

/// <summary>Open policy ids recorded by this slice. Recording an id does not close the question.</summary>
public static class OpenPolicies
{
    public const string ClaimReactivation = "PQ-272";
    public const string GraphEdges = "PQ-263.1";
    public const string PrivateLocationOffPreservation = "PQ-271";
    public const string SaPrivateMetadata = "PQ-252.SA2";
    public const string PrivatePartnerSwitch = "PQ-252.SA3";
    public const string PrivateCode = "PQ-252.SA6";
    public const string FieldApproval = "PQ-252.SA1";
    public const string ReadAudience = "PQ-252.SA10";
}
