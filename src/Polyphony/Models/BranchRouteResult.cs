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
    /// Name of the PG the action targets (e.g. "PG-1"). Empty when
    /// <see cref="Action"/> is <c>all_complete</c> or <c>error</c>.
    /// </summary>
    public required string CurrentPg { get; init; }

    /// <summary>
    /// Branch name suggestion for the PG. Empty when no PG is active.
    /// </summary>
    public required string BranchName { get; init; }

    /// <summary>Container item IDs (issues, etc.) belonging to the PG.</summary>
    [JsonPropertyName("work_item_ids")]
    public required IReadOnlyList<int> WorkItemIds { get; init; }

    /// <summary>Implementable item IDs (children) belonging to the PG.</summary>
    [JsonPropertyName("child_ids")]
    public required IReadOnlyList<int> ChildIds { get; init; }

    /// <summary>Existing PR number associated with the PG branch (0 when none).</summary>
    public required int PrNumber { get; init; }

    /// <summary>Existing PR URL when one was matched, else empty.</summary>
    public required string PrUrl { get; init; }

    /// <summary>Names of PGs already complete (merged or all items terminal).</summary>
    public required IReadOnlyList<string> CompletedPgs { get; init; }

    /// <summary>Names of PGs still in flight.</summary>
    public required IReadOnlyList<string> RemainingPgs { get; init; }

    /// <summary>Total PG count discovered (1 in the unstructured fallback).</summary>
    public required int TotalPgs { get; init; }

    /// <summary>Resolved ADO workspace identifier ("org/project").</summary>
    public required string AdoWorkspace { get; init; }

    /// <summary>Error message when <see cref="Action"/> is <c>error</c>; else null.</summary>
    public string? Error { get; init; }
}
