namespace Polyphony;

/// <summary>
/// A child work item discovered by <c>polyphony plan next-child</c> as plannable.
/// Carried in <see cref="PlanNextChildResult.PlannableChildren"/> for consumption
/// by <c>plan-level.yaml</c>'s <c>plan_children_group</c> for_each loop.
/// </summary>
public sealed record PlannableChild
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required string Title { get; init; }
}
