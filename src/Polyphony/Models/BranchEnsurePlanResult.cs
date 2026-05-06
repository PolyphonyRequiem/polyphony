namespace Polyphony;

/// <summary>
/// Output of <c>polyphony branch ensure-plan</c>: confirms a plan branch
/// exists locally and on the remote. The verb is idempotent — if the
/// branch already exists nothing is created and no push is performed.
///
/// <para>Three plan-branch shapes are supported, distinguished by the
/// input args:</para>
/// <list type="bullet">
///   <item><description><b>Root plan</b> — <c>--item-id == --root-id</c>, no <c>--parent-item-id</c>. Branch <c>plan/{root}</c>, base <c>feature/{root}</c>.</description></item>
///   <item><description><b>Child of root plan</b> — <c>--item-id != --root-id</c>, no <c>--parent-item-id</c>. Branch <c>plan/{root}-{item_id}</c>, base <c>plan/{root}</c>.</description></item>
///   <item><description><b>Descendant plan</b> — <c>--item-id != --root-id</c>, <c>--parent-item-id</c> provided. Branch <c>plan/{root}-{item_id}</c> (FLAT — hierarchy is in the base, not the name), base <c>plan/{root}-{parent_item_id}</c>.</description></item>
/// </list>
/// </summary>
public sealed record BranchEnsurePlanResult
{
    /// <summary>The fully-qualified plan branch name (e.g. <c>plan/1234</c> or <c>plan/1234-5678</c>).</summary>
    public required string Branch { get; init; }

    /// <summary>The base branch the plan branch was (or would be) created from.</summary>
    public required string BaseBranch { get; init; }

    /// <summary><c>created</c> | <c>checked_out</c> | <c>error</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Whether the plan branch already existed on the remote before this call.</summary>
    public required bool RemoteExisted { get; init; }

    /// <summary>Whether the plan branch was pushed to the remote by this call.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Whether the base branch existed on the remote before this call.</summary>
    public required bool BaseRemoteExisted { get; init; }

    /// <summary>Whether the base branch was fetched from the remote by this call.</summary>
    public required bool BaseFetched { get; init; }

    /// <summary>The base branch/ref the plan branch was created from (only set when action=created).</summary>
    public string? CreatedFrom { get; init; }

    /// <summary>The root work-item id supplied as input.</summary>
    public required int RootId { get; init; }

    /// <summary>The plan's owning work-item id supplied as input.</summary>
    public required int ItemId { get; init; }

    /// <summary>
    /// The immediate plan-tree parent's work-item id when this is a
    /// descendant-of-descendant plan. Null when this is the root plan
    /// (item == root) or a direct child of the root plan (parent
    /// implicitly the root plan, so no <c>--parent-item-id</c> needed).
    /// </summary>
    public int? ParentItemId { get; init; }

    /// <summary>True when this call ensured the root plan branch (<c>plan/{root}</c>).</summary>
    public required bool IsRootPlan { get; init; }

    /// <summary>Non-empty when the operation partially or fully failed.</summary>
    public string? Error { get; init; }
}
