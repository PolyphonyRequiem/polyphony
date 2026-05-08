namespace Polyphony;

/// <summary>
/// Result emitted by <c>polyphony plan commit-and-push</c>. Reports whether
/// any files were actually committed and pushed, the resulting commit SHA,
/// and (for no-ops) the reason staging produced nothing to commit.
///
/// <para>Idempotent contract: re-running the verb against an unchanged
/// worktree is a no-op (<c>pushed: false</c>, <c>no_op_reason: "no_changes"</c>)
/// and exits 0. Callers can therefore re-run the verb safely on workflow
/// resume without checking precursor state.</para>
/// </summary>
public sealed record PlanCommitAndPushResult
{
    /// <summary>Branch the commit (if any) was pushed to.</summary>
    public required string Branch { get; init; }

    /// <summary>True when a commit was created and pushed; false on no-op or error.</summary>
    public required bool Pushed { get; init; }

    /// <summary>Number of paths staged-for-commit before the commit ran. Zero on no-op.</summary>
    public int FilesStaged { get; init; }

    /// <summary>HEAD SHA after the push completed. Null on no-op or error.</summary>
    public string? CommitSha { get; init; }

    /// <summary>
    /// Why no commit was produced — <c>"no_changes"</c> when staging the
    /// requested paths left nothing for git to commit AND origin already
    /// holds them. Null when a commit was created or when the verb pushed
    /// HEAD to recover from a stale remote.
    /// </summary>
    public string? NoOpReason { get; init; }

    /// <summary>
    /// Stable error classifier for routing. One of:
    /// <list type="bullet">
    ///   <item><c>invalid_inputs</c> — missing/blank <c>--branch</c>, <c>--message</c>, or <c>--paths</c>.</item>
    ///   <item><c>git_failed</c> — checkout/stage/commit/push exited non-zero.</item>
    /// </list>
    /// Null on success or no-op.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error message; null on success or no-op.</summary>
    public string? Error { get; init; }
}
