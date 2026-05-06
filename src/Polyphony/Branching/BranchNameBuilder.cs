namespace Polyphony.Branching;

/// <summary>
/// Pure functions that build <see cref="BranchName"/> values matching the
/// Rev 4 grammar in <c>docs/decisions/branch-model.md</c>. The builder
/// always emits canonical, validated refs; consumers cannot construct
/// <see cref="BranchName"/> any other way except via
/// <see cref="BranchNameParser"/>.
/// </summary>
internal static class BranchNameBuilder
{
    /// <summary>Ref-class prefix for the apex integration trunk.</summary>
    public const string FeaturePrefix = "feature/";

    /// <summary>Ref-class prefix for plan branches.</summary>
    public const string PlanPrefix = "plan/";

    /// <summary>Ref-class prefix for merge-group branches.</summary>
    public const string MergeGroupPrefix = "mg/";

    /// <summary>Ref-class prefix for impl branches.</summary>
    public const string ImplPrefix = "impl/";

    /// <summary>Ref-class prefix for evidence branches.</summary>
    public const string EvidencePrefix = "evidence/";

    /// <summary>Builds <c>feature/{root_id}</c>.</summary>
    public static BranchName Feature(RootId rootId) =>
        BranchName.CreateUnsafe($"{FeaturePrefix}{rootId.Value}");

    /// <summary>Builds <c>plan/{root_id}</c> — the root plan branch.</summary>
    public static BranchName RootPlan(RootId rootId) =>
        BranchName.CreateUnsafe($"{PlanPrefix}{rootId.Value}");

    /// <summary>
    /// Builds <c>plan/{root_id}-{item_id}</c> — a descendant plan branch.
    /// Hierarchy is captured by the PR's base branch, not the name.
    /// </summary>
    public static BranchName DescendantPlan(RootId rootId, WorkItemId itemId) =>
        BranchName.CreateUnsafe($"{PlanPrefix}{rootId.Value}-{itemId.Value}");

    /// <summary>
    /// Builds <c>mg/{root_id}_{mg_path}</c> for a merge group at any depth.
    /// </summary>
    public static BranchName MergeGroup(RootId rootId, MergeGroupPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return BranchName.CreateUnsafe($"{MergeGroupPrefix}{rootId.Value}_{path.Canonical}");
    }

    /// <summary>
    /// Builds <c>impl/{root_id}-{item_id}</c> — an impl branch (flat).
    /// </summary>
    public static BranchName Impl(RootId rootId, WorkItemId itemId) =>
        BranchName.CreateUnsafe($"{ImplPrefix}{rootId.Value}-{itemId.Value}");

    /// <summary>
    /// Builds <c>evidence/{root_id}-{item_id}</c> — an evidence branch.
    /// </summary>
    public static BranchName Evidence(RootId rootId, WorkItemId itemId) =>
        BranchName.CreateUnsafe($"{EvidencePrefix}{rootId.Value}-{itemId.Value}");
}
