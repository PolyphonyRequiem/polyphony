namespace Polyphony;

/// <summary>
/// Output envelope for <c>polyphony reset worktrees --apex N</c> — removes
/// all linked worktrees under <c>{runs_root}/apex-{N}/</c> via
/// <c>git worktree remove --force</c>, then deletes the apex directory
/// tree.
///
/// <para>Routing-style envelope: always exits 0; per-worktree failures
/// show up as entries in <see cref="FailedWorktrees"/> with the verb still
/// reporting <see cref="Success"/> = true.</para>
/// </summary>
public sealed record ResetWorktreesResult
{
    public required int Apex { get; init; }
    public required bool Success { get; init; }
    public required bool DryRun { get; init; }

    /// <summary>
    /// Resolved <c>{runs_root}/apex-{N}/</c> path — what the verb scanned
    /// for worktrees. Empty when resolution failed.
    /// </summary>
    public string ApexRunsRoot { get; init; } = string.Empty;

    /// <summary>Worktrees that were removed (or would be in dry-run).</summary>
    public IReadOnlyList<ResetRemovedWorktree> RemovedWorktrees { get; init; } = [];

    /// <summary>Worktrees that git refused to remove.</summary>
    public IReadOnlyList<ResetFailedWorktree> FailedWorktrees { get; init; } = [];

    /// <summary>
    /// True when the <c>apex-{N}/</c> directory was deleted after all
    /// worktrees were removed. False when worktrees remain, the directory
    /// was never present, or this is a dry-run.
    /// </summary>
    public bool ApexDirDeleted { get; init; }

    public string? Error { get; init; }
}

public sealed record ResetRemovedWorktree
{
    public required string Path { get; init; }
    public string? Branch { get; init; }
}

public sealed record ResetFailedWorktree
{
    public required string Path { get; init; }
    public string? Branch { get; init; }
    public required string Reason { get; init; }
}
