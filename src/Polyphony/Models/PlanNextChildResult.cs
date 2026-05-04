namespace Polyphony;

/// <summary>
/// Output of <c>polyphony plan next-child</c>. Mirrors the JSON contract previously
/// emitted by <c>scripts/child-router.ps1</c>; consumed by <c>plan-level.yaml</c>'s
/// <c>child_router</c> route, which branches on <see cref="HasPlannableChildren"/>
/// and iterates over <see cref="PlannableChildren"/> via <c>for_each</c>.
/// </summary>
/// <remarks>
/// Routing-script convention: this verb always exits 0. When the parent work item
/// is not found, <see cref="HasPlannableChildren"/> is false, the children array is
/// empty, and <see cref="Error"/> carries the diagnostic. The workflow then routes
/// to the no-children branch, avoiding recursion.
/// </remarks>
public sealed record PlanNextChildResult
{
    public required bool HasPlannableChildren { get; init; }
    public required PlannableChild[] PlannableChildren { get; init; }
    public required int ParentId { get; init; }
    public required int Count { get; init; }
    public string? Error { get; init; }
}
