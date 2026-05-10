namespace Polyphony;

/// <summary>
/// Output of <c>polyphony mg nesting-decision</c>: given a child item
/// and its enclosing merge group, decides whether the child becomes its
/// own nested merge group or stays flat as a impl PR. Per ADR
/// <c>docs/decisions/branch-model.md</c> § Nested-MG trigger: default
/// nest when child is implementable AND decomposable; planner can
/// override either way per child.
/// </summary>
public sealed record MergeGroupNestingDecisionResult
{
    /// <summary>Echoed root work-item id.</summary>
    public required int RootId { get; init; }

    /// <summary>Echoed child work-item id being decided about.</summary>
    public required int ItemId { get; init; }

    /// <summary>Echoed path of the enclosing merge group.</summary>
    public required string ParentMgPath { get; init; }

    /// <summary>The decision: <c>nest</c> (child becomes a nested MG) or <c>flat</c> (child becomes a impl PR).</summary>
    public required string Decision { get; init; }

    /// <summary>The nested MG id when <see cref="Decision"/> is <c>nest</c>; null otherwise.</summary>
    public string? NestedMgId { get; init; }

    /// <summary>The nested MG path (parent path + nested id) when nesting; null otherwise.</summary>
    public string? NestedMgPath { get; init; }

    /// <summary>The impl branch name (<c>impl/{root}-{item}</c>) when flat; null otherwise.</summary>
    public string? ImplBranch { get; init; }

    /// <summary>Echo of the workflow-supplied has_implementable input.</summary>
    public required bool HasImplementable { get; init; }

    /// <summary>Echo of the workflow-supplied decomposable input.</summary>
    public required bool Decomposable { get; init; }

    /// <summary>
    /// What drove the decision: <c>flat</c> (override-flat), <c>nested-mg-id</c>
    /// (override-nested-mg-id), or <c>default</c> (the default trigger fired
    /// either way).
    /// </summary>
    public required string OverrideApplied { get; init; }

    /// <summary>Human-readable explanation of the decision (workflow logging).</summary>
    public required string Reason { get; init; }

    /// <summary>Non-empty when validation failed.</summary>
    public string? Error { get; init; }
}
