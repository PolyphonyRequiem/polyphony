namespace Polyphony;

using System.Text.Json.Serialization;

/// <summary>
/// Output of <c>polyphony branch route</c>: the next PR-group action
/// the workflow should take. Mirrors the JSON contract emitted by
/// <c>scripts/pg-router.ps1</c>.
/// </summary>
public sealed record BranchRouteResult
{
    /// <summary>
    /// One of <c>create_branch</c>, <c>submit_pr</c>, <c>all_complete</c>,
    /// <c>error</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Name of the merge group the action targets (e.g. "PG-1"). Empty
    /// when <see cref="Action"/> is <c>all_complete</c> or <c>error</c>.
    /// JSON wire key remains <c>current_pg</c> until the workflow rewire
    /// PR ships.
    /// </summary>
    [JsonPropertyName("current_pg")]
    public required string CurrentMergeGroup { get; init; }

    /// <summary>
    /// Branch name suggestion for the merge group. Empty when no merge
    /// group is active.
    /// </summary>
    public required string BranchName { get; init; }

    /// <summary>Container item IDs (issues, etc.) belonging to the merge group.</summary>
    [JsonPropertyName("work_item_ids")]
    public required IReadOnlyList<int> WorkItemIds { get; init; }

    /// <summary>Implementable item IDs (children) belonging to the merge group.</summary>
    [JsonPropertyName("child_ids")]
    public required IReadOnlyList<int> ChildIds { get; init; }

    /// <summary>Existing PR number associated with the merge-group branch (0 when none).</summary>
    public required int PrNumber { get; init; }

    /// <summary>Existing PR URL when one was matched, else empty.</summary>
    public required string PrUrl { get; init; }

    /// <summary>
    /// Names of merge groups already complete (merged or all items
    /// terminal). JSON wire key stays <c>completed_pgs</c>.
    /// </summary>
    [JsonPropertyName("completed_pgs")]
    public required IReadOnlyList<string> CompletedMergeGroups { get; init; }

    /// <summary>
    /// Names of merge groups still in flight. JSON wire key stays
    /// <c>remaining_pgs</c>.
    /// </summary>
    [JsonPropertyName("remaining_pgs")]
    public required IReadOnlyList<string> RemainingMergeGroups { get; init; }

    /// <summary>
    /// Total merge-group count discovered (1 in the unstructured
    /// fallback). JSON wire key stays <c>total_pgs</c>.
    /// </summary>
    [JsonPropertyName("total_pgs")]
    public required int TotalMergeGroups { get; init; }

    /// <summary>Resolved ADO workspace identifier ("org/project").</summary>
    public required string AdoWorkspace { get; init; }

    /// <summary>Error message when <see cref="Action"/> is <c>error</c>; else null.</summary>
    public string? Error { get; init; }
}
