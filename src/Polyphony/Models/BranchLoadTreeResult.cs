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

/// <summary>
/// A discovered merge group (the unit of mergeable work — formerly "PG" /
/// "Pull-request Group") with completion + reconciliation metadata. The
/// JSON wire format still emits legacy snake_case keys (<c>pr_groups</c>,
/// <c>completed_pgs</c>, etc.) via <see cref="JsonPropertyNameAttribute"/>
/// pinning so workflow YAMLs and reference scripts keep binding correctly
/// — the wire flip will land in the follow-up PR alongside workflow
/// rewires.
/// </summary>
public sealed record MergeGroup
{
    [JsonPropertyName("child_ids")]
    public required IReadOnlyList<int> ChildIds { get; init; }
    [JsonPropertyName("work_item_ids")]
    public required IReadOnlyList<int> WorkItemIds { get; init; }
    [JsonPropertyName("non_done_child_ids")]
    public required IReadOnlyList<int> NonDoneChildIds { get; init; }
    [JsonPropertyName("stale_doing_child_ids")]
    public required IReadOnlyList<int> StaleDoingChildIds { get; init; }
    [JsonPropertyName("non_done_work_item_ids")]
    public required IReadOnlyList<int> NonDoneWorkItemIds { get; init; }
    public required string Name { get; init; }
    public required string BranchNameSuggestion { get; init; }
    public required int MergedPr { get; init; }
    public required bool Completed { get; init; }
    public required bool NeedsReconciliation { get; init; }
}

/// <summary>
/// Reconciliation summary for a merge group that has a merged PR but
/// stale items.
/// </summary>
public sealed record MergeGroupReconciliation
{
    [JsonPropertyName("non_done_child_ids")]
    public required IReadOnlyList<int> NonDoneChildIds { get; init; }
    [JsonPropertyName("stale_doing_child_ids")]
    public required IReadOnlyList<int> StaleDoingChildIds { get; init; }
    [JsonPropertyName("non_done_work_item_ids")]
    public required IReadOnlyList<int> NonDoneWorkItemIds { get; init; }
    public required string Name { get; init; }
}

/// <summary>
/// Result of <c>polyphony branch load-tree</c>. Mirrors the JSON shape of
/// the legacy <c>scripts/load-work-tree.ps1</c> so existing workflow YAML
/// refs continue to bind correctly.
///
/// JSON wire format: every <c>*MergeGroup*</c> property below is pinned
/// to its legacy <c>*pg*</c> snake_case key by
/// <see cref="JsonPropertyNameAttribute"/>. The wire flip is deferred to
/// a follow-up PR that lands alongside the workflow YAML rewires.
/// </summary>
public sealed record BranchLoadTreeResult
{
    public required WorkTree WorkTree { get; init; }

    [JsonPropertyName("pr_groups")]
    public required IReadOnlyList<MergeGroup> MergeGroups { get; init; }

    [JsonPropertyName("completed_pgs")]
    public required IReadOnlyList<string> CompletedMergeGroups { get; init; }

    [JsonPropertyName("pending_pgs")]
    public required IReadOnlyList<string> PendingMergeGroups { get; init; }

    [JsonPropertyName("next_pg")]
    public required string NextMergeGroup { get; init; }

    [JsonPropertyName("pgs_needing_reconciliation")]
    public required IReadOnlyList<MergeGroupReconciliation> MergeGroupsNeedingReconciliation { get; init; }

    public required int TotalTasks { get; init; }
    public required int TotalIssues { get; init; }
    public required int TaggedItems { get; init; }
    public required int UntaggedItems { get; init; }
    public required string AdoOrg { get; init; }
    public required string AdoProject { get; init; }
    public required string AdoWorkspace { get; init; }
    public string? Error { get; init; }
}
