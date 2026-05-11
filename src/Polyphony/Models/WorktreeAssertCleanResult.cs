namespace Polyphony;

/// <summary>
/// Output of <c>polyphony worktree assert-clean</c>. Routes the launcher
/// pre-flight and the driver pre-dispatch refusal: a worktree is "ok" only
/// when it exists, has no in-progress git operation, has no dirty entries,
/// and (when an expected branch was supplied) is checked out to that branch.
///
/// <para>Routing-style verb: the exit code is always 0; consumers branch on
/// <see cref="Ok"/> and on the structured <see cref="Reason"/> field.
/// <see cref="Reason"/> is one of:
/// <list type="bullet">
///   <item><c>null</c> — assertion passed.</item>
///   <item><c>"path_missing"</c> — <see cref="Path"/> does not exist or is not a directory.</item>
///   <item><c>"not_a_worktree"</c> — git stderr matched a "not a git repository" pattern; the path is genuinely not a worktree.</item>
///   <item><c>"git_failed"</c> — git status failed for some other reason (locked index, dubious-ownership refusal, corrupt repo, permission, etc.); see <see cref="Error"/> for the verbatim stderr.</item>
///   <item><c>"git_operation_in_progress"</c> — porcelain may be empty but a merge/rebase/cherry-pick/revert/bisect is paused mid-flight; <see cref="InProgressOperation"/> carries which one.</item>
///   <item><c>"dirty"</c> — git status --porcelain returned at least one entry; see <see cref="DirtyPaths"/>.</item>
///   <item><c>"wrong_branch"</c> — current branch differs from <see cref="ExpectedBranch"/>.</item>
///   <item><c>"internal_error"</c> — verb itself crashed (process spawn failure or unexpected exception); see <see cref="Error"/>.</item>
/// </list>
/// </para>
///
/// <para>The check ordering reflects what the operator needs to act on first:
/// path → not-a-worktree → git-failed → in-progress operation → dirty →
/// wrong-branch. A dirty worktree is reported BEFORE the wrong-branch case
/// because the operator must reconcile the dirt before they can switch
/// branches; a paused git operation is reported BEFORE dirty because the
/// dirt may be a side effect of the paused operation.</para>
/// </summary>
public sealed record WorktreeAssertCleanResult
{
    /// <summary>True when the worktree passed every check the verb ran.</summary>
    public required bool Ok { get; init; }

    /// <summary>Absolute filesystem path of the worktree the verb checked.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Current branch as reported by <c>git branch --show-current</c>; null
    /// when HEAD is detached or the worktree could not be inspected.
    /// </summary>
    public string? CurrentBranch { get; init; }

    /// <summary>
    /// Branch the caller expected the worktree to be checked out to;
    /// null when the verb was invoked without <c>--expected-branch</c>
    /// (in which case the branch check is skipped).
    /// </summary>
    public string? ExpectedBranch { get; init; }

    /// <summary>
    /// Structured reason for assertion failure; null when <see cref="Ok"/>
    /// is true. See the type-level remarks for the enumerated values.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Name of the in-progress git operation when <see cref="Reason"/> is
    /// <c>"git_operation_in_progress"</c>; null otherwise. One of
    /// <c>"merge"</c>, <c>"cherry-pick"</c>, <c>"revert"</c>,
    /// <c>"bisect"</c>, <c>"rebase-merge"</c>, <c>"rebase-apply"</c>.
    /// </summary>
    public string? InProgressOperation { get; init; }

    /// <summary>
    /// Raw porcelain lines emitted by <c>git status --porcelain</c> when
    /// <see cref="Reason"/> is <c>"dirty"</c>; empty otherwise.
    /// </summary>
    public required IReadOnlyList<string> DirtyPaths { get; init; }

    /// <summary>
    /// Diagnostic detail (typically git stderr or a .NET exception message)
    /// when <see cref="Reason"/> is <c>"not_a_worktree"</c>,
    /// <c>"git_failed"</c>, or <c>"internal_error"</c>; null otherwise.
    /// </summary>
    public string? Error { get; init; }
}
