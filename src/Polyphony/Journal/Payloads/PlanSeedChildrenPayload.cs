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
}
