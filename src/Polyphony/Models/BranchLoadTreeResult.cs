using System.Text.Json.Serialization;

namespace Polyphony;

/// <summary>A single task underneath a work-tree issue.</summary>
public sealed record WorkTreeTask
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required string Tags { get; init; }
}

/// <summary>A single issue in the work-tree (child of the epic root).</summary>
public sealed record WorkTreeIssue
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required string Type { get; init; }
    public required string Tags { get; init; }
    public required int TaskCount { get; init; }
    [JsonPropertyName("tasks")]
    public required IReadOnlyList<WorkTreeTask> Children { get; init; }
}

/// <summary>The work-tree shape (epic + its issues + their tasks).</summary>
public sealed record WorkTree
{
    public required int EpicId { get; init; }
    public required string EpicTitle { get; init; }
    public required string EpicType { get; init; }
    [JsonPropertyName("issues")]
    public required IReadOnlyList<WorkTreeIssue> WorkItems { get; init; }
}

/// <summary>A discovered PR group with completion + reconciliation metadata.</summary>
public sealed record PullRequestGroup
{
    [JsonPropertyName("task_ids")]
    public required IReadOnlyList<int> ChildIds { get; init; }
    [JsonPropertyName("issue_ids")]
    public required IReadOnlyList<int> WorkItemIds { get; init; }
    [JsonPropertyName("non_done_task_ids")]
    public required IReadOnlyList<int> NonDoneChildIds { get; init; }
    [JsonPropertyName("stale_doing_task_ids")]
    public required IReadOnlyList<int> StaleDoingChildIds { get; init; }
    [JsonPropertyName("non_done_issue_ids")]
    public required IReadOnlyList<int> NonDoneWorkItemIds { get; init; }
    public required string Name { get; init; }
    public required string BranchNameSuggestion { get; init; }
    public required int MergedPr { get; init; }
    public required bool Completed { get; init; }
    public required bool NeedsReconciliation { get; init; }
}

/// <summary>Reconciliation summary for a PG that has a merged PR but stale items.</summary>
public sealed record PgReconciliation
{
    [JsonPropertyName("non_done_task_ids")]
    public required IReadOnlyList<int> NonDoneChildIds { get; init; }
    [JsonPropertyName("stale_doing_task_ids")]
    public required IReadOnlyList<int> StaleDoingChildIds { get; init; }
    [JsonPropertyName("non_done_issue_ids")]
    public required IReadOnlyList<int> NonDoneWorkItemIds { get; init; }
    public required string Name { get; init; }

}

/// <summary>
/// Result of <c>polyphony branch load-tree</c>. Mirrors the JSON shape of
/// the legacy <c>scripts/load-work-tree.ps1</c> so existing workflow YAML
/// refs continue to bind correctly.
/// </summary>
public sealed record BranchLoadTreeResult
{
    public required WorkTree WorkTree { get; init; }
    public required IReadOnlyList<PullRequestGroup> PrGroups { get; init; }
    public required IReadOnlyList<string> CompletedPgs { get; init; }
    public required IReadOnlyList<string> PendingPgs { get; init; }
    public required string NextPg { get; init; }
    public required IReadOnlyList<PgReconciliation> PgsNeedingReconciliation { get; init; }
    public required int TotalTasks { get; init; }
    public required int TotalIssues { get; init; }
    public required int TaggedItems { get; init; }
    public required int UntaggedItems { get; init; }
    public required string AdoOrg { get; init; }
    public required string AdoProject { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
