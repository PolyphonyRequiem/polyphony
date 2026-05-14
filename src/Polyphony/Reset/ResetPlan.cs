namespace Polyphony.Reset;

/// <summary>
/// Read-only enumeration of every artifact a <c>polyphony reset</c> would
/// touch for a given root. Produced by <see cref="ResetPlanner"/>; serialized
/// via <see cref="PolyphonyJsonContext"/> as the JSON contract the verb emits
/// in <c>--dry-run</c> mode.
///
/// <para>The plan performs <b>zero mutation</b> — it only reads state from
/// ADO, the twig cache, git, and the filesystem.</para>
/// </summary>
public sealed record ResetPlan
{
    /// <summary>ADO work item ID of the root being reset.</summary>
    public required int RootId { get; init; }

    /// <summary>Per-item breakdown of polyphony-owned tags that would be stripped.</summary>
    public required ResetPlanTagRemoval[] TagRemovals { get; init; }

    /// <summary>Distinct polyphony-owned tag strings across all affected items.</summary>
    public required string[] MatchingTags { get; init; }

    /// <summary>Work item IDs that carry at least one polyphony-owned tag.</summary>
    public required int[] AffectedItemIds { get; init; }

    /// <summary>
    /// Polyphony-authored PR comment threads that would be cleaned up.
    /// Null when ADO context was not provided or enumeration was skipped.
    /// </summary>
    public ResetPlanComment[]? Comments { get; init; }

    /// <summary>Per-root state directory path (<c>&lt;git-common-dir&gt;/polyphony/N/</c>).</summary>
    public string? StateDir { get; init; }

    /// <summary>Whether the state directory currently exists on disk.</summary>
    public bool StateDirExists { get; init; }

    /// <summary>Local branches belonging to this root.</summary>
    public required string[] LocalBranches { get; init; }

    /// <summary>Remote branches belonging to this root.</summary>
    public required string[] RemoteBranches { get; init; }

    /// <summary>Worktree paths under <c>polyphony-runs/apex-{rootId}/</c>.</summary>
    public required string[] Worktrees { get; init; }
}

/// <summary>
/// One work item's polyphony-owned tags that would be stripped by reset.
/// </summary>
public sealed record ResetPlanTagRemoval
{
    /// <summary>ADO work item ID.</summary>
    public required int ItemId { get; init; }

    /// <summary>Polyphony-owned tag strings on this item.</summary>
    public required string[] Tags { get; init; }
}

/// <summary>
/// A polyphony-authored PR comment thread identified for cleanup.
/// Polyphony posts advisory comments as closed, top-level threads
/// (status 4, no file path) — this is the identification heuristic.
/// </summary>
public sealed record ResetPlanComment
{
    /// <summary>ADO pull request ID the thread belongs to.</summary>
    public required int PullRequestId { get; init; }

    /// <summary>Thread ID within the PR.</summary>
    public required int ThreadId { get; init; }
}
