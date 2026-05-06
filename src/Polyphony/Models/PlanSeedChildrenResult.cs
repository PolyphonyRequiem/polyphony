namespace Polyphony;

using System.Text.Json.Serialization;

/// <summary>
/// Output of <c>polyphony plan seed-children</c>: idempotent reconciliation
/// of architect-emitted children against existing work-item children. Mirrors
/// <c>.conductor/registry/scripts/seeder.ps1</c>'s JSON contract exactly so
/// downstream YAML routes can keep referencing fields like
/// <c>seeder.output.error_count</c> and <c>seeder.output.planned_tag_set</c>.
/// </summary>
public sealed record PlanSeedChildrenResult
{
    public required int WorkItemId { get; init; }
    public required int ChildCount { get; init; }
    public required int SeededCount { get; init; }
    public required int ReusedCount { get; init; }
    public required int ErrorCount { get; init; }
    public required IReadOnlyList<SeedReconciliation> SeededItems { get; init; }
    public required IReadOnlyList<SeedReconciliation> ReusedItems { get; init; }
    public required IReadOnlyList<SeedError> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required bool PlannedTagSet { get; init; }
    public required bool PlannedTagAlready { get; init; }
}

public sealed record SeedReconciliation
{
    [JsonPropertyName("child_id")]
    public required string ChildId { get; init; }
    public required int WorkItemId { get; init; }

    /// <summary>One of <c>marker</c>, <c>title</c>, <c>created</c>.</summary>
    public required string MatchedBy { get; init; }
}

public sealed record SeedError
{
    [JsonPropertyName("child_id")]
    public string? ChildId { get; init; }
    public string? Title { get; init; }
    public required string Error { get; init; }
}

