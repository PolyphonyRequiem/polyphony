namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree gc</c>. Lists worktrees under
/// <c>polyphony-runs/</c> that are candidates for pruning (their
/// directory is gone, or their branch was deleted), and which were
/// actually removed when <c>--commit</c> was passed.
///
/// <para>Routing-style verb: exit code is always 0 (or
/// <c>ExitCodes.RoutingFailure</c> on a true crash); consumers branch
/// on whether <see cref="Error"/> is populated.</para>
/// </summary>
public sealed record WorktreeGcResult
{
    /// <summary>
    /// True when the run was a dry-run preview (no removals attempted).
    /// False when <c>--commit</c> was passed.
    /// </summary>
    public required bool DryRun { get; init; }

    /// <summary>
    /// Absolute path of the per-run worktree root (<c>polyphony-runs/</c>)
    /// scanned for candidates. Empty when no scan ran (e.g. error before
    /// resolution).
    /// </summary>
    public required string RunsRoot { get; init; }

    /// <summary>
    /// Optional apex subtree the scan was scoped to (the <c>N</c> from
    /// <c>--apex N</c>). Zero when unscoped (whole runs root scanned).
    /// </summary>
    public required int Apex { get; init; }

    /// <summary>
    /// All candidates discovered under the scope. Each entry includes
    /// the path, branch (when known), the prune reason, and (when
    /// <c>DryRun=false</c>) whether removal succeeded.
    /// </summary>
    public required IReadOnlyList<WorktreeGcCandidate> Candidates { get; init; }

    /// <summary>
    /// Number of candidates the scan would remove (dry-run) or removed
    /// (commit). Convenience aggregate; equals
    /// <c>Candidates.Count(c => c.Removed || c.WouldRemove)</c>.
    /// </summary>
    public required int RemovedCount { get; init; }

    /// <summary>
    /// Number of candidates that failed to remove (commit only). Always
    /// zero on dry-run.
    /// </summary>
    public required int FailedCount { get; init; }

    /// <summary>Error message on resolution failure; null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// One candidate worktree the gc scan considered for pruning.
/// </summary>
public sealed record WorktreeGcCandidate
{
    /// <summary>Absolute path of the worktree directory.</summary>
    public required string Path { get; init; }

    /// <summary>Branch the worktree is/was on; null when detached or unknown.</summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Why the worktree is a prune candidate.
    /// <list type="bullet">
    ///   <item><c>directory_missing</c> — git lists the worktree but its directory is gone.</item>
    ///   <item><c>branch_deleted</c> — the worktree's branch no longer exists locally.</item>
    /// </list>
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Dry-run only: true when the candidate WOULD be removed if
    /// <c>--commit</c> were passed. Always false on the commit path
    /// (use <see cref="Removed"/> instead).
    /// </summary>
    public required bool WouldRemove { get; init; }

    /// <summary>
    /// Commit only: true when <c>git worktree remove --force</c> succeeded
    /// for this candidate.
    /// </summary>
    public required bool Removed { get; init; }

    /// <summary>
    /// Commit only: error message from a failed
    /// <c>git worktree remove</c>; null on success or on dry-run.
    /// </summary>
    public string? Error { get; init; }
}
