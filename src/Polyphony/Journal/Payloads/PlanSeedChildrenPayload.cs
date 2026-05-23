namespace Polyphony.Journal.Payloads;

public sealed record PlanSeedChildrenPayload
{
    public required int WorkItemId { get; init; }
    public required int ChildCount { get; init; }
    public required IReadOnlyList<SeedReconciliation> SeededItems { get; init; }
    public required IReadOnlyList<SeedReconciliation> ReusedItems { get; init; }
    public required IReadOnlyList<SeedError> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool PlannedTagMutated { get; init; }
    public required bool PlannedTagAlreadyPresent { get; init; }
    public required IReadOnlyList<string> RootFacets { get; init; }
    public required bool FacetsTagMutated { get; init; }
    public required bool Succeeded { get; init; }
    public required bool WasMutated { get; init; }

    /// <summary>
    /// W4 (AB#3278): explicit lifecycle fact that distinguishes
    /// <c>merged_unseeded</c> from <c>complete</c> in journal-grounded
    /// <c>plan detect-state</c>. True iff the seeder succeeded AND the
    /// parent's <c>polyphony:planned</c> tag is now present (whether
    /// newly mutated or already there from a prior run). This is the
    /// only journal fact <c>detect-state</c> consults to declare a
    /// plan "complete" without re-reading the parent's tag.
    /// </summary>
    public required bool PlanningCompleted { get; init; }
}
